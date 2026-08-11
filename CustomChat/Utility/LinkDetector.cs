using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CustomChat.Utility;

public readonly record struct TextSpan(int Start, int Length, bool IsLink)
{
    public string Slice(string text) => text.Substring(Start, Length);
}

/// <summary>Finds http(s)/www URLs in chat text so they can be rendered as separate clickable ImGui elements.</summary>
public static class LinkDetector
{
    private static readonly Regex UrlRegex = new(
        @"(?<url>(https?://|www\.)[^\s<>""']+[^\s<>""'.,;:!?)\]])",
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
