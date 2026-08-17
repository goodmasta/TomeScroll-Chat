using System.Collections.Concurrent;
using System.Collections.Generic;
using TomeScrollChat.Models;

namespace TomeScrollChat.Services;

/// <summary>
/// Bounded in-memory scrollback per tab/whisper-partner, backing what the UI actually renders each
/// frame - independent of the (much larger, disk-backed, capped-at-1GiB) full history in
/// <see cref="ChatHistoryService"/>. Lazily seeded from disk the first time a tab is drawn.
/// </summary>
public sealed class TabMessageBuffer
{
    private const int MaxInMemory = 1000;

    private readonly ChatHistoryService history;
    private readonly ConcurrentDictionary<string, List<ChatMessageRecord>> buffers = new();
    private readonly object gate = new();

    public TabMessageBuffer(ChatHistoryService history)
    {
        this.history = history;
    }

    public IReadOnlyList<ChatMessageRecord> GetMessages(ChatTabConfig tab)
    {
        var key = RoutingKey(tab);
        lock (gate)
            return buffers.GetOrAdd(key, k => history.LoadRecent(k, MaxInMemory)).ToArray();
    }

    public void Append(ChatTabConfig tab, ChatMessageRecord record)
    {
        var key = RoutingKey(tab);
        lock (gate)
        {
            var list = buffers.GetOrAdd(key, k => history.LoadRecent(k, MaxInMemory));
            list.Add(record);
            if (list.Count > MaxInMemory)
                list.RemoveRange(0, list.Count - MaxInMemory);
        }
    }

    public static string RoutingKey(ChatTabConfig tab) => tab.IsPmTab ? tab.PmPartnerKey ?? tab.Id.ToString() : tab.Id.ToString();

    /// <summary>Drops every in-memory scrollback so cleared/purged disk history isn't still shown
    /// from cache until the next reload.</summary>
    public void ClearAll()
    {
        lock (gate)
            buffers.Clear();
    }
}
