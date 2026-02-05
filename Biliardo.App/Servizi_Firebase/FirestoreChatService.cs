using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plugin.Firebase.Firestore;

namespace Biliardo.App.Servizi_Firebase
{
    public sealed class FirestoreChatService
    {
        private readonly string _projectId;
        private readonly IFirebaseFirestore _db;

        public FirestoreChatService(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId non valido.", nameof(projectId));

            _projectId = projectId.Trim();
            _db = CrossFirebaseFirestore.Current;
        }

        public sealed record ChatItem(
            string ChatId,
            string PeerUid,
            string PeerNickname,
            string LastText,
            string LastType,
            DateTimeOffset? LastAtUtc,
            DateTimeOffset? UpdatedAtUtc,
            bool IsPeerTyping,
            DateTimeOffset? PeerTypingAtUtc
        );

        public sealed record MessageItem(
            string MessageId,
            string SenderId,
            string Type,
            string Text,
            DateTimeOffset CreatedAtUtc,
            IReadOnlyList<string> DeliveredTo,
            IReadOnlyList<string> ReadBy,
            bool DeletedForAll,
            IReadOnlyList<string> DeletedFor,
            DateTimeOffset? DeletedAtUtc,

            // media
            string? StoragePath,
            long DurationMs,
            string? FileName,
            string? ContentType,
            long SizeBytes,

            // preview
            string? ThumbStoragePath,
            string? LqipBase64,
            int? ThumbWidth,
            int? ThumbHeight,
            string? PreviewType,

            // audio waveform
            IReadOnlyList<int>? Waveform,

            // location
            double? Latitude,
            double? Longitude,

            // contact
            string? ContactName,
            string? ContactPhone
        )
        {
            public MessageItem(
                string MessageId,
                string SenderId,
                string Type,
                string Text,
                DateTimeOffset CreatedAtUtc,
                IReadOnlyList<string> DeliveredTo,
                IReadOnlyList<string> ReadBy,
                bool DeletedForAll,
                IReadOnlyList<string> DeletedFor,
                DateTimeOffset? DeletedAtUtc,
                string? StoragePath,
                long DurationMs,
                string? FileName,
                string? ContentType,
                long SizeBytes,
                double? Latitude,
                double? Longitude,
                string? ContactName,
                string? ContactPhone)
                : this(
                    MessageId,
                    SenderId,
                    Type,
                    Text,
                    CreatedAtUtc,
                    DeliveredTo,
                    ReadBy,
                    DeletedForAll,
                    DeletedFor,
                    DeletedAtUtc,
                    StoragePath,
                    DurationMs,
                    FileName,
                    ContentType,
                    SizeBytes,
                    ThumbStoragePath: null,
                    LqipBase64: null,
                    ThumbWidth: null,
                    ThumbHeight: null,
                    PreviewType: null,
                    Waveform: null,
                    Latitude,
                    Longitude,
                    ContactName,
                    ContactPhone)
            {
            }
        }

        public static string GetDeterministicDmChatId(string uidA, string uidB)
        {
            var a = (uidA ?? "").Trim().ToLowerInvariant();
            var b = (uidB ?? "").Trim().ToLowerInvariant();

            if (a.Length == 0 || b.Length == 0)
                throw new ArgumentException("UID non validi.");

            if (string.Equals(a, b, StringComparison.Ordinal))
                throw new InvalidOperationException("Chat con se stessi non ammessa.");

            var min = string.CompareOrdinal(a, b) <= 0 ? a : b;
            var max = string.CompareOrdinal(a, b) <= 0 ? b : a;

            return $"dm__{min}__{max}";
        }

        public async Task<string> EnsureDirectChatAsync(
            string idToken,
            string uidA,
            string uidB,
            string nicknameA,
            string nicknameB,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                throw new ArgumentException("idToken vuoto", nameof(idToken));
            if (string.IsNullOrWhiteSpace(uidA))
                throw new ArgumentException("uidA vuoto", nameof(uidA));
            if (string.IsNullOrWhiteSpace(uidB))
                throw new ArgumentException("uidB vuoto", nameof(uidB));

            var chatId = GetDeterministicDmChatId(uidA, uidB);

            var now = DateTimeOffset.UtcNow;

            var doc = _db.GetDocument($"chats/{chatId}");
            var snapshot = await doc.GetDocumentSnapshotAsync<Dictionary<string, object>>(Source.Default);
            if (snapshot.Data != null)
                return chatId;

            var fields = new Dictionary<string, object?>
            {
                ["members"] = new[] { uidA, uidB },
                ["isGroup"] = false,
                ["memberNicknames"] = new Dictionary<string, object?>
                {
                    [uidA] = nicknameA ?? string.Empty,
                    [uidB] = nicknameB ?? string.Empty
                },
                ["lastMessageText"] = string.Empty,
                ["lastMessageType"] = "text",
                ["lastMessageAt"] = now,
                ["updatedAt"] = now,
                ["createdAt"] = now
            };

            await doc.SetDataAsync(fields);

            return chatId;
        }

