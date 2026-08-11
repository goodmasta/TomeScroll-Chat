using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CustomChat.Services.Emotes;

/// <summary>Shape of 7TV v3's `GET /v3/emote-sets/global` object response.</summary>
public sealed class SevenTvEmoteSetDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("emotes")]
    public List<SevenTvEmoteDto> Emotes { get; set; } = new();
}

public sealed class SevenTvEmoteDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public SevenTvEmoteDataDto? Data { get; set; }
}

public sealed class SevenTvEmoteDataDto
{
    [JsonPropertyName("host")]
    public SevenTvHostDto? Host { get; set; }
}

public sealed class SevenTvHostDto
{
    /// <summary>Protocol-relative base URL, e.g. "//cdn.7tv.app/emote/{id}".</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public List<SevenTvHostFileDto> Files { get; set; } = new();
}

public sealed class SevenTvHostFileDto
{
    /// <summary>File name relative to <see cref="SevenTvHostDto.Url"/>, e.g. "2x.webp".</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }
}
