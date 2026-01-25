using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Firebase
{
    public sealed class FirestoreChatService
    {
        private readonly string _projectId;

        public FirestoreChatService(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("projectId non valido.", nameof(projectId));

            _projectId = projectId.Trim();
        }

        public sealed record ChatItem(
            string ChatId,
            string PeerUid,
            string PeerNickname,
            string LastText,
            string LastType,
            DateTimeOffset? LastAtUtc,
            DateTimeOffset? UpdatedAtUtc
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

            var fields = new Dictionary<string, object>
            {
                ["members"] = FirestoreRestClient.VArrayStrings(new[] { uidA, uidB }),
                ["isGroup"] = FirestoreRestClient.VBool(false),

                ["memberNicknames"] = FirestoreRestClient.VMap(new Dictionary<string, object>
                {
                    [uidA] = FirestoreRestClient.VString(nicknameA ?? string.Empty),
                    [uidB] = FirestoreRestClient.VString(nicknameB ?? string.Empty),
                }),

                ["lastMessageText"] = FirestoreRestClient.VString(string.Empty),
                ["lastMessageType"] = FirestoreRestClient.VString("text"),
                ["lastMessageAt"] = FirestoreRestClient.VTimestamp(now),

                ["updatedAt"] = FirestoreRestClient.VTimestamp(now),
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
            };

            try
            {
                await FirestoreRestClient.CreateDocumentAsync(
                    collectionPath: "chats",
                    documentId: chatId,
                    fields: fields,
                    idToken: idToken,
                    ct: ct);
            }
            catch (Exception ex)
            {
                var m = ex.Message ?? "";

                if (m.Contains("409", StringComparison.OrdinalIgnoreCase) ||
                    m.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    return chatId;
                }

                throw;
            }

            return chatId;
        }

        public async Task<IReadOnlyList<ChatItem>> ListChatsAsync(
            string idToken,
            string myUid,
            int limit = 50,
            CancellationToken ct = default)
        {
            if (limit <= 0) limit = 50;

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new object[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "chats" }
                },
                ["where"] = new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "members" },
                        ["op"] = "ARRAY_CONTAINS",
                        ["value"] = FirestoreRestClient.VString(myUid)
                    }
                },
                ["limit"] = limit
            };

            using var doc = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, ct);

            var chats = new List<ChatItem>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return chats;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(d, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var chatId = ExtractLastPathSegment(name!);

                if (!d.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                    continue;

                var members = ReadStringArray(fields, "members");
                var peerUid = members.FirstOrDefault(x => !string.Equals(x, myUid, StringComparison.Ordinal)) ?? "";

                var peerNick = ReadMapString(fields, "memberNicknames", peerUid) ?? "";

                var lastText = ReadStringField(fields, "lastMessageText") ?? "";
                var lastType = ReadStringField(fields, "lastMessageType") ?? "text";

                var lastAt = ReadTimestampField(fields, "lastMessageAt");
                var updatedAt = ReadTimestampField(fields, "updatedAt");

                chats.Add(new ChatItem(chatId, peerUid, peerNick, lastText, lastType, lastAt, updatedAt));
            }

            return chats;
        }

        public async Task<IReadOnlyList<MessageItem>> GetLastMessagesAsync(
            string idToken,
            string chatId,
            int limit = 50,
            CancellationToken ct = default)
        {
            if (limit <= 0) limit = 50;

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new object[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "messages" }
                },
                ["orderBy"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "createdAt" },
                        ["direction"] = "DESCENDING"
                    }
                },
                ["limit"] = limit
            };

            using var doc = await FirestoreRestClient.RunQueryAsync(
                structuredQuery,
                idToken,
                parentDocumentPath: $"chats/{chatId}",
                ct: ct);

            var outList = new List<MessageItem>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return outList;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(d, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var messageId = ExtractLastPathSegment(name!);

                if (!d.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                    continue;

                var senderId = ReadStringField(fields, "senderId");
                var type = ReadStringField(fields, "type");
                var createdAt = ReadTimestampField(fields, "createdAt") ?? DateTimeOffset.MinValue;

                var deliveredTo = ReadStringArray(fields, "deliveredTo");
                var readBy = ReadStringArray(fields, "readBy");
                var deletedForAll = ReadBoolField(fields, "deletedForAll") ?? false;
                var deletedFor = ReadStringArray(fields, "deletedFor");
                var deletedAt = ReadTimestampField(fields, "deletedAt");

                string text = "";

                var payloadText = ReadMapString(fields, "payload", "text");
                if (!string.IsNullOrWhiteSpace(payloadText))
                    text = payloadText;

                if (string.IsNullOrWhiteSpace(senderId))
                    senderId = ReadStringField(fields, "fromUid") ?? "";

                if (string.IsNullOrWhiteSpace(text))
                    text = ReadStringField(fields, "text") ?? "";

                if (string.IsNullOrWhiteSpace(type))
                    type = "text";

                // media
                var storagePath = ReadMapString(fields, "payload", "storagePath");
                var fileName = ReadMapString(fields, "payload", "fileName");
                var contentType = ReadMapString(fields, "payload", "contentType");
                var durationMs = ReadMapInt64(fields, "payload", "durationMs");
                var sizeBytes = ReadMapInt64(fields, "payload", "sizeBytes");

                var thumbStoragePath = ReadNestedMapString(fields, "payload", "preview", "thumbStoragePath");
                var lqipBase64 = ReadNestedMapString(fields, "payload", "preview", "lqipBase64");
                var thumbWidth = ReadNestedMapInt(fields, "payload", "preview", "thumbWidth");
                var thumbHeight = ReadNestedMapInt(fields, "payload", "preview", "thumbHeight");
                var previewType = ReadNestedMapString(fields, "payload", "preview", "previewType");

                var waveform = ReadMapIntArray(fields, "payload", "waveform");

                // location
                var lat = ReadMapDouble(fields, "payload", "lat");
                var lon = ReadMapDouble(fields, "payload", "lon");

                // contact
                var cn = ReadMapString(fields, "payload", "contactName");
                var cp = ReadMapString(fields, "payload", "contactPhone");

                outList.Add(new MessageItem(
                    MessageId: messageId,
                    SenderId: senderId ?? "",
                    Type: type ?? "text",
                    Text: text ?? "",
                    CreatedAtUtc: createdAt,
                    DeliveredTo: deliveredTo,
                    ReadBy: readBy,
                    DeletedForAll: deletedForAll,
                    DeletedFor: deletedFor,
                    DeletedAtUtc: deletedAt,
                    StoragePath: storagePath,
                    DurationMs: durationMs,
                    FileName: fileName,
                    ContentType: contentType,
                    SizeBytes: sizeBytes,
                    ThumbStoragePath: thumbStoragePath,
                    LqipBase64: lqipBase64,
                    ThumbWidth: thumbWidth,
                    ThumbHeight: thumbHeight,
                    PreviewType: previewType,
                    Waveform: waveform,
                    Latitude: lat,
                    Longitude: lon,
                    ContactName: cn,
                    ContactPhone: cp
                ));
            }

            return outList;
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

            var payloadMap = FirestoreRestClient.VMap(new Dictionary<string, object>
            {
                ["text"] = FirestoreRestClient.VString(text)
            });

            // NOTE:
            // Le rules per CREATE messages impongono keys().hasOnly([...]) e NON includono "deletedAt".
            // Se lo invii anche solo come null, Firestore risponde 403 PERMISSION_DENIED.
            var msgFields = new Dictionary<string, object>
            {
                ["senderId"] = FirestoreRestClient.VString(senderUid),
                ["type"] = FirestoreRestClient.VString("text"),
                ["payload"] = payloadMap,
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["deliveredTo"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["readBy"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedFor"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedForAll"] = FirestoreRestClient.VBool(false),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            try
            {
                await FirestoreRestClient.CreateDocumentAsync(
                    collectionPath: $"chats/{chatId}/messages",
                    documentId: messageId,
                    fields: msgFields,
                    idToken: idToken,
                    ct: ct);
            }
            catch (Exception ex)
            {
                var m = ex.Message ?? "";
                if (!(m.Contains("409", StringComparison.OrdinalIgnoreCase) || m.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase)))
                    throw;
            }

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

            var payloadFields = new Dictionary<string, object>
            {
                ["storagePath"] = FirestoreRestClient.VString(storagePath),
                ["durationMs"] = FirestoreRestClient.VInt(durationMs),
                ["sizeBytes"] = FirestoreRestClient.VInt(sizeBytes),
                ["fileName"] = FirestoreRestClient.VString(fileName ?? "file.bin"),
                ["contentType"] = FirestoreRestClient.VString(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType),
            };

            if (previewMap != null && previewMap.Count > 0)
                payloadFields["preview"] = FirestoreRestClient.VMap(previewMap);

            if (waveform != null && waveform.Count > 0)
                payloadFields["waveform"] = FirestoreRestClient.VArray(waveform.Select(x => FirestoreRestClient.VInt(x)).ToArray());

            var payloadMap = FirestoreRestClient.VMap(payloadFields);

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object>
            {
                ["senderId"] = FirestoreRestClient.VString(senderUid),
                ["type"] = FirestoreRestClient.VString(type),
                ["payload"] = payloadMap,
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["deliveredTo"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["readBy"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedFor"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedForAll"] = FirestoreRestClient.VBool(false),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            try
            {
                await FirestoreRestClient.CreateDocumentAsync(
                    collectionPath: $"chats/{chatId}/messages",
                    documentId: messageId,
                    fields: msgFields,
                    idToken: idToken,
                    ct: ct);
            }
            catch (Exception ex)
            {
                var m = ex.Message ?? "";
                if (!(m.Contains("409", StringComparison.OrdinalIgnoreCase) || m.Contains("ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase)))
                    throw;
            }

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

            var payloadMap = FirestoreRestClient.VMap(new Dictionary<string, object>
            {
                ["lat"] = new Dictionary<string, object> { ["doubleValue"] = lat },
                ["lon"] = new Dictionary<string, object> { ["doubleValue"] = lon },
            });

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object>
            {
                ["senderId"] = FirestoreRestClient.VString(senderUid),
                ["type"] = FirestoreRestClient.VString("location"),
                ["payload"] = payloadMap,
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["deliveredTo"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["readBy"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedFor"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedForAll"] = FirestoreRestClient.VBool(false),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            await FirestoreRestClient.CreateDocumentAsync(
                collectionPath: $"chats/{chatId}/messages",
                documentId: messageId,
                fields: msgFields,
                idToken: idToken,
                ct: ct);

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

            var payloadMap = FirestoreRestClient.VMap(new Dictionary<string, object>
            {
                ["contactName"] = FirestoreRestClient.VString(contactName ?? ""),
                ["contactPhone"] = FirestoreRestClient.VString(contactPhone ?? ""),
            });

            // (vedi nota sopra: niente "deletedAt" in CREATE)
            var msgFields = new Dictionary<string, object>
            {
                ["senderId"] = FirestoreRestClient.VString(senderUid),
                ["type"] = FirestoreRestClient.VString("contact"),
                ["payload"] = payloadMap,
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["deliveredTo"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["readBy"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedFor"] = FirestoreRestClient.VArrayStrings(Array.Empty<string>()),
                ["deletedForAll"] = FirestoreRestClient.VBool(false),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            await FirestoreRestClient.CreateDocumentAsync(
                collectionPath: $"chats/{chatId}/messages",
                documentId: messageId,
                fields: msgFields,
                idToken: idToken,
                ct: ct);

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
            var fields = new Dictionary<string, object>
            {
                ["deletedForAll"] = FirestoreRestClient.VBool(true),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(DateTimeOffset.UtcNow),
            };

            await FirestoreRestClient.PatchDocumentAsync(
                $"chats/{chatId}/messages/{messageId}",
                fields,
                new[] { "deletedForAll", "updatedAt" },
                idToken,
                ct);
        }

        public async Task DeleteMessageForMeAsync(string chatId, string messageId, string uid, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            await FirestoreRestClient.CommitAsync(
                $"chats/{chatId}/messages/{messageId}",
                new[]
                {
                    FirestoreRestClient.TransformAppendMissingElements("deletedFor", new object[]
                    {
                        FirestoreRestClient.VString(uid)
                    })
                },
                idToken,
                ct);
        }

        private static async Task PatchChatPreviewAsync(
            string idToken,
            string chatId,
            string senderUid,
            string type,
            string previewText,
            CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;

            var chatPatch = new Dictionary<string, object>
            {
                ["lastMessageText"] = FirestoreRestClient.VString(previewText ?? ""),
                ["lastMessageAt"] = FirestoreRestClient.VTimestamp(now),
                ["lastMessageSenderUid"] = FirestoreRestClient.VString(senderUid),
                ["lastMessageType"] = FirestoreRestClient.VString(string.IsNullOrWhiteSpace(type) ? "text" : type),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now),
            };

            await FirestoreRestClient.PatchDocumentAsync(
                documentPath: $"chats/{chatId}",
                fields: chatPatch,
                updateMaskFieldPaths: new[]
                {
                    "lastMessageText","lastMessageAt","lastMessageSenderUid","lastMessageType","updatedAt"
                },
                idToken: idToken,
                ct: ct);
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

            var merged = (currentDeliveredTo ?? Array.Empty<string>()).ToList();
            merged.Add(myUid);

            var now = DateTimeOffset.UtcNow;

            var patch = new Dictionary<string, object>
            {
                ["deliveredTo"] = FirestoreRestClient.VArrayStrings(merged),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            await FirestoreRestClient.PatchDocumentAsync(
                documentPath: $"chats/{chatId}/messages/{messageId}",
                fields: patch,
                updateMaskFieldPaths: new[] { "deliveredTo", "updatedAt" },
                idToken: idToken,
                ct: ct);

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

            var merged = (currentReadBy ?? Array.Empty<string>()).ToList();
            merged.Add(myUid);

            var now = DateTimeOffset.UtcNow;

            var patch = new Dictionary<string, object>
            {
                ["readBy"] = FirestoreRestClient.VArrayStrings(merged),
                ["updatedAt"] = FirestoreRestClient.VTimestamp(now)
            };

            await FirestoreRestClient.PatchDocumentAsync(
                documentPath: $"chats/{chatId}/messages/{messageId}",
                fields: patch,
                updateMaskFieldPaths: new[] { "readBy", "updatedAt" },
                idToken: idToken,
                ct: ct);

            return true;
        }

        // ----------------- parse helpers -----------------

        private static string ExtractLastPathSegment(string fullName)
        {
            var idx = fullName.LastIndexOf("/", StringComparison.Ordinal);
            return idx >= 0 ? fullName[(idx + 1)..] : fullName;
        }

        private static string? ReadString(JsonElement obj, string prop)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(prop, out var p)) return null;
            if (p.ValueKind != JsonValueKind.String) return null;
            return p.GetString();
        }

        private static string? ReadStringField(JsonElement fields, string fieldName)
        {
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(fieldName, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object) return null;

            if (v.TryGetProperty("stringValue", out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();

            return null;
        }

        private static DateTimeOffset? ReadTimestampField(JsonElement fields, string fieldName)
        {
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(fieldName, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object) return null;

            if (!v.TryGetProperty("timestampValue", out var t) || t.ValueKind != JsonValueKind.String)
                return null;

            var ts = t.GetString();
            if (string.IsNullOrWhiteSpace(ts)) return null;

            if (DateTimeOffset.TryParse(ts, out var dto))
                return dto;

            return null;
        }

        private static bool? ReadBoolField(JsonElement fields, string fieldName)
        {
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(fieldName, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Object) return null;

            if (v.TryGetProperty("booleanValue", out var b))
            {
                if (b.ValueKind == JsonValueKind.True) return true;
                if (b.ValueKind == JsonValueKind.False) return false;
            }

            return null;
        }

        private static List<string> ReadStringArray(JsonElement fields, string fieldName)
        {
            var list = new List<string>();

            if (fields.ValueKind != JsonValueKind.Object) return list;
            if (!fields.TryGetProperty(fieldName, out var v)) return list;
            if (v.ValueKind != JsonValueKind.Object) return list;

            if (!v.TryGetProperty("arrayValue", out var av) || av.ValueKind != JsonValueKind.Object)
                return list;

            if (!av.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("stringValue", out var sv) &&
                    sv.ValueKind == JsonValueKind.String)
                {
                    var s = sv.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s);
                }
            }

            return list;
        }

        private static string? ReadMapString(JsonElement fields, string mapFieldName, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                return null;

            if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                return null;

            if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                return null;

            if (!f.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;

            if (entry.TryGetProperty("stringValue", out var sv) && sv.ValueKind == JsonValueKind.String)
                return sv.GetString();

            return null;
        }

        private static long ReadMapInt64(JsonElement fields, string mapFieldName, string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return 0;
                if (fields.ValueKind != JsonValueKind.Object) return 0;
                if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                    return 0;

                if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                    return 0;

                if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                    return 0;

                if (!f.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                    return 0;

                if (entry.TryGetProperty("integerValue", out var iv))
                {
                    if (iv.ValueKind == JsonValueKind.String && long.TryParse(iv.GetString(), out var x))
                        return x;
                    if (iv.ValueKind == JsonValueKind.Number && iv.TryGetInt64(out var y))
                        return y;
                }
            }
            catch { }
            return 0;
        }

        private static double? ReadMapDouble(JsonElement fields, string mapFieldName, string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return null;
                if (fields.ValueKind != JsonValueKind.Object) return null;
                if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                    return null;

                if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                    return null;

                if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                    return null;

                if (!f.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                    return null;

                if (entry.TryGetProperty("doubleValue", out var dv))
                {
                    if (dv.ValueKind == JsonValueKind.Number && dv.TryGetDouble(out var x)) return x;
                    if (dv.ValueKind == JsonValueKind.String && double.TryParse(dv.GetString(), out var y)) return y;
                }
                if (entry.TryGetProperty("integerValue", out var iv))
                {
                    if (iv.ValueKind == JsonValueKind.String && long.TryParse(iv.GetString(), out var z)) return z;
                }
            }
            catch { }
            return null;
        }

        private static string? ReadNestedMapString(JsonElement fields, string mapFieldName, string nestedMapName, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                return null;

            if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                return null;

            if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                return null;

            if (!f.TryGetProperty(nestedMapName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                return null;

            if (!nested.TryGetProperty("mapValue", out var nmap) || nmap.ValueKind != JsonValueKind.Object)
                return null;

            if (!nmap.TryGetProperty("fields", out var nf) || nf.ValueKind != JsonValueKind.Object)
                return null;

            if (!nf.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;

            if (entry.TryGetProperty("stringValue", out var sv) && sv.ValueKind == JsonValueKind.String)
                return sv.GetString();

            return null;
        }

        private static int? ReadNestedMapInt(JsonElement fields, string mapFieldName, string nestedMapName, string key)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(key)) return null;
                if (fields.ValueKind != JsonValueKind.Object) return null;
                if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                    return null;

                if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                    return null;

                if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                    return null;

                if (!f.TryGetProperty(nestedMapName, out var nested) || nested.ValueKind != JsonValueKind.Object)
                    return null;

                if (!nested.TryGetProperty("mapValue", out var nmap) || nmap.ValueKind != JsonValueKind.Object)
                    return null;

                if (!nmap.TryGetProperty("fields", out var nf) || nf.ValueKind != JsonValueKind.Object)
                    return null;

                if (!nf.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                    return null;

                if (entry.TryGetProperty("integerValue", out var iv))
                {
                    if (iv.ValueKind == JsonValueKind.String && int.TryParse(iv.GetString(), out var x))
                        return x;
                    if (iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var y))
                        return y;
                }
            }
            catch { }

            return null;
        }

        private static IReadOnlyList<int>? ReadMapIntArray(JsonElement fields, string mapFieldName, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            if (fields.ValueKind != JsonValueKind.Object) return null;
            if (!fields.TryGetProperty(mapFieldName, out var v) || v.ValueKind != JsonValueKind.Object)
                return null;

            if (!v.TryGetProperty("mapValue", out var mv) || mv.ValueKind != JsonValueKind.Object)
                return null;

            if (!mv.TryGetProperty("fields", out var f) || f.ValueKind != JsonValueKind.Object)
                return null;

            if (!f.TryGetProperty(key, out var entry) || entry.ValueKind != JsonValueKind.Object)
                return null;

            if (!entry.TryGetProperty("arrayValue", out var av) || av.ValueKind != JsonValueKind.Object)
                return null;

            if (!av.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<int>();
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("integerValue", out var iv))
                {
                    if (iv.ValueKind == JsonValueKind.String && int.TryParse(iv.GetString(), out var x))
                        list.Add(x);
                    else if (iv.ValueKind == JsonValueKind.Number && iv.TryGetInt32(out var y))
                        list.Add(y);
                }
            }

            return list.Count == 0 ? null : list;
        }
    }
}
