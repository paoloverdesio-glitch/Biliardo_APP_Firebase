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

                // Evita rimbalzi: auto-scroll solo se l'utente è già in top e non sta scrollando.
                if (!_isUserScrolling && (_lastKnownVisibleIndex <= 1 || _lastFirstVisibleIndex <= 1))
                {
                    FeedCollection.ScrollTo(0, position: ScrollToPosition.Start, animate: false);
                    _ = ScrollHomeToTopWithRetryAsync(considerKeyboard: true);
                }
            });

            // Aggiorna RAM feed (debounced)
            ScheduleMemoryCacheRefresh();
        }

        private async Task SendHomePostOptimisticAsync(ComposerSendPayload payload, HomePostVm vm)
        {
            DiagLog.Step("Home.SendPost", "Start");
            DiagLog.Note("Home.SendPost.TextLen", (vm.PendingText ?? "").Length.ToString());
            DiagLog.Note("Home.SendPost.PendingItems", vm.PendingItems.Count.ToString());

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
            DiagLog.Note("Home.SendPost.PostId", postId);
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

            ScheduleMemoryCacheRefresh();
            DiagLog.Step("Home.SendPost", "Ok");
        }

        private void MarkHomePostFailed(HomePostVm vm, Exception ex)
        {
            DiagLog.Exception("Home.SendPost", ex);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.IsPendingUpload = false;
                vm.HasSendError = true;
            });

            ScheduleMemoryCacheRefresh();
            MainThread.BeginInvokeOnMainThread(() => _ = ShowServerErrorPopupAsync("Errore invio post", ex));
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

    }
}
