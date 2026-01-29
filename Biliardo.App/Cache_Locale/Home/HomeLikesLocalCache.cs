using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Biliardo.App.Utilita;

namespace Biliardo.App.Cache_Locale.Home
{
    public sealed class HomeLikesLocalCache : IDisposable
    {
        private const int SchemaVersion = 1;
        private readonly SemaphoreSlim _ioLock = new(1, 1);
        private readonly DebounceAsync _debounce = new();
        private readonly object _memLock = new();
        private readonly Dictionary<string, HashSet<string>> _memSets = new(StringComparer.Ordinal);

        public async Task<HashSet<string>> LoadAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return new HashSet<string>(StringComparer.Ordinal);

            lock (_memLock)
            {
                if (_memSets.TryGetValue(uid, out var cached))
                    return new HashSet<string>(cached, StringComparer.Ordinal);
            }

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(uid, ct);
                var set = payload?.LikedPostIds != null
                    ? new HashSet<string>(payload.LikedPostIds, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);

                lock (_memLock)
                {
                    _memSets[uid] = new HashSet<string>(set, StringComparer.Ordinal);
                }

                return new HashSet<string>(set, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeLikesLocalCache] Read failed: {ex.Message}");
                return new HashSet<string>(StringComparer.Ordinal);
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task SaveAsync(string uid, HashSet<string> likedSet, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid) || likedSet == null)
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = new CachePayload
                {
                    Version = SchemaVersion,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    LikedPostIds = new List<string>(likedSet)
                };

                await WritePayloadAsync(uid, payload, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeLikesLocalCache] Write failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public Task SetLiked(string uid, string postId, bool isLiked)
        {
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(postId))
                return Task.CompletedTask;

            HashSet<string> set;
            lock (_memLock)
            {
                if (!_memSets.TryGetValue(uid, out set!))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _memSets[uid] = set;
                }

                if (isLiked)
                    set.Add(postId);
                else
                    set.Remove(postId);
            }

            var snapshot = new HashSet<string>(set, StringComparer.Ordinal);
            return _debounce.RunAsync(_ => SaveAsync(uid, snapshot, CancellationToken.None), TimeSpan.FromMilliseconds(350));
        }

        private static string GetPath(string uid)
            => Path.Combine(FileSystem.AppDataDirectory, $"home_likes_{uid}_v1.json");

        private async Task<CachePayload?> ReadPayloadAsync(string uid, CancellationToken ct)
        {
            var path = GetPath(uid);
            if (!File.Exists(path))
                return null;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var payload = await JsonSerializer.DeserializeAsync<CachePayload>(stream, cancellationToken: ct);
            if (payload == null || payload.Version != SchemaVersion)
                return null;

            return payload;
        }

        private async Task WritePayloadAsync(string uid, CachePayload payload, CancellationToken ct)
        {
            var path = GetPath(uid);
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
                return;

            Directory.CreateDirectory(dir);
            var tmpPath = path + ".tmp";
            var bakPath = path + ".bak";

            try
            {
                await using (var stream = new FileStream(
                    tmpPath,
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

                CommitReplace(tmpPath, path, bakPath);
            }
            finally
            {
                TryDeleteFile(tmpPath);
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
                Debug.WriteLine($"[HomeLikesLocalCache] Commit failed: {ex.Message}");
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

        public void Dispose()
        {
            _debounce.Dispose();
            try { _ioLock.Dispose(); } catch { }
        }

        private sealed class CachePayload
        {
            public int Version { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
            public List<string>? LikedPostIds { get; set; }
        }
    }
}
