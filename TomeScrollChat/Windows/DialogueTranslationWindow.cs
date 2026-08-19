using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using TomeScrollChat.Models;
using TomeScrollChat.Services;
using TomeScrollChat.Utility;

namespace TomeScrollChat.Windows;

/// <summary>
/// Shows nothing but translated lines queued by <see cref="Services.DialogueTranslationService"/> -
/// NPC/cutscene dialogue and quest toasts, translated into <see cref="Configuration.TranslateTargetLanguage"/>.
/// Toggled on entirely by <see cref="Configuration.EnableDialogueTranslationWindow"/> (Settings >
/// General); this window never has its own separate open/close state, same "always open, closing is
/// refused" shape <see cref="NotificationOverlay"/> already uses - see <see cref="DrawConditions"/> for
/// the actual show/hide logic (config toggle + at least one entry + not gone stale past
/// <see cref="Configuration.DialogueTranslationAutoHideSeconds"/>).
///
/// <para>Deliberately has no cutscene-hiding check (unlike <c>MainWindow</c>/<c>DetachedTabWindow</c>,
/// which optionally hide via <c>Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ...)</c> - see
/// <see cref="Configuration.HideChatDuringCutscenes"/>) - simply never adding that check is what keeps
/// this window visible during cutscenes, which is the whole point of it.</para>
/// </summary>
public sealed class DialogueTranslationWindow : Window
{
    private readonly Configuration configuration;
    private readonly DialogueTranslationService dialogueService;
    private int lastDrawnCount = -1;

    public DialogueTranslationWindow(Configuration configuration, DialogueTranslationService dialogueService)
        : base("Story & Dialogue Translation###TomeScrollChatDialogueTranslation")
    {
        this.configuration = configuration;
        this.dialogueService = dialogueService;

        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;

        Size = new Vector2(420, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
        Position = new Vector2(200, 400);
        PositionCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240, 120),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnClose() => IsOpen = true;

    public override bool DrawConditions()
    {
        if (!configuration.EnableDialogueTranslationWindow)
            return false;

        // Fixed 2026-08-17, per explicit user request: with auto-hide off, the window should stay
        // visible unconditionally - it used to still require Entries.Count > 0 even then, so it
        // stayed hidden until the very first dialogue line came in instead of being there from the
        // start the way "always visible" implies.
        if (!configuration.DialogueTranslationAutoHide)
            return true;

        if (dialogueService.Entries.Count == 0)
            return false;

        var idleSeconds = (DateTime.UtcNow - dialogueService.LastEntryAt).TotalSeconds;
        return idleSeconds < Math.Max(1f, configuration.DialogueTranslationAutoHideSeconds);
    }

    public override void Draw()
    {
        var entries = dialogueService.Entries;

        using (var child = ImRaii.Child("DialogueTranslationScroll", Vector2.Zero, false))
        {
            if (child.Success)
            {
                foreach (var entry in entries)
                {
                    var kindLabel = entry.Kind switch
                    {
                        DialogueTranslationKind.CutsceneSubtitle => "Cutscene",
                        DialogueTranslationKind.QuestNotice => "Quest",
                        _ => "NPC",
                    };

                    ImGui.TextDisabled($"[{kindLabel}]");
                    if (!string.IsNullOrEmpty(entry.Speaker))
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(NpcColorPalette.GetColor(entry.Speaker), entry.Speaker);
                    }

                    ImGui.TextWrapped(entry.TranslatedText);
                    ImGui.Spacing();
                }

                if (entries.Count != lastDrawnCount)
                {
                    ImGui.SetScrollHereY(1f);
                    lastDrawnCount = entries.Count;
                }
            }
        }
    }
}
