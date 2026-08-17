using System.Collections.Generic;

namespace TomeScrollChat.Utility;

/// <summary>
/// One tab's up/down-arrow send history - shared logic for <c>MainWindow</c> (one instance per tab,
/// since a single tab bar switches between several) and <c>DetachedTabWindow</c> (one instance total,
/// since a detached window only ever has the one tab it was popped out for). Deliberately a plain
/// class rather than a <c>Services/</c> singleton - purely local per-window/per-tab state, never
/// constructed via <c>Plugin.cs</c>.
/// </summary>
public sealed class SendHistoryTracker
{
    private const int MaxEntries = 50;

    private readonly List<string> entries = new();
    private int index = -1; // -1 = not currently browsing history
    private string draft = string.Empty;

    /// <summary>True while a history entry is currently loaded into the compose box (as opposed to
    /// the player's own freshly-typed text) - lets the caller decide whether Up/Down should navigate
    /// history at all, or be left alone for normal multi-line cursor movement.</summary>
    public bool IsBrowsing => index != -1;

    /// <summary>Records a just-sent message, skipping empty/whitespace-only text and an exact repeat
    /// of the immediately previous entry (so spamming the same line doesn't fill history with
    /// duplicates) - also ends any in-progress browsing, since the just-sent text is now the most
    /// recent real entry.</summary>
    public void Push(string text)
    {
        index = -1;

        if (string.IsNullOrWhiteSpace(text) || (entries.Count > 0 && entries[^1] == text))
            return;

        entries.Add(text);
        if (entries.Count > MaxEntries)
            entries.RemoveAt(0);
    }

    /// <summary>Ends browsing without touching the compose box - used when switching away from this
    /// tab/tracker mid-browse, so resuming later starts fresh instead of on a stale index.</summary>
    public void ResetBrowsing() => index = -1;

    /// <summary><paramref name="current"/> is whatever's in the compose box right now. Returns null if
    /// there's nothing to do (no history yet, or already at the newest/oldest end in that direction);
    /// otherwise returns the text that should replace the compose box's contents. Saves
    /// <paramref name="current"/> as the "draft" the moment browsing starts (via Up on an empty/not-
    /// yet-browsing box), restored once Down is pressed past the newest entry.</summary>
    public string? Navigate(bool up, string current)
    {
        if (up)
        {
            if (entries.Count == 0)
                return null;

            if (index == -1)
            {
                draft = current;
                index = entries.Count - 1;
            }
            else if (index > 0)
            {
                index--;
            }
            else
            {
                return null; // already at the oldest entry
            }

            return entries[index];
        }

        if (index == -1)
            return null;

        if (index < entries.Count - 1)
        {
            index++;
            return entries[index];
        }

        index = -1;
        return draft;
    }
}
