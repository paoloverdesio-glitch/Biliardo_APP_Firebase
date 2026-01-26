using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Biliardo.App.Componenti_UI;
using Biliardo.App.Infrastructure.Media;
using Biliardo.App.Infrastructure.Media.Cache;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Componenti_UI.Media
{
    public partial class AudioWavePlayerView : ContentView
    {
        private const int PlaybackWaveHistoryMs = 5000;
        private const int PlaybackWaveTickMs = 80;
        private const float PlaybackWaveStrokePx = 2f;
        private const float PlaybackWaveMaxPeakToPeakDip = 40f;

        private readonly MediaCacheService _mediaCache = new();
        private readonly IAudioPlayback _playback = AudioPlaybackFactory.Create();
        private readonly WaveformDrawable _playbackWave;
        private IDispatcherTimer? _waveTimer;
        private double _wavePhase;
        private int _waveIndex;
        private bool _isPlaying;

        public static readonly BindableProperty StoragePathProperty =
            BindableProperty.Create(nameof(StoragePath), typeof(string), typeof(AudioWavePlayerView), default(string));

        public static readonly BindableProperty FileNameProperty =
            BindableProperty.Create(nameof(FileName), typeof(string), typeof(AudioWavePlayerView), default(string));

        public static readonly BindableProperty DurationMsProperty =
            BindableProperty.Create(nameof(DurationMs), typeof(long), typeof(AudioWavePlayerView), 0L,
                propertyChanged: (bindable, __, ___) =>
                {
                    if (bindable is AudioWavePlayerView view)
                        view.OnPropertyChanged(nameof(DurationLabel));
                });

        public static readonly BindableProperty WaveformProperty =
            BindableProperty.Create(nameof(Waveform), typeof(IReadOnlyList<int>), typeof(AudioWavePlayerView), default(IReadOnlyList<int>));

        public AudioWavePlayerView()
        {
            InitializeComponent();
            _playbackWave = new WaveformDrawable(PlaybackWaveHistoryMs, PlaybackWaveTickMs, PlaybackWaveStrokePx, PlaybackWaveMaxPeakToPeakDip);
            WaveView.Drawable = _playbackWave;
            _playback.PlaybackEnded += (_, __) => MainThread.BeginInvokeOnMainThread(StopPlaybackWave);
        }

        public string? StoragePath
        {
            get => (string?)GetValue(StoragePathProperty);
            set => SetValue(StoragePathProperty, value);
        }

        public string? FileName
        {
            get => (string?)GetValue(FileNameProperty);
            set => SetValue(FileNameProperty, value);
        }

        public long DurationMs
        {
            get => (long)GetValue(DurationMsProperty);
            set => SetValue(DurationMsProperty, value);
        }

        public IReadOnlyList<int>? Waveform
        {
            get => (IReadOnlyList<int>?)GetValue(WaveformProperty);
            set => SetValue(WaveformProperty, value);
        }

        public string DurationLabel => DurationMs > 0 ? TimeSpan.FromMilliseconds(DurationMs).ToString(@"mm\:ss") : string.Empty;

        private async void OnPlayClicked(object sender, EventArgs e)
        {
            try
            {
                if (_isPlaying)
                {
                    StopPlayback();
                    return;
                }

                if (string.IsNullOrWhiteSpace(StoragePath))
                {
                    await ShowAlertAsync("Audio non disponibile.");
                    return;
                }

                var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync();
                if (string.IsNullOrWhiteSpace(idToken))
                {
                    await ShowAlertAsync("Sessione scaduta.");
                    return;
                }

                var local = await _mediaCache.GetOrDownloadAsync(idToken!, StoragePath!, FileName ?? "audio.m4a", isThumb: false, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(local))
                {
                    await ShowAlertAsync("Audio non disponibile.");
                    return;
                }

                _isPlaying = true;
                PlayButton.Text = "■";
                StartPlaybackWave();
                await _playback.PlayAsync(local);
            }
            catch (Exception ex)
            {
                await ShowAlertAsync(ex.Message);
                StopPlayback();
            }
        }

        private void StopPlayback()
        {
            _playback.StopPlaybackSafe();
            StopPlaybackWave();
        }

        private void StartPlaybackWave()
        {
            StopPlaybackWave();
            _wavePhase = 0;
            _waveIndex = 0;
            _playbackWave.Reset();

            var waveform = Waveform?.ToArray();

            _waveTimer = Dispatcher.CreateTimer();
            _waveTimer.Interval = TimeSpan.FromMilliseconds(PlaybackWaveTickMs);
            _waveTimer.Tick += (_, __) =>
            {
                var combined = 0.5f;

                if (waveform != null && waveform.Length > 0)
                {
                    var idx = _waveIndex++ % waveform.Length;
                    combined = Math.Clamp(waveform[idx] / 100f, 0f, 1f);
                }
                else
                {
                    var level = Math.Abs(Math.Sin(_wavePhase));
                    var harmonic = Math.Abs(Math.Sin(_wavePhase * 0.37));
                    combined = (float)Math.Clamp(level * 0.7 + harmonic * 0.3, 0f, 1f);
                    _wavePhase += 0.35;
                }

                _playbackWave.AddSample(combined);
                WaveView.Invalidate();
            };
            _waveTimer.Start();
        }

        private void StopPlaybackWave()
        {
            if (_waveTimer != null)
            {
                _waveTimer.Stop();
                _waveTimer = null;
            }

            _playbackWave.Reset();
            WaveView.Invalidate();
            _isPlaying = false;
            PlayButton.Text = "▶";
        }

        private static Task ShowAlertAsync(string message)
        {
            var page = Application.Current?.MainPage;
            return page != null ? page.DisplayAlert("Info", message, "OK") : Task.CompletedTask;
        }

        private interface IAudioPlayback
        {
            event EventHandler? PlaybackEnded;
            Task PlayAsync(string filePath);
            void StopPlaybackSafe();
        }

        private static class AudioPlaybackFactory
        {
            public static IAudioPlayback Create()
            {
#if ANDROID
                return new AndroidAudioPlayback();
#elif WINDOWS
                return new WindowsAudioPlayback();
#else
                return new NoopAudioPlayback();
#endif
            }
        }

        private sealed class NoopAudioPlayback : IAudioPlayback
        {
            public event EventHandler? PlaybackEnded;
            public Task PlayAsync(string filePath) => throw new NotSupportedException("Playback audio supportato solo su Android/Windows (per ora).");
            public void StopPlaybackSafe() { }
        }

#if ANDROID
        private sealed class AndroidAudioPlayback : IAudioPlayback
        {
            private Android.Media.MediaPlayer? _player;
            public event EventHandler? PlaybackEnded;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();
                _player = new Android.Media.MediaPlayer();
                _player.SetDataSource(filePath);
                _player.Prepare();
                _player.Completion += OnCompleted;
                _player.Start();
                return Task.CompletedTask;
            }

            private void OnCompleted(object? sender, EventArgs e)
            {
                StopPlaybackSafe();
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            }

            public void StopPlaybackSafe()
            {
                try
                {
                    if (_player != null)
                    {
                        _player.Completion -= OnCompleted;
                        if (_player.IsPlaying) _player.Stop();
                        _player.Release();
                        _player.Dispose();
                        _player = null;
                    }
                }
                catch { }
            }
        }
#endif

#if WINDOWS
        private sealed class WindowsAudioPlayback : IAudioPlayback
        {
            private Windows.Media.Playback.MediaPlayer? _player;
            public event EventHandler? PlaybackEnded;

            public Task PlayAsync(string filePath)
            {
                StopPlaybackSafe();
                _player = new Windows.Media.Playback.MediaPlayer();
                _player.MediaEnded += OnEnded;
                _player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(filePath));
                _player.Play();
                return Task.CompletedTask;
            }

            private void OnEnded(Windows.Media.Playback.MediaPlayer sender, object args)
            {
                StopPlaybackSafe();
                PlaybackEnded?.Invoke(this, EventArgs.Empty);
            }

            public void StopPlaybackSafe()
            {
                try
                {
                    if (_player != null)
                    {
                        _player.MediaEnded -= OnEnded;
                        _player.Pause();
                        _player.Dispose();
                        _player = null;
                    }
                }
                catch { }
            }
        }
#endif
    }
}
