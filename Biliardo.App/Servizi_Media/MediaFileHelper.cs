using System;
using System.IO;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Diagnostics;
using Microsoft.Maui.Storage;

namespace Biliardo.App.Servizi_Media
{
    public static class MediaFileHelper
    {
        public static async Task<string> CopyToCacheAsync(FileResult fr, string prefix)
        {
            if (fr == null) throw new ArgumentNullException(nameof(fr));

            var ext = Path.GetExtension(fr.FileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

            var dest = Path.Combine(
                FileSystem.CacheDirectory,
                $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");

            await using var src = await fr.OpenReadAsync();
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst);

            return dest;
        }

        public static async Task<long> WaitForStableFileSizeAsync(string filePath, int attempts = 3, int delayMs = 80)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return 0;

            long lastSize = -1;
            for (var i = 0; i < attempts; i++)
            {
                long size = 0;
                try { size = new FileInfo(filePath).Length; } catch { }

                if (size > 0 && size == lastSize)
                    return size;

                lastSize = size;
                await Task.Delay(delayMs);
            }

            return lastSize < 0 ? 0 : lastSize;
        }

        public static void LogFileSnapshot(string label, string? filePath, string? contentType = null)
        {
            long size = 0;
            var exists = false;

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                exists = true;
                try { size = new FileInfo(filePath).Length; } catch { }
            }

            DiagLog.AppendLog($"[MEDIA] {label} | path={(filePath ?? "<null>")} | exists={exists} | size={size} | contentType={(contentType ?? "")}");
        }
    }
}
