// File: Biliardo.App/Pagine_Home/Pagina_Home.xaml.cs
// ========================= 1) NOME FILE E SCOPO =========================
// Pagine_Home/Pagina_Home.xaml.cs
// Code-behind della Home. Implementa:
//  - Barra icone (menu laterale, mercatino, sfida, chat, menu account).
//  - Menu laterale sinistro free1..free15 con pannello a scorrimento.
//  - Menu laterale destro account (Info app / Esci →) con pannello a scorrimento.
//  - Popup verde stile unificato (informazioni e messaggi Home).
//  - Navigazione verso Pagina_MessaggiLista.
//  - Feed Home (FirestoreHomeFeedService) con supporto allegati e audio playback.
// =======================================================================
//
// REQUISITO NUOVO (implementato qui):
//  - “rete consentita solo a scroll fermo e con priorità bassa” (NON “rete vietata”)
//  - I post vanno caricati in momenti strategici:
//      1) all'apertura dell'app (dopo primo render)
//      2) quando l'utente raggiunge quasi la fine dei post presenti in home
//  - Il caricamento deve essere in task background e NON deve bloccare lo scroll
//    (nessun await nel handler di scroll; rete eseguita solo quando lo scroll è idle).
// =======================================================================

using Biliardo.App.Componenti_UI.Composer;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Infrastructure.Media.Home;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Infrastructure.Home;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Pagine_Debug;
using Biliardo.App.RiquadroDebugTrasferimentiFirebase;
using Biliardo.App.Pagine_Media;
using Biliardo.App.Servizi_Diagnostics;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Realtime;
using Biliardo.App.Utilita;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Media;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel.Communication;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using MauiMediaSource = CommunityToolkit.Maui.Views.MediaSource;


namespace Biliardo.App.Pagine_Home
{
    public partial class Pagina_Home : ContentPage
    {
        // ===================== 2) STATO INTERNO ==========================
        private bool _menuAperto = false;
        private bool _logoutMenuAperto = false;

        private readonly FirestoreHomeFeedService _homeFeed = new();
        private readonly FirestoreRealtimeService _realtime = new();
        private readonly IAudioPlayback _audioPlayback;
        private readonly MediaCacheService _mediaCache = new();
        private readonly IMediaPreviewGenerator _previewGenerator = new MediaPreviewGenerator();
        private readonly HomeMediaPipeline _homeMediaPipeline;
        public Command<HomeAttachmentVm> OpenPdfCommand { get; }
        public Command<HomePostVm> RetryHomePostCommand { get; }

        public ObservableCollection<HomePostVm> Posts { get; } = new();
        private bool _isHomeLoading;
        private bool _isHomeRefreshing;
        private HashSet<string> _likedPostIds = new(StringComparer.Ordinal);
        private string _currentUid = "";
        private FirestoreDirectoryService.UserPublicItem? _myProfile;
        private readonly HashSet<string> _prefetchMediaKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingPreviewEnsures = new(StringComparer.Ordinal);
        private readonly ListenerRegistry _listeners = new();
        private IDisposable? _homeListener;
        private IDisposable? _profileListener;
        public Command RefreshHomeCommand { get; }
        private DateTimeOffset? _homePagingCursorUtc;
        private bool _initialPageLoaded;

        // Popup personalizzato
        private TaskCompletionSource<bool>? _popupTcs;

        // Tuning scroll (Android: disabilita change animations, cache, fixed size)
        private readonly ScrollWorkCoordinator _feedCoordinator = new();
        private CollectionViewNativeScrollStateTracker? _feedTracker;
        private bool _realtimeSubscribed;
        private bool _isLoadingMore;
        private bool _noMoreHomePosts;
        private FirstRenderGate? _firstRenderGate;
        private CancellationTokenSource? _appearanceCts;
        private bool _loadedFromMemory;

