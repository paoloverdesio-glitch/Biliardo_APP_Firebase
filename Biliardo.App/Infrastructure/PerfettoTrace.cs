using System;
using System.Runtime.CompilerServices;

namespace Biliardo.App.Infrastructure
{
    /// <summary>
    /// Marker per Perfetto/System Trace.
    /// - ANDROID + DEBUG: crea sezioni visibili nel System Trace (Perfetto).
    /// - Altrove: no-op.
    /// Progettato per non crashare mai (Begin/End protetti da try/catch, nome sanitizzato).
    /// </summary>
    public static class PerfettoTrace
    {
#if ANDROID && DEBUG
        /// <summary>
        /// Toggle runtime (DEBUG). Se false, Section() ritorna uno scope no-op.
        /// </summary>
        public static bool Enabled { get; set; } = true;

        // Android Trace section name max ~127 chars (limite storico; in pratica viene troncato).
        private const int MaxSectionNameLength = 127;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SectionScope Section(string name)
            => Enabled ? new SectionScope(SanitizeName(name)) : default;

        private static string SanitizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "TRACE";

            var s = name.Trim();

            // Evita stringhe enormi (anche se Android spesso tronca, qui preveniamo qualsiasi edge)
            if (s.Length > MaxSectionNameLength)
                s = s.Substring(0, MaxSectionNameLength);

            return s;
        }

        public readonly struct SectionScope : IDisposable
        {
            private readonly bool _active;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public SectionScope(string name)
            {
                _active = false;

                if (!Enabled)
                    return;

                try
                {
                    Android.OS.Trace.BeginSection(name);
                    _active = true;
                }
                catch
                {
                    // No-op: se Trace non disponibile/errore runtime non deve mai crashare.
                    _active = false;
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose()
            {
                if (!_active)
                    return;

                try
                {
                    Android.OS.Trace.EndSection();
                }
                catch
                {
                    // No-op
                }
            }
        }
#else
        public static bool Enabled { get; set; } = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SectionScope Section(string name) => default;

        public readonly struct SectionScope : IDisposable
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Dispose() { }
        }
#endif
    }
}
