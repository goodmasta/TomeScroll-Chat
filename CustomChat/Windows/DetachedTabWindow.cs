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
    private string? pendingPrefillText;

    /// <summary>Same wrap-newline position tracking as MainWindow - see its field comment for the full
    /// reasoning (an earlier "invisible" Unicode marker character approach turned out not to actually
    /// be invisible in this game's font).</summary>
    private readonly List<(int Position, bool IsSoftBreak)> wrapNewlines = new();
    private byte[] lastWrapSnapshot = Array.Empty<byte>();

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
    /// MainWindow's version for the full reasoning, including why this uses GetTextLineHeight() +
    /// TightRowSpacing rather than GetTextLineHeightWithSpacing() (which bakes in the theme's default
    /// ItemSpacing.Y, not the tightened spacing actually pushed around the input row further down).</summary>
    private float GetInputRowReserve() => GetComposeBoxHeight() + ImGui.GetTextLineHeight() + TightRowSpacing;

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning.</summary>
    private void DrawOutgoingChannelLabel()
    {
        var target = string.IsNullOrEmpty(Tab.OutgoingChannelCommand)
            ? "current in-game chat channel"
            : Tab.OutgoingChannelCommand;
        ImGui.TextDisabled($"Sending to: {target}");
    }

    /// <summary>Same reconciliation as MainWindow's version - see its doc comment for the full
    /// reasoning.</summary>
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
            return;

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

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning.</summary>
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

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning.</summary>
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

    /// <summary>Same manual word-wrap simulation as MainWindow - see its version for the full
    /// reasoning (no built-in word-wrap in Dear ImGui's InputTextMultiline, byte-vs-character offset
    /// care for multi-byte UTF-8 text, and why every inserted newline's position is tracked in
    /// <see cref="wrapNewlines"/> instead of embedding a marker character in the text itself).</summary>
    private void WrapComposeLineIfNeeded(ImGuiInputTextCallbackDataPtr data, float wrapWidth)
    {
        RemoveJustInsertedEnterNewline(data);
        ReconcileWrapNewlines(data.BufTextSpan);
        DoWrapCheck(data, wrapWidth);
        lastWrapSnapshot = data.BufTextSpan.ToArray();
    }

    /// <summary>Same as MainWindow's version - see its doc comment for the full reasoning (nothing to
    /// do with <see cref="wrapNewlines"/> at all, despite the visible symptom looking just like a
    /// wrap-corruption bug).</summary>
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
            data.InsertChars(breakByteOffset, "\n");
            wrapNewlines.Add((breakByteOffset, false));
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

    /// <summary>Appends text to the compose box and queues a refocus - the "Reply" context-menu
    /// action's target on this window specifically (unlike MainWindow.PrefillInput, nothing here also
    /// needs to bring a whole separate window to front, since the player is already looking at this
    /// exact window when they right-click a message in it). Deferred by a frame the same way
    /// MainWindow's version is (see its own doc comment) - giving focus and already-non-empty text on
    /// the same frame makes ImGui select the whole buffer, so the very next keystroke would overwrite
    /// the reply target instead of continuing after it.</summary>
    public void PrefillInput(string text)
    {
        pendingPrefillText = text;
        refocusInput = true;
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
                    var lastVisible = ChatMessageRenderer.DrawMessages(Tab, messages, Plugin.Configuration, plugin.EmoteService, plugin.TranslationService, PrefillInput, plugin.OpenTellToKey, plugin.SendPartyInvite, plugin.SendFriendRequest, plugin.ViewAdventurerPlate, plugin.OpenMapLink, plugin.ItemTooltipService, plugin.ItemContextService, Plugin.GetLocalPlayerKey(), plugin.FriendListService.IsFriendKey, dividerIndex, pendingScrollToDivider, searchMode ? searchQuery : null);
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
        else if (pendingPrefillText != null)
        {
            // Deliberately one frame after the focus request above - see PrefillInput's own doc
            // comment. Appended, not replacing outright, same as MainWindow's version.
            if (inputText.Length > 0 && !char.IsWhiteSpace(inputText[^1]))
                inputText += " ";
            inputText += pendingPrefillText;
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
        // Same column as the emote button below, split into two stacked half-height buttons rather
        // than a 4th column - see MainWindow's mirror of this for why (keeps this icon group's total
        // height exactly iconSize, so the surrounding bottomReserve alignment math needs no changes).
        using (var quickInsertGroup = ImRaii.Group())
        {
            var topHeight = MathF.Max(1f, (iconSize - TightRowSpacing) / 2f);
            var bottomHeight = iconSize - TightRowSpacing - topHeight;

            var quickPosClicked = ImGui.Button($"P##quickpos_{Tab.Id}", new Vector2(iconSize, topHeight));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Insert <pos> - the game expands this to your current position when sent, same as typing it into the native chatbox.");
            if (quickPosClicked)
                inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + "<pos>";

            bool emoteClicked;
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                emoteClicked = ImGui.Button($"{FontAwesomeIcon.Smile.ToIconString()}##emotebtn_{Tab.Id}", new Vector2(iconSize, bottomHeight));
            if (emoteClicked)
            {
                emoteSearch = string.Empty;
                ImGui.OpenPopup($"EmotePicker_{Tab.Id}");
            }
        }

        EmotePicker.Draw($"EmotePicker_{Tab.Id}", plugin.EmoteService, ref emoteSearch, code =>
        {
            inputText += (inputText.Length > 0 && !inputText.EndsWith(' ') ? " " : string.Empty) + code + " ";
        });

        if (send && !string.IsNullOrWhiteSpace(inputText))
        {
            plugin.SendFromTab(Tab, StripWrapNewlines(inputText));
            inputText = string.Empty;
            wrapNewlines.Clear();
            lastWrapSnapshot = Array.Empty<byte>();
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
