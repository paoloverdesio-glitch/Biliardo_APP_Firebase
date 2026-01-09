using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public partial class BottomSheetAllegatiPage : ContentPage
    {
        public enum AllegatoAzione
        {
            Gallery,
            Camera,
            Document,
            Audio,
            Location,
            Contact,
            Poll,
            Event
        }

        public event EventHandler<AllegatoAzione>? AzioneSelezionata;

        private bool _isAnimating;

        public BottomSheetAllegatiPage()
        {
            InitializeComponent();
        }

        protected override void OnSizeAllocated(double width, double height)
        {
            base.OnSizeAllocated(width, height);

            // circa 30% schermo, con clamp ragionevole
            if (height > 0)
            {
                var h = height * 0.30;
                if (h < 240) h = 240;
                if (h > 420) h = 420;

                SheetBorder.HeightRequest = h;
            }
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (_isAnimating) return;

            // animazione: parte da sotto e sale
            try
            {
                _isAnimating = true;

                // attendo un frame per avere dimensioni
                await Task.Delay(10);

                var startY = (SheetBorder.HeightRequest > 0 ? SheetBorder.HeightRequest : 320) + 40;
                SheetBorder.TranslationY = startY;
                SheetBorder.Opacity = 1;

                await SheetBorder.TranslateTo(0, 0, 180, Easing.CubicOut);
            }
            catch
            {
                // best effort
                SheetBorder.TranslationY = 0;
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private async Task CloseModalBestEffortAsync()
        {
            if (_isAnimating) return;

            try
            {
                _isAnimating = true;

                var endY = (SheetBorder.HeightRequest > 0 ? SheetBorder.HeightRequest : 320) + 40;
                await SheetBorder.TranslateTo(0, endY, 140, Easing.CubicIn);

                await Navigation.PopModalAsync();
            }
            catch
            {
                try { await Navigation.PopModalAsync(); } catch { }
            }
            finally
            {
                _isAnimating = false;
            }
        }

        private void Fire(AllegatoAzione a) => AzioneSelezionata?.Invoke(this, a);

        private async void OnBackdropTapped(object sender, TappedEventArgs e)
            => await CloseModalBestEffortAsync();

        private async void OnCloseTapped(object sender, TappedEventArgs e)
            => await CloseModalBestEffortAsync();

        private async void OnGalleryTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Gallery);
        }

        private async void OnCameraTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Camera);
        }

        private async void OnDocumentTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Document);
        }

        private async void OnAudioTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Audio);
        }

        private async void OnLocationTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Location);
        }

        private async void OnContactTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Contact);
        }

        private async void OnPollTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Poll);
        }

        private async void OnEventTapped(object sender, TappedEventArgs e)
        {
            await CloseModalBestEffortAsync();
            Fire(AllegatoAzione.Event);
        }
    }
}
