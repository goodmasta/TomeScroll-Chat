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

    /// <param name="onSendTell">Called with a "Name@World" key when the user picks "Send Tell" from a
    /// sender name's right-click menu. Only offered for messages with a resolvable player sender.</param>
    /// <param name="localPlayerKey">The local character's own "Name@World" (see
    /// <see cref="Plugin.GetLocalPlayerKey"/>), used to show "You" instead of the player's own name.</param>
    public static void DrawMessages(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages, Configuration config, EmoteService emotes, Action<string> onSendTell, string? localPlayerKey)
    {
        for (var i = 0; i < messages.Count; i++)
            DrawMessage(tab, messages[i], i, config, emotes, onSendTell, localPlayerKey);
    }

    private static void DrawMessage(ChatTabConfig tab, ChatMessageRecord msg, int index, Configuration config, EmoteService emotes, Action<string> onSendTell, string? localPlayerKey)
    {
        ImGui.TextDisabled(msg.TimestampUtc.ToLocalTime().ToString("HH:mm"));

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
        // me" signal; every other channel type just compares the resolved sender key directly.
        var isOwn = msg.ChatType == XivChatType.TellOutgoing ||
                    (!string.IsNullOrEmpty(localPlayerKey) && msg.SenderKey == localPlayerKey);

        var sender = isOwn
            ? "You"
            : config.ScreenshotMode && !string.IsNullOrEmpty(msg.SenderName) ? RedactedName : msg.SenderName;

        if (!string.IsNullOrEmpty(sender))
        {
            // Only the nickname gets the per-player colour - the message body below still uses the
            // normal per-channel colour, same as before. Own messages use the local player's own key so
            // "You" gets one consistent colour everywhere, rather than an outgoing tell's target's colour.
            var colorKey = isOwn ? localPlayerKey : msg.SenderKey;
            var senderColor = !string.IsNullOrEmpty(colorKey) ? PlayerColorPalette.GetColor(colorKey) : channelColor;
            ImGui.PushStyleColor(ImGuiCol.Text, senderColor);
            ImGui.TextUnformatted($"{sender}:");
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

            var wrapWidth = rightEdge - ImGui.GetCursorPosX();
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();

            canInline = ImGui.CalcTextSize(text, false, wrapWidth).Y <= ImGui.GetTextLineHeight() * 1.5f;
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
