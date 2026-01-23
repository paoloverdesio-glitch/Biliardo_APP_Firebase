using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Sicurezza;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) EVENTI UI: SCROLL (anti-jank + prefetch + deferred apply)
        // ============================================================
        private void OnMessagesScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            // 1.1) Marca la UI come "busy" per evitare apply durante lo scroll
            MarkScrollBusy();

            // 1.2) Near-bottom: regola per auto-scroll solo quando l’utente è in basso
            _userNearBottom = e.LastVisibleItemIndex >= (Messaggi.Count - 2);

            // 1.3) Range visibile (con piccolo anticipo)
            var first = Math.Max(0, e.FirstVisibleItemIndex);
            var last = Math.Min(Messaggi.Count - 1, e.LastVisibleItemIndex + 5);

            // 1.4) Prefetch media (implementato in Media.cs - File 6)
            _ = SchedulePrefetchMediaAsync(first, last);

            // 1.5) Se ho aggiornamenti pending, prova ad applicarli quando lo scroll va idle
            SchedulePendingApply();
        }

        // ============================================================
        // 2) PENDING APPLY: accoda update mentre l’utente scrolla
        // ============================================================
        private void QueuePendingUpdate(string signature, List<FirestoreChatService.MessageItem> ordered)
        {
            lock (_pendingLock)
            {
                _pendingSignature = signature;
                _pendingOrdered = ordered;
            }
            SchedulePendingApply();
        }

        private void SchedulePendingApply()
        {
            try { _pendingApplyCts?.Cancel(); } catch { }
            try { _pendingApplyCts?.Dispose(); } catch { }
            _pendingApplyCts = new CancellationTokenSource();
            var token = _pendingApplyCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // 2.1) Debounce: aspetta un po' dopo l’ultimo scroll event
                    await Task.Delay(ScrollIdleDelay, token);

                    // 2.2) Se nel frattempo è tornato busy, rimanda
                    if (token.IsCancellationRequested || IsScrollBusy())
                        return;

                    List<FirestoreChatService.MessageItem>? ordered = null;
                    string? signature = null;

                    lock (_pendingLock)
                    {
                        ordered = _pendingOrdered;
                        signature = _pendingSignature;
                        _pendingOrdered = null;
                        _pendingSignature = null;
                    }

                    if (ordered == null || string.IsNullOrWhiteSpace(signature))
                        return;

                    // 2.3) Contesto minimo necessario (evita apply se non ho sessione/chat)
                    var idToken = _lastIdToken;
                    var myUid = _lastMyUid;
                    var peerId = _lastPeerId;
                    var chatId = _lastChatId;

                    if (string.IsNullOrWhiteSpace(idToken) ||
                        string.IsNullOrWhiteSpace(myUid) ||
                        string.IsNullOrWhiteSpace(peerId) ||
                        string.IsNullOrWhiteSpace(chatId))
                        return;

                    await ApplyOrderedMessagesAsync(ordered, signature, idToken!, myUid!, peerId!, chatId!, token);
                }
                catch { }
            }, token);
        }

        private void CancelPendingApply()
        {
            lock (_pendingLock)
            {
                _pendingOrdered = null;
                _pendingSignature = null;
            }

            try { _pendingApplyCts?.Cancel(); } catch { }
            try { _pendingApplyCts?.Dispose(); } catch { }
            _pendingApplyCts = null;
        }

        // ============================================================
        // 3) POLLING LOOP
        // ============================================================
        private void StartPolling()
        {
            if (_pollCts != null) return;

            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(_pollCts.Token));
        }

        private void StopPolling()
        {
            var cts = _pollCts;
            _pollCts = null;

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await LoadOnceAsync(ct); } catch { }

                try { await Task.Delay(_pollInterval, ct); }
                catch { break; }
            }
        }

        // ============================================================
        // 4) LOAD ONCE: legge Firestore e applica diff in modo scroll-safe
        // ============================================================
        private static string ComputeUiSignature(List<FirestoreChatService.MessageItem> ordered)
        {
            var hc = new HashCode();

            // Nota: firma volutamente "leggera": cambia quando cambiano campi
            // che impattano UI (id, tipo, receipts, delete, metadati file).
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];

                hc.Add(m.MessageId);
                hc.Add(m.SenderId);
                hc.Add(m.Type);

                hc.Add(m.DeliveredTo?.Count ?? 0);
                hc.Add(m.ReadBy?.Count ?? 0);

                hc.Add(m.DeletedForAll);
                hc.Add(m.DeletedFor?.Count ?? 0);

                hc.Add(m.FileName);
                hc.Add(m.SizeBytes);
                hc.Add(m.DurationMs);
                hc.Add(m.StoragePath);
            }

            return hc.ToHashCode().ToString("X");
        }

        private async Task LoadOnceAsync(CancellationToken ct)
        {
            // 4.1) Validazione peer
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            // 4.2) Solo provider Firebase
            var provider = await SessionePersistente.GetProviderAsync();
            if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                return;

            // 4.3) Token + uid
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            var myUid = FirebaseSessionePersistente.GetLocalId();

            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                return;

            // 4.4) chatId
            var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId, ct);

            // 4.5) cache contesto per pending apply
            _lastIdToken = idToken;
            _lastMyUid = myUid;
            _lastPeerId = peerId;
            _lastChatId = chatId;

            // 4.6) lettura ultimi messaggi
            var msgs = await _fsChat.GetLastMessagesAsync(idToken!, chatId, limit: 80, ct: ct);
            var ordered = msgs.OrderBy(m => m.CreatedAtUtc).ToList();

            // 4.7) firma
            var sig = ComputeUiSignature(ordered);
            if (sig == _lastUiSignature)
            {
                if (IsLoadingMessages)
                    MainThread.BeginInvokeOnMainThread(() => IsLoadingMessages = false);
                return;
            }

            // 4.8) se sto scrollando, accoda e applica quando idle
            if (IsScrollBusy())
            {
                QueuePendingUpdate(sig, ordered);

                if (IsLoadingMessages)
                    MainThread.BeginInvokeOnMainThread(() => IsLoadingMessages = false);

                return;
            }

            // 4.9) apply immediato
            await ApplyOrderedMessagesAsync(ordered, sig, idToken!, myUid!, peerId, chatId, ct);
        }

        // ============================================================
        // 5) APPLY: aggiorna la ObservableCollection in modo incrementale
        // ============================================================
        private async Task ApplyOrderedMessagesAsync(
            List<FirestoreChatService.MessageItem> ordered,
            string signature,
            string idToken,
            string myUid,
            string peerId,
            string chatId,
            CancellationToken ct)
        {
            // 5.1) anti-ripetizione
            if (string.Equals(signature, _lastUiSignature, StringComparison.Ordinal))
            {
                if (IsLoadingMessages)
                    MainThread.BeginInvokeOnMainThread(() => IsLoadingMessages = false);
                return;
            }

            _lastUiSignature = signature;

            // 5.2) receipts best-effort in background (non blocca UI)
            _ = Task.Run(async () =>
            {
                foreach (var m in ordered.Where(x => !string.Equals(x.SenderId, myUid, StringComparison.Ordinal)))
                {
                    try
                    {
                        await _fsChat.TryMarkDeliveredAsync(idToken, chatId, m.MessageId, m.DeliveredTo, myUid, ct);
                        await _fsChat.TryMarkReadAsync(idToken, chatId, m.MessageId, m.ReadBy, myUid, ct);
                    }
                    catch { }
                }
            }, ct);

            // 5.3) apply in UI thread
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsLoadingMessages = false;

                // 5.3.1) Mappa dei messaggi già presenti (solo non-separatori)
                var byId = new Dictionary<string, ChatMessageVm>(StringComparer.Ordinal);
                foreach (var vm in Messaggi.Where(x => !x.IsDateSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(vm.Id))
                        byId[vm.Id] = vm;
                }

                bool appended = false;

                // 5.3.2) Caso iniziale: lista vuota -> ricostruisco con separatori
                if (Messaggi.Count == 0)
                {
                    DateTime? lastDay = null;

                    foreach (var m in ordered)
                    {
                        // "Elimina per me": non renderizzare
                        if (m.DeletedFor != null && m.DeletedFor.Contains(myUid, StringComparer.Ordinal))
                            continue;

                        var day = m.CreatedAtUtc.ToLocalTime().Date;
                        if (lastDay == null || day != lastDay.Value)
                        {
                            Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));
                            lastDay = day;
                        }

                        Messaggi.Add(ChatMessageVm.FromFirestore(m, myUid, peerId));
                    }

                    appended = true;
                }
                else
                {
                    // 5.3.3) Caso incrementale: aggiorno existing e appendo solo nuovi
                    var lastReal = Messaggi.LastOrDefault(x => !x.IsDateSeparator);
                    var lastDay = lastReal?.CreatedAt.ToLocalTime().Date;

                    foreach (var m in ordered)
                    {
                        if (m.DeletedFor != null && m.DeletedFor.Contains(myUid, StringComparer.Ordinal))
                            continue;

                        var id = m.MessageId ?? "";
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        // 5.3.3.a) già esiste: patch in place (spunte e metadati media)
                        if (byId.TryGetValue(id, out var existing))
                        {
                            if (existing.IsMine)
                            {
                                var delivered = (m.DeliveredTo ?? Array.Empty<string>())
                                    .Contains(peerId, StringComparer.Ordinal);

                                var read = (m.ReadBy ?? Array.Empty<string>())
                                    .Contains(peerId, StringComparer.Ordinal);

                                var newStatus = (read || delivered) ? "✓✓" : "✓";
                                if (!string.Equals(existing.StatusLabel, newStatus, StringComparison.Ordinal))
                                    existing.StatusLabel = newStatus;
                            }

                            // Patch metadati media che possono arrivare in ritardo
                            if (existing.IsPhoto || existing.IsVideo || existing.IsFile || existing.IsAudio)
                            {
                                bool fileNameChanged = false;
                                bool sizeChanged = false;
                                bool durationChanged = false;
                                bool storageChanged = false;

                                if (string.IsNullOrWhiteSpace(existing.FileName) && !string.IsNullOrWhiteSpace(m.FileName))
                                {
                                    existing.FileName = m.FileName;
                                    fileNameChanged = true;
                                }

                                if (existing.SizeBytes <= 0 && m.SizeBytes > 0)
                                {
                                    existing.SizeBytes = m.SizeBytes;
                                    sizeChanged = true;
                                }

                                if (existing.DurationMs <= 0 && m.DurationMs > 0)
                                {
                                    existing.DurationMs = m.DurationMs;
                                    durationChanged = true;
                                }

                                if (string.IsNullOrWhiteSpace(existing.StoragePath) && !string.IsNullOrWhiteSpace(m.StoragePath))
                                {
                                    existing.StoragePath = m.StoragePath;
                                    storageChanged = true;
                                }

                                if (fileNameChanged)
                                    existing.NotificaCambio(nameof(ChatMessageVm.FileName),
                                                            nameof(ChatMessageVm.FileLabel),
                                                            nameof(ChatMessageVm.AudioLabel));

                                if (sizeChanged)
                                    existing.NotificaCambio(nameof(ChatMessageVm.SizeBytes),
                                                            nameof(ChatMessageVm.FileSizeLabel));

                                if (durationChanged)
                                    existing.NotificaCambio(nameof(ChatMessageVm.DurationMs),
                                                            nameof(ChatMessageVm.DurationLabel));

                                if (storageChanged)
                                    existing.NotificaCambio(nameof(ChatMessageVm.StoragePath));
                            }

                            continue;
                        }

                        // 5.3.3.b) nuovo: aggiungi separatore se cambia giorno + append
                        var day = m.CreatedAtUtc.ToLocalTime().Date;
                        if (lastDay == null || day != lastDay.Value)
                        {
                            Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));
                            lastDay = day;
                        }

                        Messaggi.Add(ChatMessageVm.FromFirestore(m, myUid, peerId));
                        appended = true;
                    }
                }

                // 5.3.4) auto-scroll solo se l’utente è near-bottom
                if (appended && _userNearBottom)
                    ScrollToEnd();
            });
        }

        // ============================================================
        // 6) CHATID: caching + ensure direct chat
        // ============================================================
        private async Task<string> EnsureChatIdAsync(string idToken, string myUid, string peerUid, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(_chatIdCached))
                return _chatIdCached!;

            var myNick = FirebaseSessionePersistente.GetDisplayName()
                         ?? FirebaseSessionePersistente.GetEmail()
                         ?? myUid;

            var peerNick = string.IsNullOrWhiteSpace(_peerNickname) ? peerUid : _peerNickname!;

            var chatId = await _fsChat.EnsureDirectChatAsync(idToken, myUid, peerUid, myNick, peerNick, ct);
            _chatIdCached = chatId;
            return chatId;
        }

        // ============================================================
        // 7) SCROLL HELPERS
        // ============================================================
        private void ScrollToEnd()
        {
            try
            {
                if (Messaggi.Count == 0)
                    return;

                CvMessaggi.ScrollTo(Messaggi.Count - 1, position: ScrollToPosition.End, animate: false);
            }
            catch { }
        }

        // ============================================================
        // 8) AZIONI SUL MESSAGGIO (tap bolla: delete per me / per tutti)
        // ============================================================
        private async void OnMessageActionTapped(object sender, TappedEventArgs e)
        {
            if (sender is not BindableObject bo || bo.BindingContext is not ChatMessageVm m)
                return;

            if (m.IsDateSeparator || m.IsHiddenForMe)
                return;

            var choice = await DisplayActionSheet("Messaggio", "Annulla", null, "Elimina per me", "Elimina per tutti");

            if (choice == "Elimina per me")
            {
                if (!string.IsNullOrWhiteSpace(_lastChatId) && !string.IsNullOrWhiteSpace(_lastMyUid))
                    await _fsChat.DeleteMessageForMeAsync(_lastChatId, m.Id, _lastMyUid);

                await LoadOnceAsync(CancellationToken.None);
            }
            else if (choice == "Elimina per tutti")
            {
                if (!string.IsNullOrWhiteSpace(_lastChatId))
                    await _fsChat.DeleteMessageForAllAsync(_lastChatId, m.Id);

                await LoadOnceAsync(CancellationToken.None);
            }
        }

        // ============================================================
        // 9) HOOK: PREFETCH MEDIA (IMPLEMENTATO IN Media.cs - File 6)
        // ============================================================
        private partial Task SchedulePrefetchMediaAsync(int firstIndex, int lastIndex);
    }
}
