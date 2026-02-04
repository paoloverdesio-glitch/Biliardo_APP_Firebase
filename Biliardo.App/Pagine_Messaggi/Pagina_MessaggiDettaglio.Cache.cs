using System;
using System.Linq;
using System.Threading.Tasks;
using Biliardo.App.Infrastructure;
using Biliardo.App.Servizi_Firebase;
using Microsoft.Maui.ApplicationModel;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio
    {
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

        private async Task AppendOptimisticMessageAsync(string peerId, FirestoreChatService.MessageItem message)
        {
            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            if (string.IsNullOrWhiteSpace(myUid) || string.IsNullOrWhiteSpace(peerId))
                return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (Messaggi.Any(x => !x.IsDateSeparator && string.Equals(x.Id, message.MessageId, StringComparison.Ordinal)))
                    return;

                var lastReal = Messaggi.LastOrDefault(x => !x.IsDateSeparator);
                var lastDay = lastReal?.CreatedAt.ToLocalTime().Date;
                var day = message.CreatedAtUtc.ToLocalTime().Date;
                if (lastDay == null || day != lastDay.Value)
                    Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));

                var vm = BuildVmFromMessage(message, myUid, peerId);
                Messaggi.Add(vm);
                ScrollBottomImmediately(force: true);
            });

            var cacheKey = _chatCacheKey ?? _chatIdCached ?? $"peer:{peerId}";
            var current = ChatDetailMemoryCache.Instance.TryGet(cacheKey, out var existing)
                ? existing.ToList()
                : new System.Collections.Generic.List<FirestoreChatService.MessageItem>();
            current.Add(message);
            ChatDetailMemoryCache.Instance.Set(cacheKey, current);
        }

    }
}
