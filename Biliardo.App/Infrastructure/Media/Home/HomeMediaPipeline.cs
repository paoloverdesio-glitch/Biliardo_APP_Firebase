using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Biliardo.App.Componenti_UI.Composer;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure.Media.Home
{
    public sealed class HomeMediaPipeline
    {
        private readonly IMediaPreviewGenerator _previewGenerator;
        private readonly MediaCacheService _mediaCache = new();

        public HomeMediaPipeline(IMediaPreviewGenerator previewGenerator)
        {
            _previewGenerator = previewGenerator;
        }

        public async Task<FirestoreHomeFeedService.HomeAttachment?> BuildAttachmentAsync(PendingItemVm item, string idToken, CancellationToken ct)
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

            var fileName = Path.GetFileName(item.LocalFilePath);
            var contentType = FirebaseStorageRestClient.GuessContentTypeFromPath(item.LocalFilePath);
            var sizeBytes = new FileInfo(item.LocalFilePath).Length;

            var kind = GetMediaKind(contentType, fileName);
            ValidateAttachmentLimits(kind, sizeBytes, item.DurationMs);

            if (string.IsNullOrWhiteSpace(item.MediaCacheKey))
            {
                var originalPath = item.LocalFilePath;
                var registration = await _mediaCache.RegisterLocalFileAsync(item.LocalFilePath, kind.ToString().ToLowerInvariant(), ct);
                if (registration != null)
                {
                    item.MediaCacheKey = registration.CacheKey;
                    item.LocalFilePath = registration.LocalPath;

                    if (!string.Equals(originalPath, item.LocalFilePath, StringComparison.Ordinal))
                    {
                        try { if (File.Exists(originalPath)) File.Delete(originalPath); } catch { }
                    }
                }
            }

            MediaPreviewResult? preview = null;
            var requiresPreview = kind is MediaKind.Image or MediaKind.Video or MediaKind.Pdf;
            if (requiresPreview)
            {
                preview = await _previewGenerator.GenerateAsync(
                    new MediaPreviewRequest(item.LocalFilePath, kind, contentType, fileName, "home", null, null),
                    ct);
            }

            var attachmentId = Guid.NewGuid().ToString("N");
            var objectPath = $"home_posts/media/{attachmentId}/{fileName}";

            var upload = await FirebaseStorageRestClient.UploadFileWithResultAsync(
                idToken: idToken,
                objectPath: objectPath,
                localFilePath: item.LocalFilePath,
                contentType: contentType,
                ct: ct);

            if (!string.IsNullOrWhiteSpace(item.MediaCacheKey))
                await _mediaCache.RegisterAliasAsync(upload.StoragePath, item.MediaCacheKey, ct);

            if (requiresPreview && (preview == null || string.IsNullOrWhiteSpace(preview.ThumbLocalPath) || !File.Exists(preview.ThumbLocalPath)))
                throw new InvalidOperationException("Preview non disponibile.");

            string? thumbStoragePath = null;
            if (preview != null && File.Exists(preview.ThumbLocalPath))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                thumbStoragePath = $"home_posts/media/{attachmentId}/thumb_{baseName}.jpg";

                await FirebaseStorageRestClient.UploadFileAsync(
                    idToken: idToken,
                    objectPath: thumbStoragePath,
                    localFilePath: preview.ThumbLocalPath,
                    contentType: "image/jpeg",
                    ct: ct);
            }

            if (preview != null && !string.IsNullOrWhiteSpace(preview.ThumbLocalPath))
            {
                try { if (File.Exists(preview.ThumbLocalPath)) File.Delete(preview.ThumbLocalPath); } catch { }
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
                null,
                thumbStoragePath,
                AppMediaOptions.StoreLqipInFirestore ? preview?.LqipBase64 : null,
                preview?.PreviewType,
                preview?.Width,
                preview?.Height);
        }

        public static MediaKind GetMediaKind(string contentType, string fileName)
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
    }
}
