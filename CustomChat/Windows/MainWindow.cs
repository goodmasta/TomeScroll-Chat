using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
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

    /// <summary>Vertical gap below the (now bordered) "Messages" child and between the toolbar/input
    /// rows - tighter than the theme's default ItemSpacing.Y, which read as an oddly large gap once
    /// the message area got a visible border to actually compare it against.</summary>
    private const float TightRowSpacing = 2f;

    private readonly Plugin plugin;
    private Guid? selectedTabId;
    private string inputText = string.Empty;
    private string emoteSearch = string.Empty;
    private bool refocusInput;
    private string? pendingPrefillText;

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

    /// <summary>Brings the window to front and focuses the current tab's input box - the "press Enter
    /// to open chat" keybind's handler (see <see cref="Services.EnterToChatService"/>).</summary>
    public void RequestFocusInput()
    {
        RequestFocus = true;
        refocusInput = true;
    }

    /// <summary>Same as <see cref="RequestFocusInput"/>, but also seeds the input box with text - the
    /// "typed '/' into the native chat" redirect's handler (see
    /// <see cref="Services.NativeChatInputWatcher"/>): the native input already captured the
    /// character(s) before this fires, so they have to be carried over explicitly rather than just
    /// refocusing an empty box. The text is deliberately applied one frame *after* the focus request
    /// (see <see cref="DrawInputRow"/>), not in the same one - ImGui's InputText selects the entire
    /// buffer when it's given keyboard focus and already-non-empty text on the very same frame, which
    /// made the next keystroke overwrite the redirected "/" instead of continuing after it.</summary>
    public void PrefillInput(string text)
    {
        pendingPrefillText = text;
        RequestFocus = true;
        refocusInput = true;
    }

    public override void Draw()
    {
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
        using (var child = ImRaii.Child("Sidebar", new Vector2(0, -28), true))
        {
            if (child.Success)
            {
                // Snapshot the list: closing a whisper tab from the context menu below mutates
                // TabManager.Tabs mid-draw, which would otherwise throw iterating the live list.
                foreach (var tab in plugin.TabManager.Tabs.ToList())
                {
                    if (tab.IsDetached)
                        continue;

                    DrawTabRow(tab);

                    if (ImGui.BeginPopupContextItem($"ctx_{tab.Id}"))
                    {
                        DrawTabContextMenu(tab);
                        ImGui.EndPopup();
                    }
                }
            }
        }

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

        var isBlinking = tab.UnreadCount > 0 && tab.ShouldNotify;
        var nameColor = isBlinking
            ? Vector4.Lerp(BlinkBase, blinkColor, (MathF.Sin((float)ImGui.GetTime() * 4f) + 1f) / 2f)
            : BlinkBase;

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

        var namePos = new Vector2(textX, textY);
        drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(nameColor), tab.Name);

        if (tab.UnreadCount > 0)
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
        // gap now visibly reads as a gap against the message area's own border.
        var bottomReserve = ImGui.GetFrameHeight() + TightRowSpacing;
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
                    var lastVisible = ChatMessageRenderer.DrawMessages(tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, plugin.OpenTellToKey, plugin.SendPartyInvite, plugin.SendFriendRequest, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider, searchMode ? searchQuery : null);
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

    /// <summary>The message input box with a "jump to bottom" button (always visible, but only
    /// actually clickable while scrolled up from the bottom - see <see cref="canScrollToBottom"/>), a
    /// "select text" toggle, and a Telegram/Discord-style emote-picker smiley button, all attached
    /// flush to its right edge (zero spacing between all four) instead of any of them living on a
    /// separate row.</summary>
    private void DrawInputRow(ChatTabConfig tab)
    {
        var iconSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(iconSize * 3 + ImGui.GetStyle().ItemSpacing.X));

        // Re-focusing after a send has to happen right before InputText is submitted (offset 0 = "the
        // very next widget") - doing it *after*, like before the icon buttons were added here, would
        // now count back through them/their popups instead of the input box, an unpredictable number
        // of widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
        }
        else if (pendingPrefillText != null)
        {
            // Deliberately not applied on the same frame the focus request above fires (see
            // PrefillInput) - it lands here, one frame later, once the widget is already active.
            inputText = pendingPrefillText;
            pendingPrefillText = null;
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

        var send = ImGui.InputText($"##input_{tab.Id}", ref inputText, 500, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackAlways, data =>
        {
            inputSelectionStart = data.SelectionStart;
            inputSelectionEnd = data.SelectionEnd;
            return 0;
        });

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
        bool emoteClicked;
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

        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(tab, inputText);
            inputText = string.Empty;
            refocusInput = true;
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
