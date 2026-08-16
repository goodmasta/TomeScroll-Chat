using System;
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

/// <summary>One floating window for a tab popped out of the main window - or a whisper conversation
/// configured to open this way by default (see <see cref="Configuration.OpenWhispersInSeparateWindow"/>).</summary>
public sealed class DetachedTabWindow : Window, IDisposable
{
    /// <summary>Same tightened row spacing as MainWindow - see its field comment for the reasoning.</summary>
    private const float TightRowSpacing = 2f;

    /// <summary>Same auto-growing compose box cap as MainWindow - see its field comment for the
    /// reasoning.</summary>
    private const int MaxComposeBoxLines = 3;

    private readonly Plugin plugin;
    public ChatTabConfig Tab { get; }
    private string inputText = string.Empty;
    private string emoteSearch = string.Empty;
    private bool refocusInput;

    /// <summary>Same "force a fresh widget on send" trick as MainWindow - see its field comment for
    /// the reasoning.</summary>
    private int inputGeneration;

    /// <summary>Same "GetFrameHeight()-for-one-line" formula as MainWindow - see its field comment for
    /// why GetTextLineHeightWithSpacing() * n (tried first) caused per-keystroke height jitter. Sizes
    /// only the compose box widget itself - see <see cref="GetInputRowReserve"/> for the reserved space
    /// below the messages area, which also has to account for the destination-channel label drawn
    /// above this box (folding the label's height into *this* function once made it leak into the
    /// input box's own size too, inflating the actual text box by a whole line).</summary>
    private float GetComposeBoxHeight()
    {
        var lines = 1;
        foreach (var c in inputText)
        {
            if (c == '\n')
                lines++;
        }

        var n = Math.Clamp(lines, 1, MaxComposeBoxLines);
        var textHeight = ImGui.GetTextLineHeight();
        var framePadding = ImGui.GetStyle().FramePadding.Y;
        var itemSpacing = ImGui.GetStyle().ItemSpacing.Y;
        return textHeight * n + framePadding * 2f + itemSpacing * (n - 1);
    }

    /// <summary>Total space to reserve below the messages area for the whole input row - see
    /// MainWindow's version for the full reasoning.</summary>
    private float GetInputRowReserve() => GetComposeBoxHeight() + ImGui.GetTextLineHeightWithSpacing();

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning.</summary>
    private void DrawOutgoingChannelLabel()
    {
        var target = string.IsNullOrEmpty(Tab.OutgoingChannelCommand)
            ? "current in-game chat channel"
            : Tab.OutgoingChannelCommand;
        ImGui.TextDisabled($"Sending to: {target}");
    }

    /// <summary>Same marker character MainWindow uses to tag a visual-only wrap newline (vs. a real
    /// Shift+Enter one) - see its version for the full reasoning.</summary>
    private const char WrapMarker = (char)0x200B;

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning.</summary>
    private static string StripWrapMarkers(string text) =>
        text.Replace("\n" + WrapMarker, " ").Replace(WrapMarker.ToString(), string.Empty);

