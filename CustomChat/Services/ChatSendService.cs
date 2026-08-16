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
    /// used to only work from the one tab whose channel command happened to be empty (Log). A literal
    /// "&lt;flag&gt;" is sent as-is, plain text, same as any other placeholder this plugin doesn't
    /// specifically recognize (e.g. "&lt;pos&gt;") - see <see cref="AppendWithPlaceholderExpansion"/>'s
    /// doc comment for why an earlier attempt at expanding it into a real map link was reverted.</param>
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

    /// <summary>Writes <paramref name="text"/> as UTF-8, expanding literal "&lt;link&gt;" placeholders
    /// into real item link payload bytes, consumed in order from <paramref name="attachments"/> - see
    /// <see cref="NativeItemLinkWatcher"/> for how those get queued. The payload bytes are written
    /// directly to the buffer, never round-tripped through a C# string/UTF8 decode, since the raw bytes
    /// that encode an item id use arbitrary byte values, not just printable UTF-8, and could be
    /// silently corrupted by that trip.
    /// <para>An earlier version also expanded "&lt;flag&gt;" into a live-built map link the same way
    /// (the native chatbox does this same substitution itself, but only as part of its own submit
    /// handling *before* it ever calls <c>UIModule::ProcessChatBoxEntry</c> - since this plugin calls
    /// that entry point directly, bypassing the native UI, that expansion never ran for us either way).
    /// **Reverted** (2026-08-13): sending "&lt;flag&gt;" through that path made the whole message vanish
    /// - no error, nothing sent, nothing echoed back - while plain literal text (e.g. "&lt;pos&gt;",
    /// which isn't recognized at all) sent and displayed correctly every time. The exact cause was never
    /// pinned down (never got a diagnostic build in front of the failure - the log lines that would have
    /// shown *why* were accidentally dropped in the same rewrite that unified this with "&lt;link&gt;"
    /// handling). "&lt;flag&gt;" is deliberately left unhandled now, sent as ordinary plain text like any
    /// other unrecognized token - reliability over the feature, given repeated failures trying to build
    /// this specific payload live. If revisiting, add logging *before* re-attempting the expansion, not
    /// after - this was debugged blind for too many rounds already.</para></summary>
    private void AppendWithPlaceholderExpansion(MemoryStream buffer, string text, IReadOnlyList<PendingItemLink>? attachments)
    {
        var itemIndex = 0;
        var pos = 0;

        while (pos < text.Length)
        {
            var linkIndex = text.IndexOf(LinkPlaceholder, pos, StringComparison.Ordinal);
            if (linkIndex < 0)
            {
                buffer.Write(Encoding.UTF8.GetBytes(text[pos..]));
                return;
            }

            if (linkIndex > pos)
                buffer.Write(Encoding.UTF8.GetBytes(text[pos..linkIndex]));

            if (attachments != null && itemIndex < attachments.Count)
            {
                var link = attachments[itemIndex++];
                buffer.Write(new SeStringBuilder().AddItemLink(link.ItemId, link.IsHq, link.DisplayName).Encode());
            }
            else
            {
                buffer.Write(Encoding.UTF8.GetBytes(LinkPlaceholder));
            }

            pos = linkIndex + LinkPlaceholder.Length;
        }
    }
}
