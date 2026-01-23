using System;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Servizi_Sicurezza
{
    /// <summary>
    /// Stato locale (provider-agnostico):
    /// - Contatore accessi "senza biometria" (Preferences)
    /// - Flag "ha mai fatto login" (Preferences)
    /// - Sessione valida:
    ///     * Firebase -> idToken valido + uid presente (FirebaseSessionePersistente)
    ///     * API legacy -> auth_token su SecureStorage (compatibilità)
    /// </summary>
    public static class SessionePersistente
    {
        public const int AccessiConsentitiSenzaBiometria = 500000;

        private const string K_AuthProvider = "auth_provider"; // "firebase" | "api" (legacy)
        private const string K_ApiAccessToken = "auth_token";  // legacy

        private const string K_HasLoggedIn = "auth.hasLoggedIn";
        private const string K_AccessCount = "auth.accessCountSinceBiometric";

        public static bool HaMaiFattoLogin()
            => Preferences.Default.Get(K_HasLoggedIn, false);

        public static int GetConteggioAccessiSenzaBiometria()
            => Preferences.Default.Get(K_AccessCount, 0);

        public static async Task<string> GetProviderAsync()
        {
            try
            {
                var v = await SecureStorage.Default.GetAsync(K_AuthProvider);
                v = (v ?? "").Trim().ToLowerInvariant();
                return (v == "api") ? "api" : "firebase";
            }
            catch
            {
                return "firebase";
            }
        }

        /// <summary>
        /// True se esiste una sessione valida (Firebase) o token legacy (API).
        /// </summary>
        public static async Task<bool> HaTokenAsync()
        {
            var provider = await GetProviderAsync();

            if (provider == "firebase")
            {
                try
                {
                    var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                    var uid = FirebaseSessionePersistente.GetLocalId();
                    return !string.IsNullOrWhiteSpace(idToken) && !string.IsNullOrWhiteSpace(uid);
                }
                catch
                {
                    return false;
                }
            }

            // Legacy API
            var token = await TryGetSecureAsync(K_ApiAccessToken);
            return !string.IsNullOrWhiteSpace(token);
        }

        /// <summary>
        /// Da chiamare dopo un login riuscito (qualunque provider):
        /// - marca "ha fatto login"
        /// - azzera contatore accessi
        /// </summary>
        public static Task SalvaLoginAsync(string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("accessToken vuoto", nameof(accessToken));

            Preferences.Default.Set(K_HasLoggedIn, true);
            Preferences.Default.Set(K_AccessCount, 0);
            return Task.CompletedTask;
        }

        public static Task LogoutAsync()
        {
            Preferences.Default.Set(K_AccessCount, 0);
            return Task.CompletedTask;
        }

        public static async Task<int> IncrementaAccessoSeLoggatoAsync()
        {
            if (!await HaTokenAsync())
                return GetConteggioAccessiSenzaBiometria();

            var current = Preferences.Default.Get(K_AccessCount, 0);
            current++;
            Preferences.Default.Set(K_AccessCount, current);
            return current;
        }

        public static void ResetDopoBiometriaOk()
        {
            Preferences.Default.Set(K_AccessCount, 0);
        }

        public static async Task<bool> DeveRichiedereBiometriaAsync()
        {
            if (!HaMaiFattoLogin())
                return false;

            if (!await HaTokenAsync())
                return false;

            var count = GetConteggioAccessiSenzaBiometria();
            return count >= AccessiConsentitiSenzaBiometria;
        }

        private static async Task<string?> TryGetSecureAsync(string key)
        {
            try
            {
                return await SecureStorage.Default.GetAsync(key);
            }
            catch
            {
                return null;
            }
        }
    }
}