    /// <summary>Same manual word-wrap simulation as MainWindow - see its version for the full
    /// reasoning (no built-in word-wrap in Dear ImGui's InputTextMultiline, byte-vs-character offset
    /// care for multi-byte UTF-8 text, and why every inserted newline is tagged with
    /// <see cref="WrapMarker"/>).</summary>
    private static void WrapComposeLineIfNeeded(ImGuiInputTextCallbackDataPtr data, float wrapWidth)
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
            data.InsertChars(spaceByteOffset, "\n" + WrapMarker);
            return;
        }

        // No space anywhere in the line - see MainWindow's version for the full reasoning, including
        // why short tokens (<=16 chars, e.g. the native "<flag>" placeholder) are exempted from the
        // hard break below rather than risk landing mid-token and silently corrupting it. Hard-break
        // by scanning backward per .NET character index (never a raw byte count) for the longest
        // prefix that still fits.
        if (line.Length <= 16)
            return;

        for (var i = line.Length - 1; i > 0; i--)
        {
            if (ImGui.CalcTextSize(line[..i]).X > wrapWidth)
                continue;

            var breakByteOffset = lineStartByte + Encoding.UTF8.GetByteCount(line[..i]);
            data.InsertChars(breakByteOffset, "\n" + WrapMarker);
            return;
        }
    }

    // Same Discord-style "last read position" tracking as MainWindow - see its DrawContent for the
    // reasoning. This window only ever shows one fixed tab, so it's frozen once at construction
    // (equivalent to "just opened") rather than re-detected on a tab switch.
    private int dividerIndex;
    private bool pendingScrollToDivider;
    private bool pendingScrollToBottom;

    // Whether the "jump to bottom" button (always visible) is actually usable this frame - captured
    // while the "Messages" child is current, see MainWindow's field comment for the same flag.
    private bool canScrollToBottom;

    // "Select text" mode: swaps the rich message rendering for a read-only plain-text transcript
    // (native ImGui click-drag selection + Ctrl+C) - see MainWindow's field comment for the same flag.
    private bool selectionMode;
    private string transcriptText = string.Empty;
    private int transcriptMessageCount = -1;

    // "Search in this tab" - see MainWindow's field comment for the same flag.
    private bool searchMode;
    private string searchQuery = string.Empty;
    private bool focusSearchInput;

    // Right-click the message input -> "Translate to" a picked language - see MainWindow's field
    // comment for the same flags.
    private int inputSelectionStart;
    private int inputSelectionEnd;
    private (int Start, int Length, string Translated)? pendingInputSplice;

    public DetachedTabWindow(Plugin plugin, ChatTabConfig tab)
        : base($"{tab.Name}###CustomChatTab_{tab.Id}")
    {
        this.plugin = plugin;
        Tab = tab;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        Size = tab.DetachedWindowSize ?? new Vector2(420, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        ShowCloseButton = true;
        IsOpen = true;

        // All scrolling happens inside the "Messages" child - see MainWindow's constructor for why
        // the window itself should never grow its own second, outer scrollbar.
        Flags |= ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        var messagesNow = plugin.TabMessageBuffer.GetMessages(tab);
        dividerIndex = tab.UnreadCount > 0 ? Math.Max(0, messagesNow.Count - tab.UnreadCount) : -1;
        pendingScrollToDivider = dividerIndex >= 0;
    }

    /// <summary>Same fade-while-unfocused and body-matching-title-bar behaviour as MainWindow.PreDraw -
    /// see there for the reasoning.</summary>
    public override void PreDraw()
    {
        var fading = !IsFocused && Plugin.Configuration.FadeWindowWhenInactive;
        BgAlpha = fading ? Plugin.Configuration.InactiveWindowAlpha : null;

        var bodyColor = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var titleBarColor = fading ? new Vector4(bodyColor.X, bodyColor.Y, bodyColor.Z, Plugin.Configuration.InactiveWindowAlpha) : bodyColor;
        ImGui.PushStyleColor(ImGuiCol.TitleBg, titleBarColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, titleBarColor);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, titleBarColor);
    }

    /// <summary>Pops the three colours pushed in <see cref="PreDraw"/> - see MainWindow.PostDraw for
    /// why this can't just be tacked onto the end of <c>Draw()</c> instead.</summary>
    public override void PostDraw() => ImGui.PopStyleColor(3);

    /// <summary>Same cutscene hiding as MainWindow - see there for the reasoning.</summary>
    public override bool DrawConditions() =>
        Plugin.PlayerState.IsLoaded && // hidden at the title screen/character select - see MainWindow's version
        (!Plugin.Configuration.HideChatDuringCutscenes ||
         !Plugin.Condition.Any(ConditionFlag.WatchingCutscene, ConditionFlag.WatchingCutscene78, ConditionFlag.OccupiedInCutSceneEvent));

    public override void Draw()
    {
        WindowName = $"{Tab.Name}###CustomChatTab_{Tab.Id}";

        if (ImGui.SmallButton("Reattach to main window"))
        {
            plugin.SetTabDetached(Tab, false);
            return;
        }

        // No sidebar/right-click menu to hang a "Search..." item off of in a single-tab floating
        // window (unlike MainWindow's DrawTabContextMenu), so this is a small button instead.
        ImGui.SameLine();
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (ImGui.SmallButton($"{FontAwesomeIcon.Search.ToIconString()}##searchtoggle_{Tab.Id}"))
            {
                searchMode = !searchMode;
                selectionMode = false;
                focusSearchInput = searchMode;
                if (!searchMode)
                    searchQuery = string.Empty;
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Search in this tab");

        if (searchMode)
            DrawSearchBar();

        // Leave room for the input row below (the "jump to bottom" button now lives flush against it,
        // see the input row further down, rather than a separate row of its own) - see MainWindow's
        // own bottomReserve comment for why this uses TightRowSpacing rather than the theme's default
        // ItemSpacing.Y, and GetInputRowReserve() rather than a single frame height now that the input
        // is a multi-line, auto-growing box (see the input row further down).
        var bottomReserve = GetInputRowReserve() + TightRowSpacing;
        using (var child = ImRaii.Child("Messages", new Vector2(0, -bottomReserve), true))
        {
            if (child.Success)
            {
                ImGui.SetWindowFontScale(Plugin.Configuration.FontSize / 14f);
                var messages = plugin.TabMessageBuffer.GetMessages(Tab);

                if (selectionMode)
                {
                    if (transcriptMessageCount != messages.Count)
                    {
                        transcriptText = ChatMessageRenderer.BuildTranscript(messages);
                        transcriptMessageCount = messages.Count;
                    }

                    ImGui.InputTextMultiline($"##transcript_{Tab.Id}", ref transcriptText, transcriptText.Length + 1024, new Vector2(-1, -1), ImGuiInputTextFlags.ReadOnly);
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
                    var lastVisible = ChatMessageRenderer.DrawMessages(Tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, plugin.OpenTellToKey, plugin.SendPartyInvite, plugin.SendFriendRequest, plugin.ViewAdventurerPlate, plugin.OpenMapLink, plugin.ItemTooltipService, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider, searchMode ? searchQuery : null);
                    pendingScrollToDivider = false;

                    if (!searchMode && lastVisible >= 0)
                    {
                        var newUnread = Math.Max(0, messages.Count - 1 - lastVisible);
                        if (newUnread < Tab.UnreadCount)
                        {
                            Tab.UnreadCount = newUnread;
                            plugin.TabManager.Save();
                        }
                    }

                    // Never auto-follow to the bottom on the same frame we just scrolled to the "New
                    // messages" divider - see MainWindow's DrawContent for why. Also skipped while
                    // searching - see MainWindow's DrawContent for that reasoning too.
                    if (!searchMode && !wasScrollingToDivider && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f)
                        ImGui.SetScrollHereY(1f);
                }

                // Captured here (inside the child, the only place its scroll state is queryable) for
                // the "jump to bottom" button drawn later - always visible now, but only clickable
                // while there's actually somewhere below to jump to.
                canScrollToBottom = ImGui.GetScrollY() < ImGui.GetScrollMaxY() - 2f;
            }
        }

        // Tighter than the theme's default ItemSpacing.Y - see bottomReserve above for why the two
        // have to stay in sync. Popped at the end of this method.
        var rowItemSpacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(rowItemSpacing.X, TightRowSpacing));

        var iconSize = ImGui.GetFrameHeight();
        var toolbarSpacing = ImGui.GetStyle().ItemSpacing.X;

        DrawOutgoingChannelLabel();

        // Re-focus has to happen right before the input box (offset 0 = "the very next widget") rather
        // than after, since after now runs through the icon buttons/popup - an unpredictable number of
        // widgets depending on whether the emote popup happens to be open that frame.
        if (refocusInput)
        {
            ImGui.SetKeyboardFocusHere();
            refocusInput = false;
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

        // Multi-line so Shift+Enter can insert an actual line break, Telegram/Discord-style - see
        // MainWindow.DrawInputRow for the full reasoning behind the send-detection logic below.
        // Explicit absolute width (not the usual negative-size trick) so it can double as the wrap
        // width passed to WrapComposeLineIfNeeded - see MainWindow's own version for the full reasoning.
        var boxWidth = ImGui.GetContentRegionAvail().X - (iconSize * 3 + toolbarSpacing);
        var boxSize = new Vector2(boxWidth, GetComposeBoxHeight());
        var wrapWidth = boxWidth - ImGui.GetStyle().FramePadding.X * 2f - 4f;

        ImGui.InputTextMultiline($"##input_{Tab.Id}_{inputGeneration}", ref inputText, 500, boxSize, ImGuiInputTextFlags.CallbackAlways, data =>
        {
            inputSelectionStart = data.SelectionStart;
            inputSelectionEnd = data.SelectionEnd;
            WrapComposeLineIfNeeded(data, wrapWidth);
            return 0;
        });

        var send = false;
        if (ImGui.IsItemFocused() && !ImGui.GetIO().KeyShift && (ImGui.IsKeyPressed(ImGuiKey.Enter, false) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, false)))
        {
            if (inputText.EndsWith('\n'))
                inputText = inputText[..^1];
            send = true;

            // Forces a fresh widget next frame - see MainWindow's version for the full reasoning
            // (needed even when nothing actually gets sent below, e.g. Enter on an empty line).
            inputGeneration++;
            refocusInput = true;
        }

        // Right-click the input box to translate what's typed - the whole text, or just the current
        // selection if there is one.
        if (ImGui.BeginPopupContextItem($"inputctx_{Tab.Id}"))
        {
            DrawInputTranslateMenu();
            ImGui.EndPopup();
        }

        ImGui.SameLine(0, 0);
        bool jumpClicked;
        using (ImRaii.Disabled(!canScrollToBottom))
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            jumpClicked = ImGui.Button($"{FontAwesomeIcon.AngleDoubleDown.ToIconString()}##jumpbottom_{Tab.Id}", new Vector2(iconSize, iconSize));
        if (jumpClicked)
            pendingScrollToBottom = true;

        ImGui.SameLine(0, 0);
        bool selectClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            selectClicked = ImGui.Button($"{FontAwesomeIcon.ICursor.ToIconString()}##selecttoggle_{Tab.Id}", new Vector2(iconSize, iconSize));
        if (selectClicked)
        {
            selectionMode = !selectionMode;
            if (selectionMode)
                searchMode = false;
            transcriptMessageCount = -1;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(selectionMode ? "Back to normal chat view" : "Select & copy text");

        ImGui.SameLine(0, 0);
        bool emoteClicked;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            emoteClicked = ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##emotebtn_{Tab.Id}", new Vector2(iconSize, iconSize));
        if (emoteClicked)
        {
            emoteSearch = string.Empty;
            ImGui.OpenPopup($"EmotePicker_{Tab.Id}");
        }

        EmotePicker.Draw($"EmotePicker_{Tab.Id}", plugin.EmoteService, ref emoteSearch, code =>
        {
            inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + code + " ";
        });

        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(Tab, StripWrapMarkers(inputText));
            inputText = string.Empty;
        }

        ImGui.PopStyleVar();
    }

    /// <summary>Same behaviour as MainWindow.DrawInputTranslateMenu - see there for the reasoning.</summary>
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

    /// <summary>Same behaviour as MainWindow.DrawSearchBar - see there for the reasoning.</summary>
    private void DrawSearchBar()
    {
        var closeSize = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(-(closeSize + ImGui.GetStyle().ItemSpacing.X));

        if (focusSearchInput)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearchInput = false;
        }

        ImGui.InputTextWithHint($"##search_{Tab.Id}", "Search in this tab...", ref searchQuery, 200);

        var escapePressed = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.IsKeyPressed(ImGuiKey.Escape);

        ImGui.SameLine(0, 0);
        var closeClicked = ImGui.Button($"X##searchclose_{Tab.Id}", new Vector2(closeSize, closeSize));

        if (escapePressed || closeClicked)
        {
            searchMode = false;
            searchQuery = string.Empty;
        }

        ImGui.Separator();
    }

    /// <summary>Closing the floating window (X button) reattaches the tab to the main window rather than deleting it.</summary>
    public override void OnClose() => plugin.SetTabDetached(Tab, false);

    public void Dispose()
    {
    }
}
