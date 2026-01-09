#pragma warning disable IDE0130 // Namespace non coerente con struttura cartelle (scelta intenzionale in MAUI)

using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Plugin.Firebase.CloudMessaging;

namespace Biliardo.App
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges =
            ConfigChanges.ScreenSize
            | ConfigChanges.Orientation
            | ConfigChanges.UiMode
            | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize
            | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        private const int RequestPostNotificationsCode = 1001;
        private const string DefaultAndroidChannelId = "biliardo_default";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Colore barra di stato allineato con il top del gradiente Home (#00100A)
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                var window = Window;
                window.ClearFlags(WindowManagerFlags.TranslucentStatus);
                window.AddFlags(WindowManagerFlags.DrawsSystemBarBackgrounds);
                window.SetStatusBarColor(Android.Graphics.Color.ParseColor("#00100A"));
            }

            EnsureNotificationChannel();
            RequestPostNotificationsIfNeeded();

            // Gestisce il tap su notifica quando l'app viene lanciata "da fredda"
            FirebaseCloudMessagingImplementation.OnNewIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);

            if (intent != null)
            {
                // Gestisce il tap su notifica quando l'app è già in memoria (SingleTop)
                FirebaseCloudMessagingImplementation.OnNewIntent(intent);
            }
        }

        private void EnsureNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return;

            var mgr = (NotificationManager?)GetSystemService(NotificationService);
            if (mgr == null)
                return;

            var existing = mgr.GetNotificationChannel(DefaultAndroidChannelId);
            if (existing != null)
                return;

            var channel = new NotificationChannel(
                DefaultAndroidChannelId,
                "Biliardo",
                NotificationImportance.High)
            {
                Description = "Notifiche Biliardo"
            };

            mgr.CreateNotificationChannel(channel);
        }

        private void RequestPostNotificationsIfNeeded()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu)
                return;

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.PostNotifications) == Permission.Granted)
                return;

            ActivityCompat.RequestPermissions(
                this,
                new[] { Manifest.Permission.PostNotifications },
                RequestPostNotificationsCode);
        }
    }
}
