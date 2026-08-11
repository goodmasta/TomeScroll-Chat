using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CustomChat.Utility;

public readonly record struct TextSpan(int Start, int Length, bool IsLink)
{
    public string Slice(string text) => text.Substring(Start, Length);
}

/// <summary>Finds URLs in chat text so they can be rendered as separate clickable ImGui elements.</summary>
public static class LinkDetector
{
    // Common TLDs seen in chat (invite links like "discord.gg/xxxx" have no "http://"/"www." prefix
    // at all, so they need their own alternative rather than relying on a scheme/www prefix).
    private const string BareDomainTlds =
        "gg|com|net|org|io|co|me|tv|app|dev|xyz|info|link|shop|store|online|site|club|live|chat|" +
        "ru|su|us|uk|de|fr|jp|edu|gov|ly|to|gl|pro|biz|cc|wtf|fun|city";

    private static readonly Regex UrlRegex = new(
        $@"(?<url>(?:https?://|www\.)[^\s<>""']+[^\s<>""'.,;:!?)\]]|" +
        $@"\b[a-z0-9](?:[a-z0-9-]{{0,61}}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{{0,61}}[a-z0-9])?)*\.(?:{BareDomainTlds})\b(?:/[^\s<>""']*[^\s<>""'.,;:!?)\]])?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Splits <paramref name="text"/> into alternating plain-text and link spans, in order.</summary>
    public static List<TextSpan> Split(string text)
    {
        var spans = new List<TextSpan>();
        if (string.IsNullOrEmpty(text))
            return spans;

        var cursor = 0;
        foreach (Match match in UrlRegex.Matches(text))
        {
            if (match.Index > cursor)
                spans.Add(new TextSpan(cursor, match.Index - cursor, false));

            spans.Add(new TextSpan(match.Index, match.Length, true));
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
            spans.Add(new TextSpan(cursor, text.Length - cursor, false));

        return spans;
    }

    public static string NormalizeForBrowser(string url) =>
        url.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)
            ? url
            : $"https://{url}";
}
