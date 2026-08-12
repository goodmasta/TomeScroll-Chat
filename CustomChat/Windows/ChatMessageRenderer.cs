using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Utility;
using CustomChat.Models;
using CustomChat.Services;
using CustomChat.Utility;

namespace CustomChat.Windows;

/// <summary>
/// Draws a tab's message list: per-channel colours, clickable links (opened via <see cref="Util.OpenLink"/>),
/// and inline BTTV/7TV emote images - shared between the main window's tab content and detached tab windows
/// so both render identically.
/// </summary>
public static class ChatMessageRenderer
{
    private static readonly Vector4 LinkColor = new(0.45f, 0.7f, 1f, 1f);
    private static readonly Vector4 FallbackColor = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Vector4 TranslationColor = new(0.65f, 0.8f, 0.65f, 1f);
    private static readonly Vector4 MentionColor = new(1f, 0.85f, 0.2f, 0.35f);
    private const string RedactedName = "Player";

    private static readonly Dictionary<XivChatType, Vector4> DefaultColors = new()
    {
        [XivChatType.Say] = new Vector4(1f, 1f, 1f, 1f),
        [XivChatType.Yell] = new Vector4(1f, 0.85f, 0.3f, 1f),
        [XivChatType.Shout] = new Vector4(1f, 0.6f, 0.3f, 1f),
        [XivChatType.Party] = new Vector4(0.4f, 0.8f, 1f, 1f),
        [XivChatType.Alliance] = new Vector4(1f, 0.5f, 0.5f, 1f),
        [XivChatType.CrossParty] = new Vector4(0.4f, 0.8f, 1f, 1f),
        [XivChatType.FreeCompany] = new Vector4(0.5f, 1f, 0.7f, 1f),
        [XivChatType.NoviceNetwork] = new Vector4(0.6f, 1f, 0.6f, 1f),
        [XivChatType.TellIncoming] = new Vector4(1f, 0.6f, 0.9f, 1f),
        [XivChatType.TellOutgoing] = new Vector4(1f, 0.6f, 0.9f, 1f),
        [XivChatType.CustomEmote] = new Vector4(0.8f, 0.9f, 0.6f, 1f),
        [XivChatType.StandardEmote] = new Vector4(0.8f, 0.9f, 0.6f, 1f),
        [XivChatType.Echo] = new Vector4(0.7f, 0.7f, 0.9f, 1f),
        [XivChatType.ErrorMessage] = new Vector4(1f, 0.35f, 0.35f, 1f),
        [XivChatType.SystemError] = new Vector4(1f, 0.35f, 0.35f, 1f),
        [XivChatType.SystemMessage] = new Vector4(0.75f, 0.75f, 0.75f, 1f),
    };

    private static readonly Vector4 DividerColor = new(1f, 0.35f, 0.35f, 1f);

    /// <param name="onSendTell">Called with a "Name@World" key when the user picks "Send Tell" from a
    /// sender name's right-click menu. Only offered for messages with a resolvable player sender.</param>
    /// <param name="onPartyInvite">Called with a "Name@World" key for "Send Party Invite" - same
    /// availability as <paramref name="onSendTell"/>.</param>
    /// <param name="onFriendRequest">Called with a "Name@World" key for "Send Friend Request" - same
    /// availability as <paramref name="onSendTell"/>.</param>
    /// <param name="localPlayerKey">The local character's own "Name@World" (see
    /// <see cref="Plugin.GetLocalPlayerKey"/>), used to show "You" instead of the player's own name.</param>
    /// <param name="isFriend">Whether a "Name@World" key is on the friends list, for the marker prefix.</param>
    /// <param name="dividerIndex">Index to draw a Discord-style "New messages" divider before, or -1 for none.</param>
    /// <param name="scrollToDivider">Scrolls the divider into view once, the frame it's drawn (tab just opened).</param>
    /// <param name="searchQuery">When non-empty, only messages whose body or sender name contain this
    /// (case-insensitive) are drawn at all - the Ctrl+F "search in this tab" filter. The "New messages"
    /// divider is suppressed while searching, since its index no longer lines up with what's shown.</param>
    /// <returns>The highest message index that was actually scrolled into view this frame, or -1 if
    /// none were (used by the caller to shrink the tab's unread count as the player reads down).</returns>
    public static int DrawMessages(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages, Configuration config, EmoteService emotes, TranslationService translation, Action<string> onSendTell, Action<string> onPartyInvite, Action<string> onFriendRequest, string? localPlayerKey, Func<string, bool> isFriend, int dividerIndex, bool scrollToDivider, string? searchQuery = null)
    {
        var lastVisible = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.IsNullOrEmpty(searchQuery) && !MatchesSearch(messages[i], searchQuery))
                continue;

