using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Biliardo.App.Servizi_Diagnostics
{
    public sealed class DiagKV
    {
        public DateTimeOffset TsUtc { get; init; }
        public string Name { get; init; } = "";
        public string? Value { get; init; }
    }

    public static class DiagLog
    {
        private static readonly object _sync = new();
        private static readonly List<DiagKV> _fields = new();
        private static readonly StringBuilder _sb = new();
        private static Exception? _lastException;

        public static DateTimeOffset StartUtc { get; private set; }
        public static string? LastStep { get; private set; }
        public static Exception? LastException => _lastException;

        public static void Init()
        {
            lock (_sync)
            {
                _fields.Clear();
                _sb.Clear();
                _lastException = null;
                StartUtc = DateTimeOffset.UtcNow;
                LastStep = null;
            }
        }

        public static void Note(string name, string? value)
        {
            lock (_sync)
            {
                _fields.Add(new DiagKV
                {
                    TsUtc = DateTimeOffset.UtcNow,
                    Name = name,
                    Value = value
                });
            }
        }

        // Overload 1: come prima
        public static void Step(string name)
        {
            LastStep = name;
            AppendLog($"[STEP] {name}");
            Note("Step", name);
        }

        // Overload 2: accetta anche info aggiuntiva (per compatibilità con le chiamate a due argomenti)
        public static void Step(string name, string? info)
        {
            LastStep = name;
            if (string.IsNullOrWhiteSpace(info))
            {
                AppendLog($"[STEP] {name}");
            }
            else
            {
                AppendLog($"[STEP] {name} | {info}");
                Note($"Step.{name}", info);
            }
        }

        public static void AppendLog(string line)
        {
            lock (_sync)
            {
                _sb.AppendLine($"{DateTimeOffset.UtcNow:o} {line}");
            }
        }

        // Overload – compatibilità con chiamate che passano anche un'etichetta
        public static void Exception(string label, Exception ex)
        {
            _lastException = ex;
            AppendLog($"[EXC] {label} -> {ex.GetType().Name}: {ex.Message}");
            AppendLog(ex.ToString());
            Note("Exception.Label", label);
        }

        // Overload storico
        public static void Exception(Exception ex)
        {
            _lastException = ex;
            AppendLog($"[EXC] {ex.GetType().Name}: {ex.Message}");
            AppendLog(ex.ToString());
        }

        public static IReadOnlyList<DiagKV> SnapshotFields()
        {
            lock (_sync) return _fields.ToList();
        }

        public static string SnapshotTextLog()
        {
            lock (_sync) return _sb.ToString();
        }
    }
}
