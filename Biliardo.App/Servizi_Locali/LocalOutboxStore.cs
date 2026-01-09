// === File: Biliardo.App/Servizi_Locali/LocalOutboxStore.cs =====================
// Scopo: persistenza dei messaggi "in coda" (non ancora confermati dal server).
// - Un file JSON per utente locale: outbox_{myUserId}.json
// - Ogni voce rappresenta un messaggio da me -> altro utente.
// - Esteso per supportare media (audio ora; video/documenti nei prossimi step).
// ==============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Servizi_Locali
{
    public sealed class OutboxEntry
    {
        public string Id { get; set; } = "";
        public string FromUserId { get; set; } = "";
        public string ToUserId { get; set; } = "";

        // "text" | "audio" | "video" | "file" | "gif" | "sticker" ...
        public string Kind { get; set; } = "text";

        // Testo (solo per Kind="text")
        public string Text { get; set; } = "";

        // Per media: path file locale da caricare (audio/video/file)
        public string LocalFilePath { get; set; } = "";

        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public long SizeBytes { get; set; }
        public long DurationMs { get; set; } // audio/video

        public DateTimeOffset CreatedUtc { get; set; }
    }

    public static class LocalOutboxStore
    {
        private static readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        private static readonly SemaphoreSlim _lock = new(1, 1);

        private static string GetFilePath(string myUserId)
        {
            var safeId = string.IsNullOrWhiteSpace(myUserId) ? "unknown" : myUserId;
            return Path.Combine(FileSystem.AppDataDirectory, $"outbox_{safeId}.json");
        }

        private static async Task<List<OutboxEntry>> LoadAllAsync(string myUserId, CancellationToken ct = default)
        {
            var path = GetFilePath(myUserId);

            if (!File.Exists(path))
                return new List<OutboxEntry>();

            try
            {
                await _lock.WaitAsync(ct);
                var json = await File.ReadAllTextAsync(path, ct);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<OutboxEntry>();

                var list = JsonSerializer.Deserialize<List<OutboxEntry>>(json, _jsonOpts);
                return list ?? new List<OutboxEntry>();
            }
            catch
            {
                return new List<OutboxEntry>();
            }
            finally
            {
                if (_lock.CurrentCount == 0)
                    _lock.Release();
            }
        }

        private static async Task SaveAllAsync(string myUserId, List<OutboxEntry> entries, CancellationToken ct = default)
        {
            var path = GetFilePath(myUserId);

            await _lock.WaitAsync(ct);
            try
            {
                if (entries == null || entries.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    return;
                }

                var json = JsonSerializer.Serialize(entries, _jsonOpts);
                await File.WriteAllTextAsync(path, json, ct);
            }
            finally
            {
                if (_lock.CurrentCount == 0)
                    _lock.Release();
            }
        }

        public static async Task<List<OutboxEntry>> LoadForPeerAsync(
            string myUserId,
            string peerUserId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(myUserId) || string.IsNullOrWhiteSpace(peerUserId))
                return new List<OutboxEntry>();

            var all = await LoadAllAsync(myUserId, ct);

            return all
                .Where(e => e.FromUserId == myUserId && e.ToUserId == peerUserId)
                .OrderBy(e => e.CreatedUtc)
                .ToList();
        }

        public static async Task AddAsync(
            string myUserId,
            OutboxEntry entry,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(myUserId) || entry == null)
                return;

            var all = await LoadAllAsync(myUserId, ct);

            if (!string.IsNullOrWhiteSpace(entry.Id) &&
                all.Exists(e => e.Id == entry.Id))
            {
                return;
            }

            all.Add(entry);
            await SaveAllAsync(myUserId, all, ct);
        }

        public static async Task RemoveAsync(
            string myUserId,
            string entryId,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(myUserId) || string.IsNullOrWhiteSpace(entryId))
                return;

            var all = await LoadAllAsync(myUserId, ct);

            var removed = all.RemoveAll(e => e.Id == entryId);
            if (removed > 0)
            {
                await SaveAllAsync(myUserId, all, ct);
            }
        }
    }
}
