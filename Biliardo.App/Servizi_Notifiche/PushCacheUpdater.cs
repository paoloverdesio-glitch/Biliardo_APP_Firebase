using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

            await TrySendDeliveredAsync(chatId, messageId, senderId, myUid, ct);
        }

        private static async Task HandleHomeAsync(IReadOnlyDictionary<string, string> data, CancellationToken ct)
        {
            if (!data.TryGetValue("postId", out var postId) || string.IsNullOrWhiteSpace(postId))
                return;

            await Task.CompletedTask;
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

    }
}
