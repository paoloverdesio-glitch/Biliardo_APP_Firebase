using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Biliardo.App.Componenti_UI;
using Biliardo.App.Infrastructure;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Pagine_Autenticazione;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiLista : ContentPage
    {
        // =========================================================
        // 1) PARAMETRI / STATO
        // =========================================================
        private readonly ListaViewModel _vm = new();

        private CancellationTokenSource? _pollCts;
        private const int ThreadsPollingIntervalMs = 3000;

        // Guardia anti doppia navigazione (doppio tap / eventi ripetuti)
        private bool _isNavigatingToChat;

#if DEBUG
        private readonly IPushNotificationService _push = new PushNotificationService();
        private bool _debugTokenShown;
#endif

        public Pagina_MessaggiLista()
        {
            InitializeComponent();
            BindingContext = _vm;

            UserPicker.IsVisible = false;
            UserPicker.UtenteSelezionato += OnUserPicked;
        }

        // =========================================================
        // 2) UI - Nuova chat / Selezione
        // =========================================================
        private void OnNuovaChat(object sender, EventArgs e)
        {
            try
            {
                UserPicker.Clear();
                UserPicker.IsVisible = true;
                UserPicker.FocusInput();
            }
            catch (Exception ex)
            {
                DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnUserPicked(object? sender, UtenteSelezionatoEventArgs e)
        {
            try
            {
                UserPicker.IsVisible = false;
                await NavigateToChatAsync(e.Id, e.Nickname);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnSelezioneChat(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (e.CurrentSelection?.Count > 0 && e.CurrentSelection[0] is ChatPreview chat)
                {
                    // evita che resti selezionata
                    ListaChat.SelectedItem = null;

                    await NavigateToChatAsync(chat.WithUserId, chat.Nickname);
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        // =========================================================
        // 3) NAVIGAZIONE (NO ANIMAZIONE + STOP POLLING IMMEDIATO)
        // =========================================================
        private async Task NavigateToChatAsync(string withUserId, string nickname)
        {
            if (_isNavigatingToChat)
                return;

            _isNavigatingToChat = true;

            try
            {
                var myUid = FirebaseSessionePersistente.GetLocalId();

                // blocco “scrivere a se stessi”
                if (!string.IsNullOrWhiteSpace(myUid) &&
                    string.Equals(withUserId, myUid, StringComparison.Ordinal))
                {
                    await DisplayAlert("Messaggi", "Non puoi scrivere a te stesso.", "OK");
                    return;
                }

                // stop polling PRIMA del push (evita refresh durante transizione)
                StopPolling();

                // IMPORTANTISSIMO: push senza animazione (riduce “trasparenze/glitch”)
                await Navigation.PushAsync(new Pagina_MessaggiDettaglio(withUserId, nickname), animated: false);
            }
            finally
            {
                // Il flag viene riabilitato quando torno qui (OnAppearing).
            }
        }

        // =========================================================
        // 4) CICLO VITA PAGINA
        // =========================================================
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // rientro dalla chat: riabilita navigazione e polling
            _isNavigatingToChat = false;

            try
            {
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                {
                    await DisplayAlert("Sessione", "Sessione Firebase assente/scaduta. Rifai login.", "OK");
                    Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
                    return;
                }

                // Esclusione forte: io NON devo comparire in autocomplete
                UserPicker.ExcludeUid = myUid;

                // Esclusione nickname (secondaria)
                UserPicker.ExcludeNickname = FirebaseSessionePersistente.GetDisplayName()
                    ?? FirebaseSessionePersistente.GetEmail();

#if DEBUG
                await MostraTokenFcmDebugAsync();
#endif

                await _vm.CaricaAsync();
                StartPolling();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            StopPolling();
        }

#if DEBUG
        private async Task MostraTokenFcmDebugAsync()
        {
            if (_debugTokenShown) return;
            _debugTokenShown = true;

            try
            {
                // Evita blocchi: massimo 8s
                var tokenTask = _push.GetTokenAsync();
                var done = await Task.WhenAny(tokenTask, Task.Delay(8000));

                if (done != tokenTask)
                {
                    await DisplayAlert("FCM Token (DEBUG)", "Timeout recupero token FCM. Verifica connessione e Google Play Services.", "OK");
                    return;
                }

                var token = (await tokenTask) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(token))
                {
                    await DisplayAlert("FCM Token (DEBUG)", "Token FCM vuoto. Verifica google-services.json e inizializzazione Firebase.", "OK");
                    return;
                }

                try { await Clipboard.Default.SetTextAsync(token); } catch { /* best-effort */ }

                Debug.WriteLine($"[PushNotificationService] FCM Token: {token}");
                await DisplayAlert("FCM Token (DEBUG)", $"{token}\n\n(Copiato negli appunti)", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("FCM Token (DEBUG)", ex.Message, "OK");
            }
        }
#endif

        // =========================================================
        // 5) POLLING LISTA CHAT
        // =========================================================
        private void StartPolling()
        {
            if (_pollCts != null)
                return;

            _pollCts = new CancellationTokenSource();
            var token = _pollCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            await Task.Delay(ThreadsPollingIntervalMs, token);
                            if (token.IsCancellationRequested)
                                break;

                            await MainThread.InvokeOnMainThreadAsync(async () =>
                            {
                                try
                                {
                                    using var _span = PerfLog.Span("CHATLIST_REFRESH");
                                    await _vm.CaricaAsync();
                                }
                                catch { }
                            });
                        }
                        catch (TaskCanceledException) { break; }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[MessagesPolling] {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    Debug.WriteLine("[MessagesPolling] stopped");
                }
            }, token);
        }

        private void StopPolling()
        {
            try { _pollCts?.Cancel(); } catch { }
            try { _pollCts?.Dispose(); } catch { }
            _pollCts = null;
        }
    }

    // ===================== ViewModel + Model =====================

    public sealed class ListaViewModel
    {
        public ObservableCollection<ChatPreview> ChatPreviews { get; } = new();

        private bool _isLoading;

        private readonly FirestoreChatService _fsChat = new("biliardoapp");

        // cache profili (riduce chiamate)
        private readonly Dictionary<string, FirestoreDirectoryService.UserPublicItem> _userCache = new(StringComparer.Ordinal);

        // Sweep delivered: evita burst e sovrapposizioni
        private readonly SemaphoreSlim _deliveredSweepLock = new(1, 1);
        private DateTimeOffset _lastDeliveredSweepUtc = DateTimeOffset.MinValue;

        public async Task CaricaAsync()
        {
            if (_isLoading)
                return;

            _isLoading = true;
            try
            {
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login Firebase.");

                var chats = await _fsChat.ListChatsAsync(idToken, myUid, limit: 200);

                // Ordina lato client per updatedAt/lastAt desc (stile WhatsApp)
                var ordered = chats
                    .Where(c => !string.IsNullOrWhiteSpace(c.PeerUid))
                    .OrderByDescending(c => c.UpdatedAtUtc ?? c.LastAtUtc ?? DateTimeOffset.MinValue)
                    .ToList();

                // Pre-carica profili peer (best-effort)
                await PrefetchProfilesAsync(ordered.Select(x => x.PeerUid).Distinct());

                ChatPreviews.Clear();

                foreach (var c in ordered)
                {
                    var whenUtc = c.UpdatedAtUtc ?? c.LastAtUtc ?? DateTimeOffset.MinValue;
                    var whenLocal = whenUtc.ToLocalTime().DateTime;

                    var peerUid = c.PeerUid;

                    var p = TryGetProfile(peerUid);
                    var nickname =
                        (!string.IsNullOrWhiteSpace(p?.Nickname) ? p!.Nickname :
                        !string.IsNullOrWhiteSpace(c.PeerNickname) ? c.PeerNickname :
                        peerUid);

                    var fullName = p?.FullNameOrPlaceholder ?? "xxxxx xxxxx";
                    var photo = p?.PhotoUrl ?? "";

                    var preview = c.LastText ?? "";
                    var type = string.IsNullOrWhiteSpace(c.LastType) ? "text" : c.LastType;

                    if (string.IsNullOrWhiteSpace(preview) || preview.Trim().Length == 0)
                    {
                        preview = type switch
                        {
                            "audio" => "🎤 Messaggio vocale",
                            "video" => "🎬 Video",
                            "file" => "📎 Documento",
                            "gif" => "GIF",
                            "sticker" => "Sticker",
                            _ => ""
                        };
                    }

                    ChatPreviews.Add(new ChatPreview(
                        withUserId: peerUid,
                        nickname: nickname,
                        fullName: fullName,
                        photoUrl: photo,
                        ultimoMessaggio: preview,
                        dataOra: whenLocal,
                        nonLetti: 0));
                }

                // Sweep delivered best-effort (come tuo codice)
                _ = SweepDeliveredFromListBestEffortAsync(myUid, ordered);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private FirestoreDirectoryService.UserPublicItem? TryGetProfile(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            lock (_userCache)
            {
                return _userCache.TryGetValue(uid, out var p) ? p : null;
            }
        }

        private async Task PrefetchProfilesAsync(IEnumerable<string> uids)
        {
            var list = uids.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            if (list.Count == 0) return;

            // evita chiamate duplicate
            var toFetch = new List<string>();
            lock (_userCache)
            {
                foreach (var u in list)
                {
                    if (!_userCache.ContainsKey(u))
                        toFetch.Add(u);
                }
            }
            if (toFetch.Count == 0) return;

            // concorrenza max 6
            using var sem = new SemaphoreSlim(6, 6);

            var tasks = toFetch.Select(async uid =>
            {
                await sem.WaitAsync();
                try
                {
                    var p = await FirestoreDirectoryService.GetUserPublicAsync(uid);
                    if (p != null)
                    {
                        lock (_userCache)
                            _userCache[uid] = p;
                    }
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private async Task SweepDeliveredFromListBestEffortAsync(
            string myUid,
            IReadOnlyList<FirestoreChatService.ChatItem> orderedChats)
        {
            // throttle: max 1 sweep ogni 4 secondi
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastDeliveredSweepUtc) < TimeSpan.FromSeconds(4))
                return;

            if (!await _deliveredSweepLock.WaitAsync(0))
                return;

            try
            {
                now = DateTimeOffset.UtcNow;
                if ((now - _lastDeliveredSweepUtc) < TimeSpan.FromSeconds(4))
                    return;

                _lastDeliveredSweepUtc = now;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    return;

                // lavora solo su chat più recenti
                var targets = orderedChats
                    .Where(c => !string.IsNullOrWhiteSpace(c.ChatId))
                    .Take(15)
                    .ToList();

                if (targets.Count == 0)
                    return;

                // concorrenza max 3
                using var sem = new SemaphoreSlim(3, 3);

                var tasks = targets.Select(async chat =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var msgs = await _fsChat.GetLastMessagesAsync(idToken, chat.ChatId, limit: 25);

                        var inbound = msgs
                            .Where(m => !string.Equals(m.SenderId, myUid, StringComparison.Ordinal))
                            .Where(m => m.DeliveredTo == null || !m.DeliveredTo.Contains(myUid, StringComparer.Ordinal))
                            .OrderByDescending(m => m.CreatedAtUtc)
                            .Take(20)
                            .ToList();

                        foreach (var m in inbound)
                        {
                            try
                            {
                                await _fsChat.TryMarkDeliveredAsync(
                                    idToken: idToken,
                                    chatId: chat.ChatId,
                                    messageId: m.MessageId,
                                    currentDeliveredTo: m.DeliveredTo,
                                    myUid: myUid);
                            }
                            catch
                            {
                                // best-effort
                            }
                        }
                    }
                    catch
                    {
                        // best-effort
                    }
                    finally
                    {
                        sem.Release();
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }
            catch
            {
                // best-effort
            }
            finally
            {
                _deliveredSweepLock.Release();
            }
        }
    }

    public sealed class ChatPreview
    {
        public string WithUserId { get; }

        public string Nickname { get; }
        public string FullName { get; }
        public string PhotoUrl { get; }

        public string DisplayTitle => $"{Nickname} ({FullName})";

        public string UltimoMessaggio { get; }
        public string OraBreve { get; }
        public int NonLetti { get; }

        public string Iniziale =>
            string.IsNullOrWhiteSpace(Nickname) ? "?" : Nickname[..1].ToUpperInvariant();

        public bool HasPhoto => !string.IsNullOrWhiteSpace(PhotoUrl);
        public bool HasNoPhoto => !HasPhoto;

        public bool NonLettiVisibile => NonLetti > 0;

        public ChatPreview(
            string withUserId,
            string nickname,
            string fullName,
            string photoUrl,
            string ultimoMessaggio,
            DateTime dataOra,
            int nonLetti)
        {
            WithUserId = withUserId;
            Nickname = nickname;
            FullName = string.IsNullOrWhiteSpace(fullName) ? "xxxxx xxxxx" : fullName;
            PhotoUrl = photoUrl ?? "";
            UltimoMessaggio = ultimoMessaggio ?? "";
            NonLetti = nonLetti;

            if (dataOra.Date == DateTime.Today)
                OraBreve = dataOra.ToString("HH:mm");
            else
                OraBreve = dataOra.ToString("dd/MM");
        }
    }
}
