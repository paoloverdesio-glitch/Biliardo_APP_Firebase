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
        protected override void OnAppearing()
        {
            base.OnAppearing();
            _ = OnAppearingAsync();
        }

        private async Task OnAppearingAsync()
        {
            _appearanceCts?.Cancel();
            _appearanceCts?.Dispose();
            _appearanceCts = new CancellationTokenSource();
            var ct = _appearanceCts.Token;

            ApplyHomeFeedScrollTuning();
            ResetPrefetchTokens();
            _homeFeedJankMonitor.Start();

            // Reset stato di paging ad ogni apertura (evita condizioni “pagina non carica mai”).
            _initialPageLoaded = false;
            _noMoreHomePosts = false;
            _isLoadingMore = false;
            _pendingInitialNetworkRefresh = false;
            _pendingLoadMoreRequest = false;
            _lastKnownVisibleIndex = -1;
            ResetFeedMetricsSession();

            // 1) Render immediato da RAM (se presente), senza bloccare UI.
            await LoadFromCacheAndRenderImmediatelyAsync();

            // Calcola cursor “oldest” dai dati già in pagina (serve per load-more).
            TryRebuildPagingCursorFromCurrentPosts();

            // 2) Aspetta il primo render, ma senza introdurre lavoro pesante sul thread UI.
            if (_firstRenderGate != null)
                await _firstRenderGate.WaitAsync();

            if (ct.IsCancellationRequested)
                return;

            // 3) Verifica sessione (operazione leggera): se manca, torna a login.
            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            // 4) Strategia rete (REQUISITO):
            //    - all’apertura app: pianifica un refresh rete (low priority) ESEGUIBILE solo a scroll idle.
            //    - se Posts è vuoto, questo refresh è indispensabile per non restare “pagina vuota”.
            _pendingInitialNetworkRefresh = true;

            // Se già idle, avvia subito il lavoro in background (non blocca UI).
            _ = RunDeferredNetworkWorkIfIdleAsync();
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

            CancelAndDispose(ref _prefetchCts);
            CancelAndDispose(ref _previewEnsureCts);
            CancelAndDispose(ref _memRefreshCts);
            _homeFeedJankMonitor.Stop();
            FlushFeedMetricsSession();

            _appearanceCts?.Cancel();
            _appearanceCts?.Dispose();
            _appearanceCts = null;
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

        private void ResetPrefetchTokens()
        {
            CancelAndDispose(ref _prefetchCts);
            CancelAndDispose(ref _previewEnsureCts);

            _prefetchCts = new CancellationTokenSource();
            _previewEnsureCts = new CancellationTokenSource();
        }

        private static void CancelAndDispose(ref CancellationTokenSource? cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
            cts = null;
        }

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

                        // Durante scroll, non fare nulla di costoso.
                        if (_isUserScrolling)
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

        private void StartRealtimeUpdatesAfterFirstRender(CancellationToken ct)
        {
            // Realtime è rete: NON attivarlo automaticamente per non introdurre traffico continuo.
            // Se in futuro servisse, va gating-ato su idle e scenario esplicito.
            return;
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

    }
}
