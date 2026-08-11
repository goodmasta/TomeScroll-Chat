using System;
using System.Collections.Generic;
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
    /// <param name="localPlayerKey">The local character's own "Name@World" (see
    /// <see cref="Plugin.GetLocalPlayerKey"/>), used to show "You" instead of the player's own name.</param>
    /// <param name="isFriend">Whether a "Name@World" key is on the friends list, for the marker prefix.</param>
    /// <param name="dividerIndex">Index to draw a Discord-style "New messages" divider before, or -1 for none.</param>
    /// <param name="scrollToDivider">Scrolls the divider into view once, the frame it's drawn (tab just opened).</param>
    /// <returns>The highest message index that was actually scrolled into view this frame, or -1 if
    /// none were (used by the caller to shrink the tab's unread count as the player reads down).</returns>
    public static int DrawMessages(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages, Configuration config, EmoteService emotes, Action<string> onSendTell, string? localPlayerKey, Func<string, bool> isFriend, int dividerIndex, bool scrollToDivider)
    {
        var lastVisible = -1;
        for (var i = 0; i < messages.Count; i++)
        {
            if (i == dividerIndex && dividerIndex > 0)
            {
                DrawDivider();
                if (scrollToDivider)
                    ImGui.SetScrollHereY(0.1f);
            }

            if (DrawMessage(tab, messages[i], i, config, emotes, onSendTell, localPlayerKey, isFriend))
                lastVisible = i;
        }

        return lastVisible;
    }

    private static void DrawDivider()
    {
        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, DividerColor);
        ImGui.Separator();
        ImGui.TextUnformatted("New messages");
        ImGui.Separator();
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    /// <summary>Draws one message row. Returns whether it was scrolled into view this frame.</summary>
    private static bool DrawMessage(ChatTabConfig tab, ChatMessageRecord msg, int index, Configuration config, EmoteService emotes, Action<string> onSendTell, string? localPlayerKey, Func<string, bool> isFriend)
    {
        ImGui.TextDisabled(msg.TimestampUtc.ToLocalTime().ToString("HH:mm"));
        var isVisible = ImGui.IsItemVisible();

        // Right-click the timestamp to copy the whole message ("[HH:mm] Sender: body") - always
        // available, even for system/echo lines that have no sender to right-click.
        if (ImGui.BeginPopupContextItem($"msgctx_{index}"))
        {
            if (ImGui.MenuItem("Copy message"))
                ImGui.SetClipboardText(BuildCopyText(msg));
            ImGui.EndPopup();
        }

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

        if (!string.IsNullOrEmpty(sender))
        {
            var marker = !isOwn && !string.IsNullOrEmpty(config.FriendMarkerEmoji) &&
                         !string.IsNullOrEmpty(msg.SenderKey) && isFriend(msg.SenderKey)
                ? config.FriendMarkerEmoji + " "
                : string.Empty;

            // Only the nickname gets the per-player colour - the message body below still uses the
            // normal per-channel colour, same as before. Own messages use the local player's own key so
            // "You" gets one consistent colour everywhere, rather than an outgoing tell's target's colour.
            var colorKey = isOwn ? localPlayerKey : msg.SenderKey;
            var senderColor = !string.IsNullOrEmpty(colorKey) ? PlayerColorPalette.GetColor(colorKey) : channelColor;
            ImGui.PushStyleColor(ImGuiCol.Text, senderColor);
            ImGui.TextUnformatted($"{marker}{sender}:");
            ImGui.PopStyleColor();

            // Right-click a sender name to whisper them - the game's own "Send Tell" menu item only
            // works from native UI (party list, target, etc.) and does nothing here since this window
            // isn't a native addon, so this is the only way to whisper someone straight from chat.
            if (!string.IsNullOrEmpty(msg.SenderKey) && ImGui.BeginPopupContextItem($"senderctx_{index}"))
            {
                if (ImGui.MenuItem("Send Tell"))
                    onSendTell(msg.SenderKey);
                ImGui.Separator();
                if (ImGui.MenuItem("Copy message"))
                    ImGui.SetClipboardText(BuildCopyText(msg));
                ImGui.EndPopup();
            }

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

        ImGui.Unindent(indentWidth);

        return isVisible;
    }

    private static string BuildCopyText(ChatMessageRecord msg)
    {
        var time = msg.TimestampUtc.ToLocalTime().ToString("HH:mm");
        return string.IsNullOrEmpty(msg.SenderName)
            ? $"[{time}] {msg.Body}"
            : $"[{time}] {msg.SenderName}: {msg.Body}";
    }

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
                var prevRightX = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X;
                var firstWordEnd = text.IndexOf(' ');
                var firstWord = firstWordEnd < 0 ? text : text[..firstWordEnd];
                if (prevRightX + spacing + ImGui.CalcTextSize(firstWord).X <= rightEdge)
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

        return DefaultColors.GetValueOrDefault(chatType, FallbackColor);
    }
}
