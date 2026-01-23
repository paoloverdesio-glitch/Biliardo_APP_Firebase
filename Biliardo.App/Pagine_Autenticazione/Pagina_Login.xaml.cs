﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Biliardo.App.Pagine_Home;
using Biliardo.App.Servizi_Diagnostics;
using Biliardo.App.Servizi_Sicurezza;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Infrastructure;

namespace Biliardo.App.Pagine_Autenticazione
{
    public partial class Pagina_Login : ContentPage
    {
        private bool _busy;
        private bool _pwdVisible = false;

        private TaskCompletionSource<bool>? _popupTcs;
        private bool _showInserisciCredenziali;

        private const string K_AuthProvider = "auth_provider"; // "firebase"

        public Pagina_Login(bool showInserisciCredenziali = false)
        {
            _showInserisciCredenziali = showInserisciCredenziali;
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_showInserisciCredenziali)
            {
                _showInserisciCredenziali = false;
                await ShowPopupAsync("Inserisci credenziali.", "Login");
            }
        }

        private void TogglePassword_Clicked(object sender, EventArgs e)
        {
            _pwdVisible = !_pwdVisible;
            PasswordEntry.IsPassword = !_pwdVisible;
            if (sender is Button b)
                b.Text = _pwdVisible ? "🙈" : "👁";
        }

        private Task ShowPopupAsync(string message, string title)
        {
            PopupTitleLabel.Text = title;
            PopupMessageLabel.Text = message;

            PopupOverlay.IsVisible = true;
            _popupTcs = new TaskCompletionSource<bool>();
            return _popupTcs.Task;
        }

        private void PopupOkButton_Clicked(object sender, EventArgs e)
        {
            PopupOverlay.IsVisible = false;
            _popupTcs?.TrySetResult(true);
            _popupTcs = null;
        }

        // ============================================================
        // Login Firebase (Email+Password) + blocco finché email non verificata
        // ============================================================
        private async void OnLoginFirebaseClicked(object sender, EventArgs e)
        {
            if (_busy) return;

            var email = (NicknameEntry.Text ?? "").Trim();
            var password = PasswordEntry.Text ?? "";

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                await ShowPopupAsync("Inserisci Email e Password.", "Errore");
                return;
            }

            if (!email.Contains("@", StringComparison.Ordinal) || !email.Contains(".", StringComparison.Ordinal))
            {
                await ShowPopupAsync("Formato email non valido.", "Errore");
                return;
            }

            if (Connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                await ShowPopupAsync("Nessuna connessione Internet.", "Errore");
                return;
            }

            _busy = true;
            if (sender is Button b) b.IsEnabled = false;

