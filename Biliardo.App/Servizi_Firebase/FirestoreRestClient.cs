using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Biliardo.App.RiquadroDebugTrasferimentiFirebase;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Client REST minimale per Cloud Firestore.
    /// Autenticazione: Authorization: Bearer {Firebase ID Token}
    /// Nota: con ID token, Firestore applica le Security Rules.
    /// </summary>
    public static class FirestoreRestClient
    {
        // === CONFIG ===
        // Preso dal google-services.json (project_info.project_id)
        private const string ProjectId = "biliardoapp";

        // Quasi sempre è (default). Se hai creato un DB con ID diverso, si cambia qui.
        private const string DatabaseId = "(default)";

        // HTTP client strumentato per generare i pallini (API transfers).
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            // Label che comparirà nei log/pallini: "Firestore ..."
            var handler = new TransferDebugHttpHandler("Firestore", FirebaseTransferDebugMonitor.Instance);

            var http = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(25)
            };

            return http;
        }

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private static string DocumentsResourceRoot =>
            $"projects/{ProjectId}/databases/{DatabaseId}/documents";

        private static string BaseDocumentsUrl =>
            $"https://firestore.googleapis.com/v1/{DocumentsResourceRoot}";

        // Root runQuery (parent = .../documents)
        private static string RunQueryUrlRoot =>
            $"{BaseDocumentsUrl}:runQuery";

        private static string CommitUrl =>
            $"{BaseDocumentsUrl}:commit";

        private static string BuildDocumentResourceName(string documentPath)
            => $"{DocumentsResourceRoot}/{documentPath.TrimStart('/')}";

        // =========================================================
        // [1] API di base
        // =========================================================

        public static async Task<JsonDocument> GetDocumentAsync(string documentPath, string idToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(documentPath)) throw new ArgumentException("documentPath vuoto", nameof(documentPath));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var url = $"{BaseDocumentsUrl}/{documentPath.TrimStart('/')}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore GET failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");

            return JsonDocument.Parse(body);
        }

        public static async Task<JsonDocument> CreateDocumentAsync(
            string collectionPath,
            string? documentId,
            Dictionary<string, object> fields,
            string idToken,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(collectionPath)) throw new ArgumentException("collectionPath vuoto", nameof(collectionPath));
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var baseUrl = $"{BaseDocumentsUrl}/{collectionPath.TrimStart('/')}";
            var url = string.IsNullOrWhiteSpace(documentId)
                ? baseUrl
                : $"{baseUrl}?documentId={Uri.EscapeDataString(documentId)}";

            var payload = new Dictionary<string, object>
            {
                ["fields"] = fields
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore CREATE failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");

            return JsonDocument.Parse(body);
        }

        public static async Task<JsonDocument> PatchDocumentAsync(
            string documentPath,
            Dictionary<string, object> fields,
            IReadOnlyList<string> updateMaskFieldPaths,
            string idToken,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(documentPath)) throw new ArgumentException("documentPath vuoto", nameof(documentPath));
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            if (updateMaskFieldPaths == null || updateMaskFieldPaths.Count == 0) throw new ArgumentException("updateMask vuota", nameof(updateMaskFieldPaths));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var sb = new StringBuilder($"{BaseDocumentsUrl}/{documentPath.TrimStart('/')}");

            var first = true;
            foreach (var p in updateMaskFieldPaths)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                sb.Append(first ? "?" : "&");
                first = false;
                sb.Append("updateMask.fieldPaths=");
                sb.Append(Uri.EscapeDataString(p));
            }

            if (first)
                throw new ArgumentException("updateMask vuota (tutti i fieldPaths erano vuoti).", nameof(updateMaskFieldPaths));

            var payload = new Dictionary<string, object>
            {
                ["fields"] = fields
            };

            using var req = new HttpRequestMessage(HttpMethod.Patch, sb.ToString());
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore PATCH failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");

            return JsonDocument.Parse(body);
        }

        public static async Task DeleteDocumentAsync(string documentPath, string idToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(documentPath)) throw new ArgumentException("documentPath vuoto", nameof(documentPath));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var url = $"{BaseDocumentsUrl}/{documentPath.TrimStart('/')}";
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore DELETE failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");
        }

        // =========================================================
        // [1.1] Commit con FieldTransforms (increment/arrayUnion)
        // =========================================================

        public sealed record FieldTransform(string FieldPath, Dictionary<string, object> Transform);

        public static FieldTransform TransformIncrement(string fieldPath, long amount)
        {
            return new FieldTransform(fieldPath, new Dictionary<string, object>
            {
                ["increment"] = VInt(amount)
            });
        }

        public static FieldTransform TransformAppendMissingElements(string fieldPath, IEnumerable<object> values)
        {
            return new FieldTransform(fieldPath, new Dictionary<string, object>
            {
                ["appendMissingElements"] = new Dictionary<string, object>
                {
                    ["values"] = values?.ToArray() ?? Array.Empty<object>()
                }
            });
        }

        public static async Task CommitAsync(
            string documentPath,
            IReadOnlyList<FieldTransform> transforms,
            string idToken,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(documentPath)) throw new ArgumentException("documentPath vuoto", nameof(documentPath));
            if (transforms == null || transforms.Count == 0) throw new ArgumentException("transforms vuoti", nameof(transforms));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var docName = BuildDocumentResourceName(documentPath);

            var fieldTransforms = transforms.Select(t =>
            {
                var dict = new Dictionary<string, object>
                {
                    ["fieldPath"] = t.FieldPath
                };

                foreach (var kv in t.Transform)
                    dict[kv.Key] = kv.Value;

                return dict;
            }).ToArray();

            var payload = new Dictionary<string, object>
            {
                ["writes"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["transform"] = new Dictionary<string, object>
                        {
                            ["document"] = docName,
                            ["fieldTransforms"] = fieldTransforms
                        }
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, CommitUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore COMMIT failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");
        }

        /// <summary>
        /// Esegue una StructuredQuery sul parent ROOT (projects/.../documents).
        /// </summary>
        public static async Task<JsonDocument> RunQueryAsync(object structuredQuery, string idToken, CancellationToken ct = default)
        {
            return await RunQueryInternalAsync(structuredQuery, idToken, parentDocumentPath: null, ct);
        }

        /// <summary>
        /// Esegue una StructuredQuery su un parent specifico (document_path), utile per subcollection.
        /// Esempio parentDocumentPath: "chats/{chatId}" -> query su ".../documents/chats/{chatId}:runQuery"
        /// </summary>
        public static async Task<JsonDocument> RunQueryAsync(object structuredQuery, string idToken, string parentDocumentPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(parentDocumentPath))
                throw new ArgumentException("parentDocumentPath vuoto", nameof(parentDocumentPath));

            return await RunQueryInternalAsync(structuredQuery, idToken, parentDocumentPath.Trim().TrimStart('/'), ct);
        }

        private static async Task<JsonDocument> RunQueryInternalAsync(object structuredQuery, string idToken, string? parentDocumentPath, CancellationToken ct)
        {
            if (structuredQuery == null) throw new ArgumentNullException(nameof(structuredQuery));
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var url = string.IsNullOrWhiteSpace(parentDocumentPath)
                ? RunQueryUrlRoot
                : $"{BaseDocumentsUrl}/{parentDocumentPath}:runQuery";

            var payload = new Dictionary<string, object>
            {
                ["structuredQuery"] = structuredQuery
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", idToken);
            req.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Firestore RUNQUERY failed: {(int)resp.StatusCode}. {TryParseGoogleApiError(body) ?? body}");

            return JsonDocument.Parse(body);
        }

        // =========================================================
        // [2] Builder valori Firestore (fields)
        // =========================================================

        public static object VString(string value) => new Dictionary<string, object> { ["stringValue"] = value ?? "" };

        public static object VBool(bool value) => new Dictionary<string, object> { ["booleanValue"] = value };

        public static object VInt(long value) => new Dictionary<string, object> { ["integerValue"] = value.ToString() };

        public static object VDouble(double value) => new Dictionary<string, object> { ["doubleValue"] = value };

        public static object VTimestamp(DateTimeOffset utcTime)
        {
            var s = utcTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
            return new Dictionary<string, object> { ["timestampValue"] = s };
        }

        public static object VArray(params object[] values)
        {
            var arr = new Dictionary<string, object>();
            if (values != null && values.Length > 0)
                arr["values"] = values;

            return new Dictionary<string, object>
            {
                ["arrayValue"] = arr
            };
        }

        public static object VArrayStrings(IEnumerable<string> strings)
        {
            var vals = new List<object>();
            foreach (var s in strings)
                vals.Add(VString(s));

            return VArray(vals.ToArray());
        }

        public static object VNull() => new Dictionary<string, object> { ["nullValue"] = "NULL_VALUE" };

        public static object VMap(Dictionary<string, object> fields)
        {
            fields ??= new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["mapValue"] = new Dictionary<string, object>
                {
                    ["fields"] = fields
                }
            };
        }

        // =========================================================
        // [3] Parse error Google APIs
        // =========================================================

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
    }
}
