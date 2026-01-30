using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Devices;
using Biliardo.App.Servizi_Diagnostics;
using Biliardo.App.Pagine_Debug;
using Biliardo.App.Servizi_Sicurezza;
using Biliardo.App.Pagine_Home;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Servizi_Notifiche;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Realtime;
using Biliardo.App.Infrastructure.Sync;
using Plugin.Firebase.CloudMessaging.EventArgs;

namespace Biliardo.App
{
    public partial class App : Application
    {
        private const bool UsaPaginaDebugNotifiche = false;

        // === Service locator minimale per lifecycle events (Android/iOS) ===
        public static IServiceProvider? RootServices { get; private set; }

        private readonly IPushNotificationService? _push;
        private Dictionary<string, string>? _pendingPushData;
        private readonly FetchMissingContentUseCase _fetchMissing = new();

        public App(IServiceProvider services)
        {
            InitializeComponent();

            RootServices = services;

            DiagLog.Init();
            DiagLog.Note("App.Version", AppInfo.VersionString);
            DiagLog.Note("App.Build", AppInfo.BuildString);
            DiagLog.Note("App.StartUtc", DiagLog.StartUtc.ToString("o"));
            DiagLog.Note("Device.Platform", DeviceInfo.Platform.ToString());
            DiagLog.Note("Device.OSVersion", DeviceInfo.VersionString);
            DiagLog.Note("Device.Model", DeviceInfo.Model);
            DiagLog.Note("Device.Manufacturer", DeviceInfo.Manufacturer);
            DiagLog.Note("Navigation.Model", "NavigationPage");
            DiagLog.Step("App.Started");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new Exception("Unhandled non-Exception");
                DiagLog.Exception("UnhandledException", ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                DiagLog.Exception("UnobservedTaskException", e.Exception);
                e.SetObserved();
            };

            _push = services.GetService<IPushNotificationService>();
            if (_push != null)
            {
                _push.NotificationReceived += OnPushNotificationReceived;
                _push.NotificationTapped += OnPushNotificationTapped;
                DiagLog.Note("Push.Service", "Enabled");
            }
            else
            {
                DiagLog.Note("Push.Service", "Disabled");
            }

            // Servizio foreground receipts (delivered) per ✓✓ grigie al mittente
#if DEBUG
            if (UsaPaginaDebugNotifiche)
            {
                MainPage = new NavigationPage(new DebugNotifichePage());
                return;
            }
#endif

            MainPage = new NavigationPage(new Pagina_Login());

            MainThread.BeginInvokeOnMainThread(async () =>
            {
                await BootstrapAndRouteAsync();
            });
        }

        private static bool IsFirebaseProvider(string? provider)
            => string.Equals(provider, "firebase", StringComparison.OrdinalIgnoreCase);

        private async Task<bool> HasUsableSessionAsync(string? provider)
        {
            if (IsFirebaseProvider(provider))
            {
                // Nessun accesso Firestore: solo check locale token/refresh
                var has = await FirebaseSessionePersistente.HaSessioneAsync();
                var uid = FirebaseSessionePersistente.GetLocalId();
                return has && !string.IsNullOrWhiteSpace(uid);
            }

            // Provider legacy/API
            return await SessionePersistente.HaTokenAsync();
        }

        private async Task BootstrapAndRouteAsync()
        {
            try
            {
                var provider = await SessionePersistente.GetProviderAsync();
                DiagLog.Note("Auth.Provider", provider ?? "");

                var hasSession = await HasUsableSessionAsync(provider);
                DiagLog.Note("Auth.HasSession", hasSession.ToString());

                if (hasSession)
                {
                    // NOTA: finché siamo in login/biometria, NON avviamo servizi che toccano Firestore.
                    var count = await SessionePersistente.IncrementaAccessoSeLoggatoAsync();
                    DiagLog.Note("Auth.AccessCount", count.ToString());

                    var needBio = await SessionePersistente.DeveRichiedereBiometriaAsync();
                    DiagLog.Note("Auth.NeedBiometric", needBio.ToString());

                    if (!needBio)
                    {
                        Application.Current.MainPage = new NavigationPage(new Pagina_Home());
                        DiagLog.Step("Navigation", "AutoToHome");

                        await TryHandlePendingPushAsync();
                        return;
                    }
                }

                // Nessuna sessione (o biometria richiesta): stay/login e nessun accesso a Firestore
                Application.Current.MainPage = new NavigationPage(new Pagina_Login());
                DiagLog.Step("Navigation", "ToLogin");

                await TryHandlePendingPushAsync();
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Auth.BootstrapAndRoute", ex);
                Application.Current.MainPage = new NavigationPage(new Pagina_Login());
                await TryHandlePendingPushAsync();
            }
        }

        private void OnPushNotificationReceived(object? sender, FCMNotificationReceivedEventArgs e)
        {
            try
            {
                var n = e.Notification;
                DiagLog.Step("Push", "ReceivedForeground");
                DiagLog.Note("Push.Title", n?.Title ?? "");
                DiagLog.Note("Push.Body", n?.Body ?? "");

                if (n?.Data != null && n.Data.Count > 0)
                {
                    DiagLog.Note("Push.Data", string.Join(";", n.Data.Select(kv => $"{kv.Key}={kv.Value}")));

                    // FIX CS1503: IDictionary<string,string> -> IReadOnlyDictionary<string,string>
                    var ro = n.Data.ToDictionary(kv => kv.Key, kv => kv.Value);
                    _ = Task.Run(() => PushCacheUpdater.UpdateAsync(ro, CancellationToken.None));
                    _ = Task.Run(() => EnqueueMissingIfNeededAsync(ro));
                    PublishRealtimeFromPush(ro);
                }
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Push.Received", ex);
            }
        }

