namespace CustomChat.Models;

public enum EmoteProvider
{
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
