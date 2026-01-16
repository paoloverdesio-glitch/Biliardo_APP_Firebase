#if ANDROID
using System;
using System.Diagnostics;
using System.Threading;
using Android.OS;

namespace Biliardo.App.Infrastructure
{
    public static partial class PerfLog
    {
        public static bool IsActive => PerfLogSession.IsActive;

        public static void Note(string eventName, string? detail = null, long value1 = 0, long value2 = 0)
        {
            if (!PerfLogSession.IsActive)
                return;

            var record = PerfLogRecord.Create(
                kind: "NOTE",
                name: eventName,
                durationUs: 0,
                value1: value1,
                value2: value2,
                detail: detail);

            PerfLogSession.Enqueue(record);
        }

        public static IDisposable Span(string spanName, string? detail = null, long value1 = 0, long value2 = 0)
        {
            if (!PerfLogSession.IsActive)
                return PerfLogSpanScope.Noop;

            return new PerfLogSpanScope(spanName, detail, value1, value2);
        }

        public static void SetContext(string key, string value)
        {
            if (!PerfLogSession.IsActive)
                return;

            var record = PerfLogRecord.Create(
                kind: "CONTEXT",
                name: key,
                durationUs: 0,
                value1: 0,
                value2: 0,
                detail: value);

            PerfLogSession.Enqueue(record);
        }

        private sealed class PerfLogSpanScope : IDisposable
        {
            public static readonly PerfLogSpanScope Noop = new("", null, 0, 0, isNoop: true);

            private readonly string _name;
            private readonly string? _detail;
            private readonly long _value1;
            private readonly long _value2;
            private readonly long _startTimestamp;
            private readonly bool _isNoop;

            public PerfLogSpanScope(string name, string? detail, long value1, long value2, bool isNoop = false)
            {
                _name = name;
                _detail = detail;
                _value1 = value1;
                _value2 = value2;
                _startTimestamp = Stopwatch.GetTimestamp();
                _isNoop = isNoop;
            }

            public void Dispose()
            {
                if (_isNoop || !PerfLogSession.IsActive)
                    return;

                var end = Stopwatch.GetTimestamp();
                var durationUs = (end - _startTimestamp) * 1_000_000 / Stopwatch.Frequency;

                var record = PerfLogRecord.Create(
                    kind: "SPAN",
                    name: _name,
                    durationUs: durationUs,
                    value1: _value1,
                    value2: _value2,
                    detail: _detail);

                PerfLogSession.Enqueue(record);
            }
        }
    }
}
#endif
