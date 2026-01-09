using System;
using System.IO;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Media
{
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

        /// <summary>0..1 (best-effort)</summary>
        double TryGetLevel01();

        /// <summary>Durata registrazione (ms) best-effort</summary>
        long GetElapsedMs();
    }

    public static class VoiceRecorderFactory
    {
        public static IVoiceRecorder Create()
        {
#if ANDROID
            return new AndroidVoiceRecorder();
#else
            return new NoopVoiceRecorder();
#endif
        }
    }

    internal sealed class NoopVoiceRecorder : IVoiceRecorder
    {
        public bool IsRecording => false;
        public bool IsPaused => false;
        public string? CurrentFilePath => null;

        public Task StartAsync(string filePath) => throw new NotSupportedException("Registrazione vocale supportata solo su Android (per ora).");
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResumeAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task CancelAsync() => Task.CompletedTask;

        public double TryGetLevel01() => 0;
        public long GetElapsedMs() => 0;
    }
}
