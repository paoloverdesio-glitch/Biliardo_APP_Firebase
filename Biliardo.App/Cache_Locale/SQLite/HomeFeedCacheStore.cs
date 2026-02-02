using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public sealed class HomeFeedCacheStore
    {
        public sealed record HomePostRow(
            string PostId,
            string? AuthorName,
            string? AuthorFullName,
            string? AuthorUid,
            string? AuthorNickname,
            string? AuthorFirstName,
            string? AuthorLastName,
            string? AuthorAvatarPath,
            string? AuthorAvatarUrl,
            string? Text,
            string? ThumbKey,
            DateTimeOffset CreatedAtUtc,
            int SchemaVersion,
            bool Ready,
            bool Deleted,
            DateTimeOffset? DeletedAtUtc,
            int LikeCount,
            int CommentCount,
            int ShareCount,
            string? RepostOfPostId,
            string? ClientNonce,
            string? AttachmentsJson);

        public async Task UpsertPostAsync(HomePostRow post, CancellationToken ct)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.PostId))
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO HomeFeed(PostId, AuthorName, AuthorFullName, AuthorUid, AuthorNickname, AuthorFirstName, AuthorLastName, AuthorAvatarPath, AuthorAvatarUrl, Text, ThumbKey, CreatedAtUtc, SchemaVersion, Ready, Deleted, DeletedAtUtc, LikeCount, CommentCount, ShareCount, RepostOfPostId, ClientNonce, AttachmentsJson)
VALUES ($postId, $authorName, $authorFullName, $authorUid, $authorNickname, $authorFirstName, $authorLastName, $authorAvatarPath, $authorAvatarUrl, $text, $thumbKey, $createdAtUtc, $schemaVersion, $ready, $deleted, $deletedAtUtc, $likeCount, $commentCount, $shareCount, $repostOfPostId, $clientNonce, $attachmentsJson)
ON CONFLICT(PostId) DO UPDATE SET
    AuthorName = excluded.AuthorName,
    AuthorFullName = excluded.AuthorFullName,
    AuthorUid = excluded.AuthorUid,
    AuthorNickname = excluded.AuthorNickname,
    AuthorFirstName = excluded.AuthorFirstName,
    AuthorLastName = excluded.AuthorLastName,
    AuthorAvatarPath = excluded.AuthorAvatarPath,
    AuthorAvatarUrl = excluded.AuthorAvatarUrl,
    Text = excluded.Text,
    ThumbKey = excluded.ThumbKey,
    CreatedAtUtc = excluded.CreatedAtUtc,
    SchemaVersion = excluded.SchemaVersion,
    Ready = excluded.Ready,
    Deleted = excluded.Deleted,
    DeletedAtUtc = excluded.DeletedAtUtc,
    LikeCount = excluded.LikeCount,
    CommentCount = excluded.CommentCount,
    ShareCount = excluded.ShareCount,
    RepostOfPostId = excluded.RepostOfPostId,
    ClientNonce = excluded.ClientNonce,
    AttachmentsJson = excluded.AttachmentsJson;";
            cmd.Parameters.AddWithValue("$postId", post.PostId);
            cmd.Parameters.AddWithValue("$authorName", (object?)post.AuthorName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorFullName", (object?)post.AuthorFullName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorUid", (object?)post.AuthorUid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorNickname", (object?)post.AuthorNickname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorFirstName", (object?)post.AuthorFirstName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorLastName", (object?)post.AuthorLastName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorAvatarPath", (object?)post.AuthorAvatarPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorAvatarUrl", (object?)post.AuthorAvatarUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$text", (object?)post.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumbKey", (object?)post.ThumbKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$createdAtUtc", post.CreatedAtUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$schemaVersion", post.SchemaVersion);
            cmd.Parameters.AddWithValue("$ready", post.Ready ? 1 : 0);
            cmd.Parameters.AddWithValue("$deleted", post.Deleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$deletedAtUtc", post.DeletedAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("$likeCount", post.LikeCount);
            cmd.Parameters.AddWithValue("$commentCount", post.CommentCount);
            cmd.Parameters.AddWithValue("$shareCount", post.ShareCount);
            cmd.Parameters.AddWithValue("$repostOfPostId", (object?)post.RepostOfPostId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clientNonce", (object?)post.ClientNonce ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$attachmentsJson", (object?)post.AttachmentsJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<HomePostRow>> ListPostsAsync(int limit, CancellationToken ct)
        {
            var list = new List<HomePostRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT PostId, AuthorName, AuthorFullName, AuthorUid, AuthorNickname, AuthorFirstName, AuthorLastName, AuthorAvatarPath, AuthorAvatarUrl, Text, ThumbKey, CreatedAtUtc, SchemaVersion, Ready, Deleted, DeletedAtUtc, LikeCount, CommentCount, ShareCount, RepostOfPostId, ClientNonce, AttachmentsJson
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
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    DateTimeOffset.Parse(reader.GetString(11)),
                    reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                    !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                    !reader.IsDBNull(14) && reader.GetInt32(14) == 1,
                    reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
                    reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                    reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                    reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21))));
            }
            return list;
        }

        public async Task<IReadOnlyList<HomePostRow>> ListPostsBeforeAsync(DateTimeOffset beforeUtc, int limit, CancellationToken ct)
        {
            var list = new List<HomePostRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT PostId, AuthorName, AuthorFullName, AuthorUid, AuthorNickname, AuthorFirstName, AuthorLastName, AuthorAvatarPath, AuthorAvatarUrl, Text, ThumbKey, CreatedAtUtc, SchemaVersion, Ready, Deleted, DeletedAtUtc, LikeCount, CommentCount, ShareCount, RepostOfPostId, ClientNonce, AttachmentsJson
FROM HomeFeed
WHERE CreatedAtUtc < $before
ORDER BY CreatedAtUtc DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$before", beforeUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new HomePostRow(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    DateTimeOffset.Parse(reader.GetString(11)),
                    reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                    !reader.IsDBNull(13) && reader.GetInt32(13) == 1,
                    !reader.IsDBNull(14) && reader.GetInt32(14) == 1,
                    reader.IsDBNull(15) ? null : DateTimeOffset.Parse(reader.GetString(15)),
                    reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                    reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                    reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21))));
            }
            return list;
        }

        public async Task TrimOldestAsync(int maxItems, CancellationToken ct)
        {
            if (maxItems <= 0)
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM HomeFeed
WHERE PostId IN (
    SELECT PostId
    FROM HomeFeed
    ORDER BY CreatedAtUtc DESC
    LIMIT -1 OFFSET $maxItems
);";
            cmd.Parameters.AddWithValue("$maxItems", maxItems);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
#if DEBUG
            if (affected > 0)
                Debug.WriteLine($"[HomeFeedCacheStore] TrimOldest removed={affected}");
#endif
        }
    }
}
