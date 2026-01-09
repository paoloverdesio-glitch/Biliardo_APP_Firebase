using System;
using System.Threading.Tasks;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.EventArgs;

namespace Biliardo.App.Servizi_Notifiche
{
    public interface IPushNotificationService
    {
        /// <summary>
        /// Restituisce il token FCM corrente per questo dispositivo/app.
        /// </summary>
        Task<string> GetTokenAsync();

        /// <summary>
        /// Evento invocato quando arriva una nuova notifica push mentre l’app è in foreground.
        /// </summary>
        event EventHandler<FCMNotificationReceivedEventArgs> NotificationReceived;

        /// <summary>
        /// Evento invocato quando l’utente tocca una notifica nell’area notifiche (app attivata da background).
        /// </summary>
        event EventHandler<FCMNotificationTappedEventArgs> NotificationTapped;
    }

    /// <summary>
    /// Implementazione di IPushNotificationService basata su Plugin.Firebase.CloudMessaging.
    /// </summary>
    public class PushNotificationService : IPushNotificationService
    {
        public PushNotificationService()
        {
            // Sottoscrive gli eventi globali del plugin FCM
            CrossFirebaseCloudMessaging.Current.NotificationReceived += OnNotificationReceived;
            CrossFirebaseCloudMessaging.Current.NotificationTapped += OnNotificationTapped;
            // Volendo puoi aggiungere anche TokenChanged, Error, ecc.
        }

        public async Task<string> GetTokenAsync()
        {
            // Verifica validità (chiede permessi se necessario su iOS, controlla Play Services su Android)
            await CrossFirebaseCloudMessaging.Current.CheckIfValidAsync();

            // Ritorna il token FCM corrente
            var token = await CrossFirebaseCloudMessaging.Current.GetTokenAsync();

#if DEBUG
            System.Diagnostics.Debug.WriteLine($"[PushNotificationService] FCM Token: {token}");
#endif

            return token;
        }

        public event EventHandler<FCMNotificationReceivedEventArgs> NotificationReceived;
        public event EventHandler<FCMNotificationTappedEventArgs> NotificationTapped;

        private void OnNotificationReceived(object sender, FCMNotificationReceivedEventArgs e)
        {
#if DEBUG
            var notif = e.Notification;
            System.Diagnostics.Debug.WriteLine(
                $"[PushNotificationService] Notifica ricevuta (foreground): {notif.Title} - {notif.Body}");
#endif
            NotificationReceived?.Invoke(this, e);
        }

        private void OnNotificationTapped(object sender, FCMNotificationTappedEventArgs e)
        {
#if DEBUG
            var notif = e.Notification;
            System.Diagnostics.Debug.WriteLine(
                $"[PushNotificationService] Notifica tappata: {notif.Title} - {notif.Body}");
#endif
            NotificationTapped?.Invoke(this, e);
        }
    }
}
