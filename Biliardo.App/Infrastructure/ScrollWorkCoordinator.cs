using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;

namespace Biliardo.App.Infrastructure
{
    public sealed class ScrollWorkCoordinator
    {
        private readonly TimeSpan _idleDelay;
        private readonly ConcurrentQueue<Func<Task>> _pendingUiWork = new();
        private readonly object _gate = new();
        private CancellationTokenSource? _idleCts;
        private long _lastActivityUtcTicks;
        private int _nativeScrolling;
        private int _pumpScheduled;

        public ScrollWorkCoordinator(TimeSpan idleDelay)
        {
            _idleDelay = idleDelay;
            _lastActivityUtcTicks = DateTime.UtcNow.Ticks;
        }

        public bool IsScrolling
        {
            get
            {
                if (Volatile.Read(ref _nativeScrolling) == 1)
                    return true;

                var last = Volatile.Read(ref _lastActivityUtcTicks);
                return DateTime.UtcNow.Ticks - last < _idleDelay.Ticks;
            }
        }

        public void NotifyActivity()
        {
            Interlocked.Exchange(ref _lastActivityUtcTicks, DateTime.UtcNow.Ticks);
            ScheduleIdleCheck();
        }

        public void SetNativeScrolling(bool isScrolling)
        {
            Interlocked.Exchange(ref _nativeScrolling, isScrolling ? 1 : 0);
            if (isScrolling)
                NotifyActivity();
            else
                ScheduleIdleCheck();
        }

        public void EnqueueUiWork(Func<Task> work, string? reason = null)
        {
            _pendingUiWork.Enqueue(work);
            SchedulePump();
        }

        private void ScheduleIdleCheck()
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                _idleCts?.Cancel();
                _idleCts?.Dispose();
                _idleCts = new CancellationTokenSource();
                cts = _idleCts;
            }

            var token = cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_idleDelay, token);
                    if (token.IsCancellationRequested)
                        return;

                    if (IsScrolling)
                    {
                        ScheduleIdleCheck();
                        return;
                    }

                    SchedulePump();
                }
                catch
                {
                    // best-effort
                }
            });
        }

        private void SchedulePump()
        {
            if (Interlocked.Exchange(ref _pumpScheduled, 1) == 1)
                return;

            _ = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                while (true)
                {
                    if (IsScrolling)
                    {
                        await Task.Delay(_idleDelay);
                        continue;
                    }

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        while (_pendingUiWork.TryDequeue(out var work))
                        {
                            await work();
                        }
                    });

                    if (_pendingUiWork.IsEmpty)
                        break;
                }
            }
            catch
            {
                // best-effort
            }
            finally
            {
                Interlocked.Exchange(ref _pumpScheduled, 0);
                if (!_pendingUiWork.IsEmpty)
                    SchedulePump();
            }
        }
    }
}
