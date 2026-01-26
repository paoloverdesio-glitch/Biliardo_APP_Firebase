using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

using Biliardo.App.Componenti_UI;
using Biliardo.App.Componenti_UI.Composer;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Servizi_Sicurezza;

// Alias
using Path = System.IO.Path;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) COMPOSER: EVENTI (ComposerBarView)
        // ============================================================

        private async void OnComposerAttachmentRequested(object sender, EventArgs e)
        {
            try
            {
                // 1.1) Apro un modal -> non fermare polling quando la pagina “sparisce”
                _suppressStopPollingOnce = true;

                var sheet = new BottomSheetAllegatiPage();
                sheet.AzioneSelezionata += async (_, az) =>
                {
                    await HandleAttachmentActionAsync(az);
                };

                await Navigation.PushModalAsync(sheet);
            }
            catch { }
        }

        private async void OnComposerSendRequested(object sender, ComposerSendPayload payload)
        {
            if (_isSending) return;
            await SendComposerPayloadAsync(payload, sentSingleLocalId: null);
        }

        private async void OnComposerPendingItemSendRequested(object sender, PendingItemVm item)
        {
            if (_isSending) return;
            await SendComposerPayloadAsync(new ComposerSendPayload("", new[] { item }), sentSingleLocalId: item.LocalId);
        }

        private void OnComposerPendingItemRemoved(object sender, PendingItemVm item)
        {
            // 1.2) Cleanup locale (bozza audio o file selezionato)
            if (!string.IsNullOrWhiteSpace(item.LocalFilePath))
            {
                try { if (File.Exists(item.LocalFilePath)) File.Delete(item.LocalFilePath); } catch { }
            }
        }

        // ============================================================
        // 2) INVIO: TESTO + ALLEGATI (server authoritative)
        // ============================================================
        private async Task SendComposerPayloadAsync(ComposerSendPayload payload, string? sentSingleLocalId)
        {
            if (_isSending) return;

            var peerId = (_peerUserId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(peerId))
            {
                await DisplayAlert("Errore", "Peer non valido (userId mancante).", "OK");
                return;
            }

            _isSending = true;

            string? idToken = null;
            string? myUid = null;
            string? chatId = null;

            try
            {
                // 2.1) Solo provider Firebase (questa pagina è progettata per Firebase)
                var provider = await SessionePersistente.GetProviderAsync();
                if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Questa pagina è implementata per provider Firebase.");

                // 2.2) Token + uid
                idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

                // 2.3) chatId
                chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

                // 2.4) Creazione VM ottimistiche (testo + allegati)
                var pendingSend = new List<(ChatMessageVm Vm, Func<Task> Send)>();

                if (!string.IsNullOrWhiteSpace(payload.Text))
                {
                    var msgId = Guid.NewGuid().ToString("N");
                    var vm = CreateOptimisticTextMessage(msgId, myUid!, payload.Text);
                    vm.RetryCommand = RetrySendCommand;
                    AddOptimisticMessage(vm);

                    pendingSend.Add((vm, async () =>
                    {
                        await _fsChat.SendTextMessageWithIdAsync(idToken!, chatId!, msgId, myUid!, payload.Text);
                        MarkOptimisticSent(vm);
                    }));
                }

                var items = payload.PendingItems ?? Array.Empty<PendingItemVm>();
                foreach (var item in items)
                {
                    var attachment = ToAttachmentVm(item);
                    if (attachment == null) continue;

                    var msgId = Guid.NewGuid().ToString("N");
                    var vm = CreateOptimisticAttachmentMessage(msgId, myUid!, attachment);
                    vm.PendingLocalPath = attachment.LocalPath;
                    vm.RetryCommand = RetrySendCommand;
                    AddOptimisticMessage(vm);

                    pendingSend.Add((vm, async () =>
                    {
                        await SendAttachmentAsync(idToken!, myUid!, peerId, chatId!, attachment, msgId);
                        MarkOptimisticSent(vm);

                        if (item.Kind == PendingKind.AudioDraft && !string.IsNullOrWhiteSpace(item.LocalFilePath))
                        {
                            try { if (File.Exists(item.LocalFilePath)) File.Delete(item.LocalFilePath); } catch { }
                        }
                    }));
                }

                // 2.5) Aggiorna UI composer subito
                if (sentSingleLocalId != null)
                {
                    var pending = ComposerBar.PendingItems.FirstOrDefault(x => x.LocalId == sentSingleLocalId);
                    if (pending != null)
                        ComposerBar.PendingItems.Remove(pending);
                }
                else
                {
                    ComposerBar.ClearComposer();
                }

                _isSending = false;
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));

                // 2.6) Invio in background (non blocco UI)
                foreach (var item in pendingSend)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await item.Send();
                        }
                        catch (Exception ex)
                        {
                            MarkOptimisticFailed(item.Vm, ex);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Invio non riuscito: {ex.Message}", "OK");
            }
            finally
            {
                if (_isSending)
                {
                    _isSending = false;
                    OnPropertyChanged(nameof(CanSendTextOrAttachments));
                    OnPropertyChanged(nameof(CanShowMic));
                }
            }
        }

        // ============================================================
        // 3) ALLEGATI: SCELTA AZIONE DAL BOTTOM SHEET
        // ============================================================
        private async Task HandleAttachmentActionAsync(BottomSheetAllegatiPage.AllegatoAzione az)
        {
            switch (az)
            {
                case BottomSheetAllegatiPage.AllegatoAzione.Gallery:
                    await PickFromGalleryAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Camera:
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        var choice = await DisplayActionSheet("Fotocamera", "Annulla", null, "Foto", "Video");
                        if (choice == "Foto") await CapturePhotoAsync();
                        else if (choice == "Video") await CaptureVideoAsync();
                    });
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Document:
                    await PickDocumentAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Audio:
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        await DisplayAlert("Vocale", "Usa il tasto microfono (pressione lunga) nella barra in basso.", "OK");
                    });
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Location:
                    await AttachLocationAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Contact:
                    await AttachContactAsync();
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Poll:
                    ComposerBar.TryAddPendingItem(new PendingItemVm
                    {
                        Kind = PendingKind.Poll,
                        DisplayName = "Sondaggio"
                    });
                    break;

                case BottomSheetAllegatiPage.AllegatoAzione.Event:
                    ComposerBar.TryAddPendingItem(new PendingItemVm
                    {
                        Kind = PendingKind.Event,
                        DisplayName = "Evento"
                    });
                    break;
            }
        }

        // ============================================================
        // 4) PICKERS: GALLERY / CAMERA / DOCUMENT
        // ============================================================
        private async Task PickFromGalleryAsync()
        {
            var choice = await DisplayActionSheet("Galleria", "Annulla", null, "Foto", "Video");

            if (choice == "Foto")
            {
                var fr = await MediaPicker.Default.PickPhotoAsync();
                if (fr == null) return;

                var local = await CopyToCacheAsync(fr, "photo");
                ComposerBar.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Image,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
                    SizeBytes = new FileInfo(local).Length
                });
            }
            else if (choice == "Video")
            {
                var fr = await MediaPicker.Default.PickVideoAsync();
                if (fr == null) return;

                var local = await CopyToCacheAsync(fr, "video");
                ComposerBar.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Video,
                    DisplayName = Path.GetFileName(local),
                    LocalFilePath = local,
                    SizeBytes = new FileInfo(local).Length,
                    DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
                });
            }
        }

        private async Task CapturePhotoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CapturePhotoAsync();
            if (fr == null) return;

            var local = await CopyToCacheAsync(fr, "camera_photo");
            ComposerBar.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Image,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                SizeBytes = new FileInfo(local).Length
            });
        }

        private async Task CaptureVideoAsync()
        {
            var st = await Permissions.RequestAsync<Permissions.Camera>();
            if (st != PermissionStatus.Granted)
            {
                await DisplayAlert("Permesso negato", "Serve il permesso fotocamera.", "OK");
                return;
            }

            var fr = await MediaPicker.Default.CaptureVideoAsync();
            if (fr == null) return;

            var local = await CopyToCacheAsync(fr, "camera_video");
            ComposerBar.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.Video,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                SizeBytes = new FileInfo(local).Length,
                DurationMs = MediaMetadataHelper.TryGetDurationMs(local)
            });
        }

        private async Task PickDocumentAsync()
        {
            var res = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Seleziona documento" });
            if (res == null) return;

            var local = await CopyToCacheAsync(res, "doc");
            ComposerBar.TryAddPendingItem(new PendingItemVm
            {
                Kind = PendingKind.File,
                DisplayName = Path.GetFileName(local),
                LocalFilePath = local,
                SizeBytes = new FileInfo(local).Length
            });
        }

        // ============================================================
        // 5) PICKERS: LOCATION / CONTACT
        // ============================================================
        private async Task AttachLocationAsync()
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

                ComposerBar.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Location,
                    DisplayName = "Posizione",
                    Latitude = loc.Latitude,
                    Longitude = loc.Longitude
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        private async Task AttachContactAsync()
        {
            try
            {
                var c = await Contacts.Default.PickContactAsync();
                if (c == null) return;

                // 5.1) Nome “umano”
                var name = (c.DisplayName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    var parts = new List<string>();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(c.GivenName)) parts.Add(c.GivenName.Trim());
                        if (!string.IsNullOrWhiteSpace(c.FamilyName)) parts.Add(c.FamilyName.Trim());
                    }
                    catch { }
                    name = string.Join(" ", parts).Trim();
                }
                if (string.IsNullOrWhiteSpace(name))
                    name = "Contatto";

                // 5.2) Numero
                var phone = (c.Phones?.FirstOrDefault()?.PhoneNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(phone))
                {
                    await DisplayAlert("Contatto", "Contatto senza numero telefonico.", "OK");
                    return;
                }

                ComposerBar.TryAddPendingItem(new PendingItemVm
                {
                    Kind = PendingKind.Contact,
                    DisplayName = name,
                    ContactName = name,
                    ContactPhone = phone
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }

        // ============================================================
        // 6) HELPERS: COPIA FILE IN CACHE (per invio/upload)
        // ============================================================
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

        // ============================================================
        // 7) SEND ATTACHMENT: upload su Storage + messaggio su Firestore
        // ============================================================
        private async Task SendAttachmentAsync(string idToken, string myUid, string peerUid, string chatId, AttachmentVm a, string? messageId = null)
        {
            if (a == null) return;

            var msgId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId;

            // 7.1) Location -> messaggio diretto (niente Storage)
            if (a.Kind == AttachmentKind.Location)
            {
                if (a.Latitude == null || a.Longitude == null)
                    return;

                await _fsChat.SendLocationMessageWithIdAsync(idToken, chatId, msgId, myUid, a.Latitude.Value, a.Longitude.Value);
                return;
            }

            // 7.2) Contact -> messaggio diretto (niente Storage)
            if (a.Kind == AttachmentKind.Contact)
            {
                await _fsChat.SendContactMessageWithIdAsync(
                    idToken, chatId, msgId, myUid,
                    a.ContactName ?? a.DisplayName,
                    a.ContactPhone ?? "");
                return;
            }

            // 7.3) File locale obbligatorio per audio/foto/video/documenti
            if (string.IsNullOrWhiteSpace(a.LocalPath) || !File.Exists(a.LocalPath))
                throw new InvalidOperationException($"Allegato locale non valido: {a.DisplayName}");

            var fileName = Path.GetFileName(a.LocalPath);

            var contentType = string.IsNullOrWhiteSpace(a.ContentType)
                ? MediaMetadataHelper.GuessContentType(fileName)
                : a.ContentType!;

            var sizeBytes = a.SizeBytes > 0 ? a.SizeBytes : new FileInfo(a.LocalPath).Length;

            var kind = GetMediaKind(contentType, fileName);

            var type = a.Kind switch
            {
                AttachmentKind.Audio => "audio",
                AttachmentKind.Photo => "photo",
                AttachmentKind.Video => "video",
                _ => "file"
            };

            var durationMs = a.DurationMs;
            if ((type == "audio" || type == "video") && durationMs <= 0)
                durationMs = MediaMetadataHelper.TryGetDurationMs(a.LocalPath);

            ValidateAttachmentLimits(kind, sizeBytes, durationMs);

            MediaPreviewResult? preview = null;
            if (kind is MediaKind.Image or MediaKind.Video or MediaKind.Pdf)
            {
                preview = await _previewGenerator.GenerateAsync(
                    new MediaPreviewRequest(a.LocalPath, kind, contentType, fileName, "chat", a.Latitude, a.Longitude),
                    CancellationToken.None);
            }

            // 7.4) Path oggetto su Storage
            var storagePath = $"chats/{chatId}/media/{msgId}/{fileName}";

            // 7.5) Upload Storage (REST)
            await FirebaseStorageRestClient.UploadFileAsync(
                idToken: idToken,
                objectPath: storagePath,
                localFilePath: a.LocalPath,
                contentType: contentType,
                ct: default);

            string? thumbStoragePath = null;
            if (preview != null && AppMediaOptions.StoreThumbInStorage && File.Exists(preview.ThumbLocalPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                thumbStoragePath = $"chats/{chatId}/media/{msgId}/thumb_{baseName}.jpg";

                await FirebaseStorageRestClient.UploadFileAsync(
                    idToken: idToken,
                    objectPath: thumbStoragePath,
                    localFilePath: preview.ThumbLocalPath,
                    contentType: "image/jpeg",
                    ct: default);
            }

            var previewMap = BuildPreviewPayload(preview, thumbStoragePath);

            // 7.6) Scrittura messaggio su Firestore
            await _fsChat.SendFileMessageWithIdAsync(
                idToken: idToken,
                chatId: chatId,
                messageId: msgId,
                senderUid: myUid,
                type: type,
                storagePath: storagePath,
                durationMs: durationMs,
                sizeBytes: sizeBytes,
                fileName: fileName,
                contentType: contentType,
                previewMap: previewMap,
                waveform: a.Waveform);

            if (preview != null && !string.IsNullOrWhiteSpace(preview.ThumbLocalPath))
            {
                try { if (File.Exists(preview.ThumbLocalPath)) File.Delete(preview.ThumbLocalPath); } catch { }
            }
        }

        private static MediaKind GetMediaKind(string contentType, string fileName)
        {
            if (!string.IsNullOrWhiteSpace(contentType))
            {
                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return MediaKind.Image;
                if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                    return MediaKind.Video;
                if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    return MediaKind.Audio;
                if (contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
                    return MediaKind.Pdf;
            }

            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" => MediaKind.Image,
                ".mp4" or ".mov" => MediaKind.Video,
                ".m4a" or ".mp3" or ".wav" or ".aac" => MediaKind.Audio,
                ".pdf" => MediaKind.Pdf,
                _ => MediaKind.File
            };
        }

        private static void ValidateAttachmentLimits(MediaKind kind, long sizeBytes, long durationMs)
        {
            switch (kind)
            {
                case MediaKind.Image:
                    if (sizeBytes > AppMediaOptions.MaxImageBytes)
                        throw new InvalidOperationException("Immagine troppo grande.");
                    break;
                case MediaKind.Video:
                    if (sizeBytes > AppMediaOptions.MaxVideoBytes)
                        throw new InvalidOperationException("Video troppo grande.");
                    if (durationMs > AppMediaOptions.MaxVideoDurationMs)
                        throw new InvalidOperationException("Video troppo lungo.");
                    break;
                case MediaKind.Audio:
                    if (sizeBytes > AppMediaOptions.MaxAudioBytes)
                        throw new InvalidOperationException("Audio troppo grande.");
                    break;
                case MediaKind.Pdf:
                case MediaKind.File:
                    if (sizeBytes > AppMediaOptions.MaxDocumentBytes)
                        throw new InvalidOperationException("Documento troppo grande.");
                    break;
            }
        }

        private static Dictionary<string, object>? BuildPreviewPayload(MediaPreviewResult? preview, string? thumbStoragePath)
        {
            if (preview == null)
                return null;

            var map = new Dictionary<string, object>();

            if (!string.IsNullOrWhiteSpace(thumbStoragePath))
                map["thumbStoragePath"] = FirestoreRestClient.VString(thumbStoragePath);

            if (!string.IsNullOrWhiteSpace(preview.LqipBase64) && AppMediaOptions.StoreLqipInFirestore)
                map["lqipBase64"] = FirestoreRestClient.VString(preview.LqipBase64);

            if (preview.Width > 0)
                map["thumbWidth"] = FirestoreRestClient.VInt(preview.Width);
            if (preview.Height > 0)
                map["thumbHeight"] = FirestoreRestClient.VInt(preview.Height);

            if (!string.IsNullOrWhiteSpace(preview.PreviewType))
                map["previewType"] = FirestoreRestClient.VString(preview.PreviewType);

            return map.Count == 0 ? null : map;
        }

        private ChatMessageVm CreateOptimisticTextMessage(string msgId, string myUid, string text)
        {
            return new ChatMessageVm
            {
                Id = msgId,
                IsMine = true,
                Type = "text",
                Text = text ?? "",
                CreatedAt = DateTimeOffset.Now,
                StatusLabel = "✓",
                StatusColor = Colors.LightGray,
                IsPendingUpload = true
            };
        }

        private ChatMessageVm CreateOptimisticAttachmentMessage(string msgId, string myUid, AttachmentVm a)
        {
            var type = a.Kind switch
            {
                AttachmentKind.Photo => "photo",
                AttachmentKind.Video => "video",
                AttachmentKind.Audio => "audio",
                AttachmentKind.File => "file",
                AttachmentKind.Location => "location",
                AttachmentKind.Contact => "contact",
                _ => "file"
            };

            var contentType = a.ContentType;
            if (string.IsNullOrWhiteSpace(contentType) && !string.IsNullOrWhiteSpace(a.LocalPath))
                contentType = MediaMetadataHelper.GuessContentType(a.LocalPath);

            return new ChatMessageVm
            {
                Id = msgId,
                IsMine = true,
                Type = type,
                FileName = string.IsNullOrWhiteSpace(a.LocalPath) ? a.DisplayName : Path.GetFileName(a.LocalPath),
                ContentType = contentType,
                DurationMs = a.DurationMs,
                SizeBytes = a.SizeBytes,
                MediaLocalPath = a.Kind is AttachmentKind.Photo ? a.LocalPath : null,
                CreatedAt = DateTimeOffset.Now,
                StatusLabel = "✓",
                StatusColor = Colors.LightGray,
                IsPendingUpload = true,
                AudioWaveform = a.Waveform,
                Latitude = a.Latitude,
                Longitude = a.Longitude,
                ContactName = a.ContactName,
                ContactPhone = a.ContactPhone
            };
        }

        private void AddOptimisticMessage(ChatMessageVm vm)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var lastReal = Messaggi.LastOrDefault(x => !x.IsDateSeparator);
                var lastDay = lastReal?.CreatedAt.ToLocalTime().Date;
                var newDay = vm.CreatedAt.ToLocalTime().Date;

                if (lastDay == null || newDay != lastDay.Value)
                    Messaggi.Add(ChatMessageVm.CreateDateSeparator(newDay));

                Messaggi.Add(vm);
                _userNearBottom = true;
                ScrollToMessage(vm);
            });
        }

        private void MarkOptimisticSent(ChatMessageVm vm)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.IsPendingUpload = false;
                vm.HasSendError = false;
                vm.StatusLabel = "✓✓";
                vm.StatusColor = Colors.LightGray;
            });
        }

        private void MarkOptimisticFailed(ChatMessageVm vm, Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.IsPendingUpload = false;
                vm.HasSendError = true;
                vm.StatusLabel = "Errore";
                vm.StatusColor = Colors.OrangeRed;
            });
        }

        private async Task RetrySendAsync(ChatMessageVm? vm)
        {
            if (vm == null || !vm.IsMine)
                return;

            try
            {
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();
                var peerId = (_peerUserId ?? "").Trim();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid) || string.IsNullOrWhiteSpace(peerId))
                    return;

                var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

                vm.HasSendError = false;
                vm.IsPendingUpload = true;
                vm.StatusLabel = "✓";
                vm.StatusColor = Colors.LightGray;

                if (string.Equals(vm.Type, "text", StringComparison.OrdinalIgnoreCase))
                {
                    await _fsChat.SendTextMessageWithIdAsync(idToken!, chatId, vm.Id, myUid!, vm.Text ?? "");
                    MarkOptimisticSent(vm);
                    return;
                }

                if (string.Equals(vm.Type, "location", StringComparison.OrdinalIgnoreCase))
                {
                    if (vm.Latitude == null || vm.Longitude == null)
                        throw new InvalidOperationException("Posizione non valida.");

                    var attachment = new AttachmentVm
                    {
                        Kind = AttachmentKind.Location,
                        DisplayName = "Posizione",
                        Latitude = vm.Latitude,
                        Longitude = vm.Longitude
                    };

                    await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, attachment, vm.Id);
                    MarkOptimisticSent(vm);
                    return;
                }

                if (string.Equals(vm.Type, "contact", StringComparison.OrdinalIgnoreCase))
                {
                    var attachment = new AttachmentVm
                    {
                        Kind = AttachmentKind.Contact,
                        DisplayName = vm.ContactName ?? "Contatto",
                        ContactName = vm.ContactName,
                        ContactPhone = vm.ContactPhone
                    };

                    await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, attachment, vm.Id);
                    MarkOptimisticSent(vm);
                    return;
                }

                if (string.IsNullOrWhiteSpace(vm.PendingLocalPath) || !File.Exists(vm.PendingLocalPath))
                    throw new InvalidOperationException("File locale non disponibile per il retry.");

                var attachment = new AttachmentVm
                {
                    Kind = vm.Type switch
                    {
                        "photo" => AttachmentKind.Photo,
                        "video" => AttachmentKind.Video,
                        "audio" => AttachmentKind.Audio,
                        _ => AttachmentKind.File
                    },
                    DisplayName = vm.FileName ?? "File",
                    LocalPath = vm.PendingLocalPath,
                    DurationMs = vm.DurationMs,
                    SizeBytes = vm.SizeBytes,
                    ContentType = vm.ContentType,
                    Waveform = vm.AudioWaveform
                };

                await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, attachment, vm.Id);
                MarkOptimisticSent(vm);
            }
            catch (Exception ex)
            {
                MarkOptimisticFailed(vm!, ex);
            }
        }

        // ============================================================
        // 8) MAPPING: PendingItemVm (Composer) -> AttachmentVm (invio)
        // ============================================================
        private static AttachmentVm? ToAttachmentVm(PendingItemVm item)
        {
            if (item == null) return null;

            return item.Kind switch
            {
                PendingKind.Image => new AttachmentVm
                {
                    Kind = AttachmentKind.Photo,
                    DisplayName = item.DisplayName,
                    LocalPath = item.LocalFilePath ?? "",
                    SizeBytes = item.SizeBytes
                },
                PendingKind.Video => new AttachmentVm
                {
                    Kind = AttachmentKind.Video,
                    DisplayName = item.DisplayName,
                    LocalPath = item.LocalFilePath ?? "",
                    SizeBytes = item.SizeBytes,
                    DurationMs = item.DurationMs
                },
                PendingKind.File => new AttachmentVm
                {
                    Kind = AttachmentKind.File,
                    DisplayName = item.DisplayName,
                    LocalPath = item.LocalFilePath ?? "",
                    SizeBytes = item.SizeBytes
                },
                PendingKind.AudioDraft => new AttachmentVm
                {
                    Kind = AttachmentKind.Audio,
                    DisplayName = item.DisplayName,
                    LocalPath = item.LocalFilePath ?? "",
                    DurationMs = item.DurationMs,
                    SizeBytes = item.SizeBytes
                },
                PendingKind.Location => new AttachmentVm
                {
                    Kind = AttachmentKind.Location,
                    DisplayName = item.DisplayName,
                    Latitude = item.Latitude,
                    Longitude = item.Longitude
                },
                PendingKind.Contact => new AttachmentVm
                {
                    Kind = AttachmentKind.Contact,
                    DisplayName = item.DisplayName,
                    ContactName = item.ContactName ?? item.DisplayName,
                    ContactPhone = item.ContactPhone
                },
                _ => null
            };
        }

        // ============================================================
        // 9) HANDLER “LEGACY” (se ancora referenziati da UI vecchie)
        // ============================================================
        private void OnComposerTapped(object sender, TappedEventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible) IsEmojiPanelVisible = false;
                EntryText?.Focus();
            }
            catch { }
        }

        private void OnEntryFocused(object sender, FocusEventArgs e)
        {
            try
            {
                if (IsEmojiPanelVisible)
                    IsEmojiPanelVisible = false;
            }
            catch { }
        }

        private void OnEmojiToggleClicked(object sender, EventArgs e)
        {
            try
            {
                if (!IsEmojiPanelVisible)
                {
                    IsEmojiPanelVisible = true;
                    EntryText?.Unfocus();
                }
                else
                {
                    IsEmojiPanelVisible = false;
                    EntryText?.Focus();
                }
            }
            catch { }
        }

        private void OnEmojiSelected(object sender, EventArgs e)
        {
            try
            {
                if (sender is Button b && b.CommandParameter is string emo)
                    TestoMessaggio = (TestoMessaggio ?? "") + emo;
            }
            catch { }
        }

        private async void OnRemoveAttachmentClicked(object sender, EventArgs e)
        {
            try
            {
                // Nota: mantenuto per compatibilità con eventuali UI vecchie basate su AllegatiSelezionati
                if (sender is Button b && b.CommandParameter is AttachmentVm a)
                    AllegatiSelezionati.Remove(a);
            }
            catch { }
        }
    }
}
