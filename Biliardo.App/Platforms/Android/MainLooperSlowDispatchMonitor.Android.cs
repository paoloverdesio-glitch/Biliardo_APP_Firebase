#if ANDROID
using System;
using Android.OS;
using Android.Util;

namespace Biliardo.App.Infrastructure
{
    internal sealed class MainLooperSlowDispatchMonitor
    {
        private readonly long _thresholdNs;
        private readonly DispatchPrinter _printer;
        private bool _isActive;

        public MainLooperSlowDispatchMonitor(double thresholdMs = 8.0)
        {
            _thresholdNs = (long)(thresholdMs * 1_000_000.0);
            _printer = new DispatchPrinter(_thresholdNs);
        }

        public void Start()
        {
            if (_isActive)
                return;

            Looper.MainLooper.SetMessageLogging(_printer);
            _isActive = true;
        }

        public void Stop()
        {
            if (!_isActive)
                return;

            Looper.MainLooper.SetMessageLogging(null);
            _printer.Reset();
            _isActive = false;
        }

        private sealed class DispatchPrinter : Java.Lang.Object, IPrinter
        {
            private const string DispatchPrefix = ">>>>> Dispatching to ";
            private const string FinishPrefix = "<<<<< Finished to ";

            private readonly long _thresholdNs;
            private long _startNs;
            private string? _name;
            private string? _rawLine;

            public DispatchPrinter(long thresholdNs)
            {
                _thresholdNs = thresholdNs;
            }

            public void Println(string? x)
            {
                if (!PerfLogSession.IsActive || string.IsNullOrWhiteSpace(x))
                    return;

                if (x.StartsWith(DispatchPrefix, StringComparison.Ordinal))
                {
                    _startNs = SystemClock.ElapsedRealtimeNanos();
                    _rawLine = x;
                    _name = ExtractName(x, DispatchPrefix);
                    return;
                }

                if (x.StartsWith(FinishPrefix, StringComparison.Ordinal))
                {
                    if (_startNs == 0)
                        return;

                    var endNs = SystemClock.ElapsedRealtimeNanos();
                    var durationNs = endNs - _startNs;
                    _startNs = 0;

                    if (durationNs < _thresholdNs)
                        return;

                    var durationUs = durationNs / 1_000;
                    var name = _name ?? ExtractName(x, FinishPrefix);
                    var detail = _rawLine ?? x;

                    var record = PerfLogRecord.Create(
                        kind: "LOOPER",
                        name: name,
                        durationUs: durationUs,
                        value1: 0,
                        value2: 0,
                        detail: detail);

                    PerfLogSession.Enqueue(record);
                }
            }

            public void Reset()
            {
                _startNs = 0;
                _name = null;
                _rawLine = null;
            }

            private static string ExtractName(string line, string prefix)
            {
                var payload = line[prefix.Length..];
                var index = payload.IndexOf(':');
                if (index > 0)
                    payload = payload[..index];
                return payload.Trim();
            }
        }
    }
}
#endif
