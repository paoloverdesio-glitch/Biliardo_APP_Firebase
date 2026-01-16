#if ANDROID
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Android.App;
using Android.Bluetooth;
using Android.OS;
using Biliardo.App.Infrastructure;

namespace Biliardo.App.Platforms.Android.Bluetooth
{
    internal static class BtPerfLogServer
    {
        private const string ServiceName = "BiliardoPerfLog";
        private static readonly Java.Util.UUID ServiceUuid = Java.Util.UUID.FromString("8e9b2a54-5d9b-4d8b-9b6a-2b6d8b2e4f10");
        private static readonly object Gate = new();
        private static bool _isStarted;
        private static BluetoothAdapter? _adapter;
        private static Activity? _activity;
        private static BluetoothServerSocket? _serverSocket;
        private static Thread? _acceptThread;
        private static FrameJankMonitor? _frameJankMonitor;
        private static MainLooperSlowDispatchMonitor? _looperMonitor;

        // Protocol notes:
        // PC opens RFCOMM SPP and sends lines (\n).
        // START_LOG <label> -> OK START file=<name> ts=<elapsed_ns>\n
        // STOP_LOG -> FILE name=<file> size=<bytes>\n<raw bytes>EOF\n
        public static void TryStart(Activity activity)
        {
            lock (Gate)
            {
                if (_isStarted)
                    return;

                _adapter = BluetoothAdapter.DefaultAdapter;
                if (_adapter == null)
                {
                    Debug.WriteLine("[BtPerfLogServer] Bluetooth adapter not available.");
                    return;
                }

                _activity = activity;
                _serverSocket = _adapter.ListenUsingRfcommWithServiceRecord(ServiceName, ServiceUuid);

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "BtPerfLogAccept"
                };
                _acceptThread.Start();

                _frameJankMonitor = new FrameJankMonitor();
                _looperMonitor = new MainLooperSlowDispatchMonitor();
                _isStarted = true;
            }
        }

        public static void Stop()
        {
            lock (Gate)
            {
                _isStarted = false;
                try { _serverSocket?.Close(); } catch { }
                _serverSocket = null;
            }
        }

        private static void AcceptLoop()
        {
            while (true)
            {
                BluetoothSocket? socket = null;
                try
                {
                    lock (Gate)
                    {
                        if (!_isStarted || _serverSocket == null)
                            return;

                        socket = _serverSocket.Accept();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BtPerfLogServer] Accept error: {ex.Message}");
                    Thread.Sleep(500);
                    continue;
                }

                if (socket == null)
                    continue;

                _ = Task.Run(() => HandleClientAsync(socket));
            }
        }

        private static async Task HandleClientAsync(BluetoothSocket socket)
        {
            try
            {
                using var input = socket.InputStream;
                using var output = socket.OutputStream;
                using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                using var writer = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\n", AutoFlush = true };

                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null)
                        break;

                    var trimmed = line.Trim();
                    if (trimmed.Length == 0)
                        continue;

                    var command = trimmed;
                    string? arg = null;
                    var split = trimmed.IndexOf(' ');
                    if (split > 0)
                    {
                        command = trimmed[..split].Trim();
                        arg = trimmed[(split + 1)..].Trim();
                    }

                    switch (command.ToUpperInvariant())
                    {
                        case "PING":
                            await writer.WriteLineAsync("PONG");
                            break;
                        case "HELP":
                            await writer.WriteLineAsync("PING|HELP|STATUS|START_LOG [label]|STOP_LOG");
                            break;
                        case "STATUS":
                            var active = PerfLogSession.IsActive ? 1 : 0;
                            PerfLogSession.TryGetLastFileInfo(out var lastFile, out var lastSize);
                            await writer.WriteLineAsync($"STATUS active={active} file={lastFile} size={lastSize}");
                            break;
                        case "START_LOG":
                            await HandleStartLogAsync(writer, arg);
                            break;
                        case "STOP_LOG":
                            await HandleStopLogAsync(writer, output);
                            break;
                        default:
                            await writer.WriteLineAsync("ERR unknown_command");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BtPerfLogServer] Client error: {ex.Message}");
            }
            finally
            {
                try { socket.Close(); } catch { }
            }
        }

        private static Task HandleStartLogAsync(StreamWriter writer, string? label)
        {
            if (_activity == null)
                return writer.WriteLineAsync("ERR no_activity");

            if (!PerfLogSession.TryStart(_activity, label, out var info))
                return writer.WriteLineAsync("ERR already_active");

            if (PerfLogSession.IsActive)
            {
                _frameJankMonitor?.Start(_activity);
                _looperMonitor?.Start();
            }

            return writer.WriteLineAsync($"OK START file={info.FileName} ts={info.StartElapsedNs}");
        }

        private static async Task HandleStopLogAsync(StreamWriter writer, Stream output)
        {
            _frameJankMonitor?.Stop();
            _looperMonitor?.Stop();

            var info = PerfLogSession.Stop();
            if (info == null)
            {
                await writer.WriteLineAsync("ERR not_active");
                return;
            }

            await writer.WriteLineAsync($"FILE name={info.FileName} size={info.SizeBytes}");
            await writer.FlushAsync();

            try
            {
                using var fileStream = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await fileStream.CopyToAsync(output);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BtPerfLogServer] Transfer error: {ex.Message}");
                return;
            }

            await writer.WriteLineAsync("EOF");
        }
    }
}
#endif
