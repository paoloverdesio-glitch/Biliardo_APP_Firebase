// ============================================================================
// File: FirebaseAuthClient.cs
// Namespace: Biliardo.App.Servizi_Firebase
//
// FIX COMPILAZIONE:
// 1) Aggiunta API di compatibilità: SignUpWithPasswordAsync(...) -> alias di SignUpAsync(...)
// 2) Ripristinate proprietà di compatibilità nel DTO TokenRefreshResponse:
//    Access_Token / Expires_In / Id_Token / Refresh_Token / User_Id / Project_Id / Token_Type
//    così FirebaseSessionePersistente.cs continua a compilare senza modifiche.
// 3) Mapping corretto snake_case (SecureToken) via JsonPropertyName.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.RiquadroDebugTrasferimentiFirebase;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Client minimale per Firebase Auth via REST (Identity Toolkit) + SecureToken.
    /// Supporta:
    /// - SignUp (email/password)
    /// - SignInWithPassword (email/password)
    /// - Lookup (emailVerified, displayName, ecc.)
    /// - SendOobCode (VERIFY_EMAIL, PASSWORD_RESET)
    /// - Refresh idToken via SecureToken API
    /// </summary>
    public static class FirebaseAuthClient
    {
        // Presa dal tuo Platforms/Android/google-services.json -> client[0].api_key[0].current_key
        private const string ApiKey = "AIzaSyALuzYgz9kGXlsc_0WDQ_xGiSTyeWoqlM4";

        private static readonly HttpClient _http = new HttpClient(
            new TransferDebugHttpHandler("Auth", FirebaseTransferDebugMonitor.Instance))
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private static string Itk(string method) =>
            $"https://identitytoolkit.googleapis.com/v1/accounts:{method}?key={ApiKey}";

        private static string SecureToken() =>
            $"https://securetoken.googleapis.com/v1/token?key={ApiKey}";

        // =========================
        // DTO pubblici
        // =========================

        public sealed class SignInUpResponse
        {
            public string Kind { get; set; } = "";
            public string LocalId { get; set; } = "";
            public string Email { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string IdToken { get; set; } = "";
            public string RefreshToken { get; set; } = "";
            public string ExpiresIn { get; set; } = ""; // secondi in stringa
            public bool Registered { get; set; }
        }

        public sealed class GetAccountInfoResponse
        {
            public List<UserInfo> Users { get; set; } = new();
        }

        public sealed class UserInfo
        {
            public string LocalId { get; set; } = "";
            public string Email { get; set; } = "";
            public bool EmailVerified { get; set; }
            public string DisplayName { get; set; } = "";
            public long? CreatedAt { get; set; }
            public long? LastLoginAt { get; set; }
        }

        public sealed class GetOobCodeResponse
        {
            public string Email { get; set; } = "";
        }

        /// <summary>
        /// SecureToken API response (snake_case).
        /// JSON reale: access_token, expires_in, token_type, refresh_token, id_token, user_id, project_id
        /// </summary>
        public sealed class TokenRefreshResponse
        {
            // --- MAPPING CORRETTO (snake_case) ---

            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = "";

            [JsonPropertyName("expires_in")]
            public string ExpiresIn { get; set; } = "";

            [JsonPropertyName("token_type")]
            public string TokenType { get; set; } = "";

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = "";

            [JsonPropertyName("id_token")]
            public string IdToken { get; set; } = "";

            [JsonPropertyName("user_id")]
            public string UserId { get; set; } = "";

            [JsonPropertyName("project_id")]
            public string ProjectId { get; set; } = "";

            // --- COMPATIBILITÀ CON CODICE ESISTENTE (vecchi nomi) ---
            // FirebaseSessionePersistente.cs sta usando questi identificatori.

            [JsonIgnore]
            public string Access_Token { get => AccessToken; set => AccessToken = value; }

            [JsonIgnore]
            public string Expires_In { get => ExpiresIn; set => ExpiresIn = value; }

            [JsonIgnore]
            public string Token_Type { get => TokenType; set => TokenType = value; }

            [JsonIgnore]
            public string Refresh_Token { get => RefreshToken; set => RefreshToken = value; }

            [JsonIgnore]
            public string Id_Token { get => IdToken; set => IdToken = value; }

            [JsonIgnore]
            public string User_Id { get => UserId; set => UserId = value; }

            [JsonIgnore]
            public string Project_Id { get => ProjectId; set => ProjectId = value; }
        }

        // =========================
        // API pubbliche
        // =========================

        public static Task<SignInUpResponse> SignUpAsync(string email, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email vuota", nameof(email));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("password vuota", nameof(password));

            var req = new
            {
                email = email.Trim(),
                password,
                returnSecureToken = true
            };

            return PostJsonAsync<SignInUpResponse>(Itk("signUp"), req, ct);
        }

        // COMPAT: alcuni file chiamano questo nome
        public static Task<SignInUpResponse> SignUpWithPasswordAsync(string email, string password, CancellationToken ct = default)
            => SignUpAsync(email, password, ct);

        public static Task<SignInUpResponse> SignInWithPasswordAsync(string email, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email vuota", nameof(email));
            if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("password vuota", nameof(password));

            var req = new
            {
                email = email.Trim(),
                password,
                returnSecureToken = true
            };

            return PostJsonAsync<SignInUpResponse>(Itk("signInWithPassword"), req, ct);
        }

        public static Task<GetAccountInfoResponse> LookupAsync(string idToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var req = new { idToken };
            return PostJsonAsync<GetAccountInfoResponse>(Itk("lookup"), req, ct);
        }

        public static Task<GetOobCodeResponse> SendEmailVerificationAsync(string idToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idToken)) throw new ArgumentException("idToken vuoto", nameof(idToken));

            var req = new
            {
                requestType = "VERIFY_EMAIL",
                idToken
            };

            return PostJsonAsync<GetOobCodeResponse>(Itk("sendOobCode"), req, ct);
        }

        public static Task<GetOobCodeResponse> SendPasswordResetAsync(string email, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("email vuota", nameof(email));

            var req = new
            {
                requestType = "PASSWORD_RESET",
                email = email.Trim()
            };

            return PostJsonAsync<GetOobCodeResponse>(Itk("sendOobCode"), req, ct);
        }

        public static async Task<TokenRefreshResponse> RefreshIdTokenAsync(string refreshToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(refreshToken)) throw new ArgumentException("refreshToken vuoto", nameof(refreshToken));

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken.Trim()
            });

            using var resp = await _http.PostAsync(SecureToken(), content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var msg = TryParseSecureTokenError(body) ?? TryParseFirebaseError(body) ?? body;
                throw new InvalidOperationException($"FirebaseAuth error: {msg}");
            }

            var data = JsonSerializer.Deserialize<TokenRefreshResponse>(body, JsonOpts);
            if (data == null)
                throw new InvalidOperationException("FirebaseAuth error: refresh risposta vuota.");

            // Normalizzazione: in alcuni casi id_token è presente oltre ad access_token.
            // Se AccessToken è vuoto ma IdToken presente, copiamo.
            if (string.IsNullOrWhiteSpace(data.AccessToken) && !string.IsNullOrWhiteSpace(data.IdToken))
                data.AccessToken = data.IdToken;

            return data;
        }

        // =========================
        // Helper HTTP + parsing error
        // =========================

        private static async Task<TRes> PostJsonAsync<TRes>(string url, object req, CancellationToken ct)
        {
            using var resp = await _http.PostAsJsonAsync(url, req, JsonOpts, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                var ok = JsonSerializer.Deserialize<TRes>(body, JsonOpts);
                return ok ?? throw new InvalidOperationException("FirebaseAuth error: risposta vuota.");
            }

            var msg = TryParseFirebaseError(body) ?? body;
            throw new InvalidOperationException($"FirebaseAuth error: {msg}");
        }

        private static string? TryParseFirebaseError(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    // IdentityToolkit: { "error": { "message": "..." } }
                    if (err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                            return m.GetString();
                    }

                    // SecureToken (a volte): { "error": "invalid_grant", "error_description": "..." }
                    if (err.ValueKind == JsonValueKind.String)
                    {
                        var e = err.GetString();
                        if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                            return $"{e}: {d.GetString()}";
                        return e;
                    }
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string? TryParseSecureTokenError(string body)
        {
            // SecureToken tipico: { "error": "invalid_grant", "error_description": "..." }
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                {
                    var err = e.GetString() ?? "";
                    if (doc.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String)
                        return $"{err}: {d.GetString()}";
                    return err;
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
