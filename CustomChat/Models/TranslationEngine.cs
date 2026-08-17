namespace CustomChat.Models;

/// <summary>Which backend <see cref="Services.TranslationService"/> uses - see
/// <see cref="Configuration.TranslationEngine"/>.</summary>
public enum TranslationEngine
{
    /// <summary>Google Translate's free, unofficial "gtx" endpoint - no key/billing needed. Falls
    /// back to <see cref="MyMemory"/> automatically after a few consecutive failures (a likely sign
    /// of hitting its undocumented rate limit), reverting back to this once a request succeeds again.</summary>
    GoogleFree,

    /// <summary>mymemory.translated.net's free API - no key needed for the free daily quota. Used
    /// either directly (if picked here) or as <see cref="GoogleFree"/>'s automatic fallback.</summary>
    MyMemory,

    /// <summary>Google Gemini, via <see cref="Services.GeminiService"/> - needs
    /// <see cref="Configuration.GeminiApiKey"/> configured, otherwise every request silently fails.</summary>
    Gemini,
}
