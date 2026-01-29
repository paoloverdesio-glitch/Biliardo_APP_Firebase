using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Sicurezza;
using Biliardo.App.Realtime;

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
        private void StartPollingAfterFirstRender(TimeSpan initialDelay, TimeSpan interval)
        {
            if (_pollCts != null) return;

            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoopAsync(initialDelay, interval, _pollCts.Token));
        }

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

            _chatCacheKey ??= _chatCache.GetCacheKey(_chatIdCached, peerId);
            var cached = await _chatCache.TryReadAsync(_chatCacheKey, CancellationToken.None);
            if (cached.Count == 0)
                return;

            _loadedFromCache = true;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (Messaggi.Count > 0)
                    return;

                DateTime? lastDay = null;
                foreach (var m in cached.OrderBy(x => x.CreatedAtUtc))
                {
                    var day = m.CreatedAtUtc.ToLocalTime().Date;
                    if (lastDay == null || day != lastDay.Value)
                    {
                        Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));
                        lastDay = day;
                    }

                    Messaggi.Add(ChatMessageVm.FromFirestore(m, myUid!, peerId));
                }

                IsLoadingMessages = false;
                ScrollBottomImmediately(force: true);
            });
        }

        private async Task LoadOlderMessagesAsync()
        {
            if (_isLoadingOlder)
                return;

            var idToken = _lastIdToken;
            var myUid = _lastMyUid;
            var peerId = _lastPeerId;
            var chatId = _lastChatId;

            if (string.IsNullOrWhiteSpace(idToken) ||
                string.IsNullOrWhiteSpace(myUid) ||
                string.IsNullOrWhiteSpace(peerId) ||
                string.IsNullOrWhiteSpace(chatId))
                return;

            var oldest = Messaggi.FirstOrDefault(x => !x.IsDateSeparator);
            if (oldest == null)
                return;

            _isLoadingOlder = true;
            try
            {
                var older = await _fsChat.GetMessagesBeforeAsync(idToken!, chatId!, oldest.CreatedAt, limit: 40, ct: CancellationToken.None);
                if (older.Count == 0)
                    return;

                var ordered = older.OrderBy(x => x.CreatedAtUtc).ToList();

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var insertIndex = 0;
                    var existingFirstDay = oldest.CreatedAt.ToLocalTime().Date;

                    DateTime? lastDay = null;
                    foreach (var m in ordered)
                    {
                        if (m.DeletedFor != null && m.DeletedFor.Contains(myUid!, StringComparer.Ordinal))
                            continue;

                        var day = m.CreatedAtUtc.ToLocalTime().Date;
                        if (lastDay == null || day != lastDay.Value)
                        {
                            if (day != existingFirstDay)
                                Messaggi.Insert(insertIndex++, ChatMessageVm.CreateDateSeparator(day));
                            lastDay = day;
                        }

                        Messaggi.Insert(insertIndex++, ChatMessageVm.FromFirestore(m, myUid!, peerId!));
                    }
                });

                _chatCacheKey ??= _chatCache.GetCacheKey(chatId, peerId!);
                await _chatCache.UpsertAppendAsync(_chatCacheKey, older, maxItems: 200, CancellationToken.None);
            }
            catch
            {
                // ignore
            }
            finally
            {
                _isLoadingOlder = false;
            }
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

        private void StartRealtimeUpdatesAfterFirstRender()
        {
            if (_realtimeSubscribed)
                return;

            _realtimeSubscribed = true;
            BusEventiRealtime.Instance.NewChatMessageNotification += OnRealtimeChatMessage;
        }

        private void StopRealtimeUpdates()
        {
            if (!_realtimeSubscribed)
                return;

            _realtimeSubscribed = false;
            BusEventiRealtime.Instance.NewChatMessageNotification -= OnRealtimeChatMessage;
        }

        private void OnRealtimeChatMessage(object? sender, RealtimeEventPayload e)
        {
            var data = e.Data;
            if (data == null || data.Count == 0)
                return;

            if (!IsRealtimePayloadForThisChat(data))
                return;

            if (TryBuildMessageFromPayload(data, out var message))
            {
                _ = AppendRealtimeMessageAsync(message);
                return;
            }

            _ = LoadOnceFromServerAsync(CancellationToken.None);
        }

        private bool IsRealtimePayloadForThisChat(IReadOnlyDictionary<string, string> data)
        {
            if (_chatIdCached != null && data.TryGetValue("chatId", out var chatId))
                return string.Equals(chatId, _chatIdCached, StringComparison.Ordinal);

            if (!string.IsNullOrWhiteSpace(_peerUserId))
            {
                if (data.TryGetValue("peerUid", out var peerUid)
                    && string.Equals(peerUid, _peerUserId, StringComparison.Ordinal))
                    return true;

                if (data.TryGetValue("fromUid", out var fromUid)
                    && string.Equals(fromUid, _peerUserId, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool TryBuildMessageFromPayload(IReadOnlyDictionary<string, string> data, out FirestoreChatService.MessageItem message)
        {
            message = default!;

            if (!data.TryGetValue("messageId", out var messageId) || string.IsNullOrWhiteSpace(messageId))
                return false;

            if (!data.TryGetValue("senderId", out var senderId) || string.IsNullOrWhiteSpace(senderId))
                return false;

            var text = data.TryGetValue("text", out var txt) ? txt ?? "" : "";
            var type = data.TryGetValue("type", out var t) ? t ?? "text" : "text";

            if (!TryParseTimestamp(data, out var createdAt))
                return false;

            message = new FirestoreChatService.MessageItem(
                MessageId: messageId,
                SenderId: senderId,
                Type: type,
                Text: text,
                CreatedAtUtc: createdAt,
                DeliveredTo: Array.Empty<string>(),
                ReadBy: Array.Empty<string>(),
                DeletedForAll: false,
                DeletedFor: Array.Empty<string>(),
                DeletedAtUtc: null,
                StoragePath: null,
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

            return true;
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

        private async Task AppendRealtimeMessageAsync(FirestoreChatService.MessageItem message)
        {
            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(myUid))
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

                Messaggi.Add(ChatMessageVm.FromFirestore(message, myUid, peerId));
                ScrollBottomImmediately(force: false);
            });

            _chatCacheKey ??= _chatCache.GetCacheKey(_chatIdCached, peerId);
            await _chatCache.UpsertAppendAsync(_chatCacheKey, new[] { message }, maxItems: 200, CancellationToken.None);
        }

        private async Task PollLoopAsync(TimeSpan initialDelay, TimeSpan interval, CancellationToken ct)
        {
            try
            {
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay, ct);
            }
            catch
            {
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try { await LoadOnceFromServerAsync(ct); } catch { }

                try { await Task.Delay(interval, ct); }
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
                hc.Add(m.ThumbStoragePath);
                hc.Add(m.LqipBase64);
                hc.Add(m.ThumbWidth);
                hc.Add(m.ThumbHeight);
                hc.Add(m.PreviewType);
                hc.Add(m.Waveform?.Count ?? 0);
            }

            return hc.ToHashCode().ToString("X");
        }

        private async Task LoadOnceFromServerAsync(CancellationToken ct)
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
            _chatCacheKey ??= _chatCache.GetCacheKey(chatId, peerId);

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

            // 5.2) receipts best-effort in background (batch, no loop patch)
            _ = Task.Run(async () =>
            {
                try
                {
                    var inbound = ordered.Where(x => !string.Equals(x.SenderId, myUid, StringComparison.Ordinal)).ToList();
                    var toDeliver = inbound
                        .Where(x => x.DeliveredTo == null || !x.DeliveredTo.Contains(myUid, StringComparer.Ordinal))
                        .Select(x => x.MessageId)
                        .ToList();

                    var toRead = inbound
                        .Where(x => x.ReadBy == null || !x.ReadBy.Contains(myUid, StringComparer.Ordinal))
                        .Select(x => x.MessageId)
                        .ToList();

                    if (toDeliver.Count > 0)
                        await _fsChat.MarkDeliveredBatchAsync(chatId, toDeliver, myUid, ct);

                    if (toRead.Count > 0)
                        await _fsChat.MarkReadBatchAsync(chatId, toRead, myUid, ct);
                }
                catch { }
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

                        Messaggi.Add(ChatMessageVm.FromFirestore(m, myUid, peerId));
                        appended = true;
                    }
                }

                // 5.3.4) auto-scroll solo se l’utente è near-bottom
                if (appended)
                    ScrollBottomImmediately(force: false);
            });

            try
            {
                _chatCacheKey ??= _chatCache.GetCacheKey(chatId, peerId);
                await _chatCache.UpsertAppendAsync(_chatCacheKey, ordered, maxItems: 200, ct);
            }
            catch
            {
                // ignore cache errors
            }
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

                await LoadOnceFromServerAsync(CancellationToken.None);
            }
            else if (choice == "Elimina per tutti")
            {
                if (!string.IsNullOrWhiteSpace(_lastChatId))
                    await _fsChat.DeleteMessageForAllAsync(_lastChatId, m.Id);

                await LoadOnceFromServerAsync(CancellationToken.None);
            }
        }

        // ============================================================
        // 9) HOOK: PREFETCH MEDIA (IMPLEMENTATO IN Media.cs - File 6)
        // ============================================================
        private partial Task SchedulePrefetchMediaAsync(int firstIndex, int lastIndex);
    }
}