            if (string.IsNullOrEmpty(searchQuery) && i == dividerIndex && dividerIndex > 0)
            {
                DrawDivider();
                if (scrollToDivider)
                    ImGui.SetScrollHereY(0.1f);
            }

            if (DrawMessage(tab, messages[i], i, config, emotes, translation, onSendTell, onPartyInvite, onFriendRequest, localPlayerKey, isFriend))
                lastVisible = i;
        }

        return lastVisible;
    }

    private static bool MatchesSearch(ChatMessageRecord msg, string query) =>
        msg.Body.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        msg.SenderName.Contains(query, StringComparison.OrdinalIgnoreCase);

    private const string DividerLabel = "New messages";

    private static void DrawDivider()
    {
        ImGui.Spacing();
        ImGui.Separator();

        var textWidth = ImGui.CalcTextSize(DividerLabel).X;
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > textWidth)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - textWidth) / 2f);

        ImGui.PushStyleColor(ImGuiCol.Text, DividerColor);
        ImGui.TextUnformatted(DividerLabel);
        ImGui.PopStyleColor();

        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// Draws one message row. The whole row (timestamp through the last wrapped line of the body,
    /// full window width) highlights on hover and owns a single unified right-click menu - drawn via
    /// <see cref="ImDrawList.ChannelsSplit"/> so the highlight rectangle can be painted *behind* the
    /// already-drawn text/images: the row's true height isn't known until after everything inside it
    /// (which wraps dynamically) has actually been drawn, so the background can't be sized up front.
    /// Returns whether it was scrolled into view this frame.
    /// </summary>
    private static bool DrawMessage(ChatTabConfig tab, ChatMessageRecord msg, int index, Configuration config, EmoteService emotes, TranslationService translation, Action<string> onSendTell, Action<string> onPartyInvite, Action<string> onFriendRequest, string? localPlayerKey, Func<string, bool> isFriend)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1); // foreground: the real content, drawn first so its rect is known

        ImGui.BeginGroup();

        ImGui.TextDisabled(msg.TimestampUtc.ToLocalTime().ToString("HH:mm"));
        var isVisible = ImGui.IsItemVisible();

        ImGui.SameLine(0, 4);

        var channelColor = GetColor(tab, msg.ChatType);

        // Outgoing tells are authored by the local player but their Sender field carries the *target's*
        // payload (e.g. "To Name"), not the player's own - so TellOutgoing is its own reliable "this is
        // me" signal. For every other channel, the game apparently doesn't embed a clickable
        // PlayerPayload for the *local* player's own name the way it does for other players (there's
        // nothing to click on yourself), so SenderKey often comes back empty for your own messages -
        // falling back to a plain-name comparison against the raw sender text catches those too.
        var localPlayerName = localPlayerKey?.Split('@')[0];
        var isOwn = msg.ChatType == XivChatType.TellOutgoing ||
                    (!string.IsNullOrEmpty(localPlayerKey) && msg.SenderKey == localPlayerKey) ||
                    (!string.IsNullOrEmpty(localPlayerName) && msg.SenderName == localPlayerName);

        var sender = isOwn
            ? "You"
            : config.ScreenshotMode && !string.IsNullOrEmpty(msg.SenderName) ? RedactedName : msg.SenderName;

        // Hoisted out of the sender-name block below so the context menu's header (see the popup
        // further down) can reuse the exact same colour the name is actually drawn in, without
        // recomputing the colour-key logic a second time.
        var senderColor = channelColor;

        if (!string.IsNullOrEmpty(sender))
        {
            var showMarker = !isOwn && config.FriendMarkerEnabled && !string.IsNullOrEmpty(config.FriendMarkerEmoji) &&
                              !string.IsNullOrEmpty(msg.SenderKey) && isFriend(msg.SenderKey);
            if (showMarker)
            {
                // A real emote image, not a Unicode glyph - see Configuration.FriendMarkerEmoji for why.
                var markerSize = ImGui.GetTextLineHeight();
                var markerTexture = emotes.TryGetTexture(config.FriendMarkerEmoji);
                if (markerTexture != null)
                    ImGui.Image(markerTexture.Handle, new Vector2(markerSize, markerSize));
                else
                    ImGui.Dummy(new Vector2(markerSize, markerSize)); // still loading - keep the slot reserved rather than jitter

                ImGui.SameLine(0, 3);
            }

            // Only the nickname gets the per-player colour - the message body below still uses the
            // normal per-channel colour, same as before. Own messages use the local player's own key so
            // "You" gets one consistent colour everywhere, rather than an outgoing tell's target's colour.
            var colorKey = isOwn ? localPlayerKey : msg.SenderKey;
            senderColor = !string.IsNullOrEmpty(colorKey) ? PlayerColorPalette.GetColor(colorKey) : channelColor;
            ImGui.PushStyleColor(ImGuiCol.Text, senderColor);
            ImGui.TextUnformatted($"{sender}:");
            ImGui.PopStyleColor();

            ImGui.SameLine(0, 4);
        }

        // Hanging indent: any wrapped/fresh line inside DrawBody (wrapped plain text, or a link that
        // always starts its own line) should line up under where the message text starts, not under
        // the timestamp at the far left.
        var indentWidth = ImGui.GetCursorPosX() - ImGui.GetWindowContentRegionMin().X;
        ImGui.Indent(indentWidth);

        ImGui.PushStyleColor(ImGuiCol.Text, channelColor);
        DrawBody(msg.Body, config, emotes);
        ImGui.PopStyleColor();

        // Drawn under the same hanging indent as the body above it, on its own line - "Translate"
        // (see the context menu below) fetches this lazily, so most messages never pay for it at all.
        var translatedText = translation.TryGetTranslation(msg);
        if (translatedText != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, TranslationColor);
            ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X);
            // "Retranslate" re-fetches without clearing the old text first, so the previous
            // translation stays visible (rather than briefly disappearing) while the new one loads -
            // the "..." is the only sign a refresh is in progress.
            ImGui.TextUnformatted(translation.IsTranslating(msg) ? $"→ {translatedText} ..." : $"→ {translatedText}");
            ImGui.PopTextWrapPos();
            ImGui.PopStyleColor();
        }
        else if (translation.IsTranslating(msg))
        {
            ImGui.TextDisabled("Translating...");
        }

        ImGui.Unindent(indentWidth);

        ImGui.EndGroup();

        // Full window width, not just the content's own (often narrower) bounding box - a hover
        // highlight that stopped at the last glyph would look like a text selection box, not a row.
        var rowMin = ImGui.GetItemRectMin();
        var rowMax = new Vector2(ImGui.GetWindowContentRegionMax().X + ImGui.GetWindowPos().X, ImGui.GetItemRectMax().Y);

        var popupId = $"msgctx_{index}";
        var rawHovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(rowMin, rowMax);
        // Keeps the row highlighted while its own menu is open, even after the mouse has moved onto
        // the popup itself (which un-hovers the "Messages" child window underneath it).
        var highlightActive = rawHovered || ImGui.IsPopupOpen(popupId);

        // Persistent tint (not just on hover) for a message that name-drops the local player - FFXIV
        // chat has no @mention system, so this is a plain-text match against the player's own name
        // (whole name or either half of it, so "hey Firstname" or "nice one Lastname" both count too).
        // Deliberately not excluding the player's own messages - the request was "highlight messages
        // containing my name", with no carve-out for who sent them.
        var isMention = !string.IsNullOrEmpty(localPlayerName) && ContainsMention(msg.Body, localPlayerName);

        drawList.ChannelsSetCurrent(0); // background: painted after the content, but rendered behind it
        if (isMention)
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(MentionColor));
        if (highlightActive)
            drawList.AddRectFilled(rowMin, rowMax, ImGui.GetColorU32(ImGuiCol.HeaderHovered, 0.4f));
        drawList.ChannelsMerge();

        if (rawHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            ImGui.OpenPopup(popupId);

        if (ImGui.BeginPopup(popupId))
        {
            // Non-interactive header naming who this menu's message is from - "You" (not the sender's
            // real name) when it's the local player's own message, same as the row itself shows,
            // including for outgoing tells where msg.SenderName is actually the *recipient's* name.
            if (!string.IsNullOrEmpty(sender))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, senderColor);
                ImGui.TextUnformatted(sender);
                ImGui.PopStyleColor();
                ImGui.Separator();
            }

            if (ImGui.MenuItem("Copy message"))
                ImGui.SetClipboardText(BuildCopyText(msg));

            if (!string.IsNullOrWhiteSpace(msg.Body))
            {
                if (translatedText != null)
                {
                    if (ImGui.MenuItem("Retranslate"))
                        translation.ForceRetranslate(msg, config.TranslateTargetLanguage);

                    if (ImGui.MenuItem("Hide translation"))
                        translation.ClearTranslation(msg);
                }
                else if (ImGui.MenuItem("Translate"))
                {
                    translation.RequestTranslate(msg, config.TranslateTargetLanguage);
                }
            }

            if (!string.IsNullOrEmpty(msg.SenderName) && ImGui.MenuItem("Copy nickname"))
                ImGui.SetClipboardText(msg.SenderName);

            // Whispering yourself makes no sense, and the game's own "Send Tell" menu item only works
            // from native UI (party list, target, etc.) and does nothing here since this window isn't
            // a native addon - this is the only way to whisper someone straight from chat.
            if (!isOwn && !string.IsNullOrEmpty(msg.SenderKey) && ImGui.MenuItem("Send Tell"))
                onSendTell(msg.SenderKey);

            // Works by name+world (like the vanilla `/invite` command) with no need to target/see the
            // player.
            if (!isOwn && !string.IsNullOrEmpty(msg.SenderKey) && ImGui.MenuItem("Send Party Invite"))
                onPartyInvite(msg.SenderKey);

            // Goes through "/friendlist add" as a plain text command, not a dedicated native call -
            // see Plugin.SendFriendRequest for why.
            if (!isOwn && !string.IsNullOrEmpty(msg.SenderKey) && ImGui.MenuItem("Send Friend Request"))
                onFriendRequest(msg.SenderKey);

            var links = LinkDetector.Split(msg.Body).Where(s => s.IsLink).Select(s => s.Slice(msg.Body)).Distinct().ToList();
            if (links.Count == 1)
            {
                if (ImGui.MenuItem("Copy link"))
                    ImGui.SetClipboardText(LinkDetector.NormalizeForBrowser(links[0]));
            }
            else if (links.Count > 1 && ImGui.BeginMenu("Copy link"))
            {
                foreach (var link in links)
                {
                    if (ImGui.MenuItem(link))
                        ImGui.SetClipboardText(LinkDetector.NormalizeForBrowser(link));
                }

                ImGui.EndMenu();
            }

            ImGui.EndPopup();
        }

        return isVisible;
    }

    /// <summary>Whether <paramref name="body"/> name-drops <paramref name="localPlayerName"/> - the
    /// full "First Last" name, or either half of it on its own (so a message just saying "Firstname"
    /// or just "Lastname" still counts as a mention, not only the exact full name).</summary>
    private static bool ContainsMention(string body, string localPlayerName)
    {
        if (string.IsNullOrEmpty(body))
            return false;

        if (body.Contains(localPlayerName, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var part in localPlayerName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2 && body.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildCopyText(ChatMessageRecord msg)
    {
        var time = msg.TimestampUtc.ToLocalTime().ToString("HH:mm");
        return string.IsNullOrEmpty(msg.SenderName)
            ? $"[{time}] {msg.Body}"
            : $"[{time}] {msg.SenderName}: {msg.Body}";
    }

    /// <summary>Plain-text version of a tab's whole message list, one line each - used by the
    /// "select text" toggle (see MainWindow/DetachedTabWindow), which swaps the normal rich rendering
    /// (custom per-token widgets, no click-drag text selection possible across them) for a read-only
    /// <c>ImGui.InputTextMultiline</c> over this string, which gets native mouse-drag selection and
    /// Ctrl+C for free.</summary>
    public static string BuildTranscript(IReadOnlyList<ChatMessageRecord> messages) =>
        string.Join('\n', messages.Select(BuildCopyText));

    /// <summary>
    /// Plain runs of text (no link, no known emote code) are batched and drawn with a single
    /// <see cref="ImGui.TextUnformatted"/> call under <see cref="ImGui.PushTextWrapPos"/> - i.e. real
    /// word-wrap, delegated to ImGui's own tested implementation instead of reimplemented by hand.
    /// Only links and emotes (which need their own clickable/image widget) fall back to manual,
    /// single-token placement.
    /// </summary>
    private static void DrawBody(string body, Configuration config, EmoteService emotes)
    {
        var rightEdge = ImGui.GetWindowContentRegionMax().X;
        var plain = new StringBuilder();

        // Whether the *previous* thing drawn ended on a single line, so ImGui.SameLine() after it can
        // be trusted. Turns out ImGui.SameLine()'s continuation point after a WRAPPED multi-line
        // TextUnformitted is based on that block's *widest* line, not its actual last line - so
        // calling SameLine() right after a paragraph that wrapped can place the next token far closer
        // to the right edge than it visually looks like it should be, which is what caused both the
        // "links don't wrap" and the "wall of blank lines" bugs seen earlier. When the previous run
        // fit on one line this isn't an issue at all, so only multi-line runs need to skip inlining.
        var canInline = true;

        void FlushPlain()
        {
            if (plain.Length == 0)
                return;

            var text = plain.ToString();
            plain.Clear();

            var spacing = ImGui.GetStyle().ItemSpacing.X;
            if (canInline)
            {
                // Checking only the *first word*'s width here (the original check) is wrong for a
                // multi-word run: fitting the first word doesn't mean there's comfortable room for
                // the rest, and ImGui's word-wrap keeps every wrapped line of a Text widget pinned to
                // that widget's own starting X - so inlining a multi-word run into a narrow leftover
                // gap near the right edge turns every subsequent wrapped line into a ~1-character-wide
                // column running down the right edge instead of a normal paragraph (seen with a run
                // right after two links in the same message left little room on that line). Checking
                // the *whole* run's unwrapped width instead means it only ever inlines when it can sit
                // on the current line without wrapping at all - if it doesn't fit, it starts fresh
                // with the full window width to wrap into, same as any other wrapped paragraph.
                var prevRightX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
                if (prevRightX + spacing + ImGui.CalcTextSize(text).X <= rightEdge)
                    ImGui.SameLine(0, spacing);
            }

            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();

            // Use the *actual* rendered height of the widget we just drew rather than a separately
            // predicted one (previously via CalcTextSize with a manually-computed wrap width) - that
            // prediction could disagree with the real wrap boundary PushTextWrapPos(0f) used
            // internally, which meant canInline could come out wrong (multi-line text misreported as
            // single-line), letting the next token inline off the trailing edge of the widest wrapped
            // line instead of the true last line. Real geometry can't disagree with itself.
            var renderedHeight = ImGui.GetItemRectMax().Y - ImGui.GetItemRectMin().Y;
            canInline = renderedHeight <= ImGui.GetTextLineHeight() * 1.5f;
        }

        foreach (var span in LinkDetector.Split(body))
        {
            var text = span.Slice(body);
            if (span.IsLink)
            {
                FlushPlain();
                canInline = DrawLink(text, config, rightEdge, canInline);
                continue;
            }

            foreach (var word in text.Split(' '))
            {
                if (word.Length == 0)
                    continue;

                if (emotes.IsKnownEmote(word))
                {
                    FlushPlain();
                    DrawEmote(word, config, emotes, rightEdge, canInline);
                    canInline = true; // a single small image/fallback token, never wraps
                }
                else
                {
                    if (plain.Length > 0)
                        plain.Append(' ');
                    plain.Append(word);
                }
            }
        }

        FlushPlain();
    }

    /// <summary>Draws one emote token, inlining after the previous item only when that's known-safe.</summary>
    private static void DrawEmote(string token, Configuration config, EmoteService emotes, float rightEdge, bool canInline)
    {
        var texture = emotes.TryGetTexture(token);
        var lineHeight = ImGui.GetTextLineHeight() * config.EmoteScale;
        var size = new Vector2(lineHeight, lineHeight);

        if (canInline)
        {
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var prevRightX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
            if (prevRightX + spacing + size.X <= rightEdge)
                ImGui.SameLine(0, spacing);
        }

        if (texture != null)
            ImGui.Image(texture.Handle, size);
        else
            ImGui.TextUnformatted(token);
    }

    /// <summary>
    /// Draws one link, inlining after the previous item only when <paramref name="canInline"/> says
    /// that's safe (see <see cref="DrawBody"/>). Returns whether this link itself ended up wrapped to
    /// multiple lines, so the caller knows whether inlining after *it* is safe in turn.
    /// </summary>
    private static bool DrawLink(string token, Configuration config, float rightEdge, bool canInline)
    {
        if (!config.OpenLinksOnClick)
        {
            ImGui.TextUnformatted(token);
            return true;
        }

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var tokenWidth = ImGui.CalcTextSize(token).X;
        if (canInline)
        {
            var prevRightX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
            if (prevRightX + spacing + tokenWidth <= rightEdge)
                ImGui.SameLine(0, spacing);
        }

        var fullWidth = rightEdge - ImGui.GetCursorPosX();
        var needsWrap = tokenWidth > fullWidth;
        if (needsWrap)
        {
            ImGui.PushTextWrapPos(rightEdge);
            ImGui.TextColored(LinkColor, token);
            ImGui.PopTextWrapPos();
        }
        else
        {
            ImGui.TextColored(LinkColor, token);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            Util.OpenLink(LinkDetector.NormalizeForBrowser(token));

        return !needsWrap;
    }

    private static Vector4 GetColor(ChatTabConfig tab, XivChatType chatType)
    {
        if (tab.ColorOverrides.TryGetValue(chatType, out var packed))
            return ImGui.ColorConvertU32ToFloat4(packed);

        return GetDefaultColor(chatType);
    }

    /// <summary>The colour a channel renders with when a tab has no <see cref="ChatTabConfig.ColorOverrides"/>
    /// entry for it - used by <see cref="Windows.ConfigWindow"/> to pre-fill the per-tab colour pickers.</summary>
    public static Vector4 GetDefaultColor(XivChatType chatType) => DefaultColors.GetValueOrDefault(chatType, FallbackColor);
}
