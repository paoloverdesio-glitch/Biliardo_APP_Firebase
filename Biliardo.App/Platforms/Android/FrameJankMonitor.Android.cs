#if ANDROID
using System;
using Android.App;
using Android.OS;
using Android.Views;

namespace Biliardo.App.Infrastructure
{
    internal sealed class FrameJankMonitor : Java.Lang.Object, Window.IOnFrameMetricsAvailableListener
    {
        private readonly long _thresholdNs;
        private readonly long _veryJankThresholdNs;
        private int _lastGc0;
        private int _lastGc1;
        private int _lastGc2;
        private bool _hasGcBaseline;
        private Window? _window;

        public FrameJankMonitor(double thresholdMs = 16.7, double veryJankThresholdMs = 33.4)
        {
            _thresholdNs = (long)(thresholdMs * 1_000_000.0);
            _veryJankThresholdNs = (long)(veryJankThresholdMs * 1_000_000.0);
        }

        public bool Start(Activity activity)
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.N)
                return false;

            _window = activity.Window;
            if (_window == null)
                return false;

            _lastGc0 = GC.CollectionCount(0);
            _lastGc1 = GC.CollectionCount(1);
            _lastGc2 = GC.CollectionCount(2);
            _hasGcBaseline = true;

            _window.AddOnFrameMetricsAvailableListener(this, null);
            return true;
        }

        public void Stop()
        {
            if (_window == null)
                return;

            if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                _window.RemoveOnFrameMetricsAvailableListener(this);

            _window = null;
        }

        public void OnFrameMetricsAvailable(Window window, FrameMetrics frameMetrics, int dropCount)
        {
            if (!PerfLogSession.IsActive)
                return;

            var totalNs = frameMetrics.GetMetric(FrameMetrics.TotalDuration);
            if (totalNs < _thresholdNs)
                return;

            var layoutNs = frameMetrics.GetMetric(FrameMetrics.LayoutMeasureDuration);
            var drawNs = frameMetrics.GetMetric(FrameMetrics.DrawDuration);
            var syncNs = frameMetrics.GetMetric(FrameMetrics.SyncDuration);
            var issueNs = frameMetrics.GetMetric(FrameMetrics.CommandIssueDuration);
            var swapNs = frameMetrics.GetMetric(FrameMetrics.SwapBuffersDuration);

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var gc2 = GC.CollectionCount(2);
            var delta0 = _hasGcBaseline ? gc0 - _lastGc0 : 0;
            var delta1 = _hasGcBaseline ? gc1 - _lastGc1 : 0;
            var delta2 = _hasGcBaseline ? gc2 - _lastGc2 : 0;
            _lastGc0 = gc0;
            _lastGc1 = gc1;
            _lastGc2 = gc2;
            _hasGcBaseline = true;

            var managedBytes = GC.GetTotalMemory(false);
            var nativeBytes = Android.OS.Debug.GetNativeHeapAllocatedSize();
            var dropped = Math.Max(0, (int)(totalNs / 16_666_667) - 1);
            var totalUs = totalNs / 1_000;

            var detail = string.Join(";", new[]
            {
                $"total_ms={ToMs(totalNs):F1}",
                $"layout_ms={ToMs(layoutNs):F1}",
                $"draw_ms={ToMs(drawNs):F1}",
                $"sync_ms={ToMs(syncNs):F1}",
                $"issue_ms={ToMs(issueNs):F1}",
                $"swap_ms={ToMs(swapNs):F1}",
                $"dropped={dropped}",
                $"jank={(totalNs >= _veryJankThresholdNs ? "2" : "1")}",
                $"gc0=+{delta0}",
                $"gc1=+{delta1}",
                $"gc2=+{delta2}",
                $"mem_managed_mb={managedBytes / (1024.0 * 1024.0):F1}",
                $"mem_native_mb={nativeBytes / (1024.0 * 1024.0):F1}"
            });

            var record = PerfLogRecord.Create(
                kind: "FRAME",
                name: "FrameMetrics",
                durationUs: totalUs,
                value1: dropped,
                value2: managedBytes / 1024,
                detail: detail);

            PerfLogSession.Enqueue(record);
        }

        private static double ToMs(long ns) => ns <= 0 ? 0 : ns / 1_000_000.0;
    }
}
#endif
