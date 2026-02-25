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
using System.Collections.Specialized;
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

            public HomePostVm()
            {
                Attachments.CollectionChanged += OnAttachmentsCollectionChanged;
            }

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
                    OnPropertyChanged(nameof(HasStatusPlaceholder));
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
                    OnPropertyChanged(nameof(HasStatusPlaceholder));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }

            public bool HasStatus => IsPendingUpload || HasSendError;
            public bool HasStatusPlaceholder => !HasStatus;
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
            public bool HasAttachmentsPlaceholder => !HasAttachments;
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

            private void OnAttachmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
            {
                OnPropertyChanged(nameof(HasAttachments));
                OnPropertyChanged(nameof(HasAttachmentsPlaceholder));
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
            var validPreviewLocalPath = HomeAttachmentVm.GetValidExistingPathOrNull(att.ThumbLocalPath);
            var validFullLocalPath = HomeAttachmentVm.GetValidExistingPathOrNull(att.LocalPath);

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
                PreviewLocalPath: validPreviewLocalPath,
                FullLocalPath: validFullLocalPath,
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
            private readonly object _previewRefreshGate = new();
            private CancellationTokenSource? _previewRefreshCts;
            private string? _previewCacheSignature;
            private bool _isPlaying;
            private bool _isDownloading;
            private bool _isPreviewDownloading;
            private int _downloadCountdownSeconds;
            private ImageSource? _cachedPreviewSource;
            private bool _cachedHasPreview;

            private const int PreviewRefreshDebounceMs = 60;
            private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMilliseconds(900);
            private static readonly Dictionary<int, PathValidityEntry> PathValidityMetadataCache = new();
            private static readonly object PathValidityMetadataCacheGate = new();

            private static long _previewSetterBaselineTicks;
            private static long _previewSetterBaselineSamples;
            private static long _previewSetterLocalTicks;
            private static long _previewSetterLocalSamples;
            private static long _previewSetterThumbTicks;
            private static long _previewSetterThumbSamples;

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
                    var normalized = NormalizePath(value);
                    if (string.Equals(_localPath, normalized, StringComparison.Ordinal))
                        return;
                    var sw = Stopwatch.StartNew();
                    CapturePreviewSetterBaseline(normalized);
                    _localPath = normalized;
                    QueuePreviewRefresh();
                    OnPropertyChanged();
                    sw.Stop();
                    Interlocked.Add(ref _previewSetterLocalTicks, sw.ElapsedTicks);
                    Interlocked.Increment(ref _previewSetterLocalSamples);
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
                    var normalized = NormalizePath(value);
                    if (string.Equals(_thumbLocalPath, normalized, StringComparison.Ordinal))
                        return;
                    var sw = Stopwatch.StartNew();
                    CapturePreviewSetterBaseline(normalized);
                    _thumbLocalPath = normalized;
                    QueuePreviewRefresh();
                    OnPropertyChanged();
                    sw.Stop();
                    Interlocked.Add(ref _previewSetterThumbTicks, sw.ElapsedTicks);
                    Interlocked.Increment(ref _previewSetterThumbSamples);
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
            public bool IsPrimaryMedia => IsImage || IsVideo;
            public bool ShowPrimaryMediaPlaceholder => IsPrimaryMedia && !HasPreviewSource;
            public bool ShowImagePreview => IsImage && HasPreviewSource;
            public bool ShowVideoPreview => IsVideo && HasPreviewSource;

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
            private bool RebuildPreviewCache()
            {
                var signature = BuildPreviewSignature();
                if (string.Equals(_previewCacheSignature, signature, StringComparison.Ordinal))
                    return false;

                var previousSource = _cachedPreviewSource;
                var previousHasPreview = _cachedHasPreview;
                _cachedPreviewSource = null;
                _cachedHasPreview = false;

                try
                {
                    if (ExistsWithMetadataCache(_localPath))
                    {
                        _cachedPreviewSource = ImageSource.FromFile(_localPath!);
                        _cachedHasPreview = true;
                        _previewCacheSignature = signature;
                        return !ReferenceEquals(previousSource, _cachedPreviewSource) || previousHasPreview != _cachedHasPreview;
                    }

                    if (ExistsWithMetadataCache(_thumbLocalPath))
                    {
                        _cachedPreviewSource = ImageSource.FromFile(_thumbLocalPath!);
                        _cachedHasPreview = true;
                    }
                }
                catch
                {
                    _cachedPreviewSource = null;
                    _cachedHasPreview = false;
                }

                _previewCacheSignature = signature;
                return !ReferenceEquals(previousSource, _cachedPreviewSource) || previousHasPreview != _cachedHasPreview;
            }

            private void QueuePreviewRefresh()
            {
                CancellationTokenSource cts;
                lock (_previewRefreshGate)
                {
                    _previewRefreshCts?.Cancel();
                    _previewRefreshCts?.Dispose();
                    _previewRefreshCts = new CancellationTokenSource();
                    cts = _previewRefreshCts;
                }

                _ = MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        await Task.Delay(PreviewRefreshDebounceMs, cts.Token);
                        if (!cts.IsCancellationRequested && RebuildPreviewCache())
                            await NotifyPreviewBindingsChangedInIdleAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    finally
                    {
                        lock (_previewRefreshGate)
                        {
                            if (ReferenceEquals(_previewRefreshCts, cts))
                            {
                                _previewRefreshCts = null;
                                cts.Dispose();
                            }
                        }
                    }
                });
            }

            private async Task NotifyPreviewBindingsChangedInIdleAsync(CancellationToken cancellationToken)
            {
                await Task.Yield();
                if (cancellationToken.IsCancellationRequested)
                    return;

                NotifyPreviewBindingsChanged();
            }

            private void NotifyPreviewBindingsChanged()
            {
                OnPropertyChanged(nameof(DisplayPreviewSource));
                OnPropertyChanged(nameof(HasPreviewSource));
                OnPropertyChanged(nameof(ShowPrimaryMediaPlaceholder));
                OnPropertyChanged(nameof(ShowImagePreview));
                OnPropertyChanged(nameof(ShowVideoPreview));
                PreviewSourceChanged?.Invoke(this, EventArgs.Empty);
            }

            private string BuildPreviewSignature()
                => string.Concat(Type, "|", _localPath, "|", _thumbLocalPath, "|", ContentType, "|", FileName);

            private static string? NormalizePath(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                var trimmed = path.Trim();
                return trimmed.Length == 0 ? null : trimmed;
            }

            public static string? GetValidExistingPathOrNull(string? path)
            {
                var normalized = NormalizePath(path);
                return ExistsWithMetadataCache(normalized) ? normalized : null;
            }

            private static bool ExistsWithMetadataCache(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                var normalized = NormalizePath(path);
                if (normalized == null)
                    return false;

                var key = StringComparer.OrdinalIgnoreCase.GetHashCode(normalized);
                var now = DateTime.UtcNow;

                lock (PathValidityMetadataCacheGate)
                {
                    if (PathValidityMetadataCache.TryGetValue(key, out var cached)
                        && string.Equals(cached.Path, normalized, StringComparison.OrdinalIgnoreCase)
                        && now - cached.TimestampUtc <= MetadataCacheTtl)
                    {
                        return cached.Exists;
                    }
                }

                var exists = File.Exists(normalized);

                lock (PathValidityMetadataCacheGate)
                {
                    PathValidityMetadataCache[key] = new PathValidityEntry(normalized, exists, now);
                }

                return exists;
            }

            private static void CapturePreviewSetterBaseline(string? normalizedPath)
            {
                var baselineSw = Stopwatch.StartNew();
                if (!string.IsNullOrWhiteSpace(normalizedPath))
                    _ = File.Exists(normalizedPath);
                baselineSw.Stop();
                Interlocked.Add(ref _previewSetterBaselineTicks, baselineSw.ElapsedTicks);
                Interlocked.Increment(ref _previewSetterBaselineSamples);
            }

            public static PreviewSetterLatencySnapshot GetPreviewSetterLatencySnapshot()
            {
                return new PreviewSetterLatencySnapshot(
                    BaselineAverageMs: ComputeAverageMilliseconds(_previewSetterBaselineTicks, _previewSetterBaselineSamples),
                    LocalPathAverageMs: ComputeAverageMilliseconds(_previewSetterLocalTicks, _previewSetterLocalSamples),
                    ThumbLocalPathAverageMs: ComputeAverageMilliseconds(_previewSetterThumbTicks, _previewSetterThumbSamples),
                    BaselineSamples: Interlocked.Read(ref _previewSetterBaselineSamples),
                    LocalPathSamples: Interlocked.Read(ref _previewSetterLocalSamples),
                    ThumbLocalPathSamples: Interlocked.Read(ref _previewSetterThumbSamples));
            }

            private static double ComputeAverageMilliseconds(long ticks, long samples)
                => samples <= 0 ? 0d : ticks * 1000d / Stopwatch.Frequency / samples;

            private readonly record struct PathValidityEntry(string Path, bool Exists, DateTime TimestampUtc);
            public readonly record struct PreviewSetterLatencySnapshot(
                double BaselineAverageMs,
                double LocalPathAverageMs,
                double ThumbLocalPathAverageMs,
                long BaselineSamples,
                long LocalPathSamples,
                long ThumbLocalPathSamples);

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
                    LocalPath = GetValidExistingPathOrNull(att.FullLocalPath),
                    ThumbLocalPath = GetValidExistingPathOrNull(att.PreviewLocalPath)
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
