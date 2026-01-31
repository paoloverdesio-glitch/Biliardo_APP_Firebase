using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Sync;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Servizi_Notifiche
{
    public static class PushCacheUpdater
    {
        public static async Task UpdateAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
        {
            if (data == null || data.Count == 0)
                return;

            if (!PushPayloadValidator.IsPayloadComplete(data, out var payloadKind, out var contentId))
            {
                if (PushPayloadValidator.TryGetContentId(data, out contentId))
                {
                    var fetchMissing = new FetchMissingContentUseCase();
                    await fetchMissing.EnqueueAsync(contentId, string.IsNullOrWhiteSpace(payloadKind) ? "unknown" : payloadKind, data, priority: 10, ct);
                }
            }

            var kind = data.TryGetValue("kind", out var k) ? k : "";

            if (string.Equals(kind, "private_message", StringComparison.OrdinalIgnoreCase))
            {
                await HandleChatAsync(data, ct);
                return;
            }

            if (string.Equals(kind, "home_post", StringComparison.OrdinalIgnoreCase))
            {
                await HandleHomeAsync(data, ct);
            }
        }

        private static async Task HandleChatAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
        {
            if (!data.TryGetValue("messageId", out var messageId) || string.IsNullOrWhiteSpace(messageId))
                return;

            if (!data.TryGetValue("senderId", out var senderId) || string.IsNullOrWhiteSpace(senderId))
                return;

            if (!TryParseTimestamp(data, out var createdAt))
                createdAt = DateTimeOffset.UtcNow;

            var chatId = data.TryGetValue("chatId", out var cid) && !string.IsNullOrWhiteSpace(cid)
                ? cid
                : null;

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var peerUid = data.TryGetValue("peerUid", out var peer) ? peer : null;

            if (string.IsNullOrWhiteSpace(peerUid))
            {
                if (!string.IsNullOrWhiteSpace(myUid) && string.Equals(senderId, myUid, StringComparison.Ordinal))
                {
                    peerUid = data.TryGetValue("toUid", out var toUid) ? toUid : null;
                }
                else
                {
                    peerUid = senderId;
                }
            }

            if (string.IsNullOrWhiteSpace(chatId))
                chatId = $"peer:{peerUid}";

            var text = data.TryGetValue("text", out var t) ? t : null;
            var mediaKey = data.TryGetValue("mediaKey", out var mk) ? mk : null;
            if (string.IsNullOrWhiteSpace(mediaKey) && data.TryGetValue("storagePath", out var sp))
                mediaKey = sp;
            var msgType = data.TryGetValue("type", out var type) ? type : null;
            if (string.IsNullOrWhiteSpace(msgType))
                msgType = string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(mediaKey) ? "file" : "text";

            var chatStore = new ChatCacheStore();
            await chatStore.UpsertMessagesAsync(new[]
            {
                new ChatCacheStore.MessageRow(chatId, messageId, senderId, text, mediaKey, createdAt)
            }, ct);

            var existingChat = await chatStore.GetChatAsync(chatId, ct);
            var unread = existingChat?.UnreadCount ?? 0;
            if (!string.IsNullOrWhiteSpace(myUid) && !string.Equals(senderId, myUid, StringComparison.Ordinal))
                unread += 1;

            await chatStore.UpsertChatAsync(new ChatCacheStore.ChatRow(
                chatId,
                peerUid ?? "",
                messageId,
                text,
                msgType,
                createdAt,
                unread,
                createdAt), ct);
            await chatStore.TrimChatListAsync(AppCacheOptions.MaxChatListEntries, ct);

            await TrySendDeliveredAsync(chatId, messageId, senderId, myUid, ct);
        }

        private static async Task HandleHomeAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
        {
            if (!data.TryGetValue("postId", out var postId) || string.IsNullOrWhiteSpace(postId))
                return;

            if (!TryParseTimestamp(data, out var createdAt))
                createdAt = DateTimeOffset.UtcNow;

            var authorName = data.TryGetValue("authorName", out var author) ? author : null;
            if (string.IsNullOrWhiteSpace(authorName) && data.TryGetValue("authorNickname", out var authorNick))
                authorName = authorNick;

            var authorFirst = data.TryGetValue("authorFirstName", out var first) ? first : null;
            var authorLast = data.TryGetValue("authorLastName", out var last) ? last : null;
            var authorFullName = $"{authorFirst} {authorLast}".Trim();

            var text = data.TryGetValue("text", out var t) ? t : null;
            var thumbKey = data.TryGetValue("thumbKey", out var thumb) ? thumb : null;
            if (string.IsNullOrWhiteSpace(thumbKey) && data.TryGetValue("thumbStoragePath", out var thumbPath))
                thumbKey = thumbPath;

            var homeStore = new HomeFeedCacheStore();
            await homeStore.UpsertPostAsync(new HomeFeedCacheStore.HomePostRow(postId, authorName, string.IsNullOrWhiteSpace(authorFullName) ? null : authorFullName, text, thumbKey, createdAt), ct);
        }

        private static async Task TrySendDeliveredAsync(string chatId, string messageId, string senderId, string myUid, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(myUid) || string.Equals(senderId, myUid, StringComparison.Ordinal))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var fsChat = new FirestoreChatService("biliardoapp");
            try
            {
                await fsChat.MarkDeliveredBatchAsync(chatId, new[] { messageId }, myUid, ct);
            }
            catch
            {
                // best-effort
            }
        }

        private static bool TryParseTimestamp(IReadOnlyDictionary<string, string> data, out DateTimeOffset timestamp)
        {
            timestamp = DateTimeOffset.UtcNow;

            if (data.TryGetValue("createdAtUtc", out var createdAtUtc)
                && DateTimeOffset.TryParse(createdAtUtc, out var dto))
            {
                timestamp = dto;
                return true;
            }

            if (data.TryGetValue("createdAt", out var createdAt)
                && DateTimeOffset.TryParse(createdAt, out var dto2))
            {
                timestamp = dto2;
                return true;
            }

            if (data.TryGetValue("createdAtMs", out var msString)
                && long.TryParse(msString, out var ms))
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                return true;
            }

            return false;
        }
    }
}
