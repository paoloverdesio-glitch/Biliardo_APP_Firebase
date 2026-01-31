using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure
{
    public sealed class ChatLocalCache
    {
        public sealed record ChatCacheSummary(string ChatId, int MessageCount, long Bytes);

        private readonly ChatCacheStore _store = new();

        public string GetCacheKey(string? chatId, string peerId)
        {
            if (!string.IsNullOrWhiteSpace(chatId))
                return chatId.Trim();

            return $"peer:{peerId}".Trim();
        }

        public async Task<IReadOnlyList<FirestoreChatService.MessageItem>> TryReadAsync(string cacheKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return Array.Empty<FirestoreChatService.MessageItem>();

            var rows = await _store.ListRecentMessagesAsync(cacheKey, limit: 30, ct);
            var list = new List<FirestoreChatService.MessageItem>(rows.Count);
            foreach (var row in rows)
                list.Add(MapFromRow(row));

            return list;
        }

        public async Task WriteAsync(string cacheKey, IReadOnlyList<FirestoreChatService.MessageItem> messages, int maxItems, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || messages == null || messages.Count == 0)
                return;

            var rows = new List<ChatCacheStore.MessageRow>();
            foreach (var m in messages)
                rows.Add(MapToRow(cacheKey, m));

            await _store.UpsertMessagesAsync(rows, ct);
            await _store.TrimChatMessagesAsync(cacheKey, maxItems, ct);
        }

        public async Task UpsertAppendAsync(string cacheKey, IEnumerable<FirestoreChatService.MessageItem> incoming, int maxItems, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || incoming == null)
                return;

            var rows = new List<ChatCacheStore.MessageRow>();
            foreach (var m in incoming)
                rows.Add(MapToRow(cacheKey, m));

            await _store.UpsertMessagesAsync(rows, ct);
            await _store.TrimChatMessagesAsync(cacheKey, maxItems, ct);
        }

        public async Task<IReadOnlyList<ChatCacheSummary>> ListSummariesAsync(CancellationToken ct)
        {
            var list = new List<ChatCacheSummary>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ChatId, COUNT(*), COALESCE(SUM(LENGTH(Text)), 0)
FROM Messages
GROUP BY ChatId
ORDER BY ChatId;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ChatCacheSummary(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt64(2)));
            }
            return list;
        }

        private static ChatCacheStore.MessageRow MapToRow(string chatId, FirestoreChatService.MessageItem m)
        {
            return new ChatCacheStore.MessageRow(
                chatId,
                m.MessageId ?? Guid.NewGuid().ToString("N"),
                m.SenderId ?? "",
                m.Text,
                m.StoragePath,
                m.CreatedAtUtc);
        }

        private static FirestoreChatService.MessageItem MapFromRow(ChatCacheStore.MessageRow row)
        {
            var type = string.IsNullOrWhiteSpace(row.Text) ? "file" : "text";
            return new FirestoreChatService.MessageItem(
                MessageId: row.MessageId,
                SenderId: row.SenderId,
                Type: type,
                Text: row.Text ?? "",
                CreatedAtUtc: row.CreatedAtUtc,
                DeliveredTo: Array.Empty<string>(),
                ReadBy: Array.Empty<string>(),
                DeletedForAll: false,
                DeletedFor: Array.Empty<string>(),
                DeletedAtUtc: null,
                StoragePath: row.MediaKey,
                DurationMs: 0,
                FileName: null,
                ContentType: null,
                SizeBytes: 0,
                ThumbStoragePath: null,
                LqipBase64: null,
                ThumbWidth: null,
                ThumbHeight: null,
                PreviewType: null,
                Waveform: null,
                Latitude: null,
                Longitude: null,
                ContactName: null,
                ContactPhone: null);
        }
    }
}
