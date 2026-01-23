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
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Graphics;
using Android.Media;
#endif

#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using System.Runtime.InteropServices.WindowsRuntime;
#endif

using Biliardo.App.Servizi_Firebase;

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

                var path = await EnsureMediaDownloadedAsync(m);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return;

                var img = new MauiImage { Source = path, Aspect = Aspect.AspectFit };

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

                // Modal: non fermare polling in OnDisappearing
                _suppressStopPollingOnce = true;
                await Navigation.PushModalAsync(page);
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
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async Task OpenMediaAsync(ChatMessageVm m)
        {
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
                await DisplayAlert("Errore", ex.Message, "OK");
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
                await DisplayAlert("Errore", ex.Message, "OK");
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

                var path = await EnsureMediaDownloadedAsync(m);
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
                await DisplayAlert("Errore", ex.Message, "OK");
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
            m.PlaybackWave.Reset();

            _audioWaveTimer = Dispatcher.CreateTimer();
            _audioWaveTimer.Interval = TimeSpan.FromMilliseconds(80);
            _audioWaveTimer.Tick += (_, __) =>
            {
                if (_audioWaveCurrent == null)
                    return;

                var level = Math.Abs(Math.Sin(_audioWavePhase));
                var harmonic = Math.Abs(Math.Sin(_audioWavePhase * 0.37));
                var combined = 0.15 + (level * 0.85 * (0.6 + harmonic * 0.4));
                if (combined > 1) combined = 1;

                _audioWaveCurrent.PlaybackWave.AddSample((float)combined);
                _audioWavePhase += 0.35;

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
        private async Task<string?> EnsureMediaDownloadedAsync(ChatMessageVm m)
        {
            if (m == null) return null;

            try
            {
                // 7.1) già in cache
                if (!string.IsNullOrWhiteSpace(m.MediaLocalPath) && File.Exists(m.MediaLocalPath))
                {
                    if (m.IsVideo && !m.HasVideoThumbnail)
                        _ = EnsureVideoThumbnailAsync(m, CancellationToken.None);

                    return m.MediaLocalPath;
                }

                // 7.2) serve storagePath
                if (string.IsNullOrWhiteSpace(m.StoragePath))
                    return null;

                // 7.3) token
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return null;

                // 7.4) estensione coerente
                var ext = Path.GetExtension(m.FileName ?? "");
                if (string.IsNullOrWhiteSpace(ext))
                    ext = m.IsPhoto ? ".jpg" : (m.IsAudio ? ".m4a" : (m.IsVideo ? ".mp4" : ".bin"));

                var local = Path.Combine(FileSystem.CacheDirectory, $"dl_{m.Id}_{Guid.NewGuid():N}{ext}");

                await FirebaseStorageRestClient.DownloadToFileAsync(idToken!, m.StoragePath!, local);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    m.MediaLocalPath = local;
                });

                // 7.5) per video: genera thumbnail in background
                if (m.IsVideo && !m.HasVideoThumbnail)
                    _ = EnsureVideoThumbnailAsync(m, CancellationToken.None);

                return local;
            }
            catch
            {
                return null;
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
                    if (!vm.IsPhoto && !vm.IsVideo) continue;
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
                                if (token.IsCancellationRequested)
                                    return;

                                // già scaricato
                                if (!string.IsNullOrWhiteSpace(vm.MediaLocalPath) && File.Exists(vm.MediaLocalPath))
                                {
                                    if (vm.IsVideo && !vm.HasVideoThumbnail)
                                        await EnsureVideoThumbnailAsync(vm, token);
                                    return;
                                }

                                if (string.IsNullOrWhiteSpace(vm.StoragePath))
                                    return;

                                var ext = Path.GetExtension(vm.FileName ?? "");
                                if (string.IsNullOrWhiteSpace(ext))
                                    ext = vm.IsPhoto ? ".jpg" : ".mp4";

                                var local = Path.Combine(FileSystem.CacheDirectory, $"dl_{vm.Id}_{Guid.NewGuid():N}{ext}");

                                await FirebaseStorageRestClient.DownloadToFileAsync(idToken!, vm.StoragePath!, local);

                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    vm.MediaLocalPath = local;
                                });

                                if (vm.IsVideo && !vm.HasVideoThumbnail)
                                    await EnsureVideoThumbnailAsync(vm, token);
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
        // 9) THUMBNAIL VIDEO
        // ============================================================
        private async Task EnsureVideoThumbnailAsync(ChatMessageVm m, CancellationToken ct)
        {
            if (m == null || !m.IsVideo) return;
            if (m.HasVideoThumbnail) return;

            var videoPath = m.MediaLocalPath;
            if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
                return;

            try
            {
                var thumbPath = Path.Combine(FileSystem.CacheDirectory, $"thumb_{m.Id}_{Guid.NewGuid():N}.jpg");

#if ANDROID
                var ok = await Task.Run(() => TryCreateVideoThumbnailAndroid(videoPath!, thumbPath), ct);
                if (!ok) return;
#elif WINDOWS
                var ok = await TryCreateVideoThumbnailWindowsAsync(videoPath!, thumbPath);
                if (!ok) return;
#else
                return;
#endif
                if (!File.Exists(thumbPath))
                    return;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    m.VideoThumbnailPath = thumbPath;
                });
            }
            catch { }
        }

#if ANDROID
        private static bool TryCreateVideoThumbnailAndroid(string videoPath, string outJpgPath)
        {
            try
            {
                using var retriever = new MediaMetadataRetriever();
                retriever.SetDataSource(videoPath);

                using var bmp = retriever.GetFrameAtTime(0, Option.ClosestSync);
                if (bmp == null) return false;

                using var fs = File.Create(outJpgPath);
                var ok = bmp.Compress(Bitmap.CompressFormat.Jpeg, 82, fs);

                return ok;
            }
            catch
            {
                return false;
            }
        }
#endif

#if WINDOWS
        private static async Task<bool> TryCreateVideoThumbnailWindowsAsync(string videoPath, string outJpgPath)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(videoPath);

                using var thumb = await file.GetThumbnailAsync(
                    ThumbnailMode.VideosView,
                    320,
                    ThumbnailOptions.UseCurrentScale);

                if (thumb == null) return false;

                using var input = thumb.AsStreamForRead();
                using var output = File.Create(outJpgPath);
                await input.CopyToAsync(output);

                return File.Exists(outJpgPath);
            }
            catch
            {
                return false;
            }
        }
#endif

        // ============================================================
        // 10) PLAYBACK: interfaccia + factory + implementazioni
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
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
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
