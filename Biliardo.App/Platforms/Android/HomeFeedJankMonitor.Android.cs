#if ANDROID
using System;
using Android.Views;
using Biliardo.App.Servizi_Diagnostics;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class HomeFeedJankMonitor : Java.Lang.Object, Choreographer.IFrameCallback
    {
        private readonly object _sync = new();
        private bool _running;
        private long _lastFrameNs;
        private int _droppedFrames;
        private int _sampleFrames;
        private DateTimeOffset _lastFlushUtc;

        partial void StartPlatform()
        {
            lock (_sync)
            {
                if (_running)
                    return;

                _running = true;
                _lastFrameNs = 0;
                _droppedFrames = 0;
                _sampleFrames = 0;
                _lastFlushUtc = DateTimeOffset.UtcNow;
            }

            Choreographer.Instance.PostFrameCallback(this);
        }

        partial void StopPlatform()
        {
            lock (_sync)
            {
                if (!_running)
                    return;

                _running = false;
            }

            try { Choreographer.Instance.RemoveFrameCallback(this); } catch { }
            FlushSample(force: true);
        }

        public void DoFrame(long frameTimeNanos)
        {
            bool reschedule;
            lock (_sync)
            {
                if (!_running)
                    return;

                if (_lastFrameNs > 0)
                {
                    var deltaNs = frameTimeNanos - _lastFrameNs;
                    if (deltaNs > 16_666_666)
                    {
                        var skipped = (int)Math.Max(0L, (deltaNs / 16_666_666L) - 1L);
                        _droppedFrames += skipped;
                    }
                }

                _lastFrameNs = frameTimeNanos;
                _sampleFrames++;
                reschedule = _running;
            }

            FlushSample(force: false);

            if (reschedule)
                Choreographer.Instance.PostFrameCallback(this);
        }

        private void FlushSample(bool force)
        {
            int frames;
            int dropped;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            lock (_sync)
            {
                if (!force && (now - _lastFlushUtc) < TimeSpan.FromSeconds(1))
                    return;

                frames = _sampleFrames;
                dropped = _droppedFrames;
                _sampleFrames = 0;
                _droppedFrames = 0;
                _lastFlushUtc = now;
            }

            if (frames <= 0)
                return;

            DiagLog.Note("Home.Jank.sample_frames", frames.ToString());
            DiagLog.Note("Home.Jank.frame_drops", dropped.ToString());
            DiagLog.Note("Home.Jank.drop_ratio", ((double)dropped / frames).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
#endif

