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
#if WINDOWS
using WindowsMediaSource = Windows.Media.Core.MediaSource;
using Windows.Media.Playback;
#endif

namespace Biliardo.App.Pagine_Home
{
    public partial class Pagina_Home
    {
        private Task SetIsRefreshingHomeAsync(bool value)
        {
            if (MainThread.IsMainThread)
            {
                IsRefreshingHome = value;
                return Task.CompletedTask;
            }

            return MainThread.InvokeOnMainThreadAsync(() => IsRefreshingHome = value);
        }

        private async Task LoadFromCacheAndRenderImmediatelyAsync()
        {
            try
            {
                _currentUid = FirebaseSessionePersistente.GetLocalId() ?? "";
                _likedPostIds = new HashSet<string>(StringComparer.Ordinal);

                IsHomeLoading = true;

                // RAM cache first
                if (HomeFeedMemoryCache.Instance.TryGet(out var memory) && memory != null && memory.Count > 0)
                {
                    _loadedFromMemory = true;

                    var visible = new List<HomePostVm>(memory.Count);

                    foreach (var cachedPost in memory.OrderByDescending(x => x.CreatedAtUtc))
                    {
                        var vm = HomePostVm.FromService(cachedPost);
                        vm.IsLiked = _likedPostIds.Contains(vm.PostId);
                        vm.RetryCommand = RetryHomePostCommand;
                        vm.SyncCommand = null;

                        var contract = BuildContractFromVm(vm);
                        if (HomePostValidatorV2.IsServerReady(contract, out _))
                            visible.Add(vm);
                    }

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        Posts.Clear();
                        foreach (var vm in visible.OrderByDescending(x => x.CreatedAtUtc))
                            Posts.Add(vm);
                    });

                    // Preview ensure: solo se non stiamo scrollando e con token cancellabile
                    foreach (var post in visible)
                        QueueEnsurePreviewAvailable(post);
                }
                else
                {
                    _loadedFromMemory = false;
                }

                // Prefetch iniziale solo se NON scroll e token cancellabile.
                // NOTA: prefetch e preview sono comunque “rete a scroll fermo” (vedi gating in Prefetch/EnsurePreview).
                if (!_isUserScrolling)
                {
                    var end = Math.Min(Posts.Count - 1, 5);
                    if (end >= 0)
                    {
                        _pendingPrefetchFirst = 0;
                        _pendingPrefetchLast = end;
                        _ = RunPendingPrefetchIfIdleAsync();
                    }
                }
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

        private void TryRebuildPagingCursorFromCurrentPosts()
        {
            try
            {
                if (Posts.Count <= 0)
                {
                    _homePagingCursorUtc = null;
                    return;
                }

                // Cursor = timestamp del post più vecchio attualmente caricato.
                var oldest = Posts.Min(x => x.CreatedAtUtc);
                _homePagingCursorUtc = oldest;
            }
            catch
            {
                _homePagingCursorUtc = null;
            }
        }

        private async Task ExecutePullToRefreshAsync()
        {
            if (_isLoadingMore)
            {
                await SetIsRefreshingHomeAsync(false);
                return;
            }

            await SetIsRefreshingHomeAsync(true);
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_appearanceCts?.Token ?? CancellationToken.None, timeoutCts.Token);

