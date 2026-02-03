using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Servizi_Firebase;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Infrastructure.Media.Cache
{
    public sealed class MediaCacheService
    {
        public sealed record MediaRegistration(string CacheKey, string LocalPath);

        public sealed record MediaCacheEntry(
            string CacheKey,
            string LocalPath,
            long SizeBytes,
            string Kind,
            DateTimeOffset LastAccessUtc);

        private readonly string _root;
        private readonly SemaphoreSlim _downloadSemaphore;
        private readonly Dictionary<string, Task<string?>> _inflight = new(StringComparer.Ordinal);
        private readonly object _lock = new();
        private readonly MediaCacheStore _store = new();

        public MediaCacheService()
        {
            _root = Path.Combine(FileSystem.AppDataDirectory, "media_cache");
            Directory.CreateDirectory(_root);
            _downloadSemaphore = new SemaphoreSlim(AppMediaOptions.DownloadConcurrency, AppMediaOptions.DownloadConcurrency);
        }

        public async Task<string?> GetOrDownloadAsync(string idToken, string storagePath, string fileName, bool isThumb, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(storagePath))
                return null;

            var cacheKey = BuildCacheKey(storagePath, isThumb);
            var resolvedKey = await ResolveCacheKeyAsync(cacheKey, ct);
            var cached = await _store.GetByCacheKeyAsync(resolvedKey, ct);
            if (cached != null && File.Exists(cached.LocalPath))
            {
                await _store.TouchAsync(cached.CacheKey, DateTimeOffset.UtcNow, ct);
                return cached.LocalPath;
            }

            Task<string?> task;
            lock (_lock)
            {
                if (!_inflight.TryGetValue(cacheKey, out task!))
                {
                    task = DownloadAndStoreAsync(cacheKey, idToken, storagePath, fileName, isThumb, ct);
                    _inflight[cacheKey] = task;
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
                    _inflight.Remove(cacheKey);
                }
            }
        }

        public async Task<string?> TryGetCachedPathAsync(string storagePath, bool isThumb)
        {
            var cacheKey = BuildCacheKey(storagePath, isThumb);
            var resolvedKey = await ResolveCacheKeyAsync(cacheKey, CancellationToken.None);
            var cached = await _store.GetByCacheKeyAsync(resolvedKey, CancellationToken.None);
            if (cached == null || !File.Exists(cached.LocalPath))
                return null;

            await _store.TouchAsync(cached.CacheKey, DateTimeOffset.UtcNow, CancellationToken.None);
            return cached.LocalPath;
        }

        public async Task<MediaRegistration?> RegisterLocalFileAsync(string localPath, string kind, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                return null;

            var sha = await ComputeSha256Async(localPath, ct);
            var localKey = $"local:{Guid.NewGuid():N}";
            var existing = await _store.GetByShaAsync(sha, ct);
            if (existing != null && File.Exists(existing.LocalPath))
            {
                await _store.TouchAsync(existing.CacheKey, DateTimeOffset.UtcNow, ct);
                await _store.UpsertAliasAsync(localKey, existing.CacheKey, ct);
                return new MediaRegistration(localKey, existing.LocalPath);
            }

            var ext = Path.GetExtension(localPath);
            var fileName = $"{sha}{ext}";
            var destPath = Path.Combine(_root, fileName);
            if (!string.Equals(localPath, destPath, StringComparison.Ordinal))
                File.Copy(localPath, destPath, overwrite: true);

            var size = new FileInfo(destPath).Length;

            await _store.UpsertMediaAsync(new MediaCacheStore.MediaRow(
                localKey,
                sha,
                kind,
                destPath,
                size,
                DateTimeOffset.UtcNow), ct);

            await PruneIfNeededAsync(ct);
            return new MediaRegistration(localKey, destPath);
        }

        public Task RegisterAliasAsync(string aliasKey, string cacheKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(aliasKey) || string.IsNullOrWhiteSpace(cacheKey))
                return Task.CompletedTask;

            return _store.UpsertAliasAsync(aliasKey, cacheKey, ct);
        }

        public async Task<IReadOnlyList<MediaCacheEntry>> ListEntriesAsync(CancellationToken ct)
        {
            var rows = await _store.ListOldestAsync(1000, ct);
            var list = new List<MediaCacheEntry>(rows.Count);
            foreach (var row in rows)
                list.Add(new MediaCacheEntry(row.CacheKey, row.LocalPath, row.SizeBytes, row.Kind, row.LastAccessUtc));
            return list;
        }

        public Task<long> GetTotalBytesAsync(CancellationToken ct)
            => _store.GetTotalBytesAsync(ct);

        private async Task<string?> DownloadAndStoreAsync(string cacheKey, string idToken, string storagePath, string fileName, bool isThumb, CancellationToken ct)
        {
            await _downloadSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ext = isThumb ? ".jpg" : NormalizeExtension(fileName);
                var tempPath = Path.Combine(_root, $"{Guid.NewGuid():N}{ext}");
                await FirebaseStorageRestClient.DownloadToFileAsync(idToken, storagePath, tempPath, ct).ConfigureAwait(false);

                var sha = await ComputeSha256Async(tempPath, ct).ConfigureAwait(false);
                var existing = await _store.GetByShaAsync(sha, ct).ConfigureAwait(false);
                if (existing != null && File.Exists(existing.LocalPath))
                {
                    TryDelete(tempPath);
                    await _store.UpsertAliasAsync(cacheKey, existing.CacheKey, ct).ConfigureAwait(false);
                    await _store.TouchAsync(existing.CacheKey, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                    return existing.LocalPath;
                }

                var finalPath = Path.Combine(_root, $"{sha}{ext}");
                if (!string.Equals(tempPath, finalPath, StringComparison.Ordinal))
                {
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    File.Move(tempPath, finalPath);
                }

                var size = new FileInfo(finalPath).Length;
                await _store.UpsertMediaAsync(new MediaCacheStore.MediaRow(
                    cacheKey,
                    sha,
                    isThumb ? "thumb" : "file",
                    finalPath,
                    size,
                    DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

                await PruneIfNeededAsync(ct).ConfigureAwait(false);
                return finalPath;
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

        private async Task<string> ResolveCacheKeyAsync(string cacheKey, CancellationToken ct)
        {
            var alias = await _store.ResolveAliasAsync(cacheKey, ct);
            return string.IsNullOrWhiteSpace(alias) ? cacheKey : alias;
        }

        private async Task PruneIfNeededAsync(CancellationToken ct)
        {
            var total = await _store.GetTotalBytesAsync(ct);
            if (total <= AppMediaOptions.CacheMaxBytes)
                return;

            var oldest = await _store.ListOldestAsync(100, ct);
            foreach (var row in oldest)
            {
                if (total <= AppMediaOptions.CacheMaxBytes)
                    break;

                if (File.Exists(row.LocalPath))
                {
                    TryDelete(row.LocalPath);
                    await _store.DeleteAsync(row.CacheKey, ct);
                    total -= row.SizeBytes;
                }
            }
        }

        private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
        {
            await using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(stream, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string BuildCacheKey(string storagePath, bool isThumb)
            => isThumb ? $"thumb:{storagePath}" : storagePath;

        private static string NormalizeExtension(string? fileName)
        {
            var ext = Path.GetExtension(fileName ?? "");
            return string.IsNullOrWhiteSpace(ext) ? ".bin" : ext.ToLowerInvariant();
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
