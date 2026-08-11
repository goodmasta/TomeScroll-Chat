using System.Collections.Generic;
using Dalamud.Game.Text;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>Builds the five built-in tabs created on first run (see тз section "Стандартные каналы по умолчанию").</summary>
public static class DefaultTabFactory
{
    /// <summary>
    /// System/informational chat types that make up the default "Log" tab. Deliberately excludes
    /// the raw combat-log types (Damage/Miss/Action/Item/Healing/GainBuff/GainDebuff/LoseBuff/LoseDebuff,
    /// values 41-49) per the spec - those are combat log, not chat log, and are not shown anywhere by default.
    /// </summary>
    public static readonly HashSet<XivChatType> LogChannels = new()
    {
        XivChatType.Debug,
        XivChatType.Urgent,
        XivChatType.Notice,
        XivChatType.GlamourNotifications,
        XivChatType.Alarm,
        XivChatType.Echo,
        XivChatType.SystemMessage,
        XivChatType.SystemError,
        XivChatType.GatheringSystemMessage,
        XivChatType.ErrorMessage,
        XivChatType.NPCDialogue,
        XivChatType.NPCDialogueAnnouncements,
        XivChatType.LootNotice,
        XivChatType.Progress,
        XivChatType.LootRoll,
        XivChatType.Crafting,
        XivChatType.Gathering,
        XivChatType.FreeCompanyAnnouncement,
        XivChatType.FreeCompanyLoginLogout,
        XivChatType.RetainerSale,
        XivChatType.PeriodicRecruitmentNotification,
        XivChatType.Sign,
        XivChatType.RandomNumber,
        XivChatType.NoviceNetworkSystem,
        XivChatType.Orchestrion,
        XivChatType.PvpTeamAnnouncement,
        XivChatType.PvpTeamLoginLogout,
        XivChatType.MessageBook,
    };

    public static readonly HashSet<XivChatType> GeneralChannels = new()
    {
        XivChatType.Say,
        XivChatType.Yell,
        XivChatType.Shout,
    };

    public static readonly HashSet<XivChatType> PartyChannels = new()
    {
        XivChatType.Party,
        XivChatType.Alliance,
        XivChatType.CrossParty,
    };

    public static readonly HashSet<XivChatType> FreeCompanyChannels = new()
    {
        XivChatType.FreeCompany,
    };

    public static readonly HashSet<XivChatType> NoviceChannels = new()
    {
        XivChatType.NoviceNetwork,
    };

    /// <summary>Tells (in and out) - never part of a static tab; routed to per-partner PM tabs/windows instead.</summary>
    public static readonly HashSet<XivChatType> TellChannels = new()
    {
        XivChatType.TellIncoming,
        XivChatType.TellOutgoing,
    };

    public static List<ChatTabConfig> CreateDefaults() => new()
    {
        new ChatTabConfig { Name = "Party", Channels = new(PartyChannels), OutgoingChannelCommand = "/p", IsBuiltIn = true },
        new ChatTabConfig { Name = "General", Channels = new(GeneralChannels), OutgoingChannelCommand = "/s", IsBuiltIn = true },
        new ChatTabConfig { Name = "Free Company", Channels = new(FreeCompanyChannels), OutgoingChannelCommand = "/fc", IsBuiltIn = true },
        new ChatTabConfig { Name = "Novice Chat", Channels = new(NoviceChannels), OutgoingChannelCommand = "/nov", IsBuiltIn = true },
        new ChatTabConfig { Name = "Log", Channels = new(LogChannels), OutgoingChannelCommand = string.Empty, IsBuiltIn = true },
    };
}
