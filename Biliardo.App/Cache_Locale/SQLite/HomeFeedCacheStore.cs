using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public sealed class HomeFeedCacheStore
    {
        public sealed record HomePostRow(string PostId, string? AuthorName, string? Text, string? ThumbKey, DateTimeOffset CreatedAtUtc);

        public async Task UpsertPostAsync(HomePostRow post, CancellationToken ct)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.PostId))
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO HomeFeed(PostId, AuthorName, Text, ThumbKey, CreatedAtUtc)
VALUES ($postId, $authorName, $text, $thumbKey, $createdAtUtc)
ON CONFLICT(PostId) DO UPDATE SET
    AuthorName = excluded.AuthorName,
    Text = excluded.Text,
    ThumbKey = excluded.ThumbKey,
    CreatedAtUtc = excluded.CreatedAtUtc;";
            cmd.Parameters.AddWithValue("$postId", post.PostId);
            cmd.Parameters.AddWithValue("$authorName", (object?)post.AuthorName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", (object?)post.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumbKey", (object?)post.ThumbKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAtUtc", post.CreatedAtUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<HomePostRow>> ListPostsAsync(int limit, CancellationToken ct)
        {
            var list = new List<HomePostRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT PostId, AuthorName, Text, ThumbKey, CreatedAtUtc
FROM HomeFeed
ORDER BY CreatedAtUtc DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new HomePostRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4))));
            }
            return list;
        }
    }
}
