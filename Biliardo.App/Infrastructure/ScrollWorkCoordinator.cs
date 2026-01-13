using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public sealed class ScrollWorkCoordinator
    {
        private readonly object _gate = new();
        private readonly Queue<PendingWork> _pending = new();
        private bool _isFlushing;
        private bool _nativeTrackingActive;
        private DateTime _lastActivityUtc = DateTime.MinValue;
        private TimeSpan _idleDelay = TimeSpan.FromMilliseconds(280);
        private IDispatcher? _dispatcher;
        private IDispatcherTimer? _idleTimer;

        public bool IsScrolling { get; private set; }
        public string LastScrollState { get; private set; } = "Idle";

        public void ConfigureIdleTimer(TimeSpan idleDelay)
        {
            _idleDelay = idleDelay;
            _dispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.GetForCurrentThread();
            if (_dispatcher == null)
                return;

            if (_idleTimer != null)
                return;

            _idleTimer = _dispatcher.CreateTimer();
            _idleTimer.Interval = TimeSpan.FromMilliseconds(60);
            _idleTimer.Tick += (_, __) => OnIdleTick();
            _idleTimer.Start();
        }

        public void SetNativeTrackingActive(bool active)
        {
            _nativeTrackingActive = active;
        }

        public void NotifyActivity()
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (_nativeTrackingActive)
                return;

            if (!IsScrolling)
                EnterScrolling("Activity");
        }

        public void EnterScrolling(string state)
        {
            IsScrolling = true;
            LastScrollState = state;
            Debug.WriteLine($"[ScrollWorkCoordinator] EnterScrolling state={state}");
        }

        public void EnterIdle()
        {
            if (!IsScrolling && string.Equals(LastScrollState, "Idle", StringComparison.Ordinal))
                return;

            IsScrolling = false;
            LastScrollState = "Idle";
            Debug.WriteLine("[ScrollWorkCoordinator] EnterIdle -> flush");
            _ = FlushIfIdleAsync();
        }

        public void EnqueueUiWork(Func<Task> work, string label)
        {
            if (work == null)
                return;

            lock (_gate)
            {
                if (!IsScrolling && !_isFlushing && _pending.Count == 0)
                {
                    _isFlushing = true;
                    _ = ExecuteOnUiAsync(work, label);
                    return;
                }

                _pending.Enqueue(new PendingWork(label, work));
            }
        }

        public void ClearPending()
        {
            lock (_gate)
            {
                _pending.Clear();
            }
        }

        public Task FlushIfIdleAsync()
        {
            if (IsScrolling)
                return Task.CompletedTask;

            PendingWork? next = null;
            lock (_gate)
            {
                if (_isFlushing)
                    return Task.CompletedTask;

                if (_pending.Count == 0)
                    return Task.CompletedTask;

                _isFlushing = true;
                next = _pending.Dequeue();
            }

            return ExecuteOnUiAsync(next.Value.Work, next.Value.Label);
        }

        private async Task ExecuteOnUiAsync(Func<Task> work, string label)
        {
            _dispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.GetForCurrentThread();
            if (_dispatcher == null)
            {
                try
                {
                    await work();
                }
                finally
                {
                    MarkFlushComplete();
                }

                return;
            }

            await _dispatcher.DispatchAsync(async () =>
            {
                try
                {
                    Debug.WriteLine($"[ScrollWorkCoordinator] Running work={label}");
                    await work();
                }
                finally
                {
                    MarkFlushComplete();
                }
            });
        }

        private void MarkFlushComplete()
        {
            lock (_gate)
            {
                _isFlushing = false;
            }

            _ = FlushIfIdleAsync();
        }

        private void OnIdleTick()
        {
            if (_nativeTrackingActive)
                return;

            if (!IsScrolling)
                return;

            if (DateTime.UtcNow - _lastActivityUtc < _idleDelay)
                return;

            EnterIdle();
        }

        private readonly record struct PendingWork(string Label, Func<Task> Work);
    }
}
