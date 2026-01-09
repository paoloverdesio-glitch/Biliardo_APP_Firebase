using System;
using Microsoft.Maui.Controls;
using Biliardo.App.Pagine_Home;
using Biliardo.App.Servizi_Sicurezza;

namespace Biliardo.App.Pagine_Autenticazione;

public partial class Pagina_Accesso_Selezione : ContentPage
{
    private bool _autoDone;

    public Pagina_Accesso_Selezione()
    {
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // Evita rientri multipli
        if (_autoDone)
            return;

        try
        {
            // Se ho un token valido, entro direttamente (finché non scatta la soglia)
            var hasToken = await SessionePersistente.HaTokenAsync();
            if (!hasToken)
                return;

            var needGate = await SessionePersistente.DeveRichiedereBiometriaAsync();
            if (needGate)
            {
                // Biometria non integrata ancora: per ora NON auto-entriamo.
                // L’utente può fare "Accedi" normalmente.
                return;
            }

            _autoDone = true;
            Application.Current.MainPage = new NavigationPage(new Pagina_Home());
        }
        catch
        {
            // In caso di problemi non blocchiamo: resta nella pagina.
        }
    }

    private async void OnEntraComeOspite(object sender, EventArgs e) =>
        await DisplayAlert("Ospite", "Accesso come ospite (placeholder).", "OK");

    private async void OnAccedi(object sender, EventArgs e) =>
        await Navigation.PushAsync(new Pagina_Login());

    private async void OnRegistrati(object sender, EventArgs e) =>
        await Navigation.PushAsync(new Pagina_Registrazione());
}
