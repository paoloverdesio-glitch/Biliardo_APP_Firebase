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
        public sealed class HomePostVm : BindableObject
        {
            private bool _isPendingUpload;
            private bool _hasSendError;
            private bool _requiresSync;
            private Command<HomePostVm>? _syncCommand;
            private bool _hasFullData;
            private bool _isReadyForDisplay;

            public string PostId { get; set; } = "";
            public string? ClientNonce { get; set; }
            public string AuthorUid { get; set; } = "";
            public string AuthorNickname { get; set; } = "";
            public string AuthorFirstName { get; set; } = "";
            public string AuthorLastName { get; set; } = "";
            public string? AuthorAvatarPath { get; set; }
            public string? AuthorAvatarUrl { get; set; }
            public DateTimeOffset CreatedAtUtc { get; set; }
            public string Text { get; set; } = "";
            public ObservableCollection<HomeAttachmentVm> Attachments { get; set; } = new();
            public int LikeCount { get; set; }
            public int CommentCount { get; set; }
            public int ShareCount { get; set; }
            public int SchemaVersion { get; set; } = HomePostValidatorV2.SchemaVersion;
            public bool Ready { get; set; }
            public bool Deleted { get; set; }
            public DateTimeOffset? DeletedAtUtc { get; set; }
            public string? RepostOfPostId { get; set; }
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
            public Command<HomePostVm>? SyncCommand
            {
                get => _syncCommand;
                set
                {
                    _syncCommand = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSyncAction));
                }
            }
            public string PendingText { get; set; } = "";
            public List<PendingItemVm> PendingItems { get; } = new();

            public bool HasText => !string.IsNullOrWhiteSpace(Text);
            public string AuthorFullName => $"{AuthorFirstName} {AuthorLastName}".Trim();
            public bool HasAuthorFullName => !string.IsNullOrWhiteSpace(AuthorFullName);
            public string AuthorFullNameDisplay => HasAuthorFullName ? $"({AuthorFullName})" : "";
            public string AuthorDisplayName => AuthorNickname;
            public string AuthorAvatarDisplayName => HasAuthorFullName ? AuthorFullName : AuthorNickname;

            public Color AuthorNicknameColor => GetNicknameColor(PostId);
            public bool HasAttachments => Attachments != null && Attachments.Count > 0;
            public bool HasSyncAction => RequiresSync && SyncCommand != null;

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

            public string CreatedAtLabel => CreatedAtUtc.ToLocalTime().ToString("g");

            // Compat (eventuali usi altrove)
            public Color LikeTextColor => LikeCount > 0 ? Colors.Red : Colors.White;
            public string LikeLabel => $"♥ {LikeCount}";
            public string CommentLabel => $"💬 {CommentCount}";
            public string ShareLabel => $"↗ {ShareCount}";
            public bool HasFullData
            {
                get => _hasFullData;
                set
                {
                    if (_hasFullData == value)
                        return;
                    _hasFullData = value;
                    UpdateReadyState();
                }
            }

            public bool IsReadyForDisplay
            {
                get => _isReadyForDisplay;
                private set
                {
                    if (_isReadyForDisplay == value)
                        return;
                    _isReadyForDisplay = value;
                    OnPropertyChanged();
                }
            }

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

            public void AttachAttachment(HomeAttachmentVm attachment)
            {
                if (attachment == null)
                    return;

                attachment.PreviewSourceChanged += (_, __) => UpdateReadyState();
                Attachments.Add(attachment);
                UpdateReadyState();
            }

            public static HomePostVm FromService(FirestoreHomeFeedService.HomePostItem post)
            {
                var vm = new HomePostVm
                {
                    PostId = post.PostId,
                    ClientNonce = post.ClientNonce,
                    AuthorUid = post.AuthorUid,
                    AuthorNickname = post.AuthorNickname,
                    AuthorFirstName = post.AuthorFirstName,
                    AuthorLastName = post.AuthorLastName,
                    AuthorAvatarPath = post.AuthorAvatarPath,
                    AuthorAvatarUrl = post.AuthorAvatarUrl,
                    CreatedAtUtc = post.CreatedAtUtc,
                    Text = post.Text ?? "",
                    LikeCount = post.LikeCount,
                    CommentCount = post.CommentCount,
                    ShareCount = post.ShareCount,
                    SchemaVersion = post.SchemaVersion,
                    Ready = post.Ready,
                    Deleted = post.Deleted,
                    DeletedAtUtc = post.DeletedAtUtc,
                    RepostOfPostId = post.RepostOfPostId,
                    IsLiked = post.IsLiked,
                    IsPendingUpload = false,
                    HasSendError = false,
                    RequiresSync = false,
                    HasFullData = true
                };

                foreach (var att in post.Attachments)
                    vm.AttachAttachment(HomeAttachmentVm.FromService(att));

                vm.UpdateReadyState();

                return vm;
            }

            private void UpdateReadyState()
            {
                if (IsPendingUpload || HasSendError)
                {
                    IsReadyForDisplay = true;
                    return;
                }

                var contract = BuildContractFromVm(this);
                IsReadyForDisplay = HomePostValidatorV2.IsServerReady(contract, out _);
            }

            private static readonly Color[] NicknamePalette = new[]
            {
                Color.FromArgb("#FFD54F"),
                Color.FromArgb("#4FC3F7"),
                Color.FromArgb("#FF8A65"),
                Color.FromArgb("#81C784"),
                Color.FromArgb("#BA68C8"),
                Color.FromArgb("#F06292"),
                Color.FromArgb("#A1887F"),
                Color.FromArgb("#90A4AE")
            };

            private static Color GetNicknameColor(string seed)
            {
                if (NicknamePalette.Length == 0)
                    return Colors.White;

                var hash = seed?.GetHashCode() ?? 0;
                var idx = Math.Abs(hash) % NicknamePalette.Length;
                return NicknamePalette[idx];
            }
        }

        private static HomePostContractV2 BuildContractFromVm(HomePostVm vm)
        {
            return new HomePostContractV2(
                PostId: vm.PostId,
                CreatedAtUtc: vm.CreatedAtUtc,
                AuthorUid: vm.AuthorUid,
                AuthorNickname: vm.AuthorNickname,
                AuthorFirstName: vm.AuthorFirstName,
                AuthorLastName: vm.AuthorLastName,
                AuthorAvatarPath: vm.AuthorAvatarPath,
                AuthorAvatarUrl: vm.AuthorAvatarUrl,
                Text: vm.Text ?? "",
                Attachments: vm.Attachments.Select(ToContract).ToArray(),
                Deleted: vm.Deleted,
                DeletedAtUtc: vm.DeletedAtUtc,
                RepostOfPostId: vm.RepostOfPostId,
                ClientNonce: vm.ClientNonce,
                LikeCount: vm.LikeCount,
                CommentCount: vm.CommentCount,
                ShareCount: vm.ShareCount,
                SchemaVersion: vm.SchemaVersion,
                Ready: vm.Ready);
        }

        private static FirestoreHomeFeedService.HomePostItem ToHomePostItem(HomePostVm vm)
        {
            return new FirestoreHomeFeedService.HomePostItem(
                PostId: vm.PostId,
                AuthorUid: vm.AuthorUid,
                AuthorNickname: vm.AuthorNickname,
                AuthorFirstName: vm.AuthorFirstName,
                AuthorLastName: vm.AuthorLastName,
                AuthorAvatarPath: vm.AuthorAvatarPath,
                AuthorAvatarUrl: vm.AuthorAvatarUrl,
                CreatedAtUtc: vm.CreatedAtUtc,
                Text: vm.Text ?? "",
                Attachments: vm.Attachments.Select(ToHomeAttachment).ToList(),
                LikeCount: vm.LikeCount,
                CommentCount: vm.CommentCount,
                ShareCount: vm.ShareCount,
                Deleted: vm.Deleted,
                DeletedAtUtc: vm.DeletedAtUtc,
                RepostOfPostId: vm.RepostOfPostId,
                ClientNonce: vm.ClientNonce,
                SchemaVersion: vm.SchemaVersion,
                Ready: vm.Ready,
                IsLiked: vm.IsLiked);
        }

        private static FirestoreHomeFeedService.HomeAttachment ToHomeAttachment(HomeAttachmentVm att)
        {
            return new FirestoreHomeFeedService.HomeAttachment(
                Type: att.Type ?? "",
                StoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: BuildAttachmentExtra(att),
                ThumbStoragePath: att.ThumbStoragePath,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                ThumbWidth: att.ThumbWidth,
                ThumbHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static HomeAttachmentContractV2 ToContract(HomeAttachmentVm att)
        {
            return new HomeAttachmentContractV2(
                Type: att.Type ?? "",
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: BuildAttachmentExtra(att),
                PreviewStoragePath: att.GetPreviewRemotePath(),
                FullStoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                PreviewLocalPath: att.ThumbLocalPath,
                FullLocalPath: att.LocalPath,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                PreviewWidth: att.ThumbWidth,
                PreviewHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static HomeAttachmentContractV2 ToContract(FirestoreHomeFeedService.HomeAttachment att)
        {
            return new HomeAttachmentContractV2(
                Type: att.Type ?? "",
                FileName: att.FileName,
                ContentType: att.ContentType,
                SizeBytes: att.SizeBytes,
                DurationMs: att.DurationMs,
                Extra: att.Extra,
                PreviewStoragePath: att.GetPreviewRemotePath(),
                FullStoragePath: att.StoragePath,
                DownloadUrl: att.DownloadUrl,
                PreviewLocalPath: null,
                FullLocalPath: null,
                LqipBase64: att.LqipBase64,
                PreviewType: att.PreviewType,
                PreviewWidth: att.ThumbWidth,
                PreviewHeight: att.ThumbHeight,
                Waveform: att.Waveform);
        }

        private static Dictionary<string, object>? BuildAttachmentExtra(HomeAttachmentVm att)
        {
            if (att == null)
                return null;

            if (att.IsLocation)
            {
                return new Dictionary<string, object>
                {
                    ["lat"] = att.Latitude ?? 0,
                    ["lon"] = att.Longitude ?? 0,
                    ["address"] = att.Address ?? ""
                };
            }

            return null;
        }

        public sealed class HomeAttachmentVm : BindableObject
        {
            private bool _isPlaying;
            private bool _isDownloading;
            private bool _isPreviewDownloading;
            private int _downloadCountdownSeconds;
            private ImageSource? _cachedPreviewSource;
            private bool _cachedHasPreview;

            public string Type { get; set; } = "";
            public string? StoragePath { get; set; }
            public string? DownloadUrl { get; set; }
            public string? FileName { get; set; }
            public string? ContentType { get; set; }
            public long SizeBytes { get; set; }
            public long DurationMs { get; set; }
            private string? _localPath;
            public string? LocalPath
            {
                get => _localPath;
                set
                {
                    if (_localPath == value)
                        return;
                    _localPath = value;
                    RebuildPreviewCache();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                    OnPropertyChanged(nameof(HasPreviewSource));
                    PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
                }
            }
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
                set
                {
                    if (_thumbLocalPath == value)
                        return;
                    _thumbLocalPath = value;
                    RebuildPreviewCache();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayPreviewSource));
                    OnPropertyChanged(nameof(HasPreviewSource));
                    PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
                }
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
            public bool RequiresPreview => HomeAttachmentPreviewRules.RequiresPreview(Type, ContentType, FileName);
            public bool HasPreviewSource => !RequiresPreview || _cachedHasPreview;

            public string? GetPreviewRemotePath() => ThumbStoragePath;

            public event EventHandler? PreviewSourceChanged;

            public bool IsPlaying
            {
                get => _isPlaying;
                set { _isPlaying = value; OnPropertyChanged(); OnPropertyChanged(nameof(AudioPlayLabel)); }
            }

            public string AudioPlayLabel => IsPlaying ? "Stop" : "Play";

            public bool IsDownloading
            {
                get => _isDownloading;
                set
                {
                    if (_isDownloading == value)
                        return;
                    _isDownloading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusy));
                    OnPropertyChanged(nameof(DownloadCountdownLabel));
                }
            }

            public bool IsPreviewDownloading
            {
                get => _isPreviewDownloading;
                set
                {
                    if (_isPreviewDownloading == value)
                        return;
                    _isPreviewDownloading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBusy));
                }
            }

            public int DownloadCountdownSeconds
            {
                get => _downloadCountdownSeconds;
                set
                {
                    if (_downloadCountdownSeconds == value)
                        return;
                    _downloadCountdownSeconds = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DownloadCountdownLabel));
                }
            }

            public string DownloadCountdownLabel => IsDownloading ? $"Download {DownloadCountdownSeconds}s" : "";

            public bool IsBusy => IsDownloading || IsPreviewDownloading;

            public ImageSource? DisplayPreviewSource => _cachedPreviewSource;

            // Cache immagine locale: niente I/O nei getter bindati.
            private void RebuildPreviewCache()
            {
                _cachedPreviewSource = null;
                _cachedHasPreview = false;

                try
                {
                    if (!string.IsNullOrWhiteSpace(_localPath) && File.Exists(_localPath))
                    {
                        _cachedPreviewSource = ImageSource.FromFile(_localPath);
                        _cachedHasPreview = true;
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(_thumbLocalPath) && File.Exists(_thumbLocalPath))
                    {
                        _cachedPreviewSource = ImageSource.FromFile(_thumbLocalPath);
                        _cachedHasPreview = true;
                    }
                }
                catch
                {
                    _cachedPreviewSource = null;
                    _cachedHasPreview = false;
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
                    SizeBytes = att.SizeBytes,
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
                    if (att.Extra.TryGetValue("lat", out var lat))
                        vm.Latitude = ReadDoubleExtra(lat);
                    if (att.Extra.TryGetValue("lon", out var lon))
                        vm.Longitude = ReadDoubleExtra(lon);
                    if (att.Extra.TryGetValue("address", out var addr))
                        vm.Address = ReadStringExtra(addr);
                }

                return vm;
            }

            public static HomeAttachmentVm FromContract(HomeAttachmentContractV2 att)
            {
                var vm = new HomeAttachmentVm
                {
                    Type = att.Type ?? "",
                    StoragePath = att.FullStoragePath,
                    DownloadUrl = att.DownloadUrl,
                    FileName = att.FileName ?? (att.Type == "audio" ? "Audio" : att.Type == "video" ? "Video" : "File"),
                    ContentType = att.ContentType,
                    SizeBytes = att.SizeBytes,
                    DurationMs = att.DurationMs,
                    ThumbStoragePath = att.PreviewStoragePath,
                    LqipBase64 = att.LqipBase64,
                    PreviewType = att.PreviewType,
                    ThumbWidth = att.PreviewWidth,
                    ThumbHeight = att.PreviewHeight,
                    Waveform = att.Waveform,
                    LocalPath = att.FullLocalPath,
                    ThumbLocalPath = att.PreviewLocalPath
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

            private static double? ReadDoubleExtra(object? value)
            {
                if (value is double d)
                    return d;
                if (value is float f)
                    return f;
                if (value is long l)
                    return l;
                if (value is int i)
                    return i;
                if (value is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d2))
                    return d2;
                return null;
            }

            private static string? ReadStringExtra(object? value)
            {
                if (value is string s)
                    return s;
                if (value is JsonElement el && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
                return null;
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
