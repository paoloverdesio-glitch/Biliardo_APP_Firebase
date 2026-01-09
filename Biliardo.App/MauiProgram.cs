using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Controls.Hosting; // <-- necessario per ConfigureEffects

using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Effects;

#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
using Plugin.Firebase.CloudMessaging;
#endif

#if IOS
using Plugin.Firebase.Core.Platforms.iOS;
using Plugin.Firebase.CloudMessaging;
#endif

namespace Biliardo.App
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // === Servizi Firebase chat / delivered receipts ===
            builder.Services.AddSingleton(_ => new FirestoreChatService("biliardoapp"));
            builder.Services.AddSingleton<ForegroundDeliveredReceiptsService>();

            // === Registrazione Effect (NO attributi [assembly: ...]) ===
            builder.ConfigureEffects(effects =>
            {
#if ANDROID
                effects.Add<TouchEffect, PlatformTouchEffectAndroid>();
#elif WINDOWS
                effects.Add<TouchEffect, PlatformTouchEffectWindows>();
#endif
            });

            // Inizializzazione Firebase + lifecycle foreground/background
            builder.ConfigureLifecycleEvents(events =>
            {
#if ANDROID
                events.AddAndroid(android =>
                {
                    android.OnCreate((activity, savedInstanceState) =>
                    {
                        CrossFirebase.Initialize(activity);
                    });

                    android.OnResume(activity => App.SetForegroundState(true));
                    android.OnPause(activity => App.SetForegroundState(false));
                });
#elif IOS
                events.AddiOS(ios =>
                {
                    ios.WillFinishLaunching((app, options) =>
                    {
                        CrossFirebase.Initialize();
                        FirebaseCloudMessagingImplementation.Initialize();
                        return false;
                    });

                    ios.OnActivated(app => App.SetForegroundState(true));
                    ios.OnResignActivation(app => App.SetForegroundState(false));
                    ios.DidEnterBackground(app => App.SetForegroundState(false));
                    ios.WillEnterForeground(app => App.SetForegroundState(true));
                });
#endif
            });

#if ANDROID || IOS
            builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
            builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
#endif

            return builder.Build();
        }
    }
}
