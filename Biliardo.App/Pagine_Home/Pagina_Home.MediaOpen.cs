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

            // Se già in locale, apri immediatamente (zero rete).
            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
            {
                await ShowImageModalAsync(att.LocalPath);
                return;
            }
            if (!string.IsNullOrWhiteSpace(att.ThumbLocalPath) && File.Exists(att.ThumbLocalPath))
            {
                await ShowImageModalAsync(att.ThumbLocalPath);
                return;
            }

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
            {
                await ShowPopupAsync("Immagine non disponibile offline.", "Offline");
                return;
            }

            var local = await EnsureHomeMediaDownloadedAsync(att, att.FileName ?? "image.jpg", showErrors: true);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
                return;

            await ShowImageModalAsync(local);
        }

        private async Task ShowImageModalAsync(string localPath)
        {
            var img = new Image
            {
                Source = localPath,
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

            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
            {
                await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(att.LocalPath) });
                return;
            }

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

            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
            {
                await Navigation.PushAsync(new PdfViewerPage(att.LocalPath, fileName));
                return;
            }

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

    }
}
