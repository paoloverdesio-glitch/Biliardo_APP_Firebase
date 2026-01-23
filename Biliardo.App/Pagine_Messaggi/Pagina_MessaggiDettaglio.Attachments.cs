// FILE 2/3: Pagine_Messaggi/Pagina_MessaggiDettaglio.Attachments.cs

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
using Microsoft.Maui.Storage;

using Biliardo.App.Componenti_UI;
using Biliardo.App.Componenti_UI.Composer;
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

            try
            {
                // 2.1) Solo provider Firebase (questa pagina è progettata per Firebase)
                var provider = await SessionePersistente.GetProviderAsync();
                if (!string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Questa pagina è implementata per provider Firebase.");

                // 2.2) Token + uid
                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                var myUid = FirebaseSessionePersistente.GetLocalId();

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(myUid))
                    throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

                // 2.3) chatId
                var chatId = await EnsureChatIdAsync(idToken!, myUid!, peerId);

                // 2.4) Invio testo (se presente)
                if (!string.IsNullOrWhiteSpace(payload.Text))
                {
                    var msgId = Guid.NewGuid().ToString("N");
                    AddPendingOutgoingText(msgId, myUid!, payload.Text);
                    await _fsChat.SendTextMessageWithIdAsync(idToken!, chatId, msgId, myUid!, payload.Text);
                }

                // 2.5) Invio allegati pending dal Composer
                var items = payload.PendingItems ?? Array.Empty<PendingItemVm>();
                foreach (var item in items)
                {
                    var attachment = ToAttachmentVm(item);
                    if (attachment == null) continue;

                    var msgId = Guid.NewGuid().ToString("N");
                    AddPendingOutgoingAttachment(msgId, myUid!, attachment);
                    await SendAttachmentAsync(idToken!, myUid!, peerId, chatId, msgId, attachment);

                    // 2.6) Se bozza audio: elimina file locale dopo invio
                    if (item.Kind == PendingKind.AudioDraft && !string.IsNullOrWhiteSpace(item.LocalFilePath))
                    {
                        try { if (File.Exists(item.LocalFilePath)) File.Delete(item.LocalFilePath); } catch { }
                    }
                }

                // 2.7) Aggiorna UI composer
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

                // 2.8) Post-invio: torno in fondo e ricarico una volta
                _userNearBottom = true;
                await LoadOnceAsync(CancellationToken.None);
                _ = ScrollToEndAfterLayoutAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Invio non riuscito: {ex.Message}", "OK");
            }
            finally
            {
                _isSending = false;

                // 2.9) Aggiorna proprietà dipendenti
                OnPropertyChanged(nameof(CanSendTextOrAttachments));
                OnPropertyChanged(nameof(CanShowMic));
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
        private static Task<string> CopyToCacheAsync(FileResult fr, string prefix)
            => MediaFileHelper.CopyToCacheAsync(fr, prefix);

        // ============================================================
        // 7) SEND ATTACHMENT: upload su Storage + messaggio su Firestore
        // ============================================================
        private async Task SendAttachmentAsync(string idToken, string myUid, string peerUid, string chatId, string messageId, AttachmentVm a)
        {
            if (a == null) return;

            var msgId = messageId;

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

            // 7.4) Path oggetto su Storage
            var storagePath = $"chats/{chatId}/media/{msgId}/{fileName}";

            // 7.5) Upload Storage (REST) + custom metadata (ownerUid)
            var meta = new Dictionary<string, string>
            {
                ["ownerUid"] = myUid,
                ["scope"] = "chat",
                ["chatId"] = chatId,
                ["messageId"] = msgId
            };

            if (a.Kind == AttachmentKind.Audio)
            {
                var stableSize = await MediaFileHelper.WaitForStableFileSizeAsync(a.LocalPath);
                if (stableSize > 0)
                    sizeBytes = stableSize;

                MediaFileHelper.LogFileSnapshot("Chat.Audio.BeforeUpload", a.LocalPath, contentType);
            }

            await FirebaseStorageRestClient.UploadFileAsync(
                idToken: idToken,
                objectPath: storagePath,
                localFilePath: a.LocalPath,
                contentType: contentType,
                customMetadata: meta,
                ct: default);

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
                contentType: contentType);
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
        // 9) PENDING UI: MESSAGGIO OTTIMISTICO (TESTO/ALLEGATI)
        // ============================================================
        private void AddPendingOutgoingText(string messageId, string myUid, string text)
        {
            var vm = new ChatMessageVm
            {
                Id = messageId,
                IsMine = true,
                Type = "text",
                Text = text ?? "",
                CreatedAt = DateTimeOffset.UtcNow,
                StatusLabel = "✓"
            };

            AppendPendingMessage(vm);
        }

        private void AddPendingOutgoingAttachment(string messageId, string myUid, AttachmentVm a)
        {
            var type = a.Kind switch
            {
                AttachmentKind.Audio => "audio",
                AttachmentKind.Photo => "photo",
                AttachmentKind.Video => "video",
                AttachmentKind.File => "file",
                AttachmentKind.Location => "location",
                AttachmentKind.Contact => "contact",
                _ => "file"
            };

            var vm = new ChatMessageVm
            {
                Id = messageId,
                IsMine = true,
                Type = type,
                Text = "",
                CreatedAt = DateTimeOffset.UtcNow,
                StatusLabel = "✓",
                FileName = a.DisplayName,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                DurationMs = a.DurationMs,
                Latitude = a.Latitude,
                Longitude = a.Longitude,
                ContactName = a.ContactName,
                ContactPhone = a.ContactPhone
            };

            AppendPendingMessage(vm);
        }

        private void AppendPendingMessage(ChatMessageVm vm)
        {
            if (vm == null) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                var lastReal = Messaggi.LastOrDefault(x => !x.IsDateSeparator);
                var lastDay = lastReal?.CreatedAt.ToLocalTime().Date;
                var day = vm.CreatedAt.ToLocalTime().Date;

                if (lastDay == null || day != lastDay.Value)
                    Messaggi.Add(ChatMessageVm.CreateDateSeparator(day));

                Messaggi.Add(vm);
                _userNearBottom = true;
                _ = ScrollToEndAfterLayoutAsync();
            });
        }

        // ============================================================
        // 10) HANDLER “LEGACY” (se ancora referenziati da UI vecchie)
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