        public async Task SendTextMessageWithIdAsync(
            string idToken,
            string chatId,
            string messageId,
            string senderUid,
            string text,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var now = DateTimeOffset.UtcNow;

            var payloadMap = new Dictionary<string, object?>
            {
                ["text"] = text
            };

            // NOTE:
            // Le rules per CREATE messages impongono keys().hasOnly([...]) e NON includono "deletedAt".
            // Se lo invii anche solo come null, Firestore risponde 403 PERMISSION_DENIED.
            var msgFields = new Dictionary<string, object?>
            {
                ["senderId"] = senderUid,
                ["type"] = "text",
                ["payload"] = payloadMap,
                ["createdAt"] = now,
                ["deliveredTo"] = Array.Empty<string>(),
                ["readBy"] = Array.Empty<string>(),
                ["deletedFor"] = Array.Empty<string>(),
                ["deletedForAll"] = false,
                ["updatedAt"] = now
            };

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.SetDataAsync(msgFields);

            await PatchChatPreviewAsync(idToken, chatId, senderUid, "text", text, ct);
        }

        public async Task SendFileMessageWithIdAsync(
            string idToken,
            string chatId,
            string messageId,
            string senderUid,
            string type, // "audio" | "photo" | "video" | "file"
            string storagePath,
            long durationMs,
            long sizeBytes,
            string fileName,
            string contentType,
            Dictionary<string, object>? previewMap = null,
            IReadOnlyList<int>? waveform = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(type)) type = "file";
            if (string.IsNullOrWhiteSpace(storagePath))
                throw new ArgumentException("storagePath vuoto", nameof(storagePath));

            var now = DateTimeOffset.UtcNow;

            var payloadFields = new Dictionary<string, object?>
            {
                ["storagePath"] = storagePath,
                ["durationMs"] = durationMs,
                ["sizeBytes"] = sizeBytes,
                ["fileName"] = fileName ?? "file.bin",
                ["contentType"] = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            };

            if (previewMap != null && previewMap.Count > 0)
                payloadFields["preview"] = previewMap;

            if (waveform != null && waveform.Count > 0)
                payloadFields["waveform"] = waveform.ToList();

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object?>
            {
                ["senderId"] = senderUid,
                ["type"] = type,
                ["payload"] = payloadFields,
                ["createdAt"] = now,
                ["deliveredTo"] = Array.Empty<string>(),
                ["readBy"] = Array.Empty<string>(),
                ["deletedFor"] = Array.Empty<string>(),
                ["deletedForAll"] = false,
                ["updatedAt"] = now
            };

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.SetDataAsync(msgFields);

            var preview = type switch
            {
                "audio" => "🎤 Messaggio vocale",
                "photo" => "📷 Foto",
                "video" => "🎬 Video",
                _ => "📎 Documento"
            };

            await PatchChatPreviewAsync(idToken, chatId, senderUid, type, preview, ct);
        }

        public async Task SendLocationMessageWithIdAsync(
            string idToken,
            string chatId,
            string messageId,
            string senderUid,
            double lat,
            double lon,
            CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;

            var payloadMap = new Dictionary<string, object?>
            {
                ["lat"] = lat,
                ["lon"] = lon,
            };

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object?>
            {
                ["senderId"] = senderUid,
                ["type"] = "location",
                ["payload"] = payloadMap,
                ["createdAt"] = now,
                ["deliveredTo"] = Array.Empty<string>(),
                ["readBy"] = Array.Empty<string>(),
                ["deletedFor"] = Array.Empty<string>(),
                ["deletedForAll"] = false,
                ["updatedAt"] = now
            };

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.SetDataAsync(msgFields);

            await PatchChatPreviewAsync(idToken, chatId, senderUid, "location", "📍 Posizione", ct);
        }

        public async Task SendContactMessageWithIdAsync(
            string idToken,
            string chatId,
            string messageId,
            string senderUid,
            string contactName,
            string contactPhone,
            CancellationToken ct = default)
        {
            var now = DateTimeOffset.UtcNow;

            var payloadMap = new Dictionary<string, object?>
            {
                ["contactName"] = contactName ?? "",
                ["contactPhone"] = contactPhone ?? "",
            };

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object?>
            {
                ["senderId"] = senderUid,
                ["type"] = "contact",
                ["payload"] = payloadMap,
                ["createdAt"] = now,
                ["deliveredTo"] = Array.Empty<string>(),
                ["readBy"] = Array.Empty<string>(),
                ["deletedFor"] = Array.Empty<string>(),
                ["deletedForAll"] = false,
                ["updatedAt"] = now
            };

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.SetDataAsync(msgFields);

            await PatchChatPreviewAsync(idToken, chatId, senderUid, "contact", "👤 Contatto", ct);
        }

