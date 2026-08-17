using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// Translates message bodies on demand via Google Translate's free, unofficial "gtx" client
/// endpoint (the same one used by a number of open-source translation tools and browser
/// extensions) - not the official paid Cloud Translation API, so no API key/billing setup is
/// needed, but it's also undocumented and could be rate-limited or change without notice; this is
/// a deliberate v1 tradeoff (see the user's own choice when this was scoped) rather than an
/// oversight. Source language is always "auto" - only the target is user-configurable
/// (<see cref="Configuration.TranslateTargetLanguage"/>).
///
/// <para>Per-message requests (<see cref="RequestTranslate"/>/<see cref="ForceRetranslate"/> - the
/// per-message cache path used by the message context menu and <see cref="ChatTabConfig.AutoTranslate"/>)
/// go through a single-consumer queue rather than firing immediately: <see cref="WorkerLoopAsync"/>
/// processes one at a time with a baseline delay between requests, plus exponential backoff stacked
/// on top after consecutive failures (the free endpoint's most likely failure mode is a rate limit,
/// even though it never returns a distinguishable status - any failure is treated as a possible one).
/// This matters specifically because of <see cref="ChatTabConfig.AutoTranslate"/>: opening a tab with
/// hundreds of untranslated backlog messages would otherwise fire that many requests in the same
/// frame. <see cref="TranslateRawAsync"/> (translating the player's own not-yet-sent input box text)
/// deliberately bypasses this queue - it's a rare, manual, one-off action, not the bursty case this
/// exists for.</para>
///
/// <para>Results are cached by the <see cref="ChatMessageRecord"/> instance itself (reference
/// identity, not <see cref="ChatMessageRecord.Id"/> - that's the SQLite rowid and reads back 0 for
/// any message not yet flushed to disk by the background writer, which would collide across
/// distinct messages if used as a cache key). <see cref="ChatCaptureService"/> already creates one
/// distinct record instance per tab a message matches, so this also naturally scopes a translation
/// to the specific tab it was requested from. A successful translation is also persisted back to
/// history (<see cref="ChatHistoryService.SaveTranslation"/>), and <see cref="ChatMessageRecord.PersistedTranslation"/>
/// (set only when a record is loaded from disk) is checked as a cache hit before ever queuing a new
/// request - so re-opening a tab, or a plugin restart, doesn't re-translate what's already been
/// translated before. The cached/persisted language is tracked alongside the text so switching
/// <see cref="Configuration.TranslateTargetLanguage"/> doesn't show a stale translation into the
/// previous target language.</para>
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IPluginLog log;
    private readonly ChatHistoryService historyService;
    private readonly ConcurrentDictionary<ChatMessageRecord, (string Text, string Language)> results = new();
    private readonly ConcurrentDictionary<ChatMessageRecord, byte> pendingOrInFlight = new();
    private readonly Channel<TranslateJob> queue = Channel.CreateUnbounded<TranslateJob>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly CancellationTokenSource cts = new();
    private readonly Task workerTask;

    private const int BaseDelayMs = 350;
    private const int MaxBackoffSeconds = 60;

    private readonly record struct TranslateJob(ChatMessageRecord Message, string TargetLanguage);

    public TranslationService(IPluginLog log, ChatHistoryService historyService)
    {
        this.log = log;
        this.historyService = historyService;
        workerTask = Task.Run(() => WorkerLoopAsync(cts.Token));
    }

    public string? TryGetTranslation(ChatMessageRecord message, string targetLanguage)
    {
        if (results.TryGetValue(message, out var cached) && cached.Language == targetLanguage)
            return cached.Text;

        if (!string.IsNullOrEmpty(message.PersistedTranslation) && message.PersistedTranslationLanguage == targetLanguage)
        {
            results[message] = (message.PersistedTranslation, targetLanguage);
            return message.PersistedTranslation;
        }

        return null;
    }

    public bool IsTranslating(ChatMessageRecord message) => pendingOrInFlight.ContainsKey(message);

    public void ClearTranslation(ChatMessageRecord message) => results.TryRemove(message, out _);

    /// <summary>Queues a background translation if this message hasn't already been translated (in
    /// this session or a previous one, see <see cref="TryGetTranslation"/>) or isn't already
    /// queued/in flight. Safe to call repeatedly, e.g. on every "Translate" click or every draw while
    /// <see cref="ChatTabConfig.AutoTranslate"/> is on - actually dispatching happens on
    /// <see cref="WorkerLoopAsync"/>'s own throttled schedule, not immediately.</summary>
    public void RequestTranslate(ChatMessageRecord message, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(message.Body) || TryGetTranslation(message, targetLanguage) != null || !pendingOrInFlight.TryAdd(message, 0))
            return;

        queue.Writer.TryWrite(new TranslateJob(message, targetLanguage));
    }

    /// <summary>Always re-fetches, even if a translation is already cached - "Retranslate" in the
    /// message context menu, for when the target language was changed after the fact or the first
    /// result just looked wrong. A no-op while one's already queued/in flight for this message.</summary>
    public void ForceRetranslate(ChatMessageRecord message, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(message.Body) || !pendingOrInFlight.TryAdd(message, 0))
            return;

        queue.Writer.TryWrite(new TranslateJob(message, targetLanguage));
    }

    /// <summary>Single consumer for every queued per-message translation - see the class doc comment
    /// for why this exists instead of firing each request immediately.</summary>
    private async Task WorkerLoopAsync(CancellationToken token)
    {
        var consecutiveFailures = 0;
        try
        {
            while (await queue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var job))
                {
                    var translated = await TranslateRawAsync(job.Message.Body, job.TargetLanguage).ConfigureAwait(false);
                    pendingOrInFlight.TryRemove(job.Message, out _);

                    if (!string.IsNullOrEmpty(translated))
                    {
                        results[job.Message] = (translated, job.TargetLanguage);
                        historyService.SaveTranslation(job.Message, translated, job.TargetLanguage);
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                    }

                    // Baseline gap between every request, plus exponential backoff stacked on top
                    // after consecutive failures - the free endpoint never distinguishes "rate
                    // limited" from any other failure, so any failure is treated as a possible one.
                    // Capped so a bad patch can't back off forever.
                    var delay = TimeSpan.FromMilliseconds(BaseDelayMs);
                    if (consecutiveFailures > 0)
                    {
                        var backoffSeconds = Math.Min(MaxBackoffSeconds, 2 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 5)));
                        delay += TimeSpan.FromSeconds(backoffSeconds);
                        log.Warning("CustomChat: translation failed ({Count} in a row) - backing off {Delay:0}s before the next request", consecutiveFailures, delay.TotalSeconds);
                    }

                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>The same translation call, without the per-message cache or the queue/throttle above -
    /// used for one-off translations that aren't tied to a received <see cref="ChatMessageRecord"/>,
    /// e.g. translating the player's own not-yet-sent text in the message input box. A rare, manual,
    /// single action, not the bursty case the queue exists for.</summary>
    public async Task<string?> TranslateRawAsync(string text, string targetLanguage)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single" +
                      $"?client=gtx&sl=auto&tl={Uri.EscapeDataString(targetLanguage)}&dt=t&q={Uri.EscapeDataString(text)}";
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            return ParseTranslation(json);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to translate text");
            return null;
        }
    }

    /// <summary>The endpoint's response is a loosely-typed JSON array, not a documented schema:
    /// roughly <c>[[[translatedChunk, originalChunk, null, null, ...], ...], ...]</c>, one entry per
    /// sentence Google split the input into - concatenating the first element of each is the
    /// standard way every unofficial client for this endpoint reassembles the full translation.</summary>
    private static string? ParseTranslation(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
            return null;

        var sentences = root[0];
        if (sentences.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var sentence in sentences.EnumerateArray())
        {
            if (sentence.ValueKind != JsonValueKind.Array || sentence.GetArrayLength() == 0)
                continue;

            var piece = sentence[0].GetString();
            if (!string.IsNullOrEmpty(piece))
                sb.Append(piece);
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    public void Dispose()
    {
        cts.Cancel();
        queue.Writer.TryComplete();
        try
        {
            workerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Best-effort - shutting down anyway.
        }

        cts.Dispose();
        http.Dispose();
    }
}
