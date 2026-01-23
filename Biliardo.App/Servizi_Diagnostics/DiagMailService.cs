//Servizi_Diagnostics / DiagMailService.cs
using System.Diagnostics;
using System.Text;

namespace Biliardo.App.Servizi_Diagnostics
{
    /// <summary>
    /// Diagnostica Firebase-only:
    /// - Non invia più al server (Biliardo.Api).
    /// - Genera un file .txt con log + campi e apre lo share sheet del sistema.
    /// </summary>
    public static class DiagMailService
    {
        public static async Task<bool> SendNowAsync(string contextLabel = "")
        {
            try
            {
                var fields = DiagLog.SnapshotFields().OrderBy(kv => kv.TsUtc).ToList();
                var textLog = DiagLog.SnapshotTextLog();

                var sb = new StringBuilder();
                sb.AppendLine($"Context: {contextLabel}");
                sb.AppendLine($"NowUtc: {DateTimeOffset.UtcNow:o}");
                sb.AppendLine();
                sb.AppendLine("=== FIELDS ===");
                foreach (var f in fields)
                    sb.AppendLine($"{f.TsUtc:o} | {f.Name} = {f.Value}");
                sb.AppendLine();
                sb.AppendLine("=== LOG ===");
                sb.AppendLine(textLog ?? "");

                var fileName = $"biliardo_diag_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.txt";

                // FileSystem.CacheDirectory può essere null in ambienti particolari: fallback a LocalApplicationData
                var cacheDir = FileSystem.CacheDirectory;
                if (string.IsNullOrWhiteSpace(cacheDir))
                    cacheDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                if (!Directory.Exists(cacheDir))
                {
                    try { Directory.CreateDirectory(cacheDir); } catch { /* ignore */ }
                }

                var path = Path.Combine(cacheDir, fileName);

                // Scrittura asincrona per non bloccare il UI thread
                await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);

                if (!File.Exists(path))
                {
                    Debug.WriteLine($"DiagMailService: file not found after write: {path}");
                    return false;
                }

                try
                {
                    Debug.WriteLine($"DiagMailService: sharing file {path}");
                    var req = new ShareFileRequest
                    {
                        Title = "Diagnostica Biliardo",
                        File = new ShareFile(path)
                    };

                    await Share.Default.RequestAsync(req).ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"DiagMailService: share failed: {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DiagMailService: failed to prepare/send diagnostic: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}