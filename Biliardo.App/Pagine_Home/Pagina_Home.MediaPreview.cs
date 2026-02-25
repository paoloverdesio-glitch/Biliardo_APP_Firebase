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
        private Task PrefetchHomeMediaAsync(int firstIndex, int lastIndex, CancellationToken ct)
        {
            if (_isUserScrolling)
                return Task.CompletedTask;

            if (firstIndex < 0 || lastIndex < 0 || Posts.Count == 0 || lastIndex < firstIndex)
                return Task.CompletedTask;

            // Prefetch è rete “background”: consentita SOLO se idle e online.
            if (!CanRunBackgroundNetworkNow())
                return Task.CompletedTask;

            return Task.Run(async () =>
            {
                try
                {
                    if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                        return;

                    var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
                    if (string.IsNullOrWhiteSpace(idToken))
                        return;

                    if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                        return;

                    // Snapshot rapido dal thread UI, poi download in background.
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
                    var tasks = new List<Task>(targets.Count);

                    foreach (var att in targets)
                    {
                        if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                            break;

                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync(ct);
                            try
                            {
                                if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
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

                                var thumbLocal = await _mediaCache.GetOrDownloadAsync(
                                    idToken!,
                                    previewRemotePath!,
                                    att.FileName ?? "thumb.jpg",
                                    isThumb: true,
                                    ct);

                                if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                                    return;

                                if (string.IsNullOrWhiteSpace(thumbLocal) || !File.Exists(thumbLocal))
                                    return;

                                MainThread.BeginInvokeOnMainThread(() => att.ThumbLocalPath = thumbLocal);
                            }
                            catch { }
                            finally
                            {
                                var previewKey = att.GetPreviewRemotePath();
                                if (!string.IsNullOrWhiteSpace(previewKey))
                                {
                                    lock (_prefetchMediaKeys)
                                        _prefetchMediaKeys.Remove(previewKey);
                                }

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

        private void QueueEnsurePreviewAvailable(HomePostVm vm)
        {
            if (vm == null || string.IsNullOrWhiteSpace(vm.PostId))
                return;

            lock (_pendingPreviewEnsures)
            {
                if (!_pendingPreviewEnsures.Add(vm.PostId))
                    return;
            }

            var ct = _previewEnsureCts?.Token ?? CancellationToken.None;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Aspetta che lo scroll sia idle: priorità assoluta allo scroll
                    while (_isUserScrolling && !ct.IsCancellationRequested)
                        await Task.Delay(80, ct);

                    if (ct.IsCancellationRequested || _isUserScrolling)
                        return;

                    // Preview ensure è rete background: solo se idle e online
                    if (!CanRunBackgroundNetworkNow())
                        return;

                    await EnsurePreviewAvailableAsync(vm, ct);
                }
                catch { }
                finally
                {
                    lock (_pendingPreviewEnsures)
                    {
                        _pendingPreviewEnsures.Remove(vm.PostId);
                    }
                }
            }, ct);
        }

        private async Task EnsurePreviewAvailableAsync(HomePostVm post, CancellationToken ct)
        {
            if (post == null)
                return;

            if (_isUserScrolling || ct.IsCancellationRequested)
                return;

            var contract = BuildContractFromVm(post);
            if (!HomePostValidatorV2.IsServerReady(contract, out _))
                return;

            var requiresAnyPreview = post.Attachments.Any(att => att.RequiresPreview);
            if (!requiresAnyPreview)
                return;

            // Preview è rete background: solo se idle e online
            if (!CanRunBackgroundNetworkNow())
                return;

            foreach (var att in post.Attachments)
            {
                if (_isUserScrolling || ct.IsCancellationRequested)
                    return;

                if (!att.RequiresPreview)
                    continue;

                var previewRemotePath = att.GetPreviewRemotePath();
                if (string.IsNullOrWhiteSpace(previewRemotePath))
                    return;

                if (!string.IsNullOrWhiteSpace(att.ThumbLocalPath) && File.Exists(att.ThumbLocalPath))
                    continue;

                var cached = await _mediaCache.TryGetCachedPathAsync(previewRemotePath, isThumb: true);
                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                {
                    await MainThread.InvokeOnMainThreadAsync(() => att.ThumbLocalPath = cached);
                    continue;
                }

                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                    return;

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
                if (string.IsNullOrWhiteSpace(idToken))
                    return;

                if (!CanRunBackgroundNetworkNow() || ct.IsCancellationRequested)
                    return;

                var thumbLocal = await _mediaCache.GetOrDownloadAsync(
                    idToken!,
                    previewRemotePath,
                    att.FileName ?? "thumb.jpg",
                    isThumb: true,
                    ct);

                if (string.IsNullOrWhiteSpace(thumbLocal) || !File.Exists(thumbLocal))
                    return;

                await MainThread.InvokeOnMainThreadAsync(() => att.ThumbLocalPath = thumbLocal);
            }
        }

    }
}
