using Biliardo.App.Componenti_UI;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

// ============================================================
// Alias coerenti (evita ambiguità e rende il codice uniforme)
// ============================================================
using Color = Microsoft.Maui.Graphics.Color;

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
        // 3) PERFORMANCE / SCROLL (anti-jank)
        // ============================================================
        private long _scrollBusyUntilUtcTicks;
        private static readonly TimeSpan ScrollIdleDelay = TimeSpan.FromMilliseconds(AppMediaOptions.ScrollIdleDelayMs);

        private bool IsScrollBusy()
            => DateTime.UtcNow.Ticks < Interlocked.Read(ref _scrollBusyUntilUtcTicks);

        private void MarkScrollBusy()
            => Interlocked.Exchange(ref _scrollBusyUntilUtcTicks, DateTime.UtcNow.AddMilliseconds(AppMediaOptions.ScrollBusyHoldMs).Ticks);

        // ============================================================
        // 4) DIMENSIONI (BOLLE / MEDIA PREVIEW) - BINDING XAML
        // ============================================================
        private double _bubbleMaxWidth = 320;
        public double BubbleMaxWidth
        {
            get => _bubbleMaxWidth;
            set { _bubbleMaxWidth = value; OnPropertyChanged(); }
        }

        private double _mediaPreviewWidth = 280;
        public double MediaPreviewWidth
        {
            get => _mediaPreviewWidth;
            set { _mediaPreviewWidth = value; OnPropertyChanged(); }
        }

        private double _mediaPreviewHeight = 200;
        public double MediaPreviewHeight
        {
            get => _mediaPreviewHeight;
            set { _mediaPreviewHeight = value; OnPropertyChanged(); }
        }

        private double _voiceLockPanelHeight = 180;
        public double VoiceLockPanelHeight
        {
            get => _voiceLockPanelHeight;
            set { _voiceLockPanelHeight = value; OnPropertyChanged(); }
        }

        // ============================================================
        // 5) VM/UI STATE - COLLECTIONS (BINDING XAML)
        // ============================================================
        public ObservableCollection<ChatMessageVm> Messaggi { get; } = new();
        public ObservableCollection<AttachmentVm> AllegatiSelezionati { get; } = new();
        public ObservableCollection<string> EmojiItems { get; } = new();
        public Command<ChatMessageVm> OpenPdfCommand { get; set; } = null!;
        public Command<ChatMessageVm> RetrySendCommand { get; set; } = null!;

        // ============================================================
        // 6) HEADER / TITOLI (BINDING XAML)
        // ============================================================
        private string _titoloChat = "Chat";
        public string TitoloChat
        {
            get => _titoloChat;
            set { _titoloChat = value; OnPropertyChanged(); }
        }

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

        // ============================================================
        // 7) COMPOSER: TESTO / ALLEGATI / INVIO (BINDING XAML o VIEW)
        // ============================================================
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

        public bool CanSendTextOrAttachments
            => !_isSending && (!string.IsNullOrWhiteSpace(TestoMessaggio) || HasSelectedAttachments);

        // Mic visibile quando non c’è testo/allegati
        public bool CanShowMic
            => !_isSending
               && string.IsNullOrWhiteSpace(TestoMessaggio)
               && !HasSelectedAttachments;

        // ============================================================
        // 8) LOADING OVERLAY
        // ============================================================
        private bool _isLoadingMessages;
        public bool IsLoadingMessages
        {
            get => _isLoadingMessages;
            set { _isLoadingMessages = value; OnPropertyChanged(); }
        }

        // ============================================================
        // 9) EMOJI PANEL (se presente nel layout/Composer)
        // ============================================================
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

        // ============================================================
        // 10) COLORI PER BINDING XAML
        // ============================================================
        public Color PageBgColor => Color.FromArgb(HEX_PAGE_BG);
        public Color TopBarBgColor => Color.FromArgb(HEX_TOPBAR_BG);

        public Color ComposerBarBgColor => Color.FromArgb(HEX_COMPOSER_BAR_BG);
        public Color AccentGreenColor => Color.FromArgb(HEX_ACCENT_GREEN);

        public Color BubbleMineColor => Color.FromArgb(HEX_BUBBLE_MINE);
        public Color BubblePeerColor => Color.FromArgb(HEX_BUBBLE_PEER);
        public Color BubbleTextColor => Color.FromArgb(HEX_BUBBLE_TEXT);

        // ============================================================
        // 11) CHAT CONTEXT (peer / token / chatId)
        // ============================================================
        private readonly string? _peerUserId;
        private readonly string? _peerNickname;

        private bool _peerProfileLoaded;

        private string? _lastIdToken;
        private string? _lastMyUid;
        private string? _lastPeerId;
        private string? _lastChatId;

        private string? _chatIdCached;
        private string? _chatCacheKey;
        private bool _loadedFromCache;

        private bool _userNearBottom = true;
        private bool _isLoadingOlder;

        // Modali: evita stop polling quando apro un modal (foto fullscreen, bottom sheet, ecc.)
        private bool _suppressStopPollingOnce;

        // ============================================================
        // 12) SERVIZI FIREBASE (chat)
        // ============================================================
        private readonly FirestoreChatService _fsChat = new("biliardoapp");
        private readonly MediaCacheService _mediaCache = new();

        // FIX CS0246: ChatLocalCache non esisteva nel progetto -> definito qui (stub minimo compilabile).
        // Se/Quando creerai una vera cache persistente, rimuovi questa classe annidata e usa l’implementazione reale.
        private readonly ChatLocalCache _chatCache = new();

        private readonly IMediaPreviewGenerator _previewGenerator = new MediaPreviewGenerator();

        private readonly List<int> _recordingWaveform = new();
        private long _lastWaveformSampleTicks;

        // ============================================================
        // 13) POLLING / DIFF / PENDING APPLY (anti-jank)
        // ============================================================
        private CancellationTokenSource? _pollCts;
        private readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(1200);
        private string _lastUiSignature = "";

        private List<FirestoreChatService.MessageItem>? _pendingOrdered;
        private string? _pendingSignature;
        private CancellationTokenSource? _pendingApplyCts;
        private readonly object _pendingLock = new();

        // ============================================================
        // 14) PREFETCH MEDIA (foto+video visibili + anticipo)
        // ============================================================
        private readonly HashSet<string> _prefetchMediaMessageIds = new(StringComparer.Ordinal);
        private CancellationTokenSource? _prefetchCts;

        // ============================================================
        // 15) VOICE (WhatsApp-like) - campi condivisi
        // ============================================================
        private readonly IVoiceRecorder _voice = VoiceMediaFactory.CreateRecorder();
        private readonly SemaphoreSlim _voiceOpLock = new(1, 1);

        private bool _micIsPressed;
        private bool _voiceLocked;
        private bool _voiceCanceledBySwipe;

        private string _voiceTimeLabel = "00:00";
        public string VoiceTimeLabel
        {
            get => _voiceTimeLabel;
            set { _voiceTimeLabel = value; OnPropertyChanged(); }
        }

        public bool IsVoiceHoldStripVisible => _voice.IsRecording && !_voiceLocked && !_voiceCanceledBySwipe;
        public bool IsVoiceLockPanelVisible => _voice.IsRecording && _voiceLocked && !_voiceCanceledBySwipe;

        public string VoicePauseResumeLabel => _voice.IsPaused ? "REC" : "||";

        private string? _voiceFilePath;
        private long _voiceDurationMs;

        private CancellationTokenSource? _voiceUiCts;

        // drawable waveform (istanziato in InitVoiceSubsystem)
        private VoiceWaveDrawable _voiceWave = null!;

        // ============================================================
        // 16) PLAYBACK AUDIO MESSAGGI (istanziato in InitPlaybackSubsystem)
        // ============================================================
        private IAudioPlayback _playback = null!;
        private readonly Dictionary<ChatMessageVm, GraphicsView> _audioWaveViews = new();
        private IDispatcherTimer? _audioWaveTimer;
        private ChatMessageVm? _audioWaveCurrent;
        private double _audioWavePhase;
        private int _audioWaveIndex;

        // ============================================================
        // 17) INIT: COLLEZIONI + EVENTI + EMOJI
        // ============================================================
        private partial void InitUiCollections()
        {
            // 17.1) Cambio allegati selezionati: aggiorna proprietà dipendenti
            AllegatiSelezionati.CollectionChanged += (_, __) =>
            {
                // Se aggiungo allegati, chiudo eventuale pannello emoji (se presente)
                if (AllegatiSelezionati.Count > 0 && IsEmojiPanelVisible)
                    IsEmojiPanelVisible = false;

                OnPropertyChanged(nameof(HasSelectedAttachments));
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));
            };

            // 17.2) Emoji base (come tuo file originale)
            if (EmojiItems.Count == 0)
            {
                foreach (var e in new[]
                         { "😀","😁","😂","🤣","😊","😍","😘","😎",
                           "😅","😇","😉","🙂","🤔","😴","😭","😡",
                           "👍","👎","🙏","👏","🎉","🔥","❤️","💪",
                           "⚽","🎱","📎","📷","🎤","🎬","📍","👤" })
                    EmojiItems.Add(e);
            }
        }

        // ============================================================
        // 18) INIT: TUNING SCROLL (come nel file originale)
        // ============================================================
        private partial void InitScrollTuning()
        {
            // Nota: CvMessaggi è definito in XAML, quindi disponibile qui.
            ChatScrollTuning.Apply(CvMessaggi);
        }

        // ============================================================
        // 19) CHAT LOCAL CACHE (STUB MINIMO PER RISOLVERE CS0246)
        // ============================================================
        private sealed class ChatLocalCache
        {
            private sealed class Entry
            {
                public List<FirestoreChatService.MessageItem> Items { get; }
                public DateTimeOffset SavedAtUtc { get; }

                public Entry(List<FirestoreChatService.MessageItem> items)
                {
                    Items = items;
                    SavedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            private readonly Dictionary<string, Entry> _mem = new(StringComparer.Ordinal);
            private readonly object _lock = new();

            public Task<List<FirestoreChatService.MessageItem>?> TryLoadAsync(string cacheKey, CancellationToken ct = default)
            {
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return Task.FromResult<List<FirestoreChatService.MessageItem>?>(null);

                lock (_lock)
                {
                    if (_mem.TryGetValue(cacheKey, out var e))
                        return Task.FromResult<List<FirestoreChatService.MessageItem>?>(new List<FirestoreChatService.MessageItem>(e.Items));
                }

                return Task.FromResult<List<FirestoreChatService.MessageItem>?>(null);
            }

            public Task SaveAsync(string cacheKey, IEnumerable<FirestoreChatService.MessageItem> items, CancellationToken ct = default)
            {
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return Task.CompletedTask;

                var list = items is List<FirestoreChatService.MessageItem> l ? new List<FirestoreChatService.MessageItem>(l)
                    : new List<FirestoreChatService.MessageItem>(items ?? Array.Empty<FirestoreChatService.MessageItem>());

                lock (_lock)
                {
                    _mem[cacheKey] = new Entry(list);
                }

                return Task.CompletedTask;
            }

            public Task ClearAsync(CancellationToken ct = default)
            {
                lock (_lock) { _mem.Clear(); }
                return Task.CompletedTask;
            }

            public Task RemoveAsync(string cacheKey, CancellationToken ct = default)
            {
                if (string.IsNullOrWhiteSpace(cacheKey))
                    return Task.CompletedTask;

                lock (_lock) { _mem.Remove(cacheKey); }
                return Task.CompletedTask;
            }
        }
    }
}
