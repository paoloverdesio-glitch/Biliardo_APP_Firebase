using Biliardo.App.Componenti_UI.Composer;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Infrastructure.Media.Home;
using Biliardo.App.Infrastructure.Media.Processing;
using Biliardo.App.Infrastructure.Home;
using Biliardo.App.Pagine_Autenticazione;
using Biliardo.App.Pagine_Debug;
using Biliardo.App.RiquadroDebugTrasferimentiFirebase;
using Biliardo.App.Pagine_Media;
using Biliardo.App.Servizi_Diagnostics;
using Biliardo.App.Servizi_Firebase;
using Biliardo.App.Servizi_Media;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Realtime;
using Biliardo.App.Utilita;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using Microsoft.Maui.Media;
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel.Communication;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
#if WINDOWS
using WindowsMediaSource = Windows.Media.Core.MediaSource;
using Windows.Media.Playback;
#endif

namespace Biliardo.App.Pagine_Home
{
    public partial class Pagina_Home
    {
        private async void OnMenuLaterale_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleMenuAsync();
        }

        private async Task ToggleMenuAsync()
        {
            var overlayMenu = this.FindByName<Grid>("overlay_menu");
            var menuPanel = this.FindByName<VisualElement>("menu_panel");

            if (overlayMenu == null || menuPanel == null)
                return;

            if (_menuAperto)
            {
                _menuAperto = false;
                await Task.WhenAll(
                    menuPanel.TranslateTo(-menuPanel.Width, 0, 250, Easing.CubicIn),
                    overlayMenu.FadeTo(0, 250, Easing.CubicInOut)
                );
                overlayMenu.IsVisible = false;
            }
            else
            {
                overlayMenu.IsVisible = true;
                overlayMenu.Opacity = 0;
                menuPanel.TranslationX = -menuPanel.Width;

                await Task.WhenAll(
                    menuPanel.TranslateTo(0, 0, 250, Easing.CubicOut),
                    overlayMenu.FadeTo(1, 250, Easing.CubicInOut)
                );

                _menuAperto = true;
            }
        }

