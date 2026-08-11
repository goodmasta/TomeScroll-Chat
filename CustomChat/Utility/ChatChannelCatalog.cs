using System.Collections.Generic;
using Dalamud.Game.Text;

namespace CustomChat.Utility;

/// <summary>Curated, grouped list of user-assignable channels for the tab editor (deliberately
/// excludes GM-only chat types - not relevant to normal players).</summary>
public static class ChatChannelCatalog
{
    public sealed record Group(string Title, IReadOnlyList<(XivChatType Type, string Label)> Channels);

    public static readonly IReadOnlyList<Group> Groups = new List<Group>
    {
        new("Chat", new (XivChatType, string)[]
        {
            (XivChatType.Say, "Say"),
            (XivChatType.Yell, "Yell"),
            (XivChatType.Shout, "Shout"),
            (XivChatType.CustomEmote, "Custom Emote"),
            (XivChatType.StandardEmote, "Standard Emote"),
        }),
        new("Party / Alliance", new (XivChatType, string)[]
        {
            (XivChatType.Party, "Party"),
            (XivChatType.Alliance, "Alliance"),
            (XivChatType.CrossParty, "Cross-world Party"),
        }),
        new("Free Company / Novice / PvP", new (XivChatType, string)[]
        {
            (XivChatType.FreeCompany, "Free Company"),
            (XivChatType.NoviceNetwork, "Novice Network"),
            (XivChatType.PvPTeam, "PvP Team"),
        }),
        new("Tells", new (XivChatType, string)[]
        {
            (XivChatType.TellIncoming, "Tell (incoming)"),
            (XivChatType.TellOutgoing, "Tell (outgoing)"),
        }),
        new("Linkshells", new (XivChatType, string)[]
        {
            (XivChatType.Ls1, "Linkshell 1"), (XivChatType.Ls2, "Linkshell 2"),
            (XivChatType.Ls3, "Linkshell 3"), (XivChatType.Ls4, "Linkshell 4"),
            (XivChatType.Ls5, "Linkshell 5"), (XivChatType.Ls6, "Linkshell 6"),
            (XivChatType.Ls7, "Linkshell 7"), (XivChatType.Ls8, "Linkshell 8"),
        }),
        new("Cross-world Linkshells", new (XivChatType, string)[]
        {
            (XivChatType.CrossLinkShell1, "CWLS 1"), (XivChatType.CrossLinkShell2, "CWLS 2"),
            (XivChatType.CrossLinkShell3, "CWLS 3"), (XivChatType.CrossLinkShell4, "CWLS 4"),
            (XivChatType.CrossLinkShell5, "CWLS 5"), (XivChatType.CrossLinkShell6, "CWLS 6"),
            (XivChatType.CrossLinkShell7, "CWLS 7"), (XivChatType.CrossLinkShell8, "CWLS 8"),
        }),
        new("System / Log", new (XivChatType, string)[]
        {
            (XivChatType.Debug, "Debug"),
            (XivChatType.Urgent, "Urgent"),
            (XivChatType.Notice, "Notice"),
            (XivChatType.Echo, "Echo"),
            (XivChatType.SystemMessage, "System Message"),
            (XivChatType.SystemError, "System Error"),
            (XivChatType.ErrorMessage, "Error"),
            (XivChatType.GatheringSystemMessage, "Gathering System"),
            (XivChatType.NPCDialogue, "NPC Dialogue"),
            (XivChatType.NPCDialogueAnnouncements, "NPC Announcement"),
            (XivChatType.LootNotice, "Loot Notice"),
            (XivChatType.LootRoll, "Loot Roll"),
            (XivChatType.Progress, "Progress"),
            (XivChatType.Crafting, "Crafting"),
            (XivChatType.Gathering, "Gathering"),
            (XivChatType.FreeCompanyAnnouncement, "FC Announcement"),
            (XivChatType.FreeCompanyLoginLogout, "FC Login/Logout"),
            (XivChatType.RetainerSale, "Retainer Sale"),
            (XivChatType.PeriodicRecruitmentNotification, "Recruitment"),
            (XivChatType.Sign, "Sign"),
            (XivChatType.RandomNumber, "Random Number"),
            (XivChatType.NoviceNetworkSystem, "Novice Network System"),
            (XivChatType.Orchestrion, "Orchestrion"),
            (XivChatType.PvpTeamAnnouncement, "PvP Team Announcement"),
            (XivChatType.PvpTeamLoginLogout, "PvP Team Login/Logout"),
            (XivChatType.MessageBook, "Message Book"),
            (XivChatType.GlamourNotifications, "Glamour"),
            (XivChatType.Alarm, "Alarm"),
        }),
        new("Combat Log (off by default)", new (XivChatType, string)[]
        {
            (XivChatType.Damage, "Damage"),
            (XivChatType.Miss, "Miss"),
            (XivChatType.Action, "Action"),
            (XivChatType.Item, "Item"),
            (XivChatType.Healing, "Healing"),
            (XivChatType.GainBuff, "Gain Buff"),
            (XivChatType.GainDebuff, "Gain Debuff"),
            (XivChatType.LoseBuff, "Lose Buff"),
            (XivChatType.LoseDebuff, "Lose Debuff"),
        }),
    };
}
