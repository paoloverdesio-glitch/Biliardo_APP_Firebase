using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.RiquadroDebugTrasferimentiFirebase;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Client REST minimale per Firebase Storage.
    /// Autenticazione: Authorization: Bearer {Firebase ID Token}
    /// Nota: con ID token, Storage applica le Security Rules.
    ///
    /// Upload:
    /// POST https://firebasestorage.googleapis.com/v0/b/{bucket}/o?uploadType=media&name={objectPathUrlEncoded}
    ///
    /// Download (autenticato):
    /// GET  https://firebasestorage.googleapis.com/v0/b/{bucket}/o/{objectPathUrlEncoded}?alt=media
    /// </summary>
    public static class FirebaseStorageRestClient
    {
        // >>> IMPORTANTE <<<
        // Questo valore DEVE corrispondere a Platforms/Android/google-services.json -> project_info.storage_bucket
        // Se cambia, aggiorna SOLO questa costante.
        public const string DefaultStorageBucket = "biliardoapp.firebasestorage.app";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        private static string BaseUrl(string bucket) =>
            $"https://firebasestorage.googleapis.com/v0/b/{bucket}/o";

        public sealed record UploadResult(
            string StoragePath,
            string ContentType,
            long SizeBytes,
            string? DownloadToken,
            string? DownloadUrl
        );

        // =========================================================
        // Upload (API "ricca")
        // =========================================================

        public static Task<UploadResult> UploadAsync(
            string idToken,
            string storagePath,
            Stream content,
            string contentType,
            CancellationToken ct = default)
        {
            return UploadAsync(idToken, DefaultStorageBucket, storagePath, content, contentType, ct);
        }

        public static async Task<UploadResult> UploadAsync(
            string idToken,
            string bucket,
            string storagePath,
            Stream content,
            string contentType,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                throw new ArgumentException("idToken vuoto", nameof(idToken));
            if (string.IsNullOrWhiteSpace(bucket))
                throw new ArgumentException("bucket vuoto", nameof(bucket));
            if (string.IsNullOrWhiteSpace(storagePath))
                throw new ArgumentException("storagePath vuoto", nameof(storagePath));
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            if (string.IsNullOrWhiteSpace(contentType))
                contentType = "application/octet-stream";

            var normalized = NormalizeObjectPath(storagePath);
            var fileName = Path.GetFileName(normalized);
            var totalBytes = content.CanSeek ? Math.Max(0, content.Length - content.Position) : -1;

            return await UploadAsyncInternal(
                idToken,
                bucket,
                normalized,
                content,
                contentType,
                fileName,
                totalBytes,
                ct);
        }

        private static async Task<UploadResult> UploadAsyncInternal(
            string idToken,
            string bucket,
            string normalizedStoragePath,
            Stream content,
            string contentType,
            string fileName,
            long totalBytes,
            CancellationToken ct)
        {
            var monitor = FirebaseTransferDebugMonitor.Instance;
            StorageToken? token = null;

            var nameEncoded = Uri.EscapeDataString(normalizedStoragePath);
            var url = $"{BaseUrl(bucket)}?uploadType=media&name={nameEncoded}";

            try
            {
                token = monitor.BeginStorage(TransferDirection.Up, fileName, normalizedStoragePath, totalBytes);

                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

                var progressStream = new ProgressStream(content, bytes => monitor.ReportStorageProgress(token, bytes));
                var sc = new StreamContent(progressStream);
                sc.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                if (content.CanSeek)
                {
                    var remaining = content.Length - content.Position;
                    if (remaining >= 0)
                        sc.Headers.ContentLength = remaining;
                }

                req.Content = sc;

                using var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    monitor.EndStorage(token, false, TryParseGoogleApiError(body) ?? body);
                    token = null;
                    throw new InvalidOperationException(
                        $"Storage UPLOAD failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");
                }

                using var doc = JsonDocument.Parse(body);

                var returnedName = ReadString(doc.RootElement, "name") ?? normalizedStoragePath;
                var returnedContentType = ReadString(doc.RootElement, "contentType") ?? contentType;
                var sizeBytes = ReadLongFromString(doc.RootElement, "size");

                var tokens = ReadString(doc.RootElement, "downloadTokens");
                var downloadToken = FirstToken(tokens);

                string? downloadUrl = null;
                if (!string.IsNullOrWhiteSpace(downloadToken))
                    downloadUrl = BuildAnonDownloadUrl(bucket, returnedName, downloadToken);

                monitor.EndStorage(token, true, null);
                token = null;

                return new UploadResult(
                    StoragePath: returnedName,
                    ContentType: returnedContentType,
                    SizeBytes: sizeBytes,
                    DownloadToken: downloadToken,
                    DownloadUrl: downloadUrl
                );
            }
            catch (Exception ex)
            {
                if (token != null)
                    monitor.EndStorage(token, false, ex.Message);
                throw;
            }
        }

        public static Task<UploadResult> UploadFileWithResultAsync(
            string idToken,
            string objectPath,
            string localFilePath,
            string? contentType = null,
            CancellationToken ct = default)
        {
            return UploadFileWithResultAsync(idToken, DefaultStorageBucket, objectPath, localFilePath, contentType, ct);
        }

        public static async Task<UploadResult> UploadFileWithResultAsync(
            string idToken,
            string bucket,
            string objectPath,
            string localFilePath,
            string? contentType = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(localFilePath))
                throw new ArgumentException("localFilePath vuoto", nameof(localFilePath));
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException("File non trovato.", localFilePath);

            var normalized = NormalizeObjectPath(objectPath);
            var fileName = Path.GetFileName(localFilePath);
            var totalBytes = new FileInfo(localFilePath).Length;

            contentType ??= GuessContentTypeFromPath(normalized);

            await using var fs = File.OpenRead(localFilePath);
            return await UploadAsyncInternal(
                idToken,
                bucket,
                normalized,
                fs,
                contentType,
                fileName,
                totalBytes,
                ct);
        }

        // =========================================================
        // Upload (API "semplice" - COMPATIBILITÀ)
        // =========================================================
        // Questa firma/ritorno serve se in altri file hai chiamate che si aspettano Task<string>.
        // Ritorna lo StoragePath effettivo (objectPath normalizzato/ritornato dall'API).
        public static async Task<string> UploadFileAsync(
            string idToken,
            string objectPath,
            string localFilePath,
            string contentType,
            CancellationToken ct = default)
        {
            var res = await UploadFileWithResultAsync(
                idToken: idToken,
                objectPath: objectPath,
                localFilePath: localFilePath,
                contentType: contentType,
                ct: ct);

            return res.StoragePath;
        }

        // =========================================================
        // Download
        // =========================================================

        public static Task DownloadToStreamAsync(
            string idToken,
            string objectPath,
            Stream destination,
            CancellationToken ct = default)
        {
            return DownloadToStreamAsync(idToken, DefaultStorageBucket, objectPath, destination, ct);
        }

        public static async Task DownloadToStreamAsync(
            string idToken,
            string bucket,
            string objectPath,
            Stream destination,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken))
                throw new ArgumentException("idToken vuoto", nameof(idToken));
            if (string.IsNullOrWhiteSpace(bucket))
                throw new ArgumentException("bucket vuoto", nameof(bucket));
            if (string.IsNullOrWhiteSpace(objectPath))
                throw new ArgumentException("objectPath vuoto", nameof(objectPath));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            var normalized = NormalizeObjectPath(objectPath);
            var fileName = Path.GetFileName(normalized);
            await DownloadToStreamInternalAsync(idToken, bucket, normalized, fileName, destination, ct);
        }

        private static async Task DownloadToStreamInternalAsync(
            string idToken,
            string bucket,
            string normalizedObjectPath,
            string fileName,
            Stream destination,
            CancellationToken ct)
        {
            var url = BuildAuthDownloadUrl(bucket, normalizedObjectPath);
            var monitor = FirebaseTransferDebugMonitor.Instance;
            StorageToken? token = null;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            try
            {
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    token = monitor.BeginStorage(TransferDirection.Down, fileName, normalizedObjectPath, -1);
                    monitor.EndStorage(token, false, TryParseGoogleApiError(body) ?? body);
                    token = null;
                    throw new InvalidOperationException(
                        $"Storage DOWNLOAD failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");
                }

                var totalBytes = resp.Content.Headers.ContentLength ?? -1;
                token = monitor.BeginStorage(TransferDirection.Down, fileName, normalizedObjectPath, totalBytes);

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    monitor.ReportStorageProgress(token, total);
                }

                monitor.EndStorage(token, true, null);
                token = null;
            }
            catch (Exception ex)
            {
                if (token != null)
                    monitor.EndStorage(token, false, ex.Message);
                throw;
            }
        }

        public static Task<string> DownloadToFileAsync(
            string idToken,
            string objectPath,
            string destinationPath,
            CancellationToken ct = default)
        {
            return DownloadToFileAsync(idToken, DefaultStorageBucket, objectPath, destinationPath, ct);
        }

        public static async Task<string> DownloadToFileAsync(
            string idToken,
            string bucket,
            string objectPath,
            string destinationPath,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("destinationPath vuoto", nameof(destinationPath));

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await using var fs = File.Open(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var fileName = Path.GetFileName(destinationPath);
            await DownloadToStreamInternalAsync(idToken, bucket, NormalizeObjectPath(objectPath), fileName, fs, ct);
            return destinationPath;
        }

        // =========================================================
        // URL helpers
        // =========================================================

        public static string BuildAuthDownloadUrl(string bucket, string objectPath)
        {
            var objEncoded = Uri.EscapeDataString(NormalizeObjectPath(objectPath));
            return $"{BaseUrl(bucket)}/{objEncoded}?alt=media";
        }

        public static string BuildAnonDownloadUrl(string bucket, string objectPath, string downloadToken)
        {
            var objEncoded = Uri.EscapeDataString(NormalizeObjectPath(objectPath));
            return $"{BaseUrl(bucket)}/{objEncoded}?alt=media&token={Uri.EscapeDataString(downloadToken)}";
        }

        public static string GuessContentTypeFromPath(string path)
        {
            var ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext switch
            {
                ".m4a" => "audio/mp4",
                ".aac" => "audio/aac",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                _ => "application/octet-stream"
            };
        }

        // =========================================================
        // Helpers JSON/error
        // =========================================================

        private static string NormalizeObjectPath(string p)
        {
            p = (p ?? "").Trim();
            p = p.TrimStart('/');
            return p;
        }

        private static string? TryParseGoogleApiError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                {
                    string? status = null;
                    string? message = null;

                    if (err.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                        status = st.GetString();

                    if (err.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                        message = msg.GetString();

                    if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(message))
                        return $"{status ?? "ERROR"}: {message ?? ""}".Trim();
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        private static string? ReadString(JsonElement obj, string prop)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(prop, out var p)) return null;
            if (p.ValueKind != JsonValueKind.String) return null;
            return p.GetString();
        }

        private static long ReadLongFromString(JsonElement obj, string prop)
        {
            var s = ReadString(obj, prop);
            if (string.IsNullOrWhiteSpace(s)) return 0;
            return long.TryParse(s, out var v) ? v : 0;
        }

        private static string? FirstToken(string? tokens)
        {
            if (string.IsNullOrWhiteSpace(tokens)) return null;
            var parts = tokens.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length > 0 ? parts[0] : null;
        }

        private sealed class ProgressStream : Stream
        {
            private readonly Stream _inner;
            private readonly Action<long> _progress;
            private long _totalRead;

            public ProgressStream(Stream inner, Action<long> progress)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _progress = progress ?? throw new ArgumentNullException(nameof(progress));
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;

            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                Report(read);
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var read = await _inner.ReadAsync(buffer, cancellationToken);
                Report(read);
                return read;
            }

            public override int ReadByte()
            {
                var b = _inner.ReadByte();
                if (b >= 0)
                    Report(1);
                return b;
            }

            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

            public override void SetLength(long value) => _inner.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
                => _inner.WriteAsync(buffer, cancellationToken);

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    _inner.Dispose();
                base.Dispose(disposing);
            }

            private void Report(int read)
            {
                if (read <= 0) return;
                _totalRead += read;
                _progress(_totalRead);
            }
        }
    }
}
