using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using CustomChat.Models;

namespace CustomChat;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<ChatTabConfig> Tabs { get; set; } = new();

    /// <summary>False = new whisper conversations open as a tab in the main window (default). True = each opens its own floating window.</summary>
    public bool OpenWhispersInSeparateWindow { get; set; }

    /// <summary>Hide the game's own chat log addon(s) while this plugin is active.</summary>
    public bool HideNativeChat { get; set; } = true;

    /// <summary>Redacts player/FC/linkshell names in the chat display for screenshots.</summary>
    public bool ScreenshotMode { get; set; }

    public float FontSize { get; set; } = 14f;

    public bool OpenLinksOnClick { get; set; } = true;

    public bool BttvEnabled { get; set; } = true;

    public bool SevenTvEnabled { get; set; } = true;

    /// <summary>Optional Twitch channel (login name) whose BTTV/7TV channel emotes are loaded in addition to the global sets.</summary>
    public string EmoteTwitchChannel { get; set; } = string.Empty;

    public float EmoteScale { get; set; } = 1.0f;

    /// <summary>Hours a cached emote set/image is considered fresh before being re-fetched.</summary>
    public int EmoteCacheTtlHours { get; set; } = 24;

    /// <summary>Hard cap, in bytes, on total chat history stored on disk (default 1 GiB). Oldest messages are pruned first.</summary>
    public long MaxHistoryBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
