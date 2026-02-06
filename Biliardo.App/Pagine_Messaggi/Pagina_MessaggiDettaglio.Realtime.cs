using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Infrastructure;

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

            // 1.4b) Read receipts quando messaggi entrano nel viewport
            for (var i = first; i <= last && i < Messaggi.Count; i++)
            {
                var vm = Messaggi[i];
                if (vm.IsDateSeparator || vm.IsMine)
                    continue;

                if (!string.IsNullOrWhiteSpace(vm.Id))
                    QueueRead(vm.Id);
            }

            // 1.5) Se ho aggiornamenti pending, prova ad applicarli quando lo scroll va idle
            SchedulePendingApply();

            if (e.FirstVisibleItemIndex <= 1)
                _ = LoadOlderMessagesAsync();
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
                    var myUid = _lastMyUid;
                    var peerId = _lastPeerId;
                    var chatId = _lastChatId;

                    if (string.IsNullOrWhiteSpace(myUid) ||
                        string.IsNullOrWhiteSpace(peerId) ||
                        string.IsNullOrWhiteSpace(chatId))
                        return;

                    await ApplyOrderedMessagesAsync(ordered, signature, myUid!, peerId!, chatId!, token);
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
        // 3) CACHE + REALTIME FLOW
        // ============================================================
        private async Task LoadFromCacheAndRenderImmediatelyAsync()
        {
            if (_loadedFromCache)
                return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var myUid = FirebaseSessionePersistente.GetLocalId();
            if (string.IsNullOrWhiteSpace(myUid))
                return;

            _chatCacheKey ??= _chatIdCached ?? $"peer:{peerId}";
            if (ChatDetailMemoryCache.Instance.TryGet(_chatCacheKey, out var memoryItems) && !_loadedFromMemory)
            {
                _loadedFromMemory = true;
                await RenderMessagesAsync(memoryItems, myUid, peerId);
            }
        }

        private async Task RenderMessagesAsync(IReadOnlyList<FirestoreChatService.MessageItem> cached, string myUid, string peerId)
        {
            var chatId = ResolveChatIdForClear(myUid, peerId);
            var clearedAt = ChatLocalState.GetClearedAt(chatId ?? "");
            var filtered = clearedAt.HasValue
                ? cached.Where(m => m.CreatedAtUtc > clearedAt.Value).ToList()
                : cached;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (Messaggi.Count > 0)
                    return;

                DateTime? lastDay = null;
                foreach (var m in filtered.OrderBy(x => x.CreatedAtUtc))
                {
                    var day = m.CreatedAtUtc.ToLocalTime().Date;
                    if (lastDay == null || day != lastDay.Value)
                    {
                        Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));
                        lastDay = day;
                    }

                    Messaggi.Add(BuildVmFromMessage(m, myUid!, peerId));
                }

                IsLoadingMessages = false;
                ScrollBottomImmediately(force: true);
            });
        }

        private async Task LoadOlderMessagesAsync()
        {
            await Task.CompletedTask;
        }

        private async Task StartFirestoreListenersAsync(CancellationToken ct)
        {
            var myUid = FirebaseSessionePersistente.GetLocalId();
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(myUid) || string.IsNullOrWhiteSpace(peerId))
                return;

            StartPeerProfileListener();

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var chatId = await EnsureChatIdAsync(idToken, myUid, peerId, ct);
            if (string.IsNullOrWhiteSpace(chatId))
                return;

            _chatIdCached = chatId;
            _lastChatId = chatId;
            _chatCacheKey ??= chatId;

            _messagesListener?.Dispose();
            _messagesListener = _realtime.SubscribeChatMessages(
                chatId,
                80,
                items =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    var ordered = items.OrderBy(x => x.CreatedAtUtc).ToList();
                    var clearedAt = ChatLocalState.GetClearedAt(chatId);
                    if (clearedAt.HasValue)
                        ordered = ordered.Where(m => m.CreatedAtUtc > clearedAt.Value).ToList();

                    var sig = ComputeUiSignature(ordered);

                    if (IsScrollBusy())
                    {
                        QueuePendingUpdate(sig, ordered);
                    }
                    else
                    {
                        _ = ApplyOrderedMessagesAsync(ordered, sig, myUid, peerId, chatId, ct);
                    }

                    ChatDetailMemoryCache.Instance.Set(_chatCacheKey, ordered);
                },
                ex => Debug.WriteLine($"[ChatDetail] messages listener error: {ex}"));
            _listeners.Add(_messagesListener);

            _typingListener?.Dispose();
            _typingListener = _realtime.SubscribeChatTyping(
                chatId,
                myUid,
                isTyping =>
                {
                    if (ct.IsCancellationRequested)
                        return;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        IsPeerTyping = isTyping;
                    });
                },
                ex => Debug.WriteLine($"[ChatDetail] typing listener error: {ex}"));
            _listeners.Add(_typingListener);
        }

        private void StartRealtimeUpdatesAfterFirstRender()
        {
            // No-op: realtime gestito da listener Firestore
        }

        private void StopRealtimeUpdates()
        {
            // No-op: realtime gestito da listener Firestore
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
                hc.Add(m.ThumbStoragePath);
                hc.Add(m.LqipBase64);
                hc.Add(m.ThumbWidth);
                hc.Add(m.ThumbHeight);
                hc.Add(m.PreviewType);
                hc.Add(m.Waveform?.Count ?? 0);
            }

            return hc.ToHashCode().ToString("X");
        }

        // ============================================================
        // 5) APPLY: aggiorna la ObservableCollection in modo incrementale
        // ============================================================
        private async Task ApplyOrderedMessagesAsync(
            List<FirestoreChatService.MessageItem> ordered,
            string signature,
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

                        Messaggi.Add(BuildVmFromMessage(m, myUid, peerId));
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
                                var newColor = read ? Colors.DeepSkyBlue : Colors.LightGray;

                                if (!string.Equals(existing.StatusLabel, newStatus, StringComparison.Ordinal))
                                    existing.StatusLabel = newStatus;

                                if (existing.StatusColor != newColor)
                                    existing.StatusColor = newColor;
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

                        Messaggi.Add(BuildVmFromMessage(m, myUid, peerId));
                        appended = true;
                    }
                }

                // 5.3.4) auto-scroll solo se l’utente è near-bottom
                if (appended)
                    ScrollBottomImmediately(force: false);
            });

            if (!string.IsNullOrWhiteSpace(_chatCacheKey))
                ChatDetailMemoryCache.Instance.Set(_chatCacheKey, ordered);
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
        private void ScrollBottomImmediately(bool force)
        {
            try
            {
                if (Messaggi.Count == 0)
                    return;

                if (!force && !_userNearBottom)
                    return;

                CvMessaggi.ScrollTo(Messaggi.Count - 1, position: ScrollToPosition.End, animate: false);
            }
            catch { }
        }

        private void OnLayoutSizeChanged(object? sender, EventArgs e)
        {
            if (!_userNearBottom)
                return;

            _ = _layoutScrollDebounce.RunAsync(_ =>
            {
                MainThread.BeginInvokeOnMainThread(() => ScrollBottomImmediately(force: true));
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(80));
        }

        private void ScrollToMessage(ChatMessageVm vm)
        {
            try
            {
                if (vm == null)
                    return;

                CvMessaggi.ScrollTo(vm, position: ScrollToPosition.End, animate: false);
            }
            catch { }
        }

        private string? ResolveChatIdForClear(string myUid, string peerId)
        {
            if (!string.IsNullOrWhiteSpace(_chatIdCached))
                return _chatIdCached;
            if (!string.IsNullOrWhiteSpace(_lastChatId))
                return _lastChatId;

            try
            {
                return FirestoreChatService.GetDeterministicDmChatId(myUid, peerId);
            }
            catch
            {
                return null;
            }
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
            }
            else if (choice == "Elimina per tutti")
            {
                if (!string.IsNullOrWhiteSpace(_lastChatId))
                    await _fsChat.DeleteMessageForAllAsync(_lastChatId, m.Id);
            }
        }

        // ============================================================
        // 9) HOOK: PREFETCH MEDIA (IMPLEMENTATO IN Media.cs - File 6)
        // ============================================================
        private partial Task SchedulePrefetchMediaAsync(int firstIndex, int lastIndex);
    }
}
