using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace CustomChat.Services;

/// <summary>
/// Watches <c>AgentChatLog.LinkedItem</c> every frame for the item the native right-click "Link"
/// action last set, the same polling approach <see cref="NativeChatInputWatcher"/> already uses for
/// other <c>AgentChatLog</c> state this plugin has no event for.
/// </summary>
/// <remarks>
/// Replaced an earlier attempt that hooked <c>AgentChatLog::LinkItem(uint itemId)</c> directly via
/// <c>IGameInteropProvider</c> - it installed cleanly (confirmed via its own log line) but never once
/// fired despite repeated live testing, meaning that isn't actually the function the native "Link"
/// action calls. Rather than guess at another function to hook blind (a wrong guess there risks
/// crashing the game, unlike a wrong guess here, which just does nothing), this reads the *result*
/// state the action leaves behind instead - <c>LinkedItem</c> is exactly what its name says, and its
/// well-defined accessor methods (<c>IsEmpty</c>/<c>GetBaseItemId</c>/<c>IsHighQuality</c>) don't
/// require knowing which internal code path actually populated it. Matches the same approach ChatTwo
/// itself uses for this feature (per the user, who pointed at ChatTwo's implementation directly): a
/// literal "&lt;link&gt;" placeholder appears in the compose box the moment an item is linked, and gets
/// swapped for the real link only at send time (see <see cref="ChatSendService"/>).
///
/// <para>Also captures the raw bytes sitting in the (hidden) native chat log's own text input the same
/// frame a new link is detected - the same textbox <see cref="NativeChatInputWatcher"/> watches for
/// leaked "/" commands, confirmed to also receive the item's encoded link payload the moment "Link" is
/// clicked. A manually-reconstructed link (built via <c>SeStringBuilder.AddItemLink</c>) displayed
/// correctly when sent but silently lost its <c>ItemPayload</c> on the round trip through the server/
/// client, becoming plain text - using the game's own actual bytes instead of reconstructing them
/// sidesteps whatever that mismatch was, without needing to find it. Read via
/// <c>Utf8String.AsSpan()</c>, not <c>.ToString()</c> - the earlier, abandoned attempt at reading this
/// same textbox for item links assumed a decode-to-C#-string round trip was needed and worried it
/// would corrupt the non-ASCII payload bytes a link encodes; reading the raw byte span directly avoids
/// that decode entirely, so there's nothing left to corrupt.</para>
/// </remarks>
public sealed unsafe class NativeItemLinkWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action<uint, bool, byte[]?> onItemLinked;
    private uint lastItemId;
    private bool lastIsHq;

    public NativeItemLinkWatcher(IFramework framework, IGameGui gameGui, IPluginLog log, Action<uint, bool, byte[]?> onItemLinked)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.onItemLinked = onItemLinked;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null || agent->LinkedItem.IsEmpty())
            return;

        var itemId = agent->LinkedItem.GetBaseItemId();
        var isHq = agent->LinkedItem.IsHighQuality();
        if (itemId == 0 || (itemId == lastItemId && isHq == lastIsHq))
            return;

        lastItemId = itemId;
        lastIsHq = isHq;
        onItemLinked(itemId, isHq, TryCaptureNativePayloadBytes());
    }

    private byte[]? TryCaptureNativePayloadBytes()
    {
        try
        {
            var addon = gameGui.GetAddonByName<AddonChatLog>("ChatLog");
            if (addon == null || addon->TextInput == null)
                return null;

            var span = addon->TextInput->RawString.AsSpan();
            if (span.Length == 0)
                return null;

            var bytes = span.ToArray();

            // TEMPORARY diagnostic (2026-08-13) - the whole point of capturing these bytes is to fix a
            // report where the manually-reconstructed version silently failed; this confirms whether
            // the native box actually had anything in it to capture at all. Remove once confirmed.
            log.Warning("CustomChat: captured {Bytes} raw bytes from the native chat input after an item link", bytes.Length);

            // Cleared immediately so it doesn't leak into NativeChatInputWatcher's own leak-through
            // detection on a later frame - same cleanup this same textbox always needs after reading it.
            addon->TextInput->SetText(string.Empty);
            var chatLogAgent = AgentChatLog.Instance();
            if (chatLogAgent != null)
                chatLogAgent->HideLogWindow();

            return bytes;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to capture the native item link payload bytes");
            return null;
        }
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
