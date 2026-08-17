using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using CustomChat.Models;

namespace CustomChat.Services;

/// <summary>
/// Disk-backed chat history. Writes happen on a dedicated background task fed by an unbounded
/// channel so message capture on the framework thread never blocks on disk I/O. A separate timer
/// periodically enforces the configured byte cap by deleting the oldest rows and running an
/// incremental vacuum to actually reclaim space on disk.
/// </summary>
public sealed class ChatHistoryService : IDisposable
{
    private readonly string dbPath;
    private readonly IPluginLog log;
    private readonly Channel<ChatMessageRecord> pending = Channel.CreateUnbounded<ChatMessageRecord>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    private readonly SqliteConnection writerConnection;
    private readonly CancellationTokenSource cts = new();
    private readonly Task writerTask;
    private readonly Timer rotationTimer;
    private long maxBytes;

    public ChatHistoryService(string configDirectory, long maxHistoryBytes, IPluginLog log)
    {
        this.log = log;
        maxBytes = maxHistoryBytes;

        Directory.CreateDirectory(configDirectory);
        dbPath = Path.Combine(configDirectory, "history.db");

        writerConnection = OpenConnection();
        InitializeSchema(writerConnection);

        writerTask = Task.Run(() => WriterLoopAsync(cts.Token));
        rotationTimer = new Timer(_ => EnforceSizeCap(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    public void SetMaxBytes(long bytes) => maxBytes = bytes;

    /// <summary>Wipes every stored message and reclaims the disk space - the plugin settings' "Clear
    /// history" button. Irreversible; callers are expected to confirm with the user first.</summary>
    public void ClearAll()
    {
        try
        {
            using var cmd = writerConnection.CreateCommand();
            cmd.CommandText = "DELETE FROM messages; PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum;";
            cmd.ExecuteNonQuery();
            log.Information("CustomChat: cleared all chat history");
        }
        catch (Exception ex)
        {
            log.Error(ex, "CustomChat: failed to clear chat history");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA auto_vacuum=INCREMENTAL;";
            pragma.ExecuteNonQuery();
        }

        return connection;
    }

    private static void InitializeSchema(SqliteConnection connection)
    {
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    routing_key TEXT NOT NULL,
                    timestamp_utc INTEGER NOT NULL,
                    chat_type INTEGER NOT NULL,
                    sender_name TEXT NOT NULL,
                    sender_key TEXT NOT NULL,
                    body TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_messages_routing ON messages(routing_key, id);
                """;
            cmd.ExecuteNonQuery();
        }

        // Columns added after the original schema (payload_links, then translation/translation_lang)
        // need an explicit migration for an already-existing database file - CREATE TABLE IF NOT
        // EXISTS above only matters for a brand new one, and SQLite has no "ADD COLUMN IF NOT
        // EXISTS", so check what's already there first.
        var existingColumns = new HashSet<string>(StringComparer.Ordinal);
        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "PRAGMA table_info(messages);";
            using var reader = checkCmd.ExecuteReader();
            while (reader.Read())
                existingColumns.Add(reader.GetString(1));
        }

        void AddColumnIfMissing(string name, string sqlType)
        {
            if (existingColumns.Contains(name))
                return;

            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE messages ADD COLUMN {name} {sqlType};";
            alterCmd.ExecuteNonQuery();
        }

        AddColumnIfMissing("payload_links", "TEXT");

        // translation/translation_lang (2026-08-17) - lets a translation survive a tab reopen/plugin
        // restart instead of re-requesting it from the translation endpoint every time, see
        // SaveTranslation/LoadRecent and TranslationService.
        AddColumnIfMissing("translation", "TEXT");
        AddColumnIfMissing("translation_lang", "TEXT");
    }

    /// <summary>Enqueues a message for background persistence. Non-blocking.</summary>
    public void Enqueue(ChatMessageRecord record) => pending.Writer.TryWrite(record);

    private async Task WriterLoopAsync(CancellationToken token)
    {
        var batch = new List<ChatMessageRecord>(64);
        try
        {
            while (await pending.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (pending.Reader.TryRead(out var record))
                {
                    batch.Add(record);
                    if (batch.Count >= 64)
                        break;
                }

                if (batch.Count == 0)
                    continue;

                try
                {
                    FlushBatch(batch);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "CustomChat: failed to write {Count} chat message(s) to history", batch.Count);
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void FlushBatch(List<ChatMessageRecord> batch)
    {
        using var transaction = writerConnection.BeginTransaction();
        using var cmd = writerConnection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO messages (routing_key, timestamp_utc, chat_type, sender_name, sender_key, body, payload_links)
            VALUES ($routingKey, $timestamp, $chatType, $senderName, $senderKey, $body, $payloadLinks);
            """;
        var pRouting = cmd.CreateParameter(); pRouting.ParameterName = "$routingKey"; cmd.Parameters.Add(pRouting);
        var pTimestamp = cmd.CreateParameter(); pTimestamp.ParameterName = "$timestamp"; cmd.Parameters.Add(pTimestamp);
        var pChatType = cmd.CreateParameter(); pChatType.ParameterName = "$chatType"; cmd.Parameters.Add(pChatType);
        var pSenderName = cmd.CreateParameter(); pSenderName.ParameterName = "$senderName"; cmd.Parameters.Add(pSenderName);
        var pSenderKey = cmd.CreateParameter(); pSenderKey.ParameterName = "$senderKey"; cmd.Parameters.Add(pSenderKey);
        var pBody = cmd.CreateParameter(); pBody.ParameterName = "$body"; cmd.Parameters.Add(pBody);
        var pPayloadLinks = cmd.CreateParameter(); pPayloadLinks.ParameterName = "$payloadLinks"; cmd.Parameters.Add(pPayloadLinks);

        foreach (var record in batch)
        {
            pRouting.Value = record.RoutingKey;
            pTimestamp.Value = new DateTimeOffset(record.TimestampUtc).ToUnixTimeMilliseconds();
            pChatType.Value = (int)record.ChatType;
            pSenderName.Value = record.SenderName;
            pSenderKey.Value = record.SenderKey;
            pBody.Value = record.Body;
            pPayloadLinks.Value = (object?)SerializePayloadLinks(record.PayloadLinks) ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>A link's <see cref="MapLinkPayload"/>/<see cref="ItemPayload"/> object itself isn't
    /// something SQLite (or Newtonsoft, given the SDK types' other properties like <c>RowRef&lt;T&gt;</c>)
    /// can round-trip directly - stored instead as this minimal DTO with just enough raw data
    /// (territory/map ids + raw X/Y for a map link; item id + <see cref="ItemKind"/> for an item link)
    /// to reconstruct an equivalent payload object via the same constructors used elsewhere in this
    /// project to build one from scratch (see <see cref="ChatSendService"/>/<see cref="ItemTooltipService"/>).</summary>
    private sealed class StoredPayloadLink
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Type { get; set; } = string.Empty;
        public uint TerritoryTypeId { get; set; }
        public uint MapId { get; set; }
        public int RawX { get; set; }
        public int RawY { get; set; }
        public uint ItemId { get; set; }
        public int ItemKind { get; set; }
        public uint ListingId { get; set; }
        public int PartyFinderLinkType { get; set; }
    }

    private static string? SerializePayloadLinks(IReadOnlyList<ChatPayloadLink> links)
    {
        if (links.Count == 0)
            return null;

        var stored = new List<StoredPayloadLink>(links.Count);
        foreach (var link in links)
        {
            switch (link)
            {
                case { Type: ChatPayloadLinkType.MapLink, MapLink: { } mapLink }:
                    stored.Add(new StoredPayloadLink
                    {
                        Start = link.Start,
                        Length = link.Length,
                        Type = nameof(ChatPayloadLinkType.MapLink),
                        TerritoryTypeId = mapLink.TerritoryType.RowId,
                        MapId = mapLink.Map.RowId,
                        RawX = mapLink.RawX,
                        RawY = mapLink.RawY,
                    });
                    break;
                case { Type: ChatPayloadLinkType.Item, Item: { } item }:
                    stored.Add(new StoredPayloadLink
                    {
                        Start = link.Start,
                        Length = link.Length,
                        Type = nameof(ChatPayloadLinkType.Item),
                        ItemId = item.ItemId,
                        ItemKind = (int)item.Kind,
                    });
                    break;
                case { Type: ChatPayloadLinkType.PartyFinder, PartyFinder: { } pf }:
                    stored.Add(new StoredPayloadLink
                    {
                        Start = link.Start,
                        Length = link.Length,
                        Type = nameof(ChatPayloadLinkType.PartyFinder),
                        ListingId = pf.ListingId,
                        PartyFinderLinkType = (int)pf.LinkType,
                    });
                    break;
            }
        }

        return stored.Count == 0 ? null : JsonConvert.SerializeObject(stored);
    }

    private List<ChatPayloadLink> DeserializePayloadLinks(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<ChatPayloadLink>();

        try
        {
            var stored = JsonConvert.DeserializeObject<List<StoredPayloadLink>>(json);
            if (stored == null || stored.Count == 0)
                return new List<ChatPayloadLink>();

            var links = new List<ChatPayloadLink>(stored.Count);
            foreach (var s in stored)
            {
                if (s.Type == nameof(ChatPayloadLinkType.MapLink))
                {
                    links.Add(new ChatPayloadLink
                    {
                        Start = s.Start,
                        Length = s.Length,
                        Type = ChatPayloadLinkType.MapLink,
                        MapLink = new MapLinkPayload(s.TerritoryTypeId, s.MapId, s.RawX, s.RawY),
                    });
                }
                else if (s.Type == nameof(ChatPayloadLinkType.Item))
                {
                    links.Add(new ChatPayloadLink
                    {
                        Start = s.Start,
                        Length = s.Length,
                        Type = ChatPayloadLinkType.Item,
                        Item = new ItemPayload(s.ItemId, (ItemKind)s.ItemKind, null),
                    });
                }
                else if (s.Type == nameof(ChatPayloadLinkType.PartyFinder))
                {
                    links.Add(new ChatPayloadLink
                    {
                        Start = s.Start,
                        Length = s.Length,
                        Type = ChatPayloadLinkType.PartyFinder,
                        PartyFinder = new PartyFinderPayload(s.ListingId, (PartyFinderPayload.PartyFinderLinkType)s.PartyFinderLinkType),
                    });
                }
            }

            return links;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to restore stored payload links from history");
            return new List<ChatPayloadLink>();
        }
    }

    /// <summary>Loads the most recent <paramref name="limit"/> messages for a routing key, oldest first.</summary>
    public List<ChatMessageRecord> LoadRecent(string routingKey, int limit = 500)
    {
        // Capped independently of the SQL LIMIT below - this is a List<T> *initial capacity* hint,
        // not a row count, and callers exporting "everything" pass int.MaxValue as limit (no
        // practical upper bound on rows they want back). Pre-sizing the list's backing array to over
        // two billion elements throws immediately, regardless of how many rows the query actually
        // returns - capping the hint avoids that while still helping the common small-limit case.
        var results = new List<ChatMessageRecord>(Math.Min(limit, 1024));
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, timestamp_utc, chat_type, sender_name, sender_key, body, payload_links, translation, translation_lang
            FROM messages
            WHERE routing_key = $routingKey
            ORDER BY id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$routingKey", routingKey);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ChatMessageRecord
            {
                Id = reader.GetInt64(0),
                TimestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).UtcDateTime,
                ChatType = (XivChatType)reader.GetInt32(2),
                SenderName = reader.GetString(3),
                SenderKey = reader.GetString(4),
                Body = reader.GetString(5),
                RoutingKey = routingKey,
                PayloadLinks = DeserializePayloadLinks(reader.IsDBNull(6) ? null : reader.GetString(6)),
                PersistedTranslation = reader.IsDBNull(7) ? null : reader.GetString(7),
                PersistedTranslationLanguage = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        results.Reverse();
        return results;
    }

    /// <summary>Persists a completed translation so it doesn't need to be re-requested from the
    /// translation endpoint the next time this message is loaded (a tab reopen, or a plugin restart) -
    /// called by <see cref="TranslationService"/> after a successful fetch. Matched by
    /// (routing_key, timestamp_utc, sender_key, body) rather than <see cref="ChatMessageRecord.Id"/>,
    /// which is still 0 at this point for a just-captured live message (the background writer may not
    /// have flushed it yet) - see that field's own doc comment. A no-op if the row hasn't actually
    /// been written yet (rare timing case: translation finished faster than the batched history
    /// write) - the translation still displays for the rest of this session either way via
    /// TranslationService's own in-memory cache, it just won't survive a reload this one time. Opens
    /// its own connection rather than touching <see cref="writerConnection"/> - this runs on
    /// TranslationService's own background worker thread, not the history writer's.</summary>
    public void SaveTranslation(ChatMessageRecord record, string translatedText, string targetLanguage)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                UPDATE messages
                SET translation = $translation, translation_lang = $lang
                WHERE routing_key = $routingKey AND timestamp_utc = $timestamp AND sender_key = $senderKey AND body = $body;
                """;
            cmd.Parameters.AddWithValue("$translation", translatedText);
            cmd.Parameters.AddWithValue("$lang", targetLanguage);
            cmd.Parameters.AddWithValue("$routingKey", record.RoutingKey);
            cmd.Parameters.AddWithValue("$timestamp", new DateTimeOffset(record.TimestampUtc).ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$senderKey", record.SenderKey);
            cmd.Parameters.AddWithValue("$body", record.Body);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to persist a translation to history");
        }
    }

    /// <summary>Generic guess (schema overhead - id/routing_key/timestamp/chat_type/sender columns -
    /// plus a short chat line) used only until there's real stored data to measure from instead - see
    /// <see cref="EstimateAverageBytesPerMessage"/>.</summary>
    private const double FallbackAverageBytesPerMessage = 200d;

    /// <summary>Rough on-disk bytes-per-message average, computed from the *actual* current database
    /// (current file size on disk / total row count) rather than a fixed guess - backs the "≈N
    /// messages fit in this many MiB" estimate next to the history size slider in Settings > General,
    /// nothing else depends on this being precise. Falls back to <see cref="FallbackAverageBytesPerMessage"/>
    /// when there's no data yet to measure from (fresh install, or right after "Clear history"). Opens
    /// its own short-lived read-only connection rather than touching <see cref="writerConnection"/> -
    /// same reasoning as <see cref="LoadRecent"/> (that connection belongs to the background writer
    /// thread; WAL mode allows a concurrent reader like this one without contention).</summary>
    public double EstimateAverageBytesPerMessage()
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            long count;
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM messages;";
                count = Convert.ToInt64(countCmd.ExecuteScalar());
            }

            if (count <= 0)
                return FallbackAverageBytesPerMessage;

            var fileInfo = new FileInfo(dbPath);
            var walInfo = new FileInfo(dbPath + "-wal");
            var currentSize = (fileInfo.Exists ? fileInfo.Length : 0) + (walInfo.Exists ? walInfo.Length : 0);

            return currentSize > 0 ? (double)currentSize / count : FallbackAverageBytesPerMessage;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "CustomChat: failed to estimate average message size");
            return FallbackAverageBytesPerMessage;
        }
    }

    /// <summary>Deletes the oldest rows in batches until the database file is back under <see cref="maxBytes"/>.</summary>
    private void EnforceSizeCap()
    {
        try
        {
            var fileInfo = new FileInfo(dbPath);
            var walInfo = new FileInfo(dbPath + "-wal");
            long CurrentSize() => (fileInfo.Exists ? fileInfo.Length : 0) + (walInfo.Exists ? walInfo.Length : 0);

            fileInfo.Refresh();
            walInfo.Refresh();
            if (CurrentSize() <= maxBytes)
                return;

            log.Information("CustomChat: history size {Size} bytes exceeds cap {Max} bytes, pruning oldest messages", CurrentSize(), maxBytes);

            const int batchSize = 2000;
            const int maxIterations = 500;
            var iterations = 0;
            while (iterations++ < maxIterations)
            {
                using (var deleteCmd = writerConnection.CreateCommand())
                {
                    deleteCmd.CommandText = """
                        DELETE FROM messages WHERE id IN (
                            SELECT id FROM messages ORDER BY id ASC LIMIT $batch
                        );
                        """;
                    deleteCmd.Parameters.AddWithValue("$batch", batchSize);
                    var deleted = deleteCmd.ExecuteNonQuery();
                    if (deleted == 0)
                        break; // Nothing left to delete but still over cap - can't do more.
                }

                using (var vacuumCmd = writerConnection.CreateCommand())
                {
                    vacuumCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum;";
                    vacuumCmd.ExecuteNonQuery();
                }

                fileInfo.Refresh();
                walInfo.Refresh();
                if (CurrentSize() <= maxBytes)
                    break;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "CustomChat: failed to enforce history size cap");
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        pending.Writer.TryComplete();
        try
        {
            writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Best-effort flush on shutdown.
        }

        rotationTimer.Dispose();
        writerConnection.Dispose();
        cts.Dispose();
    }
}
