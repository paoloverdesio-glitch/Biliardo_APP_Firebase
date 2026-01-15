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

namespace Biliardo.App.Infrastructure
{
    public sealed class ChatLocalCache
    {
        private const int SchemaVersion = 1;

        // ============================================================
        // REGISTRAZIONE "SICURA" (CACHE) = NO CORRUZIONE / NO RACE
        // - lock I/O per evitare Read/Write concorrenti nello stesso processo
        // - write su file .tmp + commit con replace/move (pattern anti-corruzione)
        // - fallback robusti per piattaforme diverse (Android/Windows)
        // ============================================================
        private readonly SemaphoreSlim _ioLock = new(1, 1);

        public string GetCacheKey(string? chatId, string peerId)
        {
            if (!string.IsNullOrWhiteSpace(chatId))
                return $"chat_{chatId}".Trim();

            return $"peer_{peerId}".Trim();
        }

        public async Task<IReadOnlyList<FirestoreChatService.MessageItem>> TryReadAsync(string cacheKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return Array.Empty<FirestoreChatService.MessageItem>();

            var path = GetCachePath(cacheKey);
            if (!File.Exists(path))
                return Array.Empty<FirestoreChatService.MessageItem>();

            await _ioLock.WaitAsync(ct);
            try
            {
                // FileShare.ReadWrite: consente lettura anche se qualche altro thread sta aprendo il file.
                // Con commit atomico (tmp -> replace/move) si minimizzano i casi sporchi.
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                var payload = await JsonSerializer.DeserializeAsync<CachePayload>(stream, cancellationToken: ct);

                if (payload == null)
                    return Array.Empty<FirestoreChatService.MessageItem>();

                // Se cambia schema in futuro: ignora cache vecchia.
                if (payload.Version != SchemaVersion)
                    return Array.Empty<FirestoreChatService.MessageItem>();

                if (payload.Messages == null || payload.Messages.Count == 0)
                    return Array.Empty<FirestoreChatService.MessageItem>();

                return payload.Messages.Select(MapFromCache).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLocalCache] Read failed: {ex.Message}");
                return Array.Empty<FirestoreChatService.MessageItem>();
            }
            finally
            {
                _ioLock.Release();
            }
        }

        public async Task WriteAsync(string cacheKey, IReadOnlyList<FirestoreChatService.MessageItem> messages, int maxItems, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return;

            if (messages == null || messages.Count == 0)
                return;

            await _ioLock.WaitAsync(ct);
            try
            {
                var trimmed = messages
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .Take(Math.Max(1, maxItems))
                    .OrderBy(m => m.CreatedAtUtc)
                    .Select(MapToCache)
                    .ToList();

                var payload = new CachePayload
                {
                    Version = SchemaVersion,
                    SavedAtUtc = DateTimeOffset.UtcNow,
                    Messages = trimmed
                };

                var path = GetCachePath(cacheKey);
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrWhiteSpace(dir))
                    return;

                Directory.CreateDirectory(dir);

                // Write su file temporaneo nello stesso folder (commit più affidabile)
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

                        // Su alcune piattaforme Flush(true) può non essere supportato -> fallback silenzioso.
                        try { stream.Flush(flushToDisk: true); } catch { }
                    }

                    CommitReplace(tmpPath, path, bakPath);
                }
                finally
                {
                    // Se per qualche motivo il tmp non è stato spostato, lo elimino.
                    TryDeleteFile(tmpPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLocalCache] Write failed: {ex.Message}");
            }
            finally
            {
                _ioLock.Release();
            }
        }

        private static void CommitReplace(string tmpPath, string finalPath, string backupPath)
        {
            // 1) Provo File.Replace (quando supportato). È la scelta migliore dove disponibile.
            // 2) Fallback a File.Move(overwrite:true).
            try
            {
                if (File.Exists(finalPath))
                {
                    try
                    {
                        File.Replace(tmpPath, finalPath, backupPath, ignoreMetadataErrors: true);
                        return;
                    }
                    catch
                    {
                        // PlatformNotSupportedException o altre: fallback sotto
                    }
                }

                File.Move(tmpPath, finalPath, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLocalCache] Commit failed: {ex.Message}");

                // Ultimo tentativo: se esiste il final, provo a cancellare e spostare.
                try
                {
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);

                    File.Move(tmpPath, finalPath);
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine($"[ChatLocalCache] Commit fallback failed: {ex2.Message}");
                }
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

        private static string GetCachePath(string cacheKey)
        {
            var safeKey = string.Join("_", cacheKey.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            return Path.Combine(FileSystem.AppDataDirectory, "chat_cache", $"{safeKey}.json");
        }

        private static CacheMessage MapToCache(FirestoreChatService.MessageItem m)
        {
            return new CacheMessage
            {
                MessageId = m.MessageId,
                SenderId = m.SenderId,
                Type = m.Type,
                Text = m.Text,
                CreatedAtUtc = m.CreatedAtUtc,
                DeliveredTo = m.DeliveredTo?.ToArray() ?? Array.Empty<string>(),
                ReadBy = m.ReadBy?.ToArray() ?? Array.Empty<string>(),
                StoragePath = m.StoragePath,
                DurationMs = m.DurationMs,
                FileName = m.FileName,
                ContentType = m.ContentType,
                SizeBytes = m.SizeBytes,
                Latitude = m.Latitude,
                Longitude = m.Longitude,
                ContactName = m.ContactName,
                ContactPhone = m.ContactPhone
            };
        }

        private static FirestoreChatService.MessageItem MapFromCache(CacheMessage m)
        {
            return new FirestoreChatService.MessageItem(
                MessageId: m.MessageId ?? "",
                SenderId: m.SenderId ?? "",
                Type: m.Type ?? "text",
                Text: m.Text ?? "",
                CreatedAtUtc: m.CreatedAtUtc,
                DeliveredTo: m.DeliveredTo ?? Array.Empty<string>(),
                ReadBy: m.ReadBy ?? Array.Empty<string>(),
                StoragePath: m.StoragePath,
                DurationMs: m.DurationMs,
                FileName: m.FileName,
                ContentType: m.ContentType,
                SizeBytes: m.SizeBytes,
                Latitude: m.Latitude,
                Longitude: m.Longitude,
                ContactName: m.ContactName,
                ContactPhone: m.ContactPhone
            );
        }

        private sealed class CachePayload
        {
            public int Version { get; set; }
            public DateTimeOffset SavedAtUtc { get; set; }
            public List<CacheMessage>? Messages { get; set; }
        }

        private sealed class CacheMessage
        {
            public string? MessageId { get; set; }
            public string? SenderId { get; set; }
            public string? Type { get; set; }
            public string? Text { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string[]? DeliveredTo { get; set; }
            public string[]? ReadBy { get; set; }
            public string? StoragePath { get; set; }
            public long DurationMs { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? ContactName { get; set; }
            public string? ContactPhone { get; set; }
        }
    }
}
