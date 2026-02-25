using Biliardo.App.Servizi_Diagnostics;
using System;
#if ANDROID
using Android.Views;
#endif

namespace Biliardo.App.Pagine_Home
{
    internal interface IHomeFeedJankMonitor
    {
        void Start();
        void Stop();
    }

    internal static class HomeFeedJankMonitorFactory
    {
        public static IHomeFeedJankMonitor Create() => new HomeFeedJankMonitor();
    }

#if ANDROID
    internal sealed class HomeFeedJankMonitor : Java.Lang.Object, IHomeFeedJankMonitor, Choreographer.IFrameCallback
    {
        private const double JankThresholdMs = 24d;
        private bool _running;
        private long _lastFrameNs;
        private int _jankCount;
        private long _worstFrameMs;
        private long _lastFlushTicks;

        public void Start()
        {
            if (_running)
                return;

            _running = true;
            _lastFrameNs = 0;
            _jankCount = 0;
            _worstFrameMs = 0;
            _lastFlushTicks = System.Environment.TickCount64;
            Choreographer.Instance.PostFrameCallback(this);
        }

        public void Stop()
        {
            if (!_running)
                return;

            _running = false;
            Choreographer.Instance.RemoveFrameCallback(this);
            Flush();
        }

        public void DoFrame(long frameTimeNanos)
        {
            if (!_running)
                return;

            if (_lastFrameNs != 0)
            {
                var deltaMs = (frameTimeNanos - _lastFrameNs) / 1_000_000d;
                if (deltaMs > JankThresholdMs)
                {
                    _jankCount++;
                    var rounded = (long)Math.Round(deltaMs);
                    if (rounded > _worstFrameMs)
                        _worstFrameMs = rounded;
                }
            }

            _lastFrameNs = frameTimeNanos;

            var now = System.Environment.TickCount64;
            if (now - _lastFlushTicks >= 2000)
            {
                Flush();
                _lastFlushTicks = now;
            }

            Choreographer.Instance.PostFrameCallback(this);
        }

        private void Flush()
        {
            if (_jankCount <= 0 && _worstFrameMs <= 0)
                return;

            DiagLog.Note("Home.Feed.Jank.frame_drop_count", _jankCount.ToString());
            DiagLog.Note("Home.Feed.Jank.worst_frame_ms", _worstFrameMs.ToString());
            _jankCount = 0;
            _worstFrameMs = 0;
        }
    }
#else
    internal sealed class HomeFeedJankMonitor : IHomeFeedJankMonitor
    {
        public void Start() { }
        public void Stop() { }
    }
#endif
}