            try
            {
                DiagLog.Step("Login(Firebase)", "Click");

                // 1) Sign-in
                var signIn = await FirebaseAuthClient.SignInWithPasswordAsync(email, password, CancellationToken.None);

                // 2) Lookup per emailVerified (+ displayName/email/localId aggiornati)
                var info = await FirebaseAuthClient.LookupAsync(signIn.IdToken, CancellationToken.None);
                var user = (info.Users != null && info.Users.Count > 0) ? info.Users[0] : null;

                if (user == null)
                    throw new InvalidOperationException("Firebase: lookup utente fallito (users vuoto).");

                // 3) Se NON verificata: invia mail di verifica e FERMA qui
                if (!user.EmailVerified)
                {
                    try
                    {
                        await FirebaseAuthClient.SendEmailVerificationAsync(signIn.IdToken, CancellationToken.None);
                    }
                    catch (Exception exSend)
                    {
                        DiagLog.Exception("FirebaseSendVerifyEmailError", exSend);
                    }

                    await ShowPopupAsync(
                        "Email non verificata.\n\nHo (ri)inviato l’email di verifica: aprila, conferma e poi rifai login.",
                        "Verifica Email (Firebase)");

                    return;
                }

                // 4) Normalizzazione metadati (se lookup fornisce valori migliori)
                if (!string.IsNullOrWhiteSpace(user.LocalId)) signIn.LocalId = user.LocalId;
                if (!string.IsNullOrWhiteSpace(user.Email)) signIn.Email = user.Email;
                if (!string.IsNullOrWhiteSpace(user.DisplayName)) signIn.DisplayName = user.DisplayName;
                if (string.IsNullOrWhiteSpace(signIn.Email)) signIn.Email = email;

                // 5) Persist sessione Firebase: UNICO punto di verità (token + refresh + uid + scadenza)
                await FirebaseSessionePersistente.SalvaSessioneAsync(signIn);

                // Provider corrente (per routing provider-agnostico)
                await SecureStorage.Default.SetAsync(K_AuthProvider, "firebase");

                // Per coerenza del flusso App.xaml.cs (contatore biometria)
                await SessionePersistente.SalvaLoginAsync(accessToken: "firebase");

                await ShowPopupAsync(
                    $"Firebase OK\nEmail: {FirebaseSessionePersistente.GetEmail()}\nUID: {FirebaseSessionePersistente.GetLocalId()}\nEmail verificata: True",
                    "Login Firebase");

                try
                {
                    var home = new Pagina_Home();
                    Application.Current.MainPage = new NavigationPage(home);
                    DiagLog.Step("Navigation", "ToHome(Firebase)");
                }
                catch (Exception navEx)
                {
                    DiagLog.Exception("NavigationHomeError", navEx);
                    ExceptionFormatter.Log(navEx);
                    var userMsg = ExceptionFormatter.FormatUserMessage(navEx);
#if DEBUG
                    var debug = ExceptionFormatter.FormatDebugDetails(navEx);
                    if (!string.IsNullOrWhiteSpace(debug))
                        userMsg += "\n\n" + debug;
#endif
                    await ShowPopupAsync(userMsg, "Errore Firebase");
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Exception("LoginFirebaseError", ex);
                ExceptionFormatter.Log(ex);
                var userMsg = ExceptionFormatter.FormatUserMessage(ex);
#if DEBUG
                var debug = ExceptionFormatter.FormatDebugDetails(ex);
                if (!string.IsNullOrWhiteSpace(debug))
                    userMsg += "\n\n" + debug;
#endif
                await ShowPopupAsync(userMsg, "Errore Firebase");
            }
            finally
            {
                _busy = false;
                if (sender is Button b2) b2.IsEnabled = true;
            }
        }

        private static string HumanizeFirebaseError(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
                return "Errore Firebase.";

            var msg = rawMessage.Trim();
            const string prefix = "FirebaseAuth error:";
            if (msg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                msg = msg.Substring(prefix.Length).Trim();

            return msg switch
            {
                "EMAIL_NOT_FOUND" => "Email non trovata.",
                "INVALID_PASSWORD" => "Password errata.",
                "USER_DISABLED" => "Account disabilitato.",
                "INVALID_EMAIL" => "Email non valida.",
                "TOO_MANY_ATTEMPTS_TRY_LATER" => "Troppi tentativi. Riprova più tardi.",
                "INVALID_ID_TOKEN" => "Sessione scaduta. Rifai login.",
                _ => rawMessage
            };
        }

        private async void OnVaiARegistrazioneClicked(object sender, EventArgs e)
        {
            try
            {
                await Navigation.PushAsync(new Pagina_Registrazione());
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(ex.Message, "Errore");
            }
        }

        private async void OnSendDiagClicked(object sender, EventArgs e)
        {
            if (_busy) return;

            try
            {
                DiagLog.Step("DiagShare", "Click");
                await DiagMailService.SendNowAsync("Login");
                await ShowPopupAsync("Diagnostica condivisa.", "Diagnostica");
            }
            catch (Exception ex)
            {
                DiagLog.Exception("DiagShareError", ex);
                await ShowPopupAsync($"Condivisione fallita: {ex.Message}", "Diagnostica");
            }
        }
    }
}
