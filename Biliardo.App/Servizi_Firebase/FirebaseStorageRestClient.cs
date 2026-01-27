using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

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

            var nameEncoded = Uri.EscapeDataString(normalized);
            var url = $"{BaseUrl(bucket)}?uploadType=media&name={nameEncoded}";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            var sc = new StreamContent(content);
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
                throw new InvalidOperationException(
                    $"Storage UPLOAD failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");

            using var doc = JsonDocument.Parse(body);

            var returnedName = ReadString(doc.RootElement, "name") ?? normalized;
            var returnedContentType = ReadString(doc.RootElement, "contentType") ?? contentType;
            var sizeBytes = ReadLongFromString(doc.RootElement, "size");

            var tokens = ReadString(doc.RootElement, "downloadTokens");
            var token = FirstToken(tokens);

            string? downloadUrl = null;
            if (!string.IsNullOrWhiteSpace(token))
                downloadUrl = BuildAnonDownloadUrl(bucket, returnedName, token);

            return new UploadResult(
                StoragePath: returnedName,
                ContentType: returnedContentType,
                SizeBytes: sizeBytes,
                DownloadToken: token,
                DownloadUrl: downloadUrl
            );
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

            contentType ??= GuessContentTypeFromPath(normalized);

            await using var fs = File.OpenRead(localFilePath);
            return await UploadAsync(
                idToken: idToken,
                bucket: bucket,
                storagePath: normalized,
                content: fs,
                contentType: contentType,
                ct: ct);
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
            var url = BuildAuthDownloadUrl(bucket, normalized);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    $"Storage DOWNLOAD failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");
            }

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await src.CopyToAsync(destination, 81920, ct);
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
            await DownloadToStreamAsync(idToken, bucket, objectPath, fs, ct);
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
    }
}
