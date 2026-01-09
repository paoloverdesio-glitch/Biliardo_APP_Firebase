using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.EventArgs;
using Biliardo.App.Pagine_Messaggi; // Pagina_MessaggiLista

namespace Biliardo.App.Pagine_Debug
{
    public partial class DebugNotifichePage : ContentPage
    {
        private bool _tapNavigating;
        private DateTimeOffset _lastTapUtc = DateTimeOffset.MinValue;

        public DebugNotifichePage()
        {
            InitializeComponent();

            AppendLine("Debug notifiche FCM avviato.");

            // Sottoscrizione agli eventi del plugin (senza DI)
            CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;
            CrossFirebaseCloudMessaging.Current.NotificationTapped += OnNotificationTapped;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            // Cleanup handler per evitare leak
            CrossFirebaseCloudMessaging.Current.NotificationReceived -= OnNotificationReceived;
            CrossFirebaseCloudMessaging.Current.NotificationTapped -= OnNotificationTapped;
        }

        // BUTTON: Leggi token FCM
        private async void OnLeggiTokenClicked(object sender, EventArgs e)
        {
            try
            {
                AppendLine("Richiesta token FCM...");

                // Verifica permessi / Play Services
                await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();

                var token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();

                AppendLine("TOKEN FCM:");
                AppendLine(token ?? "<null>");

                // Copia automatica negli appunti
                if (!string.IsNullOrWhiteSpace(token))
                {
                    await Clipboard.Default.SetTextAsync(token);
                    AppendLine("Token copiato negli appunti.");
                }
                else
                {
                    AppendLine("Token vuoto: niente da copiare.");
                }
            }
            catch (Exception ex)
            {
                AppendLine($"Errore GetTokenAsync: {ex.Message}");
            }
        }

        // BUTTON: Simula NotificationReceived
        private void OnSimulaNotificationReceivedClicked(object sender, EventArgs e)
        {
            var fake = new FCMNotification(
                title: "TEST RECEIVED",
                body: "Questa   una notifica simulata (received).",
                data: new Dictionary<string, string>
                {
                    ["source"] = "debug",
                    ["kind"] = "received"
                });

            var args = new FCMNotificationReceivedEventArgs(fake);
            OnNotificationReceived(this, args);
        }

        // BUTTON: Simula NotificationTapped
        private void OnSimulaNotificationTappedClicked(object sender, EventArgs e)
        {
            var fake = new FCMNotification(
                title: "TEST TAPPED",
                body: "Questa   una notifica simulata (tapped).",
                data: new Dictionary<string, string>
                {
                    ["source"] = "debug",
                    ["kind"] = "tapped"
                });

            var args = new FCMNotificationTappedEventArgs(fake);
            OnNotificationTapped(this, args);
        }

        // BUTTON: Pulisci log
        private void OnPulisciLogClicked(object sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (LblOutput != null)
                    LblOutput.Text = string.Empty;
            });
        }

        // Handler effettivo di NotificationReceived dal plugin
        private void OnNotificationReceived(object sender, FCMNotificationReceivedEventArgs e)
        {
            var n = e.Notification;

            AppendLine($"[RECEIVED] Title='{n.Title}' Body='{n.Body}'");

            if (n.Data is { Count: > 0 })
            {
                var data = string.Join(", ", n.Data.Select(kv => $"{kv.Key}={kv.Value}"));
                AppendLine($"  Data: {data}");
            }
        }

        // Handler effettivo di NotificationTapped dal plugin
        private void OnNotificationTapped(object sender, FCMNotificationTappedEventArgs e)
        {
            var n = e.Notification;

            AppendLine($"[TAPPED] Title='{n.Title}' Body='{n.Body}'");

            if (n.Data is { Count: > 0 })
            {
                var data = string.Join(", ", n.Data.Select(kv => $"{kv.Key}={kv.Value}"));
                AppendLine($"  Data: {data}");
            }

            // Anti-doppio tap ravvicinato
            var nowUtc = DateTimeOffset.UtcNow;
            if (nowUtc - _lastTapUtc < TimeSpan.FromSeconds(2))
            {
                AppendLine("[TAPPED] Ignorato (tap duplicato ravvicinato).");
                return;
            }
            _lastTapUtc = nowUtc;

            // Navigazione sul thread UI
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (_tapNavigating) return;
                _tapNavigating = true;

                try
                {
                    var nav = Application.Current?.MainPage?.Navigation;
                    if (nav == null)
                    {
                        AppendLine("[TAPPED] Navigation non disponibile (MainPage nulla).");
                        return;
                    }

                    // Se siamo già sulla lista messaggi, non fare nulla
                    var current = nav.NavigationStack.LastOrDefault();
                    if (current is Pagina_MessaggiLista)
                    {
                        AppendLine("[TAPPED] Già su Pagina_MessaggiLista.");
                        return;
                    }

                    // Apri la lista chat come target standard del tap
                    await nav.PushAsync(new Pagina_MessaggiLista());
                    AppendLine("[TAPPED] Navigazione -> Pagina_MessaggiLista.");
                }
                catch (Exception ex)
                {
                    AppendLine($"[TAPPED] Errore navigazione: {ex.Message}");
                }
                finally
                {
                    _tapNavigating = false;
                }
            });
        }

        // Append di una riga al log (sempre sul thread UI)
        private void AppendLine(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss}  {message}";

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (LblOutput == null)
                    return;

                LblOutput.Text = string.IsNullOrWhiteSpace(LblOutput.Text)
                    ? line
                    : line + Environment.NewLine + LblOutput.Text;
            });
        }
    }
}
