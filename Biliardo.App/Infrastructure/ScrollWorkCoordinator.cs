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
            PerfLog.Note("SCROLL_STATE", state);
        }

        public void EnterIdle()
        {
            using var _trace = PerfettoTrace.Section("CHAT_SCROLLWORK_ENTER_IDLE");

            if (!IsScrolling && string.Equals(LastScrollState, "Idle", StringComparison.Ordinal))
                return;

            IsScrolling = false;
            LastScrollState = "Idle";
            Debug.WriteLine("[ScrollWorkCoordinator] EnterIdle -> flush");
            PerfLog.Note("SCROLL_STATE", "Idle");
            _ = FlushIfIdleAsync();
        }

        public void EnqueueUiWork(Func<Task> work, string label)
        {
            using var _trace = PerfettoTrace.Section($"CHAT_SCROLLWORK_ENQUEUE:{label}");

            if (work == null)
                return;

            PerfLog.Note("SCROLLWORK_ENQUEUE", label);

            lock (_gate)
            {
                if (!IsScrolling && !_isFlushing && _pending.Count == 0)
                {
                    using var _traceImmediate = PerfettoTrace.Section($"CHAT_SCROLLWORK_ENQUEUE_IMMEDIATE:{label}");
                    _isFlushing = true;
                    _ = ExecuteOnUiAsync(work, label);
                    return;
                }

                using var _traceQueued = PerfettoTrace.Section($"CHAT_SCROLLWORK_ENQUEUE_QUEUE:{label}");
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
            using var _trace = PerfettoTrace.Section("CHAT_SCROLLWORK_FLUSH_IF_IDLE");

            if (IsScrolling)
                return Task.CompletedTask;

            PendingWork? next = null;
            string nextLabel = "";
            lock (_gate)
            {
                if (_isFlushing)
                    return Task.CompletedTask;

                if (_pending.Count == 0)
                    return Task.CompletedTask;

                _isFlushing = true;
                next = _pending.Dequeue();
                nextLabel = next.Value.Label ?? "";
            }

            using var _traceNext = PerfettoTrace.Section($"CHAT_SCROLLWORK_DEQUEUE:{nextLabel}");
            return ExecuteOnUiAsync(next.Value.Work, next.Value.Label);
        }

        private async Task ExecuteOnUiAsync(Func<Task> work, string label)
        {
            _dispatcher ??= Application.Current?.Dispatcher ?? Dispatcher.GetForCurrentThread();
            if (_dispatcher == null)
            {
                try
                {
                    using var _trace = PerfettoTrace.Section($"CHAT_SCROLLWORK_FLUSH:{label}");
                    using var _span = PerfLog.Span("SCROLLWORK_FLUSH", $"label={label}");
                    await work();
                }
                finally
                {
                    MarkFlushComplete();
                }

                return;
            }

            using var _traceDispatch = PerfettoTrace.Section($"CHAT_SCROLLWORK_DISPATCH:{label}");
            await _dispatcher.DispatchAsync(async () =>
            {
                using var _trace = PerfettoTrace.Section($"CHAT_SCROLLWORK_FLUSH:{label}");
                using var _span = PerfLog.Span("SCROLLWORK_FLUSH", $"label={label}");
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

            using var _trace = PerfettoTrace.Section("CHAT_SCROLLWORK_IDLE_TICK_TRIGGER");
            EnterIdle();
        }

        private readonly record struct PendingWork(string Label, Func<Task> Work);
    }
}
