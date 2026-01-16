#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Biliardo.App.Infrastructure
{
    internal readonly record struct PerfLogRecord(
        long TimestampNs,
        int Tid,
        int ManagedTid,
        string ThreadName,
        string Kind,
        string Name,
        long DurationUs,
        long Value1,
        long Value2,
        string? Detail)
    {
        public static PerfLogRecord Create(string kind, string name, long durationUs, long value1, long value2, string? detail)
        {
            var tid = Android.OS.Process.MyTid();
            var managedTid = Environment.CurrentManagedThreadId;
            var threadName = Thread.CurrentThread.Name ?? string.Empty;
            var timestampNs = SystemClock.ElapsedRealtimeNanos();
            return new PerfLogRecord(timestampNs, tid, managedTid, threadName, kind, name, durationUs, value1, value2, detail);
        }
    }

    internal sealed record PerfLogSessionInfo(string FilePath, string FileName, long StartElapsedNs);

    internal sealed record PerfLogStopInfo(string FilePath, string FileName, long SizeBytes);

    internal static class PerfLogSession
    {
        private static readonly object Gate = new();
        private static Channel<PerfLogRecord>? _channel;
        private static Task? _writerTask;
        private static CancellationTokenSource? _writerCts;
        private static StreamWriter? _writer;
        private static BufferedStream? _bufferedStream;
        private static FileStream? _fileStream;
        private static bool _isActive;
        private static string? _currentFilePath;
        private static string? _currentFileName;
        private static long _startElapsedNs;
        private static string? _lastFileName;
        private static long _lastFileSize;

        public static bool IsActive
        {
            get
            {
                lock (Gate)
                {
                    return _isActive;
                }
            }
        }

        public static bool TryStart(Context context, string? label, out PerfLogSessionInfo info)
        {
            lock (Gate)
            {
                if (_isActive)
                {
                    info = new PerfLogSessionInfo(
                        _currentFilePath ?? string.Empty,
                        _currentFileName ?? string.Empty,
                        _startElapsedNs);
                    return false;
                }

                var baseDir = context.GetExternalFilesDir(null) ?? context.FilesDir;
                var dir = new Java.IO.File(baseDir, "perf_logs");
                dir.Mkdirs();

                var device = SanitizeFileToken(Build.Device ?? "device");
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var pid = Android.OS.Process.MyPid();
                var fileName = $"perflog_{timestamp}_{device}_{pid}.csv";
                var filePath = Path.Combine(dir.AbsolutePath ?? string.Empty, fileName);

                _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _bufferedStream = new BufferedStream(_fileStream, 64 * 1024);
                _writer = new StreamWriter(_bufferedStream, new UTF8Encoding(false));
                _writer.NewLine = "\n";

                _startElapsedNs = SystemClock.ElapsedRealtimeNanos();
                WriteHeader(context, label);
                _writer.Flush();

                _channel = Channel.CreateUnbounded<PerfLogRecord>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

                _writerCts = new CancellationTokenSource();
                _writerTask = Task.Run(() => WriterLoopAsync(_writerCts.Token));

                _currentFilePath = filePath;
                _currentFileName = fileName;
                _isActive = true;

                info = new PerfLogSessionInfo(filePath, fileName, _startElapsedNs);
                return true;
            }
        }

        public static PerfLogStopInfo? Stop()
        {
            Channel<PerfLogRecord>? channel;
            Task? writerTask;
            CancellationTokenSource? cts;
            string? filePath;
            string? fileName;

            lock (Gate)
            {
                if (!_isActive)
                    return null;

                _isActive = false;
                channel = _channel;
                writerTask = _writerTask;
                cts = _writerCts;
                filePath = _currentFilePath;
                fileName = _currentFileName;

                _channel = null;
                _writerTask = null;
                _writerCts = null;
                _currentFilePath = null;
                _currentFileName = null;
            }

            try
            {
                channel?.Writer.TryComplete();
                cts?.Cancel();
                writerTask?.GetAwaiter().GetResult();
            }
            catch
            {
            }
            finally
            {
                try { _writer?.Flush(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _bufferedStream?.Dispose(); } catch { }
                try { _fileStream?.Dispose(); } catch { }
                _writer = null;
                _bufferedStream = null;
                _fileStream = null;
            }

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                var size = new FileInfo(filePath).Length;
                lock (Gate)
                {
                    _lastFileName = fileName;
                    _lastFileSize = size;
                }

                return new PerfLogStopInfo(filePath!, fileName ?? string.Empty, size);
            }

            return null;
        }

        public static bool TryGetLastFileInfo(out string fileName, out long sizeBytes)
        {
            lock (Gate)
            {
                fileName = _lastFileName ?? string.Empty;
                sizeBytes = _lastFileSize;
                return !string.IsNullOrWhiteSpace(_lastFileName);
            }
        }

        public static void Enqueue(PerfLogRecord record)
        {
            Channel<PerfLogRecord>? channel;
            lock (Gate)
            {
                if (!_isActive)
                    return;

                channel = _channel;
            }

            if (channel == null)
                return;

            channel.Writer.TryWrite(record);
        }

        private static async Task WriterLoopAsync(CancellationToken token)
        {
            if (_channel == null || _writer == null)
                return;

            var reader = _channel.Reader;
            try
            {
                await foreach (var record in reader.ReadAllAsync(token))
                {
                    WriteRecord(_writer, record);
                }
            }
            catch
            {
            }
        }

        private static void WriteHeader(Context context, string? label)
        {
            var appVersion = GetAppVersion(context);
            var androidVersion = Build.VERSION.Release ?? "unknown";
            var device = Build.Device ?? "unknown";
            var model = Build.Model ?? "unknown";
            var build = GetBuildFlavor();

            _writer?.WriteLine($"# app_version={appVersion}");
            _writer?.WriteLine($"# android={androidVersion}");
            _writer?.WriteLine($"# device={device}");
            _writer?.WriteLine($"# model={model}");
            _writer?.WriteLine($"# build={build}");
            _writer?.WriteLine($"# start_elapsed_ns={_startElapsedNs}");
            if (!string.IsNullOrWhiteSpace(label))
                _writer?.WriteLine($"# label={label}");
            _writer?.WriteLine("ts_elapsed_ns,tid,managed_tid,thread_name,kind,name,dur_us,value1,value2,detail");
        }

        private static string GetBuildFlavor()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        private static string GetAppVersion(Context context)
        {
            try
            {
                var pm = context.PackageManager;
                if (pm == null)
                    return "unknown";

                var packageName = context.PackageName;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
                {
                    var info = pm.GetPackageInfo(packageName, Android.Content.PM.PackageManager.PackageInfoFlags.Of(0));
                    return $"{info?.VersionName} ({info?.LongVersionCode})";
                }

                var legacyInfo = pm.GetPackageInfo(packageName, 0);
                return $"{legacyInfo?.VersionName} ({legacyInfo?.VersionCode})";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string SanitizeFileToken(string token)
        {
            var builder = new StringBuilder(token.Length);
            foreach (var ch in token)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                    builder.Append(ch);
                else
                    builder.Append('_');
            }

            return builder.Length == 0 ? "device" : builder.ToString();
        }

        private static void WriteRecord(StreamWriter writer, PerfLogRecord record)
        {
            writer.Write(record.TimestampNs);
            writer.Write(',');
            writer.Write(record.Tid);
            writer.Write(',');
            writer.Write(record.ManagedTid);
            writer.Write(',');
            WriteCsvField(writer, record.ThreadName);
            writer.Write(',');
            WriteCsvField(writer, record.Kind);
            writer.Write(',');
            WriteCsvField(writer, record.Name);
            writer.Write(',');
            writer.Write(record.DurationUs);
            writer.Write(',');
            writer.Write(record.Value1);
            writer.Write(',');
            writer.Write(record.Value2);
            writer.Write(',');
            WriteCsvField(writer, record.Detail ?? string.Empty);
            writer.Write('\n');
        }

        private static void WriteCsvField(StreamWriter writer, string value)
        {
            writer.Write('"');
            foreach (var ch in value)
            {
                if (ch == '"')
                    writer.Write("\"\"");
                else if (ch == '\n' || ch == '\r')
                    writer.Write(' ');
                else
                    writer.Write(ch);
            }
            writer.Write('"');
        }
    }
}
#endif
