using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace CustomChat.Services;

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
/// </summary>
public sealed class GeminiService : IDisposable
{
    /// <summary>A fast, low-cost model - reasonable for short, frequent calls like translating a
    /// single chat line. Editable in Settings (<see cref="Configuration.GeminiModel"/>) since Google's
    /// current model lineup will keep moving on from whatever's hardcoded here.</summary>
    public const string DefaultModel = "gemini-2.5-flash";

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IPluginLog log;
    private readonly Configuration configuration;

    public GeminiService(IPluginLog log, Configuration configuration)
    {
        this.log = log;
        this.configuration = configuration;
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

            if (!response.IsSuccessStatusCode)
            {
                log.Warning("CustomChat: Gemini request failed ({Status}): {Body}", (int)response.StatusCode, Truncate(responseJson));
                return null;
            }

            return ParseReply(responseJson);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: Gemini request failed");
            return null;
        }
    }

    /// <summary>Standard <c>generateContent</c> response shape: <c>candidates[0].content.parts[].text</c> -
    /// concatenates every part's text (a reply is usually one part, but nothing guarantees that).</summary>
    private static string? ParseReply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
            return null;

        var firstCandidate = candidates[0];
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
