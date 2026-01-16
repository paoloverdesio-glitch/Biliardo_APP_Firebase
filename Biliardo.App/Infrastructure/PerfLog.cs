using System;

namespace Biliardo.App.Infrastructure
{
    public static partial class PerfLog
    {
#if !ANDROID
        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }

        public static bool IsActive => false;

        public static void Note(string eventName, string? detail = null, long value1 = 0, long value2 = 0)
        {
        }

        public static IDisposable Span(string spanName, string? detail = null, long value1 = 0, long value2 = 0)
            => NoopScope.Instance;

        public static void SetContext(string key, string value)
        {
        }
#endif
    }
}
