using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.Storage;

using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure.Media.Cache
{
    public sealed class MediaCacheService
    {
        public sealed record MediaCacheEntry(
            string FileName,
            string LocalPath,
            long Bytes,
            DateTimeOffset AddedAtUtc,
            DateTimeOffset LastAccessUtc,
            bool IsThumb,
            string StoragePath);

        private readonly string _root;
        private readonly string _indexPath;
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly Dictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private CacheIndex _index = new();

        public MediaCacheService()
        {
            _root = Path.Combine(FileSystem.CacheDirectory, "media_cache");
            _indexPath = Path.Combine(_root, "media_cache_index.json");
            _downloadSemaphore = new SemaphoreSlim(AppMediaOptions.DownloadConcurrency, AppMediaOptions.DownloadConcurrency);
            Directory.CreateDirectory(_root);
            LoadIndex();
        }

        public async Task<string?> GetOrDownloadAsync(string idToken, string storagePath, string fileName, bool isThumb, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(storagePath))
                return null;

            var key = BuildEntryKey(storagePath, isThumb);

            if (TryGetCachedPathInternal(key, out var cachedPath))
                return cachedPath;

            Task<string?> task;
            lock (_lock)
            {
                if (!_inflight.TryGetValue(key, out task!))
                {
                    task = DownloadAndStoreAsync(key, idToken, storagePath, fileName, isThumb, ct);
                    _inflight[key] = task;
                }
            }

            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                lock (_lock)
                {
                    _inflight.Remove(key);
                }
            }
        }

        public Task<string?> TryGetCachedPathAsync(string storagePath, bool isThumb)
        {
            var key = BuildEntryKey(storagePath, isThumb);
            return Task.FromResult(TryGetCachedPathInternal(key, out var path) ? path : null);
        }

        public Task TouchAsync(string storagePath, bool isThumb)
        {
            var key = BuildEntryKey(storagePath, isThumb);
            lock (_lock)
            {
                if (_index.Entries.TryGetValue(key, out var entry))
                {
                    entry.LastAccessUtc = DateTimeOffset.UtcNow;
                    SaveIndex();
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MediaCacheEntry>> ListEntriesAsync()
        {
            lock (_lock)
            {
                var list = _index.Entries.Values
                    .Select(e => new MediaCacheEntry(
                        FileName: Path.GetFileName(e.LocalPath),
                        LocalPath: e.LocalPath,
                        Bytes: e.SizeBytes,
                        AddedAtUtc: e.AddedAtUtc,
                        LastAccessUtc: e.LastAccessUtc,
                        IsThumb: e.IsThumb,
                        StoragePath: e.StoragePath))
                    .OrderByDescending(e => e.AddedAtUtc)
                    .ToList();

                return Task.FromResult<IReadOnlyList<MediaCacheEntry>>(list);
            }
        }

        public Task<long> GetTotalBytesAsync()
        {
            lock (_lock)
            {
                var total = _index.Entries.Values.Sum(x => x.SizeBytes);
                return Task.FromResult(total);
            }
        }

        public Task PruneIfNeededAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                var total = _index.Entries.Values.Sum(x => x.SizeBytes);
                if (total <= AppMediaOptions.CacheMaxBytes)
                    return Task.CompletedTask;

                foreach (var entry in _index.Entries.Values.OrderBy(x => x.AddedAtUtc).ToList())
                {
                    if (ct.IsCancellationRequested)
                        break;

                    TryDelete(entry);
                    total -= entry.SizeBytes;
                    _index.Entries.Remove(entry.Key);

                    if (total <= AppMediaOptions.CacheMaxBytes)
                        break;
                }

                SaveIndex();
            }

            return Task.CompletedTask;
        }

        private bool TryGetCachedPathInternal(string key, out string? path)
        {
            lock (_lock)
            {
                if (_index.Entries.TryGetValue(key, out var entry) && File.Exists(entry.LocalPath))
                {
                    entry.LastAccessUtc = DateTimeOffset.UtcNow;
                    SaveIndex();
                    path = entry.LocalPath;
                    return true;
                }
            }

            path = null;
            return false;
        }

        private async Task<string?> DownloadAndStoreAsync(string key, string idToken, string storagePath, string fileName, bool isThumb, CancellationToken ct)
        {
            await _downloadSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ext = isThumb ? ".jpg" : NormalizeExtension(fileName);
                var localPath = Path.Combine(_root, $"{key}{ext}");

                if (File.Exists(localPath))
                {
                    UpdateIndexEntry(key, storagePath, localPath, isThumb);
                    return localPath;
                }

                await FirebaseStorageRestClient.DownloadToFileAsync(idToken, storagePath, localPath, ct).ConfigureAwait(false);

                UpdateIndexEntry(key, storagePath, localPath, isThumb);
                await PruneIfNeededAsync(ct).ConfigureAwait(false);

                return localPath;
            }
            catch
            {
                return null;
            }
            finally
            {
                _downloadSemaphore.Release();
            }
        }

        private void UpdateIndexEntry(string key, string storagePath, string localPath, bool isThumb)
        {
            var size = File.Exists(localPath) ? new FileInfo(localPath).Length : 0;
            lock (_lock)
            {
                if (_index.Entries.TryGetValue(key, out var existing))
                {
                    existing.StoragePath = storagePath;
                    existing.LocalPath = localPath;
                    existing.SizeBytes = size;
                    existing.IsThumb = isThumb;
                    existing.LastAccessUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    _index.Entries[key] = new CacheEntry
                    {
                        Key = key,
                        StoragePath = storagePath,
                        LocalPath = localPath,
                        SizeBytes = size,
                        IsThumb = isThumb,
                        AddedAtUtc = DateTimeOffset.UtcNow,
                        LastAccessUtc = DateTimeOffset.UtcNow
                    };
                }

                SaveIndex();
            }
        }

        private void LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    _index = new CacheIndex();
                    return;
                }

                var json = File.ReadAllText(_indexPath);
                _index = JsonSerializer.Deserialize<CacheIndex>(json) ?? new CacheIndex();
            }
            catch
            {
                _index = new CacheIndex();
            }
        }

        private void SaveIndex()
        {
            try
            {
                var json = JsonSerializer.Serialize(_index, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_indexPath, json);
            }
            catch
            {
                // ignore
            }
        }

        private static string BuildEntryKey(string storagePath, bool isThumb)
        {
            var hash = ComputeSha1(storagePath ?? string.Empty);
            return isThumb ? $"{hash}_thumb" : $"{hash}_orig";
        }

        private static string ComputeSha1(string input)
        {
            using var sha = SHA1.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string NormalizeExtension(string? fileName)
        {
            var ext = Path.GetExtension(fileName ?? "");
            if (string.IsNullOrWhiteSpace(ext))
                return ".bin";

            return ext.ToLowerInvariant();
        }

        private static void TryDelete(CacheEntry entry)
        {
            try
            {
                if (File.Exists(entry.LocalPath))
                    File.Delete(entry.LocalPath);
            }
            catch
            {
                // ignore
            }
        }

        private sealed class CacheIndex
        {
            public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
        }

        private sealed class CacheEntry
        {
            public string Key { get; set; } = "";
            public string StoragePath { get; set; } = "";
            public string LocalPath { get; set; } = "";
            public long SizeBytes { get; set; }
            public bool IsThumb { get; set; }
            public DateTimeOffset AddedAtUtc { get; set; } = DateTimeOffset.UtcNow;
            public DateTimeOffset LastAccessUtc { get; set; }
        }
    }
}
