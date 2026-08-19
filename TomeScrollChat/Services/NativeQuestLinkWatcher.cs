using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TomeScrollChat.Services;

/// <summary>
/// Watches <c>AgentChatLog.LinkedQuestId</c> every frame for the quest the native Quest Journal's own
/// "Link in Chat" action last set - the same polling approach <see cref="NativeItemLinkWatcher"/> and
/// <see cref="NativePartyFinderLinkWatcher"/> already use for the other <c>AgentChatLog</c> "linked
/// something" fields this plugin has no event for (confirmed via the metadata tool: <c>LinkedQuestId</c>
/// (UInt32) and <c>LinkedQuestName</c> (Utf8String) sit right next to <c>LinkedItem</c>/
/// <c>LinkedPartyFinderId</c> on the same struct). Without this, "Link in Chat" writes its link straight
/// into the (hidden) native chat log's own input box, which this plugin never reads from - so the click
/// silently did nothing visible from here, while the game still queued the link for its own hidden
/// textbox.
///
/// <para>Captures the raw payload bytes sitting in that same textbox the same frame a new quest id is
/// detected - identical technique to <see cref="NativeItemLinkWatcher"/>/<see cref="NativePartyFinderLinkWatcher"/>,
/// for the same reason: reading the game's own already-encoded bytes sidesteps ever needing to
/// reconstruct the payload by hand.</para>
/// </summary>
public sealed unsafe class NativeQuestLinkWatcher : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly Action<uint, string, byte[]?> onQuestLinked;
    private uint lastQuestId;

    public NativeQuestLinkWatcher(IFramework framework, IGameGui gameGui, IPluginLog log, Action<uint, string, byte[]?> onQuestLinked)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.onQuestLinked = onQuestLinked;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var agent = AgentChatLog.Instance();
        if (agent == null)
            return;

        var questId = agent->LinkedQuestId;
        if (questId == 0 || questId == lastQuestId)
            return;

        lastQuestId = questId;
        var questName = agent->LinkedQuestName.ToString();
        log.Debug("TomeScrollChat: native quest link detected - {QuestId} ({QuestName})", questId, questName);
        onQuestLinked(questId, questName, TryCaptureNativePayloadBytes());
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

            // Cleared immediately, same as NativeItemLinkWatcher/NativePartyFinderLinkWatcher do for
            // this same textbox - otherwise it leaks into NativeChatInputWatcher's own leak-through
            // detection on a later frame.
            addon->TextInput->SetText(string.Empty);
            var chatLogAgent = AgentChatLog.Instance();
            if (chatLogAgent != null)
                chatLogAgent->HideLogWindow();

            return bytes;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to capture the native quest link payload bytes");
            return null;
        }
    }

    public void Dispose() => framework.Update -= OnFrameworkUpdate;
}
