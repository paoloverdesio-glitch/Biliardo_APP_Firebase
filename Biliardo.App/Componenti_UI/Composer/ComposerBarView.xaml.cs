using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Biliardo.App.Componenti_UI;
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
        private const int VoiceMinSendMs = 600;
        private const int VoiceUiTickMs = 80;
        private const int VoiceWaveRecordHistoryMs = 2500;
        private const float VoiceWaveStrokePx = 2f;
        private const float VoiceWaveMaxPeakToPeakDip = 40f;

        private const double GestureDeadzoneDip = 12;
        private const double GestureLockDyDip = -90;
        private const double GestureCancelDxDip = -110;

        private readonly IVoiceRecorder _recorder;
        private readonly DraftAudioPlayback _draftPlayback;
        private readonly WaveformDrawable _voiceWave;
        private IDispatcherTimer? _voiceTimer;

        private bool _isRecording;
        private bool _isLocked;
        private bool _micDisabled;
        private bool _isSending;

        // evita “rilascio” che arriva mentre l’annullamento è in corso
        private bool _cancelInProgress;

        private Point _holdStartPoint;

        private string _composerText = "";
        private string _voiceTimeLabel = "00:00";
        private string _voiceHintLabel = "Scorri a sinistra per annullare • Scorri su per bloccare";

        public ComposerBarView()
        {
            _recorder = VoiceMediaFactory.CreateRecorder();
            _draftPlayback = new DraftAudioPlayback();

            InitializeComponent();

            _voiceWave = new WaveformDrawable(VoiceWaveRecordHistoryMs, VoiceUiTickMs, VoiceWaveStrokePx, VoiceWaveMaxPeakToPeakDip);
            VoiceWaveHoldView.Drawable = _voiceWave;
            VoiceWaveLockView.Drawable = _voiceWave;

            if (_recorder is NoopVoiceRecorder)
                _micDisabled = true;

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

        public string VoicePauseResumeLabel => (_recorder?.IsPaused ?? false) ? "Riprendi" : "Pausa";

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

            _cancelInProgress = false;

            _holdStartPoint = e.Point;
            _isLocked = false;
            VoiceHintLabel = "Scorri a sinistra per annullare • Scorri su per bloccare";

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
            _voiceWave.Reset();
            StartVoiceTimer();
            RefreshVoiceBindings();
        }

        private void OnMicHoldMoved(object sender, HoldEventArgs e)
        {
            if (!_isRecording || _isLocked || _cancelInProgress) return;

            var dx = e.Point.X - _holdStartPoint.X;
            var dy = e.Point.Y - _holdStartPoint.Y;

            if (Math.Abs(dx) < GestureDeadzoneDip && Math.Abs(dy) < GestureDeadzoneDip)
                return;

            if (dy <= GestureLockDyDip)
            {
                _isLocked = true;
                VoiceHintLabel = "Registrazione bloccata";
                RefreshVoiceBindings();
                return;
            }

            if (dx <= GestureCancelDxDip)
            {
                _cancelInProgress = true;
                _ = CancelRecordingAsync();
                return;
            }
        }

        private async void OnMicHoldEnded(object sender, HoldEventArgs e)
        {
            if (!_isRecording) return;
            if (_isLocked) return;
            if (_cancelInProgress) return;

            await StopRecordingAndSendAsync();
        }

        private async void OnMicHoldCanceled(object sender, HoldEventArgs e)
        {
            if (!_isRecording) return;
            _cancelInProgress = true;
            await CancelRecordingAsync();
        }

        private async void OnVoiceTrashClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            _cancelInProgress = true;
            await CancelRecordingAsync();
        }

        private async void OnVoicePauseResumeClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;

            try
            {
                if (_recorder.IsPaused)
                    await _recorder.ResumeAsync();
                else
                    await _recorder.PauseAsync();
            }
            catch { }

            OnPropertyChanged(nameof(VoicePauseResumeLabel));
        }

        private async void OnVoiceDoneClicked(object sender, EventArgs e)
        {
            if (!_isRecording) return;
            await StopRecordingAndSendAsync();
        }

        private async Task StopRecordingAndSendAsync()
        {
            var (filePath, durationMs) = await StopVoiceAndGetFileAsync();
            if (string.IsNullOrWhiteSpace(filePath))
            {
                RefreshVoiceBindings();
                return;
            }

            if (durationMs < VoiceMinSendMs)
            {
                TryDeleteFile(filePath);
                RefreshVoiceBindings();
                return;
            }

            var item = new PendingItemVm
            {
                Kind = PendingKind.AudioDraft,
                LocalFilePath = filePath,
                DisplayName = "Audio",
                DurationMs = durationMs,
                SizeBytes = TryGetFileSize(filePath),
                CreatedUtc = DateTimeOffset.UtcNow
            };

            SendRequested?.Invoke(this, new ComposerSendPayload("", new[] { item }));
            RefreshVoiceBindings();
        }

        private async Task CancelRecordingAsync()
        {
            // UI subito (così l’eventuale “rilascio” non può più inviare)
            StopVoiceTimer();

            _isRecording = false;
            _isLocked = false;
            VoiceTimeLabel = "00:00";
            _voiceWave.Reset();
            VoiceWaveHoldView.Invalidate();
            VoiceWaveLockView.Invalidate();
            RefreshVoiceBindings();

            try { await _recorder.CancelAsync(); } catch { }

            _cancelInProgress = false;
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
            _voiceWave.Reset();
            VoiceWaveHoldView.Invalidate();
            VoiceWaveLockView.Invalidate();
            return (path, duration);
        }

        private void StartVoiceTimer()
        {
            StopVoiceTimer();
            _voiceTimer = Dispatcher.CreateTimer();
            _voiceTimer.Interval = TimeSpan.FromMilliseconds(VoiceUiTickMs);
            _voiceTimer.Tick += (_, __) =>
            {
                var ms = _recorder.GetElapsedMs();
                VoiceTimeLabel = FormatMs(ms);
                var level = _recorder.TryGetLevel01();
                _voiceWave.AddSample((float)level);
                VoiceWaveHoldView.Invalidate();
                VoiceWaveLockView.Invalidate();
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
            var fileName = $"voice_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.m4a";
            return Path.Combine(FileSystem.CacheDirectory, fileName);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
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
