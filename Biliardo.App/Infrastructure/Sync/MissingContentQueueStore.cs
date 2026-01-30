using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.SQLite;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Infrastructure.Sync
{
    public sealed class MissingContentQueueStore
    {
        public sealed record MissingContentItem(
            string ContentId,
            string Kind,
            string PayloadJson,
            int Priority,
            DateTimeOffset CreatedAtUtc,
            int RetryCount,
            DateTimeOffset? LastAttemptUtc);

        public async Task EnqueueAsync(MissingContentItem item, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO MissingContentQueue(ContentId, Kind, PayloadJson, Priority, CreatedAtUtc, RetryCount, LastAttemptUtc)
VALUES ($contentId, $kind, $payload, $priority, $createdAt, $retryCount, $lastAttempt)
ON CONFLICT(ContentId, Kind) DO UPDATE SET
    PayloadJson = excluded.PayloadJson,
    Priority = excluded.Priority,
    CreatedAtUtc = excluded.CreatedAtUtc,
    RetryCount = excluded.RetryCount,
    LastAttemptUtc = excluded.LastAttemptUtc;";
            cmd.Parameters.AddWithValue("$contentId", item.ContentId);
            cmd.Parameters.AddWithValue("$kind", item.Kind);
            cmd.Parameters.AddWithValue("$payload", item.PayloadJson);
            cmd.Parameters.AddWithValue("$priority", item.Priority);
            cmd.Parameters.AddWithValue("$createdAt", item.CreatedAtUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$retryCount", item.RetryCount);
            cmd.Parameters.AddWithValue("$lastAttempt", item.LastAttemptUtc?.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<MissingContentItem>> ListPendingAsync(int limit, CancellationToken ct)
        {
            var list = new List<MissingContentItem>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ContentId, Kind, PayloadJson, Priority, CreatedAtUtc, RetryCount, LastAttemptUtc
FROM MissingContentQueue
ORDER BY Priority DESC, CreatedAtUtc ASC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MissingContentItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6))));
            }
            return list;
        }

        public async Task RemoveAsync(string contentId, string kind, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MissingContentQueue WHERE ContentId = $contentId AND Kind = $kind;";
            cmd.Parameters.AddWithValue("$contentId", contentId);
            cmd.Parameters.AddWithValue("$kind", kind);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public static string SerializePayload(IReadOnlyDictionary<string, string> data)
            => JsonSerializer.Serialize(data);
    }
}
