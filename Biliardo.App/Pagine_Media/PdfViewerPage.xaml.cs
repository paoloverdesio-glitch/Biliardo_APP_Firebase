using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Biliardo.App.Infrastructure.Media.Processing;

namespace Biliardo.App.Pagine_Media
{
    public partial class PdfViewerPage : ContentPage
    {
        private readonly string _localPath;
        private readonly MediaPreviewGenerator _previewGenerator = new();

        public PdfViewerPage(string localPath, string fileName)
        {
            InitializeComponent();
            _localPath = localPath;
            FileName = fileName;
            BindingContext = this;
            _ = LoadPreviewAsync();
        }

        public string FileName { get; }
        public ImageSource? PreviewSource { get; private set; }

        private async Task LoadPreviewAsync()
        {
            try
            {
                if (!File.Exists(_localPath))
                    return;

                var preview = await _previewGenerator.GenerateAsync(
                    new MediaPreviewRequest(_localPath, MediaKind.Pdf, "application/pdf", FileName, "pdf_view", null, null),
                    CancellationToken.None);

                if (preview != null && File.Exists(preview.ThumbLocalPath))
                {
                    PreviewSource = ImageSource.FromFile(preview.ThumbLocalPath);
                    OnPropertyChanged(nameof(PreviewSource));
                }
            }
            catch
            {
                // ignore
            }
        }

        private async void OnOpenExternalClicked(object sender, EventArgs e)
        {
            try
            {
                if (!File.Exists(_localPath))
                    return;

                await Launcher.Default.OpenAsync(new OpenFileRequest
                {
                    File = new ReadOnlyFile(_localPath, "application/pdf")
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", ex.Message, "OK");
            }
        }
    }
}
