using System;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Infrastructure;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio
    {
        private static FirestoreChatService.MessageItem MapRowToMessage(ChatCacheStore.MessageRow row)
        {
            var type = string.IsNullOrWhiteSpace(row.Text) ? "file" : "text";
            return new FirestoreChatService.MessageItem(
                MessageId: row.MessageId,
                SenderId: row.SenderId,
                Type: type,
                Text: row.Text ?? "",
                CreatedAtUtc: row.CreatedAtUtc,
                DeliveredTo: Array.Empty<string>(),
                ReadBy: Array.Empty<string>(),
                DeletedForAll: false,
                DeletedFor: Array.Empty<string>(),
                DeletedAtUtc: null,
                StoragePath: row.MediaKey,
                DurationMs: 0,
                FileName: null,
                ContentType: null,
                SizeBytes: 0,
                ThumbStoragePath: null,
                LqipBase64: null,
                ThumbWidth: null,
                ThumbHeight: null,
                PreviewType: null,
                Waveform: null,
                Latitude: null,
                Longitude: null,
                ContactName: null,
                ContactPhone: null);
        }

        private ChatMessageVm BuildVmFromMessage(FirestoreChatService.MessageItem message, string myUid, string peerId)
        {
            var vm = ChatMessageVm.FromFirestore(message, myUid, peerId);

            if (NeedsSyncPlaceholder(message))
            {
                vm.Text = "Contenuto disponibile";
                vm.RequiresSync = true;
                vm.SyncCommand = null;
            }

            return vm;
        }

        private static bool NeedsSyncPlaceholder(FirestoreChatService.MessageItem message)
        {
            return string.IsNullOrWhiteSpace(message.Text) && !string.IsNullOrWhiteSpace(message.StoragePath);
        }

        private async Task UpsertLocalMessageAsync(string chatId, string peerId, FirestoreChatService.MessageItem message)
        {
            await _chatCache.UpsertAppendAsync(chatId, new[] { message }, maxItems: AppCacheOptions.MaxChatMessagesPerChat, CancellationToken.None);

            await _chatStore.UpsertChatAsync(new ChatCacheStore.ChatRow(
                chatId,
                peerId,
                message.MessageId,
                message.Text,
                message.Type,
                message.CreatedAtUtc,
                UnreadCount: 0,
                UpdatedAtUtc: message.CreatedAtUtc), CancellationToken.None);
        }
    }
}
