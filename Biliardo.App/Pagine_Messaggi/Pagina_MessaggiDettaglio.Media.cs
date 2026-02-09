using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Media;
#endif

#if WINDOWS
using WindowsMediaSource = Windows.Media.Core.MediaSource;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices.WindowsRuntime;
#endif

using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Pagine_Media;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Infrastructure;

// ✅ FIX: MediaSource corretto (CommunityToolkit)
using MauiMediaSource = CommunityToolkit.Maui.Views.MediaSource;


using Path = System.IO.Path;
using MauiImage = Microsoft.Maui.Controls.Image;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) INIT PLAYBACK (chiamato dal costruttore nel File 1)
        // ============================================================
        private partial void InitPlaybackSubsystem()
        {
            _playback = AudioPlaybackFactory.Create();
        }

        private partial void StopPlaybackSafe()
        {
            try { _playback?.StopPlaybackSafe(); } catch { }
            StopPlaybackWave();
        }

        // ============================================================
        // 2) TAP: FOTO FULLSCREEN
        // ============================================================
        private async void OnPhotoTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not ChatMessageVm m)
                    return;

                var path = await EnsureMediaDownloadedAsync(m, showErrors: true);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                var img = new MauiImage
                {
                    Source = path,
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

                // Modal: non fermare aggiornamenti realtime in OnDisappearing
                _suppressStopRealtimeOnce = true;
                await Navigation.PushModalAsync(page);

                try
                {
                    await Task.WhenAll(
                        img.FadeTo(1, 160, Easing.CubicOut),
                        img.ScaleTo(1, 160, Easing.CubicOut));
                }
                catch { }
            }
            catch { }
        }

        // ============================================================
        // 3) TAP: OPEN MEDIA (file/video) - usato da Border TapGesture
        // ============================================================
        private async void OnOpenMediaTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (sender is not BindableObject bo || bo.BindingContext is not ChatMessageVm m)
                    return;

                await OpenMediaAsync(m);
            }
            catch { }
        }

        // Se in futuro vuoi un Button Clicked, questo resta valido
        private async void OnOpenMediaClicked(object sender, EventArgs e)
        {
            try
            {
                if (sender is not Button b || b.CommandParameter is not ChatMessageVm m)
                    return;

                await OpenMediaAsync(m);
            }
            catch (Exception ex)
            {
                await ShowServerErrorPopupAsync("Errore media", ex);
            }
        }

        private async Task OpenMediaAsync(ChatMessageVm m)
        {
            if (m.IsVideo)
            {
                await OpenVideoAsync(m);
                return;
            }

            if (m.IsPdf)
            {
                await OnOpenPdfAsync(m);
                return;
            }

            if (!m.IsPhoto && !m.IsFileNonPdf)
                return;

            var path = await EnsureMediaDownloadedAsync(m, showErrors: true);
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

        private async Task OnOpenPdfAsync(ChatMessageVm m)
        {
            if (m == null)
                return;

            var path = await EnsureMediaDownloadedAsync(m, showErrors: true);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                await DisplayAlert("Info", "PDF non disponibile.", "OK");
                return;
            }

            var fileName = m.FileName ?? "document.pdf";
            if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.pdf";

            await Navigation.PushAsync(new PdfViewerPage(path, fileName));
        }

        private async Task OpenVideoAsync(ChatMessageVm m)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.StoragePath))
                return;

            if (!string.IsNullOrWhiteSpace(m.MediaLocalPath) && File.Exists(m.MediaLocalPath))
            {
                await Navigation.PushAsync(new VideoPlayerPage(m.MediaLocalPath, m.DisplayPreviewSource));

                return;
            }

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var url = FirebaseStorageRestClient.BuildAuthDownloadUrl(FirebaseStorageRestClient.DefaultStorageBucket, m.StoragePath);
            await Navigation.PushAsync(new VideoPlayerPage(url, m.DisplayPreviewSource));


            _ = Task.Run(async () =>
            {
                try
                {
                    MainThread.BeginInvokeOnMainThread(() => m.IsDownloading = true);
                    var local = await _mediaCache.GetOrDownloadAsync(idToken!, m.StoragePath!, m.FileName ?? "video.mp4", isThumb: false, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(local))
                    {
                        MainThread.BeginInvokeOnMainThread(() => m.MediaLocalPath = local);
                    }
                }
                catch { }
                finally
                {
                    MainThread.BeginInvokeOnMainThread(() => m.IsDownloading = false);
                }
            });
        }

        // ============================================================
        // 4) LOCATION: open maps
        // ============================================================
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
                await ShowServerErrorPopupAsync("Errore media", ex);
            }
        }

        // ============================================================
        // 5) CONTATTO: chiama
        // ============================================================
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
                await ShowServerErrorPopupAsync("Errore media", ex);
            }
        }

        // ============================================================
        // 6) AUDIO: play/stop
        // ============================================================
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
                    StopPlaybackSafe();
                    m.IsAudioPlaying = false;
                    return;
                }

                // stop altri audio in riproduzione
                foreach (var x in Messaggi.Where(x => x.IsAudioPlaying))
                    x.IsAudioPlaying = false;

                var path = await EnsureMediaDownloadedAsync(m, showErrors: true);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    await DisplayAlert("Info", "Audio non disponibile.", "OK");
                    return;
                }

                m.IsAudioPlaying = true;
                StartPlaybackWave(m);
                await _playback.PlayAsync(path);
                m.IsAudioPlaying = false;
                StopPlaybackWave();
            }
            catch (Exception ex)
            {
                await ShowServerErrorPopupAsync("Errore media", ex);
            }
        }

        private void OnAudioWaveLoaded(object sender, EventArgs e)
        {
            if (sender is not GraphicsView view || view.BindingContext is not ChatMessageVm m)
                return;

            view.Drawable = m.PlaybackWave;
            _audioWaveViews[m] = view;
            view.Invalidate();
        }

        private void OnAudioWaveUnloaded(object sender, EventArgs e)
        {
            if (sender is not GraphicsView view || view.BindingContext is not ChatMessageVm m)
                return;

            if (_audioWaveViews.ContainsKey(m))
                _audioWaveViews.Remove(m);
        }

        private void StartPlaybackWave(ChatMessageVm m)
        {
            StopPlaybackWave();

            _audioWaveCurrent = m;
            _audioWavePhase = 0;
            _audioWaveIndex = 0;
            m.PlaybackWave.Reset();

            var waveform = m.AudioWaveform?.ToArray();

            _audioWaveTimer = Dispatcher.CreateTimer();
            _audioWaveTimer.Interval = TimeSpan.FromMilliseconds(80);
            _audioWaveTimer.Tick += (_, __) =>
            {
                if (_audioWaveCurrent == null)
                    return;

                float combined;
                if (waveform != null && waveform.Length > 0)
                {
                    var idx = _audioWaveIndex++ % waveform.Length;
                    combined = Math.Clamp(waveform[idx] / 100f, 0f, 1f);
                }
                else
                {
                    var level = Math.Abs(Math.Sin(_audioWavePhase));
                    var harmonic = Math.Abs(Math.Sin(_audioWavePhase * 0.37));
                    var fallback = 0.15 + (level * 0.85 * (0.6 + harmonic * 0.4));
                    combined = (float)Math.Min(1, fallback);
                    _audioWavePhase += 0.35;
                }

                _audioWaveCurrent.PlaybackWave.AddSample(combined);

                if (_audioWaveViews.TryGetValue(_audioWaveCurrent, out var view))
                    view.Invalidate();
            };
            _audioWaveTimer.Start();
        }

        private void StopPlaybackWave()
        {
            if (_audioWaveTimer != null)
            {
                _audioWaveTimer.Stop();
                _audioWaveTimer = null;
            }

            if (_audioWaveCurrent != null)
            {
                _audioWaveCurrent.PlaybackWave.Reset();
                if (_audioWaveViews.TryGetValue(_audioWaveCurrent, out var view))
                    view.Invalidate();
            }

            _audioWaveCurrent = null;
        }

        // ============================================================
        // 7) DOWNLOAD MEDIA (cache su FileSystem.CacheDirectory)
        // ============================================================
        private async Task<string?> EnsureMediaDownloadedAsync(ChatMessageVm m, bool showErrors)
        {
            if (m == null) return null;

            try
            {
                // 7.1) già in cache
                if (!string.IsNullOrWhiteSpace(m.MediaLocalPath) && File.Exists(m.MediaLocalPath))
                    return m.MediaLocalPath;

                // 7.2) serve storagePath
                if (string.IsNullOrWhiteSpace(m.StoragePath))
                    return null;

                var cached = await _mediaCache.TryGetCachedPathAsync(m.StoragePath, isThumb: false);
                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                {
                    MainThread.BeginInvokeOnMainThread(() => m.MediaLocalPath = cached);
                    return cached;
                }

                if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
                {
                    if (showErrors)
                        await DisplayAlert("Offline", "Contenuto non disponibile offline.", "OK");
                    return null;
                }

                // 7.3) token
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return null;

                MainThread.BeginInvokeOnMainThread(() => m.IsDownloading = true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(AppCacheOptions.MediaDownloadTimeoutSeconds));
                var local = await _mediaCache.GetOrDownloadAsync(idToken!, m.StoragePath!, m.FileName ?? "file.bin", isThumb: false, cts.Token);
                if (string.IsNullOrWhiteSpace(local))
                {
                    if (showErrors)
                        await ShowServerErrorPopupAsync("Errore download", new InvalidOperationException("Impossibile scaricare il file."));
                    return null;
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    m.MediaLocalPath = local;
                });

                if (AppMediaOptions.GenerateThumbIfMissingAfterDownload && string.IsNullOrWhiteSpace(m.ThumbLocalPath))
                    _ = GenerateLocalPreviewFallbackAsync(m, local, CancellationToken.None);

                return local;
            }
            catch (OperationCanceledException)
            {
                if (showErrors)
                    await ShowServerErrorPopupAsync("Errore download", new TimeoutException("Timeout download contenuto."));
                return null;
            }
            catch
            {
                if (showErrors)
                    await ShowServerErrorPopupAsync("Errore apertura", new InvalidOperationException("Impossibile aprire il contenuto."));
                return null;
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(() => m.IsDownloading = false);
            }
        }

        // ============================================================
        // 8) PREFETCH MEDIA (richiamato dallo scroll - File 4)
        // ============================================================
        private void CancelPrefetch()
        {
            try { _prefetchCts?.Cancel(); } catch { }
            try { _prefetchCts?.Dispose(); } catch { }
            _prefetchCts = null;
        }

        private partial Task SchedulePrefetchMediaAsync(int firstIndex, int lastIndex)
            => SchedulePrefetchMediaCoreAsync(firstIndex, lastIndex);

        private async Task SchedulePrefetchMediaCoreAsync(int firstIndex, int lastIndex)
        {
            if (firstIndex < 0 || lastIndex < 0 || firstIndex > lastIndex)
                return;

            CancelPrefetch();

            _prefetchCts = new CancellationTokenSource();
            var token = _prefetchCts.Token;

            var targets = new List<ChatMessageVm>();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (Messaggi.Count == 0) return;

                var last = Math.Min(lastIndex, Messaggi.Count - 1);
                for (int i = firstIndex; i <= last; i++)
                {
                    var vm = Messaggi[i];
                    if (vm == null || vm.IsDateSeparator) continue;
                    if (string.IsNullOrWhiteSpace(vm.ThumbStoragePath)) continue;
                    if (string.IsNullOrWhiteSpace(vm.Id)) continue;

                    lock (_prefetchMediaMessageIds)
                    {
                        if (_prefetchMediaMessageIds.Contains(vm.Id))
                            continue;

                        _prefetchMediaMessageIds.Add(vm.Id);
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

                    using var sem = new SemaphoreSlim(AppMediaOptions.DownloadConcurrency, AppMediaOptions.DownloadConcurrency);
                    var tasks = new List<Task>();

                    foreach (var vm in targets)
                    {
                        if (token.IsCancellationRequested) break;

                        tasks.Add(Task.Run(async () =>
                        {
                            await sem.WaitAsync(token);
                            try
                            {
                                if (token.IsCancellationRequested)
                                    return;

                                if (!AppMediaOptions.PrefetchThumbsOnScroll)
                                    return;

                                if (string.IsNullOrWhiteSpace(vm.ThumbStoragePath))
                                    return;

                                if (!string.IsNullOrWhiteSpace(vm.ThumbLocalPath) && File.Exists(vm.ThumbLocalPath))
                                    return;

                                var local = await _mediaCache.GetOrDownloadAsync(idToken!, vm.ThumbStoragePath!, vm.FileName ?? "thumb.jpg", isThumb: true, token);
                                if (string.IsNullOrWhiteSpace(local))
                                    return;

                                MainThread.BeginInvokeOnMainThread(() => vm.ThumbLocalPath = local);
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

        private async Task GenerateLocalPreviewFallbackAsync(ChatMessageVm m, string localPath, CancellationToken ct)
        {
            try
            {
                if (m == null || string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                    return;

                var kind = m.IsPhoto ? MediaKind.Image :
                    m.IsVideo ? MediaKind.Video :
                    (string.Equals(m.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(m.FileName ?? ""), ".pdf", StringComparison.OrdinalIgnoreCase))
                        ? MediaKind.Pdf
                        : MediaKind.File;

                if (kind is not (MediaKind.Image or MediaKind.Video or MediaKind.Pdf))
                    return;

                var preview = await _previewGenerator.GenerateAsync(
                    new MediaPreviewRequest(localPath, kind, m.ContentType, m.FileName, "chat_download", m.Latitude, m.Longitude),
                    ct);

                if (preview == null)
                    return;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (string.IsNullOrWhiteSpace(m.ThumbLocalPath))
                        m.ThumbLocalPath = preview.ThumbLocalPath;

                    if (AppMediaOptions.GenerateLqipIfMissingAfterDownload && string.IsNullOrWhiteSpace(m.LqipBase64))
                        m.LqipBase64 = preview.LqipBase64;
                });
            }
            catch
            {
                // ignore
            }
        }

        // ============================================================
        // 9) PLAYBACK: interfaccia + factory + implementazioni
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
#elif WINDOWS
                return new WindowsAudioPlayback();
#else
                return new NoopAudioPlayback();
#endif
            }
        }

        private sealed class NoopAudioPlayback : IAudioPlayback
        {
            public Task PlayAsync(string filePath)
                => throw new NotSupportedException("Playback audio supportato solo su Android/Windows (per ora).");

            public void StopPlaybackSafe() { }
        }

#if ANDROID
        private sealed class AndroidAudioPlayback : IAudioPlayback
        {
            private Android.Media.MediaPlayer? _player;
            private TaskCompletionSource<bool>? _playTcs;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();

                _playTcs = new TaskCompletionSource<bool>();

                _player = new Android.Media.MediaPlayer();
                _player.SetDataSource(filePath);
                _player.Prepare();
                _player.Start();

                _player.Completion += (_, __) => StopPlaybackSafe();
                return _playTcs.Task;
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

                    _playTcs?.TrySetResult(true);
                    _playTcs = null;
                }
                catch { }
            }
        }
#endif

#if WINDOWS
        private sealed class WindowsAudioPlayback : IAudioPlayback
        {
            private MediaPlayer? _player;
            private TaskCompletionSource<bool>? _playTcs;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();
                _playTcs = new TaskCompletionSource<bool>();
                _player = new MediaPlayer();
                _player.Source = WindowsMediaSource.CreateFromUri(new Uri(filePath));
                _player.MediaEnded += (_, __) => StopPlaybackSafe();
                _player.Play();
                return _playTcs.Task;
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

                    _playTcs?.TrySetResult(true);
                    _playTcs = null;
                }
                catch { }
            }
        }
#endif
    }
}
