using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public partial class LogTrasferimentiPage : ContentPage
    {
        private readonly LogTrasferimentiViewModel _viewModel = new();

        public LogTrasferimentiPage()
        {
            InitializeComponent();
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            await _viewModel.EnsureFilesAndRefreshAsync();
        }

        private async void OnOpenClicked(object sender, EventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not LogFileInfo info)
                return;

            try
            {
                var request = new OpenFileRequest
                {
                    File = new ReadOnlyFile(info.Path)
                };
                await Launcher.Default.OpenAsync(request);
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Impossibile aprire il file: {ex.Message}", "OK");
            }
        }

        private async void OnShareClicked(object sender, EventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not LogFileInfo info)
                return;

            try
            {
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Log trasferimenti",
                    File = new ShareFile(info.Path)
                });
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Impossibile inoltrare il file: {ex.Message}", "OK");
            }
        }

        private async void OnDeleteClicked(object sender, EventArgs e)
        {
            if (sender is not Button button || button.CommandParameter is not LogFileInfo info)
                return;

            var ok = await DisplayAlert("Conferma", "Sicuro di voler cancellare il file?", "OK", "Annulla");
            if (!ok) return;

            try
            {
                if (File.Exists(info.Path))
                    File.Delete(info.Path);

                if (info.Kind == LogFileKind.Bars)
                    await CsvLoggers.RecreateBarsFileAsync();
                else
                    await CsvLoggers.RecreateDotsFileAsync();

                await _viewModel.RefreshSizesAsync();
            }
            catch (Exception ex)
            {
                await DisplayAlert("Errore", $"Impossibile cancellare il file: {ex.Message}", "OK");
            }
        }
    }

    public enum LogFileKind
    {
        Bars,
        Dots
    }

    public sealed class LogFileInfo : BindableObject
    {
        private string _sizeLabel = "";

        public LogFileKind Kind { get; init; }

        public string Name { get; init; } = "";

        public string Path { get; init; } = "";

        public string SizeLabel
        {
            get => _sizeLabel;
            set
            {
                if (_sizeLabel == value) return;
                _sizeLabel = value;
                OnPropertyChanged();
            }
        }
    }

    public sealed class LogTrasferimentiViewModel : BindableObject
    {
        private readonly FirebaseTransferDebugMonitor _monitor = FirebaseTransferDebugMonitor.Instance;
        private bool _showOverlay;

        public ObservableCollection<LogFileInfo> LogFiles { get; } = new();

        public LogTrasferimentiViewModel()
        {
            _showOverlay = _monitor.ShowOverlay;
            LogFiles.Add(new LogFileInfo
            {
                Kind = LogFileKind.Bars,
                Name = "TrasfBarre.csv",
                Path = CsvLoggers.BarsFilePath
            });
            LogFiles.Add(new LogFileInfo
            {
                Kind = LogFileKind.Dots,
                Name = "TrasfPallini.csv",
                Path = CsvLoggers.DotsFilePath
            });
        }

        public bool ShowOverlay
        {
            get => _showOverlay;
            set
            {
                if (_showOverlay == value) return;
                _showOverlay = value;
                _monitor.ShowOverlay = value;
                OnPropertyChanged();
            }
        }

        public async Task EnsureFilesAndRefreshAsync()
        {
            await CsvLoggers.EnsureFilesAsync();
            await RefreshSizesAsync();
            ShowOverlay = _monitor.ShowOverlay;
        }

        public Task RefreshSizesAsync()
        {
            foreach (var file in LogFiles)
            {
                file.SizeLabel = FormatSize(file.Path);
            }

            return Task.CompletedTask;
        }

        private static string FormatSize(string path)
        {
            try
            {
                if (!File.Exists(path)) return "0 B";
                var bytes = new FileInfo(path).Length;
                string[] units = { "B", "KB", "MB", "GB" };
                double size = bytes;
                var idx = 0;
                while (size >= 1024 && idx < units.Length - 1)
                {
                    size /= 1024;
                    idx++;
                }
                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[idx]);
            }
            catch
            {
                return "?";
            }
        }
    }
}
