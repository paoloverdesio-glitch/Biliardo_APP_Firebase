using Biliardo.App.Componenti_UI;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Realtime;
using Biliardo.App.Effects;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Servizi_Sicurezza;
using Biliardo.App.Utilita;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        private readonly HashSet<string> _selectedChatIds = new(StringComparer.Ordinal);
        private readonly Dictionary<VisualElement, CancellationTokenSource> _pressCts = new();
        private readonly HashSet<VisualElement> _longPressTriggered = new();
        private const int LongPressMs = 600;

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

        public bool HasChatSelection => _selectedChatIds.Count > 0;

        private Task ShowServerErrorPopupAsync(string title, Exception ex)
        {
            var message = ex?.ToString() ?? "";
            return PopupErrorHelper.ShowAsync(this, title, message);
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
                await ShowServerErrorPopupAsync("Errore", ex);
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
                await ShowServerErrorPopupAsync("Errore", ex);
            }
        }

        // =========================================================
        // 3) NAVIGAZIONE (STOP POLLING + SUSPEND LIST UPDATES)
        // =========================================================
        private async Task NavigateToChatAsync(string withUserId, string nickname)
        {
            if (HasChatSelection)
                return;

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

                var existing = _vm.ChatPreviews.FirstOrDefault(c => string.Equals(c.WithUserId, withUserId, StringComparison.Ordinal));
                if (existing != null)
                    existing.NonLetti = 0;
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
            ClearChatSelection();

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

                await _vm.TryLoadFromMemoryAsync();
                await _vm.StartAsync(myUid);
            }
            catch (Exception ex)
            {
                await ShowServerErrorPopupAsync("Errore", ex);
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Quando esco dalla pagina lista (es. apro una chat), sospendo aggiornamenti e fermo realtime
            _vm.SetSuspended(true);
            _vm.Stop();
        }

        private void ClearChatSelection()
        {
            if (_selectedChatIds.Count == 0)
                return;

            foreach (var chat in _vm.ChatPreviews)
                chat.IsSelected = false;

            _selectedChatIds.Clear();
            OnPropertyChanged(nameof(HasChatSelection));
        }

        private void ToggleChatSelection(ChatPreview chat, bool forceSelect = false)
        {
            if (chat == null)
                return;

            if (forceSelect)
            {
                chat.IsSelected = true;
                _selectedChatIds.Add(chat.WithUserId);
            }
            else
            {
                chat.IsSelected = !chat.IsSelected;
                if (chat.IsSelected)
                    _selectedChatIds.Add(chat.WithUserId);
                else
                    _selectedChatIds.Remove(chat.WithUserId);
            }

            OnPropertyChanged(nameof(HasChatSelection));
        }

        private void OnChatRowLoaded(object sender, EventArgs e)
        {
            if (sender is not VisualElement element)
                return;

            var effect = new TouchEffect { Capture = false };
            effect.TouchAction += OnChatRowTouch;
            element.Effects.Add(effect);
        }

        private void OnChatRowUnloaded(object sender, EventArgs e)
        {
            if (sender is not VisualElement element)
                return;

            if (_pressCts.TryGetValue(element, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _pressCts.Remove(element);
            }

            _longPressTriggered.Remove(element);

            var existing = element.Effects.OfType<TouchEffect>().FirstOrDefault();
            if (existing != null)
            {
                existing.TouchAction -= OnChatRowTouch;
                element.Effects.Remove(existing);
            }
        }

        private void OnChatRowTouch(object sender, TouchActionEventArgs args)
        {
            if (sender is not BindableObject bo || bo.BindingContext is not ChatPreview chat)
                return;

            if (sender is not VisualElement element)
                return;

            if (args.Type == TouchActionType.Pressed)
            {
                if (_pressCts.TryGetValue(element, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }

                var cts = new CancellationTokenSource();
                _pressCts[element] = cts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(LongPressMs, cts.Token);
                        if (cts.IsCancellationRequested)
                            return;

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            _longPressTriggered.Add(element);
                            ToggleChatSelection(chat, forceSelect: true);
                        });
                    }
                    catch (TaskCanceledException) { }
                }, cts.Token);
            }
            else if (args.Type == TouchActionType.Released || args.Type == TouchActionType.Cancelled)
            {
                if (_pressCts.TryGetValue(element, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    _pressCts.Remove(element);
                }

                var wasLongPress = _longPressTriggered.Remove(element);
                if (wasLongPress)
                    return;

                if (HasChatSelection)
                {
                    ToggleChatSelection(chat);
                }
                else
                {
                    _ = NavigateToChatAsync(chat.WithUserId, chat.Nickname);
                }
            }
        }

        private async void OnDeleteSelectedChats(object sender, EventArgs e)
        {
            if (!HasChatSelection)
                return;

            var confirm = await DisplayAlert("Chat", "Vuoi eliminare le chat selezionate dal dispositivo?", "Elimina", "Annulla");
            if (!confirm)
                return;

            var now = DateTimeOffset.UtcNow;
            foreach (var id in _selectedChatIds.ToList())
            {
                var chat = _vm.ChatPreviews.FirstOrDefault(c => string.Equals(c.WithUserId, id, StringComparison.Ordinal));
                if (chat != null)
                    ChatLocalState.SetClearedAt(chat.ChatId, now);
            }

            var remaining = _vm.ChatPreviews
                .Where(c => ListaViewModel.ShouldShowChat(c.ChatId, c.LastAtUtc))
                .ToList();
            _vm.ReplaceAll(remaining);

            ClearChatSelection();
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
            // no-op: Firestore realtime gestito dal ViewModel
        }

        private void StopRealtimeUpdates()
        {
            // no-op: Firestore realtime gestito dal ViewModel
        }
    }

    // ===================== ViewModel + Model =====================

    public sealed class ListaViewModel : RealtimeViewModelBase
    {
        public ObservableCollection<ChatPreview> ChatPreviews { get; } = new();

        private int _suspendedFlag; // 0/1 (thread-safe)
        private bool _listenerStarted;
        private readonly Dictionary<string, ChatPreview> _byPeer = new(StringComparer.Ordinal);
        private readonly FirestoreChatService _fsChat = new("biliardoapp");

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        public bool IsSuspended => Volatile.Read(ref _suspendedFlag) == 1;

        public void SetSuspended(bool suspended)
            => Volatile.Write(ref _suspendedFlag, suspended ? 1 : 0);

        private readonly FirestoreRealtimeService _realtime = new();
        private IDisposable? _chatListListener;
        private readonly ConcurrentDictionary<string, Task> _profileLoads = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Task> _unreadLoads = new(StringComparer.Ordinal);

        private static Task ShowServerErrorPopupAsync(string title, Exception ex)
        {
            var page = Application.Current?.MainPage;
            if (page == null)
                return Task.CompletedTask;

            var message = ex?.ToString() ?? "";
            return PopupErrorHelper.ShowAsync(page, title, message);
        }

        public void ReplaceAll(IReadOnlyList<ChatPreview> items)
        {
            if (IsSuspended)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (IsSuspended)
                    return;

                ChatPreviews.Clear();
                _byPeer.Clear();
                foreach (var it in items.Where(x => ShouldShowChat(x.ChatId, x.LastAtUtc)))
                {
                    ChatPreviews.Add(it);
                    _byPeer[it.WithUserId] = it;
                }

                ChatListMemoryCache.Instance.Set(items);
            });
        }

        public async Task TryLoadFromMemoryAsync()
        {
            if (IsSuspended)
                return;

            if (!ChatListMemoryCache.Instance.TryGet(out var cached))
                return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (IsSuspended)
                    return;

                ChatPreviews.Clear();
                _byPeer.Clear();
                foreach (var it in cached.Where(x => ShouldShowChat(x.ChatId, x.LastAtUtc)))
                {
                    ChatPreviews.Add(it);
                    _byPeer[it.WithUserId] = it;
                }
            });
        }

        public async Task StartAsync(string myUid)
        {
            // Se sospeso (es. sto navigando), non toccare la collection
            if (IsSuspended)
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login Firebase.");

                if (_listenerStarted)
                    return;

                _listenerStarted = true;
                _chatListListener = _realtime.SubscribeChatList(
                    myUid,
                    60,
                    items =>
                    {
                        if (IsSuspended)
                            return;

                        var ordered = items
                            .Where(c => !string.IsNullOrWhiteSpace(c.PeerUid))
                            .OrderByDescending(c => c.LastAtUtc ?? c.UpdatedAtUtc)
                            .ToList();

                        var newItems = new List<ChatPreview>(ordered.Count);
                        foreach (var c in ordered)
                        {
                            var whenUtc = c.LastAtUtc ?? c.UpdatedAtUtc ?? DateTimeOffset.MinValue;
                            var whenLocal = whenUtc == DateTimeOffset.MinValue
                                ? DateTime.MinValue
                                : whenUtc.ToLocalTime().DateTime;

                            var preview = c.LastText ?? "";
                            var type = c.LastType ?? "text";
                            if (string.IsNullOrWhiteSpace(preview))
                            {
                                preview = type switch
                                {
                                    "file" => "📎 Contenuto disponibile",
                                    _ => "Contenuto disponibile"
                                };
                            }

                            var nickname = string.IsNullOrWhiteSpace(c.PeerNickname) ? c.PeerUid : c.PeerNickname;
                            var chat = new ChatPreview(
                                chatId: c.ChatId,
                                withUserId: c.PeerUid,
                                nickname: nickname,
                                fullName: "",
                                avatarUrl: "",
                                avatarPath: "",
                                ultimoMessaggio: preview,
                                dataOra: whenLocal,
                                lastAtUtc: whenUtc,
                                nonLetti: 0,
                                isTyping: c.IsPeerTyping);

                            if (ShouldShowChat(chat.ChatId, chat.LastAtUtc))
                                newItems.Add(chat);
                        }

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            if (IsSuspended)
                                return;

                            ChatPreviews.Clear();
                            _byPeer.Clear();
                            foreach (var it in newItems)
                            {
                                ChatPreviews.Add(it);
                                _byPeer[it.WithUserId] = it;
                            }
                        });

                        ChatListMemoryCache.Instance.Set(newItems);

                        foreach (var it in newItems)
                        {
                            _ = EnsureProfileAsync(it.WithUserId);
                            _ = EnsureUnreadCountAsync(myUid, it.WithUserId, it.ChatId);
                        }
                    },
                    ex =>
                    {
                        Debug.WriteLine($"[ChatList] listener error: {ex}");
                        _ = ShowServerErrorPopupAsync("Errore download chat", ex);
                    });
                RegisterListener(_chatListListener);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void Stop()
        {
            _listenerStarted = false;
            ClearListeners();
        }

        private Task EnsureProfileAsync(string peerUid)
        {
            if (string.IsNullOrWhiteSpace(peerUid))
                return Task.CompletedTask;

            if (_profileLoads.TryGetValue(peerUid, out var existing))
                return existing;

            return _profileLoads.GetOrAdd(peerUid, _key => Task.Run(async () =>
            {
                try
                {
                    var profile = await FirestoreDirectoryService.GetUserPublicAsync(peerUid);
                    if (profile == null)
                        return;

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_byPeer.TryGetValue(peerUid, out var chat))
                        {
                            chat.UpdateProfile(
                                nickname: string.IsNullOrWhiteSpace(profile.Nickname) ? chat.Nickname : profile.Nickname,
                                firstName: profile.FirstName,
                                lastName: profile.LastName,
                                avatarUrl: profile.AvatarUrl,
                                avatarPath: profile.AvatarPath);
                        }
                    });
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    _profileLoads.TryRemove(peerUid, out _);
                }
            }));
        }

        private Task EnsureUnreadCountAsync(string myUid, string peerUid, string chatId)
        {
            if (string.IsNullOrWhiteSpace(peerUid) || string.IsNullOrWhiteSpace(chatId))
                return Task.CompletedTask;

            return _unreadLoads.GetOrAdd(peerUid, _key => Task.Run(async () =>
            {
                try
                {
                    var clearedAt = ChatLocalState.GetClearedAt(chatId);
                    var count = await _fsChat.GetUnreadCountAsync(chatId, myUid, clearedAt);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (_byPeer.TryGetValue(peerUid, out var current))
                            current.NonLetti = count;
                    });
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    _unreadLoads.TryRemove(peerUid, out _);
                }
            }));
        }

        public static bool ShouldShowChat(string chatId, DateTimeOffset lastAtUtc)
        {
            var clearedAt = ChatLocalState.GetClearedAt(chatId);
            if (clearedAt == null)
                return true;

            if (lastAtUtc == DateTimeOffset.MinValue)
                return false;

            return lastAtUtc > clearedAt.Value;
        }
    }

    public sealed class ChatPreview : BindableObject
    {
        public string WithUserId { get; }
        public string ChatId { get; }

        private string _nickname;
        private string _fullName;
        private string _avatarUrl;
        private string _avatarPath;
        private string _ultimoMessaggio;
        private bool _isTyping;
        private DateTime _dataOra;
        private DateTimeOffset _lastAtUtc;
        private int _nonLetti;
        private bool _isSelected;

        public string Nickname
        {
            get => _nickname;
            private set
            {
                _nickname = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(FullNameDisplay));
            }
        }

        public string FullName
        {
            get => _fullName;
            private set
            {
                _fullName = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFullName));
                OnPropertyChanged(nameof(FullNameDisplay));
            }
        }

        public string AvatarUrl
        {
            get => _avatarUrl;
            private set { _avatarUrl = value ?? ""; OnPropertyChanged(); }
        }

        public string AvatarPath
        {
            get => _avatarPath;
            private set { _avatarPath = value ?? ""; OnPropertyChanged(); }
        }

        public string UltimoMessaggio
        {
            get => _ultimoMessaggio;
            private set
            {
                _ultimoMessaggio = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewText));
            }
        }

        public bool IsTyping
        {
            get => _isTyping;
            private set
            {
                _isTyping = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PreviewText));
            }
        }

        public DateTime DataOra
        {
            get => _dataOra;
            private set
            {
                _dataOra = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeLabel));
            }
        }

        public DateTimeOffset LastAtUtc
        {
            get => _lastAtUtc;
            private set => _lastAtUtc = value;
        }

        public int NonLetti
        {
            get => _nonLetti;
            set
            {
                _nonLetti = Math.Max(0, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(UnreadBadgeVisible));
                OnPropertyChanged(nameof(UnreadBadgeText));
                OnPropertyChanged(nameof(UnreadBadgePadding));
                OnPropertyChanged(nameof(UnreadBadgeMinWidth));
                OnPropertyChanged(nameof(TimeColor));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public bool HasFullName => !string.IsNullOrWhiteSpace(FullName);
        public string FullNameDisplay => HasFullName ? $"({FullName})" : "";
        public string PreviewText => IsTyping ? "Sta scrivendo..." : UltimoMessaggio;
        public string TimeLabel => BuildTimeLabel(DataOra);
        public Color TimeColor => NonLetti > 0 ? Color.FromArgb("#25D366") : Color.FromArgb("#9A9A9A");
        public bool UnreadBadgeVisible => NonLetti > 0;
        public string UnreadBadgeText => NonLetti <= 0 ? "" : NonLetti.ToString();
        public Thickness UnreadBadgePadding => NonLetti < 10 ? new Thickness(6, 2) : new Thickness(8, 2);
        public double UnreadBadgeMinWidth => NonLetti < 10 ? 20 : 28;

        public ChatPreview(
            string chatId,
            string withUserId,
            string nickname,
            string fullName,
            string avatarUrl,
            string avatarPath,
            string ultimoMessaggio,
            DateTime dataOra,
            DateTimeOffset lastAtUtc,
            int nonLetti,
            bool isTyping)
        {
            ChatId = chatId ?? "";
            WithUserId = withUserId;
            _nickname = nickname ?? "";
            _fullName = fullName ?? "";
            _avatarUrl = avatarUrl ?? "";
            _avatarPath = avatarPath ?? "";
            _ultimoMessaggio = ultimoMessaggio ?? "";
            _dataOra = dataOra;
            _lastAtUtc = lastAtUtc;
            _nonLetti = Math.Max(0, nonLetti);
            _isTyping = isTyping;
        }

        public void UpdateProfile(string nickname, string firstName, string lastName, string avatarUrl, string avatarPath)
        {
            var fullName = BuildFullName(firstName, lastName);
            if (!string.IsNullOrWhiteSpace(nickname))
                Nickname = nickname;

            FullName = fullName;
            AvatarUrl = avatarUrl ?? "";
            AvatarPath = avatarPath ?? "";
        }

        public void UpdateLastMessage(string text, bool isTyping, DateTime whenLocal)
        {
            UltimoMessaggio = text ?? "";
            IsTyping = isTyping;
            DataOra = whenLocal;
            LastAtUtc = new DateTimeOffset(whenLocal).ToUniversalTime();
        }

        private static string BuildFullName(string? firstName, string? lastName)
        {
            var fn = (firstName ?? "").Trim();
            var ln = (lastName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fn) && string.IsNullOrWhiteSpace(ln))
                return "";
            if (string.IsNullOrWhiteSpace(fn))
                return ln;
            if (string.IsNullOrWhiteSpace(ln))
                return fn;
            return $"{fn} {ln}".Trim();
        }

        private static string BuildTimeLabel(DateTime whenLocal)
        {
            var today = DateTime.Today;
            if (whenLocal.Date == today)
                return whenLocal.ToString("HH:mm");
            if (whenLocal.Date == today.AddDays(-1))
                return "Ieri";
            return whenLocal.ToString("dd/MM/yy");
        }
    }
}
