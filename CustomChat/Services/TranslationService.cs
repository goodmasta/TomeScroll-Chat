using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
/// Results are cached by the <see cref="ChatMessageRecord"/> instance itself (reference identity,
/// not <see cref="ChatMessageRecord.Id"/> - that's the SQLite rowid and reads back 0 for any
/// message not yet flushed to disk by the background writer, which would collide across distinct
/// messages if used as a cache key). <see cref="ChatCaptureService"/> already creates one distinct
/// record instance per tab a message matches, so this also naturally scopes a translation to the
/// specific tab it was requested from.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<ChatMessageRecord, string> results = new();
    private readonly ConcurrentDictionary<ChatMessageRecord, byte> inFlight = new();

    public TranslationService(IPluginLog log)
    {
        this.log = log;
    }

    public string? TryGetTranslation(ChatMessageRecord message) =>
        results.TryGetValue(message, out var text) ? text : null;

    public bool IsTranslating(ChatMessageRecord message) => inFlight.ContainsKey(message);

    public void ClearTranslation(ChatMessageRecord message) => results.TryRemove(message, out _);

    /// <summary>Kicks off a background translation if this message hasn't already been translated
    /// (or isn't already being translated). Safe to call repeatedly, e.g. on every "Translate" click.</summary>
    public void RequestTranslate(ChatMessageRecord message, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(message.Body) || results.ContainsKey(message) || !inFlight.TryAdd(message, 0))
            return;

        _ = TranslateAsync(message, targetLanguage);
    }

    /// <summary>Always re-fetches, even if a translation is already cached - "Retranslate" in the
    /// message context menu, for when the target language was changed after the fact or the first
    /// result just looked wrong. A no-op while one's already in flight for this message.</summary>
    public void ForceRetranslate(ChatMessageRecord message, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(message.Body) || !inFlight.TryAdd(message, 0))
            return;

        _ = TranslateAsync(message, targetLanguage);
    }

    private async Task TranslateAsync(ChatMessageRecord message, string targetLanguage)
    {
        try
        {
            var translated = await TranslateRawAsync(message.Body, targetLanguage).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(translated))
                results[message] = translated;
        }
        finally
        {
            inFlight.TryRemove(message, out _);
        }
    }

    /// <summary>The same translation call, without the per-message cache - used for one-off
    /// translations that aren't tied to a received <see cref="ChatMessageRecord"/>, e.g. translating
    /// the player's own not-yet-sent text in the message input box.</summary>
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

    public void Dispose() => http.Dispose();
}
