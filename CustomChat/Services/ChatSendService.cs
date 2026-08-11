using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

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
    public void Send(string channelCommand, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var isExplicitCommand = message.TrimStart().StartsWith('/');
        var full = string.IsNullOrEmpty(channelCommand) || isExplicitCommand ? message : $"{channelCommand} {message}";
        var byteCount = Encoding.UTF8.GetByteCount(full);
        if (byteCount > MaxUtf8Bytes)
        {
            log.Warning("CustomChat: outgoing message is {Bytes} UTF-8 bytes, over the {Max}-byte limit - not sending", byteCount, MaxUtf8Bytes);
            return;
        }

        var bytes = new byte[byteCount + 1]; // native string wants a trailing null terminator
        Encoding.UTF8.GetBytes(full, 0, full.Length, bytes, 0);

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
}
