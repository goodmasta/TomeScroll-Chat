using System;

namespace TomeScrollChat.Models;

/// <summary>One past story/dialogue line remembered by <see cref="Services.TranslationService"/>'s
/// Gemini-routed dialogue translation (<see cref="Services.TranslationService.TranslateDialogueAsync"/>),
/// fed back as scene context on every new line so character voice/terminology stay consistent instead of
/// each line being translated cold. Persisted to disk (unlike <see cref="DialogueTranslationEntry"/>,
/// which is display-only and cleared every session) so context survives a plugin reload or game restart
/// mid-quest - added per explicit user request.</summary>
public sealed record DialogueTranslationMemoryEntry(string? Speaker, string OriginalText, string TranslatedText, DateTime ReceivedAt);
