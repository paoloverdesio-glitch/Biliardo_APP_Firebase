using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Biliardo.App.Componenti_UI;
using Biliardo.App.Infrastructure;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Servizi_Sicurezza;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) COLORI UI (FISSI SU TUTTI I DEVICE) - MODIFICA QUI
        // ============================================================
        private const string HEX_PAGE_BG = "#111111";
        private const string HEX_TOPBAR_BG = "#1A1A1A";

        // barra composer stile screenshot
        private const string HEX_COMPOSER_BAR_BG = "#2A2A2A";
        private const string HEX_ACCENT_GREEN = "#25D366";

        // Messaggi: inviati / ricevuti / testo
        private const string HEX_BUBBLE_MINE = "#075E54";   // verde scuro
        private const string HEX_BUBBLE_PEER = "#2A2A2A";   // grigio scuro
        private const string HEX_BUBBLE_TEXT = "#FFFFFF";   // testo bianco

        // ============================================================
        // 2) PARAMETRI VOCALE (MODIFICA QUI)
        // ============================================================
        private const int VOICE_MIN_SEND_MS = 1000;         // < 1s => scarta
        private const int VOICE_WAVE_HISTORY_MS = 5000;     // storico 5 secondi
        private const int VOICE_UI_TICK_MS = 80;            // tick update UI/onda
        private const float VOICE_WAVE_STROKE_PX = 2f;      // spessore linea onda

        // soglie gesture sul mic
        private const double MIC_CANCEL_DX = -110;          // swipe sinistra => cancella (solo hold, NON lock)
        private const double MIC_LOCK_DY = -110;            // swipe su => lock

        // ============================================================
        // VM/UI state
        // ============================================================
        public ObservableCollection<ChatMessageVm> Messaggi { get; } = new();
        public ObservableCollection<AttachmentVm> AllegatiSelezionati { get; } = new();
        public ObservableCollection<string> EmojiItems { get; } = new();

        private string _titoloChat = "Chat";
        public string TitoloChat { get => _titoloChat; set { _titoloChat = value; OnPropertyChanged(); } }

        private string _displayNomeCompleto = "";
        public string DisplayNomeCompleto
        {
            get => _displayNomeCompleto;
            set
            {
                _displayNomeCompleto = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDisplayNomeCompleto));
            }
        }

        public bool HasDisplayNomeCompleto => !string.IsNullOrWhiteSpace(DisplayNomeCompleto);

        private string _testoMessaggio = "";
        public string TestoMessaggio
        {
            get => _testoMessaggio;
            set
            {
                _testoMessaggio = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));
            }
        }

        public bool HasSelectedAttachments => AllegatiSelezionati.Count > 0;

        private bool _isSending;
        public bool CanSendTextOrAttachments => !_isSending && (!string.IsNullOrWhiteSpace(TestoMessaggio) || HasSelectedAttachments);

        // Mic visibile quando non c’è testo/allegati
        public bool CanShowMic => !_isSending
                                  && string.IsNullOrWhiteSpace(TestoMessaggio)
                                  && !HasSelectedAttachments;

        private bool _isLoadingMessages;
        public bool IsLoadingMessages { get => _isLoadingMessages; set { _isLoadingMessages = value; OnPropertyChanged(); } }

        // Emoji panel
        private bool _isEmojiPanelVisible;
        public bool IsEmojiPanelVisible
        {
            get => _isEmojiPanelVisible;
            set
            {
                _isEmojiPanelVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EmojiButtonIcon));
            }
        }

        public string EmojiButtonIcon => IsEmojiPanelVisible ? "ic_keyboard.png" : "ic_emoji.png";

        // Normal composer bar visibile solo se NON sto registrando
        public bool IsNormalComposerVisible => !_voice.IsRecording;

        // Dimensioni dinamiche
        private double _bubbleMaxWidth = 320;
        public double BubbleMaxWidth { get => _bubbleMaxWidth; set { _bubbleMaxWidth = value; OnPropertyChanged(); } }

        private double _voiceLockPanelHeight = 180;
        public double VoiceLockPanelHeight { get => _voiceLockPanelHeight; set { _voiceLockPanelHeight = value; OnPropertyChanged(); } }

        // Colori per binding XAML
        public Color PageBgColor => Color.FromArgb(HEX_PAGE_BG);
        public Color TopBarBgColor => Color.FromArgb(HEX_TOPBAR_BG);
        public Color ComposerBarBgColor => Color.FromArgb(HEX_COMPOSER_BAR_BG);
        public Color AccentGreenColor => Color.FromArgb(HEX_ACCENT_GREEN);

        public Color BubbleMineColor => Color.FromArgb(HEX_BUBBLE_MINE);
        public Color BubblePeerColor => Color.FromArgb(HEX_BUBBLE_PEER);
        public Color BubbleTextColor => Color.FromArgb(HEX_BUBBLE_TEXT);

        // ============================================================
        // Chat context
        // ============================================================
        private readonly string? _peerUserId;
        private readonly string? _peerNickname;
        private bool _peerProfileLoaded;
        private string? _lastIdToken;
        private string? _lastMyUid;
        private string? _lastPeerId;
        private string? _lastChatId;

        private CancellationTokenSource? _pollCts;
        private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(1200);
        private string _lastUiSignature = "";
        private bool _userNearBottom = true;

        private readonly FirestoreChatService _fsChat = new("biliardoapp");
        private string? _chatIdCached;

        private readonly ScrollWorkCoordinator _scrollCoordinator = new();
        private CollectionViewNativeScrollStateTracker? _scrollTracker;
        private readonly ChatLocalCache _localCache = new();
        private readonly SemaphoreSlim _loadLock = new(1, 1);
        private bool _fastLoadStarted;
        private bool _hasRenderedInitialMessages;
        private bool _backfillStarted;
        private bool _isPagingOlder;
        private int _lastFirstVisibleIndex;
        private bool _pendingOlderLoad;
        private DateTimeOffset? _oldestMessageUtc;
        private DateTimeOffset? _newestMessageUtc;

        private const int FastInitialLimit = 20;
        private const int BackfillLimit = 80;
        private const int WindowMaxMessages = 200;
        private const int CacheMaxMessages = 50;
        private const int OlderPageSize = 40;
        private const int OlderTriggerThreshold = 8;
        private static readonly TimeSpan ScrollIdleDelay = TimeSpan.FromMilliseconds(280);

        // Prefetch foto (visibili + 5)
        private readonly HashSet<string> _prefetchPhotoMessageIds = new(StringComparer.Ordinal);
        private CancellationTokenSource? _prefetchCts;

        // ============================================================
        // Voice (WhatsApp-like)
        // ============================================================
        private readonly IVoiceRecorder _voice = VoiceMediaFactory.CreateRecorder();
        private readonly SemaphoreSlim _voiceOpLock = new(1, 1);

        private bool _micIsPressed;
        private bool _voiceLocked;
        private bool _voiceCanceledBySwipe;

        private string _voiceTimeLabel = "00:00";
        public string VoiceTimeLabel { get => _voiceTimeLabel; set { _voiceTimeLabel = value; OnPropertyChanged(); } }

        public bool IsVoiceHoldStripVisible => _voice.IsRecording && !_voiceLocked && !_voiceCanceledBySwipe;
        public bool IsVoiceLockPanelVisible => _voice.IsRecording && _voiceLocked && !_voiceCanceledBySwipe;

        public string VoicePauseResumeLabel => _voice.IsPaused ? "REC" : "||";

        private string? _voiceFilePath;
        private long _voiceDurationMs;

        private CancellationTokenSource? _voiceUiCts;
        private readonly VoiceWaveDrawable _voiceWave;

        // Modali: evita stop polling quando apro un modal (foto fullscreen, bottom sheet, ecc.)
        private bool _suppressStopPollingOnce;

        // ============================================================
        // Playback audio messaggi ricevuti (Android reale, altri stub)
        // ============================================================
        private IAudioPlayback _playback;

        public Pagina_MessaggiDettaglio()
        {
            InitializeComponent();
            BindingContext = this;

            _playback = AudioPlaybackFactory.Create();

            _voiceWave = new VoiceWaveDrawable(
                historyMs: VOICE_WAVE_HISTORY_MS,
                tickMs: VOICE_UI_TICK_MS,
                strokePx: VOICE_WAVE_STROKE_PX);

            try
            {
                VoiceWaveHoldView.Drawable = _voiceWave;
                VoiceWaveLockView.Drawable = _voiceWave;
            }
            catch { }

            TitoloChat = "Chat";
            DisplayNomeCompleto = "";

            ChatScrollTuning.Apply(CvMessaggi);

            AllegatiSelezionati.CollectionChanged += (_, __) =>
            {
                if (AllegatiSelezionati.Count > 0 && IsEmojiPanelVisible)
                    IsEmojiPanelVisible = false;

                OnPropertyChanged(nameof(HasSelectedAttachments));
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));
            };

            foreach (var e in new[]
                     { "😀","😁","😂","🤣","😊","😍","😘","😎",
                       "😅","😇","😉","🙂","🤔","😴","😭","😡",
                       "👍","👎","🙏","👏","🎉","🔥","❤️","💪",
                       "⚽","🎱","📎","📷","🎤","🎬","📍","👤" })
                EmojiItems.Add(e);

            RefreshVoiceBindings();
        }

        public Pagina_MessaggiDettaglio(string peerUserId, string peerNickname) : this()
        {
            _peerUserId = (peerUserId ?? "").Trim();
            _peerNickname = (peerNickname ?? "").Trim();

            TitoloChat = string.IsNullOrWhiteSpace(_peerNickname) ? "Chat" : _peerNickname!;
            DisplayNomeCompleto = "";
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();

            if (Messaggi.Count == 0)
                IsLoadingMessages = true;

            if (!_fastLoadStarted)
            {
                _fastLoadStarted = true;
                _ = FastInitialLoadAsync();
            }

            if (_scrollTracker == null)
                _scrollTracker = CollectionViewNativeScrollStateTracker.Attach(CvMessaggi, _scrollCoordinator, ScrollIdleDelay);

            StartPolling();
            RefreshVoiceBindings();
            _ = EnsurePeerProfileAsync();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            if (_suppressStopPollingOnce)
            {
                _suppressStopPollingOnce = false;
                return;
            }

            StopPolling();
            StopVoiceUiLoop();
            _playback.StopPlaybackSafe();
            CancelPrefetch();
            _scrollCoordinator.ClearPending();
            _scrollTracker?.Dispose();
            _scrollTracker = null;
            _backfillStarted = false;
            _isPagingOlder = false;
            _pendingOlderLoad = false;

            // sicurezza: se esco mentre registro, cancello
            _ = Task.Run(async () =>
            {
                await _voiceOpLock.WaitAsync();
                try
                {
                    if (_voice.IsRecording)
                        await SafeCancelVoiceAsync();
                }
                catch { }
                finally { _voiceOpLock.Release(); }
            });
        }

        private async Task EnsurePeerProfileAsync()
        {
            if (_peerProfileLoaded)
                return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            _peerProfileLoaded = true;

            try
            {
                var profile = await FirestoreDirectoryService.GetUserPublicAsync(peerId);
                if (profile == null)
                    return;

                var display = BuildDisplayNomeCompleto(profile.FirstName, profile.LastName);
                DisplayNomeCompleto = display;
            }
            catch { }
        }

        private static string BuildDisplayNomeCompleto(string? firstName, string? lastName)
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

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            if (width > 0)
            {
                var w = width * 0.72;
                BubbleMaxWidth = Math.Clamp(w, 220, 520);
            }

            if (height > 0)
            {
                var h = height * 0.20;
                VoiceLockPanelHeight = Math.Max(160, h);
            }
        }

        // ============================================================
        // UI handlers
        // ============================================================
        private async void OnBackClicked(object sender, EventArgs e)
        {
            try { await Navigation.PopAsync(); } catch { }
        }

        private void OnComposerTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible) IsEmojiPanelVisible = false;
                EntryText?.Focus();
            }
            catch { }
        }

        private void OnEntryFocused(object sender, FocusEventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible)
                    IsEmojiPanelVisible = false;
            }
            catch { }
        }

        private void OnEmojiToggleClicked(object sender, EventArgs e)
        {
            try
            {
                if (!IsEmojiPanelVisible)
                {
                    IsEmojiPanelVisible = true;
                    EntryText?.Unfocus();
                }
                else
                {
                    IsEmojiPanelVisible = false;
                    EntryText?.Focus();
                }
            }
            catch { }
        }

        private void OnEmojiSelected(object sender, EventArgs e)
        {
            try
            {
                if (sender is Button b && b.CommandParameter is string emo)
                    TestoMessaggio = (TestoMessaggio ?? "") + emo;
            }
            catch { }
        }

        private async void OnClipClicked(object sender, EventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible) IsEmojiPanelVisible = false;

                var sheet = new BottomSheetAllegatiPage();
                sheet.AzioneSelezionata += async (_, az) =>
                {
                    await HandleAttachmentActionAsync(az);
                };

                _suppressStopPollingOnce = true;
                await Navigation.PushModalAsync(sheet);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnCameraClicked(object sender, EventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible) IsEmojiPanelVisible = false;

                var choice = await DisplayActionSheet("Fotocamera", "Annulla", null, "Foto", "Video");
                if (choice == "Foto")
                    await CapturePhotoAsync();
                else if (choice == "Video")
                    await CaptureVideoAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private void OnRemoveAttachmentClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is Button b && b.CommandParameter is AttachmentVm a)
                    AllegatiSelezionati.Remove(a);
            }
            catch { }
        }

        private void OnMessagesScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            _scrollCoordinator.NotifyActivity();
            _userNearBottom = e.LastVisibleItemIndex >= (Messaggi.Count - 2);
            _lastFirstVisibleIndex = e.FirstVisibleItemIndex;

            var first = Math.Max(0, e.FirstVisibleItemIndex);
            var last = Math.Min(Messaggi.Count - 1, e.LastVisibleItemIndex + 5);
            _ = SchedulePrefetchPhotosAsync(first, last);

            ScheduleOlderPagingIfNeeded();
        }

        // ============================================================
        // Invio testo/allegati
        // ============================================================
        private async void OnInviaClicked(object sender, EventArgs e)
        {
            if (_isSending) return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
            {
                await DisplayAlert("Errore", "Peer non valido (userId mancante).", "OK");
                return;
            }

            _isSending = true;
            OnPropertyChanged(nameof(CanSendTextOrAttachments));
            OnPropertyChanged(nameof(CanShowMic));

            try
            {
                var provider = await SessionePersistente.GetProviderAsync();
                if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Questa pagina è implementata per provider Firebase.");

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

                var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

                var text = (TestoMessaggio ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var msgId = Guid.NewGuid().ToString("N");
                    await _fsChat.SendTextMessageWithIdAsync(idToken!, chatId, msgId, myUid!, text);
                }

                var items = AllegatiSelezionati.ToList();
                foreach (var a in items)
                    await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, a);

                TestoMessaggio = "";
                AllegatiSelezionati.Clear();

                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Invio non riuscito: {ex.Message}", "OK");
            }
            finally
            {
                _isSending = false;
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));
            }
        }

        // ============================================================
        // FOTO: tap fullscreen + open media (file/video)
        // ============================================================
        private async void OnPhotoTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not ChatMessageVm m)
                    return;

                var path = await EnsureMediaDownloadedAsync(m);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                var img = new Image { Source = path, Aspect = Aspect.AspectFit };

                var close = new Button
                {
                    Text = "✕",
                    BackgroundColor = Colors.Transparent,
                    TextColor = Colors.White,
                    WidthRequest = 44,
                    HeightRequest = 44,
                    FontSize = 18,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Start,
                    Margin = new Thickness(10)
                };

                var page = new ContentPage
                {
                    BackgroundColor = Colors.Black,
                    Content = new Grid { Children = { img, close } }
                };

                close.Clicked += async (_, __) => await Navigation.PopModalAsync();

                _suppressStopPollingOnce = true;
                await Navigation.PushModalAsync(page);
            }
            catch { }
        }

        private async void OnOpenMediaClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is not Button b || b.CommandParameter is not ChatMessageVm m)
                    return;

                if (!m.IsPhoto && !m.IsFileOrVideo)
                    return;

                var path = await EnsureMediaDownloadedAsync(m);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    await DisplayAlert("Info", "File non disponibile.", "OK");
                    return;
                }

                m.MediaLocalPath = path;

                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    File = new ReadOnlyFile(path)
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnOpenLocationClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is not Button b || b.CommandParameter is not ChatMessageVm m)
                    return;

                if (!m.IsLocation || m.Latitude == null || m.Longitude == null)
                    return;

                var lat = m.Latitude.Value;
                var lon = m.Longitude.Value;

                var url = $"https://www.google.com/maps?q={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";
                await Launcher.Default.OpenAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnCallContactClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is not Button b || b.CommandParameter is not ChatMessageVm m)
                    return;

                if (!m.IsContact || string.IsNullOrWhiteSpace(m.ContactPhone))
                    return;

                PhoneDialer.Default.Open(m.ContactPhone);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async void OnPlayAudioClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is not Button b || b.CommandParameter is not ChatMessageVm m)
                    return;

                if (!m.IsAudio)
                    return;

                if (m.IsAudioPlaying)
                {
                    _playback.StopPlaybackSafe();
                    m.IsAudioPlaying = false;
                    return;
                }

                foreach (var x in Messaggi.Where(x => x.IsAudioPlaying))
                    x.IsAudioPlaying = false;

                var path = await EnsureMediaDownloadedAsync(m);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    await DisplayAlert("Info", "Audio non disponibile.", "OK");
                    return;
                }

                m.IsAudioPlaying = true;
                await _playback.PlayAsync(path);
                m.IsAudioPlaying = false;
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        // ============================================================
        // VOCALE - Mic Press/Hold + Pan (robusto: Released + fallback Pan Completed/Canceled)
        // ============================================================
        private async void OnMicPressed(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (_voice.IsRecording)
                    return;

                _micIsPressed = true;
                _voiceLocked = false;
                _voiceCanceledBySwipe = false;

                _voiceDurationMs = 0;
                _voiceFilePath = Path.Combine(FileSystem.CacheDirectory, $"voice_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.m4a");

                var st = await Permissions.RequestAsync<Permissions.Microphone>();
                if (st != PermissionStatus.Granted)
                {
                    _micIsPressed = false;
                    _voiceLocked = false;
                    _voiceCanceledBySwipe = false;
                    RefreshVoiceBindings();
                    return;
                }

                await _voice.StartAsync(_voiceFilePath);
                _voiceWave.Reset();

                StartVoiceUiLoop();
                RefreshVoiceBindings();
            }
            catch
            {
                _micIsPressed = false;
                try { await SafeCancelVoiceAsync(); } catch { }
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnMicReleased(object sender, EventArgs e)
        {
            await HandleMicPointerUpAsync();
        }

        private async void OnMicPanUpdated(object sender, PanUpdatedEventArgs e)
        {
            try
            {
                // Fallback fondamentale: alcuni casi non chiamano Released dopo swipe.
                if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                {
                    await HandleMicPointerUpAsync();
                    return;
                }

                if (!_micIsPressed || !_voice.IsRecording || _voiceCanceledBySwipe)
                    return;

                if (e.StatusType != GestureStatus.Running)
                    return;

                if (!_voiceLocked && e.TotalY <= MIC_LOCK_DY)
                {
                    _voiceLocked = true;
                    RefreshVoiceBindings();
                    return;
                }

                if (!_voiceLocked && e.TotalX <= MIC_CANCEL_DX)
                {
                    _voiceCanceledBySwipe = true;
                    await SafeCancelVoiceAsync();
                    RefreshVoiceBindings();
                    return;
                }
            }
            catch { }
        }

        private async Task HandleMicPointerUpAsync()
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                _micIsPressed = false;

                if (!_voice.IsRecording)
                {
                    RefreshVoiceBindings();
                    return;
                }

                if (_voiceCanceledBySwipe)
                {
                    RefreshVoiceBindings();
                    return;
                }

                // Se lock, il rilascio NON deve fermare la registrazione.
                if (_voiceLocked)
                {
                    RefreshVoiceBindings();
                    return;
                }

                var (filePath, ms) = await StopVoiceAndGetFileAsync();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    RefreshVoiceBindings();
                    return;
                }

                if (ms < VOICE_MIN_SEND_MS)
                {
                    TryDeleteFile(filePath);
                    RefreshVoiceBindings();
                    return;
                }

                await SendVoiceFileAsync(filePath, ms);
                TryDeleteFile(filePath);

                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);

                RefreshVoiceBindings();
            }
            catch
            {
                try { await SafeCancelVoiceAsync(); } catch { }
                RefreshVoiceBindings();
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnVoiceTrashClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                _voiceCanceledBySwipe = true;
                await SafeCancelVoiceAsync();
                RefreshVoiceBindings();
            }
            catch { }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnVoicePauseResumeClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (!_voice.IsRecording) return;
                if (!_voiceLocked) return;

                if (_voice.IsPaused)
                    await _voice.ResumeAsync();
                else
                    await _voice.PauseAsync();

                RefreshVoiceBindings();
            }
            catch { }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private async void OnVoiceSendClicked(object sender, EventArgs e)
        {
            await _voiceOpLock.WaitAsync();
            try
            {
                if (!_voice.IsRecording) return;
                if (!_voiceLocked) return;

                var (filePath, ms) = await StopVoiceAndGetFileAsync();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    RefreshVoiceBindings();
                    return;
                }

                if (ms < VOICE_MIN_SEND_MS)
                {
                    TryDeleteFile(filePath);
                    RefreshVoiceBindings();
                    return;
                }

                await SendVoiceFileAsync(filePath, ms);
                TryDeleteFile(filePath);

                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);

                RefreshVoiceBindings();
            }
            catch
            {
                try { await SafeCancelVoiceAsync(); } catch { }
                RefreshVoiceBindings();
            }
            finally
            {
                _voiceOpLock.Release();
            }
        }

        private void StartVoiceUiLoop()
        {
            StopVoiceUiLoop();

            _voiceUiCts = new CancellationTokenSource();
            var token = _voiceUiCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (!_voice.IsRecording)
                            break;

                        var level = _voice.TryGetLevel01();
                        var ms = _voice.GetElapsedMs();

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            VoiceTimeLabel = FormatMs(ms);

                            _voiceWave.AddSample((float)level);
                            VoiceWaveHoldView.Invalidate();
                            VoiceWaveLockView.Invalidate();

                            OnPropertyChanged(nameof(IsVoiceHoldStripVisible));
                            OnPropertyChanged(nameof(IsVoiceLockPanelVisible));
                            OnPropertyChanged(nameof(IsNormalComposerVisible));
                            OnPropertyChanged(nameof(VoicePauseResumeLabel));
                        });

                        await Task.Delay(VOICE_UI_TICK_MS, token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, token);
        }

        private void StopVoiceUiLoop()
        {
            try { _voiceUiCts?.Cancel(); } catch { }
            try { _voiceUiCts?.Dispose(); } catch { }
            _voiceUiCts = null;
        }

        private async Task SafeCancelVoiceAsync()
        {
            try { await _voice.CancelAsync(); } catch { }
            StopVoiceUiLoop();

            if (!string.IsNullOrWhiteSpace(_voiceFilePath))
                TryDeleteFile(_voiceFilePath);

            _voiceFilePath = null;
            _voiceDurationMs = 0;
            VoiceTimeLabel = "00:00";

            _micIsPressed = false;
            _voiceLocked = false;
            _voiceCanceledBySwipe = false;

            _voiceWave.Reset();

            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VoiceWaveHoldView.Invalidate();
                    VoiceWaveLockView.Invalidate();
                });
            }
            catch { }
        }

        private async Task<(string? filePath, long ms)> StopVoiceAndGetFileAsync()
        {
            if (!_voice.IsRecording)
                return (null, 0);

            try { await _voice.StopAsync(); } catch { }
            StopVoiceUiLoop();

            var filePath = _voice.CurrentFilePath ?? _voiceFilePath;
            var ms = _voice.GetElapsedMs();

            _voiceFilePath = null;
            _voiceDurationMs = 0;
            VoiceTimeLabel = "00:00";

            _micIsPressed = false;
            _voiceLocked = false;
            _voiceCanceledBySwipe = false;

            _voiceWave.Reset();
            try
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VoiceWaveHoldView.Invalidate();
                    VoiceWaveLockView.Invalidate();
                });
            }
            catch { }

            return (filePath, ms);
        }

        private async Task SendVoiceFileAsync(string filePath, long durationMs)
        {
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            var myUid = FirebaseSessionePersistente.GetLocalId();

            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

            var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

            var a = new AttachmentVm
            {
                Kind = AttachmentKind.Audio,
                DisplayName = Path.GetFileName(filePath),
                LocalPath = filePath,
                ContentType = "audio/mp4",
                SizeBytes = new FileInfo(filePath).Length,
                DurationMs = durationMs > 0 ? durationMs : MediaMetadataHelper.TryGetDurationMs(filePath)
            };

            await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, a);
        }

        private void RefreshVoiceBindings()
        {
            OnPropertyChanged(nameof(CanShowMic));
            OnPropertyChanged(nameof(IsVoiceHoldStripVisible));
            OnPropertyChanged(nameof(IsVoiceLockPanelVisible));
            OnPropertyChanged(nameof(VoicePauseResumeLabel));
            OnPropertyChanged(nameof(IsNormalComposerVisible));
        }

        private static string FormatMs(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            if (ts.TotalHours >= 1)
                return ts.ToString(@"hh\:mm\:ss");
            return ts.ToString(@"mm\:ss");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        // ============================================================
        // Attachments actions
        // ============================================================
        private async Task HandleAttachmentActionAsync(BottomSheetAllegatiPage.AllegatoAzione az)
        {
            switch (az)
            {
                case BottomSheetAllegatiPage.AllegatoAzione.Gallery:
                    await PickFromGalleryAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Camera:
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var choice = await DisplayActionSheet("Fotocamera", "Annulla", null, "Foto", "Video");
                        if (choice == "Foto") await CapturePhotoAsync();
                        else if (choice == "Video") await CaptureVideoAsync();
                    });
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Document:
                    await PickDocumentAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Audio:
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await DisplayAlert("Vocale", "Usa il tasto microfono (pressione lunga) nella barra in basso.", "OK");
                    });
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Location:
                    await AttachLocationAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Contact:
                    await AttachContactAsync();
                    break;
            }
        }

        private async Task PickFromGalleryAsync()
        {
            var choice = await DisplayActionSheet("Galleria", "Annulla", null, "Foto", "Video");
            if (choice == "Foto")
            {
                var fr = await MediaPicker.Default.PickPhotoAsync();
                if (fr == null) return;

                var local = await CopyToCacheAsync(fr, "photo");
                AllegatiSelezionati.Add(new AttachmentVm
                {
                    Kind = AttachmentKind.Photo,
                    DisplayName = Path.GetFileName(local),
                    LocalPath = local,
                    ContentType = fr.ContentType ?? MediaMetadataHelper.GuessContentType(local),
                    SizeBytes = new FileInfo(local).Length
                });
            }
            else if (choice == "Video")
            {
                var fr = await MediaPicker.Default.PickVideoAsync();
                if (fr == null) return;

                var local = await CopyToCacheAsync(fr, "video");
                AllegatiSelezionati.Add(new AttachmentVm
                {
                    Kind = AttachmentKind.Video,
                    DisplayName = Path.GetFileName(local),
                    LocalPath = local,
                    ContentType = fr.ContentType ?? MediaMetadataHelper.GuessContentType(local),
                    SizeBytes = new FileInfo(local).Length,
                    DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
                });
            }
        }

        private async Task CapturePhotoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CapturePhotoAsync();
            if (fr == null) return;

            var local = await CopyToCacheAsync(fr, "camera_photo");
            AllegatiSelezionati.Add(new AttachmentVm
            {
                Kind = AttachmentKind.Photo,
                DisplayName = Path.GetFileName(local),
                LocalPath = local,
                ContentType = fr.ContentType ?? MediaMetadataHelper.GuessContentType(local),
                SizeBytes = new FileInfo(local).Length
            });
        }

        private async Task CaptureVideoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CaptureVideoAsync();
            if (fr == null) return;

            var local = await CopyToCacheAsync(fr, "camera_video");
            AllegatiSelezionati.Add(new AttachmentVm
            {
                Kind = AttachmentKind.Video,
                DisplayName = Path.GetFileName(local),
                LocalPath = local,
                ContentType = fr.ContentType ?? MediaMetadataHelper.GuessContentType(local),
                SizeBytes = new FileInfo(local).Length,
                DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
            });
        }

        private async Task PickDocumentAsync()
        {
            var res = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Seleziona documento" });
            if (res == null) return;

            var local = await CopyToCacheAsync(res, "doc");
            AllegatiSelezionati.Add(new AttachmentVm
            {
                Kind = AttachmentKind.File,
                DisplayName = Path.GetFileName(local),
                LocalPath = local,
                ContentType = res.ContentType ?? MediaMetadataHelper.GuessContentType(local),
                SizeBytes = new FileInfo(local).Length
            });
        }

        private async Task AttachLocationAsync()
        {
            try
            {
                var req = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(10));
                var loc = await Geolocation.Default.GetLocationAsync(req);

                if (loc == null)
                {
                    await DisplayAlert("Posizione", "Impossibile ottenere la posizione.", "OK");
                    return;
                }

                AllegatiSelezionati.Add(new AttachmentVm
                {
                    Kind = AttachmentKind.Location,
                    DisplayName = "Posizione",
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async Task AttachContactAsync()
        {
            try
            {
                var c = await Contacts.Default.PickContactAsync();
                if (c == null) return;

                var name = (c.DisplayName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    var parts = new List<string>();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(c.GivenName)) parts.Add(c.GivenName.Trim());
                        if (!string.IsNullOrWhiteSpace(c.FamilyName)) parts.Add(c.FamilyName.Trim());
                    }
                    catch { }
                    name = string.Join(" ", parts).Trim();
                }
                if (string.IsNullOrWhiteSpace(name))
                    name = "Contatto";

                var phone = (c.Phones?.FirstOrDefault()?.PhoneNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(phone))
                {
                    await DisplayAlert("Contatto", "Contatto senza numero telefonico.", "OK");
                    return;
                }

                AllegatiSelezionati.Add(new AttachmentVm
                {
                    Kind = AttachmentKind.Contact,
                    DisplayName = name,
                    ContactName = name,
                    ContactPhone = phone
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private static async Task<string> CopyToCacheAsync(FileResult fr, string prefix)
        {
            var ext = Path.GetExtension(fr.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

            var dest = Path.Combine(FileSystem.CacheDirectory, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");

            await using var src = await fr.OpenReadAsync();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst);

            return dest;
        }

        // ============================================================
        // Polling / Load + receipts + date separators + scroll safe
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

        private static string ComputeUiSignature(List<FirestoreChatService.MessageItem> ordered)
        {
            var hc = new HashCode();
            for (int i = 0; i < ordered.Count; i++)
            {
                var m = ordered[i];
                hc.Add(m.MessageId);
                hc.Add(m.SenderId);
                hc.Add(m.Type);
                hc.Add(m.DeliveredTo?.Count ?? 0);
                hc.Add(m.ReadBy?.Count ?? 0);
            }
            return hc.ToHashCode().ToString("X");
        }

        private async Task FastInitialLoadAsync()
        {
            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var myUid = FirebaseSessionePersistente.GetLocalId();
            if (string.IsNullOrWhiteSpace(myUid))
                return;

            var cacheKey = _localCache.GetCacheKey(_chatIdCached, peerId);
            var swCache = Stopwatch.StartNew();
            var cached = await _localCache.TryReadAsync(cacheKey, CancellationToken.None);
            swCache.Stop();
            Debug.WriteLine($"[ChatLoad] Cache read {cached.Count} in {swCache.ElapsedMilliseconds}ms");

            if (cached.Count > 0)
            {
                var ordered = cached.OrderBy(m => m.CreatedAtUtc).ToList();
                var sig = ComputeUiSignature(ordered);
                await ApplyMessageDiffAsync(ordered, sig, idToken: "", myUid!, peerId, chatId: "", ct: CancellationToken.None, allowTrim: true, applyReceipts: false, source: "cache");
            }

            try
            {
                var provider = await SessionePersistente.GetProviderAsync();
                if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                    return;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return;

                var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);
                _lastIdToken = idToken;
                _lastMyUid = myUid;
                _lastPeerId = peerId;
                _lastChatId = chatId;

                var swFetch = Stopwatch.StartNew();
                var latest = await _fsChat.GetLastMessagesAsync(idToken!, chatId, limit: FastInitialLimit, ct: CancellationToken.None);
                swFetch.Stop();
                Debug.WriteLine($"[ChatLoad] Fast fetch {latest.Count} in {swFetch.ElapsedMilliseconds}ms");

                var ordered = latest.OrderBy(m => m.CreatedAtUtc).ToList();
                var sig = ComputeUiSignature(ordered);
                await ApplyMessageDiffAsync(ordered, sig, idToken!, myUid!, peerId, chatId, CancellationToken.None, allowTrim: true, applyReceipts: true, source: "fast");

                if (!_backfillStarted)
                {
                    _backfillStarted = true;
                    _ = StartBackfillAsync(idToken!, myUid!, peerId, chatId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ChatLoad] Fast load failed: {ex.Message}");
            }
        }

        private async Task StartBackfillAsync(string idToken, string myUid, string peerId, string chatId)
        {
            var swFetch = Stopwatch.StartNew();
            var msgs = await _fsChat.GetLastMessagesAsync(idToken, chatId, limit: BackfillLimit, ct: CancellationToken.None);
            swFetch.Stop();
            Debug.WriteLine($"[ChatLoad] Backfill fetch {msgs.Count} in {swFetch.ElapsedMilliseconds}ms");

            var ordered = msgs.OrderBy(m => m.CreatedAtUtc).ToList();
            var sig = ComputeUiSignature(ordered);
            await ApplyMessageDiffAsync(ordered, sig, idToken, myUid, peerId, chatId, CancellationToken.None, allowTrim: true, applyReceipts: true, source: "backfill");

            _ = _localCache.WriteAsync(_localCache.GetCacheKey(chatId, peerId), ordered, CacheMaxMessages, CancellationToken.None);
        }

        private void ScheduleOlderPagingIfNeeded()
        {
            if (_pendingOlderLoad || _isPagingOlder)
                return;

            if (_lastFirstVisibleIndex > OlderTriggerThreshold)
                return;

            _pendingOlderLoad = true;
            _scrollCoordinator.EnqueueUiWork(async () =>
            {
                _pendingOlderLoad = false;
                if (_lastFirstVisibleIndex > OlderTriggerThreshold)
                    return;

                await LoadOlderMessagesAsync();
            }, "page-older");
        }

        private async Task LoadOlderMessagesAsync()
        {
            if (_isPagingOlder)
                return;

            if (_oldestMessageUtc == null)
                return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var provider = await SessionePersistente.GetProviderAsync();
            if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                return;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            var myUid = FirebaseSessionePersistente.GetLocalId();
            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                return;

            var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);
            _isPagingOlder = true;

            try
            {
                var swFetch = Stopwatch.StartNew();
                var older = await _fsChat.GetMessagesBeforeAsync(idToken!, chatId, _oldestMessageUtc.Value, limit: OlderPageSize, ct: CancellationToken.None);
                swFetch.Stop();
                Debug.WriteLine($"[ChatLoad] Older fetch {older.Count} in {swFetch.ElapsedMilliseconds}ms");

                if (older.Count == 0)
                    return;

                var ordered = older.OrderBy(m => m.CreatedAtUtc).ToList();
                var sig = ComputeUiSignature(ordered);
                await ApplyMessageDiffAsync(ordered, sig, idToken!, myUid!, peerId, chatId, CancellationToken.None, allowTrim: true, applyReceipts: false, source: "older");
            }
            finally
            {
                _isPagingOlder = false;
            }
        }

        private async Task ApplyMessageDiffOnUiAsync(
            List<FirestoreChatService.MessageItem> ordered,
            string myUid,
            string peerId,
            bool allowTrim,
            string source)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var swApply = Stopwatch.StartNew();

                var existingById = new Dictionary<string, ChatMessageVm>(StringComparer.Ordinal);
                foreach (var vm in Messaggi.Where(x => !x.IsDateSeparator))
                {
                    if (!string.IsNullOrWhiteSpace(vm.Id))
                        existingById[vm.Id] = vm;
                }

                var newMessages = ordered.Where(m => !existingById.ContainsKey(m.MessageId)).ToList();
                var updates = ordered.Where(m => existingById.ContainsKey(m.MessageId)).ToList();

                foreach (var m in updates)
                {
                    if (!existingById.TryGetValue(m.MessageId, out var existing))
                        continue;

                    if (existing.IsMine)
                    {
                        var delivered = (m.DeliveredTo ?? Array.Empty<string>()).Contains(peerId, StringComparer.Ordinal);
                        var read = (m.ReadBy ?? Array.Empty<string>()).Contains(peerId, StringComparer.Ordinal);
                        var newStatus = (read || delivered) ? "✓✓" : "✓";
                        if (!string.Equals(existing.StatusLabel, newStatus, StringComparison.Ordinal))
                            existing.StatusLabel = newStatus;
                    }
                }

                var insertedCount = 0;
                var appended = false;

                if (Messaggi.Count == 0)
                {
                    AppendMessagesWithSeparators(ordered, myUid, peerId, null);
                    insertedCount = ordered.Count;
                    appended = true;
                }
                else if (newMessages.Count > 0)
                {
                    var allOlder = _oldestMessageUtc != null && newMessages.Max(m => m.CreatedAtUtc) < _oldestMessageUtc.Value;
                    if (allOlder)
                    {
                        var anchor = CaptureAnchor();
                        InsertOlderMessagesWithSeparators(newMessages, myUid, peerId);
                        insertedCount = newMessages.Count;
                        RestoreAnchor(anchor);
                    }
                    else
                    {
                        AppendMessagesWithSeparators(newMessages, myUid, peerId, Messaggi.LastOrDefault(x => !x.IsDateSeparator));
                        insertedCount = newMessages.Count;
                        appended = true;
                    }
                }

                if (allowTrim)
                    TrimWindowIfNeeded();

                UpdateBounds();
                CleanupDateSeparators();

                if (appended && _userNearBottom)
                    ScrollToEnd();

                if (!_hasRenderedInitialMessages && Messaggi.Count > 0)
                {
                    _hasRenderedInitialMessages = true;
                    IsLoadingMessages = false;
                }
                else if (IsLoadingMessages && Messaggi.Count > 0)
                {
                    IsLoadingMessages = false;
                }

                swApply.Stop();
                Debug.WriteLine($"[ChatLoad] Apply {source} add={insertedCount} update={updates.Count} in {swApply.ElapsedMilliseconds}ms");
            });
        }

        private void AppendMessagesWithSeparators(
            IEnumerable<FirestoreChatService.MessageItem> messages,
            string myUid,
            string peerId,
            ChatMessageVm? lastExisting)
        {
            var lastDay = lastExisting?.CreatedAt.ToLocalTime().Date;
            foreach (var m in messages)
            {
                var d = m.CreatedAtUtc.ToLocalTime().Date;
                if (lastDay == null || d != lastDay.Value)
                {
                    Messaggi.Add(ChatMessageVm.CreateDateSeparator(d));
                    lastDay = d;
                }

                Messaggi.Add(ChatMessageVm.FromFirestore(m, myUid, peerId));
            }
        }

        private void InsertOlderMessagesWithSeparators(
            IEnumerable<FirestoreChatService.MessageItem> messages,
            string myUid,
            string peerId)
        {
            var firstExisting = Messaggi.FirstOrDefault(x => !x.IsDateSeparator);
            var firstExistingDay = firstExisting?.CreatedAt.ToLocalTime().Date;

            var insertList = new List<ChatMessageVm>();
            DateTime? lastDay = null;

            foreach (var m in messages)
            {
                var day = m.CreatedAtUtc.ToLocalTime().Date;
                if (lastDay == null || day != lastDay.Value)
                {
                    if (!(firstExistingDay != null && insertList.Count == 0 && day == firstExistingDay.Value))
                        insertList.Add(ChatMessageVm.CreateDateSeparator(day));

                    lastDay = day;
                }

                insertList.Add(ChatMessageVm.FromFirestore(m, myUid, peerId));
            }

            for (int i = insertList.Count - 1; i >= 0; i--)
                Messaggi.Insert(0, insertList[i]);
        }

        private void TrimWindowIfNeeded()
        {
            var realCount = Messaggi.Count(x => !x.IsDateSeparator);
            if (realCount <= WindowMaxMessages)
                return;

            var targetRemove = realCount - WindowMaxMessages;
            var removed = 0;

            for (int i = 0; i < Messaggi.Count && removed < targetRemove;)
            {
                if (!Messaggi[i].IsDateSeparator)
                {
                    Messaggi.RemoveAt(i);
                    removed++;
                    continue;
                }

                i++;
            }

            CleanupDateSeparators();
        }

        private void CleanupDateSeparators()
        {
            if (Messaggi.Count == 0)
                return;

            for (int i = Messaggi.Count - 1; i >= 0; i--)
            {
                if (i == 0 && Messaggi[i].IsDateSeparator)
                {
                    Messaggi.RemoveAt(i);
                    continue;
                }

                if (i == Messaggi.Count - 1 && Messaggi[i].IsDateSeparator)
                {
                    Messaggi.RemoveAt(i);
                    continue;
                }

                if (i > 0 && Messaggi[i].IsDateSeparator && Messaggi[i - 1].IsDateSeparator)
                {
                    Messaggi.RemoveAt(i);
                }
            }
        }

        private void UpdateBounds()
        {
            var first = Messaggi.FirstOrDefault(x => !x.IsDateSeparator);
            var last = Messaggi.LastOrDefault(x => !x.IsDateSeparator);
            _oldestMessageUtc = first?.CreatedAt;
            _newestMessageUtc = last?.CreatedAt;
        }

        private ChatScrollAnchor CaptureAnchor()
        {
            if (Messaggi.Count == 0)
                return new ChatScrollAnchor(null, 0, 0);

            var fallbackIndex = Math.Clamp(_lastFirstVisibleIndex, 0, Math.Max(0, Messaggi.Count - 1));
            var messageId = Messaggi.ElementAtOrDefault(fallbackIndex)?.Id;

            return ChatScrollAnchorHelper.Capture(CvMessaggi, fallbackIndex, messageId);
        }

        private void RestoreAnchor(ChatScrollAnchor anchor)
        {
            ChatScrollAnchorHelper.Restore(CvMessaggi, anchor, id =>
            {
                if (string.IsNullOrWhiteSpace(id))
                    return -1;

                for (int i = 0; i < Messaggi.Count; i++)
                {
                    if (string.Equals(Messaggi[i].Id, id, StringComparison.Ordinal))
                        return i;
                }

                return -1;
            });
        }

        private async Task LoadOnceAsync(CancellationToken ct)
        {
            await _loadLock.WaitAsync(ct);
            try
            {
                var peerId = (_peerUserId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(peerId))
                    return;

                var provider = await SessionePersistente.GetProviderAsync();
                if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                    return;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    return;

                var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId, ct);
                _lastIdToken = idToken;
                _lastMyUid = myUid;
                _lastPeerId = peerId;
                _lastChatId = chatId;

                var swFetch = Stopwatch.StartNew();
                var msgs = await _fsChat.GetLastMessagesAsync(idToken!, chatId, limit: BackfillLimit, ct: ct);
                swFetch.Stop();
                Debug.WriteLine($"[ChatLoad] Poll fetch {msgs.Count} in {swFetch.ElapsedMilliseconds}ms");

                var ordered = msgs.OrderBy(m => m.CreatedAtUtc).ToList();
                var sig = ComputeUiSignature(ordered);
                if (sig == _lastUiSignature)
                {
                    if (IsLoadingMessages)
                        MainThread.BeginInvokeOnMainThread(() => IsLoadingMessages = false);
                    return;
                }

                await ApplyMessageDiffAsync(ordered, sig, idToken!, myUid!, peerId, chatId, ct, allowTrim: true, applyReceipts: true, source: "poll");
            }
            finally
            {
                _loadLock.Release();
            }
        }

        private async Task ApplyMessageDiffAsync(
            List<FirestoreChatService.MessageItem> ordered,
            string signature,
            string idToken,
            string myUid,
            string peerId,
            string chatId,
            CancellationToken ct,
            bool allowTrim,
            bool applyReceipts,
            string source)
        {
            if (string.Equals(signature, _lastUiSignature, StringComparison.Ordinal))
            {
                if (IsLoadingMessages)
                    MainThread.BeginInvokeOnMainThread(() => IsLoadingMessages = false);
                return;
            }

            _lastUiSignature = signature;

            if (applyReceipts)
            {
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
            }

            var swApply = Stopwatch.StartNew();
            _scrollCoordinator.EnqueueUiWork(async () =>
            {
                await ApplyMessageDiffOnUiAsync(ordered, myUid, peerId, allowTrim, source);
            }, $"apply-{source}");
            swApply.Stop();
            Debug.WriteLine($"[ChatLoad] Diff queued {ordered.Count} ({source}) in {swApply.ElapsedMilliseconds}ms");

            if (!string.Equals(source, "cache", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(chatId))
            {
                _ = _localCache.WriteAsync(_localCache.GetCacheKey(chatId, peerId), ordered, CacheMaxMessages, ct);
            }
        }

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
        // Prefetch foto (thread-safe: snapshot UI -> download bg)
        // ============================================================
        private void CancelPrefetch()
        {
            try { _prefetchCts?.Cancel(); } catch { }
            try { _prefetchCts?.Dispose(); } catch { }
            _prefetchCts = null;
        }

        private async Task SchedulePrefetchPhotosAsync(int firstIndex, int lastIndex)
        {
            if (firstIndex < 0 || lastIndex < 0 || firstIndex > lastIndex)
                return;

            CancelPrefetch();
            _prefetchCts = new CancellationTokenSource();
            var token = _prefetchCts.Token;

            List<ChatMessageVm> targets = new();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (Messaggi.Count == 0) return;
                var last = Math.Min(lastIndex, Messaggi.Count - 1);
                for (int i = firstIndex; i <= last; i++)
                {
                    var vm = Messaggi[i];
                    if (vm == null || vm.IsDateSeparator) continue;
                    if (!vm.IsPhoto) continue;
                    if (string.IsNullOrWhiteSpace(vm.Id)) continue;

                    lock (_prefetchPhotoMessageIds)
                    {
                        if (_prefetchPhotoMessageIds.Contains(vm.Id))
                            continue;
                        _prefetchPhotoMessageIds.Add(vm.Id);
                    }

                    targets.Add(vm);
                }
            });

            if (targets.Count == 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(token);
                    if (string.IsNullOrWhiteSpace(idToken))
                        return;

                    using var sem = new SemaphoreSlim(2, 2);
                    var tasks = new List<Task>();

                    foreach (var vm in targets)
                    {
                        if (token.IsCancellationRequested) break;

                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync(token);
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(vm.MediaLocalPath) && File.Exists(vm.MediaLocalPath))
                                    return;
                                if (string.IsNullOrWhiteSpace(vm.StoragePath))
                                    return;

                                var ext = Path.GetExtension(vm.FileName ?? "");
                                if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
                                var local = Path.Combine(FileSystem.CacheDirectory, $"dl_{vm.Id}_{Guid.NewGuid():N}{ext}");

                                await FirebaseStorageRestClient.DownloadToFileAsync(idToken!, vm.StoragePath!, local);

                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    vm.MediaLocalPath = local;
                                });
                            }
                            catch { }
                            finally
                            {
                                try { sem.Release(); } catch { }
                            }
                        }, token));
                    }

                    await Task.WhenAll(tasks);
                }
                catch { }
            }, token);
        }

        // ============================================================
        // Send attachment (Storage + Firestore)
        // ============================================================
        private async Task SendAttachmentAsync(string idToken, string myUid, string peerUid, string chatId, AttachmentVm a)
        {
            if (a == null) return;

            var msgId = Guid.NewGuid().ToString("N");

            if (a.Kind == AttachmentKind.Location)
            {
                if (a.Latitude == null || a.Longitude == null)
                    return;

                await _fsChat.SendLocationMessageWithIdAsync(idToken, chatId, msgId, myUid, a.Latitude.Value, a.Longitude.Value);
                return;
            }

            if (a.Kind == AttachmentKind.Contact)
            {
                await _fsChat.SendContactMessageWithIdAsync(idToken, chatId, msgId, myUid, a.ContactName ?? a.DisplayName, a.ContactPhone ?? "");
                return;
            }

            if (string.IsNullOrWhiteSpace(a.LocalPath) || !File.Exists(a.LocalPath))
                throw new InvalidOperationException($"Allegato locale non valido: {a.DisplayName}");

            var fileName = Path.GetFileName(a.LocalPath);
            var contentType = string.IsNullOrWhiteSpace(a.ContentType) ? MediaMetadataHelper.GuessContentType(fileName) : a.ContentType!;
            var sizeBytes = a.SizeBytes > 0 ? a.SizeBytes : new FileInfo(a.LocalPath).Length;

            var type = a.Kind switch
            {
                AttachmentKind.Audio => "audio",
                AttachmentKind.Photo => "photo",
                AttachmentKind.Video => "video",
                _ => "file"
            };

            var durationMs = a.DurationMs;
            if ((type == "audio" || type == "video") && durationMs <= 0)
                durationMs = MediaMetadataHelper.TryGetDurationMs(a.LocalPath);

            var storagePath = $"chats/{chatId}/media/{msgId}/{fileName}";

            await FirebaseStorageRestClient.UploadFileAsync(
                idToken: idToken,
                objectPath: storagePath,
                localFilePath: a.LocalPath,
                contentType: contentType,
                ct: default);

            await _fsChat.SendFileMessageWithIdAsync(
                idToken: idToken,
                chatId: chatId,
                messageId: msgId,
                senderUid: myUid,
                type: type,
                storagePath: storagePath,
                durationMs: durationMs,
                sizeBytes: sizeBytes,
                fileName: fileName,
                contentType: contentType);
        }

        private async Task<string?> EnsureMediaDownloadedAsync(ChatMessageVm m)
        {
            if (m == null) return null;

            try
            {
                if (!string.IsNullOrWhiteSpace(m.MediaLocalPath) && File.Exists(m.MediaLocalPath))
                    return m.MediaLocalPath;

                if (string.IsNullOrWhiteSpace(m.StoragePath))
                    return null;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return null;

                var ext = Path.GetExtension(m.FileName ?? "");
                if (string.IsNullOrWhiteSpace(ext))
                    ext = m.IsPhoto ? ".jpg" : (m.IsAudio ? ".m4a" : (m.IsVideo ? ".mp4" : ".bin"));

                var local = Path.Combine(FileSystem.CacheDirectory, $"dl_{m.Id}_{Guid.NewGuid():N}{ext}");

                await FirebaseStorageRestClient.DownloadToFileAsync(idToken!, m.StoragePath!, local);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    m.MediaLocalPath = local;
                });

                return local;
            }
            catch
            {
                return null;
            }
        }

        // ============================================================
        // VM classes
        // ============================================================
        public enum AttachmentKind { Audio, Photo, Video, File, Location, Contact }

        public sealed class AttachmentVm
        {
            public AttachmentKind Kind { get; set; }
            public string DisplayName { get; set; } = "";
            public string LocalPath { get; set; } = "";

            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }

            public double? Latitude { get; set; }
            public double? Longitude { get; set; }

            public string? ContactName { get; set; }
            public string? ContactPhone { get; set; }

            public string KindLabel => Kind switch
            {
                AttachmentKind.Audio => "Audio",
                AttachmentKind.Photo => "Foto",
                AttachmentKind.Video => "Video",
                AttachmentKind.File => "Documento",
                AttachmentKind.Location => "Posizione",
                AttachmentKind.Contact => "Contatto",
                _ => "Allegato"
            };
        }

        public sealed class ChatMessageVm : BindableObject
        {
            public string Id { get; set; } = "";
            public bool IsMine { get; set; }

            public string Type { get; set; } = "text";

            public bool IsDateSeparator => string.Equals(Type, "date", StringComparison.OrdinalIgnoreCase);
            public string DateSeparatorText { get; set; } = "";

            private string _text = "";
            public string Text
            {
                get => _text;
                set { _text = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasText)); }
            }

            public bool HasText => !string.IsNullOrWhiteSpace(Text);

            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
            public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");

            private string _statusLabel = "";
            public string StatusLabel
            {
                get => _statusLabel;
                set { _statusLabel = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(HasStatus)); }
            }

            public bool HasStatus => !string.IsNullOrWhiteSpace(StatusLabel);

            public string? StoragePath { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }

            private string? _mediaLocalPath;
            public string? MediaLocalPath
            {
                get => _mediaLocalPath;
                set { _mediaLocalPath = value; OnPropertyChanged(); }
            }

            private bool _isAudioPlaying;
            public bool IsAudioPlaying
            {
                get => _isAudioPlaying;
                set { _isAudioPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioPlayStopLabel)); }
            }

            public string AudioPlayStopLabel => IsAudioPlaying ? "Stop" : "Play";
            public string AudioLabel => string.IsNullOrWhiteSpace(FileName) ? "Audio" : FileName!;
            public string DurationLabel => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"mm\:ss") : "";

            public bool IsAudio => string.Equals(Type, "audio", StringComparison.OrdinalIgnoreCase);
            public bool IsPhoto => string.Equals(Type, "photo", StringComparison.OrdinalIgnoreCase);
            public bool IsVideo => string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);
            public bool IsFile => string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);
            public bool IsFileOrVideo => IsFile || IsVideo;

            public string FileLabel => IsVideo ? $"🎬 {FileName}" : $"📎 {FileName}";

            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public bool IsLocation => string.Equals(Type, "location", StringComparison.OrdinalIgnoreCase);

            public string LocationLabel
            {
                get
                {
                    if (Latitude == null || Longitude == null) return "";
                    return $"{Latitude.Value:0.000000}, {Longitude.Value:0.000000}";
                }
            }

            public string? ContactName { get; set; }
            public string? ContactPhone { get; set; }
            public bool IsContact => string.Equals(Type, "contact", StringComparison.OrdinalIgnoreCase);
            public string ContactLabel => $"{ContactName} - {ContactPhone}";

            public static ChatMessageVm CreateDateSeparator(DateTime dayLocalDate)
            {
                var it = new CultureInfo("it-IT");
                var today = DateTime.Now.Date;
                var yesterday = today.AddDays(-1);

                string label;
                if (dayLocalDate == today) label = "Oggi";
                else if (dayLocalDate == yesterday) label = "Ieri";
                else
                {
                    var s = dayLocalDate.ToString("dddd dd MMMM yyyy", it);
                    label = it.TextInfo.ToTitleCase(s);
                }

                return new ChatMessageVm
                {
                    Type = "date",
                    DateSeparatorText = label,
                    CreatedAt = new DateTimeOffset(dayLocalDate)
                };
            }

            public static ChatMessageVm FromFirestore(FirestoreChatService.MessageItem m, string myUid, string peerUid)
            {
                var vm = new ChatMessageVm
                {
                    Id = m.MessageId,
                    IsMine = string.Equals(m.SenderId, myUid, StringComparison.Ordinal),
                    Type = string.IsNullOrWhiteSpace(m.Type) ? "text" : m.Type,
                    Text = m.Text ?? "",
                    CreatedAt = m.CreatedAtUtc,
                    StoragePath = m.StoragePath,
                    FileName = m.FileName,
                    ContentType = m.ContentType,
                    SizeBytes = m.SizeBytes,
                    DurationMs = m.DurationMs,
                    Latitude = m.Latitude,
                    Longitude = m.Longitude,
                    ContactName = m.ContactName,
                    ContactPhone = m.ContactPhone
                };

                if (vm.IsMine)
                {
                    var delivered = (m.DeliveredTo ?? Array.Empty<string>()).Contains(peerUid, StringComparer.Ordinal);
                    var read = (m.ReadBy ?? Array.Empty<string>()).Contains(peerUid, StringComparer.Ordinal);

                    if (read) vm.StatusLabel = "✓✓";
                    else if (delivered) vm.StatusLabel = "✓✓";
                    else vm.StatusLabel = "✓";
                }
                else
                {
                    vm.StatusLabel = "";
                }

                return vm;
            }
        }

        // ============================================================
        // Voice waveform drawable (5s history, colori progressivi)
        // ============================================================
        private sealed class VoiceWaveDrawable : IDrawable
        {
            private readonly int _maxSamples;
            private readonly float[] _samples;
            private int _count;
            private int _head;

            private readonly float _strokePx;

            private readonly Color _cBlue = Color.FromArgb("#1E90FF");
            private readonly Color _cYellow = Color.FromArgb("#FFD60A");
            private readonly Color _cOrange = Color.FromArgb("#FF9F0A");
            private readonly Color _cRed = Color.FromArgb("#FF3B30");

            public VoiceWaveDrawable(int historyMs, int tickMs, float strokePx)
            {
                if (tickMs <= 0) tickMs = 80;
                _maxSamples = Math.Max(8, (int)Math.Ceiling(historyMs / (double)tickMs));
                _samples = new float[_maxSamples];
                _strokePx = Math.Max(1f, strokePx);
            }

            public void Reset()
            {
                _count = 0;
                _head = 0;
                Array.Clear(_samples, 0, _samples.Length);
            }

            public void AddSample(float level01)
            {
                if (level01 < 0) level01 = 0;
                if (level01 > 1) level01 = 1;

                _samples[_head] = level01;
                _head = (_head + 1) % _maxSamples;
                _count = Math.Min(_count + 1, _maxSamples);
            }

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.SaveState();

                canvas.StrokeSize = _strokePx;
                canvas.StrokeLineCap = LineCap.Round;

                var w = dirtyRect.Width;
                var h = dirtyRect.Height;
                if (w <= 1 || h <= 1 || _count <= 1)
                {
                    canvas.RestoreState();
                    return;
                }

                var midY = dirtyRect.Top + h * 0.5f;
                var amp = h * 0.45f;

                int start = (_head - _count);
                if (start < 0) start += _maxSamples;

                float prevX = dirtyRect.Left;
                float prevY = midY;

                for (int i = 0; i < _count; i++)
                {
                    int idx = (start + i) % _maxSamples;
                    float v = _samples[idx];

                    float x = dirtyRect.Left + (w * i / (float)(_count - 1));
                    float y = midY - (v * amp);

                    canvas.StrokeColor = ColorForLevel(v);

                    if (i > 0)
                        canvas.DrawLine(prevX, prevY, x, y);

                    prevX = x;
                    prevY = y;
                }

                canvas.RestoreState();
            }

            private Color ColorForLevel(float v)
            {
                if (v <= 0.15f) return Lerp(_cBlue, _cYellow, v / 0.15f);
                if (v <= 0.45f) return Lerp(_cYellow, _cOrange, (v - 0.15f) / 0.30f);
                if (v <= 0.75f) return Lerp(_cOrange, _cRed, (v - 0.45f) / 0.30f);
                return _cRed;
            }

            private static Color Lerp(Color a, Color b, float t)
            {
                if (t < 0) t = 0;
                if (t > 1) t = 1;

                return new Color(
                    a.Red + (b.Red - a.Red) * t,
                    a.Green + (b.Green - a.Green) * t,
                    a.Blue + (b.Blue - a.Blue) * t,
                    1f);
            }
        }

        // ============================================================
        // Playback (Android reale, altri: stub)
        // ============================================================
        private interface IAudioPlayback
        {
            Task PlayAsync(string filePath);
            void StopPlaybackSafe();
        }

        private static class AudioPlaybackFactory
        {
            public static IAudioPlayback Create()
            {
#if ANDROID
                return new AndroidAudioPlayback();
#else
                return new NoopAudioPlayback();
#endif
            }
        }

        private sealed class NoopAudioPlayback : IAudioPlayback
        {
            public Task PlayAsync(string filePath) => throw new NotSupportedException("Playback audio supportato solo su Android (per ora).");
            public void StopPlaybackSafe() { }
        }

#if ANDROID
        private sealed class AndroidAudioPlayback : IAudioPlayback
        {
            private Android.Media.MediaPlayer? _player;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();

                _player = new Android.Media.MediaPlayer();
                _player.SetDataSource(filePath);
                _player.Prepare();
                _player.Start();

                _player.Completion += (_, __) => StopPlaybackSafe();
                return Task.CompletedTask;
            }

            public void StopPlaybackSafe()
            {
                try
                {
                    if (_player != null)
                    {
                        try { _player.Stop(); } catch { }
                        try { _player.Release(); } catch { }
                        _player = null;
                    }
                }
                catch { }
            }
        }
#endif
    }
}
