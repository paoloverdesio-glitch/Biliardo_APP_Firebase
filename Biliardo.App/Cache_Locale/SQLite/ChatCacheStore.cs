using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public sealed class ChatCacheStore
    {
        public sealed record ChatRow(string ChatId, string PeerUid, string? LastMessageId, int UnreadCount, DateTimeOffset UpdatedAtUtc);
        public sealed record MessageRow(string ChatId, string MessageId, string SenderId, string? Text, string? MediaKey, DateTimeOffset CreatedAtUtc);

        public async Task UpsertChatAsync(ChatRow chat, CancellationToken ct)
        {
            if (chat == null || string.IsNullOrWhiteSpace(chat.ChatId) || string.IsNullOrWhiteSpace(chat.PeerUid))
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO Chats(ChatId, PeerUid, LastMessageId, UnreadCount, UpdatedAtUtc)
VALUES ($chatId, $peerUid, $lastMessageId, $unreadCount, $updatedAtUtc)
ON CONFLICT(ChatId) DO UPDATE SET
    PeerUid = excluded.PeerUid,
    LastMessageId = excluded.LastMessageId,
    UnreadCount = excluded.UnreadCount,
    UpdatedAtUtc = excluded.UpdatedAtUtc;";
            cmd.Parameters.AddWithValue("$chatId", chat.ChatId);
            cmd.Parameters.AddWithValue("$peerUid", chat.PeerUid);
            cmd.Parameters.AddWithValue("$lastMessageId", (object?)chat.LastMessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$unreadCount", chat.UnreadCount);
            cmd.Parameters.AddWithValue("$updatedAtUtc", chat.UpdatedAtUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<IReadOnlyList<ChatRow>> ListChatsAsync(CancellationToken ct)
        {
            var list = new List<ChatRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ChatId, PeerUid, LastMessageId, UnreadCount, UpdatedAtUtc FROM Chats ORDER BY UpdatedAtUtc DESC;";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new ChatRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4))));
            }
            return list;
        }

        public async Task<ChatRow?> GetChatAsync(string chatId, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ChatId, PeerUid, LastMessageId, UnreadCount, UpdatedAtUtc FROM Chats WHERE ChatId = $chatId LIMIT 1;";
            cmd.Parameters.AddWithValue("$chatId", chatId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new ChatRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3),
                DateTimeOffset.Parse(reader.GetString(4)));
        }

        public async Task ResetUnreadAsync(string chatId, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Chats SET UnreadCount = 0 WHERE ChatId = $chatId;";
            cmd.Parameters.AddWithValue("$chatId", chatId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpsertMessagesAsync(IEnumerable<MessageRow> messages, CancellationToken ct)
        {
            if (messages == null)
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var tx = await conn.BeginTransactionAsync(ct);
            foreach (var msg in messages)
            {
                if (msg == null || string.IsNullOrWhiteSpace(msg.ChatId) || string.IsNullOrWhiteSpace(msg.MessageId))
                    continue;

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO Messages(ChatId, MessageId, SenderId, Text, MediaKey, CreatedAtUtc)
VALUES ($chatId, $messageId, $senderId, $text, $mediaKey, $createdAtUtc)
ON CONFLICT(ChatId, MessageId) DO UPDATE SET
    SenderId = excluded.SenderId,
    Text = excluded.Text,
    MediaKey = excluded.MediaKey,
    CreatedAtUtc = excluded.CreatedAtUtc;";
                cmd.Parameters.AddWithValue("$chatId", msg.ChatId);
                cmd.Parameters.AddWithValue("$messageId", msg.MessageId);
                cmd.Parameters.AddWithValue("$senderId", msg.SenderId);
                cmd.Parameters.AddWithValue("$text", (object?)msg.Text ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$mediaKey", (object?)msg.MediaKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$createdAtUtc", msg.CreatedAtUtc.UtcDateTime.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        public async Task<IReadOnlyList<MessageRow>> ListRecentMessagesAsync(string chatId, int limit, CancellationToken ct)
        {
            var list = new List<MessageRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ChatId, MessageId, SenderId, Text, MediaKey, CreatedAtUtc
FROM Messages
WHERE ChatId = $chatId
ORDER BY CreatedAtUtc DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$chatId", chatId);
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MessageRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5))));
            }
            return list;
        }

        public async Task<IReadOnlyList<MessageRow>> ListMessagesBeforeAsync(string chatId, DateTimeOffset beforeUtc, int limit, CancellationToken ct)
        {
            var list = new List<MessageRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ChatId, MessageId, SenderId, Text, MediaKey, CreatedAtUtc
FROM Messages
WHERE ChatId = $chatId AND CreatedAtUtc < $before
ORDER BY CreatedAtUtc DESC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$chatId", chatId);
            cmd.Parameters.AddWithValue("$before", beforeUtc.UtcDateTime.ToString("O"));
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MessageRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5))));
            }
            return list;
        }

        public async Task<MessageRow?> GetLatestMessageAsync(string chatId, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT ChatId, MessageId, SenderId, Text, MediaKey, CreatedAtUtc
FROM Messages
WHERE ChatId = $chatId
ORDER BY CreatedAtUtc DESC
LIMIT 1;";
            cmd.Parameters.AddWithValue("$chatId", chatId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new MessageRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        }
    }
}
