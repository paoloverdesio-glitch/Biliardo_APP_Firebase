using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Biliardo.App.Componenti_UI.Composer
{
    public enum PendingKind
    {
        Image,
        Video,
        AudioDraft,
        File,
        Location,
        Contact,
        Poll,
        Event
    }

    public sealed class PendingItemVm : INotifyPropertyChanged
    {
        private bool _isPlaying;

        public string LocalId { get; set; } = Guid.NewGuid().ToString("N");
        public PendingKind Kind { get; set; }
        public string? LocalFilePath { get; set; }
        public string? MediaCacheKey { get; set; }
        public string DisplayName { get; set; } = "";
        public object? Extra { get; set; }

        public long DurationMs { get; set; }
        public long SizeBytes { get; set; }
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? Address { get; set; }

        public string? ContactName { get; set; }
        public string? ContactPhone { get; set; }

        public bool IsAudioDraft => Kind == PendingKind.AudioDraft;

        public bool IsPlaying
        {
            get => _isPlaying;
            set
            {
                if (_isPlaying == value) return;
                _isPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AudioPlayLabel));
            }
        }

        public string AudioPlayLabel => IsPlaying ? "Stop" : "Play";

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed record ComposerSendPayload(string Text, IReadOnlyList<PendingItemVm> PendingItems);
}
