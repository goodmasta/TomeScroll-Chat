namespace CustomChat.Models;

public enum EmoteProvider
{
    /// <summary>A curated set of standard Unicode emoji, rendered as real images (not text glyphs -
    /// Dalamud's UI font doesn't have colour-emoji glyphs) via a CDN. Sorted first wherever emotes
    /// are listed.</summary>
    Standard,
    Bttv,
    SevenTv,
}

/// <summary>One resolved emote: a text code (e.g. "PogChamp") mapped to a downloadable image URL.</summary>
public sealed class EmoteDefinition
{
    public required string Code { get; init; }
    public required string Id { get; init; }
    public required string ImageUrl { get; init; }
    public required EmoteProvider Provider { get; init; }
}