        private void OnPushNotificationTapped(object? sender, FCMNotificationTappedEventArgs e)
        {
            try
            {
                var n = e.Notification;
                DiagLog.Step("Push", "Tapped");
                DiagLog.Note("Push.Title", n?.Title ?? "");
                DiagLog.Note("Push.Body", n?.Body ?? "");

                _pendingPushData = (n?.Data ?? new Dictionary<string, string>())
                    .ToDictionary(k => k.Key, v => v.Value);

                if (_pendingPushData.Count > 0)
                {
                    DiagLog.Note("Push.Data", string.Join(";", _pendingPushData.Select(kv => $"{kv.Key}={kv.Value}")));
                    _ = Task.Run(() => PushCacheUpdater.UpdateAsync(_pendingPushData, CancellationToken.None));
                    _ = Task.Run(() => EnqueueMissingIfNeededAsync(_pendingPushData));
                }
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Push.Tapped", ex);
            }

            MainThread.BeginInvokeOnMainThread(async () => await TryHandlePendingPushAsync());
        }

        private async Task TryHandlePendingPushAsync()
        {
            var data = _pendingPushData;
            if (data == null || data.Count == 0)
                return;

            _pendingPushData = null;

            // IMPORTANT: non gestire push se non c’è sessione valida
            try
            {
                var provider = await SessionePersistente.GetProviderAsync();
                if (!await HasUsableSessionAsync(provider))
                    return;
            }
            catch
            {
                return;
            }

            if (Application.Current?.MainPage is not NavigationPage nav)
                return;

            try
            {
                var kind = data.TryGetValue("kind", out var k) ? k : "";
                if (string.Equals(kind, "home_post", StringComparison.OrdinalIgnoreCase))
                {
                    await NavigateToHomePostAsync(nav, data);
                    return;
                }

                if (string.Equals(kind, "private_message", StringComparison.OrdinalIgnoreCase))
                {
                    await NavigateToChatAsync(nav, data);
                    return;
                }

                if (string.Equals(kind, "document", StringComparison.OrdinalIgnoreCase))
                {
                    await NavigateToHomeAsync(nav);
                    return;
                }
            }
            catch (Exception ex)
            {
                DiagLog.Exception("Push.Navigation", ex);
            }
        }

        private async Task EnqueueMissingIfNeededAsync(IReadOnlyDictionary<string, string> data)
        {
            if (!PushPayloadValidator.IsPayloadComplete(data, out var kind, out var contentId))
            {
                if (!PushPayloadValidator.TryGetContentId(data, out contentId))
                    return;

                await _fetchMissing.EnqueueAsync(contentId, string.IsNullOrWhiteSpace(kind) ? "unknown" : kind, data, priority: 10, CancellationToken.None);
            }
        }

        private static async Task NavigateToHomeAsync(NavigationPage nav)
        {
            var pageType = typeof(App).Assembly.GetType("Biliardo.App.Pagine_Home.Pagina_Home");
            if (pageType == null)
                return;

            if (nav.CurrentPage?.GetType() == pageType)
                return;

            var pageObj = Activator.CreateInstance(pageType) as Page;
            if (pageObj == null)
                return;

            await nav.PushAsync(pageObj);
        }

        private static async Task NavigateToHomePostAsync(NavigationPage nav, IReadOnlyDictionary<string, string> data)
        {
            await NavigateToHomeAsync(nav);

            if (!PushPayloadValidator.TryGetContentId(data, out var contentId))
                return;

            if (nav.CurrentPage is Pagina_Home home)
                await home.ScrollToPostIdAsync(contentId);
        }

        private static async Task NavigateToChatAsync(NavigationPage nav, IReadOnlyDictionary<string, string> data)
        {
            var peerUid = data.TryGetValue("peerUid", out var peer) ? peer : null;
            if (string.IsNullOrWhiteSpace(peerUid) && data.TryGetValue("fromUid", out var fromUid))
                peerUid = fromUid;

            if (string.IsNullOrWhiteSpace(peerUid))
            {
                var pageType = typeof(App).Assembly.GetType("Biliardo.App.Pagine_Messaggi.Pagina_MessaggiLista");
                if (pageType == null)
                    return;

                var pageObj = Activator.CreateInstance(pageType) as Page;
                if (pageObj == null)
                    return;

                await nav.PushAsync(pageObj);
                return;
            }

            await nav.PushAsync(new Pagina_Messaggi.Pagina_MessaggiDettaglio(peerUid, peerUid));
        }

        private static void PublishRealtimeFromPush(IReadOnlyDictionary<string, string> data)
        {
            if (data == null || data.Count == 0)
                return;

            var kind = data.TryGetValue("kind", out var k) ? k : "";
            if (string.Equals(kind, "private_message", StringComparison.OrdinalIgnoreCase))
            {
                BusEventiRealtime.Instance.PublishChatMessage(data);
                return;
            }

            if (string.Equals(kind, "home_post", StringComparison.OrdinalIgnoreCase))
            {
                BusEventiRealtime.Instance.PublishHomePost(data);
                return;
            }

            // fallback: se non conosco il tipo, non pubblico
        }
    }
}
