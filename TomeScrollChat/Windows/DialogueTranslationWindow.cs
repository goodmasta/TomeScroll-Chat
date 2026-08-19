using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
///
/// <para>A title bar search button (magnifying glass, per explicit user request - unlike the per-tab
/// search in <c>MainWindow</c>/<c>DetachedTabWindow</c>, which hangs off a sidebar context menu or an
/// inline button since there's no title bar room to spare there) filters the shown lines by speaker,
/// translated text, or original text, case-insensitively - see <see cref="DrawSearchBar"/>.</para>
/// </summary>
public sealed class DialogueTranslationWindow : Window
{
    private readonly Configuration configuration;
    private readonly DialogueTranslationService dialogueService;
    private int lastDrawnCount = -1;

    // "Search in this window" - same shape as MainWindow/DetachedTabWindow's own tab search, just
    // toggled from a title bar button instead of an inline one (no sidebar/tab context menu to hang a
    // "Search..." item off of here, and this window has room in its title bar unlike a tab's).
    private bool searchMode;
    private string searchQuery = string.Empty;
    private bool focusSearchInput;

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

        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Search,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Search story & dialogue"),
            Click = _ =>
            {
                searchMode = !searchMode;
                focusSearchInput = searchMode;
                if (!searchMode)
                    searchQuery = string.Empty;
            },
        });
    }

    public override void OnClose() => IsOpen = true;

    public override bool DrawConditions()
    {
        if (!configuration.EnableDialogueTranslationWindow)
            return false;

        // Actively searching should never auto-hide out from under the player mid-read, same idea as
        // the auto-hide-off case below.
        if (searchMode)
            return true;

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
        var searching = searchMode && !string.IsNullOrWhiteSpace(searchQuery);
        var displayEntries = searching ? entries.Where(MatchesSearch).ToList() : entries;

        if (searchMode)
            DrawSearchBar();

        using (var child = ImRaii.Child("DialogueTranslationScroll", Vector2.Zero, false))
        {
            if (child.Success)
            {
                foreach (var entry in displayEntries)
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

                // Skipped while actively filtering - jumping to the bottom of the *filtered* list on
                // every new (possibly non-matching) line would fight the player's own scroll position
                // while they're reading search results. Resumes the moment search closes, since that
                // re-evaluates entries.Count against the now-stale lastDrawnCount immediately.
                if (!searchMode && entries.Count != lastDrawnCount)
                {
                    ImGui.SetScrollHereY(1f);
                    lastDrawnCount = entries.Count;
                }
            }
        }
    }

    private bool MatchesSearch(DialogueTranslationEntry entry) =>
        (!string.IsNullOrEmpty(entry.Speaker) && entry.Speaker.Contains(searchQuery, StringComparison.OrdinalIgnoreCase)) ||
        entry.TranslatedText.Contains(searchQuery, StringComparison.OrdinalIgnoreCase) ||
        entry.OriginalText.Contains(searchQuery, StringComparison.OrdinalIgnoreCase);

    /// <summary>Same shape as MainWindow/DetachedTabWindow's own per-tab search bar - see there for the
    /// reasoning (Escape/the "x" button both close it and clear the query).</summary>
    private void DrawSearchBar()
    {
        var closeSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(closeSize + ImGui.GetStyle().ItemSpacing.X));

        if (focusSearchInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearchInput = false;
        }

        ImGui.InputTextWithHint("##dialogueTranslationSearch", "Search story & dialogue...", ref searchQuery, 200);

        var escapePressed = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape);

        ImGui.SameLine(0, 0);
        var closeClicked = ImGui.Button("X##dialogueTranslationSearchClose", new Vector2(closeSize, closeSize));

        if (escapePressed || closeClicked)
        {
            searchMode = false;
            searchQuery = string.Empty;
        }

        ImGui.Separator();
    }
}
