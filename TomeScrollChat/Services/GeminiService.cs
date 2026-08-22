using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Thin, general-purpose wrapper over Google's Gemini API (<c>generativelanguage.googleapis.com</c>'s
/// <c>generateContent</c> endpoint) - a single "send a prompt, get text back" call, deliberately not
/// specific to translation or any other one feature. <see cref="TranslationService"/> is the first
/// caller (as an optional, opt-in translation engine - see <see cref="Configuration.TranslationEngine"/>),
/// but this is meant to be reused by future AI-backed features too, so it has no translation-specific
/// concepts (prompts, parsing "is this a translation" logic, etc. all stay in the caller).
///
/// <para>Requires <see cref="Configuration.GeminiApiKey"/> to be set (Settings > General) - every
/// call is a no-op returning null without one, rather than an error, since a caller may want to
/// silently treat "not configured" the same as "unavailable right now" (e.g. TranslationService
/// falling through to a different engine).</para>
///
/// <para>Every genuine failure past that point - a non-2xx response, a network/timeout exception, or a
/// 2xx response with no usable reply (safety-blocked prompt/response, empty candidates, malformed JSON)
/// - shows a brief <see cref="NotificationService"/> toast in addition to the existing <c>/xllog</c>
/// warning, centralized here so every caller (translation, dialogue translation, AI reply/rephrase/
/// correct, and anything added later) gets this for free instead of each one needing its own failure
/// toast - added per explicit user request ("при любой ошибке/проблеме при обращении к Gemini кратко
/// сообщалась информация в виде уведомления"). The "not configured" early-return above deliberately
/// stays silent here, though - that's routine unconfigured state, not a failure, and callers that care
/// already show their own "needs an API key" toast when they check <see cref="IsConfigured"/> themselves.</para>
/// </summary>
public sealed class GeminiService : IDisposable
{
    /// <summary>A fast, low-cost model - reasonable for short, frequent calls like translating a
    /// single chat line. Picked from Settings > AI (see <see cref="Configuration.GeminiModel"/> and
    /// <see cref="Utility.GeminiModelCatalog"/> for the full curated list) rather than hardcoded
    /// permanently, since Google's current model lineup will keep moving on from whatever's default
    /// here (verified against the real model list at ai.google.dev/gemini-api/docs/models as of
    /// 2026-08-17 - "Flash-Lite" is that page's own fastest/cheapest tier, a good fit for a short,
    /// frequent, low-stakes call like translating one chat line).</summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    /// <summary>Fixed 2026-08-17: reported live as an intermittent <c>SSL connection could not be
    /// established... Received an unexpected EOF or 0 bytes from the transport stream</c> failure -
    /// the classic symptom of a long-lived <see cref="HttpClient"/>'s pooled connection going stale
    /// (a NAT/firewall/idle timeout silently closes the underlying TCP connection while unused between
    /// translation requests, which only surfaces as an error on the *next* reuse attempt, well after
    /// the connection actually died). This service's <c>http</c> field lives for the whole plugin
    /// session, so without this its connection pool can accumulate arbitrarily stale entries.
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> proactively recycles connections older
    /// than this instead of waiting for one to fail - standard .NET guidance for exactly this failure
    /// mode (see learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines).</summary>
    private readonly HttpClient http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly NotificationService notificationService;

    public GeminiService(IPluginLog log, Configuration configuration, NotificationService notificationService)
    {
        this.log = log;
        this.configuration = configuration;
        this.notificationService = notificationService;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration.GeminiApiKey);

    /// <summary>Sends a single-turn prompt and returns Gemini's raw text reply, or null on any
    /// failure - not configured, network error, non-2xx response, empty/missing reply, etc. Callers
    /// are expected to already have their own fallback for a null result (matching the existing
    /// convention every other translation backend in this project already follows).</summary>
    public async Task<string?> GenerateTextAsync(string prompt)
    {
        if (!IsConfigured)
            return null;

        // Reported live (2026-08-22) as occasionally taking a very long time, then every following call
        // for a while being instant - logged unconditionally (not just on the slow path) so there's
        // always a real number in /xllog to compare against next time this happens, rather than only
        // ever guessing after the fact. The two standing hypotheses this can't distinguish between from
        // just one data point: (a) SocketsHttpHandler had to open a fresh connection - expected the
        // first call after any gap longer than PooledConnectionLifetime (5 minutes) below, or after the
        // very first call this session - or (b) Gemini's own "Flash-Lite" tier has some cold-start
        // latency server-side after a period with no traffic to this API key/model. A run of several
        // slow-timestamped log lines with multi-minute gaps between them would point at (a); one-off
        // slow calls with no such pattern would point at (b).
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var model = string.IsNullOrWhiteSpace(configuration.GeminiModel) ? DefaultModel : configuration.GeminiModel;
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(configuration.GeminiApiKey)}";

