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

        private void MarkScrollActivity(int firstVisibleIndex, int lastVisibleIndex)
        {
            _isUserScrolling = true;
            _feedCoordinator.NotifyActivity();
            _lastKnownVisibleIndex = lastVisibleIndex;

            if (_lastFirstVisibleIndex >= 0 && firstVisibleIndex >= 0)
                _lastScrollDirection = firstVisibleIndex > _lastFirstVisibleIndex ? 1 : (firstVisibleIndex < _lastFirstVisibleIndex ? -1 : _lastScrollDirection);
            _lastFirstVisibleIndex = firstVisibleIndex;

            if (firstVisibleIndex >= 0 && lastVisibleIndex >= firstVisibleIndex)
            {
                _pendingPrefetchFirst = firstVisibleIndex;
                _pendingPrefetchLast = lastVisibleIndex;
            }

            QueueIdleDrainViaCoordinator();
        }

        // Unico orchestratore idle: demandiamo al ScrollWorkCoordinator il flush quando lo scroll è fermo.
        private void QueueIdleDrainViaCoordinator()
        {
            if (Interlocked.CompareExchange(ref _idleDrainQueued, 1, 0) != 0)
                return;

            _feedCoordinator.EnqueueUiWork(() =>
            {
                _ = DrainIdleWorkAsync();
                return Task.CompletedTask;
            }, "HOME_IDLE_DRAIN");
        }

        private async Task DrainIdleWorkAsync()
        {
            var shouldRequeue = false;
            var budgetExceeded = false;
            var idlePass = Stopwatch.StartNew();

            try
            {
                const int idlePassBudgetMs = 22;

                _isUserScrolling = false;

                if (_memRefreshDeferredBecauseScrolling)
                {
                    _memRefreshDeferredBecauseScrolling = false;
                    ScheduleMemoryCacheRefresh();
                }

                budgetExceeded = await ExecuteIdleStepWithinBudgetAsync(() => ApplyOlderBufferIfIdleAsync(), idlePass, idlePassBudgetMs);
                if (!budgetExceeded)
                    budgetExceeded = await ExecuteIdleStepWithinBudgetAsync(() => RunPendingPrefetchIfIdleAsync(), idlePass, idlePassBudgetMs);
                if (!budgetExceeded)
                    budgetExceeded = await ExecuteIdleStepWithinBudgetAsync(() => RunDeferredNetworkWorkIfIdleAsync(), idlePass, idlePassBudgetMs);

                shouldRequeue = budgetExceeded || HasPendingIdleWork();
            }
            catch (Exception ex)
            {
                DiagLog.Note("Home.Feed.IdleDrain.Error", ex.GetType().Name);
            }
            finally
            {
                Interlocked.Exchange(ref _idleDrainQueued, 0);
                if (shouldRequeue)
                    QueueIdleDrainViaCoordinator();
            }

            if (budgetExceeded)
                DiagLog.Note("Home.Feed.IdleDrain.Budget", idlePass.ElapsedMilliseconds.ToString());
        }

        private static async Task<bool> ExecuteIdleStepWithinBudgetAsync(Func<Task> step, Stopwatch idlePass, int budgetMs)
        {
            if (idlePass.ElapsedMilliseconds >= budgetMs)
                return true;

            await step();
            return idlePass.ElapsedMilliseconds >= budgetMs;
        }

        private bool HasPendingIdleWork()
        {
            if (_pendingApplyOlderBuffer)
                return true;

            if (_pendingPrefetchFirst >= 0 && _pendingPrefetchLast >= _pendingPrefetchFirst)
                return true;

            if (_pendingInitialNetworkRefresh || _pendingLoadMoreRequest)
                return true;

            return false;
        }

        private async Task RunPendingPrefetchIfIdleAsync()
        {
            if (_isUserScrolling)
                return;

            var first = _pendingPrefetchFirst;
            var last = _pendingPrefetchLast;

            if (first < 0 || last < 0 || last < first)
                return;

            var ct = _prefetchCts?.Token ?? CancellationToken.None;
            await PrefetchHomeMediaAsync(first, last, ct);
        }

        private void RequestLoadMoreIfNearEnd(int firstVisibleIndex)
        {
            try
            {
                if (Posts.Count <= 0)
                    return;

                // Prefetch post vecchi quando l'utente scorre verso il basso e supera ~metà lista in RAM.
                if (_lastScrollDirection > 0)
                {
                    var trigger = (int)(Posts.Count * (PaginaHomeSettings.prefetch_trigger_percent / 100.0));
                    if (firstVisibleIndex >= trigger)
                        _pendingLoadMoreRequest = true;
                }
            }
            catch { }
        }

        private void OnHomeFeedScrolled(object sender, ItemsViewScrolledEventArgs e)
        {
            RecordScrollGapSample();
            if (Posts.Count == 0)
            {
                // Se la lista è vuota, abbiamo comunque bisogno del refresh iniziale rete (ma solo a idle).
                MarkScrollActivity(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);
                return;
            }

            // Non awaitare niente qui: questo handler deve rimanere "costante time".
            MarkScrollActivity(e.FirstVisibleItemIndex, e.LastVisibleItemIndex);

            // Trigger load-more STRATEGICO: set flag soltanto (nessuna rete qui).
            RequestLoadMoreIfNearEnd(e.FirstVisibleItemIndex);
        }

        private bool CanRunBackgroundNetworkNow(out HomeFeedDiagReason reason)
        {
            // Requisito: rete consentita solo a scroll fermo.
            if (_isUserScrolling)
            {
                reason = HomeFeedDiagReason.Scrolling;
                return false;
            }

            // Evita tentativi inutili offline: la UI non deve sembrare “bloccata”.
            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                reason = HomeFeedDiagReason.Offline;
                return false;
            }

            reason = HomeFeedDiagReason.None;
            return true;
        }

        private bool CanRunBackgroundNetworkNow()
            => CanRunBackgroundNetworkNow(out _);

        private async Task RunDeferredNetworkWorkIfIdleAsync()
        {
            if (!CanRunBackgroundNetworkNow(out var reason))
            {
                DiagLog.Note("Home.Feed.Network.Skip", reason.ToDiagValue());
                return;
            }

            // Non accodare multiple esecuzioni: 1 solo worker alla volta.
            if (!await _networkWorkSemaphore.WaitAsync(0))
                return;

            try
            {
                if (!CanRunBackgroundNetworkNow(out reason))
                {
                    DiagLog.Note("Home.Feed.Network.Skip", reason.ToDiagValue());
                    return;
                }

                var ct = _appearanceCts?.Token ?? CancellationToken.None;

                // “Priorità bassa” pratica:
                //  - esegui sempre in background
                //  - non bloccare thread UI
                //  - introduci un piccolo yield/delay prima della rete per lasciare respirare l’UI post-scroll
                await Task.Yield();
                await Task.Delay(40, ct);

                if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                    return;

                // 1) Apertura app: refresh latest page (o load se vuoto).
                if (_pendingInitialNetworkRefresh)
                {
                    _pendingInitialNetworkRefresh = false;
                    await Task.Run(() => LoadOrRefreshLatestHomePostsLowPriorityAsync(ct), ct);
                }

                if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                    return;

                // 2) Trigger a metà lista verso i post vecchi: fetch in background e apply solo da idle.
                if (_pendingLoadMoreRequest)
                {
                    _pendingLoadMoreRequest = false;
                    await Task.Run(() => LoadMoreHomePostsLowPriorityAsync(ct), ct);
                }

                var mergeSw = Stopwatch.StartNew();
                await Task.Run(() => ApplyOlderBufferIfIdleAsync(), ct);
                DiagLog.Note("Home.Feed.MergeIdle.DurationMs", mergeSw.ElapsedMilliseconds.ToString());
            }
            catch
            {
                // best-effort: nessuna eccezione deve degradare lo scroll
            }
            finally
            {
                try { _networkWorkSemaphore.Release(); } catch { }
            }
        }

    }
}