                // Refresh latest deve ignorare lo stato no-more delle pagine vecchie.
                await FetchHomePostsPageAsync(linked.Token, isInitial: true, forceLatest: true);
            }
            catch (OperationCanceledException)
            {
                // timeout/cancel: spegne comunque lo spinner refresh.
            }
            finally
            {
                await SetIsRefreshingHomeAsync(false);
            }
        }

        private async Task ApplyOlderBufferIfIdleAsync()
        {
            if (_isUserScrolling || !_pendingApplyOlderBuffer)
                return;

            List<HomePostVm> buffered;
            lock (_olderBufferLock)
            {
                if (_olderBuffer.Count == 0)
                {
                    _pendingApplyOlderBuffer = false;
                    return;
                }

                buffered = new List<HomePostVm>(_olderBuffer);
                _olderBuffer.Clear();
                _pendingApplyOlderBuffer = false;
            }

            const int appendBatchSize = 16;
            var overflow = buffered.Count > appendBatchSize
                ? buffered.Skip(appendBatchSize).ToList()
                : null;

            var batch = buffered
                .Take(appendBatchSize)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var existing = new HashSet<string>(StringComparer.Ordinal);
                for (var i = 0; i < Posts.Count; i++)
                {
                    var id = Posts[i].PostId;
                    if (!string.IsNullOrWhiteSpace(id))
                        existing.Add(id);
                }

                foreach (var vm in batch)
                {
                    if (!existing.Add(vm.PostId))
                        continue;
                    Posts.Add(vm);
                }
            });

            if (overflow != null && overflow.Count > 0)
            {
                lock (_olderBufferLock)
                {
                    _olderBuffer.InsertRange(0, overflow);
                    _pendingApplyOlderBuffer = _olderBuffer.Count > 0;
                }
            }

            if (!PaginaHomeSettings.post_illimitati)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    while (Posts.Count > PaginaHomeSettings.max_post_in_ram)
                        Posts.RemoveAt(Posts.Count - 1);
                });
            }

            ScheduleMemoryCacheRefresh();
        }

        private async Task LoadOrRefreshLatestHomePostsLowPriorityAsync(CancellationToken ct)
        {
            // Requisito: rete solo a scroll idle
            if (!CanRunBackgroundNetworkNow())
                return;

            // Se è la prima volta in questa apertura, usiamo la pagina “latest” (cursor null) per:
            //  - riempire se vuoto
            //  - aggiungere post nuovi sopra (merge) se già presenti
            await FetchHomePostsPageAsync(ct, isInitial: true, forceLatest: true);

            // Dopo fetch, ricostruisci cursor oldest per load-more.
            TryRebuildPagingCursorFromCurrentPosts();
        }

        private async Task LoadMoreHomePostsLowPriorityAsync(CancellationToken ct)
        {
            if (!CanRunBackgroundNetworkNow())
                return;

            await FetchHomePostsPageAsync(ct, isInitial: false, forceLatest: false);

            // aggiorna cursor in caso di merge
            TryRebuildPagingCursorFromCurrentPosts();
        }

        private async Task FetchHomePostsPageAsync(CancellationToken ct, bool isInitial, bool forceLatest)
        {
            // Durante scroll: nessun lavoro di rete/merge.
            if (_isUserScrolling)
                return;

            if (_isLoadingMore || (!forceLatest && _noMoreHomePosts))
                return;

            // Evita condizioni dove un refresh iniziale non avviene mai:
            // - se posts vuoto, “initial” deve poter partire.
            // - _initialPageLoaded qui è solo telemetria/logica interna; non deve bloccare scenari validi.
            if (isInitial && _initialPageLoaded == false)
                _initialPageLoaded = true;

            _isLoadingMore = true;
            try
            {
                // forceLatest: cursor null => pagina più recente.
                // load-more: cursor = oldest => pagina più vecchia.
                if (forceLatest)
                    _noMoreHomePosts = false;

                DateTimeOffset? cursor = forceLatest ? null : _homePagingCursorUtc;

                // Re-check rete idle prima della chiamata.
                if (!CanRunBackgroundNetworkNow())
                    return;

                var page = await _homeFeed.GetHomePostsPageAsync(cursor, PaginaHomeSettings.page_size, ct);
                if (page == null || page.Count == 0)
                {
                    // Solo per load-more: se cursor non null e page empty => fine pagine
                    if (!forceLatest)
                        _noMoreHomePosts = true;
                    return;
                }

                // Solo per load-more “vecchio”: aggiorna flag fine pagine.
                if (!forceLatest)
                {
                    _homePagingCursorUtc = page[^1].CreatedAtUtc;
                    if (page.Count < PaginaHomeSettings.page_size)
                        _noMoreHomePosts = true;
                }

                HashSet<string> existingIds = new(StringComparer.Ordinal);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var post in Posts)
                    {
                        if (!string.IsNullOrWhiteSpace(post.PostId))
                            existingIds.Add(post.PostId);
                    }
                });

                var newItems = page.Where(p => !existingIds.Contains(p.PostId)).ToList();
                if (newItems.Count == 0)
                {
                    // In refresh latest, è normale che non ci siano novità.
                    return;
                }

                var vms = newItems.Select(HomePostVm.FromService).ToList();
                foreach (var vm in vms)
                {
                    vm.IsLiked = _likedPostIds.Contains(vm.PostId);
                    vm.RetryCommand = RetryHomePostCommand;
                    vm.SyncCommand = null;
                }

                if (forceLatest)
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        foreach (var vm in vms.OrderByDescending(x => x.CreatedAtUtc))
                            Posts.Insert(0, vm);
                    });
                    ScheduleMemoryCacheRefresh();
                }
                else
                {
                    lock (_olderBufferLock)
                    {
                        _olderBuffer.AddRange(vms);
                        _pendingApplyOlderBuffer = _olderBuffer.Count > 0;
                    }
                }

                int currentCount = 0;
                await MainThread.InvokeOnMainThreadAsync(() => currentCount = Posts.Count);

                if (CanRunBackgroundNetworkNow())
                {
                    if (isInitial)
                    {
                        _pendingPrefetchFirst = 0;
                        _pendingPrefetchLast = Math.Min(currentCount - 1, 5);
                        _ = RunPendingPrefetchIfIdleAsync();
                    }
                    else if (currentCount > 0)
                    {
                        var start = Math.Max(0, currentCount - 1);
                        _pendingPrefetchFirst = start;
                        _pendingPrefetchLast = currentCount - 1;
                        _ = RunPendingPrefetchIfIdleAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // Popup: non deve bloccare lo scroll. Qui siamo in worker (idle), ok.
                await ShowServerErrorPopupAsync("Errore download Home", ex);
            }
            finally
            {
                _isLoadingMore = false;
            }
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

            // Aggiorna RAM feed (debounced, non durante scroll)
            ScheduleMemoryCacheRefresh();

            foreach (var post in visible)
                QueueEnsurePreviewAvailable(post);

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
                if (HomePostValidatorV2.IsServerReady(contract, out _))
                    visible.Add(vm);
            }
        }

        private async Task ApplyHomeSnapshotFromServiceAsync(
            IReadOnlyList<FirestoreHomeFeedService.HomePostItem> items,
            CancellationToken ct)
        {
            // Durante scroll: niente merge
            if (_isUserScrolling)
                return;

            if (items == null)
                return;

            var visible = new List<HomePostVm>();

            foreach (var post in items)
            {
                if (ct.IsCancellationRequested)
                    return;

                var vm = HomePostVm.FromService(post);
                vm.IsLiked = _likedPostIds.Contains(vm.PostId);
                vm.RetryCommand = RetryHomePostCommand;
                vm.SyncCommand = null;

                var contract = BuildContractFromVm(vm);
                if (HomePostValidatorV2.IsServerReady(contract, out _))
                    visible.Add(vm);
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var snapshotIds = new HashSet<string>(visible.Select(x => x.PostId), StringComparer.Ordinal);
                var oldestSnapshotUtc = visible.Count > 0 ? visible.Min(x => x.CreatedAtUtc) : (DateTimeOffset?)null;

                if (oldestSnapshotUtc.HasValue)
                {
                    for (var i = Posts.Count - 1; i >= 0; i--)
                    {
                        var existing = Posts[i];
                        if (existing == null)
                            continue;
                        if (existing.CreatedAtUtc >= oldestSnapshotUtc.Value && !snapshotIds.Contains(existing.PostId))
                            Posts.RemoveAt(i);
                    }
                }

                foreach (var vm in visible.OrderByDescending(x => x.CreatedAtUtc))
                    InsertSortedByCreatedAtDesc(Posts, vm);
            });

            foreach (var post in visible)
                QueueEnsurePreviewAvailable(post);

            ScheduleMemoryCacheRefresh();
        }

        private void ScheduleMemoryCacheRefresh()
        {
            // Durante scroll: differisci. Non devi MAI introdurre jank.
            if (_isUserScrolling)
            {
                _memRefreshDeferredBecauseScrolling = true;
                return;
            }

            lock (_memRefreshLock)
            {
                CancelAndDispose(ref _memRefreshCts);
                _memRefreshCts = new CancellationTokenSource();
                var token = _memRefreshCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250, token);
                        if (token.IsCancellationRequested) return;

                        // Snapshot sul thread UI (sicuro), poi set su cache RAM.
                        List<FirestoreHomeFeedService.HomePostItem> snapshot = new();
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            var limit = Math.Max(50, PaginaHomeSettings.memory_cache_snapshot_limit);
                            snapshot = Posts.Take(limit).Select(ToHomePostItem).ToList();
                        });

                        HomeFeedMemoryCache.Instance.Set(snapshot);
                    }
                    catch { }
                }, token);
            }
        }

    }
}
