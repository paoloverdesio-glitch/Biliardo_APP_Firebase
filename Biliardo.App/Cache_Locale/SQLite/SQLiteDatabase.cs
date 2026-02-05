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
