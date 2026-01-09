// ========================= 1) NOME FILE E SCOPO =========================
// Pagine_Home/Pagina_Home.xaml.cs
// Code-behind della Home. Implementa:
//  - Barra icone (menu laterale, mercatino, sfida, chat, menu account).
//  - Menu laterale sinistro free1..free15 con pannello a scorrimento.
//  - Menu laterale destro account (Info app / Esci →) con pannello a scorrimento.
//  - Popup verde stile unificato (informazioni e messaggi Home).
//  - Navigazione verso Pagina_MessaggiLista.
// =======================================================================

using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Biliardo.App.Pagine_Autenticazione;

namespace Biliardo.App.Pagine_Home
{
    public partial class Pagina_Home : ContentPage
    {
        // ===================== 2) STATO INTERNO ==========================
        private bool _menuAperto = false;
        private bool _logoutMenuAperto = false;

        // Popup personalizzato
        private TaskCompletionSource<bool>? _popupTcs;

        // ===================== 3) COSTRUTTORE ============================
        public Pagina_Home()
        {
            InitializeComponent();

            // Nasconde la Navigation Bar (barra grigia con titolo) su questa pagina
            NavigationPage.SetHasNavigationBar(this, false);
        }
        // =================================================================


        // ===================== 4) MENU LATERALE SINISTRO =================
        // 4.1 Toggle del menu laterale sinistro (Tap su icona 3 palle o overlay)
        private async void OnMenuLaterale_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleMenuAsync();
        }

        // 4.2 Logica di apertura/chiusura con animazione (sinistra)
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

        // 4.3 Cattura voci del menu laterale sinistro (Clicked/Tapped)
        private async void OnMenuVoice(object? sender, EventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        private async void OnMenuVoice(object? sender, TappedEventArgs e)
        {
            await HandleMenuVoiceAsync(sender);
        }

        // 4.4 Router per voci free1..free15: apre pagina placeholder
        private async Task HandleMenuVoiceAsync(object? sender)
        {
            string? voce = null;
            if (sender is Label lbl) voce = lbl.Text;
            else if (sender is Button btn) voce = btn.Text;

            voce ??= "free";
            voce = voce.Trim();

            // Chiudi menu sinistro prima di navigare
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
        // =================================================================


        // ===================== 5) MENU LATERALE DESTRO (ACCOUNT) =========
        // 5.1 Toggle menu destro (icona freccia o tap overlay destro)
        private async void OnLogoutMenu(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        private async void OnLogoutMenu_Toggle(object? sender, TappedEventArgs e)
        {
            await ToggleLogoutMenuAsync();
        }

        // 5.2 Logica apertura/chiusura menu destro con animazione
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

        // 5.3 Click "Info su BiliardoApp"
        private async void OnLogoutInfoClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await ShowInfoBiliardoAppAsync();
        }

        // 5.4 Click "Esci →"
        private async void OnLogoutExitClicked(object? sender, EventArgs e)
        {
            if (_logoutMenuAperto)
            {
                await ToggleLogoutMenuAsync();
            }
            await EseguiLogoutAsync();
        }
        // =================================================================


        // ===================== 6) POPUP PERSONALIZZATO HOME ===============
        private Task ShowPopupAsync(string message, string title)
        {
            PopupTitleLabel.Text = title;
            PopupMessageLabel.Text = message;

            PopupOverlay.IsVisible = true;
            _popupTcs = new TaskCompletionSource<bool>();
            return _popupTcs.Task;
        }

        private void OnPopupOkClicked(object? sender, EventArgs e)
        {
            PopupOverlay.IsVisible = false;
            _popupTcs?.TrySetResult(true);
            _popupTcs = null;
        }

        private async Task ShowInfoBiliardoAppAsync()
        {
            var version = AppInfo.Current.VersionString;
            var build = AppInfo.Current.BuildString;

            var message =
                $"BiliardoApp v{version} (build {build})\n" +
                $"© BiliardoApp";

            await ShowPopupAsync(message, "Informazioni");
        }
        // =================================================================


        // ===================== 7) MESSAGGI (CHAT) =========================
        private async void OnApriMessaggi(object? sender, EventArgs e)
        {
            try
            {
                await Navigation.PushAsync(new Pagine_Messaggi.Pagina_MessaggiLista());
            }
            catch (Exception ex)
            {
                await ShowPopupAsync(ex.Message, "Errore");
            }
        }
        // =================================================================


        // ===================== 8) AZIONI (ICONA SFIDA, MERCATINO) =========
        private async void OnMercatino(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Sezione mercatino in sviluppo.", "Mercatino");
        }

        private async void OnCreaSfida(object? sender, TappedEventArgs e)
        {
            await ShowPopupAsync("Funzione Crea sfida in sviluppo.", "Crea sfida");
        }
        // =================================================================


        // ===================== 9) LOGOUT (LOGICA) ==========================
        private async Task EseguiLogoutAsync()
        {
            Application.Current.MainPage = new NavigationPage(new Pagina_Login());
            await Task.CompletedTask;
        }
        // =================================================================


        // ===================== 10) ACCESSO OSPITE (STUB) ==================
        private async void OnEntraComeOspite(object? sender, EventArgs e)
        {
            await ShowPopupAsync("Accesso come ospite (sola lettura) in sviluppo.", "Ospite");
        }
        // =================================================================
    }
}