        private async void OnMenuVoice(object? sender, EventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async void OnMenuVoice(object? sender, TappedEventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async Task HandleMenuVoiceAsync(object? sender)
        {
            string? voce = null;
            if (sender is Label lbl) voce = lbl.Text;
            else if (sender is Button btn) voce = btn.Text;

            voce ??= "free";
            voce = voce.Trim();

            if (_menuAperto)
            {
                await ToggleMenuAsync();
            }

            var page = new ContentPage
            {
                Title = voce,
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16),
                    Children =
                    {
                        new Label
                        {
                            Text = $"Pagina {voce} in sviluppo.",
                            HorizontalOptions = LayoutOptions.Center,
                            VerticalOptions = LayoutOptions.Center,
                            TextColor = Colors.White
                        }
                    }
                },
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Colors.Black, 0f),
                        new GradientStop(Color.FromArgb("#003020"), 0.6f),
                        new GradientStop(Color.FromArgb("#00452A"), 1f)
                    },
                    new Point(0, 0),
                    new Point(0, 1))
            };

            await Navigation.PushAsync(page);
        }

        private async void OnLogoutMenu(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async void OnLogoutMenu_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async Task ToggleLogoutMenuAsync()
        {
            var overlayLogout = this.FindByName<Grid>("overlay_logout_menu");
            var logoutPanel = this.FindByName<VisualElement>("logout_menu_panel");

            if (overlayLogout == null || logoutPanel == null)
                return;

            if (_logoutMenuAperto)
            {
                _logoutMenuAperto = false;
                await Task.WhenAll(
                    logoutPanel.TranslateTo(logoutPanel.Width, 0, 250, Easing.CubicIn),
                    overlayLogout.FadeTo(0, 250, Easing.CubicInOut)
                );
                overlayLogout.IsVisible = false;
            }
            else
            {
                overlayLogout.IsVisible = true;
                overlayLogout.Opacity = 0;
                logoutPanel.TranslationX = logoutPanel.Width;

                await Task.WhenAll(
                    logoutPanel.TranslateTo(0, 0, 250, Easing.CubicOut),
                    overlayLogout.FadeTo(1, 250, Easing.CubicInOut)
                );

                _logoutMenuAperto = true;
            }
        }

        private async void OnLogoutInfoClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await ShowInfoBiliardoAppAsync();
        }

        private async void OnInfoCacheClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }

            await Navigation.PushAsync(new InfoCachePage());
        }

        private async void OnLogoutLogClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }

            await Navigation.PushAsync(new LogTrasferimentiPage());
        }

        private async void OnLogoutExitClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await EseguiLogoutAsync();
        }

        private Task ShowPopupAsync(string message, string title)
        {
            PopupTitleLabel.Text = title;
            PopupMessageLabel.Text = message;

            PopupOverlay.IsVisible = true;
            _popupTcs = new TaskCompletionSource<bool>();
            return _popupTcs.Task;
        }

        private Task ShowServerErrorPopupAsync(string title, Exception ex)
        {
            var message = FormatExceptionForPopup(ex);
            return PopupErrorHelper.ShowAsync(this, title, message);
        }

        private void OnPopupOkClicked(object? sender, EventArgs e)
        {
            PopupOverlay.IsVisible = false;
            _popupTcs?.TrySetResult(true);
            _popupTcs = null;
        }

        private async Task ShowInfoBiliardoAppAsync()
        {
            static string SafeValue(string? value) =>
                string.IsNullOrWhiteSpace(value) ? "n/d" : value;

            var appName = string.IsNullOrWhiteSpace(AppInfo.Current.Name)
                ? "BiliardoApp"
                : AppInfo.Current.Name;
            var version = SafeValue(AppInfo.Current.VersionString);
            var build = SafeValue(AppInfo.Current.BuildString);
            var packageName = SafeValue(AppInfo.Current.PackageName);
            var platform = SafeValue(DeviceInfo.Current.Platform.ToString());
            var osVersion = SafeValue(DeviceInfo.Current.VersionString);
            var osDescription = SafeValue(RuntimeInformation.OSDescription);
            var framework = SafeValue(RuntimeInformation.FrameworkDescription);
            var architecture = SafeValue(RuntimeInformation.ProcessArchitecture.ToString());
            var manufacturer = SafeValue(DeviceInfo.Current.Manufacturer);
            var model = SafeValue(DeviceInfo.Current.Model);
            var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var releaseVersion = SafeValue(AppReleaseInfo.Version);

            var messageBuilder = new StringBuilder()
                .AppendLine($"{appName} v{version} (build {build})")
                .AppendLine($"Versione release: {releaseVersion}")
                .AppendLine($"Package: {packageName}")
                .AppendLine($"Piattaforma: {platform} {osVersion}")
                .AppendLine($"OS: {osDescription}")
                .AppendLine($"Runtime: {framework}")
                .AppendLine($"Architettura: {architecture}")
                .AppendLine($"Dispositivo: {manufacturer} {model}")
                .AppendLine($"Data/Ora (debug): {now}");

            await ShowPopupAsync(messageBuilder.ToString().TrimEnd(), "Informazioni");
        }

        private async void OnApriMessaggi(object? sender, EventArgs e)
        {
            try
            {
                await Navigation.PushAsync(new Pagine_Messaggi.Pagina_MessaggiLista());
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(FormatExceptionForPopup(ex), "Errore");
            }
        }

        private async void OnMercatino(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Sezione mercatino in sviluppo.", "Mercatino");
        }

        private async void OnCreaSfida(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Funzione Crea sfida in sviluppo.", "Crea sfida");
        }

        private async Task EseguiLogoutAsync()
        {
            try { await FirebaseSessionePersistente.LogoutAsync(); } catch { }

            Application.Current.MainPage = new NavigationPage(new Pagina_Login());
            await Task.CompletedTask;
        }

        private async void OnEntraComeOspite(object? sender, EventArgs e)
        {
            await ShowPopupAsync("Accesso come ospite (sola lettura) in sviluppo.", "Ospite");
        }

    }
}
