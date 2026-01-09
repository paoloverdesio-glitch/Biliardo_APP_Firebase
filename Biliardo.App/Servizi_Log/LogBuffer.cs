using System.Collections.Concurrent;
using System.Text;

namespace Biliardo.App.Servizi_Log;

/// <summary>
/// Buffer di log in-memory, thread-safe, con esportazione testo.
/// Non introduce side-effect: può essere usato ovunque.
/// </summary>
internal static class LogBuffer
{
    private const int MaxLines = 1000;
    private static readonly ConcurrentQueue<string> _lines = new();
    private static int _count = 0;

    // Contesto opzionale (impostabile in App.xaml.cs più avanti)
    private static volatile string _clientContext = "n/d";

    public static void SetClientContext(string context)
    {
        _clientContext = string.IsNullOrWhiteSpace(context) ? "n/d" : context.Trim();
    }

    public static void Info(string area, string message) => Write("INFO", area, message, null);
    public static void Warn(string area, string message) => Write("WARN", area, message, null);
    public static void Error(string area, string message, Exception? ex = null) => Write("ERROR", area, message, ex);
    public static void Net(string method, string path, int? status = null, long? ms = null, string? note = null)
        => Write("NET", "ApiClient", $"{method} {path} {(status is null ? "" : $"→ {status}")} {(ms is null ? "" : $"[{ms} ms]")} {note}".Trim(), null);
    public static void Nav(string from, string to, string? note = null)
        => Write("NAV", "Navigation", $"{from} → {to} {note}".Trim(), null);

    private static void Write(string level, string area, string message, Exception? ex)
    {
        var now = DateTime.UtcNow; // tracciamo in UTC
        var line = $"{now:O} [{level}] {area}: {message}".TrimEnd();

        if (ex != null)
        {
            line += $" | EX: {ex.GetType().Name}: {ex.Message}";
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                line += $" | ST: {ex.StackTrace}";
        }

        _lines.Enqueue(line);
        var newCount = Interlocked.Increment(ref _count);

        // Limitazione dimensione (FIFO)
        while (newCount > MaxLines && _lines.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _count);
            newCount--;
        }
    }

    /// <summary>
    /// Esporta tutto il buffer come testo plain, pronto per email.
    /// </summary>
    public static string Export()
    {
        var sb = new StringBuilder(64 * 1024);
        sb.AppendLine("== Biliardo.App Log Export ==");
        sb.AppendLine($"Client: {_clientContext}");
        sb.AppendLine($"UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"Lines: {Math.Max(_count, 0)}");
        sb.AppendLine(new string('-', 80));

        foreach (var line in _lines)
            sb.AppendLine(line);

        return sb.ToString();
    }
}
