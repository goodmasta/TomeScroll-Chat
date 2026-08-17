using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using CustomChat.Models;
using CustomChat.Utility;

namespace CustomChat.Windows;

/// <summary>The main chat window: a sidebar of every non-detached tab plus the selected tab's
/// messages and input box. Detached tabs render in their own <see cref="DetachedTabWindow"/> instead.
/// Tabs are created/deleted from <see cref="ConfigWindow"/>, not here - this window only lets you pop a
/// tab out, jump to its settings, or (whisper tabs only) close the conversation.</summary>
public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 BlinkBase = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 LinkshellBadgeColor = new(0.35f, 0.9f, 0.35f, 1f);

    /// <summary>Vertical gap below the (now bordered) "Messages" child and between the toolbar/input
    /// rows - tighter than the theme's default ItemSpacing.Y, which read as an oddly large gap once
    /// the message area got a visible border to actually compare it against.</summary>
    private const float TightRowSpacing = 2f;

    /// <summary>One line tall normally, growing up to this many while composing a multi-line message
    /// (Shift+Enter, see <see cref="DrawInputRow"/>) and shrinking back once it's empty again - never
    /// more than this many, even if the message has more line breaks than that (it just scrolls).</summary>
    private const int MaxComposeBoxLines = 3;

    /// <summary>Current height for the compose box *widget itself*, based on <see cref="inputText"/>'s
    /// line count - used to size the actual <c>InputTextMultiline</c> call in <see cref="DrawInputRow"/>.
    /// For the *reserved* space below the messages area (which also has to account for the destination-
    /// channel label drawn above this box), see <see cref="GetInputRowReserve"/> instead - folding the
    /// label's height into *this* function once made it leak into the input box's own size too (it's
    /// the single value <c>DrawInputRow</c> passes as the widget's height), inflating the actual text
    /// box by one whole line and visibly misaligning the messages area above it.</summary>
    private float GetComposeBoxHeight()
    {
        var lines = 1;
        foreach (var c in inputText)
        {
            if (c == '\n')
                lines++;
        }

        // GetTextLineHeightWithSpacing() * n (tried first) undersizes the box relative to what a
        // multi-line InputText actually needs - it accounts for ItemSpacing *between* lines, but not
        // the widget's own FramePadding around the text, so even 1 line came out a couple pixels
        // short of comfortably fitting. That was enough for ImGui's internal cursor-follow scroll to
        // shift the visible line up/down by a pixel or two on every keystroke as the cursor moved,
        // even with no line breaks at all. n=1 has to equal GetFrameHeight() exactly - the same
        // "one text line plus frame padding" definition already used for the icon buttons next to it
        // (so the row still lines up) - and each additional line adds its own text-line-height plus
        // the spacing between lines.
        var n = Math.Clamp(lines, 1, MaxComposeBoxLines);
        var textHeight = ImGui.GetTextLineHeight();
        var framePadding = ImGui.GetStyle().FramePadding.Y;
        var itemSpacing = ImGui.GetStyle().ItemSpacing.Y;
        return textHeight * n + framePadding * 2f + itemSpacing * (n - 1);
    }

    /// <summary>Total space to reserve below the messages area for the whole input row - the compose
    /// box itself (see <see cref="GetComposeBoxHeight"/>) *plus* the destination-channel label drawn
    /// above it (see <see cref="DrawInputRow"/>/<see cref="DrawOutgoingChannelLabel"/>), which is a
    /// separate, normally-laid-out widget with its own height, not part of the input box's own size.
    /// Uses <see cref="TightRowSpacing"/> for the gap after the label, not the theme's default
    /// <c>ItemSpacing.Y</c> (i.e. not <c>GetTextLineHeightWithSpacing()</c>) - <see cref="DrawInputRow"/>
    /// pushes that same tightened spacing for everything it draws, including the label, so using the
    /// theme default here would over-reserve relative to what's actually drawn (2026-08-13: this
    /// mismatch, compounded by the sidebar's "Close All PM" spacer not using the same tightened spacing
    /// either, is what caused the button and the compose box to visibly drift out of alignment despite
    /// both columns reserving the exact same total height - matching *totals* isn't enough if the
    /// individual gaps inside that total don't also match).</summary>
    private float GetInputRowReserve() => GetComposeBoxHeight() + ImGui.GetTextLineHeight() + TightRowSpacing;

    private readonly Plugin plugin;
    private Guid? selectedTabId;
    private string inputText = string.Empty;
    private string emoteSearch = string.Empty;
    private bool refocusInput;
    private string? pendingPrefillText;
    /// <summary>Set alongside consuming <see cref="pendingPrefillText"/> - forces the cursor to land
    /// right after the inserted text on that same frame (see the callback in <see cref="DrawInputRow"/>)
    /// instead of wherever ImGui's own default focus/text-change placement happens to put it, which
    /// isn't reliably "the end" (reported for "Reply" specifically, but applies to every PrefillInput
    /// use - a native "/" leak redirect benefits from the same "keep typing right after" behaviour).</summary>
    private bool pendingPrefillCursorToEnd;

    /// <summary>Item links queued via the native "Link" action (see
    /// <see cref="Services.NativeItemLinkWatcher"/>), consumed in order by each "&lt;link&gt;"
    /// placeholder in <see cref="inputText"/> at send time - see <see cref="AttachItemLink"/>.</summary>
    private readonly List<PendingItemLink> pendingItemLinks = new();

    // Bumped every time a message is sent, and folded into the input box's ImGui id (see
    // DrawInputRow). Without EnterReturnsTrue (removed for the multi-line Shift+Enter rework), ImGui
    // never deactivates the widget on Enter, so it stays "active" continuously while typing - and an
    // externally-cleared inputText is silently ignored by an still-active widget, which kept showing
    // its own stale internal buffer instead of the now-empty text after sending. Changing the id
    // forces ImGui to treat it as a brand new widget next frame, with no stale state to ignore.
    private int inputGeneration;

    // Right-click the message input -> "Translate to" a picked language: tracked live via the
    // InputText callback (ImGuiInputTextFlags.CallbackAlways) so the selection at the moment of the
    // right-click is known; the splice is applied on a later frame once the translation comes back
    // (background thread continuation, same pattern as TranslationService's own result cache).
    private int inputSelectionStart;
    private int inputSelectionEnd;
    private (int Start, int Length, string Translated)? pendingInputSplice;

    // Discord-style "last read position": which tab the content area is currently showing, a frozen
    // divider index into that tab's message list (set once when switching in, not updated as the
    // player reads further down - see DrawContent), and one-shot scroll requests.
    private Guid? contentTabId;
    private int dividerIndex = -1;
    private bool pendingScrollToDivider;
    private bool pendingScrollToBottom;

    // Whether the "jump to bottom" button (always visible, see DrawInputRow) is actually usable this
    // frame - captured while the "Messages" child is current, see DrawContent.
    private bool canScrollToBottom;

    // "Select text" mode: swaps the rich message rendering for a read-only plain-text transcript
    // (native ImGui click-drag selection + Ctrl+C) - see DrawContent.
    private bool selectionMode;
    private string transcriptText = string.Empty;
    private int transcriptMessageCount = -1;

    // "Search in this tab" (opened from the tab's right-click menu, see DrawTabContextMenu):
    // filters the message list down to matches - see DrawContent.
    private bool searchMode;
    private string searchQuery = string.Empty;
    private bool focusSearchInput;

    // Eye-button/auto-hide state (see PreDraw/Draw) - shrinks the window down to just the title bar
    // rather than actually closing it, since nothing is allowed to close this window (see the
    // constructor). Not persisted - always starts visible on a fresh plugin load.
    //
    // Deliberately NOT implemented via Window.Collapsed/CollapsedCondition, despite that being the
    // "correct"-looking API for this (confirmed via Dalamud's own WindowHost.cs source that
    // ApplyConditionals does call ImGui.SetNextWindowCollapsed every frame, and via ImGui's own
    // source that NoCollapse only blocks the *user's* double-click toggle, not the API) - live
    // testing showed it simply has no visible effect on this window regardless (root cause not
    // pinned down - IsTopMost's multi-viewport OS-window path is the prime suspect, but unconfirmed).
    // This instead directly forces Size down to just one frame-height tall while hidden (and back up
    // to whatever it was before, once) - a mechanism this window already relies on for its normal
    // size anyway, so it's known to actually work rather than depending on Collapsed's unconfirmed
    // behaviour in this environment.
    private bool isChatHidden;
    private bool wasChatHiddenLastFrame;
    private Vector2 lastKnownSize = new(640, 420); // matches the constructor's initial Size
    private float inactiveSeconds;
    private readonly TitleBarButton hideChatButton;

    public MainWindow(Plugin plugin)
        : base("Custom Chat###CustomChatMainWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = new Vector2(640, 420);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Always-on chat window: no close button, Esc doesn't close it, and OnClose() below
        // refuses the close so nothing (hotkey, another plugin, a stray IsOpen = false) can hide it.
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        IsOpen = true;

        // Always stays on top of other plugin windows instead of getting buried behind them.
        IsTopMost = true;

        // No collapse triangle - a gear button that opens Settings takes its place instead.
        // NoScrollbar/NoScrollWithMouse: all scrolling happens inside the "Messages" child - if the
        // window's own total content height ever slightly exceeds its size (e.g. from a future row
        // added below the message list without perfectly updating the reserved space, as already
        // happened once), it should never grow a second, outer scrollbar of its own.
        Flags |= ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip("Custom Chat settings"),
            Click = _ => plugin.OpenSettings(),
        });

        // Kept as a field (rather than only ever living inside TitleBarButtons) so PreDraw can add/
        // remove this *one* button by reference as Configuration.ShowHideChatButton is toggled live,
        // without touching the Cog button above. Icon is updated live in PreDraw too (Eye/EyeSlash) -
        // this button stays visible even while the window is hidden (title bar buttons render as part
        // of Begin() itself, independent of whether Draw() draws a body that frame), since it's the
        // only way back.
        hideChatButton = new TitleBarButton
        {
            Icon = FontAwesomeIcon.Eye,
            IconOffset = new Vector2(2, 1),
            ShowTooltip = () => ImGui.SetTooltip(isChatHidden ? "Show chat" : "Hide chat"),
            Click = _ =>
            {
                isChatHidden = !isChatHidden;
                if (!isChatHidden)
                    inactiveSeconds = 0f; // don't let a stale timer immediately re-trigger auto-hide
            },
        };
        if (Plugin.Configuration.ShowHideChatButton)
            TitleBarButtons.Add(hideChatButton);
    }

    /// <summary>Nothing is allowed to close the main chat window - it stays open for the whole session.</summary>
    public override void OnClose() => IsOpen = true;

    /// <summary>Fades the window background while unfocused (see <see cref="Configuration.FadeWindowWhenInactive"/>)
    /// and makes the title bar match the window body's own background colour in every state
    /// (focused/unfocused/collapsed) instead of whatever the current theme's accent colour is - a
    /// flat black title bar (tried first) looked like a separate strip glued onto a dark-but-not-quite-
    /// black window body; sampling the theme's actual <see cref="ImGuiCol.WindowBg"/> instead makes it
    /// read as one continuous panel, whatever shade of dark the current theme happens to use. Has to
    /// happen in <c>PreDraw</c>, before <c>Begin()</c>, since both <see cref="Window.BgAlpha"/> and any
    /// pushed style colours are only picked up at that point. <see cref="Window.IsFocused"/> reflects
    /// last frame's focus state here (this frame's Begin() hasn't run yet), an imperceptible one-frame
    /// lag for a fade.</summary>
    public override void PreDraw()
    {
        // Live-synced every frame rather than only in the constructor, since Configuration.ShowHideChatButton
        // can change any time from Settings while this window is already running.
        var showButton = Plugin.Configuration.ShowHideChatButton;
        var buttonPresent = TitleBarButtons.Contains(hideChatButton);
        if (showButton && !buttonPresent)
            TitleBarButtons.Add(hideChatButton);
        else if (!showButton && buttonPresent)
            TitleBarButtons.Remove(hideChatButton);
        hideChatButton.Icon = isChatHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye;

        // The eye button is the *only* way to un-hide (see its Click handler/the auto-hide comment
        // below) - if it's turned off in Settings there'd be no way back at all, whether already
        // hidden right now or auto-hidden later. Force-expand immediately when that happens, and
        // don't let auto-hide trigger at all while the button that would be needed to undo it is off.
        if (!showButton && isChatHidden)
            isChatHidden = false;

        // Auto-hide timer - resets the instant the window is genuinely focused, accumulates
        // otherwise. Only the eye button (manual, see its Click handler) ever un-hides once this
        // trips - just refocusing the (still-shrunk) title bar resets the timer but deliberately
        // doesn't un-hide on its own, so a stray click near it can't silently undo an intentional
        // auto-hide.
        if (IsFocused)
            inactiveSeconds = 0f;
        else
            inactiveSeconds += ImGui.GetIO().DeltaTime;

        if (showButton && Plugin.Configuration.AutoHideChatWhenInactive && inactiveSeconds >= Plugin.Configuration.AutoHideChatSeconds)
            isChatHidden = true;

        if (isChatHidden)
        {
            // Forced every single frame while hidden - width stays whatever it was, height shrinks to
            // one frame's worth (about as close to "just the title bar" as a real body region can get).
            // SizeConstraints has to be relaxed too, or the 260px MinimumSize set below would just
            // clamp this straight back up.
            Size = new Vector2(lastKnownSize.X, ImGui.GetFrameHeight());
            SizeCondition = ImGuiCond.Always;
            SizeConstraints = null;
        }
        else if (wasChatHiddenLastFrame)
        {
            // One-shot restore, the single frame right after un-hiding - forcing this with Always every
            // frame indefinitely would permanently lock the size and block the player's own manual
            // resizing, so this reverts back to "don't have an opinion" (Size = null) immediately after.
            Size = lastKnownSize;
            SizeCondition = ImGuiCond.Always;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(420, 260),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
            };
        }
        else
        {
            Size = null;
        }

        wasChatHiddenLastFrame = isChatHidden;

        var fading = !IsFocused && Plugin.Configuration.FadeWindowWhenInactive;
        BgAlpha = fading ? Plugin.Configuration.InactiveWindowAlpha : null;

        // Same alpha the body fades to, not just the same colour - otherwise a fully-opaque title
        // bar would sit on top of an increasingly translucent body while unfocused, right back to
        // looking like two separate panels instead of one.
        var bodyColor = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var titleBarColor = fading ? new Vector4(bodyColor.X, bodyColor.Y, bodyColor.Z, Plugin.Configuration.InactiveWindowAlpha) : bodyColor;
        ImGui.PushStyleColor(ImGuiCol.TitleBg, titleBarColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, titleBarColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, titleBarColor);
    }

    /// <summary>Pops the three colours pushed in <see cref="PreDraw"/> - has to happen after
    /// <c>End()</c>, which is why it can't just be tacked onto the end of <c>Draw()</c> (this window
    /// never actually collapses, but <c>Draw()</c> only runs while the window is open and expanded,
    /// same as any Dalamud window - <c>PostDraw</c> always runs regardless).</summary>
    public override void PostDraw() => ImGui.PopStyleColor(3);

    /// <summary>Fully hides the window (not drawn at all, no PreDraw/PostDraw either - unlike
    /// <see cref="Configuration.FadeWindowWhenInactive"/>, which still draws, just translucent) while
    /// a cutscene is playing, if that's enabled (see <see cref="Configuration.HideChatDuringCutscenes"/>).
    /// Both cutscene condition flags are checked - regular cutscenes and the separate "78" one FFXIV
    /// uses for some scripted/quest cutscenes.</summary>
    public override bool DrawConditions() =>
        Plugin.PlayerState.IsLoaded && // hidden at the title screen/character select - nothing useful to show before a character is actually in the world
        (!Plugin.Configuration.HideChatDuringCutscenes ||
         !Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInCutSceneEvent));

    /// <summary>Brings the window to front and focuses the current tab's input box - the "press Enter
    /// to open chat" keybind's handler (see <see cref="Services.EnterToChatService"/>).</summary>
    public void RequestFocusInput()
    {
        RequestFocus = true;
        refocusInput = true;
    }

    /// <summary>Same as <see cref="RequestFocusInput"/>, but also seeds the input box with text - the
    /// native-chat-leak-through redirect's handler (see <see cref="Services.NativeChatInputWatcher"/>),
    /// covering both a typed "/" command and a right-click "Link" item/map-coordinate insertion: the
    /// native input already captured the content before this fires, so it has to be carried over
    /// explicitly rather than just refocusing an empty box. The text is deliberately applied one frame
    /// *after* the focus request (see <see cref="DrawInputRow"/>), not in the same one - ImGui's
    /// InputText selects the entire buffer when it's given keyboard focus and already-non-empty text on
    /// the very same frame, which made the next keystroke overwrite the redirected text instead of
    /// continuing after it.</summary>
    public void PrefillInput(string text)
    {
        pendingPrefillText = text;
        RequestFocus = true;
        refocusInput = true;
    }

    /// <summary>Placeholder text inserted into the compose box the moment an item gets linked via the
    /// native "Link" action - swapped for the real link at send time (see <see cref="Services.ChatSendService"/>),
    /// same approach ChatTwo uses for this. Kept in sync with <c>ChatSendService.LinkPlaceholder</c>.</summary>
    private const string ItemLinkPlaceholder = "<link>";

    /// <summary>Queues an item link and drops a "&lt;link&gt;" placeholder into the compose box - the
    /// <see cref="Services.NativeItemLinkWatcher"/> handler for the native "Link" action. Always lands
    /// on the main window's shared compose state regardless of which tab/window last had focus, same
    /// convention as <see cref="PrefillInput"/>. Doesn't steal focus the way PrefillInput does - linking
    /// an item is an incidental action (usually done while browsing inventory, not actively typing), so
    /// yanking focus back to the chat window every time would be more disruptive than helpful. Appends
    /// directly (not via a one-frame-deferred pending field like <see cref="PrefillInput"/> needs) -
    /// the same direct-append the emote picker already does successfully, since nothing here also
    /// grants keyboard focus on the same frame (the specific combination that trips up ImGui's
    /// InputText, per <see cref="PrefillInput"/>'s own doc comment).</summary>
    public void AttachItemLink(PendingItemLink link)
    {
        pendingItemLinks.Add(link);
        if (inputText.Length > 0 && !char.IsWhiteSpace(inputText[^1]))
            inputText += " ";
        inputText += ItemLinkPlaceholder;
    }

    public override void Draw()
    {
        // Nothing drawn while hidden - the title bar (and the eye button on it) still renders
        // regardless, since that happens as part of Begin() itself, outside this method entirely.
        if (isChatHidden)
            return;

        // Captured every frame the body is actually visible - this is what PreDraw restores the
        // window to the moment it's un-hidden, so shrinking it while hidden doesn't lose whatever
        // size the player last had it at (including their own manual resizing).
        lastKnownSize = ImGui.GetWindowSize();

        using var table = ImRaii.Table("CustomChatLayout", 2, ImGuiTableFlags.Resizable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Sidebar", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        DrawSidebar();

        ImGui.TableNextColumn();
        DrawContent();
    }

    private void DrawSidebar()
    {
        // Same reserve formula as DrawContent's "Messages" child (not the old flat -28px) so the
        // sidebar and message area end up exactly the same height, and "Close All PM" lines up evenly
        // with the input row instead of sitting at a slightly different Y from one hardcoded pixel
        // count and the other computed from the current font/frame size. Uses the same
        // GetInputRowReserve() as the (now multi-line, auto-growing) input row, not a single frame
        // height, so the two stay aligned as the input row grows/shrinks.
        var bottomReserve = GetInputRowReserve() + TightRowSpacing;
        using (var child = ImRaii.Child("Sidebar", new Vector2(0, -bottomReserve), true))
        {
            if (child.Success)
            {
                // Snapshot (and reorder) the list: closing a whisper tab from the context menu below
                // mutates TabManager.Tabs mid-draw, which would otherwise throw iterating the live
                // list. Regular tabs always keep their existing relative order and stay above every
                // PM tab; within the PM tabs specifically, unread ones bubble to the top of that
                // group (not above the regular tabs) so a new whisper is easy to spot without
                // reshuffling the whole sidebar. OrderBy/ThenBy are stable, so ties (same PM-ness,
                // same unread-ness) keep their original relative order.
                var orderedTabs = plugin.TabManager.Tabs
                    .Where(t => !t.IsDetached)
                    .OrderBy(t => t.IsPmTab ? 1 : 0)
                    .ThenBy(t => t.IsPmTab && t.UnreadCount > 0 ? 0 : 1)
                    .ToList();

                foreach (var tab in orderedTabs)
                {
                    DrawTabRow(tab);

                    if (ImGui.BeginPopupContextItem($"ctx_{tab.Id}"))
                    {
                        DrawTabContextMenu(tab);
                        ImGui.EndPopup();
                    }
                }
            }
        }

        // The Sidebar child above reserves the *same* bottomReserve as the Messages child in
        // DrawContent - which is tall enough for both the destination-channel label and the compose
        // box below it, since that's what's actually drawn there. This column only draws one widget
        // (the button below) in that same reserved space, so without a spacer matching the label's own
        // height it renders flush at the top of the reservation - noticeably higher than the compose
        // box, which sits *below* its own label.
        //
        // Two things have to match DrawContent exactly, not just the *total* reserved height: the
        // spacer's own size must be GetTextLineHeight() alone (the label's actual glyph height, no
        // spacing baked in) - using GetTextLineHeightWithSpacing() here double-counts, since ImGui
        // *also* applies its own automatic gap after the Dummy on top of whatever size it's given, the
        // same as after any other widget, including the label. And that automatic gap has to be the
        // same TightRowSpacing DrawInputRow pushes for its own label-to-input-box gap, not the theme's
        // wider default - pushed here too for exactly that reason (2026-08-13: matching totals while
        // these two gaps individually disagreed is what caused the button and compose box to visibly
        // drift apart despite both columns reserving the same total height).
        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(itemSpacing.X, TightRowSpacing));

        // Fills the same one-line spacer slot the alignment math above already reserves (see the
        // comment above) - a plain Text widget is exactly GetTextLineHeight() tall, same as the Dummy
        // it replaces, so this doesn't need its own reserve math.
        var allTabs = plugin.TabManager.Tabs;
        var totalTabCount = allTabs.Count;
        var pmTabCount = allTabs.Count(t => t.IsPmTab);
        var unreadTotal = allTabs.Sum(t => t.UnreadCount);
        ImGui.TextDisabled($"{totalTabCount} tabs, {pmTabCount} PM, {unreadTotal} unread");

        var hasPmTabs = plugin.TabManager.Tabs.Any(t => t.IsPmTab);
        using (ImRaii.Disabled(!hasPmTabs))
        {
            if (ImGui.Button("Close All PM", new Vector2(-1, 0)))
            {
                plugin.CloseAllWhisperTabs();
                if (selectedTabId != null && !plugin.TabManager.Tabs.Any(t => t.Id == selectedTabId))
                    selectedTabId = null;
            }
        }

        ImGui.PopStyleVar();
    }

    /// <summary>
    /// One sidebar row: a full-width Selectable with an empty label for the click/hover area, with
    /// the name and unread count painted on top via <see cref="ImDrawList.AddText"/> so the count can
    /// be coloured red and the name can pulse - a single Selectable label can't have mixed colours.
    /// Deliberately uses the draw list directly instead of more ImGui widgets positioned via
    /// SetCursorScreenPos: that approach (tried first) fed back into ImGui's own cursor/layout state
    /// and threw off every following row's horizontal position, drifting further left row by row.
    /// Painting onto the draw list doesn't touch layout state at all, so it can't do that.
    /// </summary>
    private void DrawTabRow(ChatTabConfig tab)
    {
        var selected = tab.Id == selectedTabId;
        if (ImGui.Selectable($"##tab_{tab.Id}", selected))
            selectedTabId = tab.Id;

        var itemMin = ImGui.GetItemRectMin();
        var itemMax = ImGui.GetItemRectMax();
        var textY = itemMin.Y + (itemMax.Y - itemMin.Y - ImGui.GetTextLineHeight()) / 2f;
        var drawList = ImGui.GetWindowDrawList();

        // Per-tab override (see Settings > Tabs) if set, otherwise the global default from
        // Settings > General - whisper tabs default to WhisperNotifyColor, regular tabs to the
        // separate blink/count defaults.
        var config = Plugin.Configuration;
        var blinkColor = tab.BlinkColorOverride ?? (tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelBlinkColor);
        var countColor = tab.UnreadCountColorOverride ?? (tab.IsPmTab ? config.WhisperNotifyColor : config.ChannelUnreadCountColor);

        // Tab's own explicit colour (Settings > Tabs) first, then - for whisper tabs only - the
        // partner's by-nickname preset (Settings > Players, or "Set Tab Colour" on their messages),
        // so a colour picked before this exact tab existed still applies once it's (re)created.
        var restColor = tab.TabColorOverride ??
                         (tab.IsPmTab && !string.IsNullOrEmpty(tab.PmPartnerKey) && config.PlayerTabColors.TryGetValue(tab.PmPartnerKey, out var playerTabColor)
                             ? playerTabColor
                             : BlinkBase);

        var isBlinking = tab.UnreadCount > 0 && tab.ShouldNotify && !tab.MuteUnreadIndicator;
        var nameColor = isBlinking
            ? Vector4.Lerp(restColor, blinkColor, (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f)
            : restColor;

        var textX = itemMin.X + 4;

        if (!string.IsNullOrEmpty(tab.IconEmoji))
        {
            var iconSize = ImGui.GetTextLineHeight();
            var iconTexture = plugin.EmoteService.TryGetTexture(tab.IconEmoji);
            if (iconTexture != null)
            {
                var iconMin = new Vector2(textX, textY);
                drawList.AddImage(iconTexture.Handle, iconMin, iconMin + new Vector2(iconSize, iconSize));
            }

            textX += iconSize + 4;
        }

        // Same friend marker shown next to a friend's name in the message list (see
        // ChatMessageRenderer.DrawMessage) - also shown here so a friend's whisper tab is
        // recognisable in the sidebar itself, not just once you're already looking at their messages.
        if (tab.IsPmTab && config.FriendMarkerEnabled && !string.IsNullOrEmpty(config.FriendMarkerEmoji) &&
            !string.IsNullOrEmpty(tab.PmPartnerKey) && plugin.FriendListService.IsFriendKey(tab.PmPartnerKey))
        {
            var markerSize = ImGui.GetTextLineHeight();
            var markerTexture = plugin.EmoteService.TryGetTexture(config.FriendMarkerEmoji);
            if (markerTexture != null)
            {
                var markerMin = new Vector2(textX, textY);
                drawList.AddImage(markerTexture.Handle, markerMin, markerMin + new Vector2(markerSize, markerSize));
            }

            textX += markerSize + 4;
        }

        // Green "[LS]"/"[CWLS]" tag ahead of the name for auto-managed linkshell tabs specifically
        // (see LinkshellWatcherService) - not shown on a manually-created tab that just happens to
        // include an Ls/CrossLinkShell channel, since there's no single slot index to label it with.
        if (tab.IsAutoLinkshellTab)
        {
            var badge = tab.IsCrossWorldLinkshell ? "[CWLS] " : "[LS] ";
            drawList.AddText(new Vector2(textX, textY), ImGui.ColorConvertFloat4ToU32(LinkshellBadgeColor), badge);
            textX += ImGui.CalcTextSize(badge).X;
        }

        var namePos = new Vector2(textX, textY);
        drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(nameColor), tab.Name);

        if (tab.UnreadCount > 0 && !tab.MuteUnreadIndicator)
        {
            var countPos = new Vector2(namePos.X + ImGui.CalcTextSize(tab.Name).X + 4, textY);
            drawList.AddText(countPos, ImGui.ColorConvertFloat4ToU32(countColor), $"({tab.UnreadCount})");
        }
    }

    private void DrawTabContextMenu(ChatTabConfig tab)
    {
        using (ImRaii.Disabled(tab.UnreadCount == 0))
        {
            if (ImGui.MenuItem("Mark all as read"))
            {
                tab.UnreadCount = 0;
                if (tab.Id == contentTabId)
                    dividerIndex = -1;
                plugin.TabManager.Save();
            }
        }

        if (ImGui.MenuItem(tab.MuteUnreadIndicator ? "Unmute unread indicator" : "Mute unread indicator"))
        {
            tab.MuteUnreadIndicator = !tab.MuteUnreadIndicator;
            plugin.TabManager.Save();
        }

        ImGui.Separator();

        // Moves within this tab's own group (whisper vs. regular) - see TabManager.MoveTab for why a
        // raw-list swap alone wouldn't reliably do anything visible.
        using (ImRaii.Disabled(!plugin.TabManager.CanMoveTab(tab, -1)))
        {
            if (ImGui.MenuItem("Move up"))
                plugin.TabManager.MoveTab(tab, -1);
        }

        using (ImRaii.Disabled(!plugin.TabManager.CanMoveTab(tab, 1)))
        {
            if (ImGui.MenuItem("Move down"))
                plugin.TabManager.MoveTab(tab, 1);
        }

        ImGui.Separator();

        if (ImGui.MenuItem("Search..."))
        {
            selectedTabId = tab.Id;
            searchMode = true;
            selectionMode = false;
            focusSearchInput = true;
        }

        if (ImGui.MenuItem("Pop out to floating window"))
            plugin.SetTabDetached(tab, true);

        if (ImGui.MenuItem("Export to file..."))
            plugin.ExportTabToFile(tab);

        if (tab.IsPmTab)
        {
            // Whisper history is keyed by conversation partner, not by this tab's id, so closing it
            // here never deletes any messages - it just hides the tab until the next message (or the
            // native/in-chat "Send Tell") reopens it. Regular tabs are only managed from settings.
            ImGui.Separator();
            if (ImGui.MenuItem("Close chat"))
            {
                plugin.TabManager.RemoveTab(tab);
                if (selectedTabId == tab.Id)
                    selectedTabId = null;
            }
        }
        else
        {
            if (ImGui.MenuItem("Edit channels/filter..."))
                plugin.OpenTabEditor(tab.Id);
        }
    }

    private void DrawContent()
    {
        var tab = ResolveSelectedTab();
        if (tab == null)
        {
            ImGui.TextDisabled("No tabs yet - create one on the left.");
            return;
        }

        if (searchMode)
            DrawSearchBar(tab);

        if (contentTabId != tab.Id)
        {
            // Persist wherever the previously-viewed tab's unread count ended up (see the visibility
            // tracking below) before switching what the divider index refers to.
            if (contentTabId != null)
                plugin.TabManager.Save();

            contentTabId = tab.Id;
            var messagesNow = plugin.TabMessageBuffer.GetMessages(tab);
            // Frozen at the tab's unread count *as of opening it* - deliberately not recomputed as
            // reading progresses, so the divider stays put where "new" started, Discord-style.
            dividerIndex = tab.UnreadCount > 0 ? Math.Max(0, messagesNow.Count - tab.UnreadCount) : -1;
            pendingScrollToDivider = dividerIndex >= 0;
        }

        // Leave room for the input row below (the "jump to bottom" button now lives flush against it,
        // see DrawInputRow, rather than a separate row of its own). Uses TightRowSpacing rather than
        // the theme's default ItemSpacing.Y, matching the spacing actually applied below the child
        // (see there) - otherwise this would over-reserve relative to what's really drawn, since the
        // gap now visibly reads as a gap against the message area's own border. GetInputRowReserve()
        // (not a single frame height) since the input is a multi-line, auto-growing box, see DrawInputRow.
        var bottomReserve = GetInputRowReserve() + TightRowSpacing;
        using (var child = ImRaii.Child("Messages", new Vector2(0, -bottomReserve), true))
        {
            if (child.Success)
            {
                // Scoped to just this child (not the sidebar/buttons/whole window) - resets
                // automatically when the child ends. 14pt is the slider's default, i.e. 1x scale.
                ImGui.SetWindowFontScale(Plugin.Configuration.FontSize / 14f);
                var messages = plugin.TabMessageBuffer.GetMessages(tab);

                if (selectionMode)
                {
                    DrawSelectionTranscript(tab, messages);
                }
                else
                {
                    if (pendingScrollToBottom)
                    {
                        ImGui.SetScrollY(ImGui.GetScrollMaxY());
                        pendingScrollToBottom = false;
                        dividerIndex = -1;
                    }

                    var wasScrollingToDivider = pendingScrollToDivider;
                    var lastVisible = ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, PrefillInput, plugin.OpenTellToKey, plugin.SendPartyInvite, plugin.SendFriendRequest, plugin.ViewAdventurerPlate, plugin.OpenMapLink, plugin.ItemTooltipService, plugin.ItemContextService, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider, searchMode ? searchQuery : null);
                    pendingScrollToDivider = false;

                    // Unread count shrinks as messages actually scroll into view, not all at once on
                    // open - skipped while searching, since a filtered view's "last visible" doesn't
                    // reflect genuine reading progress through the tab.
                    if (!searchMode && lastVisible >= 0)
                    {
                        var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                        if (newUnread < tab.UnreadCount)
                            tab.UnreadCount = newUnread;
                    }

                    // Never auto-follow to the bottom on the same frame we just scrolled to the "New
                    // messages" divider - leftover scroll state from whatever tab was previously shown in
                    // this same child can otherwise read as "already at the bottom" (e.g. clamped down to
                    // the new, shorter content's max) and immediately snap back past the divider. Also
                    // skipped while searching - the filtered list's size changes as the query changes,
                    // which would otherwise fight the player's own scroll position while typing.
                    if (!searchMode && !wasScrollingToDivider && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                        ImGui.SetScrollHereY(1f);
                }

                // Captured here (inside the child, the only place its scroll state is queryable) for
                // the "jump to bottom" button drawn later in DrawInputRow - always visible now, but
                // only clickable while there's actually somewhere below to jump to.
                canScrollToBottom = ImGui.GetScrollY() < ImGui.GetScrollMaxY() - 2f;
            }
        }

        // Tighter than the theme's default ItemSpacing.Y - see bottomReserve above for why the two
        // have to stay in sync.
        var itemSpacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(itemSpacing.X, TightRowSpacing));
        DrawInputRow(tab);
        ImGui.PopStyleVar();
    }

    /// <summary>The Ctrl+F search bar: filters the message list live as the query changes, closes on
    /// Escape or the "x" button. Drawn above the "Messages" child so its own height is automatically
    /// subtracted from what's left for the child, no manual reserve math needed.</summary>
    private void DrawSearchBar(ChatTabConfig tab)
    {
        var closeSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(closeSize + ImGui.GetStyle().ItemSpacing.X));

        if (focusSearchInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearchInput = false;
        }

        ImGui.InputTextWithHint($"##search_{tab.Id}", "Search in this tab...", ref searchQuery, 200);

        var escapePressed = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape);

        ImGui.SameLine(0, 0);
        var closeClicked = ImGui.Button($"X##searchclose_{tab.Id}", new Vector2(closeSize, closeSize));

        if (escapePressed || closeClicked)
        {
            searchMode = false;
            searchQuery = string.Empty;
        }

        ImGui.Separator();
    }

    /// <summary>Read-only plain-text transcript shown instead of the normal rich message list while
    /// "select text" mode is on - see the field comment on <see cref="selectionMode"/>. Rebuilt only
    /// when the message count changes, not every frame.</summary>
    private void DrawSelectionTranscript(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages)
    {
        if (transcriptMessageCount != messages.Count)
        {
            transcriptText = ChatMessageRenderer.BuildTranscript(messages);
            transcriptMessageCount = messages.Count;
        }

        ImGui.InputTextMultiline($"##transcript_{tab.Id}", ref transcriptText, transcriptText.Length + 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
    }

    /// <summary>Byte offsets (into the native UTF-8 compose-box buffer - see
    /// <see cref="WrapComposeLineIfNeeded"/>) of every "\n" this project's own auto-wrap simulation
    /// inserted, tagged with whether it replaced a space (soft break) or was inserted into nothing
    /// (hard break) - see <see cref="StripWrapNewlines"/> for why this exists and
    /// <see cref="ReconcileWrapNewlines"/> for how it survives further edits. An earlier version tried
    /// tagging each wrap newline in the text itself with an "invisible" Unicode marker character
    /// instead - reverted (2026-08-13) after it turned out not to actually be invisible in this game's
    /// font (rendered as a visible "="), which also broke the marker-based strip-before-send logic that
    /// depended on it. Tracking positions out-of-band like this needs no such assumption about font
    /// glyph coverage.</summary>
    private readonly List<(int Position, bool IsSoftBreak)> wrapNewlines = new();

    /// <summary>The compose box's native UTF-8 buffer contents as of the end of the last
    /// <see cref="WrapComposeLineIfNeeded"/> call - the baseline <see cref="ReconcileWrapNewlines"/>
    /// diffs the current frame's buffer against to figure out what edit happened since then (typing,
    /// backspace, a paste, an external mutation like <see cref="PrefillInput"/>/the emote picker, or
    /// this same function's own wrap insertion) and shift <see cref="wrapNewlines"/> accordingly.</summary>
    private byte[] lastWrapSnapshot = Array.Empty<byte>();

    /// <summary>Shifts/drops tracked <see cref="wrapNewlines"/> positions for whatever single edit
    /// happened to the compose box's buffer since the last time this ran (every frame, so normally just
    /// one keystroke's worth) - found via simple common-prefix/common-suffix diffing against
    /// <see cref="lastWrapSnapshot"/>, which correctly isolates any single contiguous edit region
    /// (covers ordinary typing, backspace/delete, and inserting a character in the middle of existing
    /// text - exactly the "fixing a typo by editing mid-string" case that a naive fixed-position
    /// assumption would get wrong). A tracked position that falls *inside* the edited region is dropped
    /// (the edit touched that wrap point directly, so it's no longer reliably "ours" to identify) rather
    /// than guessed at; one that falls after it shifts by the edit's length delta; one before it is
    /// left alone.</summary>
    private void ReconcileWrapNewlines(ReadOnlySpan<byte> currentBytes)
    {
        var oldBytes = lastWrapSnapshot;
        var minLen = Math.Min(oldBytes.Length, currentBytes.Length);

        var prefix = 0;
        while (prefix < minLen && oldBytes[prefix] == currentBytes[prefix])
            prefix++;

        var maxSuffix = minLen - prefix;
        var suffix = 0;
        while (suffix < maxSuffix && oldBytes[oldBytes.Length - 1 - suffix] == currentBytes[currentBytes.Length - 1 - suffix])
            suffix++;

        var oldChangeLen = oldBytes.Length - prefix - suffix;
        var newChangeLen = currentBytes.Length - prefix - suffix;
        if (oldChangeLen == 0 && newChangeLen == 0)
            return; // nothing changed since last frame

        var changeStart = prefix;
        var changeEnd = changeStart + oldChangeLen;
        var delta = newChangeLen - oldChangeLen;

        for (var i = wrapNewlines.Count - 1; i >= 0; i--)
        {
            var (position, isSoftBreak) = wrapNewlines[i];
            if (position >= changeStart && position < changeEnd)
                wrapNewlines.RemoveAt(i);
            else if (position >= changeEnd)
                wrapNewlines[i] = (position + delta, isSoftBreak);
        }
    }

    /// <summary>Converts a UTF-8 *byte* offset (as tracked in <see cref="wrapNewlines"/>) into the
    /// matching .NET *character* index into <paramref name="text"/> - needed because <c>inputText</c>
    /// (what's actually bound to the widget, and what <see cref="StripWrapNewlines"/> operates on) is a
    /// plain C# string indexed by UTF-16 code unit, not the byte-indexed native buffer the callback
    /// sees - they only agree for pure-ASCII text. Treats a surrogate pair as one indivisible unit so a
    /// byte offset can never end up splitting one.</summary>
    private static int ByteOffsetToCharIndex(string text, int byteOffset)
    {
        var bytes = 0;
        var charIndex = 0;
        while (charIndex < text.Length && bytes < byteOffset)
        {
            var charLen = char.IsHighSurrogate(text[charIndex]) && charIndex + 1 < text.Length ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(text, charIndex, charLen);
            charIndex += charLen;
        }

        return charIndex;
    }

    /// <summary>Undoes every visual-only wrap this project's own auto-wrap simulation ever inserted (see
    /// <see cref="wrapNewlines"/>/<see cref="WrapComposeLineIfNeeded"/>) - called right before a message
    /// is actually sent, so what goes out never depends on how narrow the compose box happened to be
    /// while it was typed. Processes tracked positions from the end backward so removing a hard-break
    /// newline (which shortens the string by one character) never invalidates an earlier, not-yet
    /// processed position. Defensively re-checks that a tracked position still actually points at a
    /// "\n" before touching it - <see cref="ReconcileWrapNewlines"/> is best-effort, not a mathematical
    /// guarantee, for every conceivable edit sequence.</summary>
    private string StripWrapNewlines(string text)
    {
        if (wrapNewlines.Count == 0)
            return text;

        var sb = new StringBuilder(text);
        foreach (var (bytePosition, isSoftBreak) in wrapNewlines.OrderByDescending(w => w.Position))
        {
            var charIndex = ByteOffsetToCharIndex(text, bytePosition);
            if (charIndex >= sb.Length || sb[charIndex] != '\n')
                continue;

            if (isSoftBreak)
                sb[charIndex] = ' ';
            else
                sb.Remove(charIndex, 1);
        }

        return sb.ToString();
    }

    /// <summary>Dear ImGui's InputTextMultiline has no built-in word-wrap - long lines just scroll
    /// horizontally. This simulates it by physically inserting a real newline once the line containing
    /// the cursor gets too wide, breaking at the last space in that line (close enough to a proper
    /// word-wrap point since this runs every frame - <see cref="ImGuiInputTextFlags.CallbackAlways"/> -
    /// and so catches the overflow right as the newest character causes it, not on a whole
    /// already-typed paragraph). If there's no space to break at (one long unbroken word/URL), it's
    /// left alone rather than risk a hard break landing mid-codepoint in multi-byte UTF-8 text -
    /// <see cref="ImGuiInputTextCallbackData.CursorPos"/> and friends are UTF-8 *byte* offsets, not
    /// character offsets, which matters for Cyrillic (2 bytes/char) text. Every inserted newline's
    /// position is tracked in <see cref="wrapNewlines"/> so it can be undone before sending (see
    /// <see cref="StripWrapNewlines"/>) - this simulation still isn't perfect (an edit made while the
    /// cursor is elsewhere on a long paragraph can still misfire, e.g. re-wrapping mid-edit), but a
    /// misfire is now only ever a cosmetic wrinkle while typing, never a corrupted outgoing message.</summary>
    private void WrapComposeLineIfNeeded(ImGuiInputTextCallbackDataPtr data, float wrapWidth)
    {
        RemoveJustInsertedEnterNewline(data);
        ReconcileWrapNewlines(data.BufTextSpan);
        DoWrapCheck(data, wrapWidth);
        lastWrapSnapshot = data.BufTextSpan.ToArray();
    }

    /// <summary>Plain Enter (no Shift) triggers "send" (see <see cref="DrawInputRow"/>), but Dear
    /// ImGui's own default behaviour for *any* Enter keypress in a multiline InputText is to insert a
    /// real newline at the cursor first, unconditionally, regardless of what this plugin's own code
    /// does with the text afterward. <see cref="DrawInputRow"/>'s own cleanup
    /// (<c>if (inputText.EndsWith('\n')) ...</c>) only catches this when the cursor happened to already
    /// be at the very end of the text - if the player had navigated back into the middle of the
    /// message (e.g. to fix a typo, like inserting the missing "o" in "&lt;ps&gt;" to get "&lt;pos&gt;")
    /// and pressed Enter without moving back to the end first, ImGui's own newline lands wherever the
    /// cursor was instead, splitting the text there (reported as "&lt;pos&gt;" being sent as
    /// "&lt;po" + a real newline + "s&gt;" - nothing to do with <see cref="wrapNewlines"/> at all, despite
    /// the visible symptom looking just like a wrap-corruption bug). Removed here instead, in byte
    /// space (matching this whole file's established care around byte vs. character offsets for this
    /// callback), by deleting the byte immediately before the cursor if it's a "\n" - ImGui
    /// inserts-and-advances, so that's always where its own newline would land, the instant after it
    /// did that.</summary>
    private static void RemoveJustInsertedEnterNewline(ImGuiInputTextCallbackDataPtr data)
    {
        if (ImGui.GetIO().KeyShift || !(ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
            return;

        var bytes = data.BufTextSpan;
        var cursorByte = Math.Clamp(data.CursorPos, 0, bytes.Length);
        if (cursorByte > 0 && bytes[cursorByte - 1] == (byte)'\n')
            data.DeleteChars(cursorByte - 1, 1);
    }

    private void DoWrapCheck(ImGuiInputTextCallbackDataPtr data, float wrapWidth)
    {
        var bytes = data.BufTextSpan;
        if (bytes.Length == 0 || wrapWidth <= 0)
            return;

        var cursorByte = Math.Clamp(data.CursorPos, 0, bytes.Length);

        var lineStartByte = 0;
        for (var i = 0; i < cursorByte; i++)
        {
            if (bytes[i] == (byte)'\n')
                lineStartByte = i + 1;
        }

        var lineEndByte = bytes.Length;
        for (var i = cursorByte; i < bytes.Length; i++)
        {
            if (bytes[i] == (byte)'\n')
            {
                lineEndByte = i;
                break;
            }
        }

        if (lineEndByte <= lineStartByte)
            return;

        var line = Encoding.UTF8.GetString(bytes[lineStartByte..lineEndByte]);
        if (ImGui.CalcTextSize(line).X <= wrapWidth)
            return;

        var lastSpaceIndex = line.LastIndexOf(' ');
        if (lastSpaceIndex > 0)
        {
            var spaceByteOffset = lineStartByte + Encoding.UTF8.GetByteCount(line[..lastSpaceIndex]);
            data.DeleteChars(spaceByteOffset, 1);
            data.InsertChars(spaceByteOffset, "\n");
            wrapNewlines.Add((spaceByteOffset, true));
            return;
        }

        // No space anywhere in the line (one long unbroken word/URL/etc, or - as reported - random
        // text with no spaces at all) - the earlier "leave it alone" behavior here just meant the line
        // never wrapped at all in that case. Hard-break instead: scan backward by .NET *character*
        // index (never a raw byte count, which could land mid-codepoint for multi-byte UTF-8 like
        // Cyrillic) for the longest prefix that still fits, and break right after it.
        //
        // Short unbroken tokens (2026-08-13) are deliberately exempted from this, left to just overflow
        // the box horizontally a little instead: a hard break lands wherever the pixel-width threshold
        // happens to fall, with zero awareness of what the token actually is, and this hard-break path
        // is more likely to misfire mid-edit than the soft-break one above (see wrapNewlines' own doc
        // comment for why a misfire is at least no longer able to corrupt what's actually sent).
        if (line.Length <= 16)
            return;

        for (var i = line.Length - 1; i > 0; i--)
        {
            if (ImGui.CalcTextSize(line[..i]).X > wrapWidth)
                continue;

            var breakByteOffset = lineStartByte + Encoding.UTF8.GetByteCount(line[..i]);
            data.InsertChars(breakByteOffset, "\n");
            wrapNewlines.Add((breakByteOffset, false));
            return;
        }
    }

    /// <summary>One dimmed line above the compose box naming exactly what sending will do - the tab's
    /// <see cref="ChatTabConfig.OutgoingChannelCommand"/> as literally configured (e.g. "/p", "/fc",
    /// "/tell Name@World"), or a note that it follows whatever channel the game's own UI last had
    /// active when that's empty (built-in "Log" tab, or any custom tab left without one). Shown as the
    /// raw command rather than translated to a friendly channel name - always exactly accurate for
    /// custom tabs, which can have any outgoing command a player could type, not just the built-in
    /// five this plugin ships with defaults for.</summary>
    /// <summary>Plain <see cref="ImGui.TextDisabled"/> either way - deliberately never a taller widget
    /// like a Combo, even when the picker below is offered, since this label's height is baked into
    /// <see cref="GetInputRowReserve"/> as exactly <see cref="ImGui.GetTextLineHeight"/> (see that
    /// method's own comment for the alignment history this would otherwise break). The picker is a
    /// popup instead - popups float over everything and don't consume any layout space of their own,
    /// so the label's drawn size never changes regardless of whether one is offered.
    ///
    /// <para>No "use whatever the game's current default channel is" option any more (removed per
    /// explicit request) - a blank <see cref="ChatTabConfig.OutgoingChannelCommand"/> is now only ever
    /// a transient state, healed below to the tab's first sendable channel the moment there is one.
    /// A tab with none at all (e.g. the built-in "Log" tab) has nothing to default to, so
    /// <see cref="DrawInputRow"/> disables the compose controls entirely for it instead - see there.</para>
    /// </summary>
    private void DrawOutgoingChannelLabel(ChatTabConfig tab)
    {
        var sendable = ChatChannelCatalog.SendableChannels.Where(c => tab.Channels.Contains(c.Type)).ToList();

        // Self-limiting - only fires the one frame the command is still actually blank, so it's safe
        // to just check this every draw rather than as a one-time migration pass.
        if (sendable.Count > 0 && string.IsNullOrEmpty(tab.OutgoingChannelCommand))
        {
            tab.OutgoingChannelCommand = sendable[0].Command;
            plugin.TabManager.Save();
        }

        if (string.IsNullOrEmpty(tab.OutgoingChannelCommand))
        {
            ImGui.TextDisabled("No writable channel in this tab - nothing to send to.");
            return;
        }

        // Only offered once there's an actual choice to make - a tab with only 1 sendable channel
        // (or a PM tab, pinned to its "/tell Name@World") just shows the plain label.
        if (sendable.Count <= 1)
        {
            ImGui.TextDisabled($"Sending to: {tab.OutgoingChannelCommand}");
            return;
        }

        ImGui.TextDisabled($"Sending to: {tab.OutgoingChannelCommand} (click to change)");
        if (ImGui.IsItemClicked())
            ImGui.OpenPopup($"OutgoingChannelPicker_{tab.Id}");

        if (ImGui.BeginPopup($"OutgoingChannelPicker_{tab.Id}"))
        {
            foreach (var channel in sendable)
            {
                var isSelected = tab.OutgoingChannelCommand == channel.Command;
                if (ImGui.MenuItem((isSelected ? "> " : "  ") + channel.Label))
                {
                    tab.OutgoingChannelCommand = channel.Command;
                    plugin.TabManager.Save();
                }
            }

            ImGui.EndPopup();
        }
    }

    /// <summary>The message input box - a multi-line box (see <see cref="GetComposeBoxHeight"/>) so
    /// Shift+Enter can insert an actual line break, Telegram/Discord-style - with a "jump to bottom"
    /// button (always visible, but only actually clickable while scrolled up from the bottom - see
    /// <see cref="canScrollToBottom"/>), a "select text" toggle, and an emote-picker smiley button, all
    /// attached flush to its right edge (zero spacing between all four) instead of any of them living
    /// on a separate row.</summary>
    private void DrawInputRow(ChatTabConfig tab)
    {
        var iconSize = ImGui.GetFrameHeight();

        DrawOutgoingChannelLabel(tab);

        // Resolved *after* the label above, which is what actually heals a blank command to the
        // tab's first sendable channel this same frame - a tab still blank at this point genuinely
        // has nowhere to send to (e.g. the built-in "Log" tab), so composing is disabled outright
        // rather than silently falling back to whatever the game's ambient channel happens to be.
        var canWrite = tab.IsPmTab || !string.IsNullOrEmpty(tab.OutgoingChannelCommand);

        // Re-focusing after a send has to happen right before the input box is submitted (offset 0 =
        // "the very next widget") - doing it *after*, like before the icon buttons were added here,
        // would now count back through them/their popups instead of the input box, an unpredictable
        // number of widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }
        else if (pendingPrefillText != null)
        {
            // Deliberately not applied on the same frame the focus request above fires (see
            // PrefillInput) - it lands here, one frame later, once the widget is already active.
            // Appended rather than replacing outright: for a fresh "/" leak the box is normally empty
            // anyway (append behaves the same as replace), but for a right-click "Link" item/map
            // insertion the player usually already has text typed and expects the link to land after
            // it, not to wipe out what they wrote.
            if (inputText.Length > 0 && !char.IsWhiteSpace(inputText[^1]))
                inputText += " ";
            inputText += pendingPrefillText;
            pendingPrefillText = null;
            pendingPrefillCursorToEnd = true;
        }

        if (pendingInputSplice != null)
        {
            // "Translate to <language>" landing - see DrawInputTranslateMenu. Bounds are re-clamped
            // against the *current* inputText rather than trusted as-is, in case it was edited in the
            // time the translation request was in flight.
            var splice = pendingInputSplice.Value;
            var start = Math.Clamp(splice.Start, 0, inputText.Length);
            var length = Math.Clamp(splice.Length, 0, inputText.Length - start);
            inputText = inputText[..start] + splice.Translated + inputText[(start + length)..];
            pendingInputSplice = null;
        }

        // An explicit absolute width (not ImGui's usual "negative = distance from the right edge"
        // trick) so the exact same number can be reused below as the wrap width - CalcTextSize can't
        // measure against a relative size. 4 icon columns (jump/select/quick-pos/emote) - see below.
        var boxWidth = ImGui.GetContentRegionAvail().X - (iconSize * 4 + ImGui.GetStyle().ItemSpacing.X);
        var boxSize = new Vector2(boxWidth, GetComposeBoxHeight());

        // A few pixels short of the box's actual usable text width (minus its own FramePadding) as a
        // safety margin - measuring right up against the exact limit risks the wrap decision
        // flickering frame to frame from sub-pixel rounding.
        var wrapWidth = boxWidth - ImGui.GetStyle().FramePadding.X * 2f - 4f;

        using (ImRaii.Disabled(!canWrite))
        {
            ImGui.InputTextMultiline($"##input_{tab.Id}_{inputGeneration}", ref inputText, 500, boxSize, ImGuiInputTextFlags.CallbackAlways, data =>
            {
                inputSelectionStart = data.SelectionStart;
                inputSelectionEnd = data.SelectionEnd;

                if (pendingPrefillCursorToEnd)
                {
                    data.CursorPos = data.BufTextLen;
                    data.SelectionStart = data.BufTextLen;
                    data.SelectionEnd = data.BufTextLen;
                    pendingPrefillCursorToEnd = false;
                }

                WrapComposeLineIfNeeded(data, wrapWidth);
                return 0;
            });
        }

        // A multi-line InputText has no "Enter submits" concept of its own - by default Enter (with
        // or without Shift) always just inserts a newline, which is exactly what's wanted for
        // Shift+Enter but not for a plain Enter, which should send instead. Rather than fight ImGui's
        // key handling, this lets it insert the newline as normal, then - only for a plain Enter,
        // checked via the raw key state since InputText itself doesn't distinguish Shift here - strips
        // the newline it just added back off and treats it as "send" instead.
        var send = false;
        if (ImGui.IsItemFocused() && !ImGui.GetIO().KeyShift && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
        {
            if (inputText.EndsWith('\n'))
                inputText = inputText[..^1];
            send = true;

            // Forces a fresh widget next frame (see the inputGeneration field comment) - needed even
            // when the line turns out empty/whitespace-only and nothing actually gets sent below,
            // since the newline-strip just above is itself an external mutation a still-focused widget
            // would otherwise silently ignore, keeping the stale newline visible - reported as
            // "pressing Enter on an empty line still inserts a line break."
            inputGeneration++;
            refocusInput = true;
        }

        // Right-click the input box to translate what's typed - the whole text, or just the current
        // selection if there is one.
        if (ImGui.BeginPopupContextItem($"inputctx_{tab.Id}"))
        {
            DrawInputTranslateMenu();
            ImGui.EndPopup();
        }

        ImGui.SameLine(0, 0);
        bool jumpClicked;
        using (ImRaii.Disabled(!canScrollToBottom))
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            jumpClicked = ImGui.Button($"{FontAwesomeIcon.AngleDoubleDown.ToIconString()}##jumpbottom_{tab.Id}", new Vector2(iconSize, iconSize));
        if (jumpClicked)
            pendingScrollToBottom = true;

        ImGui.SameLine(0, 0);
        bool selectClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            selectClicked = ImGui.Button($"{FontAwesomeIcon.ICursor.ToIconString()}##selecttoggle_{tab.Id}", new Vector2(iconSize, iconSize));
        if (selectClicked)
        {
            selectionMode = !selectionMode;
            if (selectionMode)
                searchMode = false;
            transcriptMessageCount = -1; // force a rebuild next time selection mode is entered
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectionMode ? "Back to normal chat view" : "Select & copy text");

        ImGui.SameLine(0, 0);
        // A full-size 4th icon column, not the half-height stack tried first - that rendered
        // unreadably small/glitchy (reported), and mixed a plain-text "P" glyph's font/baseline with
        // the icon-font buttons around it in a space too cramped for either to render cleanly.
        bool quickPosClicked;
        using (ImRaii.Disabled(!canWrite))
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            quickPosClicked = ImGui.Button($"{FontAwesomeIcon.MapMarkerAlt.ToIconString()}##quickpos_{tab.Id}", new Vector2(iconSize, iconSize));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Insert <pos> - the game expands this to your current position when sent, same as typing it into the native chatbox.\nCtrl+Click: send it immediately without touching what you've typed.");
        if (quickPosClicked)
        {
            if (ImGui.GetIO().KeyCtrl)
                plugin.SendFromTab(tab, "<pos>"); // deliberately bypasses inputText/pendingItemLinks entirely - nothing typed is touched
            else
                inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + "<pos>";
        }

        ImGui.SameLine(0, 0);
        bool emoteClicked;
        using (ImRaii.Disabled(!canWrite))
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            emoteClicked = ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##emotebtn_{tab.Id}", new Vector2(iconSize, iconSize));
        if (emoteClicked)
        {
            emoteSearch = string.Empty;
            ImGui.OpenPopup($"EmotePicker_{tab.Id}");
        }

        EmotePicker.Draw($"EmotePicker_{tab.Id}", plugin.EmoteService, ref emoteSearch, code =>
        {
            inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + code + " ";
        });

        if (send && (!string.IsNullOrWhiteSpace(inputText) || pendingItemLinks.Count > 0))
        {
            plugin.SendFromTab(tab, StripWrapNewlines(inputText), pendingItemLinks);
            inputText = string.Empty;
            pendingItemLinks.Clear();
            wrapNewlines.Clear();
            lastWrapSnapshot = Array.Empty<byte>();
        }
    }

    /// <summary>The message input box's right-click menu: a "Translate to" submenu listing every
    /// language in <see cref="TranslationLanguageCatalog"/>. Translates the current selection if
    /// there is one (tracked live via the InputText callback in <see cref="DrawInputRow"/>), otherwise
    /// the whole input.</summary>
    private void DrawInputTranslateMenu()
    {
        if (string.IsNullOrEmpty(inputText) || !ImGui.BeginMenu("Translate to"))
            return;

        var min = Math.Min(inputSelectionStart, inputSelectionEnd);
        var max = Math.Max(inputSelectionStart, inputSelectionEnd);
        var hasSelection = max > min && max <= inputText.Length;
        var start = hasSelection ? min : 0;
        var length = hasSelection ? max - min : inputText.Length;
        var textToTranslate = inputText.Substring(start, length);

        foreach (var (code, name) in TranslationLanguageCatalog.Entries)
        {
            if (ImGui.MenuItem(name) && !string.IsNullOrWhiteSpace(textToTranslate))
                _ = TranslateInputAsync(start, length, textToTranslate, code);
        }

        ImGui.EndMenu();
    }

    private async Task TranslateInputAsync(int start, int length, string original, string targetLanguage)
    {
        var translated = await plugin.TranslationService.TranslateRawAsync(original, targetLanguage).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(translated))
            pendingInputSplice = (start, length, translated);
    }

    /// <summary>Marks a tab's unread counter - called by <see cref="Plugin"/> for every incoming
    /// message regardless of selection, since even the open tab may be scrolled away from the
    /// bottom; the granular visibility tracking in <see cref="DrawContent"/> brings it back down as
    /// messages actually scroll into view.</summary>
    public void NotifyUnread(ChatTabConfig tab) => tab.UnreadCount++;

    /// <summary>Switches the sidebar selection to this tab - e.g. when the right-click "Send Tell"
    /// menu item opens a whisper conversation.</summary>
    public void SelectTab(Guid tabId) => selectedTabId = tabId;

    private ChatTabConfig? ResolveSelectedTab()
    {
        if (selectedTabId != null)
        {
            foreach (var tab in plugin.TabManager.Tabs)
            {
                if (tab.Id == selectedTabId && !tab.IsDetached)
                    return tab;
            }

            selectedTabId = null;
        }

        foreach (var tab in plugin.TabManager.Tabs)
        {
            if (!tab.IsDetached)
            {
                selectedTabId = tab.Id;
                return tab;
            }
        }

        return null;
    }

    public void Dispose()
    {
    }
}