            var requestJson = JsonSerializer.Serialize(new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } },
                },
            });

            using var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(url, requestContent).ConfigureAwait(false);
            var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            LogElapsed(stopwatch.Elapsed);

            if (!response.IsSuccessStatusCode)
            {
                log.Warning("TomeScrollChat: Gemini request failed ({Status}): {Body}", (int)response.StatusCode, Truncate(responseJson));
                notificationService.Show(DescribeHttpFailure(response.StatusCode), NotificationSeverity.Warning);
                return null;
            }

            var reply = ParseReply(responseJson, out var blockReason);
            if (reply == null)
            {
                if (blockReason != null)
                {
                    log.Warning("TomeScrollChat: Gemini reply blocked ({Reason}): {Body}", blockReason, Truncate(responseJson));
                    notificationService.Show($"Gemini didn't answer - {blockReason}.", NotificationSeverity.Warning);
                }
                else
                {
                    log.Warning("TomeScrollChat: Gemini returned an empty/unparseable response: {Body}", Truncate(responseJson));
                    notificationService.Show("Gemini returned an empty reply - check /xllog for details.", NotificationSeverity.Warning);
                }
            }

            return reply;
        }
        catch (Exception ex)
        {
            LogElapsed(stopwatch.Elapsed);
            log.Warning(ex, "TomeScrollChat: Gemini request failed");
            notificationService.Show(DescribeException(ex), NotificationSeverity.Warning);
            return null;
        }
    }

    private static readonly TimeSpan SlowCallThreshold = TimeSpan.FromSeconds(3);

    private void LogElapsed(TimeSpan elapsed)
    {
        if (elapsed >= SlowCallThreshold)
            log.Warning("TomeScrollChat: Gemini request took {ElapsedMs}ms - unusually slow, see GenerateTextAsync's own doc comment", (long)elapsed.TotalMilliseconds);
        else
            log.Debug("TomeScrollChat: Gemini request took {ElapsedMs}ms", (long)elapsed.TotalMilliseconds);
    }

    /// <summary>Common cases get a specific, actionable message; anything else falls back to a generic
    /// one pointing at <c>/xllog</c> for the full body already logged by the caller above.</summary>
    private static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Gemini rejected the API key - check Settings > AI.",
        (HttpStatusCode)429 => "Gemini rate-limited this request - try again shortly.",
        HttpStatusCode.BadRequest => "Gemini rejected the request - check the model/API key in Settings > AI.",
        HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => "Gemini is temporarily unavailable - try again shortly.",
        _ => $"Gemini request failed ({(int)status}) - check /xllog for details.",
    };

    /// <summary>Deliberately doesn't surface <paramref name="ex"/>'s own message - exceptions here are
    /// usually raw network-stack text (SSL handshake failures, DNS errors) that reads as noise in a
    /// 5-second toast rather than anything actionable; the full exception is already in <c>/xllog</c>.</summary>
    private static string DescribeException(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "Gemini request timed out.",
        HttpRequestException => "Gemini request failed - network/connection error, check /xllog for details.",
        _ => "Gemini request failed - check /xllog for details.",
    };

    /// <summary>Standard <c>generateContent</c> response shape: <c>candidates[0].content.parts[].text</c> -
    /// concatenates every part's text (a reply is usually one part, but nothing guarantees that).
    /// <paramref name="blockReason"/> is set (and the return value null) specifically when Gemini
    /// declined to answer rather than merely returning something unparseable - either the whole prompt
    /// was blocked before generation (<c>promptFeedback.blockReason</c>) or the response itself was cut
    /// off by a safety/recitation filter (<c>candidates[0].finishReason</c>) - worth a distinct message
    /// from a generic parse failure since the fix (reword the prompt/text) is different from "try again".</summary>
    private static string? ParseReply(string json, out string? blockReason)
    {
        blockReason = null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var promptBlockEl) &&
            promptBlockEl.ValueKind == JsonValueKind.String)
        {
            blockReason = $"prompt blocked ({promptBlockEl.GetString()})";
            return null;
        }

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return null;

        var firstCandidate = candidates[0];

        if (firstCandidate.TryGetProperty("finishReason", out var finishEl) && finishEl.ValueKind == JsonValueKind.String)
        {
            var finishReason = finishEl.GetString();
            if (finishReason is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
                blockReason = $"response blocked ({finishReason})";
        }

        if (!firstCandidate.TryGetProperty("content", out var contentEl) ||
            !contentEl.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return null;

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                sb.Append(textEl.GetString());
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static string Truncate(string text) => text.Length > 300 ? text[..300] + "..." : text;

    public void Dispose() => http.Dispose();
}
