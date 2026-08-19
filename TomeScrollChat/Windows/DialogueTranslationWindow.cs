using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
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
/// <para><b>Fixed 2026-08-19, first attempt</b>: reported live as hiding mid-cutscene anyway, alongside
/// the main chat window - root cause found was <see cref="Configuration.DialogueTranslationAutoHide"/>'s
/// idle timeout, which has no idea a cutscene is even playing and can easily exceed
/// <see cref="Configuration.DialogueTranslationAutoHideSeconds"/> during an ordinary dialogue-free
/// cinematic beat. <see cref="DrawConditions"/> was changed to bypass the idle check entirely while a
/// cutscene is playing.</para>
///
/// <para><b>Fixed 2026-08-19, second attempt - the real root cause</b>: still reported hidden mid-
/// cutscene after the fix above. Turned out <see cref="DrawConditions"/> was never the actual problem -
/// Dalamud auto-hides *every* plugin window during cutscenes at the <c>UiBuilder</c> level, upstream of
/// any individual window's <see cref="DrawConditions"/> ever being consulted, unless
/// <c>IUiBuilder.DisableCutsceneUiHide</c> is set. <c>Plugin.cs</c> now sets it to <c>true</c> at
/// startup (next to the pre-existing <c>DisableUserUiHide</c>, same reasoning: a UiBuilder-level force-
/// hide can't be overridden by any per-window check). The idle-timeout bypass from the first attempt is
/// still correct/worth keeping - it just wasn't sufficient on its own. **General lesson**: a window
/// that's supposed to always stay visible during cutscenes/GPose/UI-hide needs both the per-window
/// <see cref="DrawConditions"/> logic *and* the matching <c>UiBuilder.Disable*UiHide</c> flag - one
/// without the other silently doesn't work, and the failure looks identical either way (window just
/// isn't there), so re-check both if this regresses again.</para>
///
/// <para>A title bar search button (magnifying glass, per explicit user request - unlike the per-tab
/// search in <c>MainWindow</c>/<c>DetachedTabWindow</c>, which hangs off a sidebar context menu or an
/// inline button since there's no title bar room to spare there) filters the shown lines by speaker,
/// translated text, or original text, case-insensitively - see <see cref="DrawSearchBar"/>.</para>
///
/// <para><b>Added 2026-08-19</b>, per explicit follow-up user request: once
/// <see cref="Services.DialogueTranslationService.IsNativeDialogueOpen"/> goes false (the NPC dialogue
/// box/cutscene subtitle actually closed), <see cref="DrawConditions"/> hides this window immediately
/// rather than waiting out <see cref="Configuration.DialogueTranslationAutoHideSeconds"/> - the user
/// specifically asked for "hide right away," not another idle-timeout variant. A quest notice (no
/// native window of its own) still uses the old idle-timeout behaviour, since there's nothing to check
/// against for that case.</para>
/// </summary>
public sealed class DialogueTranslationWindow : Window
{
    private readonly Configuration configuration;
    private readonly DialogueTranslationService dialogueService;
    private int lastDrawnCount = -1;

    // Auto-scroll to the newest line - deferred by one frame (consumed at the *top* of the next
    // Draw() call, before that frame's content is drawn), same mechanism MainWindow's own "jump to
    // bottom" uses (SetScrollY(GetScrollMaxY()), not SetScrollHereY(1f)) - the child's ScrollMaxY for
    // content just added this same frame isn't reliably settled yet (ImGui only finalizes a child's
    // content size/scroll range at the end of the frame it was drawn in), so scrolling in the same
    // frame new text appears could target a stale, too-small max and land short of the true bottom.
    private bool pendingScrollToBottom;

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

        // Fixed 2026-08-19: the idle timeout below doesn't know a cutscene is playing - an ordinary
        // dialogue-free cinematic beat can easily exceed DialogueTranslationAutoHideSeconds, hiding
        // this window mid-cutscene despite the whole class existing specifically to stay visible
        // through them (see the class doc comment). Never applies the idle check while one's active.
        if (Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInCutSceneEvent))
            return true;

        // Added 2026-08-19, per explicit user request: stay up for as long as the native NPC dialogue
        // box/cutscene subtitle is actually on screen, regardless of the idle timer - a slow reader or
        // a long pause between lines shouldn't hide this out from under someone mid-conversation.
        if (dialogueService.IsNativeDialogueOpen)
            return true;

        if (dialogueService.Entries.Count == 0)
            return false;

        // The native dialogue box (if any) is confirmed closed at this point. Per explicit user
        // request, hide immediately rather than riding out the idle timer for dialogue/cutscene-
        // subtitle content - the conversation visibly just ended, there's nothing to keep it up for.
        // A quest notice has no native window of its own to signal against, so that case still falls
        // through to the normal idle-timeout grace period below.
        var lastEntry = dialogueService.Entries[^1];
        if (lastEntry.Kind != DialogueTranslationKind.QuestNotice)
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
                // Consumes last frame's request, using this frame's now-fully-settled ScrollMaxY (the
                // new content that triggered it was already drawn once, last frame) - see
                // pendingScrollToBottom's own doc comment for why this can't just happen inline below,
                // in the same frame a new entry first appears.
                if (pendingScrollToBottom)
                {
                    ImGui.SetScrollY(ImGui.GetScrollMaxY());
                    pendingScrollToBottom = false;
                }

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
                    pendingScrollToBottom = true;
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
