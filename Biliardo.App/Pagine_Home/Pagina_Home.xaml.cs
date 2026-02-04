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
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Realtime;
using Biliardo.App.Utilita;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
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
using MauiMediaSource = CommunityToolkit.Maui.Views.MediaSource;



#if WINDOWS
using WindowsMediaSource = Windows.Media.Core.MediaSource;
using Windows.Media.Playback;
#endif

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
        private HashSet<string> _likedPostIds = new(StringComparer.Ordinal);
        private string _currentUid = "";
        private FirestoreDirectoryService.UserPublicItem? _myProfile;
        private readonly HashSet<string> _prefetchMediaKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingPreviewEnsures = new(StringComparer.Ordinal);
        private readonly ListenerRegistry _listeners = new();
        private IDisposable? _homeListener;
        private IDisposable? _profileListener;

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

            ApplyHomeFeedScrollTuning();
            _firstRenderGate = new FirstRenderGate(this, FeedCollection);
        }
        // =================================================================

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _ = OnAppearingAsync();
        }

        private async Task OnAppearingAsync()
        {
            _appearanceCts?.Cancel();
            _appearanceCts = new CancellationTokenSource();
            var ct = _appearanceCts.Token;

            ApplyHomeFeedScrollTuning();
            await LoadFromCacheAndRenderImmediatelyAsync();

            await _firstRenderGate!.WaitAsync();
            if (ct.IsCancellationRequested)
                return;

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            StartRealtimeUpdatesAfterFirstRender(ct);
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            try
            {
                _feedTracker?.Dispose();
                _feedTracker = null;
            }
            catch { }

            StopRealtimeUpdates();
            _listeners.Clear();
            _appearanceCts?.Cancel();
        }

        private void ApplyHomeFeedScrollTuning()
        {
            try
            {
                // Inerzia/fling tuning (Android/Windows). Su altre piattaforme è no-op.
                Biliardo.App.Componenti_UI.ChatScrollTuning.Apply(FeedCollection);
            }
            catch { }

            try
            {
                _feedTracker?.Dispose();
                _feedTracker = CollectionViewNativeScrollStateTracker.Attach(
                    FeedCollection,
                    _feedCoordinator,
                    TimeSpan.FromMilliseconds(280));
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Verifica sessione Firebase locale. Se manca, torna a Login.
        /// </summary>
        private async Task<bool> EnsureFirebaseSessionOrBackToLoginAsync()
        {
            try
            {
                var has = await FirebaseSessionePersistente.HaSessioneAsync();
                var uid = FirebaseSessionePersistente.GetLocalId();

                if (!has || string.IsNullOrWhiteSpace(uid))
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
                    });
                    return false;
                }

                return true;
            }
            catch
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Application.Current.MainPage = new NavigationPage(new Pagina_Login(showInserisciCredenziali: true));
                });
                return false;
            }
        }

        private void StartProfileListener(CancellationToken ct)
        {
            try
            {
                var uid = FirebaseSessionePersistente.GetLocalId() ?? "";
                if (string.IsNullOrWhiteSpace(uid))
                    return;

                _profileListener?.Dispose();
                _profileListener = _realtime.SubscribeUserPublic(
                    uid,
                    profile =>
                    {
                        if (ct.IsCancellationRequested)
                            return;

                        _myProfile = profile;
                    },
                    ex => Debug.WriteLine($"[Home] profile listener error: {ex}"));
                _listeners.Add(_profileListener);
            }
            catch
            {
                // best-effort
            }
        }

        private static Exception UnwrapException(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
                return UnwrapException(tie.InnerException);

            if (ex is AggregateException ae)
            {
                var flat = ae.Flatten();
                if (flat.InnerExceptions.Count == 1)
                    return UnwrapException(flat.InnerExceptions[0]);
                if (flat.InnerExceptions.Count > 1)
                    return flat;
            }

            return ex;
        }

        private static string FormatExceptionForPopup(Exception ex)
        {
            var core = UnwrapException(ex);
            var msg = core.Message;
#if DEBUG
            msg += "\n\n" + core.ToString();
#endif
            return msg;
        }

        private async Task LoadFromCacheAndRenderImmediatelyAsync()
        {
            try
            {
                _currentUid = FirebaseSessionePersistente.GetLocalId() ?? "";
                _likedPostIds = new HashSet<string>(StringComparer.Ordinal);

                IsHomeLoading = true;

                if (!_loadedFromMemory && HomeFeedMemoryCache.Instance.TryGet(out var memory))
                {
                    _loadedFromMemory = true;
                    var visible = new List<HomePostVm>();
                    var pending = new List<HomePostVm>();
                    foreach (var cachedPost in memory.OrderByDescending(x => x.CreatedAtUtc))
                    {
                        var vm = HomePostVm.FromService(cachedPost);
                        vm.IsLiked = _likedPostIds.Contains(vm.PostId);
                        vm.RetryCommand = RetryHomePostCommand;
                        vm.SyncCommand = null;

                        var contract = BuildContractFromVm(vm);
                        if (HomePostValidatorV2.IsHomeVisible(contract, out _))
                            visible.Add(vm);
                        else if (HomePostValidatorV2.IsServerReady(contract, out _))
                            pending.Add(vm);
                    }

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        Posts.Clear();
                        foreach (var vm in visible.OrderByDescending(x => x.CreatedAtUtc))
                            Posts.Add(vm);
                    });

                    foreach (var pendingPost in pending)
                        QueueEnsurePreviewAvailable(pendingPost);
                }

                _noMoreHomePosts = true;
                _ = PrefetchHomeMediaAsync(0, Math.Min(Posts.Count - 1, 5), _appearanceCts?.Token ?? CancellationToken.None);
            }
            catch
            {
                // cache best-effort
            }
            finally
            {
                IsHomeLoading = false;
            }
        }

        public async Task ScrollToPostIdAsync(string postId)
        {
            if (string.IsNullOrWhiteSpace(postId))
                return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var target = Posts.FirstOrDefault(x => x.PostId == postId);
                if (target == null)
                    return;

                FeedCollection.ScrollTo(target, position: ScrollToPosition.Start, animate: false);
            });
        }

        private async void OnHomeFeedScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            if (Posts.Count == 0)
                return;

            _ = PrefetchHomeMediaAsync(e.FirstVisibleItemIndex, e.LastVisibleItemIndex, _appearanceCts?.Token ?? CancellationToken.None);
        }

        private Task PrefetchHomeMediaAsync(int firstIndex, int lastIndex, CancellationToken ct)
        {
            if (firstIndex < 0 || lastIndex < 0 || Posts.Count == 0 || lastIndex < firstIndex)
                return Task.CompletedTask;

            return Task.Run(async () =>
            {
                try
                {
                    var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
                    if (string.IsNullOrWhiteSpace(idToken))
                        return;

                    var targets = new List<HomeAttachmentVm>();
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        var start = Math.Max(0, firstIndex);
                        var end = Math.Min(lastIndex, Posts.Count - 1);
                        for (var i = start; i <= end; i++)
                        {
                            var post = Posts[i];
                            foreach (var att in post.Attachments)
                            {
                                if (!att.IsImage && !att.IsVideo && !att.IsPdf)
                                    continue;
                                targets.Add(att);
                            }
                        }
                    });

                    if (targets.Count == 0)
                        return;

                    using var sem = new SemaphoreSlim(AppMediaOptions.DownloadConcurrency, AppMediaOptions.DownloadConcurrency);
                    var tasks = new List<Task>();

                    foreach (var att in targets)
                    {
                        if (ct.IsCancellationRequested)
                            break;

                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync(ct);
                            try
                            {
                                if (ct.IsCancellationRequested)
                                    return;

                                var previewRemotePath = att.GetPreviewRemotePath();
                                if (string.IsNullOrWhiteSpace(previewRemotePath))
                                    return;

                                if (!string.IsNullOrWhiteSpace(att.ThumbLocalPath) && File.Exists(att.ThumbLocalPath))
                                    return;

                                lock (_prefetchMediaKeys)
                                {
                                    if (!_prefetchMediaKeys.Add(previewRemotePath))
                                        return;
                                }

                                MainThread.BeginInvokeOnMainThread(() => att.IsPreviewDownloading = true);
                                var thumbLocal = await _mediaCache.GetOrDownloadAsync(idToken!, previewRemotePath!, att.FileName ?? "thumb.jpg", isThumb: true, ct);
                                if (string.IsNullOrWhiteSpace(thumbLocal))
                                    return;

                                MainThread.BeginInvokeOnMainThread(() => att.ThumbLocalPath = thumbLocal);
                                _ = UpdateCacheForAttachmentAsync(att);
                            }
                            catch { }
                            finally
                            {
                                MainThread.BeginInvokeOnMainThread(() => att.IsPreviewDownloading = false);
                                try { sem.Release(); } catch { }
                            }
                        }, ct));
                    }

                    await Task.WhenAll(tasks);
                }
                catch { }
            }, ct);
        }

        private async Task LoadMoreHomePostsAsync()
        {
            await Task.CompletedTask;
        }

        private async Task AppendOlderPostsAsync(IReadOnlyList<HomePostVm> newPosts)
        {
            if (newPosts == null || newPosts.Count == 0)
                return;

            var visible = new List<HomePostVm>();
            var pending = new List<HomePostVm>();
            SplitHomePostsByVisibility(newPosts, visible, pending);

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                foreach (var vm in visible.OrderByDescending(x => x.CreatedAtUtc))
                    InsertSortedByCreatedAtDesc(Posts, vm);
            });

            var snapshot = new List<FirestoreHomeFeedService.HomePostItem>();
            foreach (var vm in Posts)
                snapshot.Add(ToHomePostItem(vm));
            HomeFeedMemoryCache.Instance.Set(snapshot);

            foreach (var pendingPost in pending)
                QueueEnsurePreviewAvailable(pendingPost);
        }

        private static void InsertSortedByCreatedAtDesc(ObservableCollection<HomePostVm> posts, HomePostVm item)
        {
            if (posts == null || item == null)
                return;

            var existing = posts.FirstOrDefault(x => x.PostId == item.PostId);
            if (existing != null)
            {
                var index = posts.IndexOf(existing);
                if (index >= 0)
                    posts.RemoveAt(index);
            }

            var insertIndex = 0;
            while (insertIndex < posts.Count && posts[insertIndex].CreatedAtUtc > item.CreatedAtUtc)
                insertIndex++;

            posts.Insert(insertIndex, item);
        }

        private void SplitHomePostsByVisibility(IEnumerable<HomePostVm> source, List<HomePostVm> visible, List<HomePostVm> pending)
        {
            foreach (var vm in source)
            {
                if (vm == null)
                    continue;

                if (vm.IsPendingUpload || vm.HasSendError)
                {
                    visible.Add(vm);
                    continue;
                }

                var contract = BuildContractFromVm(vm);
                if (HomePostValidatorV2.IsHomeVisible(contract, out _))
                {
                    visible.Add(vm);
                }
                else if (HomePostValidatorV2.IsServerReady(contract, out _))
                {
                    pending.Add(vm);
                }
            }
        }

        private void QueueEnsurePreviewAvailable(HomePostVm vm)
        {
            if (vm == null || string.IsNullOrWhiteSpace(vm.PostId))
                return;

            lock (_pendingPreviewEnsures)
            {
                if (!_pendingPreviewEnsures.Add(vm.PostId))
                    return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsurePreviewAvailableAsync(vm);
                }
                finally
                {
                    lock (_pendingPreviewEnsures)
                    {
                        _pendingPreviewEnsures.Remove(vm.PostId);
                    }
                }
            });
        }

        private async Task ApplyHomeSnapshotFromServiceAsync(
            IReadOnlyList<FirestoreHomeFeedService.HomePostItem> items,
            CancellationToken ct)
        {
            if (items == null)
                return;

            var visible = new List<HomePostVm>();
            var pending = new List<HomePostVm>();

            foreach (var post in items)
            {
                if (ct.IsCancellationRequested)
                    return;

                var vm = HomePostVm.FromService(post);
                vm.IsLiked = _likedPostIds.Contains(vm.PostId);
                vm.RetryCommand = RetryHomePostCommand;
                vm.SyncCommand = null;

                var contract = BuildContractFromVm(vm);
                if (HomePostValidatorV2.IsHomeVisible(contract, out _))
                {
                    visible.Add(vm);
                }
                else if (HomePostValidatorV2.IsServerReady(contract, out _))
                {
                    pending.Add(vm);
                }
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Posts.Clear();
                foreach (var vm in visible.OrderByDescending(x => x.CreatedAtUtc))
                    Posts.Add(vm);
            });

            foreach (var pendingPost in pending)
                QueueEnsurePreviewAvailable(pendingPost);

            var snapshot = visible.Select(ToHomePostItem).ToList();
            HomeFeedMemoryCache.Instance.Set(snapshot);
        }

        private void StartRealtimeUpdatesAfterFirstRender(CancellationToken ct)
        {
            if (_realtimeSubscribed)
                return;

            _realtimeSubscribed = true;
            StartProfileListener(ct);

            _homeListener?.Dispose();
            _homeListener = _realtime.SubscribeHomePosts(
                20,
                items => _ = ApplyHomeSnapshotFromServiceAsync(items, ct),
                ex => Debug.WriteLine($"[Home] home feed listener error: {ex}"));
            _listeners.Add(_homeListener);
        }

        private void StopRealtimeUpdates()
        {
            if (!_realtimeSubscribed)
                return;

            _realtimeSubscribed = false;
            _homeListener?.Dispose();
            _homeListener = null;
            _profileListener?.Dispose();
            _profileListener = null;
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

        private async void OnHomeComposerAttachmentRequested(object sender, EventArgs e)
        {
            try
            {
                var sheet = new Componenti_UI.BottomSheetAllegatiPage();
                sheet.AzioneSelezionata += async (_, az) =>
                {
                    await HandleHomeAttachmentActionAsync(az);
                };

                await Navigation.PushModalAsync(sheet);
            }
            catch
            {
            }
        }

        private async Task HandleHomeAttachmentActionAsync(Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione az)
        {
            switch (az)
            {
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Gallery:
                    await PickHomeFromGalleryAsync();
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Camera:
                    var choice = await DisplayActionSheet("Fotocamera", "Annulla", null, "Foto", "Video");
                    if (choice == "Foto") await CaptureHomePhotoAsync();
                    else if (choice == "Video") await CaptureHomeVideoAsync();
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Document:
                    await PickHomeDocumentAsync();
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Audio:
                    await DisplayAlert("Vocale", "Usa il microfono nella barra in basso.", "OK");
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Location:
                    await AttachHomeLocationAsync();
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Contact:
                    await AttachHomeContactAsync();
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Poll:
                    HomeComposer.TryAddPendingItem(new PendingItemVm { Kind = PendingKind.Poll, DisplayName = "Sondaggio" }, 10);
                    break;
                case Componenti_UI.BottomSheetAllegatiPage.AllegatoAzione.Event:
                    HomeComposer.TryAddPendingItem(new PendingItemVm { Kind = PendingKind.Event, DisplayName = "Evento" }, 10);
                    break;
            }
        }

        private async void OnHomeComposerSendRequested(object sender, ComposerSendPayload payload)
        {
            await SendHomePostAsync(payload, null);
        }

        private async void OnHomeComposerPendingItemSendRequested(object sender, PendingItemVm item)
        {
            await SendHomePostAsync(new ComposerSendPayload("", new[] { item }), item.LocalId);
        }

        private void OnHomeComposerPendingItemRemoved(object sender, PendingItemVm item)
        {
            // Nessuna cancellazione: i file restano in cache persistente (LRU).
        }

        private async Task SendHomePostAsync(ComposerSendPayload payload, string? sentSingleLocalId)
        {
            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            if (string.IsNullOrWhiteSpace(payload.Text) && !payload.PendingItems.Any())
                return;

            var optimistic = CreateOptimisticPost(payload);
            optimistic.RetryCommand = RetryHomePostCommand;
            AddOptimisticPost(optimistic);

            if (sentSingleLocalId != null)
            {
                var pending = HomeComposer.PendingItems.FirstOrDefault(x => x.LocalId == sentSingleLocalId);
                if (pending != null)
                    HomeComposer.PendingItems.Remove(pending);
            }
            else
            {
                HomeComposer.ClearComposer();
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendHomePostOptimisticAsync(payload, optimistic);
                }
                catch (Exception ex)
                {
                    MarkHomePostFailed(optimistic, ex);
                }
            });
        }

        private async Task<FirestoreHomeFeedService.HomeAttachment?> BuildHomeAttachmentAsync(PendingItemVm item)
        {
            if (item == null) return null;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");
            return await _homeMediaPipeline.BuildAttachmentAsync(item, idToken, default);
        }

        private HomePostVm CreateOptimisticPost(ComposerSendPayload payload)
        {
            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var nickname = _myProfile?.Nickname;
            if (string.IsNullOrWhiteSpace(nickname))
                nickname = FirebaseSessionePersistente.GetDisplayName();
            var clientNonce = Guid.NewGuid().ToString("N");

            var vm = new HomePostVm
            {
                PostId = $"local-{clientNonce}",
                ClientNonce = clientNonce,
                AuthorUid = myUid,
                AuthorNickname = nickname ?? "",
                AuthorFirstName = _myProfile?.FirstName ?? "",
                AuthorLastName = _myProfile?.LastName ?? "",
                AuthorAvatarPath = _myProfile?.AvatarPath ?? _myProfile?.PhotoLocalPath ?? _myProfile?.PhotoUrl,
                AuthorAvatarUrl = _myProfile?.AvatarUrl ?? _myProfile?.PhotoUrl,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Text = payload.Text ?? "",
                SchemaVersion = HomePostValidatorV2.SchemaVersion,
                Ready = true,
                Deleted = false,
                IsPendingUpload = true,
                HasSendError = false,
                PendingText = payload.Text ?? ""
            };

            foreach (var item in payload.PendingItems ?? Array.Empty<PendingItemVm>())
            {
                vm.PendingItems.Add(new PendingItemVm
                {
                    Kind = item.Kind,
                    DisplayName = item.DisplayName,
                    LocalFilePath = item.LocalFilePath,
                    MediaCacheKey = item.MediaCacheKey,
                    DurationMs = item.DurationMs,
                    SizeBytes = item.SizeBytes,
                    Latitude = item.Latitude,
                    Longitude = item.Longitude,
                    Address = item.Address,
                    ContactName = item.ContactName,
                    ContactPhone = item.ContactPhone
                });

                var attVm = CreateOptimisticAttachment(item);
                if (attVm != null)
                    vm.AttachAttachment(attVm);
            }

            return vm;
        }

        private static HomeAttachmentVm? CreateOptimisticAttachment(PendingItemVm item)
        {
            if (item == null)
                return null;

            var type = item.Kind switch
            {
                PendingKind.Image => "image",
                PendingKind.Video => "video",
                PendingKind.AudioDraft => "audio",
                PendingKind.File => "file",
                PendingKind.Location => "location",
                PendingKind.Contact => "contact",
                PendingKind.Poll => "poll",
                PendingKind.Event => "event",
                _ => "file"
            };

            var vm = new HomeAttachmentVm
            {
                Type = type,
                FileName = item.DisplayName,
                LocalPath = item.LocalFilePath,
                SizeBytes = item.SizeBytes,
                DurationMs = item.DurationMs,
                Latitude = item.Latitude,
                Longitude = item.Longitude,
                Address = item.Address
            };

            return vm;
        }

        private void AddOptimisticPost(HomePostVm vm)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.RetryCommand = RetryHomePostCommand;
                InsertSortedByCreatedAtDesc(Posts, vm);
                FeedCollection.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
            });

            _ = ScrollHomeToTopWithRetryAsync(considerKeyboard: true);
            RefreshMemoryCacheFromPosts();
        }

        private async Task SendHomePostOptimisticAsync(ComposerSendPayload payload, HomePostVm vm)
        {
            var attachments = new List<FirestoreHomeFeedService.HomeAttachment>();
            var existingAttachments = vm.Attachments
                .Where(att => att != null)
                .Select(att => new
                {
                    Key = BuildAttachmentKey(att.Type, att.FileName, att.SizeBytes),
                    att.LocalPath,
                    att.ThumbLocalPath
                })
                .ToList();

            foreach (var item in vm.PendingItems)
            {
                var att = await BuildHomeAttachmentAsync(item);
                if (att == null)
                    throw new InvalidOperationException("Allegato non disponibile.");

                attachments.Add(att);
            }

            var postId = await _homeFeed.CreatePostAsync(vm.PendingText ?? "", attachments, clientNonce: vm.ClientNonce);
            var localByFullRemote = new Dictionary<string, string>(StringComparer.Ordinal);
            var localByPreviewRemote = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var att in attachments)
            {
                var key = BuildAttachmentKey(att.Type, att.FileName, att.SizeBytes);
                var match = existingAttachments.FirstOrDefault(x => x.Key == key);
                if (match == null)
                    continue;

                if (!string.IsNullOrWhiteSpace(att.StoragePath) && !string.IsNullOrWhiteSpace(match.LocalPath))
                    localByFullRemote[att.StoragePath] = match.LocalPath;

                var previewRemotePath = att.GetPreviewRemotePath();
                if (!string.IsNullOrWhiteSpace(previewRemotePath) && !string.IsNullOrWhiteSpace(match.ThumbLocalPath))
                    localByPreviewRemote[previewRemotePath] = match.ThumbLocalPath;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.PostId = postId;
                vm.IsPendingUpload = false;
                vm.HasSendError = false;
                vm.HasFullData = true;
                vm.SchemaVersion = HomePostValidatorV2.SchemaVersion;
                vm.Ready = true;
                vm.Deleted = false;
                vm.Attachments.Clear();
                foreach (var att in attachments)
                {
                    var rebuilt = HomeAttachmentVm.FromService(att);
                    if (!string.IsNullOrWhiteSpace(rebuilt.StoragePath) && localByFullRemote.TryGetValue(rebuilt.StoragePath, out var fullLocal))
                        rebuilt.LocalPath = fullLocal;

                    var previewRemotePath = rebuilt.GetPreviewRemotePath();
                    if (!string.IsNullOrWhiteSpace(previewRemotePath) && localByPreviewRemote.TryGetValue(previewRemotePath, out var previewLocal))
                        rebuilt.ThumbLocalPath = previewLocal;

                    vm.AttachAttachment(rebuilt);
                }
            });

            RefreshMemoryCacheFromPosts();
        }

        private void MarkHomePostFailed(HomePostVm vm, Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.IsPendingUpload = false;
                vm.HasSendError = true;
            });

            RefreshMemoryCacheFromPosts();
            MainThread.BeginInvokeOnMainThread(() => _ = ShowPopupAsync(FormatExceptionForPopup(ex), "Errore invio post"));
        }

        private async Task RetryHomePostAsync(HomePostVm? vm)
        {
            if (vm == null)
                return;

            try
            {
                vm.HasSendError = false;
                vm.IsPendingUpload = true;
                await SendHomePostOptimisticAsync(new ComposerSendPayload(vm.PendingText, vm.PendingItems), vm);
            }
            catch (Exception ex)
            {
                MarkHomePostFailed(vm, ex);
            }
        }

        private async Task PickHomeFromGalleryAsync()
        {
            var choice = await DisplayActionSheet("Galleria", "Annulla", null, "Foto", "Video");
            if (choice == "Foto")
            {
                var fr = await MediaPicker.Default.PickPhotoAsync();
                if (fr == null) return;
                var registration = await CopyToCacheAsync(fr, "home_photo");
                var local = registration.LocalPath;
                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Image,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
                    MediaCacheKey = registration.CacheKey,
                    SizeBytes = new FileInfo(local).Length
                }, 10);
            }
            else if (choice == "Video")
            {
                var fr = await MediaPicker.Default.PickVideoAsync();
                if (fr == null) return;
                var registration = await CopyToCacheAsync(fr, "home_video");
                var local = registration.LocalPath;
                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Video,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
                    MediaCacheKey = registration.CacheKey,
                    SizeBytes = new FileInfo(local).Length,
                    DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
                }, 10);
            }
        }

        private async Task CaptureHomePhotoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CapturePhotoAsync();
            if (fr == null) return;
            var registration = await CopyToCacheAsync(fr, "home_camera_photo");
            var local = registration.LocalPath;
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Image,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                MediaCacheKey = registration.CacheKey,
                SizeBytes = new FileInfo(local).Length
            }, 10);
        }

        private async Task CaptureHomeVideoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CaptureVideoAsync();
            if (fr == null) return;
            var registration = await CopyToCacheAsync(fr, "home_camera_video");
            var local = registration.LocalPath;
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Video,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                MediaCacheKey = registration.CacheKey,
                SizeBytes = new FileInfo(local).Length,
                DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
            }, 10);
        }

        private async Task PickHomeDocumentAsync()
        {
            var res = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Seleziona documento" });
            if (res == null) return;
            var registration = await CopyToCacheAsync(res, "home_doc");
            var local = registration.LocalPath;
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.File,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                MediaCacheKey = registration.CacheKey,
                SizeBytes = new FileInfo(local).Length
            }, 10);
        }

        private async Task AttachHomeLocationAsync()
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

                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Location,
                    DisplayName = "Posizione",
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude
                }, 10);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", FormatExceptionForPopup(ex), "OK");
            }
        }

        private async Task AttachHomeContactAsync()
        {
            try
            {
                var c = await Contacts.Default.PickContactAsync();
                if (c == null) return;

                var name = (c.DisplayName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    name = "Contatto";

                var phone = (c.Phones?.FirstOrDefault()?.PhoneNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(phone))
                {
                    await DisplayAlert("Contatto", "Contatto senza numero telefonico.", "OK");
                    return;
                }

                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Contact,
                    DisplayName = name,
                    ContactName = name,
                    ContactPhone = phone
                }, 10);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", FormatExceptionForPopup(ex), "OK");
            }
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

                var res = await _homeFeed.ToggleLikeOptimisticAsync(post.PostId, wasLiked, before);

                post.IsLiked = res.IsLikedNow;
                post.LikeCount = Math.Max(0, res.LikeCount);
                post.NotifyCounters();

                if (res.IsLikedNow)
                    _likedPostIds.Add(post.PostId);
                else
                    _likedPostIds.Remove(post.PostId);

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
            }
        }

        private async void OnFeedAudioClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not HomeAttachmentVm att)
                return;

            if (att.IsPlaying)
            {
                _audioPlayback.StopPlaybackSafe();
                att.IsPlaying = false;
                return;
            }

            foreach (var post in Posts)
            {
                foreach (var a in post.Attachments.Where(x => x.IsPlaying))
                    a.IsPlaying = false;
            }

            try
            {
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                var path = await EnsureHomeAudioDownloadedAsync(att);
                if (string.IsNullOrWhiteSpace(path))
                    return;

                att.IsPlaying = true;
                await _audioPlayback.PlayAsync(path);
                att.IsPlaying = false;
            }
            catch (Exception ex)
            {
                att.IsPlaying = false;
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore audio");
            }
        }

        private async void OnOpenVideoTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not HomeAttachmentVm att)
                    return;

                await OpenVideoFromHomeAsync(att);
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore video");
            }
        }

        private async void OnOpenImageTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not HomeAttachmentVm att)
                    return;

                await OpenImageFromHomeAsync(att);
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore immagine");
            }
        }

        private async void OnOpenFileTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not HomeAttachmentVm att)
                    return;

                await OpenFileFromHomeAsync(att);
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore file");
            }
        }

        private async Task OpenVideoFromHomeAsync(HomeAttachmentVm att)
        {
            if (att == null || string.IsNullOrWhiteSpace(att.StoragePath))
                return;

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
            {
                await Navigation.PushAsync(new VideoPlayerPage(att.LocalPath!, att.DisplayPreviewSource));

                return;
            }

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                await ShowPopupAsync("Contenuto non disponibile offline.", "Offline");
                return;
            }

            var local = await EnsureHomeMediaDownloadedAsync(att, att.FileName ?? "video.mp4", showErrors: true);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                return;

            await Navigation.PushAsync(new VideoPlayerPage(local, att.DisplayPreviewSource));
        }

        private async Task OpenImageFromHomeAsync(HomeAttachmentVm att)
        {
            if (att == null || string.IsNullOrWhiteSpace(att.StoragePath))
                return;

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            var local = await EnsureHomeMediaDownloadedAsync(att, att.FileName ?? "image.jpg", showErrors: true);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                return;

            var img = new Image
            {
                Source = local,
                Aspect = Aspect.AspectFit,
                Opacity = 0,
                Scale = 0.95
            };

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

            var container = new Grid
            {
                BackgroundColor = Colors.Black,
                Children = { img, close }
            };

            var page = new ContentPage { BackgroundColor = Colors.Black, Content = container };
            close.Clicked += async (_, __) => await Navigation.PopModalAsync();

            var tapToClose = new TapGestureRecognizer();
            tapToClose.Tapped += async (_, __) => await Navigation.PopModalAsync();
            container.GestureRecognizers.Add(tapToClose);

            await Navigation.PushModalAsync(page);
            try
            {
                await Task.WhenAll(
                    img.FadeTo(1, 160, Easing.CubicOut),
                    img.ScaleTo(1, 160, Easing.CubicOut));
            }
            catch { }
        }

        private async Task OpenFileFromHomeAsync(HomeAttachmentVm att)
        {
            if (att == null || string.IsNullOrWhiteSpace(att.StoragePath))
                return;

            if (att.IsPdf)
            {
                await OnOpenPdfFromHome(att);
                return;
            }

            if (att.IsVideo)
            {
                await OpenVideoFromHomeAsync(att);
                return;
            }

            if (att.IsImage)
            {
                await OpenImageFromHomeAsync(att);
                return;
            }

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            var fileName = att.FileName ?? "file.bin";
            var local = await EnsureHomeMediaDownloadedAsync(att, fileName, showErrors: true);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                return;

            att.LocalPath = local;
            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(local)
            });
        }

        private async Task<string?> EnsureHomeAudioDownloadedAsync(HomeAttachmentVm att)
        {
            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
                return att.LocalPath;

            if (string.IsNullOrWhiteSpace(att.StoragePath))
                return null;

            var local = await EnsureHomeMediaDownloadedAsync(att, att.FileName ?? "audio.m4a", showErrors: false);
            if (string.IsNullOrWhiteSpace(local))
                return null;

            att.LocalPath = local;
            return local;
        }

        private async Task OnOpenPdfFromHome(HomeAttachmentVm? att)
        {
            if (att == null || string.IsNullOrWhiteSpace(att.StoragePath))
                return;

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            var fileName = att.FileName ?? "document.pdf";
            if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.pdf";

            var local = await EnsureHomeMediaDownloadedAsync(att, fileName, showErrors: true);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
            {
                await ShowPopupAsync("PDF non disponibile.", "Info");
                return;
            }

            await Navigation.PushAsync(new PdfViewerPage(local, fileName));
        }

        private async Task<string?> EnsureHomeMediaDownloadedAsync(HomeAttachmentVm att, string fileName, bool showErrors)
        {
            if (att == null)
                return null;

            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
                return att.LocalPath;

            if (string.IsNullOrWhiteSpace(att.StoragePath))
                return null;

            var cached = await _mediaCache.TryGetCachedPathAsync(att.StoragePath, isThumb: false);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                att.LocalPath = cached;
                _ = UpdateCacheForAttachmentAsync(att);
                return cached;
            }

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                if (showErrors)
                    await ShowPopupAsync("Contenuto non disponibile offline.", "Offline");
                return null;
            }

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                return null;

            CancellationTokenSource? cts = null;
            CancellationTokenSource? countdownCts = null;
            Task? countdownTask = null;
            try
            {
                MainThread.BeginInvokeOnMainThread(() => att.IsDownloading = true);
                cts = new CancellationTokenSource(TimeSpan.FromSeconds(AppCacheOptions.MediaDownloadTimeoutSeconds));
                countdownCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                countdownTask = Task.Run(async () =>
                {
                    var remaining = AppCacheOptions.MediaDownloadTimeoutSeconds;
                    while (remaining >= 0 && !countdownCts.IsCancellationRequested)
                    {
                        MainThread.BeginInvokeOnMainThread(() => att.DownloadCountdownSeconds = remaining);
                        await Task.Delay(1000, countdownCts.Token);
                        remaining--;
                    }
                }, countdownCts.Token);

                var local = await _mediaCache.GetOrDownloadAsync(idToken!, att.StoragePath!, fileName, isThumb: false, cts.Token);
                countdownCts.Cancel();
                if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                {
                    if (showErrors)
                        await ShowPopupAsync("Impossibile scaricare il contenuto.", "Errore");
                    return null;
                }

                att.LocalPath = local;
                _ = UpdateCacheForAttachmentAsync(att);
                return local;
            }
            catch (OperationCanceledException)
            {
                if (showErrors)
                    await ShowPopupAsync("Timeout download contenuto.", "Errore");
                return null;
            }
            catch
            {
                if (showErrors)
                    await ShowPopupAsync("Impossibile aprire il contenuto.", "Errore");
                return null;
            }
            finally
            {
                if (countdownCts != null)
                {
                    try { countdownCts.Cancel(); } catch { }
                }
                if (countdownTask != null)
                {
                    try { await countdownTask; } catch { }
                }
                if (countdownCts != null)
                    countdownCts.Dispose();
                if (cts != null)
                    cts.Dispose();
                MainThread.BeginInvokeOnMainThread(() => att.DownloadCountdownSeconds = 0);
                MainThread.BeginInvokeOnMainThread(() => att.IsDownloading = false);
            }
        }

        private async Task ScrollHomeToTopWithRetryAsync(bool considerKeyboard)
        {
            var delays = considerKeyboard
                ? new[] { 120, 260, 520 }
                : new[] { 120 };

            foreach (var delay in delays)
            {
                await Task.Delay(delay);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (Posts.Count == 0)
                        return;
                    FeedCollection.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
                });
            }
        }

        private static string BuildAttachmentKey(string? type, string? fileName, long sizeBytes)
            => $"{type ?? ""}|{fileName ?? ""}|{sizeBytes}";

        private void RefreshMemoryCacheFromPosts()
        {
            var snapshot = Posts.Select(ToHomePostItem).ToList();
            HomeFeedMemoryCache.Instance.Set(snapshot);
        }

        private Task UpdateCacheForAttachmentAsync(HomeAttachmentVm att)
        {
            if (att == null)
                return Task.CompletedTask;

            return MainThread.InvokeOnMainThreadAsync(() =>
            {
                HomePostVm? owner = null;
                foreach (var post in Posts)
                {
                    if (post.Attachments.Contains(att))
                    {
                        owner = post;
                        break;
                    }
                }

                if (owner == null)
                    return Task.CompletedTask;

                RefreshMemoryCacheFromPosts();
                return Task.CompletedTask;
            });
        }

        private Task UpdateCacheForPostAsync(HomePostVm post)
        {
            if (post == null)
                return Task.CompletedTask;

            RefreshMemoryCacheFromPosts();
            return Task.CompletedTask;
        }

        private async Task EnsurePreviewAvailableAsync(HomePostVm post)
        {
            if (post == null)
                return;

            var contract = BuildContractFromVm(post);
            if (!HomePostValidatorV2.IsServerReady(contract, out _))
                return;

            var requiresAnyPreview = post.Attachments.Any(att => att.RequiresPreview);
            if (!requiresAnyPreview)
                return;

            var updated = false;
            foreach (var att in post.Attachments)
            {
                if (!att.RequiresPreview)
                    continue;

                var previewRemotePath = att.GetPreviewRemotePath();
                if (string.IsNullOrWhiteSpace(previewRemotePath))
                {
                    post.Ready = false;
                    await UpdateCacheForPostAsync(post);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(att.ThumbLocalPath) && File.Exists(att.ThumbLocalPath))
                    continue;

                var cached = await _mediaCache.TryGetCachedPathAsync(previewRemotePath, isThumb: true);
                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                {
                    await MainThread.InvokeOnMainThreadAsync(() => att.ThumbLocalPath = cached);
                    updated = true;
                    continue;
                }

                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                    return;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return;

                var thumbLocal = await _mediaCache.GetOrDownloadAsync(idToken!, previewRemotePath, att.FileName ?? "thumb.jpg", isThumb: true, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(thumbLocal) || !File.Exists(thumbLocal))
                {
                    post.Ready = false;
                    await UpdateCacheForPostAsync(post);
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(() => att.ThumbLocalPath = thumbLocal);
                updated = true;
            }

            if (updated)
                await UpdateCacheForPostAsync(post);

            var refreshed = BuildContractFromVm(post);
            if (!HomePostValidatorV2.IsHomeVisible(refreshed, out _))
                return;

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                post.RetryCommand ??= RetryHomePostCommand;
                InsertSortedByCreatedAtDesc(Posts, post);
            });

            var snapshot = new List<FirestoreHomeFeedService.HomePostItem>();
            foreach (var vm in Posts)
                snapshot.Add(ToHomePostItem(vm));
            HomeFeedMemoryCache.Instance.Set(snapshot);
        }

        private async Task<MediaCacheService.MediaRegistration> CopyToCacheAsync(FileResult fr, string prefix)
        {
            var ext = Path.GetExtension(fr.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

            var dest = Path.Combine(FileSystem.CacheDirectory, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");

            // FIX: gli "await using var" tenevano il file aperto (lock) fino a fine metodo.
            // Qui chiudiamo davvero src/dst prima di registrare il file nella cache persistente.
            await using (var src = await fr.OpenReadAsync())
            {
                await using (var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    await src.CopyToAsync(dst);
                    await dst.FlushAsync();
                }
            }

            var contentType = FirebaseStorageRestClient.GuessContentTypeFromPath(fr.FileName);
            var kind = HomeMediaPipeline.GetMediaKind(contentType, fr.FileName).ToString().ToLowerInvariant();
            var registration = await _mediaCache.RegisterLocalFileAsync(dest, kind, CancellationToken.None);
            if (registration == null)
                throw new InvalidOperationException("Registrazione cache locale fallita.");

            try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            return registration;
        }

        // ===================== 4) MENU LATERALE SINISTRO =================
        private async void OnMenuLaterale_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleMenuAsync();
        }

        private async Task ToggleMenuAsync()
        {
            var overlayMenu = this.FindByName<Grid>("overlay_menu");
            var menuPanel = this.FindByName<VisualElement>("menu_panel");

            if (overlayMenu == null || menuPanel == null)
                return;

            if (_menuAperto)
            {
                _menuAperto = false;
                await Task.WhenAll(
                    menuPanel.TranslateTo(-menuPanel.Width, 0, 250, Easing.CubicIn),
                    overlayMenu.FadeTo(0, 250, Easing.CubicInOut)
                );
                overlayMenu.IsVisible = false;
            }
            else
            {
                overlayMenu.IsVisible = true;
                overlayMenu.Opacity = 0;
                menuPanel.TranslationX = -menuPanel.Width;

                await Task.WhenAll(
                    menuPanel.TranslateTo(0, 0, 250, Easing.CubicOut),
                    overlayMenu.FadeTo(1, 250, Easing.CubicInOut)
                );

                _menuAperto = true;
            }
        }

        private async void OnMenuVoice(object? sender, EventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async void OnMenuVoice(object? sender, TappedEventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async Task HandleMenuVoiceAsync(object? sender)
        {
            string? voce = null;
            if (sender is Label lbl) voce = lbl.Text;
            else if (sender is Button btn) voce = btn.Text;

            voce ??= "free";
            voce = voce.Trim();

            if (_menuAperto)
            {
                await ToggleMenuAsync();
            }

            var page = new ContentPage
            {
                Title = voce,
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16),
                    Children =
                    {
                        new Label
                        {
                            Text = $"Pagina {voce} in sviluppo.",
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center,
                            TextColor = Colors.White
                        }
                    }
                },
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Colors.Black, 0f),
                        new GradientStop(Color.FromArgb("#003020"), 0.6f),
                        new GradientStop(Color.FromArgb("#00452A"), 1f)
                    },
                    new Point(0, 0),
                    new Point(0, 1))
            };

            await Navigation.PushAsync(page);
        }
        // =================================================================

        // ===================== 5) MENU LATERALE DESTRO (ACCOUNT) =========
        private async void OnLogoutMenu(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async void OnLogoutMenu_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async Task ToggleLogoutMenuAsync()
        {
            var overlayLogout = this.FindByName<Grid>("overlay_logout_menu");
            var logoutPanel = this.FindByName<VisualElement>("logout_menu_panel");

            if (overlayLogout == null || logoutPanel == null)
                return;

            if (_logoutMenuAperto)
            {
                _logoutMenuAperto = false;
                await Task.WhenAll(
                    logoutPanel.TranslateTo(logoutPanel.Width, 0, 250, Easing.CubicIn),
                    overlayLogout.FadeTo(0, 250, Easing.CubicInOut)
                );
                overlayLogout.IsVisible = false;
            }
            else
            {
                overlayLogout.IsVisible = true;
                overlayLogout.Opacity = 0;
                logoutPanel.TranslationX = logoutPanel.Width;

                await Task.WhenAll(
                    logoutPanel.TranslateTo(0, 0, 250, Easing.CubicOut),
                    overlayLogout.FadeTo(1, 250, Easing.CubicInOut)
                );

                _logoutMenuAperto = true;
            }
        }

        private async void OnLogoutInfoClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await ShowInfoBiliardoAppAsync();
        }

        private async void OnInfoCacheClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }

            await Navigation.PushAsync(new InfoCachePage());
        }

        private async void OnLogoutLogClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }

            await Navigation.PushAsync(new LogTrasferimentiPage());
        }

        private async void OnLogoutExitClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await EseguiLogoutAsync();
        }
        // =================================================================

        // ===================== 6) POPUP PERSONALIZZATO HOME ===============
        private Task ShowPopupAsync(string message, string title)
        {
            PopupTitleLabel.Text = title;
            PopupMessageLabel.Text = message;

            PopupOverlay.IsVisible = true;
            _popupTcs = new TaskCompletionSource<bool>();
            return _popupTcs.Task;
        }

        private void OnPopupOkClicked(object? sender, EventArgs e)
        {
            PopupOverlay.IsVisible = false;
            _popupTcs?.TrySetResult(true);
            _popupTcs = null;
        }

        private async Task ShowInfoBiliardoAppAsync()
        {
            static string SafeValue(string? value) =>
                string.IsNullOrWhiteSpace(value) ? "n/d" : value;

            var appName = string.IsNullOrWhiteSpace(AppInfo.Current.Name)
                ? "BiliardoApp"
                : AppInfo.Current.Name;
            var version = SafeValue(AppInfo.Current.VersionString);
            var build = SafeValue(AppInfo.Current.BuildString);
            var packageName = SafeValue(AppInfo.Current.PackageName);
            var platform = SafeValue(DeviceInfo.Current.Platform.ToString());
            var osVersion = SafeValue(DeviceInfo.Current.VersionString);
            var osDescription = SafeValue(RuntimeInformation.OSDescription);
            var framework = SafeValue(RuntimeInformation.FrameworkDescription);
            var architecture = SafeValue(RuntimeInformation.ProcessArchitecture.ToString());
            var manufacturer = SafeValue(DeviceInfo.Current.Manufacturer);
            var model = SafeValue(DeviceInfo.Current.Model);
            var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var releaseVersion = SafeValue(AppReleaseInfo.Version);

            var messageBuilder = new StringBuilder()
                .AppendLine($"{appName} v{version} (build {build})")
                .AppendLine($"Versione release: {releaseVersion}")
                .AppendLine($"Package: {packageName}")
                .AppendLine($"Piattaforma: {platform} {osVersion}")
                .AppendLine($"OS: {osDescription}")
                .AppendLine($"Runtime: {framework}")
                .AppendLine($"Architettura: {architecture}")
                .AppendLine($"Dispositivo: {manufacturer} {model}")
                .AppendLine($"Data/Ora (debug): {now}");

            await ShowPopupAsync(messageBuilder.ToString().TrimEnd(), "Informazioni");
        }
        // =================================================================

        // ===================== 7) MESSAGGI (CHAT) =========================
        private async void OnApriMessaggi(object? sender, EventArgs e)
        {
            try
            {
                await Navigation.PushAsync(new Pagine_Messaggi.Pagina_MessaggiLista());
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore");
            }
        }
        // =================================================================

        // ===================== 8) AZIONI (ICONA SFIDA, MERCATINO) =========
        private async void OnMercatino(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Sezione mercatino in sviluppo.", "Mercatino");
        }

        private async void OnCreaSfida(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Funzione Crea sfida in sviluppo.", "Crea sfida");
        }
        // =================================================================

        // ===================== 9) LOGOUT (LOGICA) ==========================
        private async Task EseguiLogoutAsync()
        {
            try { await FirebaseSessionePersistente.LogoutAsync(); } catch { }

            Application.Current.MainPage = new NavigationPage(new Pagina_Login());
            await Task.CompletedTask;
        }
        // =================================================================

        // ===================== 10) ACCESSO OSPITE (STUB) ==================
        private async void OnEntraComeOspite(object? sender, EventArgs e)
        {
            await ShowPopupAsync("Accesso come ospite (sola lettura) in sviluppo.", "Ospite");
        }
        // =================================================================

        public sealed class HomePostVm : BindableObject
        {
            private bool _isPendingUpload;
            private bool _hasSendError;
            private bool _requiresSync;
            private Command<HomePostVm>? _syncCommand;
            private bool _hasFullData;
            private bool _isReadyForDisplay;

            public string PostId { get; set; } = "";
            public string? ClientNonce { get; set; }
            public string AuthorUid { get; set; } = "";
            public string AuthorNickname { get; set; } = "";
            public string AuthorFirstName { get; set; } = "";
            public string AuthorLastName { get; set; } = "";
            public string? AuthorAvatarPath { get; set; }
            public string? AuthorAvatarUrl { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string Text { get; set; } = "";
            public ObservableCollection<HomeAttachmentVm> Attachments { get; set; } = new();
            public int LikeCount { get; set; }
            public int CommentCount { get; set; }
            public int ShareCount { get; set; }
            public int SchemaVersion { get; set; } = HomePostValidatorV2.SchemaVersion;
            public bool Ready { get; set; }
            public bool Deleted { get; set; }
            public DateTimeOffset? DeletedAtUtc { get; set; }
            public string? RepostOfPostId { get; set; }
            public string LikeHeartGlyph => LikeCount > 0 ? "❤️" : "🤍";


            // Regola richiesta:
            // - LikeCount == 0  -> cuore pieno bianco
            // - LikeCount  > 0  -> cuore pieno rosso
            public Microsoft.Maui.Graphics.Color LikeHeartColor => LikeCount > 0 ? Colors.Red : Colors.White;

            // Mantengo IsLiked (può servire in futuro), ma NON guida più il colore.
            public bool IsLiked { get; set; }

            public bool IsPendingUpload
            {
                get => _isPendingUpload;
                set
                {
                    _isPendingUpload = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusLabel));
                    OnPropertyChanged(nameof(HasStatus));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }

            public bool HasSendError
            {
                get => _hasSendError;
                set
                {
                    _hasSendError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusLabel));
                    OnPropertyChanged(nameof(HasStatus));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }

            public bool HasStatus => IsPendingUpload || HasSendError;
            public string StatusLabel => HasSendError ? "Errore invio" : IsPendingUpload ? "In invio" : "";
            public Color StatusColor => HasSendError ? Colors.OrangeRed : Colors.LightGray;

            public Command<HomePostVm>? RetryCommand { get; set; }
            public Command<HomePostVm>? SyncCommand
            {
                get => _syncCommand;
                set
                {
                    _syncCommand = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSyncAction));
                }
            }
            public string PendingText { get; set; } = "";
            public List<PendingItemVm> PendingItems { get; } = new();

            public bool HasText => !string.IsNullOrWhiteSpace(Text);
            public string AuthorFullName => $"{AuthorFirstName} {AuthorLastName}".Trim();
            public bool HasAuthorFullName => !string.IsNullOrWhiteSpace(AuthorFullName);
            public string AuthorFullNameDisplay => HasAuthorFullName ? $"({AuthorFullName})" : "";
            public string AuthorDisplayName => AuthorNickname;
            public string AuthorAvatarDisplayName => HasAuthorFullName ? AuthorFullName : AuthorNickname;

            public Color AuthorNicknameColor => GetNicknameColor(PostId);
            public bool HasAttachments => Attachments != null && Attachments.Count > 0;
            public bool HasSyncAction => RequiresSync && SyncCommand != null;

            public bool RequiresSync
            {
                get => _requiresSync;
                set
                {
                    _requiresSync = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSyncAction));
                }
            }

            public string CreatedAtLabel => CreatedAtUtc.ToLocalTime().ToString("g");

            // Compat (eventuali usi altrove)
            public Color LikeTextColor => LikeCount > 0 ? Colors.Red : Colors.White;
            public string LikeLabel => $"♥ {LikeCount}";
            public string CommentLabel => $"💬 {CommentCount}";
            public string ShareLabel => $"↗ {ShareCount}";
            public bool HasFullData
            {
                get => _hasFullData;
                set
                {
                    if (_hasFullData == value)
                        return;
                    _hasFullData = value;
                    UpdateReadyState();
                }
            }

            public bool IsReadyForDisplay
            {
                get => _isReadyForDisplay;
                private set
                {
                    if (_isReadyForDisplay == value)
                        return;
                    _isReadyForDisplay = value;
                    OnPropertyChanged();
                }
            }

            public void NotifyCounters()
            {
                // Compat
                OnPropertyChanged(nameof(LikeLabel));
                OnPropertyChanged(nameof(CommentLabel));
                OnPropertyChanged(nameof(ShareLabel));
                OnPropertyChanged(nameof(LikeTextColor));
                OnPropertyChanged(nameof(LikeHeartGlyph));


                // ✅ FIX: ora in XAML bindiamo i contatori direttamente
                OnPropertyChanged(nameof(LikeCount));
                OnPropertyChanged(nameof(CommentCount));
                OnPropertyChanged(nameof(ShareCount));

                OnPropertyChanged(nameof(LikeHeartColor));
            }

            public void AttachAttachment(HomeAttachmentVm attachment)
            {
                if (attachment == null)
                    return;

                attachment.PreviewSourceChanged += (_, __) => UpdateReadyState();
                Attachments.Add(attachment);
                UpdateReadyState();
            }

            public static HomePostVm FromService(FirestoreHomeFeedService.HomePostItem post)
            {
                var vm = new HomePostVm
                {
                    PostId = post.PostId,
                    ClientNonce = post.ClientNonce,
                    AuthorUid = post.AuthorUid,
                    AuthorNickname = post.AuthorNickname,
                    AuthorFirstName = post.AuthorFirstName,
                    AuthorLastName = post.AuthorLastName,
                    AuthorAvatarPath = post.AuthorAvatarPath,
                    AuthorAvatarUrl = post.AuthorAvatarUrl,
                    CreatedAtUtc = post.CreatedAtUtc,
                    Text = post.Text ?? "",
                    LikeCount = post.LikeCount,
                    CommentCount = post.CommentCount,
                    ShareCount = post.ShareCount,
                    SchemaVersion = post.SchemaVersion,
                    Ready = post.Ready,
                    Deleted = post.Deleted,
                    DeletedAtUtc = post.DeletedAtUtc,
                    RepostOfPostId = post.RepostOfPostId,
                    IsLiked = post.IsLiked,
                    IsPendingUpload = false,
                    HasSendError = false,
                    RequiresSync = false,
                    HasFullData = true
                };

                foreach (var att in post.Attachments)
                    vm.AttachAttachment(HomeAttachmentVm.FromService(att));

                vm.UpdateReadyState();

                return vm;
            }

            private void UpdateReadyState()
            {
                if (IsPendingUpload || HasSendError)
                {
                    IsReadyForDisplay = true;
                    return;
                }

                var contract = BuildContractFromVm(this);
                IsReadyForDisplay = HomePostValidatorV2.IsHomeVisible(contract, out _);
            }

            private static readonly Color[] NicknamePalette = new[]
            {
                Color.FromArgb("#FFD54F"),
                Color.FromArgb("#4FC3F7"),
                Color.FromArgb("#FF8A65"),
                Color.FromArgb("#81C784"),
                Color.FromArgb("#BA68C8"),
                Color.FromArgb("#F06292"),
                Color.FromArgb("#A1887F"),
                Color.FromArgb("#90A4AE")
            };

            private static Color GetNicknameColor(string seed)
            {
                if (NicknamePalette.Length == 0)
                    return Colors.White;

                var hash = seed?.GetHashCode() ?? 0;
                var idx = Math.Abs(hash) % NicknamePalette.Length;
                return NicknamePalette[idx];
            }
        }

        private static HomePostContractV2 BuildContractFromVm(HomePostVm vm)
        {
            return new HomePostContractV2(
                PostId: vm.PostId,
                CreatedAtUtc: vm.CreatedAtUtc,
                AuthorUid: vm.AuthorUid,
                AuthorNickname: vm.AuthorNickname,
                AuthorFirstName: vm.AuthorFirstName,
                AuthorLastName: vm.AuthorLastName,
                AuthorAvatarPath: vm.AuthorAvatarPath,
                AuthorAvatarUrl: vm.AuthorAvatarUrl,
                Text: vm.Text ?? "",
                Attachments: vm.Attachments.Select(ToContract).ToArray(),
                Deleted: vm.Deleted,
                DeletedAtUtc: vm.DeletedAtUtc,
                RepostOfPostId: vm.RepostOfPostId,
                ClientNonce: vm.ClientNonce,
                LikeCount: vm.LikeCount,
                CommentCount: vm.CommentCount,
                ShareCount: vm.ShareCount,
                SchemaVersion: vm.SchemaVersion,
                Ready: vm.Ready);
        }

        private static FirestoreHomeFeedService.HomePostItem ToHomePostItem(HomePostVm vm)
        {
            return new FirestoreHomeFeedService.HomePostItem(
                PostId: vm.PostId,
                AuthorUid: vm.AuthorUid,
                AuthorNickname: vm.AuthorNickname,
                AuthorFirstName: vm.AuthorFirstName,
                AuthorLastName: vm.AuthorLastName,
                AuthorAvatarPath: vm.AuthorAvatarPath,
                AuthorAvatarUrl: vm.AuthorAvatarUrl,
                CreatedAtUtc: vm.CreatedAtUtc,
                Text: vm.Text ?? "",
                Attachments: vm.Attachments.Select(ToHomeAttachment).ToList(),
                LikeCount: vm.LikeCount,
                CommentCount: vm.CommentCount,
                ShareCount: vm.ShareCount,
                Deleted: vm.Deleted,
                DeletedAtUtc: vm.DeletedAtUtc,
                RepostOfPostId: vm.RepostOfPostId,
                ClientNonce: vm.ClientNonce,
                SchemaVersion: vm.SchemaVersion,
                Ready: vm.Ready,
                IsLiked: vm.IsLiked);
        }

        private static FirestoreHomeFeedService.HomeAttachment ToHomeAttachment(HomeAttachmentVm att)
        {
            return new FirestoreHomeFeedService.HomeAttachment(
                Type: att.Type ?? "",
                StoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: BuildAttachmentExtra(att),
                ThumbStoragePath: att.ThumbStoragePath,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                ThumbWidth: att.ThumbWidth,
                ThumbHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static HomeAttachmentContractV2 ToContract(HomeAttachmentVm att)
        {
            return new HomeAttachmentContractV2(
                Type: att.Type ?? "",
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: BuildAttachmentExtra(att),
                PreviewStoragePath: att.GetPreviewRemotePath(),
                FullStoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                PreviewLocalPath: att.ThumbLocalPath,
                FullLocalPath: att.LocalPath,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                PreviewWidth: att.ThumbWidth,
                PreviewHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static HomeAttachmentContractV2 ToContract(FirestoreHomeFeedService.HomeAttachment att)
        {
            return new HomeAttachmentContractV2(
                Type: att.Type ?? "",
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: att.Extra,
                PreviewStoragePath: att.GetPreviewRemotePath(),
                FullStoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                PreviewLocalPath: null,
                FullLocalPath: null,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                PreviewWidth: att.ThumbWidth,
                PreviewHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static Dictionary<string, object>? BuildAttachmentExtra(HomeAttachmentVm att)
        {
            if (att == null)
                return null;

            if (att.IsLocation)
            {
                return new Dictionary<string, object>
                {
                    ["lat"] = att.Latitude ?? 0,
                    ["lon"] = att.Longitude ?? 0,
                    ["address"] = att.Address ?? ""
                };
            }

            return null;
        }

        public sealed class HomeAttachmentVm : BindableObject
        {
            private bool _isPlaying;
            private bool _isDownloading;
            private bool _isPreviewDownloading;
            private int _downloadCountdownSeconds;

            public string Type { get; set; } = "";
            public string? StoragePath { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }
            private string? _localPath;
            public string? LocalPath
            {
                get => _localPath;
                set
                {
                    if (_localPath == value)
                        return;
                    _localPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                    OnPropertyChanged(nameof(HasPreviewSource));
                    PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            public string? ThumbStoragePath { get; set; }
            public string? LqipBase64 { get; set; }
            public string? PreviewType { get; set; }
            public int? ThumbWidth { get; set; }
            public int? ThumbHeight { get; set; }
            public IReadOnlyList<int>? Waveform { get; set; }

            private string? _thumbLocalPath;
            public string? ThumbLocalPath
            {
                get => _thumbLocalPath;
                set
                {
                    if (_thumbLocalPath == value)
                        return;
                    _thumbLocalPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                    OnPropertyChanged(nameof(HasPreviewSource));
                    PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Address { get; set; }

            public bool IsImage => Type == "image";
            public bool IsAudio => Type == "audio";
            public bool IsFile => Type == "file";
            public bool IsVideo => Type == "video";
            public bool IsPdf => IsFile && (string.Equals(ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(Path.GetExtension(FileName ?? string.Empty), ".pdf", StringComparison.OrdinalIgnoreCase));
            public bool IsFileOrVideo => IsFile && !IsPdf;
            public bool IsLocation => Type == "location";
            public bool IsContact => Type == "contact";
            public bool IsPoll => Type == "poll";
            public bool IsEvent => Type == "event";
            public string AddressLabel => !string.IsNullOrWhiteSpace(Address) ? Address! : $"{Latitude:0.0000}, {Longitude:0.0000}";
            public bool RequiresPreview => HomeAttachmentPreviewRules.RequiresPreview(Type, ContentType, FileName);
            public bool HasPreviewSource => !RequiresPreview || DisplayPreviewSource != null;

            public string? GetPreviewRemotePath() => ThumbStoragePath;

            public event EventHandler? PreviewSourceChanged;

            public bool IsPlaying
            {
                get => _isPlaying;
                set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioPlayLabel)); }
            }

            public string AudioPlayLabel => IsPlaying ? "Stop" : "Play";

            public bool IsDownloading
            {
                get => _isDownloading;
                set
                {
                    if (_isDownloading == value)
                        return;
                    _isDownloading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(DownloadCountdownLabel));
                }
            }

            public bool IsPreviewDownloading
            {
                get => _isPreviewDownloading;
                set
                {
                    if (_isPreviewDownloading == value)
                        return;
                    _isPreviewDownloading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusy));
                }
            }

            public int DownloadCountdownSeconds
            {
                get => _downloadCountdownSeconds;
                set
                {
                    if (_downloadCountdownSeconds == value)
                        return;
                    _downloadCountdownSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DownloadCountdownLabel));
                }
            }

            public string DownloadCountdownLabel => IsDownloading ? $"Download {DownloadCountdownSeconds}s" : "";

            public bool IsBusy => IsDownloading || IsPreviewDownloading;

            public ImageSource? DisplayPreviewSource
            {
                get
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(LocalPath) && File.Exists(LocalPath))
                            return ImageSource.FromFile(LocalPath);

                        if (!string.IsNullOrWhiteSpace(ThumbLocalPath) && File.Exists(ThumbLocalPath))
                            return ImageSource.FromFile(ThumbLocalPath);
                    }
                    catch { }

                    return null;
                }
            }

            public static HomeAttachmentVm FromService(FirestoreHomeFeedService.HomeAttachment att)
            {
                var vm = new HomeAttachmentVm
                {
                    Type = att.Type ?? "",
                    StoragePath = att.StoragePath,
                    DownloadUrl = att.DownloadUrl,
                    FileName = att.FileName ?? (att.Type == "audio" ? "Audio" : att.Type == "video" ? "Video" : "File"),
                    ContentType = att.ContentType,
                    SizeBytes = att.SizeBytes,
                    DurationMs = att.DurationMs,
                    ThumbStoragePath = att.ThumbStoragePath,
                    LqipBase64 = att.LqipBase64,
                    PreviewType = att.PreviewType,
                    ThumbWidth = att.ThumbWidth,
                    ThumbHeight = att.ThumbHeight,
                    Waveform = att.Waveform
                };

                if (att.Extra != null)
                {
                    if (att.Extra.TryGetValue("lat", out var lat))
                        vm.Latitude = ReadDoubleExtra(lat);
                    if (att.Extra.TryGetValue("lon", out var lon))
                        vm.Longitude = ReadDoubleExtra(lon);
                    if (att.Extra.TryGetValue("address", out var addr))
                        vm.Address = ReadStringExtra(addr);
                }

                return vm;
            }

            public static HomeAttachmentVm FromContract(HomeAttachmentContractV2 att)
            {
                var vm = new HomeAttachmentVm
                {
                    Type = att.Type ?? "",
                    StoragePath = att.FullStoragePath,
                    DownloadUrl = att.DownloadUrl,
                    FileName = att.FileName ?? (att.Type == "audio" ? "Audio" : att.Type == "video" ? "Video" : "File"),
                    ContentType = att.ContentType,
                    SizeBytes = att.SizeBytes,
                    DurationMs = att.DurationMs,
                    ThumbStoragePath = att.PreviewStoragePath,
                    LqipBase64 = att.LqipBase64,
                    PreviewType = att.PreviewType,
                    ThumbWidth = att.PreviewWidth,
                    ThumbHeight = att.PreviewHeight,
                    Waveform = att.Waveform,
                    LocalPath = att.FullLocalPath,
                    ThumbLocalPath = att.PreviewLocalPath
                };

                if (att.Extra != null)
                {
                    if (att.Extra.TryGetValue("lat", out var lat) && lat is double latVal)
                        vm.Latitude = latVal;
                    if (att.Extra.TryGetValue("lon", out var lon) && lon is double lonVal)
                        vm.Longitude = lonVal;
                    if (att.Extra.TryGetValue("address", out var addr) && addr is string addrStr)
                        vm.Address = addrStr;
                }

                return vm;
            }

            private static double? ReadDoubleExtra(object? value)
            {
                if (value is double d)
                    return d;
                if (value is float f)
                    return f;
                if (value is long l)
                    return l;
                if (value is int i)
                    return i;
                if (value is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d2))
                    return d2;
                return null;
            }

            private static string? ReadStringExtra(object? value)
            {
                if (value is string s)
                    return s;
                if (value is JsonElement el && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
                return null;
            }
        }

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
#elif WINDOWS
                return new WindowsAudioPlayback();
#else
                return new NoopAudioPlayback();
#endif
            }
        }

        private sealed class NoopAudioPlayback : IAudioPlayback
        {
            public Task PlayAsync(string filePath) => throw new NotSupportedException("Playback audio supportato solo su Android/Windows (per ora).");
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

#if WINDOWS
        private sealed class WindowsAudioPlayback : IAudioPlayback
        {
            private MediaPlayer? _player;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();
                _player = new MediaPlayer();
                _player.Source = WindowsMediaSource.CreateFromUri(new Uri(filePath));
                _player.MediaEnded += (_, __) => StopPlaybackSafe();
                _player.Play();
                return Task.CompletedTask;
            }

            public void StopPlaybackSafe()
            {
                try
                {
                    if (_player != null)
                    {
                        _player.Pause();
                        _player.Dispose();
                        _player = null;
                    }
                }
                catch { }
            }
        }
#endif
    }
}
