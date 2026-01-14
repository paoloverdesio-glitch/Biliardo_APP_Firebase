using System;

namespace Biliardo.App.Infrastructure
{
    public static class PerfettoTrace
    {
        private sealed class NoopSection : IDisposable
        {
            public static readonly NoopSection Instance = new();
            public void Dispose() { }
        }

        public static IDisposable Section(string name) => NoopSection.Instance;
    }
}
