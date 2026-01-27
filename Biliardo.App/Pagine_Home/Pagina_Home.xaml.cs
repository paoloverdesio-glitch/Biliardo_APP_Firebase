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
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Pagine_Debug;
using Biliardo.App.RiquadroDebugTrasferimentiFirebase;
using Biliardo.App.Pagine_Media;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Infrastructure;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
        private readonly IAudioPlayback _audioPlayback;
        private readonly MediaCacheService _mediaCache = new();
        private readonly IMediaPreviewGenerator _previewGenerator = new MediaPreviewGenerator();
        private readonly HomeMediaPipeline _homeMediaPipeline;
        public Command<HomeAttachmentVm> OpenPdfCommand { get; }
        public Command<HomePostVm> RetryHomePostCommand { get; }

        public ObservableCollection<HomePostVm> Posts { get; } = new();

        // Popup personalizzato
        private TaskCompletionSource<bool>? _popupTcs;

        // Tuning scroll (Android: disabilita change animations, cache, fixed size)
        private readonly ScrollWorkCoordinator _feedCoordinator = new();
        private CollectionViewNativeScrollStateTracker? _feedTracker;

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
        }
        // =================================================================

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                return;

            ApplyHomeFeedScrollTuning();

            await LoadFeedAsync();
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

        private async Task LoadFeedAsync()
        {
            try
            {
                var res = await _homeFeed.ListPostsAsync(20, null);

                Posts.Clear();
                foreach (var post in res.Items)
                    Posts.Add(HomePostVm.FromService(post));

                _ = Task.Run(PrefetchHomeThumbsAsync);
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore feed");
            }
        }

        private async Task PrefetchHomeThumbsAsync()
        {
            if (!AppMediaOptions.PrefetchThumbsOnScroll)
                return;

            try
            {
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                    return;

                var attachments = new List<HomeAttachmentVm>();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var post in Posts)
                    {
                        foreach (var att in post.Attachments)
                            attachments.Add(att);
                    }
                });

                foreach (var att in attachments)
                {
                    if (string.IsNullOrWhiteSpace(att.ThumbStoragePath))
                        continue;

                    if (!string.IsNullOrWhiteSpace(att.ThumbLocalPath) && File.Exists(att.ThumbLocalPath))
                        continue;

                    var local = await _mediaCache.GetOrDownloadAsync(idToken!, att.ThumbStoragePath!, att.FileName ?? "thumb.jpg", isThumb: true, CancellationToken.None);
                    if (string.IsNullOrWhiteSpace(local))
                        continue;

                    MainThread.BeginInvokeOnMainThread(() => att.ThumbLocalPath = local);
                }
            }
            catch
            {
                // best-effort
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
            var nickname = FirebaseSessionePersistente.GetDisplayName() ?? FirebaseSessionePersistente.GetEmail() ?? "Io";

            var vm = new HomePostVm
            {
                PostId = $"local_{Guid.NewGuid():N}",
                AuthorUid = myUid,
                AuthorNickname = nickname,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Text = payload.Text ?? "",
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
                    vm.Attachments.Add(attVm);
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
                Posts.Insert(0, vm);
                FeedCollection.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
            });
        }

        private async Task SendHomePostOptimisticAsync(ComposerSendPayload payload, HomePostVm vm)
        {
            var attachments = new List<FirestoreHomeFeedService.HomeAttachment>();

            foreach (var item in vm.PendingItems)
            {
                var att = await BuildHomeAttachmentAsync(item);
                if (att != null)
                    attachments.Add(att);
            }

            var postId = await _homeFeed.CreatePostAsync(vm.PendingText ?? "", attachments);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.PostId = postId;
                vm.IsPendingUpload = false;
                vm.HasSendError = false;
                vm.Attachments.Clear();
                foreach (var att in attachments)
                    vm.Attachments.Add(HomeAttachmentVm.FromService(att));
            });
        }

        private void MarkHomePostFailed(HomePostVm vm, Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.IsPendingUpload = false;
                vm.HasSendError = true;
            });
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

        // ✅ FIX #1: gestione tap robusta (usa e.Parameter, non sender) -> like torna a funzionare
        private async void OnLikeClicked(object sender, TappedEventArgs e)
        {
            if (e?.Parameter is not HomePostVm post)
                return;

            try
            {
                if (!await EnsureFirebaseSessionOrBackToLoginAsync())
                    return;

                var res = await _homeFeed.ToggleLikeWithResultAsync(post.PostId);

                post.IsLiked = res.IsLikedNow;
                post.LikeCount = Math.Max(0, res.LikeCount);
                post.NotifyCounters();
            }
            catch (Exception ex)
            {
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

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var url = !string.IsNullOrWhiteSpace(att.DownloadUrl)
                ? att.DownloadUrl
                : FirebaseStorageRestClient.BuildAuthDownloadUrl(FirebaseStorageRestClient.DefaultStorageBucket, att.StoragePath);

            await Navigation.PushAsync(new VideoPlayerPage(url, att.DisplayPreviewSource));


            _ = Task.Run(async () =>
            {
                try
                {
                    var local = await _mediaCache.GetOrDownloadAsync(idToken!, att.StoragePath!, att.FileName ?? "video.mp4", isThumb: false, CancellationToken.None);
                    if (!string.IsNullOrWhiteSpace(local))
                        att.LocalPath = local;
                }
                catch { }
            });
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

            var local = await _mediaCache.GetOrDownloadAsync(idToken, att.StoragePath, att.FileName ?? "audio.m4a", isThumb: false, CancellationToken.None);
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

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
            if (string.IsNullOrWhiteSpace(idToken))
                return;

            var fileName = att.FileName ?? "document.pdf";
            if (!string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                fileName = $"{Path.GetFileNameWithoutExtension(fileName)}.pdf";

            var local = await _mediaCache.GetOrDownloadAsync(idToken!, att.StoragePath!, fileName, isThumb: false, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(local) || !File.Exists(local))
            {
                await ShowPopupAsync("PDF non disponibile.", "Info");
                return;
            }

            await Navigation.PushAsync(new PdfViewerPage(local, fileName));
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
            public string PendingText { get; set; } = "";
            public List<PendingItemVm> PendingItems { get; } = new();

            public bool HasText => !string.IsNullOrWhiteSpace(Text);
            public bool HasAttachments => Attachments != null && Attachments.Count > 0;

            public string CreatedAtLabel => CreatedAtUtc.ToLocalTime().ToString("g");

            // Compat (eventuali usi altrove)
            public Color LikeTextColor => LikeCount > 0 ? Colors.Red : Colors.White;
            public string LikeLabel => $"♥ {LikeCount}";
            public string CommentLabel => $"💬 {CommentCount}";
            public string ShareLabel => $"↗ {ShareCount}";

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
                    ShareCount = post.ShareCount,
                    IsLiked = post.IsLiked,
                    IsPendingUpload = false,
                    HasSendError = false
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
            public string? ContentType { get; set; }
            public long DurationMs { get; set; }
            public string? LocalPath { get; set; }
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
                set { _thumbLocalPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayPreviewSource)); }
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

            public bool IsPlaying
            {
                get => _isPlaying;
                set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioPlayLabel)); }
            }

            public string AudioPlayLabel => IsPlaying ? "Stop" : "Play";

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

                        if (!string.IsNullOrWhiteSpace(LqipBase64))
                        {
                            var bytes = Convert.FromBase64String(LqipBase64);
                            return ImageSource.FromStream(() => new MemoryStream(bytes));
                        }
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
