using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public sealed class MediaCacheStore
    {
        public sealed record MediaRow(
            string CacheKey,
            string Sha256,
            string Kind,
            string LocalPath,
            long SizeBytes,
            DateTimeOffset LastAccessUtc);

        public async Task<MediaRow?> GetByCacheKeyAsync(string cacheKey, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT CacheKey, Sha256, Kind, LocalPath, SizeBytes, LastAccessUtc
FROM MediaCache WHERE CacheKey = $cacheKey LIMIT 1;";
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new MediaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        }

        public async Task<MediaRow?> GetByShaAsync(string sha256, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT CacheKey, Sha256, Kind, LocalPath, SizeBytes, LastAccessUtc
FROM MediaCache WHERE Sha256 = $sha LIMIT 1;";
            cmd.Parameters.AddWithValue("$sha", sha256);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new MediaRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                DateTimeOffset.Parse(reader.GetString(5)));
        }

        public async Task UpsertMediaAsync(MediaRow row, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO MediaCache(CacheKey, Sha256, Kind, LocalPath, SizeBytes, LastAccessUtc)
VALUES ($cacheKey, $sha, $kind, $localPath, $sizeBytes, $lastAccess)
ON CONFLICT(CacheKey) DO UPDATE SET
    Sha256 = excluded.Sha256,
    Kind = excluded.Kind,
    LocalPath = excluded.LocalPath,
    SizeBytes = excluded.SizeBytes,
    LastAccessUtc = excluded.LastAccessUtc;";
            cmd.Parameters.AddWithValue("$cacheKey", row.CacheKey);
            cmd.Parameters.AddWithValue("$sha", row.Sha256);
            cmd.Parameters.AddWithValue("$kind", row.Kind);
            cmd.Parameters.AddWithValue("$localPath", row.LocalPath);
            cmd.Parameters.AddWithValue("$sizeBytes", row.SizeBytes);
            cmd.Parameters.AddWithValue("$lastAccess", row.LastAccessUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task TouchAsync(string cacheKey, DateTimeOffset whenUtc, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE MediaCache SET LastAccessUtc = $lastAccess WHERE CacheKey = $cacheKey;";
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            cmd.Parameters.AddWithValue("$lastAccess", whenUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<long> GetTotalBytesAsync(CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(SizeBytes), 0) FROM MediaCache;";
            var res = await cmd.ExecuteScalarAsync(ct);
            return res == null ? 0 : Convert.ToInt64(res);
        }

        public async Task<IReadOnlyList<MediaRow>> ListOldestAsync(int limit, CancellationToken ct)
        {
            var list = new List<MediaRow>();
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT CacheKey, Sha256, Kind, LocalPath, SizeBytes, LastAccessUtc
FROM MediaCache
ORDER BY LastAccessUtc ASC
LIMIT $limit;";
            cmd.Parameters.AddWithValue("$limit", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new MediaRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    DateTimeOffset.Parse(reader.GetString(5))));
            }
            return list;
        }

        public async Task DeleteAsync(string cacheKey, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MediaCache WHERE CacheKey = $cacheKey;";
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task UpsertAliasAsync(string aliasKey, string cacheKey, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO MediaAliases(AliasKey, CacheKey)
VALUES ($aliasKey, $cacheKey)
ON CONFLICT(AliasKey) DO UPDATE SET CacheKey = excluded.CacheKey;";
            cmd.Parameters.AddWithValue("$aliasKey", aliasKey);
            cmd.Parameters.AddWithValue("$cacheKey", cacheKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<string?> ResolveAliasAsync(string aliasKey, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CacheKey FROM MediaAliases WHERE AliasKey = $aliasKey LIMIT 1;";
            cmd.Parameters.AddWithValue("$aliasKey", aliasKey);
            var res = await cmd.ExecuteScalarAsync(ct);
            return res == null ? null : res.ToString();
        }
    }
}
