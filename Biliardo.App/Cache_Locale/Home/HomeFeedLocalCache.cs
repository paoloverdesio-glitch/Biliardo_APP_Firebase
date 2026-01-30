using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Cache_Locale.Home
{
    public sealed class HomeFeedLocalCache
    {
        private const int SchemaVersion = 1;
        private const int MaxItems = 200;
        private const string FileName = "home_feed_cache_v1.json";

        private readonly SemaphoreSlim _ioLock = new(1, 1);

        public async Task<IReadOnlyList<CachedHomePost>> LoadAsync(CancellationToken ct = default)
        {
            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(ct);
                return payload?.Posts ?? new List<CachedHomePost>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeFeedLocalCache] Read failed: {ex.Message}");
                return Array.Empty<CachedHomePost>();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task SaveAsync(IReadOnlyList<CachedHomePost> posts, CancellationToken ct = default)
        {
            if (posts == null || posts.Count == 0)
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = new CachePayload
                {
                    Version = SchemaVersion,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Posts = Trim(posts)
                };

                await WritePayloadAsync(payload, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeFeedLocalCache] Write failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task UpsertTop(CachedHomePost post, CancellationToken ct = default)
        {
            if (post == null || string.IsNullOrWhiteSpace(post.PostId))
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(ct) ?? new CachePayload
                {
                    Version = SchemaVersion,
                    Posts = new List<CachedHomePost>()
                };

                var list = payload.Posts ?? new List<CachedHomePost>();
                list = MergeInternal(list, new[] { post });

                payload.Version = SchemaVersion;
                payload.SavedAtUtc = DateTimeOffset.UtcNow;
                payload.Posts = list;

                await WritePayloadAsync(payload, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeFeedLocalCache] Upsert failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task MergeNewTop(IEnumerable<CachedHomePost> newOnes, CancellationToken ct = default)
        {
            if (newOnes == null)
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var payload = await ReadPayloadAsync(ct) ?? new CachePayload
                {
                    Version = SchemaVersion,
                    Posts = new List<CachedHomePost>()
                };

                var list = payload.Posts ?? new List<CachedHomePost>();
                list = MergeInternal(list, newOnes);

                payload.Version = SchemaVersion;
                payload.SavedAtUtc = DateTimeOffset.UtcNow;
                payload.Posts = list;

                await WritePayloadAsync(payload, ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HomeFeedLocalCache] Merge failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static List<CachedHomePost> MergeInternal(IEnumerable<CachedHomePost> existing, IEnumerable<CachedHomePost> incoming)
        {
            var byPostId = new Dictionary<string, CachedHomePost>(StringComparer.Ordinal);
            var byNonce = new Dictionary<string, CachedHomePost>(StringComparer.Ordinal);

            foreach (var post in existing ?? Array.Empty<CachedHomePost>())
            {
                if (post == null) continue;
                if (!string.IsNullOrWhiteSpace(post.PostId))
                    byPostId[post.PostId] = post;
                if (!string.IsNullOrWhiteSpace(post.ClientNonce))
                    byNonce[post.ClientNonce] = post;
            }

            foreach (var post in incoming ?? Array.Empty<CachedHomePost>())
            {
                if (post == null) continue;

                if (!string.IsNullOrWhiteSpace(post.ClientNonce) && byNonce.TryGetValue(post.ClientNonce, out var existingNonce))
                {
                    byPostId.Remove(existingNonce.PostId);
                    byPostId[post.PostId] = post;
                    byNonce[post.ClientNonce] = post;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(post.PostId))
                    byPostId[post.PostId] = post;

                if (!string.IsNullOrWhiteSpace(post.ClientNonce))
                    byNonce[post.ClientNonce] = post;
            }

            var merged = byPostId.Values
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaxItems)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            return merged;
        }

        private static List<CachedHomePost> Trim(IReadOnlyList<CachedHomePost> posts)
        {
            return posts
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(MaxItems)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();
        }

        private static string GetPath() => Path.Combine(FileSystem.AppDataDirectory, FileName);

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
                Debug.WriteLine($"[HomeFeedLocalCache] Commit failed: {ex.Message}");
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

        public sealed class CachedHomePost
        {
            public string PostId { get; set; } = "";
            public string? ClientNonce { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string AuthorUid { get; set; } = "";
            public string AuthorNickname { get; set; } = "";
            public string? AuthorAvatarPath { get; set; }
            public string? AuthorAvatarUrl { get; set; }
            public string Text { get; set; } = "";
            public List<CachedHomeAttachment> Attachments { get; set; } = new();
            public int LikeCount { get; set; }
            public int CommentCount { get; set; }
            public int ShareCount { get; set; }
            public bool PendingUpload { get; set; }
            public bool SendError { get; set; }
        }

        public sealed class CachedHomeAttachment
        {
            public string Type { get; set; } = "";
            public string? StoragePath { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }
            public Dictionary<string, object>? Extra { get; set; }
            public string? ThumbStoragePath { get; set; }
            public string? LqipBase64 { get; set; }
            public string? PreviewType { get; set; }
            public int? ThumbWidth { get; set; }
            public int? ThumbHeight { get; set; }
            public List<int>? Waveform { get; set; }
        }

        private sealed class CachePayload
        {
            public int Version { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
            public List<CachedHomePost>? Posts { get; set; }
        }

        public static CachedHomeAttachment FromAttachment(FirestoreHomeFeedService.HomeAttachment attachment)
        {
            return new CachedHomeAttachment
            {
                Type = attachment.Type,
                StoragePath = attachment.StoragePath,
                DownloadUrl = attachment.DownloadUrl,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                SizeBytes = attachment.SizeBytes,
                DurationMs = attachment.DurationMs,
                Extra = attachment.Extra,
                ThumbStoragePath = attachment.ThumbStoragePath,
                LqipBase64 = attachment.LqipBase64,
                PreviewType = attachment.PreviewType,
                ThumbWidth = attachment.ThumbWidth,
                ThumbHeight = attachment.ThumbHeight,
                Waveform = attachment.Waveform?.ToList()
            };
        }

        public static FirestoreHomeFeedService.HomeAttachment ToAttachment(CachedHomeAttachment attachment)
        {
            return new FirestoreHomeFeedService.HomeAttachment(
                Type: attachment.Type,
                StoragePath: attachment.StoragePath,
                DownloadUrl: attachment.DownloadUrl,
                FileName: attachment.FileName,
                ContentType: attachment.ContentType,
                SizeBytes: attachment.SizeBytes,
                DurationMs: attachment.DurationMs,
                Extra: attachment.Extra,
                ThumbStoragePath: attachment.ThumbStoragePath,
                LqipBase64: attachment.LqipBase64,
                PreviewType: attachment.PreviewType,
                ThumbWidth: attachment.ThumbWidth,
                ThumbHeight: attachment.ThumbHeight,
                Waveform: attachment.Waveform);
        }
    }
}
