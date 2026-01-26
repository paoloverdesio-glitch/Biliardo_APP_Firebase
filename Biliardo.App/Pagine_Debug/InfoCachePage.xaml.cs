using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.ApplicationModel;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Pagine_Media;

namespace Biliardo.App.Pagine_Debug
{
    public partial class InfoCachePage : ContentPage
    {
        private readonly MediaCacheService _mediaCache = new();
        private readonly ChatLocalCache _chatCache = new();

        public ObservableCollection<MediaEntryVm> MediaEntries { get; } = new();
        public ObservableCollection<ChatEntryVm> ChatEntries { get; } = new();

        private string _totalLabel = string.Empty;
        public string TotalLabel
        {
            get => _totalLabel;
            set { _totalLabel = value; OnPropertyChanged(); }
        }

        private string _percentLabel = string.Empty;
        public string PercentLabel
        {
            get => _percentLabel;
            set { _percentLabel = value; OnPropertyChanged(); }
        }

        public InfoCachePage()
        {
            InitializeComponent();
            BindingContext = this;
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            try
            {
                var totalBytes = await _mediaCache.GetTotalBytesAsync();
                var percent = AppMediaOptions.CacheMaxBytes > 0 ? (double)totalBytes / AppMediaOptions.CacheMaxBytes * 100 : 0;

                TotalLabel = $"{FormatBytes(totalBytes)} usati";
                PercentLabel = $"{percent:0.##}% della cache";

                MediaEntries.Clear();
                var entries = await _mediaCache.ListEntriesAsync();
                foreach (var entry in entries)
                    MediaEntries.Add(new MediaEntryVm(entry, OpenCacheEntryAsync));

                ChatEntries.Clear();
                var chatSummaries = await _chatCache.ListSummariesAsync(CancellationToken.None);
                foreach (var summary in chatSummaries)
                    ChatEntries.Add(new ChatEntryVm(summary.ChatId, summary.MessageCount, summary.Bytes));
            }
            catch
            {
                // ignore
            }
        }

        private async Task OpenCacheEntryAsync(MediaEntryVm entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.LocalPath) || !File.Exists(entry.LocalPath))
                return;

            var ext = Path.GetExtension(entry.LocalPath).ToLowerInvariant();

            if (ext is ".jpg" or ".jpeg" or ".png" or ".gif")
            {
                await Navigation.PushModalAsync(CreateImageViewerPage(entry.LocalPath));
                return;
            }

            if (ext is ".mp4" or ".mov")
            {
                await Navigation.PushAsync(new VideoPlayerPage(MediaSource.FromFile(entry.LocalPath), null));
                return;
            }

            if (ext is ".pdf")
            {
                await Navigation.PushAsync(new PdfViewerPage(entry.LocalPath, entry.FileName));
                return;
            }

            if (ext is ".m4a" or ".mp3" or ".wav" or ".aac")
            {
                var page = new ContentPage
                {
                    Title = "Audio",
                    BackgroundColor = Colors.Black,
                    Content = new Componenti_UI.Media.AudioWavePlayerView
                    {
                        StoragePath = entry.StoragePath,
                        FileName = entry.FileName,
                        DurationMs = 0
                    }
                };
                await Navigation.PushAsync(page);
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest
            {
                File = new ReadOnlyFile(entry.LocalPath)
            });
        }

        private static ContentPage CreateImageViewerPage(string path)
        {
            var img = new Image
            {
                Source = path,
                Aspect = Aspect.AspectFit
            };

            return new ContentPage
            {
                BackgroundColor = Colors.Black,
                Content = img
            };
        }

        public sealed class MediaEntryVm
        {
            public MediaEntryVm(MediaCacheService.MediaCacheEntry entry, Func<MediaEntryVm, Task> openAction)
            {
                FileName = entry.FileName;
                LocalPath = entry.LocalPath;
                StoragePath = entry.StoragePath;
                SizeLabel = FormatBytes(entry.Bytes);
                OpenCommand = new Command<MediaEntryVm>(async vm => await openAction(vm));
            }

            public string FileName { get; }
            public string LocalPath { get; }
            public string StoragePath { get; }
            public string SizeLabel { get; }
            public Command<MediaEntryVm> OpenCommand { get; }
        }

        public sealed class ChatEntryVm
        {
            public ChatEntryVm(string chatId, int count, long bytes)
            {
                ChatId = chatId;
                MessageCount = count;
                Bytes = bytes;
            }

            public string ChatId { get; }
            public int MessageCount { get; }
            public long Bytes { get; }

            public string ChatLabel => ChatId;
            public string CountLabel => $"{MessageCount} msg";
            public string SizeLabel => FormatBytes(Bytes);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 B";
            double b = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int u = 0;
            while (b >= 1024 && u < units.Length - 1)
            {
                b /= 1024;
                u++;
            }
            return $"{b:0.#} {units[u]}";
        }
    }
}
