using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.RiquadroDebugTrasferimentiFirebase
{
    public enum TransferDirection
    {
        Up,
        Down
    }

    public abstract class TransferVmBase : BindableObject
    {
        private DateTime _startTime;
        private DateTime? _endTime;
        private long _durationMs;
        private bool? _success;
        private string _errorMessage = "";

        public DateTime StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime == value) return;
                _startTime = value;
                OnPropertyChanged();
            }
        }

        public DateTime? EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime == value) return;
                _endTime = value;
                OnPropertyChanged();
            }
        }

        public long DurationMs
        {
            get => _durationMs;
            set
            {
                if (_durationMs == value) return;
                _durationMs = value;
                OnPropertyChanged();
            }
        }

        public bool? Success
        {
            get => _success;
            set
            {
                if (_success == value) return;
                _success = value;
                OnPropertyChanged();
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage == value) return;
                _errorMessage = value ?? "";
                OnPropertyChanged();
            }
        }
    }

    public sealed class BarTransferVm : TransferVmBase
    {
        private long _totalBytes;
        private long _transferredBytes;

        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public TransferDirection Direction { get; init; }

        public string FileName { get; init; } = "";

        public string StoragePath { get; init; } = "";

        public long TotalBytes
        {
            get => _totalBytes;
            set
            {
                if (_totalBytes == value) return;
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Progress));
            }
        }

        public long TransferredBytes
        {
            get => _transferredBytes;
            set
            {
                if (_transferredBytes == value) return;
                _transferredBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Progress));
            }
        }

        public double Progress => TotalBytes > 0 ? Math.Clamp((double)TransferredBytes / TotalBytes, 0, 1) : 0d;
    }

    public sealed class DotTransferVm : TransferVmBase
    {
        private long? _requestBytes;
        private long? _responseBytes;
        private int? _statusCode;
        private TransferOutcome _outcome;

        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public TransferDirection Direction { get; init; }

        public string Method { get; init; } = "";

        public string EndpointLabel { get; init; } = "";

        public long? RequestBytes
        {
            get => _requestBytes;
            set
            {
                if (_requestBytes == value) return;
                _requestBytes = value;
                OnPropertyChanged();
            }
        }

        public long? ResponseBytes
        {
            get => _responseBytes;
            set
            {
                if (_responseBytes == value) return;
                _responseBytes = value;
                OnPropertyChanged();
            }
        }

        public int? StatusCode
        {
            get => _statusCode;
            set
            {
                if (_statusCode == value) return;
                _statusCode = value;
                OnPropertyChanged();
            }
        }

        public TransferOutcome Outcome
        {
            get => _outcome;
            set
            {
                if (_outcome == value) return;
                _outcome = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OutcomeLabel));
            }
        }

        public string OutcomeLabel => Outcome switch
        {
            TransferOutcome.Success => "OK",
            TransferOutcome.Cancelled => "CANCELLED",
            TransferOutcome.Timeout => "TIMEOUT",
            _ => "FAIL"
        };
    }
}
