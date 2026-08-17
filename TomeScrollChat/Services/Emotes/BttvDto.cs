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
