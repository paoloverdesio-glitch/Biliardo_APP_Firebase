using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Pagine_Media
{
    public partial class VideoPlayerPage : ContentPage
    {
        public VideoPlayerPage(MediaSource source, ImageSource? poster)
        {
            InitializeComponent();
            MediaSource = source;
            PosterSource = poster;
            BindingContext = this;
            IsBuffering = true;
        }

        public MediaSource MediaSource { get; }
        public ImageSource? PosterSource { get; }

        private bool _isBuffering;
        public bool IsBuffering
        {
            get => _isBuffering;
            set { _isBuffering = value; OnPropertyChanged(); }
        }

        private void OnMediaOpened(object sender, EventArgs e)
        {
            IsBuffering = false;
        }

        private async void OnMediaFailed(object sender, EventArgs e)
        {
            IsBuffering = false;
            await DisplayAlert("Errore", "Impossibile riprodurre il video.", "OK");
        }
    }
}
