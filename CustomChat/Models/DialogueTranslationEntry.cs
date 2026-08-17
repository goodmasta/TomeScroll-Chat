using System;

namespace CustomChat.Models;

/// <summary>What kind of source line a <see cref="DialogueTranslationEntry"/> came from - shown as a
/// small label in <see cref="Windows.DialogueTranslationWindow"/> so it's clear whether a line was
/// spoken NPC dialogue, a plain cutscene subtitle (no speaker), or a quest toast.</summary>
public enum DialogueTranslationKind
{
    NpcDialogue,
    CutsceneSubtitle,
    QuestNotice,
}

/// <summary>One translated line queued by <see cref="Services.DialogueTranslationService"/> for
/// <see cref="Windows.DialogueTranslationWindow"/> - the speaker/original text are kept alongside the
/// translation purely for display context, not re-sent anywhere.</summary>
public sealed record DialogueTranslationEntry(DialogueTranslationKind Kind, string? Speaker, string OriginalText, string TranslatedText, DateTime ReceivedAt);
