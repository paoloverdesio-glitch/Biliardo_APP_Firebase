using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Cache_Locale.Profili
{
    public sealed class UserPublicLocalCache
    {
        private const int SchemaVersion = 1;
        private const string FileName = "user_public_cache_v1.json";

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        public async Task<FirestoreDirectoryService.UserPublicItem?> TryGetAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return null;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(ct);
                if (payload?.Items == null)
                    return null;

                if (!payload.Items.TryGetValue(uid.Trim(), out var item))
                    return null;

                return new FirestoreDirectoryService.UserPublicItem
                {
                    Uid = item.Uid,
                    Nickname = item.Nickname,
                    NicknameLower = item.NicknameLower,
                    FirstName = item.FirstName,
                    LastName = item.LastName,
                    PhotoUrl = item.PhotoUrl
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UserPublicLocalCache] Read failed: {ex.Message}");
                return null;
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task UpsertAsync(string uid, FirestoreDirectoryService.UserPublicItem data, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid) || data == null)
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(ct) ?? new CachePayload
                {
                    Version = SchemaVersion,
                    Items = new Dictionary<string, CacheUser>(StringComparer.Ordinal)
                };

                payload.Version = SchemaVersion;
                payload.SavedAtUtc = DateTimeOffset.UtcNow;
                payload.Items ??= new Dictionary<string, CacheUser>(StringComparer.Ordinal);

                payload.Items[uid.Trim()] = new CacheUser
                {
                    Uid = uid.Trim(),
                    Nickname = data.Nickname ?? "",
                    NicknameLower = data.NicknameLower ?? "",
                    FirstName = data.FirstName ?? "",
                    LastName = data.LastName ?? "",
                    PhotoUrl = data.PhotoUrl ?? ""
                };

                await WritePayloadAsync(payload, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UserPublicLocalCache] Write failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static string GetPath() => Path.Combine(FileSystem.AppDataDirectory, FileName);

        private static string GetTmpPath(string path) => path + ".tmp";

        private static string GetBakPath(string path) => path + ".bak";

        private async Task<CachePayload?> ReadPayloadAsync(CancellationToken ct)
        {
            var path = GetPath();
            if (!File.Exists(path))
                return null;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var payload = await JsonSerializer.DeserializeAsync<CachePayload>(stream, cancellationToken: ct);
            if (payload == null || payload.Version != SchemaVersion)
                return null;

            return payload;
        }

        private async Task WritePayloadAsync(CachePayload payload, CancellationToken ct)
        {
            var path = GetPath();
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
                return;

            Directory.CreateDirectory(dir);

            var tmp = GetTmpPath(path);
            var bak = GetBakPath(path);

            try
            {
                await using (var stream = new FileStream(
                    tmp,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    options: FileOptions.None))
                {
                    await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct);
                    await stream.FlushAsync(ct);
                    try { stream.Flush(flushToDisk: true); } catch { }
                }

                CommitReplace(tmp, path, bak);
            }
            finally
            {
                TryDeleteFile(tmp);
            }
        }

        private static void CommitReplace(string tmpPath, string finalPath, string backupPath)
        {
            try
            {
                if (File.Exists(finalPath))
                {
                    try
                    {
                        File.Replace(tmpPath, finalPath, backupPath, ignoreMetadataErrors: true);
                        return;
                    }
                    catch { }
                }

                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UserPublicLocalCache] Commit failed: {ex.Message}");
                try
                {
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    File.Move(tmpPath, finalPath);
                }
                catch { }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private sealed class CachePayload
        {
            public int Version { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
            public Dictionary<string, CacheUser>? Items { get; set; }
        }

        private sealed class CacheUser
        {
            public string Uid { get; set; } = "";
            public string Nickname { get; set; } = "";
            public string NicknameLower { get; set; } = "";
            public string FirstName { get; set; } = "";
            public string LastName { get; set; } = "";
            public string PhotoUrl { get; set; } = "";
        }
    }
}
