using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// Sends outgoing text into the game exactly as if it had been typed into the real chatbox and
/// confirmed with Enter, via the same native entry point the game's own UI uses
/// (<c>UIModule::ProcessChatBoxEntry</c>). This means normal game-side validation (channel
/// availability, mute state, slash-command parsing) still applies, and no separate per-channel
/// logic is needed here - the composed string (e.g. "/tell Name@World hi") does all the routing.
/// </summary>
public sealed unsafe class ChatSendService
{
    private const int MaxUtf8Bytes = 500;
    private readonly IPluginLog log;

    public ChatSendService(IPluginLog log)
    {
        this.log = log;
    }

    /// <param name="channelCommand">Outgoing slash-command prefix (e.g. "/p", "/fc", "/tell Name@World"), or empty to use whatever channel the message text/game default resolves to.</param>
    /// <param name="message">Message body, without any channel prefix - unless it's itself a slash
    /// command the player typed (e.g. "/who", "/invite Name"), in which case it's sent completely
    /// as-is: prefixing a tab's channel command in front of an already-explicit command would just
    /// produce an invalid double command (e.g. "/p /invite Name"), which is why typing any command
    /// used to only work from the one tab whose channel command happened to be empty (Log).</param>
    /// <param name="attachments">Item links queued via <see cref="ItemLinkContextMenuService"/> (right-
    /// click an item -> "Link (Custom Chat)"), appended after the typed text in order. These are
    /// encoded straight to bytes via <see cref="SeStringBuilder.AddItemLink"/> and appended to the
    /// outgoing buffer directly - never round-tripped through a C# string/UTF8 decode, since the raw
    /// payload bytes that encode an item id aren't all valid/printable UTF-8 on their own and could be
    /// corrupted by that trip (see <see cref="ItemLinkContextMenuService"/> for the full reasoning).
    /// A message can be sent with only attachments and no typed text at all - e.g. just linking an
    /// item and hitting Enter.</param>
    public void Send(string channelCommand, string message, IReadOnlyList<PendingItemLink>? attachments = null)
    {
        message ??= string.Empty;
        var hasAttachments = attachments is { Count: > 0 };
        if (string.IsNullOrWhiteSpace(message) && !hasAttachments)
            return;

        var isExplicitCommand = IsExplicitCommand(message);
        var full = string.IsNullOrEmpty(channelCommand) || isExplicitCommand
            ? message
            : message.Length > 0 ? $"{channelCommand} {message}" : channelCommand;

        using var buffer = new MemoryStream();
        var textBytes = Encoding.UTF8.GetBytes(full);
        buffer.Write(textBytes);

        if (hasAttachments)
        {
            foreach (var link in attachments!)
            {
                if (buffer.Length > 0)
                    buffer.WriteByte((byte)' ');

                var linkBytes = new SeStringBuilder().AddItemLink(link.ItemId, link.IsHq, link.DisplayName).Encode();
                buffer.Write(linkBytes);
            }
        }

        if (buffer.Length > MaxUtf8Bytes)
        {
            log.Warning("CustomChat: outgoing message is {Bytes} bytes, over the {Max}-byte limit - not sending", buffer.Length, MaxUtf8Bytes);
            return;
        }

        buffer.WriteByte(0); // native string wants a trailing null terminator
        var bytes = buffer.ToArray();

        var utf8 = Utf8String.FromSequence(bytes);
        try
        {
            UIModule.Instance()->ProcessChatBoxEntry(utf8);
        }
        catch (Exception ex)
        {
            log.Error(ex, "CustomChat: failed to send chat message via ProcessChatBoxEntry");
        }
        finally
        {
            utf8->Dtor(true);
        }
    }

    /// <summary>A real slash command starts with "/" immediately followed by a letter (e.g. "/p",
    /// "/invite"). A leading "//" is the game's own escape sequence for a literal slash - not a
    /// command - used to type a message that starts with "/" without it being parsed as one (e.g.
    /// "///" sends a literal "/"). Treating that as an explicit command too would skip the tab's
    /// channel prefix and let the message fall through to whatever channel the game's native chat
    /// state last had active, instead of the tab's configured one.</summary>
    private static bool IsExplicitCommand(string message)
    {
        var trimmed = message.TrimStart();
        return trimmed.Length > 1 && trimmed[0] == '/' && char.IsLetter(trimmed[1]);
    }
}
