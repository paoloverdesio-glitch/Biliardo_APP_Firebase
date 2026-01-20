using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Media;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

#if WINDOWS
using Windows.Media.Core;
using Windows.Media.Playback;
#endif

namespace Biliardo.App.Componenti_UI.Composer
{
    public partial class ComposerBarView : ContentView
    {
        private readonly IVoiceRecorder _recorder;
        private readonly DraftAudioPlayback _draftPlayback;
        private IDispatcherTimer? _voiceTimer;

        private bool _isRecording;
        private bool _isLocked;
        private bool _micDisabled;
        private bool _isSending;
        private Point _holdStartPoint;

        private string _composerText = "";
        private string _voiceTimeLabel = "00:00";
        private string _voiceHintLabel = "Scorri a sinistra per annullare • Su per bloccare";

        public ComposerBarView()
        {
            // IMPORTANTISSIMO:
            // inizializza PRIMA del BindingContext, così le binding non leggono campi null.
            _recorder = VoiceMediaFactory.CreateRecorder();
            _draftPlayback = new DraftAudioPlayback();

            InitializeComponent();

            BindingContext = this;

            PendingItems.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasPendingItems));
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanShowMic));
            };
        }

        public ObservableCollection<PendingItemVm> PendingItems { get; } = new();

        public bool HasPendingItems => PendingItems.Count > 0;

        public string ComposerText
        {
            get => _composerText;
            set
            {
                if (_composerText == value) return;
                _composerText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanShowMic));
            }
        }

        public bool CanSend => !_isSending && (!string.IsNullOrWhiteSpace(ComposerText) || PendingItems.Count > 0);

        public bool CanShowMic =>
            !_isSending &&
            !HasPendingItems &&
            string.IsNullOrWhiteSpace(ComposerText) &&
            !_isRecording &&
            !_micDisabled;

        public bool IsVoiceHoldStripVisible => _isRecording && !_isLocked;
        public bool IsVoiceLockPanelVisible => _isRecording && _isLocked;
        public bool IsNormalComposerVisible => !_isRecording;

        public string VoiceTimeLabel
        {
            get => _voiceTimeLabel;
            private set
            {
                if (_voiceTimeLabel == value) return;
                _voiceTimeLabel = value;
                OnPropertyChanged();
            }
        }

        public string VoiceHintLabel
        {
            get => _voiceHintLabel;
            private set
            {
                if (_voiceHintLabel == value) return;
                _voiceHintLabel = value;
                OnPropertyChanged();
            }
        }

        // Null-safe: evita crash in fase di costruzione/binding
        public string VoicePauseResumeLabel => (_recorder?.IsPaused ?? false) ? "PLAY" : "STOP";

        public event EventHandler? AttachmentActionRequested;
        public event EventHandler<ComposerSendPayload>? SendRequested;
        public event EventHandler<PendingItemVm>? PendingItemSendRequested;
        public event EventHandler<PendingItemVm>? PendingItemRemoved;

        public bool TryAddPendingItem(PendingItemVm item, int? maxNonTextItems = null)
        {
            if (item == null) return false;

            if (maxNonTextItems.HasValue && item.Kind != PendingKind.AudioDraft)
            {
                var count = PendingItems.Count(x => x.Kind != PendingKind.AudioDraft);
                if (count >= maxNonTextItems.Value)
                {
                    _ = Application.Current?.MainPage?.DisplayAlert(
                        "Limite allegati",
                        $"Puoi allegare al massimo {maxNonTextItems.Value} elementi.",
                        "OK");
                    return false;
                }
            }

            PendingItems.Add(item);
            return true;
        }

        public void ClearComposer()
        {
            ComposerText = "";
            PendingItems.Clear();
        }

        private void OnAttachmentClicked(object sender, EventArgs e)
            => AttachmentActionRequested?.Invoke(this, EventArgs.Empty);

        private void OnSendClicked(object sender, EventArgs e)
        {
            if (!CanSend) return;
            var payload = new ComposerSendPayload(ComposerText.Trim(), PendingItems.ToList());
            SendRequested?.Invoke(this, payload);
        }

        private void OnSendDraftClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is PendingItemVm item)
                PendingItemSendRequested?.Invoke(this, item);
        }

        private void OnRemovePendingClicked(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is PendingItemVm item)
            {
                PendingItems.Remove(item);
                PendingItemRemoved?.Invoke(this, item);
            }
        }

        private async void OnPlayDraftClicked(object sender, EventArgs e)
        {
            if (sender is not Button btn || btn.CommandParameter is not PendingItemVm item)
                return;

            if (!item.IsAudioDraft || string.IsNullOrWhiteSpace(item.LocalFilePath))
                return;

            if (item.IsPlaying)
            {
                _draftPlayback.StopPlaybackSafe();
                item.IsPlaying = false;
                return;
            }

            foreach (var other in PendingItems.Where(x => x.IsPlaying))
                other.IsPlaying = false;

            try
            {
                item.IsPlaying = true;
                await _draftPlayback.PlayAsync(item.LocalFilePath);
                item.IsPlaying = false;
            }
            catch (Exception ex)
            {
                item.IsPlaying = false;
                await Application.Current?.MainPage?.DisplayAlert("Audio", ex.Message, "OK");
            }
        }

        private async void OnMicHoldStarted(object sender, HoldEventArgs e)
        {
            if (_isRecording || _micDisabled) return;

            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await Application.Current?.MainPage?.DisplayAlert("Microfono", "Permesso microfono non concesso.", "OK");
                return;
            }

            _holdStartPoint = e.Point;
            _isLocked = false;
            VoiceHintLabel = "Scorri a sinistra per annullare • Su per bloccare";

            try
            {
                var path = BuildDraftPath();
                await _recorder.StartAsync(path);
            }
            catch (Exception ex)
            {
                _micDisabled = true;
                await Application.Current?.MainPage?.DisplayAlert("Microfono", ex.Message, "OK");
                RefreshVoiceBindings();
                return;
            }

            _isRecording = true;
            StartVoiceTimer();
            RefreshVoiceBindings();
        }

        private void OnMicHoldMoved(object sender, HoldEventArgs e)
        {
            if (!_isRecording || _isLocked) return;

            var dx = e.Point.X - _holdStartPoint.X;
            var dy = e.Point.Y - _holdStartPoint.Y;

            if (dx <= -80)
            {
                _ = CancelRecordingAsync();
                return;
            }

            if (dy <= -80)
            {
                _isLocked = true;
                VoiceHintLabel = "Registrazione bloccata";
                RefreshVoiceBindings();
            }
        }

        private async void OnMicHoldEnded(object sender, HoldEventArgs e)
        {
            if (!_isRecording) return;
            if (_isLocked) return;

            await StopRecordingToDraftAsync();
        }

        private async void OnMicHoldCanceled(object sender, HoldEventArgs e)
        {
            if (!_isRecording) return;
            await CancelRecordingAsync();
        }

        private async void OnVoiceTrashClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            await CancelRecordingAsync();
        }

        private async void OnVoicePauseResumeClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;

            if (_recorder.IsPaused)
                await _recorder.ResumeAsync();
            else
                await _recorder.PauseAsync();

            OnPropertyChanged(nameof(VoicePauseResumeLabel));
        }

        private async void OnVoiceDoneClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            await StopRecordingToDraftAsync();
        }

        private async Task StopRecordingToDraftAsync()
        {
            var (filePath, durationMs) = await StopVoiceAndGetFileAsync();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                RefreshVoiceBindings();
                return;
            }

            var item = new PendingItemVm
            {
                Kind = PendingKind.AudioDraft,
                LocalFilePath = filePath,
                DisplayName = "Audio pronto",
                DurationMs = durationMs,
                SizeBytes = TryGetFileSize(filePath),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            PendingItems.Add(item);
            RefreshVoiceBindings();
        }

        private async Task CancelRecordingAsync()
        {
            StopVoiceTimer();
            await _recorder.CancelAsync();
            _isRecording = false;
            _isLocked = false;
            VoiceTimeLabel = "00:00";
            RefreshVoiceBindings();
        }

        private async Task<(string? filePath, long durationMs)> StopVoiceAndGetFileAsync()
        {
            StopVoiceTimer();
            await _recorder.StopAsync();
            var path = _recorder.CurrentFilePath;
            var duration = _recorder.GetElapsedMs();
            _isRecording = false;
            _isLocked = false;
            VoiceTimeLabel = "00:00";
            return (path, duration);
        }

        private void StartVoiceTimer()
        {
            StopVoiceTimer();
            _voiceTimer = Dispatcher.CreateTimer();
            _voiceTimer.Interval = TimeSpan.FromMilliseconds(120);
            _voiceTimer.Tick += (_, __) =>
            {
                var ms = _recorder.GetElapsedMs();
                VoiceTimeLabel = FormatMs(ms);
            };
            _voiceTimer.Start();
        }

        private void StopVoiceTimer()
        {
            if (_voiceTimer == null) return;
            _voiceTimer.Stop();
            _voiceTimer = null;
        }

        private static string FormatMs(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            return ts.Minutes >= 60 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
        }

        private static long TryGetFileSize(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string BuildDraftPath()
        {
            var fileName = $"voice_draft_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.m4a";
            return Path.Combine(FileSystem.CacheDirectory, fileName);
        }

        private void RefreshVoiceBindings()
        {
            OnPropertyChanged(nameof(IsVoiceHoldStripVisible));
            OnPropertyChanged(nameof(IsVoiceLockPanelVisible));
            OnPropertyChanged(nameof(IsNormalComposerVisible));
            OnPropertyChanged(nameof(VoicePauseResumeLabel));
            OnPropertyChanged(nameof(CanShowMic));
        }

        private interface IAudioPlayback
        {
            Task PlayAsync(string filePath);
            void StopPlaybackSafe();
        }

        private sealed class DraftAudioPlayback : IAudioPlayback
        {
#if ANDROID
            private Android.Media.MediaPlayer? _player;
#elif WINDOWS
            private MediaPlayer? _player;
#endif

            public Task PlayAsync(string filePath)
            {
#if ANDROID
                StopPlaybackSafe();
                _player = new Android.Media.MediaPlayer();
                _player.SetDataSource(filePath);
                _player.Prepare();
                _player.Start();
                _player.Completion += (_, __) => StopPlaybackSafe();
                return Task.CompletedTask;
#elif WINDOWS
                StopPlaybackSafe();
                _player = new MediaPlayer();
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
                _player.MediaEnded += (_, __) => StopPlaybackSafe();
                _player.Play();
                return Task.CompletedTask;
#else
                throw new NotSupportedException("Playback audio supportato solo su Android/Windows (per ora).");
#endif
            }

            public void StopPlaybackSafe()
            {
#if ANDROID
                try
                {
                    if (_player != null)
                    {
                        try { _player.Stop(); } catch { }
                        try { _player.Release(); } catch { }
                        _player = null;
                    }
                }
                catch { }
#elif WINDOWS
                try
                {
                    if (_player != null)
                    {
                        _player.Pause();
                        _player.Dispose();
                        _player = null;
                    }
                }
                catch { }
#endif
            }
        }
    }
}
