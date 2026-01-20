// ========================= 1) NOME FILE E SCOPO =========================
// Pagine_Home/Pagina_Home.xaml.cs
// Code-behind della Home. Implementa:
//  - Barra icone (menu laterale, mercatino, sfida, chat, menu account).
//  - Menu laterale sinistro free1..free15 con pannello a scorrimento.
//  - Menu laterale destro account (Info app / Esci →) con pannello a scorrimento.
//  - Popup verde stile unificato (informazioni e messaggi Home).
//  - Navigazione verso Pagina_MessaggiLista.
// =======================================================================

using System;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Devices.Sensors;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Componenti_UI.Composer;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;

#if WINDOWS
using Windows.Media.Core;
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
        private readonly IAudioPlayback _audioPlayback;

        public ObservableCollection<HomePostVm> Posts { get; } = new();

        // Popup personalizzato
        private TaskCompletionSource<bool>? _popupTcs;

        // ===================== 3) COSTRUTTORE ============================
        public Pagina_Home()
        {
            InitializeComponent();
            BindingContext = this;

            // Nasconde la Navigation Bar (barra grigia con titolo) su questa pagina
            NavigationPage.SetHasNavigationBar(this, false);

            _audioPlayback = AudioPlaybackFactory.Create();
        }
        // =================================================================

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // BLOCCO HARD: prima di qualsiasi accesso a Firestore/Storage
            // Se non c'è sessione Firebase valida in locale, si torna a Login e NON si carica il feed.
            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            await LoadFeedAsync();
        }

        /// <summary>
        /// Verifica che esista una sessione Firebase locale (idToken+refreshToken e uid).
        /// Non fa chiamate di rete. Se manca, torna a Login.
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

            // Messaggio utente: se è TargetInvocationException, mostriamo l'inner.
            var msg = core.Message;

#if DEBUG
            // In debug aggiungiamo dettagli per diagnosi (senza dipendenze esterne).
            msg += "\n\n" + core.ToString();
