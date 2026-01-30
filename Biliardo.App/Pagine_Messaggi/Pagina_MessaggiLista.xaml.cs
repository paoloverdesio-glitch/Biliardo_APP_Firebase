using Biliardo.App.Componenti_UI;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Cache_Locale.Profili;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Realtime;
using Microsoft.Maui.ApplicationModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiLista : ContentPage
    {
        // =========================================================
        // 1) PARAMETRI / STATO
        // =========================================================
        private readonly ListaViewModel _vm = new();

        // Guardia anti doppia navigazione (doppio tap / eventi ripetuti)
        private bool _isNavigatingToChat;
        private bool _realtimeSubscribed;

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

        private static bool TryGetSelectedChat(SelectionChangedEventArgs e, out ChatPreview? chat)
        {
            chat = null;

            try
            {
                // Evita indicizzazione [0] (race possibile se la lista si aggiorna durante l'evento).
                chat = e.CurrentSelection?.OfType<ChatPreview>().FirstOrDefault();
                return chat != null;
            }
            catch
            {
                chat = null;
                return false;
            }
        }

        private async void OnSelezioneChat(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Se stiamo già navigando, ignora eventi duplicati
                if (_isNavigatingToChat)
                {
                    try { ListaChat.SelectedItem = null; } catch { }
                    return;
                }

                if (!TryGetSelectedChat(e, out var chat) || chat == null)
                    return;

                // evita che resti selezionata e riduce rimbalzi SelectionChanged
                try { ListaChat.SelectedItem = null; } catch { }

                await NavigateToChatAsync(chat.WithUserId, chat.Nickname);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        // =========================================================
        // 3) NAVIGAZIONE (STOP POLLING + SUSPEND LIST UPDATES)
        // =========================================================
        private async Task NavigateToChatAsync(string withUserId, string nickname)
        {
            if (_isNavigatingToChat)
                return;

            _isNavigatingToChat = true;

            // BLOCCA subito: evita refresh lista durante la selezione / transizione
            _vm.SetSuspended(true);

            try
            {
                var myUid = FirebaseSessionePersistente.GetLocalId();

                // blocco “scrivere a se stessi”
                if (!string.IsNullOrWhiteSpace(myUid) &&
                    string.Equals(withUserId, myUid, StringComparison.Ordinal))
                {
                    await DisplayAlert("Messaggi", "Non puoi scrivere a te stesso.", "OK");
                    _isNavigatingToChat = false;
                    _vm.SetSuspended(false);
                    return;
                }

                // IMPORTANTISSIMO: push senza animazione (riduce “trasparenze/glitch”)
                await Navigation.PushAsync(new Pagina_MessaggiDettaglio(withUserId, nickname), animated: false);
            }
            catch
            {
                // Se la navigazione fallisce, ripristina stato pagina lista
                _isNavigatingToChat = false;
                _vm.SetSuspended(false);
                throw;
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

            // rientro dalla chat: riabilita navigazione e aggiornamenti realtime
            _isNavigatingToChat = false;
            _vm.SetSuspended(false);

            try
            {
                var hasSession = await FirebaseSessionePersistente.HaSessioneAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (!hasSession || string.IsNullOrWhiteSpace(myUid))
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

                // Difensivo: nessuna selezione residua
                try { ListaChat.SelectedItem = null; } catch { }

                await _vm.CaricaAsync();
                StartRealtimeUpdates();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Quando esco dalla pagina lista (es. apro una chat), sospendo aggiornamenti e fermo realtime
            _vm.SetSuspended(true);
            StopRealtimeUpdates();
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
        // 5) REALTIME (PUSH)
        // =========================================================
        private void StartRealtimeUpdates()
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
            if (_isNavigatingToChat || _vm.IsSuspended)
                return;

            _ = _vm.CaricaAsync();
        }
    }

    // ===================== ViewModel + Model =====================

    public sealed class ListaViewModel
    {
        public ObservableCollection<ChatPreview> ChatPreviews { get; } = new();

        private int _isLoadingFlag; // 0/1 (thread-safe)
        private int _suspendedFlag; // 0/1 (thread-safe)

        public bool IsSuspended => Volatile.Read(ref _suspendedFlag) == 1;

        public void SetSuspended(bool suspended)
            => Volatile.Write(ref _suspendedFlag, suspended ? 1 : 0);

        private readonly ChatCacheStore _chatStore = new();
        private readonly UserPublicLocalCache _profileCache = new();
        private readonly Dictionary<string, FirestoreDirectoryService.UserPublicItem> _userCache = new(StringComparer.Ordinal);

        public async Task CaricaAsync()
        {
            // Se sospeso (es. sto navigando), non toccare la collection
            if (IsSuspended)
                return;

            // Anti-reentrancy robusto (realtime + OnAppearing ecc.)
            if (Interlocked.Exchange(ref _isLoadingFlag, 1) == 1)
                return;

            try
            {
                var myUid = FirebaseSessionePersistente.GetLocalId();
                if (string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login Firebase.");

                var chats = await _chatStore.ListChatsAsync(CancellationToken.None);

                // Ordina lato client per updatedAt/lastAt desc (stile WhatsApp)
                var ordered = chats
                    .Where(c => !string.IsNullOrWhiteSpace(c.PeerUid))
                    .OrderByDescending(c => c.UpdatedAtUtc)
                    .ToList();

                // Prepara nuova lista in memoria (no UI)
                var newItems = new List<ChatPreview>(ordered.Count);

                foreach (var c in ordered)
                {
                    var whenUtc = c.UpdatedAtUtc;
                    var whenLocal = whenUtc.ToLocalTime().DateTime;

                    var peerUid = c.PeerUid;

                    var p = await TryGetProfileAsync(peerUid);
                    var nickname =
                        (!string.IsNullOrWhiteSpace(p?.Nickname) ? p!.Nickname :
                        peerUid);

                    var fullName = p?.FullNameOrPlaceholder ?? "";
                    var photoUrl = p?.PhotoUrl ?? "";
                    var photoLocal = p?.PhotoLocalPath ?? "";

                    var lastMessage = await _chatStore.GetLatestMessageAsync(c.ChatId, CancellationToken.None);
                    var preview = lastMessage?.Text ?? "";
                    var type = string.IsNullOrWhiteSpace(preview) && !string.IsNullOrWhiteSpace(lastMessage?.MediaKey) ? "file" : "text";

                    if (string.IsNullOrWhiteSpace(preview) || preview.Trim().Length == 0)
                    {
                        preview = type switch
                        {
                            "file" => "📎 Contenuto disponibile",
                            _ => "Contenuto disponibile"
                        };
                    }

                    newItems.Add(new ChatPreview(
                        withUserId: peerUid,
                        nickname: nickname,
                        fullName: fullName,
                        avatarUrl: photoUrl,
                        avatarPath: photoLocal,
                        ultimoMessaggio: preview,
                        dataOra: whenLocal,
                        nonLetti: c.UnreadCount));
                }

                // Applica su UI thread, e solo se non sospeso
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (IsSuspended)
                        return;

                    ChatPreviews.Clear();
                    foreach (var it in newItems)
                        ChatPreviews.Add(it);
                });

            }
            finally
            {
                Interlocked.Exchange(ref _isLoadingFlag, 0);
            }
        }

        private async Task<FirestoreDirectoryService.UserPublicItem?> TryGetProfileAsync(string uid)
        {
            if (string.IsNullOrWhiteSpace(uid)) return null;
            lock (_userCache)
            {
                if (_userCache.TryGetValue(uid, out var p))
                    return p;
            }

            var cached = await _profileCache.TryGetAsync(uid, CancellationToken.None);
            if (cached != null)
            {
                lock (_userCache)
                    _userCache[uid] = cached;
            }

            return cached;
        }
    }

    public sealed class ChatPreview
    {
        public string WithUserId { get; }

        public string Nickname { get; }
        public string FullName { get; }
        public string AvatarUrl { get; }
        public string AvatarPath { get; }

        public string DisplayTitle => string.IsNullOrWhiteSpace(FullName) ? Nickname : $"{Nickname} ({FullName})";

        public string UltimoMessaggio { get; }
        public string OraBreve { get; }
        public int NonLetti { get; }

        public bool NonLettiVisibile => NonLetti > 0;

        public ChatPreview(
            string withUserId,
            string nickname,
            string fullName,
            string avatarUrl,
            string avatarPath,
            string ultimoMessaggio,
            DateTime dataOra,
            int nonLetti)
        {
            WithUserId = withUserId;
            Nickname = nickname;
            FullName = fullName ?? "";
            AvatarUrl = avatarUrl ?? "";
            AvatarPath = avatarPath ?? "";
            UltimoMessaggio = ultimoMessaggio ?? "";
            NonLetti = nonLetti;

            if (dataOra.Date == DateTime.Today)
                OraBreve = dataOra.ToString("HH:mm");
            else
                OraBreve = dataOra.ToString("dd/MM");
        }
    }
}
