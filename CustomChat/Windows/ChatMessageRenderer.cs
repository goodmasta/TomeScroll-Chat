using System;
using System.Collections.Generic;
using System.Numerics;
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
    public static void DrawMessages(ChatTabConfig tab, IReadOnlyList<ChatMessageRecord> messages, Configuration config, EmoteService emotes, Action<string> onSendTell)
    {
        for (var i = 0; i < messages.Count; i++)
            DrawMessage(tab, messages[i], i, config, emotes, onSendTell);
    }

    private static void DrawMessage(ChatTabConfig tab, ChatMessageRecord msg, int index, Configuration config, EmoteService emotes, Action<string> onSendTell)
    {
        ImGui.TextDisabled(msg.TimestampUtc.ToLocalTime().ToString("HH:mm"));
        ImGui.SameLine(0, 4);

        var color = GetColor(tab, msg.ChatType);
        ImGui.PushStyleColor(ImGuiCol.Text, color);

        var sender = config.ScreenshotMode && !string.IsNullOrEmpty(msg.SenderName) ? RedactedName : msg.SenderName;
        if (!string.IsNullOrEmpty(sender))
        {
            ImGui.TextUnformatted($"{sender}:");

            // Right-click a sender name to whisper them - the game's own "Send Tell" menu item only
            // works from native UI (party list, target, etc.) and does nothing here since this window
            // isn't a native addon, so this is the only way to whisper someone straight from chat.
            if (!string.IsNullOrEmpty(msg.SenderKey) && ImGui.BeginPopupContextItem($"senderctx_{index}"))
            {
                if (ImGui.MenuItem("Send Tell"))
                    onSendTell(msg.SenderKey);
                ImGui.EndPopup();
            }

            ImGui.SameLine(0, 4);
        }

        DrawBody(msg.Body, config, emotes);
        ImGui.PopStyleColor();
    }

    private static void DrawBody(string body, Configuration config, EmoteService emotes)
    {
        foreach (var span in LinkDetector.Split(body))
        {
            var text = span.Slice(body);
            if (span.IsLink)
            {
                DrawToken(text, isLink: true, config, emotes);
                continue;
            }

            foreach (var word in text.Split(' '))
            {
                if (word.Length > 0)
                    DrawToken(word, isLink: false, config, emotes);
            }
        }
    }

    private static void DrawToken(string token, bool isLink, Configuration config, EmoteService emotes)
    {
        var isEmote = !isLink && emotes.IsKnownEmote(token);
        var texture = isEmote ? emotes.TryGetTexture(token) : null;
        var lineHeight = ImGui.GetTextLineHeight() * config.EmoteScale;
        var size = isEmote ? new Vector2(lineHeight, lineHeight) : ImGui.CalcTextSize(token);

        // Continue the current line if this token fits; otherwise let it fall to a new one naturally
        // (ImGui starts a fresh line for any widget that doesn't follow a SameLine() call).
        if (size.X <= ImGui.GetContentRegionAvail().X)
            ImGui.SameLine(0, ImGui.GetStyle().ItemSpacing.X);

        if (isEmote)
        {
            if (texture != null)
                ImGui.Image(texture.Handle, size);
            else
                ImGui.TextUnformatted(token);
        }
        else if (isLink && config.OpenLinksOnClick)
        {
            ImGui.TextColored(LinkColor, token);
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                Util.OpenLink(LinkDetector.NormalizeForBrowser(token));
        }
        else
        {
            ImGui.TextUnformatted(token);
        }
    }

    private static Vector4 GetColor(ChatTabConfig tab, XivChatType chatType)
    {
        if (tab.ColorOverrides.TryGetValue(chatType, out var packed))
            return ImGui.ColorConvertU32ToFloat4(packed);

        return DefaultColors.GetValueOrDefault(chatType, FallbackColor);
    }
}
