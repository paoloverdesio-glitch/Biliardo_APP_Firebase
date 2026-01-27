using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public static class CsvLoggers
    {
        private static readonly SemaphoreSlim FileLock = new(1, 1);

        public static string BarsFilePath => Path.Combine(FileSystem.AppDataDirectory, "TrasfBarre.csv");
        public static string DotsFilePath => Path.Combine(FileSystem.AppDataDirectory, "TrasfPallini.csv");

        private const string BarsHeader = "Tipo,NomeFile,StoragePath,DimensioneKB,DataInizio,OraInizio,MsInizio,DataFine,OraFine,MsFine,DurataMs,Esito,Errore";
        private const string DotsHeader = "Tipo,MetodoHTTP,EndpointLabel,RequestBytes,ResponseBytes,StatusCode,DataInizio,OraInizio,MsInizio,DataFine,OraFine,MsFine,DurataMs,Esito,Errore";

        public static async Task EnsureFilesAsync()
        {
            await EnsureFileAsync(BarsFilePath, BarsHeader);
            await EnsureFileAsync(DotsFilePath, DotsHeader);
        }

        public static async Task EnsureBarsFileAsync() => await EnsureFileAsync(BarsFilePath, BarsHeader);

        public static async Task EnsureDotsFileAsync() => await EnsureFileAsync(DotsFilePath, DotsHeader);

        public static async Task AppendBarAsync(BarTransferVm vm)
        {
            if (vm == null) return;
            await EnsureFileAsync(BarsFilePath, BarsHeader);

            var totalKb = vm.TotalBytes > 0
                ? (vm.TotalBytes / 1024d).ToString("0.0", CultureInfo.InvariantCulture)
                : "";

            var line = string.Join(",",
                Csv(vm.Direction == TransferDirection.Up ? "UP" : "DOWN"),
                Csv(vm.FileName),
                Csv(vm.StoragePath),
                Csv(totalKb),
                Csv(Date(vm.StartTime)),
                Csv(Time(vm.StartTime)),
                Csv(Ms(vm.StartTime)),
                Csv(Date(vm.EndTime)),
                Csv(Time(vm.EndTime)),
                Csv(Ms(vm.EndTime)),
                Csv(vm.DurationMs),
                Csv(vm.Success == true ? "OK" : "FAIL"),
                Csv(vm.ErrorMessage));

            await AppendLineAsync(BarsFilePath, line);
        }

        public static async Task AppendDotAsync(DotTransferVm vm)
        {
            if (vm == null) return;
            await EnsureFileAsync(DotsFilePath, DotsHeader);

            var line = string.Join(",",
                Csv(vm.Direction == TransferDirection.Up ? "UP" : "DOWN"),
                Csv(vm.Method),
                Csv(vm.EndpointLabel),
                Csv(vm.RequestBytes),
                Csv(vm.ResponseBytes),
                Csv(vm.StatusCode),
                Csv(Date(vm.StartTime)),
                Csv(Time(vm.StartTime)),
                Csv(Ms(vm.StartTime)),
                Csv(Date(vm.EndTime)),
                Csv(Time(vm.EndTime)),
                Csv(Ms(vm.EndTime)),
                Csv(vm.DurationMs),
                Csv(vm.Success == true ? "OK" : "FAIL"),
                Csv(vm.ErrorMessage));

            await AppendLineAsync(DotsFilePath, line);
        }

        public static async Task RecreateBarsFileAsync()
        {
            await RecreateFileAsync(BarsFilePath, BarsHeader);
        }

        public static async Task RecreateDotsFileAsync()
        {
            await RecreateFileAsync(DotsFilePath, DotsHeader);
        }

        private static async Task EnsureFileAsync(string path, string header)
        {
            await FileLock.WaitAsync();
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? FileSystem.AppDataDirectory);
                    await File.WriteAllTextAsync(path, header + Environment.NewLine, Encoding.UTF8);
                    return;
                }

                var info = new FileInfo(path);
                if (info.Length == 0)
                    await File.WriteAllTextAsync(path, header + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                FileLock.Release();
            }
        }

        private static async Task RecreateFileAsync(string path, string header)
        {
            await FileLock.WaitAsync();
            try
            {
                if (File.Exists(path))
                    File.Delete(path);

                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? FileSystem.AppDataDirectory);
                await File.WriteAllTextAsync(path, header + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                FileLock.Release();
            }
        }

        private static async Task AppendLineAsync(string path, string line)
        {
            await FileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(path, line + Environment.NewLine, Encoding.UTF8);
            }
            finally
            {
                FileLock.Release();
            }
        }

        private static string Date(DateTime? dt) => dt?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "";

        private static string Time(DateTime? dt) => dt?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "";

        private static string Ms(DateTime? dt) => dt?.Millisecond.ToString(CultureInfo.InvariantCulture) ?? "";

        private static string Csv(object? value)
        {
            if (value == null) return "";
            var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Contains(',') || s.Contains('"'))
            {
                s = s.Replace("\"", "\"\"");
                s = $"\"{s}\"";
            }
            return s;
        }
    }
}
