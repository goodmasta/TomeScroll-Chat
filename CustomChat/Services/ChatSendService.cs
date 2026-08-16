using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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
    private const string FlagPlaceholder = "<flag>";
    private const string LinkPlaceholder = "<link>";

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
    /// <param name="attachments">Item links queued via <see cref="NativeItemLinkWatcher"/> (right-click
    /// an item -> "Link"), consumed in order by each literal "&lt;link&gt;" placeholder found in
    /// <paramref name="message"/> - see <see cref="AppendWithPlaceholderExpansion"/>. A message can be
    /// sent with only a "&lt;link&gt;" and no other typed text at all.</param>
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
        AppendWithPlaceholderExpansion(buffer, full, attachments);

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

    /// <summary>Writes <paramref name="text"/> as UTF-8, expanding two kinds of placeholder along the
    /// way into real payload bytes instead of literal text: "&lt;flag&gt;" (the currently-set map flag,
    /// read fresh from <c>AgentMap.FlagMapMarkers</c> - matches the native chatbox's own auto-expansion
    /// of the same text) and "&lt;link&gt;" (consumed in order from <paramref name="attachments"/>, one
    /// per occurrence - see <see cref="NativeItemLinkWatcher"/> for how those get queued). Both
    /// placeholders are handled in one left-to-right pass so they can appear anywhere in the text,
    /// mixed freely, in any order.
    /// <para>Both expansions matter for the same reason: the native chatbox does its own placeholder
    /// substitution (and its own translation of a linked item into a real payload) as part of its *own*
    /// submit handling, before it ever calls <c>UIModule::ProcessChatBoxEntry</c> - since this plugin
    /// calls that entry point directly, bypassing the native UI entirely, none of that ever runs for
    /// us, so both have to be reimplemented here. The resulting payload bytes are always written
    /// directly to the buffer, never round-tripped through a C# string/UTF8 decode - the raw bytes that
    /// encode a map coordinate or an item id use arbitrary byte values, not just printable UTF-8, and
    /// could be silently corrupted by that trip.</para></summary>
    private void AppendWithPlaceholderExpansion(MemoryStream buffer, string text, IReadOnlyList<PendingItemLink>? attachments)
    {
        var itemIndex = 0;
        var pos = 0;

        while (pos < text.Length)
        {
            var flagIndex = text.IndexOf(FlagPlaceholder, pos, StringComparison.Ordinal);
            var linkIndex = text.IndexOf(LinkPlaceholder, pos, StringComparison.Ordinal);

            var isFlag = flagIndex >= 0 && (linkIndex < 0 || flagIndex <= linkIndex);
            var nextIndex = isFlag ? flagIndex : linkIndex;

            if (nextIndex < 0)
            {
                buffer.Write(Encoding.UTF8.GetBytes(text[pos..]));
                return;
            }

            if (nextIndex > pos)
                buffer.Write(Encoding.UTF8.GetBytes(text[pos..nextIndex]));

            if (isFlag)
            {
                var flagBytes = TryBuildFlagLinkBytes();
                buffer.Write(flagBytes != null ? flagBytes : Encoding.UTF8.GetBytes(FlagPlaceholder));
                pos = nextIndex + FlagPlaceholder.Length;
            }
            else
            {
                if (attachments != null && itemIndex < attachments.Count)
                {
                    var link = attachments[itemIndex++];
                    buffer.Write(new SeStringBuilder().AddItemLink(link.ItemId, link.IsHq, link.DisplayName).Encode());
                }
                else
                {
                    buffer.Write(Encoding.UTF8.GetBytes(LinkPlaceholder));
                }

                pos = nextIndex + LinkPlaceholder.Length;
            }
        }
    }

    private byte[]? TryBuildFlagLinkBytes()
    {
        try
        {
            var agentMap = AgentMap.Instance();
            if (agentMap == null || agentMap->FlagMarkerCount == 0)
                return null;

            var marker = agentMap->FlagMapMarkers[0];
            if (marker.TerritoryId == 0)
                return null;

            return new SeStringBuilder().AddMapLink(marker.TerritoryId, marker.MapId, marker.XFloat, marker.YFloat, 0f).Encode();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to build a map link for the current flag");
            return null;
        }
    }
}
