using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Utilita;
using Microsoft.Maui.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) COSTRUTTORI E BOOTSTRAP
        // ============================================================
        public Pagina_MessaggiDettaglio()
        {
            InitializeComponent();
            BindingContext = this;

            // 1.1) Inizializzazioni per sottosistemi (definite nei file partial)
            InitPlaybackSubsystem();     // Media / audio playback
            InitVoiceSubsystem();        // Vocale + waveform
            InitUiCollections();         // Emoji, eventi collezioni, binding state
            InitScrollTuning();          // Tuning scroll CollectionView
            OpenPdfCommand = new Command<ChatMessageVm>(async vm => await OnOpenPdfAsync(vm));
            RetrySendCommand = new Command<ChatMessageVm>(async vm => await RetrySendAsync(vm));
            SyncMessageCommand = new Command<ChatMessageVm>(async _ => await SyncChatFromServerAsync());

            // 1.2) Stato iniziale UI
            TitoloChat = "Chat";
            DisplayNomeCompleto = "";

            RefreshVoiceBindings();

            _firstRenderGate = new FirstRenderGate(this, CvMessaggi);
            ComposerBar.SizeChanged += OnLayoutSizeChanged;
            SizeChanged += OnLayoutSizeChanged;
        }

        public Pagina_MessaggiDettaglio(string peerUserId, string peerNickname) : this()
        {
            _peerUserId = (peerUserId ?? "").Trim();
            _peerNickname = (peerNickname ?? "").Trim();

            TitoloChat = string.IsNullOrWhiteSpace(_peerNickname) ? "Chat" : _peerNickname!;
            DisplayNomeCompleto = "";
        }

        // ============================================================
        // 2) CICLO DI VITA PAGINA
        // ============================================================
        protected override void OnAppearing()
        {
            base.OnAppearing();

            if (Messaggi.Count == 0)
                IsLoadingMessages = true;

            _ = OnAppearingAsync();
            RefreshVoiceBindings();
        }

        private async Task OnAppearingAsync()
        {
            CancelPendingApply();
            _appearanceCts?.Cancel();
            _appearanceCts = new CancellationTokenSource();
            var ct = _appearanceCts.Token;

            await LoadFromCacheAndRenderImmediatelyAsync();
            await LoadPeerProfileFromCacheAsync();
            ScrollBottomImmediately(force: true);

            await _firstRenderGate!.WaitAsync();
            if (ct.IsCancellationRequested)
                return;

            _lastMyUid = FirebaseSessionePersistente.GetLocalId();
            _lastPeerId = (_peerUserId ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(_lastPeerId))
            {
                _chatCacheKey ??= _chatCache.GetCacheKey(_chatIdCached, _lastPeerId);
                await _chatStore.ResetUnreadAsync(_chatCacheKey, CancellationToken.None);
            }

            StartRealtimeUpdatesAfterFirstRender();
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // 2.1) Caso “modal aperto” (foto fullscreen / bottom sheet allegati, ecc.)
            if (_suppressStopPollingOnce)
            {
                _suppressStopPollingOnce = false;
                return;
            }

            // 2.2) Stop sottosistemi
            StopRealtimeUpdates();
            StopVoiceUiLoop();
            StopPlaybackSafe();
            CancelPrefetch();
            CancelPendingApply();
            CancelReceipts();
            _appearanceCts?.Cancel();

            // 2.3) Sicurezza: se esco mentre registro, cancello in background
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

        // ============================================================
        // 3) LAYOUT RESPONSIVE (DIMENSIONI BOLLE/MEDIA)
        // ============================================================
        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            // 3.1) Larghezza massima bolla e preview media
            if (width > 0)
            {
                var w = width * 0.72;
                BubbleMaxWidth = Math.Clamp(w, 220, 520);

                MediaPreviewWidth = Math.Clamp(BubbleMaxWidth, 220, 380);
                MediaPreviewHeight = Math.Clamp(MediaPreviewWidth * 0.72, 160, 320);
            }

            // 3.2) Altezza pannello voice-lock
            if (height > 0)
            {
                var h = height * 0.20;
                VoiceLockPanelHeight = Math.Max(160, h);
            }
        }

        // ============================================================
        // 4) HEADER
        // ============================================================
        private async void OnBackClicked(object sender, EventArgs e)
        {
            try { await Navigation.PopAsync(); } catch { }
        }

        // ============================================================
        // 5) PROFILO PEER (NOME COMPLETO SOTTO TITOLO CHAT)
        // ============================================================
        private async Task LoadPeerProfileFromCacheAsync()
        {
            if (_peerProfileLoaded)
                return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
                return;

            var cached = await _userPublicCache.TryGetAsync(peerId, CancellationToken.None);
            if (cached == null)
                return;

            DisplayNomeCompleto = BuildDisplayNomeCompleto(cached.FirstName, cached.LastName);
            _peerProfileLoaded = true;
        }

        // Nessun fetch automatico profilo: si usa solo la cache locale.

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

        // ============================================================
        // 6) HOOK DI INIZIALIZZAZIONE (IMPLEMENTATI NEI FILE PARZIALI)
        // ============================================================
        private partial void InitPlaybackSubsystem();
        private partial void InitVoiceSubsystem();
        private partial void InitUiCollections();
        private partial void InitScrollTuning();

        // ============================================================
        // 7) HOOK DI STOP (IMPLEMENTATI NEI FILE PARZIALI)
        // ============================================================
        private partial void StopPlaybackSafe();
    }
}
