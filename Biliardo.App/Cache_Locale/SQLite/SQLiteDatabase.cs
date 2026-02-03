using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public static class SQLiteDatabase
    {
        private static readonly object Gate = new();
        private static bool _initialized;

        public static string DbPath => Path.Combine(FileSystem.AppDataDirectory, "biliardo_cache.sqlite");

        public static void EnsureCreated()
        {
            lock (Gate)
            {
                if (_initialized)
                    return;

                var dir = Path.GetDirectoryName(DbPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                using var conn = OpenConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;

CREATE TABLE IF NOT EXISTS Chats (
    ChatId TEXT PRIMARY KEY,
    PeerUid TEXT NOT NULL,
    LastMessageId TEXT,
    LastMessageText TEXT,
    LastMessageType TEXT,
    LastMessageAtUtc TEXT,
    UnreadCount INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    ServerTimestamp TEXT
);
CREATE INDEX IF NOT EXISTS IX_Chats_UpdatedAtUtc ON Chats(UpdatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS Messages (
    ChatId TEXT NOT NULL,
    MessageId TEXT NOT NULL,
    SenderId TEXT NOT NULL,
    Text TEXT,
    MediaKey TEXT,
    CreatedAtUtc TEXT NOT NULL,
    ServerTimestamp TEXT,
    PRIMARY KEY(ChatId, MessageId)
);
CREATE INDEX IF NOT EXISTS IX_Messages_Chat_CreatedAtUtc ON Messages(ChatId, CreatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS HomeFeed (
    PostId TEXT PRIMARY KEY,
    AuthorName TEXT,
    AuthorFullName TEXT,
    AuthorUid TEXT,
    AuthorNickname TEXT,
    AuthorFirstName TEXT,
    AuthorLastName TEXT,
    AuthorAvatarPath TEXT,
    AuthorAvatarUrl TEXT,
    Text TEXT,
    ThumbKey TEXT,
    CreatedAtUtc TEXT NOT NULL,
    SchemaVersion INTEGER,
    Ready INTEGER,
    Deleted INTEGER,
    DeletedAtUtc TEXT,
    LikeCount INTEGER,
    CommentCount INTEGER,
    ShareCount INTEGER,
    RepostOfPostId TEXT,
    ClientNonce TEXT,
    AttachmentsJson TEXT,
    ServerTimestamp TEXT
);
CREATE INDEX IF NOT EXISTS IX_HomeFeed_CreatedAtUtc ON HomeFeed(CreatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS HomeIndex (
    PostId TEXT PRIMARY KEY,
    CreatedAtUtc TEXT NOT NULL,
    ServerTimestamp TEXT
);
CREATE INDEX IF NOT EXISTS IX_HomeIndex_CreatedAtUtc ON HomeIndex(CreatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS Profiles (
    Uid TEXT PRIMARY KEY,
    Nickname TEXT,
    FirstName TEXT,
    LastName TEXT,
    PhotoUrl TEXT,
    PhotoLocalPath TEXT,
    UpdatedAtUtc TEXT NOT NULL,
    ServerTimestamp TEXT
);

CREATE TABLE IF NOT EXISTS MediaCache (
    CacheKey TEXT PRIMARY KEY,
    Sha256 TEXT NOT NULL UNIQUE,
    Kind TEXT NOT NULL,
    LocalPath TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL,
    LastAccessUtc TEXT NOT NULL,
    ServerTimestamp TEXT
);
CREATE INDEX IF NOT EXISTS IX_MediaCache_LastAccessUtc ON MediaCache(LastAccessUtc ASC);

CREATE TABLE IF NOT EXISTS MediaAliases (
    AliasKey TEXT PRIMARY KEY,
    CacheKey TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS MissingContentQueue (
    ContentId TEXT NOT NULL,
    Kind TEXT NOT NULL,
    PayloadJson TEXT NOT NULL,
    Priority INTEGER NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    RetryCount INTEGER NOT NULL,
    LastAttemptUtc TEXT,
    PRIMARY KEY(ContentId, Kind)
);
CREATE INDEX IF NOT EXISTS IX_MissingContentQueue_CreatedAtUtc ON MissingContentQueue(CreatedAtUtc DESC);
";
                cmd.ExecuteNonQuery();

                EnsureChatColumns(conn);
                EnsureHomeFeedColumns(conn);
                _initialized = true;
            }
        }

        private static void EnsureChatColumns(SqliteConnection conn)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(Chats);";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(1));
            }

            AddColumnIfMissing(conn, "Chats", existing, "LastMessageText", "TEXT");
            AddColumnIfMissing(conn, "Chats", existing, "LastMessageType", "TEXT");
            AddColumnIfMissing(conn, "Chats", existing, "LastMessageAtUtc", "TEXT");
        }

        private static void EnsureHomeFeedColumns(SqliteConnection conn)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA table_info(HomeFeed);";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                    existing.Add(reader.GetString(1));
            }

            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorUid", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorNickname", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorFirstName", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorLastName", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorAvatarPath", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AuthorAvatarUrl", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "SchemaVersion", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "Ready", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "Deleted", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "DeletedAtUtc", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "LikeCount", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "CommentCount", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "ShareCount", "INTEGER");
            AddColumnIfMissing(conn, "HomeFeed", existing, "RepostOfPostId", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "ClientNonce", "TEXT");
            AddColumnIfMissing(conn, "HomeFeed", existing, "AttachmentsJson", "TEXT");
        }

        private static void AddColumnIfMissing(SqliteConnection conn, string table, HashSet<string> existing, string column, string type)
        {
            if (existing.Contains(column))
                return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            cmd.ExecuteNonQuery();
        }

        public static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }
    }
}
