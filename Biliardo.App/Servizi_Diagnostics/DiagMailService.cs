using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

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
                var path = Path.Combine(FileSystem.CacheDirectory, fileName);
                File.WriteAllText(path, sb.ToString());

                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Diagnostica Biliardo",
                    File = new ShareFile(path)
                });

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