        public async Task DeleteMessageForAllAsync(string chatId, string messageId, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            // Coerente con le rules:
            // - in update sono permessi solo: deliveredTo, readBy, deletedFor, deletedForAll, updatedAt
            // - deletedAt/text/payload NON sono ammessi in update (quindi generano 403).
            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.UpdateDataAsync(new[]
            {
                ("deletedForAll", (object)true),
                ("updatedAt", (object)FieldValue.ServerTimestamp())
            });
        }

        public async Task DeleteMessageForMeAsync(string chatId, string messageId, string uid, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.UpdateDataAsync(new[]
            {
                ("deletedFor", (object)FieldValue.ArrayUnion(new object[] { uid })),
                ("updatedAt", (object)FieldValue.ServerTimestamp())
            });
        }

        public async Task MarkDeliveredBatchAsync(string chatId, IEnumerable<string> messageIds, string myUid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(myUid))
                return;

            var ids = messageIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList()
                ?? new List<string>();
            if (ids.Count == 0)
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var batch = _db.CreateBatch();
            foreach (var messageId in ids)
            {
                var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
                batch.UpdateData(doc, new[]
                {
                    ("deliveredTo", (object)FieldValue.ArrayUnion(new object[] { myUid })),
                    ("updatedAt", (object)FieldValue.ServerTimestamp())
                });
            }

            await batch.CommitAsync();
        }

        public async Task MarkReadBatchAsync(string chatId, IEnumerable<string> messageIds, string myUid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(myUid))
                return;

            var ids = messageIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList()
                ?? new List<string>();
            if (ids.Count == 0)
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var batch = _db.CreateBatch();
            foreach (var messageId in ids)
            {
                var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
                batch.UpdateData(doc, new[]
                {
                    ("readBy", (object)FieldValue.ArrayUnion(new object[] { myUid })),
                    ("updatedAt", (object)FieldValue.ServerTimestamp())
                });
            }

            await batch.CommitAsync();
        }

        private async Task PatchChatPreviewAsync(
            string idToken,
            string chatId,
            string senderUid,
            string type,
            string previewText,
            CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;

            var doc = _db.GetDocument($"chats/{chatId}");
            await doc.UpdateDataAsync(new[]
            {
                ("lastMessageText", (object)(previewText ?? "")),
                ("lastMessageAt", (object)now),
                ("lastMessageSenderUid", (object)senderUid),
                ("lastMessageType", (object)(string.IsNullOrWhiteSpace(type) ? "text" : type)),
                ("updatedAt", (object)now)
            });
        }

        public async Task SetTypingStateAsync(
            string chatId,
            string myUid,
            bool isTyping,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(myUid))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var payload = new Dictionary<string, object?>();
            if (isTyping)
            {
                payload["typingUid"] = myUid;
                payload["typingAt"] = DateTimeOffset.UtcNow;
            }

            var doc = _db.GetDocument($"chats/{chatId}");
            await doc.UpdateDataAsync(new[]
            {
                ("payload", (object)payload)
            });
        }

        public async Task<bool> TryMarkDeliveredAsync(
            string idToken,
            string chatId,
            string messageId,
            IReadOnlyList<string> currentDeliveredTo,
            string myUid,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(myUid))
                return false;

            if (currentDeliveredTo != null && currentDeliveredTo.Contains(myUid, StringComparer.Ordinal))
                return false;

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.UpdateDataAsync(new[]
            {
                ("deliveredTo", (object)FieldValue.ArrayUnion(new object[] { myUid })),
                ("updatedAt", (object)FieldValue.ServerTimestamp())
            });

            return true;
        }

        public async Task<bool> TryMarkReadAsync(
            string idToken,
            string chatId,
            string messageId,
            IReadOnlyList<string> currentReadBy,
            string myUid,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(myUid))
                return false;

            if (currentReadBy != null && currentReadBy.Contains(myUid, StringComparer.Ordinal))
                return false;

            var doc = _db.GetDocument($"chats/{chatId}/messages/{messageId}");
            await doc.UpdateDataAsync(new[]
            {
                ("readBy", (object)FieldValue.ArrayUnion(new object[] { myUid })),
                ("updatedAt", (object)FieldValue.ServerTimestamp())
            });

            return true;
        }

    }
}
