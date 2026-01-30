using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Marca "delivered" (aggiunge myUid in deliveredTo) per i messaggi inbound
    /// quando l'app è in FOREGROUND, indipendentemente dalla pagina aperta.
    ///
    /// Effetto: il mittente vede ✓✓ grigie appena il dispositivo destinatario
    /// scarica il messaggio (poll in foreground).
    ///
    /// NON marca "read": quello resta responsabilità della pagina chat aperta.
    /// </summary>
    public sealed class ForegroundDeliveredReceiptsService : IDisposable
    {
        private readonly FirestoreChatService _fsChat;
        private readonly TimeSpan _interval;

        private readonly object _gate = new();
        private CancellationTokenSource? _cts;

        private volatile bool _isForeground;
        private readonly SemaphoreSlim _tickLock = new(1, 1);
        private readonly object _perChatLock = new();
        private readonly Dictionary<string, long> _lastChatBatchTicks = new(StringComparer.Ordinal);
        private static readonly TimeSpan MinChatBatchInterval = TimeSpan.FromSeconds(4);

        public ForegroundDeliveredReceiptsService(FirestoreChatService fsChat)
        {
            _fsChat = fsChat ?? throw new ArgumentNullException(nameof(fsChat));
            _interval = TimeSpan.FromSeconds(2);
        }

        public void SetForeground(bool isForeground)
        {
            _isForeground = isForeground;

            if (isForeground)
                EnsureStarted();
            else
                Stop();
        }

        private void EnsureStarted()
        {
            lock (_gate)
            {
                if (_cts != null)
                    return;

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => LoopAsync(_cts.Token));
            }
        }

        public void Stop()
        {
            CancellationTokenSource? cts;
            lock (_gate)
            {
                cts = _cts;
                _cts = null;
            }

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_isForeground)
                        await TickAsync(ct);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ForegroundDeliveredReceiptsService] {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    await Task.Delay(_interval, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task TickAsync(CancellationToken ct)
        {
            // Evita tick concorrenti
            if (!await _tickLock.WaitAsync(0, ct))
                return;

            try
            {
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    return;

                // Lista chat (limit prudente)
                IReadOnlyList<FirestoreChatService.ChatItem> chats;
                try
                {
                    chats = await _fsChat.ListChatsAsync(idToken, myUid, limit: 60, ct: ct);
                }
                catch
                {
                    // best-effort
                    return;
                }

                if (chats == null || chats.Count == 0)
                    return;

                // Concorrenza max 3 (evita troppe chiamate REST)
                using var sem = new SemaphoreSlim(3, 3);

                var tasks = chats
                    .Where(c => !string.IsNullOrWhiteSpace(c.ChatId))
                    .Select(async chat =>
                    {
                        await sem.WaitAsync(ct);
                        try
                        {
                            await ProcessChatAsync(idToken, myUid, chat.ChatId, ct);
                        }
                        catch
                        {
                            // best-effort per chat
                        }
                        finally
                        {
                            sem.Release();
                        }
                    })
                    .ToList();

                await Task.WhenAll(tasks);
            }
            finally
            {
                _tickLock.Release();
            }
        }

        private async Task ProcessChatAsync(string idToken, string myUid, string chatId, CancellationToken ct)
        {
            if (!CanBatchChat(chatId))
                return;

            // Messaggi recenti (DESC)
            var msgs = await _fsChat.GetLastMessagesAsync(idToken, chatId, limit: 40, ct: ct);
            if (msgs == null || msgs.Count == 0)
                return;

            // inbound non ancora delivered sul mio device
            var toDeliver = msgs
                .Where(m =>
                    !string.Equals(m.SenderId, myUid, StringComparison.Ordinal) &&
                    (m.DeliveredTo == null || !m.DeliveredTo.Contains(myUid, StringComparer.Ordinal)))
                .Take(12) // limite per ridurre scritture
                .ToList();

            if (toDeliver.Count == 0)
                return;

            try
            {
                await _fsChat.MarkDeliveredBatchAsync(chatId, toDeliver.Select(x => x.MessageId), myUid, ct);
                StampChatBatch(chatId);
            }
            catch
            {
                // best-effort: se una commit fallisce, riproveremo al prossimo tick
            }
        }

        private bool CanBatchChat(string chatId)
        {
            var now = Environment.TickCount64;
            lock (_perChatLock)
            {
                if (_lastChatBatchTicks.TryGetValue(chatId, out var last))
                {
                    if (now - last < MinChatBatchInterval.TotalMilliseconds)
                        return false;
                }
            }

            return true;
        }

        private void StampChatBatch(string chatId)
        {
            var now = Environment.TickCount64;
            lock (_perChatLock)
            {
                _lastChatBatchTicks[chatId] = now;
            }
        }

        public void Dispose()
        {
            Stop();
            try { _tickLock.Dispose(); } catch { }
        }
    }
}
