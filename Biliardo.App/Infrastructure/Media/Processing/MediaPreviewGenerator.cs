using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.Storage;

using Biliardo.App.Infrastructure.Media;

// =========================================================
// Alias espliciti per evitare ambiguità tra namespace.
// - SysFile/SysPath: usati sempre (tutte le piattaforme)
// =========================================================
using SysFile = global::System.IO.File;
using SysPath = global::System.IO.Path;

#if ANDROID
// =========================================================
// ANDROID: alias e namespace specifici
// =========================================================
using JFile = global::Java.IO.File;

using Android.Graphics;
using Android.Media;
using Android.Graphics.Pdf;
using Android.OS;
#endif

#if WINDOWS
// =========================================================
// WINDOWS: namespace specifici WinRT
// =========================================================
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
#endif

namespace Biliardo.App.Infrastructure.Media.Processing
{
    public sealed class MediaPreviewGenerator : IMediaPreviewGenerator
    {
        // =========================================================
        // API PUBBLICA
        // - Genera anteprime “best effort” (thumb + LQIP) da file locale
        // - Deve rimanere non bloccante: lavoro pesante fuori dal thread UI
        // =========================================================
        public Task<MediaPreviewResult?> GenerateAsync(MediaPreviewRequest req, CancellationToken ct)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.LocalFilePath))
                return Task.FromResult<MediaPreviewResult?>(null);

            return Task.Run(async () =>
            {
                try
                {
                    if (ct.IsCancellationRequested)
                        return null;

                    // Sezione: dispatch per tipo contenuto
                    return req.Kind switch
                    {
                        MediaKind.Image => await GenerateImagePreviewAsync(req, ct),
                        MediaKind.Video => await GenerateVideoPreviewAsync(req, ct),
                        MediaKind.Pdf => await GeneratePdfPreviewAsync(req, ct),
                        _ => null
                    };
                }
                catch
                {
                    // Best-effort: non bloccare mai la pipeline per errori preview
                    return null;
                }
            }, ct);
        }

        // =========================================================
        // IMMAGINI
        // =========================================================
        private async Task<MediaPreviewResult?> GenerateImagePreviewAsync(MediaPreviewRequest req, CancellationToken ct)
        {
            // Nota: per GIF generiamo una preview “still” (primo frame / decodifica standard)
            var previewType = IsGif(req) ? "gif_still" : "image";

#if ANDROID
            // Android: decode + resize + jpeg
            return await Task.Run(() => GenerateAndroidImagePreview(req.LocalFilePath, previewType), ct);
#elif WINDOWS
            // Windows: decoder WinRT + transform + jpeg
            return await GenerateWindowsImagePreviewAsync(req.LocalFilePath, previewType, ct);
#else
            return null;
#endif
        }

        // =========================================================
        // VIDEO (poster frame)
        // =========================================================
        private async Task<MediaPreviewResult?> GenerateVideoPreviewAsync(MediaPreviewRequest req, CancellationToken ct)
        {
#if ANDROID
            // Android: MediaMetadataRetriever -> frame -> resize + jpeg
            return await Task.Run(() => GenerateAndroidVideoPreview(req.LocalFilePath), ct);
#elif WINDOWS
            // Windows: thumbnail WinRT -> resize + jpeg
            return await GenerateWindowsVideoPreviewAsync(req.LocalFilePath, ct);
#else
            return null;
#endif
        }

        // =========================================================
        // PDF (prima pagina)
        // =========================================================
        private async Task<MediaPreviewResult?> GeneratePdfPreviewAsync(MediaPreviewRequest req, CancellationToken ct)
        {
#if ANDROID
            // Android: PdfRenderer -> render page 0 -> resize + jpeg
            return await Task.Run(() => GenerateAndroidPdfPreview(req.LocalFilePath), ct);
#elif WINDOWS
            // Windows: Windows.Data.Pdf -> render page 0 -> resize + jpeg
            return await GenerateWindowsPdfPreviewAsync(req.LocalFilePath, ct);
#else
            return null;
#endif
        }

        // =========================================================
        // UTIL: riconoscimento GIF
        // =========================================================
        private static bool IsGif(MediaPreviewRequest req)
        {
            if (!string.IsNullOrWhiteSpace(req.ContentType) && req.ContentType.Contains("gif", StringComparison.OrdinalIgnoreCase))
                return true;

            var ext = SysPath.GetExtension(req.FileName ?? req.LocalFilePath) ?? "";
            return string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase);
        }