        // ===================== PRIORITÀ SCROLL (HARD) =====================
        // Filosofia: durante lo scroll non si fanno operazioni costose (rete, merge, prefetch, refresh cache).
        private readonly object _olderBufferLock = new();
        private readonly List<HomePostVm> _olderBuffer = new();
        private volatile bool _isUserScrolling;
        private int _pendingPrefetchFirst = -1;
        private int _pendingPrefetchLast = -1;
        private int _lastFirstVisibleIndex = -1;
        private int _lastScrollDirection = 0;
        private int _scrollEventStamp;
        private int _scrollIdleWorkerRunning;
        private volatile bool _pendingApplyOlderBuffer;

        private CancellationTokenSource? _prefetchCts;
        private CancellationTokenSource? _previewEnsureCts;

        private const int ScrollIdleDelayMs = 350;

        // ===================== POLICY RETE (NUOVA) =========================
        // Rete consentita SOLO quando lo scroll è idle, e in task di background.
        // Due trigger strategici:
        //  1) apertura app (dopo primo render)
        //  2) quasi fine lista (threshold)
        private const int LoadMoreThresholdItems = 4; // “quasi fine”
        private volatile bool _pendingInitialNetworkRefresh;
        private volatile bool _pendingLoadMoreRequest;
        private volatile int _lastKnownVisibleIndex = -1;

        private readonly SemaphoreSlim _networkWorkSemaphore = new(1, 1);

        // ===================== CACHE REFRESH (DEBOUNCE) ====================
        // Evita refresh completo della cache RAM mentre si scrolla (jank).
        private readonly object _memRefreshLock = new();
        private CancellationTokenSource? _memRefreshCts;
        private bool _memRefreshDeferredBecauseScrolling;

        public bool IsHomeLoading
        {
            get => _isHomeLoading;
            private set
            {
                if (_isHomeLoading == value) return;
                _isHomeLoading = value;
                OnPropertyChanged();
            }
        }



        // ===================== 3) COSTRUTTORE ============================
        public Pagina_Home()
        {
            InitializeComponent();
            BindingContext = this;

            // Nasconde la Navigation Bar su questa pagina
            NavigationPage.SetHasNavigationBar(this, false);

            _audioPlayback = AudioPlaybackFactory.Create();
            _homeMediaPipeline = new HomeMediaPipeline(_previewGenerator);
            OpenPdfCommand = new Command<HomeAttachmentVm>(async att => await OnOpenPdfFromHome(att));
            RetryHomePostCommand = new Command<HomePostVm>(async post => await RetryHomePostAsync(post));
            RefreshHomeCommand = new Command(async () => await ExecutePullToRefreshAsync());

            ApplyHomeFeedScrollTuning();
            _firstRenderGate = new FirstRenderGate(this, FeedCollection);

            ResetPrefetchTokens();
        }
        // =================================================================







        /// <summary>
        /// Verifica sessione Firebase locale. Se manca, torna a Login.
        /// </summary>







        // ===================== SCROLL EVENT (NO WORK) =====================

        // Debounce efficiente: un solo worker riusato (nessuna nuova CTS per ogni tick di scroll).









        public bool IsRefreshingHome
        {
            get => _isHomeRefreshing;
            private set
            {
                if (_isHomeRefreshing == value) return;
                _isHomeRefreshing = value;
                OnPropertyChanged();
            }
        }












        private void ApplyServerPostToPending(HomePostVm pending, FirestoreHomeFeedService.HomePostItem post)
        {
            pending.PostId = post.PostId;
            pending.IsPendingUpload = false;
            pending.HasSendError = false;
            pending.RequiresSync = false;
            pending.HasFullData = true;
            pending.CreatedAtUtc = post.CreatedAtUtc;
            pending.Text = post.Text ?? "";
            pending.AuthorUid = post.AuthorUid;
            pending.AuthorNickname = post.AuthorNickname;
            pending.AuthorFirstName = post.AuthorFirstName;
            pending.AuthorLastName = post.AuthorLastName;
            pending.AuthorAvatarPath = post.AuthorAvatarPath;
            pending.AuthorAvatarUrl = post.AuthorAvatarUrl;
            pending.LikeCount = post.LikeCount;
            pending.CommentCount = post.CommentCount;
            pending.ShareCount = post.ShareCount;
            pending.SchemaVersion = post.SchemaVersion;
            pending.Ready = post.Ready;
            pending.Deleted = post.Deleted;
            pending.DeletedAtUtc = post.DeletedAtUtc;
            pending.RepostOfPostId = post.RepostOfPostId;
            pending.Attachments.Clear();
            foreach (var att in post.Attachments)
                pending.AttachAttachment(HomeAttachmentVm.FromService(att));
        }




















