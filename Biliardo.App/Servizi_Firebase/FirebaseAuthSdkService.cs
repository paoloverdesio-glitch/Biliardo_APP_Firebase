using System;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Diagnostics;
using Plugin.Firebase.Auth;

namespace Biliardo.App.Servizi_Firebase
{
    public static class FirebaseAuthSdkService
    {
        public static bool IsSupported => CrossFirebaseAuth.IsSupported;

        public static IFirebaseUser? CurrentUser => CrossFirebaseAuth.Current.CurrentUser;

        public static async Task<bool> EnsureSignedInAsync(
            string email,
            string password,
            CancellationToken ct = default)
        {
            if (!IsSupported)
            {
                DiagLog.Note("Auth.SdkSupported", "false");
                return false;
            }

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                DiagLog.Note("Auth.SdkSignIn", "Skipped: empty credentials");
                return false;
            }

            try
            {
                DiagLog.Step("Auth.SdkSignIn", "Start");
                var user = await CrossFirebaseAuth.Current
                    .SignInWithEmailAndPasswordAsync(email.Trim(), password, false)
                    .ConfigureAwait(false);

                var uid = user?.Uid ?? "";
                DiagLog.Note("Auth.SdkSignIn.Uid", uid);
                DiagLog.Note("Auth.SdkSignIn.EmailVerified", user?.IsEmailVerified.ToString());
                DiagLog.Step("Auth.SdkSignIn", "Ok");
                return !string.IsNullOrWhiteSpace(uid);
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Auth.SdkSignIn", ex);
                return false;
            }
            finally
            {
                if (ct.IsCancellationRequested)
                    DiagLog.Note("Auth.SdkSignIn.Cancelled", "true");
            }
        }

        public static async Task<string?> TryGetIdTokenAsync(bool forceRefresh = false)
        {
            if (!IsSupported)
            {
                DiagLog.Note("Auth.SdkToken", "NotSupported");
                return null;
            }

            try
            {
                var user = CurrentUser;
                if (user == null)
                {
                    DiagLog.Note("Auth.SdkToken", "NoCurrentUser");
                    return null;
                }

                var token = await user.GetIdTokenResultAsync(forceRefresh).ConfigureAwait(false);
                DiagLog.Note("Auth.SdkToken.ExpiryUtc", token?.ExpirationDate.ToString("o"));
                return token?.Token;
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Auth.SdkToken", ex);
                return null;
            }
        }
    }
}
