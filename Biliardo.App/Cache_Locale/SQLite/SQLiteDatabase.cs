using System;
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
    UnreadCount INTEGER NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Chats_UpdatedAtUtc ON Chats(UpdatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS Messages (
    ChatId TEXT NOT NULL,
    MessageId TEXT NOT NULL,
    SenderId TEXT NOT NULL,
    Text TEXT,
    MediaKey TEXT,
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY(ChatId, MessageId)
);
CREATE INDEX IF NOT EXISTS IX_Messages_Chat_CreatedAtUtc ON Messages(ChatId, CreatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS HomeFeed (
    PostId TEXT PRIMARY KEY,
    AuthorName TEXT,
    Text TEXT,
    ThumbKey TEXT,
    CreatedAtUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_HomeFeed_CreatedAtUtc ON HomeFeed(CreatedAtUtc DESC);

CREATE TABLE IF NOT EXISTS Profiles (
    Uid TEXT PRIMARY KEY,
    Nickname TEXT,
    FirstName TEXT,
    LastName TEXT,
    PhotoUrl TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS MediaCache (
    CacheKey TEXT PRIMARY KEY,
    Sha256 TEXT NOT NULL UNIQUE,
    Kind TEXT NOT NULL,
    LocalPath TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL,
    LastAccessUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MediaCache_LastAccessUtc ON MediaCache(LastAccessUtc ASC);

CREATE TABLE IF NOT EXISTS MediaAliases (
    AliasKey TEXT PRIMARY KEY,
    CacheKey TEXT NOT NULL
);
";
                cmd.ExecuteNonQuery();
                _initialized = true;
            }
        }

        public static SqliteConnection OpenConnection()
        {
            var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            return conn;
        }
    }
}