        // ✅ FIX #1: gestione tap robusta (usa e.Parameter, non sender) -> like torna a funzionare
        private async void OnLikeClicked(object sender, TappedEventArgs e)
        {
            if (e?.Parameter is not HomePostVm post)
                return;

            var wasLiked = _likedPostIds.Contains(post.PostId);
            var before = post.LikeCount;

            try
            {
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                post.IsLiked = !wasLiked;
                post.LikeCount = Math.Max(0, before + (wasLiked ? -1 : 1));
                post.NotifyCounters();

                if (post.IsLiked)
                    _likedPostIds.Add(post.PostId);
                else
                    _likedPostIds.Remove(post.PostId);

                // Like è azione utente: consentita anche se la lista sta “rallentando”.
                var res = await _homeFeed.ToggleLikeOptimisticAsync(post.PostId, wasLiked, before);

                post.IsLiked = res.IsLikedNow;
                post.LikeCount = Math.Max(0, res.LikeCount);
                post.NotifyCounters();

                if (res.IsLikedNow)
                    _likedPostIds.Add(post.PostId);
                else
                    _likedPostIds.Remove(post.PostId);

                ScheduleMemoryCacheRefresh();
            }
            catch (Exception ex)
            {
                post.IsLiked = wasLiked;
                post.LikeCount = Math.Max(0, before);
                post.NotifyCounters();
                if (wasLiked)
                    _likedPostIds.Add(post.PostId);
                else
                    _likedPostIds.Remove(post.PostId);

                ScheduleMemoryCacheRefresh();
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore like");
            }
        }

        // ✅ FIX #3: comment/share senza Button (tap su layout)
        private async void OnCommentClicked(object sender, TappedEventArgs e)
        {
            if (e?.Parameter is not HomePostVm post)
                return;

            await Navigation.PushAsync(new PostDetailPage(post));
        }

        private async void OnShareClicked(object sender, TappedEventArgs e)
        {
            if (e?.Parameter is not HomePostVm post)
                return;

            var choice = await DisplayActionSheet("Condividi", "Annulla", null, "Condividi esterno", "Repost interno");
            if (choice == "Condividi esterno")
            {
                var hasText = !string.IsNullOrWhiteSpace(post.Text);
                string? fallbackUrl = null;
                if (!hasText)
                {
                    var att = post.Attachments?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.DownloadUrl));
                    fallbackUrl = att?.DownloadUrl;
                }

                var shared = await Biliardo.App.Helpers.ShareHelper.ShareIfNotEmptyAsync(
                    hasText ? post.Text : null,
                    fallbackUrl,
                    "Condividi");

                if (!shared)
                {
                    await ShowPopupAsync("Nessun contenuto disponibile da condividere o condivisione non riuscita.", "Info");
                }
            }
            else if (choice == "Repost interno")
            {
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                await _homeFeed.CreateRepostAsync(post.PostId, null);
                post.ShareCount += 1;
                post.NotifyCounters();
                ScheduleMemoryCacheRefresh();
            }
        }

















        // ===================== 4) MENU LATERALE SINISTRO =================




        // =================================================================

        // ===================== 5) MENU LATERALE DESTRO (ACCOUNT) =========






        // =================================================================

        // ===================== 6) POPUP PERSONALIZZATO HOME ===============



        // =================================================================

        // ===================== 7) MESSAGGI (CHAT) =========================
        // =================================================================

        // ===================== 8) AZIONI (ICONA SFIDA, MERCATINO) =========

        // =================================================================

        // ===================== 9) LOGOUT (LOGICA) ==========================
        // =================================================================

        // ===================== 10) ACCESSO OSPITE (STUB) ==================
        // =================================================================
    }
}
