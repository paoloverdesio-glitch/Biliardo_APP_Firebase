using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Biliardo.App.Cache_Locale.SQLite
{
    public sealed class ProfileCacheStore
    {
        public sealed record ProfileRow(
            string Uid,
            string? Nickname,
            string? FirstName,
            string? LastName,
            string? PhotoUrl,
            string? PhotoLocalPath,
            DateTimeOffset UpdatedAtUtc);

        public async Task UpsertProfileAsync(ProfileRow profile, CancellationToken ct)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Uid))
                return;

            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO Profiles(Uid, Nickname, FirstName, LastName, PhotoUrl, PhotoLocalPath, UpdatedAtUtc)
VALUES ($uid, $nickname, $firstName, $lastName, $photoUrl, $photoLocalPath, $updatedAtUtc)
ON CONFLICT(Uid) DO UPDATE SET
    Nickname = excluded.Nickname,
    FirstName = excluded.FirstName,
    LastName = excluded.LastName,
    PhotoUrl = excluded.PhotoUrl,
    PhotoLocalPath = excluded.PhotoLocalPath,
    UpdatedAtUtc = excluded.UpdatedAtUtc;";
            cmd.Parameters.AddWithValue("$uid", profile.Uid);
            cmd.Parameters.AddWithValue("$nickname", (object?)profile.Nickname ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$firstName", (object?)profile.FirstName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lastName", (object?)profile.LastName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$photoUrl", (object?)profile.PhotoUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$photoLocalPath", (object?)profile.PhotoLocalPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updatedAtUtc", profile.UpdatedAtUtc.UtcDateTime.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<ProfileRow?> GetProfileAsync(string uid, CancellationToken ct)
        {
            await using var conn = SQLiteDatabase.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Uid, Nickname, FirstName, LastName, PhotoUrl, PhotoLocalPath, UpdatedAtUtc FROM Profiles WHERE Uid = $uid LIMIT 1;";
            cmd.Parameters.AddWithValue("$uid", uid);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            return new ProfileRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)));
        }
    }
}
