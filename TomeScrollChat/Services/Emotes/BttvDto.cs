using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TomeScrollChat.Services.Emotes;

/// <summary>Shape of one entry in BTTV's `GET /3/cached/emotes/global` array response.</summary>
public sealed class BttvEmoteDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("imageType")]
    public string ImageType { get; set; } = "png";
}

/// <summary>Shape of BTTV's `GET /3/cached/users/twitch/{twitchId}` object response - the per-channel
/// lookup <see cref="Services.EmoteService"/> uses for <see cref="Models.EmoteChannelConfig"/> entries.
/// <see cref="ChannelEmotes"/> are the channel's own uploads; <see cref="SharedEmotes"/> are ones the
/// channel owner added from elsewhere - both use the same entry shape as the global list, and both are
/// loaded the same way, since neither this plugin nor its player needs to distinguish them.</summary>
public sealed class BttvChannelResponseDto
{
    [JsonPropertyName("channelEmotes")]
    public List<BttvEmoteDto> ChannelEmotes { get; set; } = new();

    [JsonPropertyName("sharedEmotes")]
    public List<BttvEmoteDto> SharedEmotes { get; set; } = new();
}
