using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

using Biliardo.App.Componenti_UI;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Pagine_Messaggi
{
    public partial class Pagina_MessaggiDettaglio : ContentPage
    {
        // ============================================================
        // 1) MODELLI VM PER UI (MESSAGGI + ALLEGATI)
        //    - Obiettivo: tenere la UI "pulita" e il binding semplice
        // ============================================================

        // ============================================================
        // 2) ENUM: TIPI ALLEGATO (usati per invio e per mapping)
        // ============================================================
        public enum AttachmentKind
        {
            Audio,
            Photo,
            Video,
            File,
            Location,
            Contact
        }

        // ============================================================
        // 3) VM: ALLEGATO (usato per invio e pending items)
        // ============================================================
        public sealed class AttachmentVm
        {
            // 3.1) Identità allegato
            public AttachmentKind Kind { get; set; }
            public string DisplayName { get; set; } = "";
            public string LocalPath { get; set; } = "";
            public string? MediaCacheKey { get; set; }

            // 3.2) Metadati media/file
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }
            public IReadOnlyList<int>? Waveform { get; set; }

            // 3.3) Location
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }

            // 3.4) Contact
            public string? ContactName { get; set; }
            public string? ContactPhone { get; set; }

            // 3.5) Label UI
            public string KindLabel => Kind switch
            {
                AttachmentKind.Audio => "Audio",
                AttachmentKind.Photo => "Foto",
                AttachmentKind.Video => "Video",
                AttachmentKind.File => "Documento",
                AttachmentKind.Location => "Posizione",
                AttachmentKind.Contact => "Contatto",
                _ => "Allegato"
            };
        }

        // ============================================================
        // 4) VM: MESSAGGIO CHAT (binding diretto per DataTemplate)
        // ============================================================
        public sealed class ChatMessageVm : BindableObject
        {
            private bool _requiresSync;
            private const int PlaybackWaveHistoryMs = 5000;
            private const int PlaybackWaveTickMs = 80;
            private const float PlaybackWaveStrokePx = 2f;
            private const float PlaybackWaveMaxPeakToPeakDip = 40f;
            // ------------------------------------------------------------
            // 4.1) Identità e ownership
            // ------------------------------------------------------------
            public string Id { get; set; } = "";
            public bool IsMine { get; set; }

            // "text", "photo", "video", "file", "audio", "location", "contact", "date"
            public string Type { get; set; } = "text";

            // ------------------------------------------------------------
            // 4.2) Cancellazioni
            // ------------------------------------------------------------
            public bool DeletedForAll { get; set; }
            public IReadOnlyList<string> DeletedFor { get; set; } = Array.Empty<string>();

            public bool IsHiddenForMe { get; set; }

            // Placeholder "Questo messaggio è stato eliminato"
            public bool IsDeletedPlaceholder => DeletedForAll;

            // ------------------------------------------------------------
            // 4.3) Separatore data
            // ------------------------------------------------------------
            public bool IsDateSeparator => string.Equals(Type, "date", StringComparison.OrdinalIgnoreCase);
            public string DateSeparatorText { get; set; } = "";

            // ------------------------------------------------------------
            // 4.4) Testo
            // ------------------------------------------------------------
            private string _text = "";
            public string Text
            {
                get => _text;
                set
                {
                    _text = value ?? "";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasText));
                }
            }

            public bool HasText => !string.IsNullOrWhiteSpace(Text);

            // ------------------------------------------------------------
            // 4.5) Timestamp
            // ------------------------------------------------------------
            public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
            public string TimeLabel => CreatedAt.ToLocalTime().ToString("HH:mm");

            // ------------------------------------------------------------
            // 4.6) Stato (spunte)
            // ------------------------------------------------------------
            private string _statusLabel = "";
            public string StatusLabel
            {
                get => _statusLabel;
                set
                {
                    _statusLabel = value ?? "";
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasStatus));
                }
            }

            public bool HasStatus => !string.IsNullOrWhiteSpace(StatusLabel);

            private Color _statusColor = Colors.White;
            public Color StatusColor
            {
                get => _statusColor;
                set
                {
                    _statusColor = value;
                    OnPropertyChanged();
                }
            }

            private bool _isPendingUpload;
            public bool IsPendingUpload
            {
                get => _isPendingUpload;
                set
                {
                    _isPendingUpload = value;
                    OnPropertyChanged();
                }
            }

            private bool _hasSendError;
            private bool _isDownloading;
            public bool HasSendError
            {
                get => _hasSendError;
                set
                {
                    _hasSendError = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowRetry));
                }
            }

            public bool IsDownloading
            {
                get => _isDownloading;
                set
                {
                    _isDownloading = value;
                    OnPropertyChanged();
                }
            }

            public bool ShowRetry => HasSendError;

            public Command<ChatMessageVm>? RetryCommand { get; set; }

            public string? PendingLocalPath { get; set; }

            // ------------------------------------------------------------
            // 4.7) Media/File (storage + metadati)
            // ------------------------------------------------------------
            public string? StoragePath { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }

            public string? ThumbStoragePath { get; set; }
            private string? _lqipBase64;
            public string? LqipBase64
            {
                get => _lqipBase64;
                set
                {
                    _lqipBase64 = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                }
            }

            public int? ThumbWidth { get; set; }
            public int? ThumbHeight { get; set; }
            public string? PreviewType { get; set; }

            private string? _mediaLocalPath;
            public string? MediaLocalPath
            {
                get => _mediaLocalPath;
                set
                {
                    _mediaLocalPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                }
            }

            private string? _thumbLocalPath;
            public string? ThumbLocalPath
            {
                get => _thumbLocalPath;
                set
                {
                    _thumbLocalPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                }
            }

            private string? _videoThumbnailPath;
            public string? VideoThumbnailPath
            {
                get => _videoThumbnailPath;
                set
                {
                    _videoThumbnailPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasVideoThumbnail));
                }
            }

            public bool HasVideoThumbnail
                => !string.IsNullOrWhiteSpace(VideoThumbnailPath) && File.Exists(VideoThumbnailPath);

            // Audio play state (UI button "Play/Stop")
            private bool _isAudioPlaying;
            public bool IsAudioPlaying
            {
                get => _isAudioPlaying;
                set
                {
                    _isAudioPlaying = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AudioPlayStopLabel));
                }
            }

            public string AudioPlayStopLabel => IsAudioPlaying ? "■" : "▶";
            public string AudioLabel => string.IsNullOrWhiteSpace(FileName) ? "Audio" : FileName!;
            public string DurationLabel => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"mm\:ss") : "";

            public WaveformDrawable PlaybackWave { get; } =
                new(PlaybackWaveHistoryMs, PlaybackWaveTickMs, PlaybackWaveStrokePx, PlaybackWaveMaxPeakToPeakDip);

            public IReadOnlyList<int>? AudioWaveform { get; set; }

            public ImageSource? DisplayPreviewSource
            {
                get
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(MediaLocalPath) && File.Exists(MediaLocalPath))
                            return ImageSource.FromFile(MediaLocalPath);

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
            // Flags tipo (usati dal DataTemplate XAML)
            public bool IsAudio => !DeletedForAll && string.Equals(Type, "audio", StringComparison.OrdinalIgnoreCase);
            public bool IsPhoto => !DeletedForAll && string.Equals(Type, "photo", StringComparison.OrdinalIgnoreCase);
            public bool IsVideo => !DeletedForAll && string.Equals(Type, "video", StringComparison.OrdinalIgnoreCase);
            public bool IsFile => !DeletedForAll && string.Equals(Type, "file", StringComparison.OrdinalIgnoreCase);
            public bool IsPdf => IsFile && (string.Equals(ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(Path.GetExtension(FileName ?? string.Empty), ".pdf", StringComparison.OrdinalIgnoreCase));
            public bool IsFileNonPdf => IsFile && !IsPdf;
            public bool IsFileOrVideo => IsFile || IsVideo;

            public string FileSizeLabel => SizeBytes > 0 ? FormatBytes(SizeBytes) : "";
            public string FileLabel => IsVideo ? $"🎬 {FileName}" : $"📎 {FileName}";

            // ------------------------------------------------------------
            // 4.8) Location
            // ------------------------------------------------------------
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }

            public bool IsLocation => !DeletedForAll && string.Equals(Type, "location", StringComparison.OrdinalIgnoreCase);

            public string LocationLabel
            {
                get
                {
                    if (Latitude == null || Longitude == null) return "";
                    return $"{Latitude.Value:0.000000}, {Longitude.Value:0.000000}";
                }
            }

            // ------------------------------------------------------------
            // 4.9) Contatto
            // ------------------------------------------------------------
            public string? ContactName { get; set; }
            public string? ContactPhone { get; set; }

            private Command<ChatMessageVm>? _syncCommand;

            public Command<ChatMessageVm>? SyncCommand
            {
                get => _syncCommand;
                set
                {
                    _syncCommand = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSyncAction));
                }
            }

            public bool RequiresSync
            {
                get => _requiresSync;
                set
                {
                    _requiresSync = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSyncAction));
                }
            }

            public bool HasSyncAction => RequiresSync && SyncCommand != null;

            public bool IsContact => !DeletedForAll && string.Equals(Type, "contact", StringComparison.OrdinalIgnoreCase);

            public string ContactLabel => $"{ContactName} - {ContactPhone}";

            // ============================================================
            // 5) FACTORY: SEPARATORI DATA
            // ============================================================
            public static ChatMessageVm CreateDateSeparator(DateTime dayLocalDate)
            {
                var it = new CultureInfo("it-IT");
                var today = DateTime.Now.Date;
                var yesterday = today.AddDays(-1);

                string label;
                if (dayLocalDate == today) label = "Oggi";
                else if (dayLocalDate == yesterday) label = "Ieri";
                else
                {
                    var s = dayLocalDate.ToString("dddd dd MMMM yyyy", it);
                    label = it.TextInfo.ToTitleCase(s);
                }

                return new ChatMessageVm
                {
                    Type = "date",
                    DateSeparatorText = label,
                    CreatedAt = new DateTimeOffset(dayLocalDate)
                };
            }

            // ============================================================
            // 6) FACTORY: DA FIRESTORE
            // ============================================================
            public static ChatMessageVm FromFirestore(FirestoreChatService.MessageItem m, string myUid, string peerUid)
            {
                var vm = new ChatMessageVm
                {
                    Id = m.MessageId,
                    IsMine = string.Equals(m.SenderId, myUid, StringComparison.Ordinal),
                    Type = string.IsNullOrWhiteSpace(m.Type) ? "text" : m.Type,
                    Text = m.Text ?? "",
                    DeletedForAll = m.DeletedForAll,
                    DeletedFor = m.DeletedFor ?? Array.Empty<string>(),
                    CreatedAt = m.CreatedAtUtc,
                    StoragePath = m.StoragePath,
                    FileName = m.FileName,
                    ContentType = m.ContentType,
                    SizeBytes = m.SizeBytes,
                    DurationMs = m.DurationMs,
                    ThumbStoragePath = m.ThumbStoragePath,
                    LqipBase64 = m.LqipBase64,
                    ThumbWidth = m.ThumbWidth,
                    ThumbHeight = m.ThumbHeight,
                    PreviewType = m.PreviewType,
                    AudioWaveform = m.Waveform,
                    Latitude = m.Latitude,
                    Longitude = m.Longitude,
                    ContactName = m.ContactName,
                    ContactPhone = m.ContactPhone
                };

                // 6.1) Nascosto per me (deletedFor contiene myUid)
                vm.IsHiddenForMe = vm.DeletedFor?.Contains(myUid, StringComparer.Ordinal) ?? false;

                // 6.2) Se eliminato per tutti: testo vuoto e placeholder visibile via IsDeletedPlaceholder
                if (vm.DeletedForAll)
                    vm.Text = "";

                // 6.3) Stato spunte (solo per messaggi miei)
                if (vm.IsMine)
                {
                    var delivered = (m.DeliveredTo ?? Array.Empty<string>()).Contains(peerUid, StringComparer.Ordinal);
                    var read = (m.ReadBy ?? Array.Empty<string>()).Contains(peerUid, StringComparer.Ordinal);

                    if (read)
                    {
                        vm.StatusLabel = "✓✓";
                        vm.StatusColor = Colors.DeepSkyBlue;
                    }
                    else if (delivered)
                    {
                        vm.StatusLabel = "✓✓";
                        vm.StatusColor = Colors.LightGray;
                    }
                    else
                    {
                        vm.StatusLabel = "✓";
                        vm.StatusColor = Colors.LightGray;
                    }
                }
                else
                {
                    vm.StatusLabel = "";
                    vm.StatusColor = Colors.White;
                }

                return vm;
            }

            // ============================================================
            // 7) HELPER: NOTIFICA CAMBIO MULTIPLO (PER UI PATCH IN PLACE)
            // ============================================================
            public void NotificaCambio(params string[] props)
            {
                if (props == null || props.Length == 0)
                {
                    OnPropertyChanged(string.Empty);
                    return;
                }

                foreach (var p in props)
                    OnPropertyChanged(p);
            }

            // ============================================================
            // 8) HELPER: FORMAT BYTES
            // ============================================================
            private static string FormatBytes(long bytes)
            {
                if (bytes <= 0) return "";
                double b = bytes;
                string[] units = { "B", "KB", "MB", "GB" };
                int u = 0;
                while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
                return $"{b:0.#} {units[u]}";
            }
        }
    }
}
