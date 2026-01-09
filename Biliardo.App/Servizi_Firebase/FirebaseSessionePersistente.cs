using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Stato locale sessione Firebase Auth (email/password):
    /// - idToken + refreshToken in SecureStorage (sensibile)
    /// - metadati (uid/email/displayName/scadenza) in Preferences
    ///
    /// Nota CRITICA:
    /// - Per Firestore con Security Rules va usato l'ID TOKEN (id_token), NON l'access_token.
    /// </summary>
    public static class FirebaseSessionePersistente
    {
        // === SecureStorage (sensibile) - CANONICHE ===
        private const string K_IdToken = "fb_id_token";
        private const string K_RefreshToken = "fb_refresh_token";

        // === SecureStorage (sensibile) - LEGACY (punti) ===
        private const string K_IdToken_Legacy = "fb.idToken";
        private const string K_RefreshToken_Legacy = "fb.refreshToken";

        // === Preferences (non sensibile) - CANONICHE ===
        private const string K_LocalId = "fb_local_id";
        private const string K_Email = "fb_email";
        private const string K_DisplayName = "fb_display_name";
        private const string K_ExpiresAtUtcTicks = "fb_expires_at_utc_ticks";

        private static readonly SemaphoreSlim _refreshLock = new(1, 1);

        public static async Task<bool> HaSessioneAsync()
        {
            var id = await TryGetSecureAnyAsync(K_IdToken, K_IdToken_Legacy);
            var rt = await TryGetSecureAnyAsync(K_RefreshToken, K_RefreshToken_Legacy);
            return !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(rt);
        }

        public static string? GetLocalId() => Preferences.Default.Get<string?>(K_LocalId, null);
        public static string? GetEmail() => Preferences.Default.Get<string?>(K_Email, null);
        public static string? GetDisplayName() => Preferences.Default.Get<string?>(K_DisplayName, null);

        public static async Task SalvaSessioneAsync(FirebaseAuthClient.SignInUpResponse res)
        {
            if (res == null) throw new ArgumentNullException(nameof(res));
            if (string.IsNullOrWhiteSpace(res.IdToken)) throw new ArgumentException("IdToken vuoto", nameof(res));
            if (string.IsNullOrWhiteSpace(res.RefreshToken)) throw new ArgumentException("RefreshToken vuoto", nameof(res));

            // Scrivo le chiavi CANONICHE
            await SecureStorage.Default.SetAsync(K_IdToken, res.IdToken);
            await SecureStorage.Default.SetAsync(K_RefreshToken, res.RefreshToken);

            // Pulizia eventuali legacy (non critico)
            try { await SecureStorage.Default.SetAsync(K_IdToken_Legacy, ""); } catch { }
            try { await SecureStorage.Default.SetAsync(K_RefreshToken_Legacy, ""); } catch { }

            if (!string.IsNullOrWhiteSpace(res.LocalId)) Preferences.Default.Set(K_LocalId, res.LocalId);
            else Preferences.Default.Remove(K_LocalId);

            if (!string.IsNullOrWhiteSpace(res.Email)) Preferences.Default.Set(K_Email, res.Email);
            else Preferences.Default.Remove(K_Email);

            if (!string.IsNullOrWhiteSpace(res.DisplayName)) Preferences.Default.Set(K_DisplayName, res.DisplayName);
            else Preferences.Default.Remove(K_DisplayName);

            Preferences.Default.Set(K_ExpiresAtUtcTicks, ComputeExpiresAtUtcTicks(res.ExpiresIn));
        }

        public static async Task LogoutAsync()
        {
            try { await SecureStorage.Default.SetAsync(K_IdToken, ""); } catch { }
            try { await SecureStorage.Default.SetAsync(K_RefreshToken, ""); } catch { }
            try { await SecureStorage.Default.SetAsync(K_IdToken_Legacy, ""); } catch { }
            try { await SecureStorage.Default.SetAsync(K_RefreshToken_Legacy, ""); } catch { }

            Preferences.Default.Remove(K_LocalId);
            Preferences.Default.Remove(K_Email);
            Preferences.Default.Remove(K_DisplayName);
            Preferences.Default.Remove(K_ExpiresAtUtcTicks);
        }

        /// <summary>
        /// Ritorna un idToken valido. Se è prossimo alla scadenza, esegue refresh via SecureToken API.
        /// </summary>
        public static async Task<string?> GetIdTokenValidoAsync(CancellationToken ct = default)
        {
            var idToken = await TryGetSecureAnyAsync(K_IdToken, K_IdToken_Legacy);
            var refreshToken = await TryGetSecureAnyAsync(K_RefreshToken, K_RefreshToken_Legacy);

            if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(refreshToken))
                return null;

            if (!TokenStaPerScadere())
                return idToken;

            await _refreshLock.WaitAsync(ct);
            try
            {
                // rileggi dopo lock
                idToken = await TryGetSecureAnyAsync(K_IdToken, K_IdToken_Legacy);
                refreshToken = await TryGetSecureAnyAsync(K_RefreshToken, K_RefreshToken_Legacy);

                if (string.IsNullOrWhiteSpace(idToken) || string.IsNullOrWhiteSpace(refreshToken))
                    return null;

                if (!TokenStaPerScadere())
                    return idToken;

                var refresh = await FirebaseAuthClient.RefreshIdTokenAsync(refreshToken, ct);

                // *** CRITICO ***
                // Firestore (con Security Rules) vuole l'ID TOKEN (id_token).
                // Non usare access_token come prima scelta.
                var newIdToken =
                    !string.IsNullOrWhiteSpace(refresh.Id_Token) ? refresh.Id_Token :
                    !string.IsNullOrWhiteSpace(refresh.Access_Token) ? refresh.Access_Token :
                    null;

                if (string.IsNullOrWhiteSpace(newIdToken))
                    throw new InvalidOperationException("Firebase refresh: id_token mancante.");

                var newRefreshToken =
                    !string.IsNullOrWhiteSpace(refresh.Refresh_Token) ? refresh.Refresh_Token : refreshToken;

                await SecureStorage.Default.SetAsync(K_IdToken, newIdToken);
                await SecureStorage.Default.SetAsync(K_RefreshToken, newRefreshToken);

                Preferences.Default.Set(K_ExpiresAtUtcTicks, ComputeExpiresAtUtcTicks(refresh.Expires_In));

                if (!string.IsNullOrWhiteSpace(refresh.User_Id))
                    Preferences.Default.Set(K_LocalId, refresh.User_Id);

                return newIdToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private static bool TokenStaPerScadere()
        {
            var ticks = Preferences.Default.Get<long>(K_ExpiresAtUtcTicks, 0L);
            if (ticks <= 0) return true;

            var exp = new DateTimeOffset(ticks, TimeSpan.Zero);

            // margine sicurezza: refresh se mancano meno di 60s
            return (exp - DateTimeOffset.UtcNow) < TimeSpan.FromSeconds(60);
        }

        private static long ComputeExpiresAtUtcTicks(string? expiresInSecondsString)
        {
            if (!int.TryParse(expiresInSecondsString, out var seconds) || seconds <= 0)
                seconds = 3600;

            // tolgo 60s per margine
            var exp = DateTimeOffset.UtcNow.AddSeconds(seconds - 60);
            return exp.UtcTicks;
        }

        private static async Task<string?> TryGetSecureAnyAsync(string primaryKey, string legacyKey)
        {
            var v = await TryGetSecureAsync(primaryKey);
            if (!string.IsNullOrWhiteSpace(v)) return v;

            v = await TryGetSecureAsync(legacyKey);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        private static async Task<string?> TryGetSecureAsync(string key)
        {
            try
            {
                var v = await SecureStorage.Default.GetAsync(key);
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch
            {
                return null;
            }
        }
    }
}
