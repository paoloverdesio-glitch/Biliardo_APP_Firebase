using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public sealed class FirebaseTransferDebugMonitor : BindableObject
    {
        private const string PreferenceKey = "DebugTransferOverlayEnabled";

        // Throttling UI progress update: 100 ms => max ~10 update/sec per trasferimento.
        private const int StorageProgressUiMinIntervalMs = 100;

        // Per rendere visibile il pallino anche se la chiamata dura pochi ms:
        // lo teniamo in UI almeno per un frame abbondante (50ms), poi scompare.
        private const int ApiDotMinVisibleMs = 50;

        private readonly object _lock = new();
        private readonly Dictionary<Guid, BarTransferVm> _storageTransfers = new();
        private readonly Dictionary<Guid, DotTransferVm> _apiTransfers = new();

        // Per evitare di martellare la UI.
        private readonly Dictionary<Guid, long> _lastStorageUiTick = new();

        private bool _showOverlay;

        public static FirebaseTransferDebugMonitor Instance { get; } = new();

        public ObservableCollection<BarTransferVm> ActiveStorageTransfers { get; } = new();
        public ObservableCollection<BarTransferVm> TopStorageTransfers { get; } = new();
        public ObservableCollection<DotTransferVm> ActiveApiTransfers { get; } = new();

        private FirebaseTransferDebugMonitor()
        {
            _showOverlay = Preferences.Default.Get(PreferenceKey, true);
        }

        public bool ShowOverlay
        {
            get => _showOverlay;
            set
            {
                if (_showOverlay == value) return;
                _showOverlay = value;
                Preferences.Default.Set(PreferenceKey, value);
                OnPropertyChanged();
            }
        }

        public StorageToken BeginStorage(TransferDirection direction, string fileName, string storagePath, long totalBytes)
        {
            var token = new StorageToken();
            var vm = new BarTransferVm
            {
                Direction = direction,
                FileName = fileName ?? "",
                StoragePath = storagePath ?? "",
                TotalBytes = totalBytes,
                TransferredBytes = 0,
                StartTime = DateTime.Now
            };

            lock (_lock)
            {
                _storageTransfers[token.Id] = vm;
                _lastStorageUiTick[token.Id] = 0;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ActiveStorageTransfers.Add(vm);
                RecalculateTopStorage(); // SOLO quando cambia l’elenco (add/remove)
            });

            return token;
        }

        public void ReportStorageProgress(StorageToken token, long bytesTransferred)
        {
            if (token == null) return;

            BarTransferVm? vm;
            long lastTick;
            lock (_lock)
            {
                if (!_storageTransfers.TryGetValue(token.Id, out vm)) return;
                _lastStorageUiTick.TryGetValue(token.Id, out lastTick);
            }

            if (bytesTransferred < 0) bytesTransferred = 0;
            if (vm.TotalBytes > 0 && bytesTransferred > vm.TotalBytes)
                bytesTransferred = vm.TotalBytes;

            var nowTick = Environment.TickCount64;
            var force = vm.TotalBytes > 0 && bytesTransferred >= vm.TotalBytes;
            if (!force && (nowTick - lastTick) < StorageProgressUiMinIntervalMs)
                return;

            lock (_lock)
            {
                _lastStorageUiTick[token.Id] = nowTick;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.TransferredBytes = bytesTransferred;
            });
        }

        public void EndStorage(StorageToken token, bool success, string? errorMessage)
        {
            if (token == null) return;

            BarTransferVm? vm;
            lock (_lock)
            {
                if (!_storageTransfers.TryGetValue(token.Id, out vm)) return;
                _storageTransfers.Remove(token.Id);
                _lastStorageUiTick.Remove(token.Id);
            }

            var endTime = DateTime.Now;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.EndTime = endTime;
                vm.DurationMs = (long)(endTime - vm.StartTime).TotalMilliseconds;
                vm.Success = success;
                vm.ErrorMessage = errorMessage ?? "";

                if (vm.TotalBytes > 0 && vm.TransferredBytes < vm.TotalBytes && success)
                    vm.TransferredBytes = vm.TotalBytes;

                ActiveStorageTransfers.Remove(vm);
                RecalculateTopStorage(); // SOLO quando cambia l’elenco (add/remove)
            });

            _ = Task.Run(() => CsvLoggers.AppendBarAsync(vm));
        }

        public ApiToken BeginApi(TransferDirection direction, string method, string endpointLabel, long? requestBytes)
        {
            var token = new ApiToken();
            var vm = new DotTransferVm
            {
                Direction = direction,
                Method = method ?? "",
                EndpointLabel = endpointLabel ?? "",
                RequestBytes = requestBytes,
                StartTime = DateTime.Now
            };

            lock (_lock)
            {
                _apiTransfers[token.Id] = vm;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ActiveApiTransfers.Add(vm);
            });

            return token;
        }

        public void EndApi(ApiToken token, bool success, int? statusCode, long? responseBytes, string? errorMessage)
        {
            if (token == null) return;

            DotTransferVm? vm;
            lock (_lock)
            {
                if (!_apiTransfers.TryGetValue(token.Id, out vm)) return;
                _apiTransfers.Remove(token.Id);
            }

            var endTime = DateTime.Now;

            // Aggiorno i campi finali subito (UI thread).
            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.EndTime = endTime;
                vm.DurationMs = (long)(endTime - vm.StartTime).TotalMilliseconds;
                vm.Success = success;
                vm.StatusCode = statusCode;
                vm.ResponseBytes = responseBytes;
                vm.ErrorMessage = errorMessage ?? "";
            });

            // Rimozione "minimamente ritardata" per garantire che la UI faccia in tempo a renderizzare il pallino.
            var delay = ApiDotMinVisibleMs;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > 0)
                        await Task.Delay(delay).ConfigureAwait(false);
                }
                catch { }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    ActiveApiTransfers.Remove(vm);
                });
            });

            _ = Task.Run(() => CsvLoggers.AppendDotAsync(vm));
        }

        private void RecalculateTopStorage()
        {
            // Top 8 più grandi per TotalBytes (come richiesto).
            var ordered = ActiveStorageTransfers
                .OrderByDescending(x => x.TotalBytes)
                .ThenBy(x => x.StartTime)
                .Take(8)
                .ToList();

            TopStorageTransfers.Clear();
            foreach (var vm in ordered)
                TopStorageTransfers.Add(vm);
        }
    }

    public sealed class StorageToken
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }

    public sealed class ApiToken
    {
        internal Guid Id { get; } = Guid.NewGuid();
    }
}
