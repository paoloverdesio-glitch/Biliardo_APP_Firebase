// File: Servizi_Media/VoiceMediaFactory.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Media
{
    public static class VoiceMediaFactory
    {
        public static IVoiceRecorder CreateRecorder()
        {
#if ANDROID
            return new AndroidVoiceRecorder();
#else
            return new NoopVoiceRecorder();
#endif
        }
    }

    public interface IVoiceRecorder
    {
        bool IsRecording { get; }
        bool IsPaused { get; }
        string? CurrentFilePath { get; }

        Task StartAsync(string filePath);
        Task PauseAsync();
        Task ResumeAsync();
        Task StopAsync();
        Task CancelAsync();

        double TryGetLevel01();
        long GetElapsedMs();
    }

#if ANDROID
    internal sealed class AndroidVoiceRecorder : IVoiceRecorder
    {
        private readonly object _sync = new();

        private Android.Media.MediaRecorder? _rec;
        private readonly Stopwatch _sw = new();

        private long _pauseAccumulatedMs;
        private long _pauseStartedAtMs;
        private long _lastElapsedMs;

        public bool IsRecording { get; private set; }
        public bool IsPaused { get; private set; }
        public string? CurrentFilePath { get; private set; }

        public Task StartAsync(string filePath)
        {
            lock (_sync)
            {
                if (IsRecording)
                    return Task.CompletedTask;

                _lastElapsedMs = 0;
                _pauseAccumulatedMs = 0;
                _pauseStartedAtMs = 0;
                IsPaused = false;

                CurrentFilePath = filePath;

                var r = new Android.Media.MediaRecorder();
                r.SetAudioSource(Android.Media.AudioSource.Mic);
                r.SetOutputFormat(Android.Media.OutputFormat.Mpeg4);
                r.SetAudioEncoder(Android.Media.AudioEncoder.Aac);

                r.SetAudioEncodingBitRate(128000);
                r.SetAudioSamplingRate(44100);

                r.SetOutputFile(filePath);

                r.Prepare();
                r.Start();

                _rec = r;
                IsRecording = true;
                _sw.Restart();
            }

            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            lock (_sync)
            {
                if (!IsRecording || IsPaused)
                    return Task.CompletedTask;

                try { _rec?.Pause(); } catch { }

                IsPaused = true;
                _pauseStartedAtMs = _sw.ElapsedMilliseconds;
            }

            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            lock (_sync)
            {
                if (!IsRecording || !IsPaused)
                    return Task.CompletedTask;

                try { _rec?.Resume(); } catch { }

                IsPaused = false;

                if (_pauseStartedAtMs > 0)
                {
                    _pauseAccumulatedMs += Math.Max(0, _sw.ElapsedMilliseconds - _pauseStartedAtMs);
                    _pauseStartedAtMs = 0;
                }
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            lock (_sync)
            {
                if (!IsRecording)
                    return Task.CompletedTask;

                // Salvo durata finale prima di smontare tutto
                _lastElapsedMs = ComputeElapsedMsNoLock();

                try { _rec?.Stop(); } catch { }
                try { _rec?.Release(); } catch { }
                _rec = null;

                IsRecording = false;
                IsPaused = false;

                _sw.Stop();
                _pauseAccumulatedMs = 0;
                _pauseStartedAtMs = 0;
            }

            return Task.CompletedTask;
        }

        public async Task CancelAsync()
        {
            string? path;
            lock (_sync) { path = CurrentFilePath; }

            try { await StopAsync(); } catch { }

            if (!string.IsNullOrWhiteSpace(path))
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            lock (_sync)
            {
                CurrentFilePath = null;
                _lastElapsedMs = 0;
            }
        }

        public double TryGetLevel01()
        {
            lock (_sync)
            {
                if (!IsRecording || _rec == null)
                    return 0;

                try
                {
                    var amp = _rec.MaxAmplitude; // 0..32767
                    if (amp <= 0) return 0;
                    return Math.Min(1.0, amp / 32767.0);
                }
                catch
                {
                    return 0;
                }
            }
        }

        public long GetElapsedMs()
        {
            lock (_sync)
            {
                if (IsRecording)
                    return ComputeElapsedMsNoLock();

                return _lastElapsedMs;
            }
        }

        private long ComputeElapsedMsNoLock()
        {
            long elapsed = _sw.ElapsedMilliseconds;

            // Se sono in pausa: il tempo "corrente" si ferma all'istante della pausa
            if (IsPaused && _pauseStartedAtMs > 0)
                elapsed = _pauseStartedAtMs;

            var net = elapsed - _pauseAccumulatedMs;
            if (net < 0) net = 0;
            return net;
        }
    }
#endif

    internal sealed class NoopVoiceRecorder : IVoiceRecorder
    {
        public bool IsRecording => false;
        public bool IsPaused => false;
        public string? CurrentFilePath => null;

        public Task StartAsync(string filePath) => throw new NotSupportedException("Registrazione vocale non implementata su questa piattaforma.");
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;

        public double TryGetLevel01() => 0;
        public long GetElapsedMs() => 0;
    }
}