#endif
            return msg;
        }

        private async Task LoadFeedAsync()
        {
            try
            {
                var res = await _homeFeed.ListPostsAsync(20, null);
                Posts.Clear();
                foreach (var post in res.Items)
                    Posts.Add(HomePostVm.FromService(post));
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore feed");
            }
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
            if (!string.IsNullOrWhiteSpace(item.LocalFilePath))
            {
                try { if (File.Exists(item.LocalFilePath)) File.Delete(item.LocalFilePath); } catch { }
            }
        }

        private async Task SendHomePostAsync(ComposerSendPayload payload, string? sentSingleLocalId)
        {
            try
            {
                // SAFETY: evita invii se non loggato (es. Home aperta per errore)
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                if (string.IsNullOrWhiteSpace(payload.Text) && !payload.PendingItems.Any())
                    return;

                var attachments = new List<FirestoreHomeFeedService.HomeAttachment>();

                foreach (var item in payload.PendingItems)
                {
                    var att = await BuildHomeAttachmentAsync(item);
                    if (att != null)
                        attachments.Add(att);
                }

                await _homeFeed.CreatePostAsync(payload.Text ?? "", attachments);

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

                await LoadFeedAsync();
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore invio");
            }
        }

        private async Task<FirestoreHomeFeedService.HomeAttachment?> BuildHomeAttachmentAsync(PendingItemVm item)
        {
            if (item == null) return null;

            if (item.Kind == PendingKind.Location)
            {
                var extra = new Dictionary<string, object>
                {
                    ["lat"] = FirestoreRestClient.VDouble(item.Latitude ?? 0),
                    ["lon"] = FirestoreRestClient.VDouble(item.Longitude ?? 0),
                    ["address"] = FirestoreRestClient.VString(item.Address ?? "")
                };

                return new FirestoreHomeFeedService.HomeAttachment("location", null, null, null, null, 0, 0, extra);
            }

            if (item.Kind == PendingKind.Contact)
            {
                var extra = new Dictionary<string, object>
                {
                    ["name"] = FirestoreRestClient.VString(item.ContactName ?? item.DisplayName),
                    ["phone"] = FirestoreRestClient.VString(item.ContactPhone ?? "")
                };

                return new FirestoreHomeFeedService.HomeAttachment("contact", null, null, null, null, 0, 0, extra);
            }

            if (item.Kind == PendingKind.Poll)
                return new FirestoreHomeFeedService.HomeAttachment("poll", null, null, null, null, 0, 0, null);

            if (item.Kind == PendingKind.Event)
                return new FirestoreHomeFeedService.HomeAttachment("event", null, null, null, null, 0, 0, null);

            if (string.IsNullOrWhiteSpace(item.LocalFilePath) || !File.Exists(item.LocalFilePath))
                return null;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var fileName = Path.GetFileName(item.LocalFilePath);
            var objectPath = $"home_posts/media/{Guid.NewGuid():N}/{fileName}";

            // FIX: named args per disambiguare l'overload (build/runtime issue)
            var upload = await FirebaseStorageRestClient.UploadFileWithResultAsync(
                idToken: idToken,
                objectPath: objectPath,
                localFilePath: item.LocalFilePath,
                contentType: FirebaseStorageRestClient.GuessContentTypeFromPath(item.LocalFilePath),
                ct: default);

            if (item.Kind == PendingKind.AudioDraft)
            {
                try { File.Delete(item.LocalFilePath); } catch { }
            }

            var type = item.Kind switch
            {
                PendingKind.Image => "image",
                PendingKind.Video => "video",
                PendingKind.AudioDraft => "audio",
                PendingKind.File => "file",
                _ => "file"
            };

            return new FirestoreHomeFeedService.HomeAttachment(
                type,
                upload.StoragePath,
                upload.DownloadUrl,
                fileName,
                upload.ContentType,
                upload.SizeBytes,
                item.DurationMs,
                null);
        }

        private async Task PickHomeFromGalleryAsync()
        {
            var choice = await DisplayActionSheet("Galleria", "Annulla", null, "Foto", "Video");
            if (choice == "Foto")
            {
                var fr = await MediaPicker.Default.PickPhotoAsync();
                if (fr == null) return;
                var local = await CopyToCacheAsync(fr, "home_photo");
                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Image,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
                    SizeBytes = new FileInfo(local).Length
                }, 10);
            }
            else if (choice == "Video")
            {
                var fr = await MediaPicker.Default.PickVideoAsync();
                if (fr == null) return;
                var local = await CopyToCacheAsync(fr, "home_video");
                HomeComposer.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Video,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
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
            var local = await CopyToCacheAsync(fr, "home_camera_photo");
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Image,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
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
            var local = await CopyToCacheAsync(fr, "home_camera_video");
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Video,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                SizeBytes = new FileInfo(local).Length,
                DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
            }, 10);
        }

        private async Task PickHomeDocumentAsync()
        {
            var res = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Seleziona documento" });
            if (res == null) return;
            var local = await CopyToCacheAsync(res, "home_doc");
            HomeComposer.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.File,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
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

        private async void OnLikeClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not HomePostVm post)
                return;

            try
            {
                // SAFETY: evita chiamate se non loggato
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                await _homeFeed.ToggleLikeAsync(post.PostId);
                post.IsLiked = !post.IsLiked;
                post.LikeCount += post.IsLiked ? 1 : -1;
                post.NotifyCounters();
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore like");
            }
        }

        private async void OnCommentClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not HomePostVm post)
                return;

            await Navigation.PushAsync(new PostDetailPage(post));
        }

        private async void OnShareClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not HomePostVm post)
                return;

            var choice = await DisplayActionSheet("Condividi", "Annulla", null, "Condividi esterno", "Repost interno");
            if (choice == "Condividi esterno")
            {
                await Share.Default.RequestAsync(new ShareTextRequest
                {
                    Text = post.Text,
                    Title = "Condividi"
                });
            }
            else if (choice == "Repost interno")
            {
                // SAFETY: evita chiamate se non loggato
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
                // SAFETY: evita download da Storage se non loggato
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

        private async Task<string?> EnsureHomeAudioDownloadedAsync(HomeAttachmentVm att)
        {
            if (!string.IsNullOrWhiteSpace(att.LocalPath) && File.Exists(att.LocalPath))
                return att.LocalPath;

            if (string.IsNullOrWhiteSpace(att.StoragePath))
                return null;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                return null;

            var ext = Path.GetExtension(att.FileName ?? "");
            if (string.IsNullOrWhiteSpace(ext)) ext = ".m4a";
            var local = Path.Combine(FileSystem.CacheDirectory, $"home_audio_{Guid.NewGuid():N}{ext}");

            await FirebaseStorageRestClient.DownloadToFileAsync(idToken, att.StoragePath, local);
            att.LocalPath = local;
            return local;
        }

        private static async Task<string> CopyToCacheAsync(FileResult fr, string prefix)
        {
            var ext = Path.GetExtension(fr.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

            var dest = Path.Combine(FileSystem.CacheDirectory, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");

            await using var src = await fr.OpenReadAsync();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst);

            return dest;
        }

        // ===================== 4) MENU LATERALE SINISTRO =================
        // 4.1 Toggle del menu laterale sinistro (Tap su icona 3 palle o overlay)
        private async void OnMenuLaterale_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleMenuAsync();
        }

        // 4.2 Logica di apertura/chiusura con animazione (sinistra)
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

        // 4.3 Cattura voci del menu laterale sinistro (Clicked/Tapped)
        private async void OnMenuVoice(object? sender, EventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async void OnMenuVoice(object? sender, TappedEventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        // 4.4 Router per voci free1..free15: apre pagina placeholder
        private async Task HandleMenuVoiceAsync(object? sender)
        {
            string? voce = null;
            if (sender is Label lbl) voce = lbl.Text;
            else if (sender is Button btn) voce = btn.Text;

            voce ??= "free";
            voce = voce.Trim();

            // Chiudi menu sinistro prima di navigare
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
        // 5.1 Toggle menu destro (icona freccia o tap overlay destro)
        private async void OnLogoutMenu(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async void OnLogoutMenu_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        // 5.2 Logica apertura/chiusura menu destro con animazione
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

        // 5.3 Click "Info su BiliardoApp"
        private async void OnLogoutInfoClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await ShowInfoBiliardoAppAsync();
        }

        // 5.4 Click "Esci →"
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

            var messageBuilder = new StringBuilder()
                .AppendLine($"{appName} v{version} (build {build})")
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
            // Logout reale Firebase: altrimenti rimangono token e al riavvio si tenta accesso Firestore.
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
            public string PostId { get; set; } = "";
            public string AuthorUid { get; set; } = "";
            public string AuthorNickname { get; set; } = "";
            public string? AuthorAvatarPath { get; set; }
            public string? AuthorAvatarUrl { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string Text { get; set; } = "";
            public ObservableCollection<HomeAttachmentVm> Attachments { get; set; } = new();
            public int LikeCount { get; set; }
            public int CommentCount { get; set; }
            public int ShareCount { get; set; }
            public bool IsLiked { get; set; }

            public bool HasText => !string.IsNullOrWhiteSpace(Text);
            public string CreatedAtLabel => CreatedAtUtc.ToLocalTime().ToString("g");
            public string LikeLabel => $"{(IsLiked ? "♥" : "♡")} {LikeCount}";
            public string CommentLabel => $"💬 {CommentCount}";
            public string ShareLabel => $"↗ {ShareCount}";

            public void NotifyCounters()
            {
                OnPropertyChanged(nameof(LikeLabel));
                OnPropertyChanged(nameof(CommentLabel));
                OnPropertyChanged(nameof(ShareLabel));
            }

            public static HomePostVm FromService(FirestoreHomeFeedService.HomePostItem post)
            {
                var vm = new HomePostVm
                {
                    PostId = post.PostId,
                    AuthorUid = post.AuthorUid,
                    AuthorNickname = post.AuthorNickname,
                    AuthorAvatarPath = post.AuthorAvatarPath,
                    AuthorAvatarUrl = post.AuthorAvatarUrl,
                    CreatedAtUtc = post.CreatedAtUtc,
                    Text = post.Text ?? "",
                    LikeCount = post.LikeCount,
                    CommentCount = post.CommentCount,
                    ShareCount = post.ShareCount
                };

                foreach (var att in post.Attachments)
                    vm.Attachments.Add(HomeAttachmentVm.FromService(att));

                return vm;
            }
        }

        public sealed class HomeAttachmentVm : BindableObject
        {
            private bool _isPlaying;

            public string Type { get; set; } = "";
            public string? StoragePath { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileName { get; set; }
            public long DurationMs { get; set; }
            public string? LocalPath { get; set; }

            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public string? Address { get; set; }

            public bool IsImage => Type == "image";
            public bool IsAudio => Type == "audio";
            public bool IsFile => Type == "file";
            public bool IsVideo => Type == "video";
            public bool IsFileOrVideo => IsFile || IsVideo;
            public bool IsLocation => Type == "location";
            public bool IsContact => Type == "contact";
            public bool IsPoll => Type == "poll";
            public bool IsEvent => Type == "event";
            public string AddressLabel => !string.IsNullOrWhiteSpace(Address) ? Address! : $"{Latitude:0.0000}, {Longitude:0.0000}";

            public bool IsPlaying
            {
                get => _isPlaying;
                set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioPlayLabel)); }
            }

            public string AudioPlayLabel => IsPlaying ? "Stop" : "Play";

            public static HomeAttachmentVm FromService(FirestoreHomeFeedService.HomeAttachment att)
            {
                var vm = new HomeAttachmentVm
                {
                    Type = att.Type ?? "",
                    StoragePath = att.StoragePath,
                    DownloadUrl = att.DownloadUrl,
                    FileName = att.FileName ?? (att.Type == "audio" ? "Audio" : att.Type == "video" ? "Video" : "File"),
                    DurationMs = att.DurationMs
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
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
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
