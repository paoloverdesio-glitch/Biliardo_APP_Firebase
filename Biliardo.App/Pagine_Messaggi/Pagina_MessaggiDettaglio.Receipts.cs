using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio
    {
        private static readonly TimeSpan ReceiptsDebounce = TimeSpan.FromMilliseconds(350);
        private readonly HashSet<string> _pendingDelivered = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingRead = new(StringComparer.Ordinal);
        private readonly object _receiptsLock = new();
        private CancellationTokenSource? _receiptsCts;

        private void QueueDelivered(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                return;

            lock (_receiptsLock)
            {
                _pendingDelivered.Add(messageId);
            }

            ScheduleReceiptsFlush();
        }

        private void QueueRead(string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                return;

            lock (_receiptsLock)
            {
                _pendingRead.Add(messageId);
            }

            ScheduleReceiptsFlush();
        }

        private void ScheduleReceiptsFlush()
        {
            lock (_receiptsLock)
            {
                _receiptsCts?.Cancel();
                _receiptsCts?.Dispose();
                _receiptsCts = new CancellationTokenSource();
                var token = _receiptsCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(ReceiptsDebounce, token);
                        await FlushReceiptsAsync(token);
                    }
                    catch (TaskCanceledException)
                    {
                    }
                }, token);
            }
        }

        private async Task FlushReceiptsAsync(CancellationToken ct)
        {
            List<string> toDeliver;
            List<string> toRead;
            lock (_receiptsLock)
            {
                toDeliver = _pendingDelivered.ToList();
                toRead = _pendingRead.ToList();
                _pendingDelivered.Clear();
                _pendingRead.Clear();
            }

            if (toDeliver.Count == 0 && toRead.Count == 0)
                return;

            var chatId = _chatIdCached;
            var myUid = FirebaseSessionePersistente.GetLocalId();

            if (string.IsNullOrWhiteSpace(chatId) || string.IsNullOrWhiteSpace(myUid))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            try
            {
                if (toDeliver.Count > 0)
                    await _fsChat.MarkDeliveredBatchAsync(chatId, toDeliver, myUid, ct);

                if (toRead.Count > 0)
                    await _fsChat.MarkReadBatchAsync(chatId, toRead, myUid, ct);
            }
            catch
            {
                // best-effort
            }
        }

        private void CancelReceipts()
        {
            lock (_receiptsLock)
            {
                _receiptsCts?.Cancel();
                _receiptsCts?.Dispose();
                _receiptsCts = null;
                _pendingDelivered.Clear();
                _pendingRead.Clear();
            }
        }
    }
}
