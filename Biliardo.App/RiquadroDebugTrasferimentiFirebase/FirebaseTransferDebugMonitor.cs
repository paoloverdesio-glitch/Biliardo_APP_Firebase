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

        private readonly object _lock = new();
        private readonly Dictionary<Guid, BarTransferVm> _storageTransfers = new();
        private readonly Dictionary<Guid, DotTransferVm> _apiTransfers = new();

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
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ActiveStorageTransfers.Add(vm);
                RecalculateTopStorage();
            });

            return token;
        }

        public void ReportStorageProgress(StorageToken token, long bytesTransferred)
        {
            if (token == null) return;
            BarTransferVm? vm;
            lock (_lock)
            {
                if (!_storageTransfers.TryGetValue(token.Id, out vm)) return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                vm.TransferredBytes = bytesTransferred;
                RecalculateTopStorage();
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
            }

            var endTime = DateTime.Now;
            vm.EndTime = endTime;
            vm.DurationMs = (long)(endTime - vm.StartTime).TotalMilliseconds;
            vm.Success = success;
            vm.ErrorMessage = errorMessage ?? "";

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ActiveStorageTransfers.Remove(vm);
                RecalculateTopStorage();
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
            vm.EndTime = endTime;
            vm.DurationMs = (long)(endTime - vm.StartTime).TotalMilliseconds;
            vm.Success = success;
            vm.StatusCode = statusCode;
            vm.ResponseBytes = responseBytes;
            vm.ErrorMessage = errorMessage ?? "";

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ActiveApiTransfers.Remove(vm);
            });

            _ = Task.Run(() => CsvLoggers.AppendDotAsync(vm));
        }

        private void RecalculateTopStorage()
        {
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
