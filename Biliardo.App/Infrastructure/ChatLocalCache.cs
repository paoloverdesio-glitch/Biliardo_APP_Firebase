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

            try
            {
                await using var stream = File.OpenRead(path);
                var payload = await JsonSerializer.DeserializeAsync<CachePayload>(stream, cancellationToken: ct);
                if (payload?.Messages == null)
                    return Array.Empty<FirestoreChatService.MessageItem>();

                return payload.Messages.Select(MapFromCache).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLocalCache] Read failed: {ex.Message}");
                return Array.Empty<FirestoreChatService.MessageItem>();
            }
        }

        public async Task WriteAsync(string cacheKey, IReadOnlyList<FirestoreChatService.MessageItem> messages, int maxItems, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
                return;

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
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                await using var stream = File.Create(path);
                await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLocalCache] Write failed: {ex.Message}");
            }
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
