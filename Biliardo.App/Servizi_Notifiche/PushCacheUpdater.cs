using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.Home;
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

            var text = data.TryGetValue("text", out var t) ? t : null;
            var thumbKey = data.TryGetValue("thumbKey", out var thumb) ? thumb : null;
            if (string.IsNullOrWhiteSpace(thumbKey) && data.TryGetValue("thumbStoragePath", out var thumbPath))
                thumbKey = thumbPath;

            var schemaVersion = data.TryGetValue("schemaVersion", out var schemaValue) && int.TryParse(schemaValue, out var schemaParsed)
                ? schemaParsed
                : 0;
            var ready = data.TryGetValue("ready", out var readyValue) && bool.TryParse(readyValue, out var readyParsed) && readyParsed;

            var cached = new HomeFeedLocalCache.CachedHomePost(
                PostId: postId,
                AuthorUid: data.TryGetValue("authorUid", out var authorUid) ? authorUid : "",
                AuthorNickname: authorName ?? "",
                AuthorFirstName: data.TryGetValue("authorFirstName", out var first) ? first : null,
                AuthorLastName: data.TryGetValue("authorLastName", out var last) ? last : null,
                AuthorAvatarPath: data.TryGetValue("authorAvatarPath", out var avatarPath) ? avatarPath : null,
                AuthorAvatarUrl: data.TryGetValue("authorAvatarUrl", out var avatarUrl) ? avatarUrl : null,
                Text: text ?? "",
                ThumbKey: thumbKey,
                CreatedAtUtc: createdAt,
                Attachments: Array.Empty<Biliardo.App.Infrastructure.Home.HomeAttachmentContractV2>(),
                LikeCount: data.TryGetValue("likeCount", out var likeValue) && int.TryParse(likeValue, out var likeParsed) ? likeParsed : 0,
                CommentCount: data.TryGetValue("commentCount", out var commentValue) && int.TryParse(commentValue, out var commentParsed) ? commentParsed : 0,
                ShareCount: data.TryGetValue("shareCount", out var shareValue) && int.TryParse(shareValue, out var shareParsed) ? shareParsed : 0,
                Deleted: false,
                DeletedAtUtc: null,
                RepostOfPostId: data.TryGetValue("repostOfPostId", out var repostValue) ? repostValue : null,
                ClientNonce: data.TryGetValue("clientNonce", out var nonceValue) ? nonceValue : null,
                SchemaVersion: schemaVersion,
                Ready: ready);

            var contract = new Biliardo.App.Infrastructure.Home.HomePostContractV2(
                PostId: cached.PostId,
                CreatedAtUtc: cached.CreatedAtUtc,
                AuthorUid: cached.AuthorUid,
                AuthorNickname: cached.AuthorNickname,
                AuthorFirstName: cached.AuthorFirstName,
                AuthorLastName: cached.AuthorLastName,
                AuthorAvatarPath: cached.AuthorAvatarPath,
                AuthorAvatarUrl: cached.AuthorAvatarUrl,
                Text: cached.Text,
                Attachments: cached.Attachments,
                Deleted: cached.Deleted,
                DeletedAtUtc: cached.DeletedAtUtc,
                RepostOfPostId: cached.RepostOfPostId,
                ClientNonce: cached.ClientNonce,
                LikeCount: cached.LikeCount,
                CommentCount: cached.CommentCount,
                ShareCount: cached.ShareCount,
                SchemaVersion: cached.SchemaVersion,
                Ready: cached.Ready);

            if (!Biliardo.App.Infrastructure.Home.HomePostValidatorV2.IsCacheSafe(contract, out _))
                return;

            var homeStore = new HomeFeedCacheStore();
            var authorFullName = $"{cached.AuthorFirstName} {cached.AuthorLastName}".Trim();

            await homeStore.UpsertPostAsync(new HomeFeedCacheStore.HomePostRow(
                cached.PostId,
                cached.AuthorNickname,
                string.IsNullOrWhiteSpace(authorFullName) ? null : authorFullName,
                cached.AuthorUid,
                cached.AuthorNickname,
                cached.AuthorFirstName,
                cached.AuthorLastName,
                cached.AuthorAvatarPath,
                cached.AuthorAvatarUrl,
                cached.Text,
                cached.ThumbKey,
                cached.CreatedAtUtc,
                cached.SchemaVersion,
                cached.Ready,
                cached.Deleted,
                cached.DeletedAtUtc,
                cached.LikeCount,
                cached.CommentCount,
                cached.ShareCount,
                cached.RepostOfPostId,
                cached.ClientNonce,
                "[]"), ct);
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
