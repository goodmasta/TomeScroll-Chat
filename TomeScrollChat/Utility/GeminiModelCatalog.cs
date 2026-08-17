namespace TomeScrollChat.Utility;

/// <summary>Curated list of Gemini models for the "AI" settings tab's model picker (see
/// <see cref="Configuration.GeminiModel"/>) - pulled from the official model list at
/// <c>ai.google.dev/gemini-api/docs/models</c> (2026-08-17), filtered down to plain text-generation
/// models compatible with <see cref="Services.GeminiService.GenerateTextAsync"/>'s simple
/// prompt-in/text-out call. Deliberately excludes image/video/music/TTS/embedding/live-streaming/
/// specialized-agent models (Nano Banana, Veo, Lyria, Computer Use, Deep Research, Antigravity,
/// Robotics, etc.) - those need a different request shape this service doesn't send - and anything
/// the same page marked "(Shut down)"/deprecated. Ordered newest/most-capable-ish first.</summary>
public static class GeminiModelCatalog
{
    public static readonly (string Id, string Label)[] Entries =
    {
        ("gemini-3.7-flash", "Gemini 3.7 Flash"),
        ("gemini-3.6-flash", "Gemini 3.6 Flash"),
        ("gemini-3.5-flash", "Gemini 3.5 Flash"),
        ("gemini-3.5-flash-lite", "Gemini 3.5 Flash-Lite (fastest/cheapest)"),
        ("gemini-3.1-pro-preview", "Gemini 3.1 Pro (Preview)"),
        ("gemini-3.1-flash-lite", "Gemini 3.1 Flash-Lite"),
        ("gemini-3-flash-preview", "Gemini 3 Flash (Preview)"),
        ("gemini-2.5-pro", "Gemini 2.5 Pro"),
        ("gemini-2.5-flash", "Gemini 2.5 Flash"),
        ("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite"),
    };
}
