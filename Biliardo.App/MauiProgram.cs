using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;
using Microsoft.Maui.Controls.Hosting; // necessario per ConfigureEffects

using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Effects;
using Biliardo.App.Cache_Locale.SQLite;
using CommunityToolkit.Maui;

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

            SQLiteBootstrap.Initialize();

            // FIX MCTME001: UseMauiCommunityToolkitMediaElement() deve essere nella stessa catena di UseMauiApp<App>()
            builder
                .UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                .UseMauiCommunityToolkitMediaElement()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // === Servizi Firebase chat ===
            builder.Services.AddSingleton(_ => new FirestoreChatService("biliardoapp"));

            // Trace numerata (ordine garantito da contatore interno)
            TouchTrace.Log("MauiProgram.CreateMauiApp START");
            TouchTrace.Log("MauiProgram: before ConfigureEffects");

            // === Registrazione Effect (MAUI) ===
            // NOTA: In MAUI è corretta la registrazione via ConfigureEffects.
            builder.ConfigureEffects(effects =>
            {
                TouchTrace.Log("ConfigureEffects ENTER");

#if ANDROID
                TouchTrace.Log("ConfigureEffects: registering TouchEffect -> PlatformTouchEffectAndroid");
                effects.Add<TouchEffect, PlatformTouchEffectAndroid>();
                TouchTrace.Log("ConfigureEffects: registered TouchEffect -> PlatformTouchEffectAndroid");
#elif WINDOWS
                TouchTrace.Log("ConfigureEffects: registering TouchEffect -> PlatformTouchEffectWindows");
                effects.Add<TouchEffect, PlatformTouchEffectWindows>();
                TouchTrace.Log("ConfigureEffects: registered TouchEffect -> PlatformTouchEffectWindows");
#else
                TouchTrace.Log("ConfigureEffects: no platform mapping compiled for this target");
#endif
            });

            TouchTrace.Log("MauiProgram: after ConfigureEffects");

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

                    android.OnResume(activity => { });
                    android.OnPause(activity => { });
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

                    ios.OnActivated(app => { });
                    ios.OnResignActivation(app => { });
                    ios.DidEnterBackground(app => { });
                    ios.WillEnterForeground(app => { });
                });
#endif
            });

#if ANDROID || IOS
            builder.Services.AddSingleton(_ => CrossFirebaseCloudMessaging.Current);
            builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
#endif

            TouchTrace.Log("MauiProgram: builder.Build about to run");
            return builder.Build();
        }
    }
}