#if ANDROID
        // =========================================================
        // ANDROID IMPLEMENTATION
        // =========================================================

        private static MediaPreviewResult? GenerateAndroidImagePreview(string path, string previewType)
        {
            try
            {
                if (!SysFile.Exists(path)) return null;

                // 1) Legge dimensioni senza decodificare tutto
                using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
                BitmapFactory.DecodeFile(path, bounds);

                // 2) Calcola downsample per ridurre memoria/CPU
                var sample = CalculateInSampleSize(bounds.OutWidth, bounds.OutHeight, AppMediaOptions.ThumbMaxLongSidePx);

                // 3) Decodifica downsampled
                using var opts = new BitmapFactory.Options { InSampleSize = sample };
                using var bmp = BitmapFactory.DecodeFile(path, opts);
                if (bmp == null) return null;

                // 4) Resize finale “thumb”
                using var thumb = ResizeBitmapIfNeeded(bmp, AppMediaOptions.ThumbMaxLongSidePx);

                // 5) Salva thumb JPG su file temporaneo
                var thumbPath = CreateTempJpegPath("thumb");
                if (!SaveJpeg(thumb, thumbPath, AppMediaOptions.ThumbJpegQuality)) return null;

                // 6) LQIP (ultra compresso) da thumb
                var (lqip, width, height) = BuildLqipFromBitmap(thumb, thumbPath);

                return new MediaPreviewResult(thumbPath, lqip, width, height, previewType);
            }
            catch
            {
                return null;
            }
        }

        private static MediaPreviewResult? GenerateAndroidVideoPreview(string path)
        {
            try
            {
                // Estrae un fotogramma (poster) e lo usa come thumb
                using var retriever = new MediaMetadataRetriever();
                retriever.SetDataSource(path);

                using var bmp = retriever.GetFrameAtTime(0, Option.ClosestSync);
                if (bmp == null) return null;

                using var thumb = ResizeBitmapIfNeeded(bmp, AppMediaOptions.ThumbMaxLongSidePx);
                var thumbPath = CreateTempJpegPath("thumb");
                if (!SaveJpeg(thumb, thumbPath, AppMediaOptions.ThumbJpegQuality)) return null;

                var (lqip, width, height) = BuildLqipFromBitmap(thumb, thumbPath);

                return new MediaPreviewResult(thumbPath, lqip, width, height, "video_poster");
            }
            catch
            {
                return null;
            }
        }

        private static MediaPreviewResult? GenerateAndroidPdfPreview(string path)
        {
            try
            {
                // PdfRenderer richiede un ParcelFileDescriptor
                using var file = new JFile(path);
                using var pfd = ParcelFileDescriptor.Open(file, ParcelFileMode.ReadOnly);
                using var renderer = new PdfRenderer(pfd);
                if (renderer.PageCount <= 0) return null;

                using var page = renderer.OpenPage(0);

                var width = page.Width;
                var height = page.Height;

                using var bmp = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888);
                page.Render(bmp, null, null, PdfRenderMode.ForDisplay);

                using var thumb = ResizeBitmapIfNeeded(bmp, AppMediaOptions.ThumbMaxLongSidePx);
                var thumbPath = CreateTempJpegPath("thumb");
                if (!SaveJpeg(thumb, thumbPath, AppMediaOptions.ThumbJpegQuality)) return null;

                var (lqip, outW, outH) = BuildLqipFromBitmap(thumb, thumbPath);

                return new MediaPreviewResult(thumbPath, lqip, outW, outH, "pdf_page1");
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // ANDROID HELPERS
        // =========================================================

        private static int CalculateInSampleSize(int width, int height, int reqSize)
        {
            if (width <= 0 || height <= 0) return 1;

            var maxSide = Math.Max(width, height);
            var sample = 1;
            while (maxSide / sample > reqSize)
                sample *= 2;
            return sample < 1 ? 1 : sample;
        }

        private static Bitmap ResizeBitmapIfNeeded(Bitmap source, int maxLongSide)
        {
            var maxSide = Math.Max(source.Width, source.Height);
            if (maxSide <= maxLongSide)
                return source;

            var scale = (float)maxLongSide / maxSide;
            var newW = Math.Max(1, (int)(source.Width * scale));
            var newH = Math.Max(1, (int)(source.Height * scale));
            return Bitmap.CreateScaledBitmap(source, newW, newH, true);
        }

        private static bool SaveJpeg(Bitmap bmp, string path, int quality)
        {
            try
            {
                using var fs = SysFile.Create(path);
                return bmp.Compress(Bitmap.CompressFormat.Jpeg, quality, fs);
            }
            catch
            {
                return false;
            }
        }

        private static (string? Lqip, int Width, int Height) BuildLqipFromBitmap(Bitmap thumb, string thumbPath)
        {
            var width = thumb.Width;
            var height = thumb.Height;

            // Se disabilitato via opzioni, non generiamo LQIP
            if (!AppMediaOptions.StoreLqipInFirestore)
                return (null, width, height);

            // Attenzione: questa funzione ridimensiona ulteriormente per ottenere un JPEG piccolissimo (base64)
            using var lqipBmp = ResizeBitmapIfNeeded(thumb, AppMediaOptions.LqipMaxLongSidePx);
            using var ms = new MemoryStream();
            lqipBmp.Compress(Bitmap.CompressFormat.Jpeg, AppMediaOptions.LqipJpegQuality, ms);

            var bytes = ms.ToArray();
            if (bytes.Length > AppMediaOptions.MaxLqipBase64Bytes)
                return (null, width, height);

            return (Convert.ToBase64String(bytes), width, height);
        }

        private static string CreateTempJpegPath(string prefix)
        {
            // File temporanei in cache; verranno puliti dal chiamante o dal sistema
            var dir = FileSystem.CacheDirectory;
            return SysPath.Combine(dir, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");
        }
#endif

#if WINDOWS
        // =========================================================
        // WINDOWS IMPLEMENTATION
        // =========================================================

        private static async Task<MediaPreviewResult?> GenerateWindowsImagePreviewAsync(string path, string previewType, CancellationToken ct)
        {
            try
            {
                if (!SysFile.Exists(path)) return null;

                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);

                var (thumbWidth, thumbHeight) = CalculateScaledSize(decoder.PixelWidth, decoder.PixelHeight, AppMediaOptions.ThumbMaxLongSidePx);

                var transform = new BitmapTransform
                {
                    ScaledWidth = thumbWidth,
                    ScaledHeight = thumbHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var thumbPath = CreateTempJpegPath("thumb");

                await SaveJpegAsync(pixelData.DetachPixelData(), thumbWidth, thumbHeight, thumbPath);

                var lqip = await BuildLqipFromFileAsync(thumbPath, ct);

                return new MediaPreviewResult(thumbPath, lqip, (int)thumbWidth, (int)thumbHeight, previewType);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<MediaPreviewResult?> GenerateWindowsVideoPreviewAsync(string path, CancellationToken ct)
        {
            try
            {
                if (!SysFile.Exists(path)) return null;

                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumb = await file.GetThumbnailAsync(
                    ThumbnailMode.VideosView,
                    (uint)AppMediaOptions.ThumbMaxLongSidePx,
                    ThumbnailOptions.UseCurrentScale);

                if (thumb == null) return null;

                var decoder = await BitmapDecoder.CreateAsync(thumb);
                var (thumbWidth, thumbHeight) = CalculateScaledSize(decoder.PixelWidth, decoder.PixelHeight, AppMediaOptions.ThumbMaxLongSidePx);

                var transform = new BitmapTransform
                {
                    ScaledWidth = thumbWidth,
                    ScaledHeight = thumbHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var thumbPath = CreateTempJpegPath("thumb");
                await SaveJpegAsync(pixelData.DetachPixelData(), thumbWidth, thumbHeight, thumbPath);

                var lqip = await BuildLqipFromFileAsync(thumbPath, ct);

                return new MediaPreviewResult(thumbPath, lqip, (int)thumbWidth, (int)thumbHeight, "video_poster");
            }
            catch
            {
                return null;
            }
        }

        private static async Task<MediaPreviewResult?> GenerateWindowsPdfPreviewAsync(string path, CancellationToken ct)
        {
            try
            {
                if (!SysFile.Exists(path)) return null;

                var file = await StorageFile.GetFileFromPathAsync(path);
                var doc = await PdfDocument.LoadFromFileAsync(file);
                if (doc.PageCount == 0) return null;

                using var page = doc.GetPage(0);

                var (thumbWidth, thumbHeight) = CalculateScaledSize((uint)page.Size.Width, (uint)page.Size.Height, AppMediaOptions.ThumbMaxLongSidePx);

                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                var transform = new BitmapTransform
                {
                    ScaledWidth = thumbWidth,
                    ScaledHeight = thumbHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                var thumbPath = CreateTempJpegPath("thumb");
                await SaveJpegAsync(pixelData.DetachPixelData(), thumbWidth, thumbHeight, thumbPath);

                var lqip = await BuildLqipFromFileAsync(thumbPath, ct);

                return new MediaPreviewResult(thumbPath, lqip, (int)thumbWidth, (int)thumbHeight, "pdf_page1");
            }
            catch
            {
                return null;
            }
        }

        // =========================================================
        // WINDOWS HELPERS
        // =========================================================

        private static (uint Width, uint Height) CalculateScaledSize(uint width, uint height, int maxSide)
        {
            if (width == 0 || height == 0) return (1, 1);

            var max = Math.Max(width, height);
            if (max <= maxSide) return (width, height);

            var scale = (double)maxSide / max;
            return ((uint)Math.Max(1, width * scale), (uint)Math.Max(1, height * scale));
        }

        private static async Task SaveJpegAsync(byte[] pixels, uint width, uint height, string path)
        {
            // Crea directory se manca
            Directory.CreateDirectory(SysPath.GetDirectoryName(path) ?? FileSystem.CacheDirectory);

            // Scrive JPEG via BitmapEncoder
            using var file = SysFile.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var stream = file.AsRandomAccessStream();

            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
            await encoder.FlushAsync();
        }

        private static async Task<string?> BuildLqipFromFileAsync(string thumbPath, CancellationToken ct)
        {
            if (!AppMediaOptions.StoreLqipInFirestore)
                return null;

            var file = await StorageFile.GetFileFromPathAsync(thumbPath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);

            var (w, h) = CalculateScaledSize(decoder.PixelWidth, decoder.PixelHeight, AppMediaOptions.LqipMaxLongSidePx);

            var transform = new BitmapTransform
            {
                ScaledWidth = w,
                ScaledHeight = h,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var mem = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, mem);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixelData.DetachPixelData());
            await encoder.FlushAsync();

            mem.Seek(0);
            using var ms = new MemoryStream();
            await mem.AsStream().CopyToAsync(ms, ct);

            var bytes = ms.ToArray();
            if (bytes.Length > AppMediaOptions.MaxLqipBase64Bytes)
                return null;

            return Convert.ToBase64String(bytes);
        }

        private static string CreateTempJpegPath(string prefix)
        {
            var dir = FileSystem.CacheDirectory;
            return SysPath.Combine(dir, $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");
        }
#endif
    }
}
