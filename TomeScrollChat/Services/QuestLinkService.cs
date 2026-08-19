using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TomeScrollChat.Services;

public sealed unsafe class QuestLinkService
{
    private readonly IPluginLog log;

    public QuestLinkService(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary><paramref name="questId"/> is expected to already carry the +65536 offset
    /// <see cref="Dalamud.Game.Text.SeStringHandling.Payloads.QuestPayload"/>'s own <c>Quest.RowId</c>
    /// bakes in (see <c>QuestPayload.DecodeImpl</c>) - the same range native quest-id APIs expect, no
    /// further transformation needed. <c>type = 1</c> selects the regular Quest journal type (as
    /// opposed to <c>2</c> for a LeveQuest) - <see cref="Dalamud.Game.Text.SeStringHandling.Payloads.QuestPayload"/>
    /// doesn't distinguish leve-specific links, so every quest link found in chat is treated as a
    /// regular quest, matching what clicking one in the native chat log does.</summary>
    public void OpenQuest(uint questId)
    {
        try
        {
            AgentQuestJournal.Instance()->OpenForQuest(questId, 1);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "TomeScrollChat: failed to open quest {QuestId}", questId);
        }
    }
}
