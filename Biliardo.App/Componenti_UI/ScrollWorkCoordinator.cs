using System;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Componenti_UI
{
    public sealed class ScrollWorkCoordinator : IDisposable
    {
        private readonly IScrollStateProvider _stateProvider;
        private readonly Func<Task> _work;
        private readonly object _gate = new();
        private CancellationTokenSource? _debounceCts;
        private TimeSpan _debounce;
        private bool _pending;
        private bool _disposed;

        public ScrollWorkCoordinator(IScrollStateProvider stateProvider, Func<Task> work)
        {
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _work = work ?? throw new ArgumentNullException(nameof(work));
            _stateProvider.ScrollingStateChanged += OnScrollingStateChanged;
        }

        public void SetDebounce(TimeSpan debounce)
        {
            lock (_gate)
            {
                _debounce = debounce < TimeSpan.Zero ? TimeSpan.Zero : debounce;
            }
        }

        public void RequestWork()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _pending = true;

                if (_stateProvider.IsScrolling)
                    return;

                ScheduleDebouncedWorkLocked();
            }
        }

        private void OnScrollingStateChanged(object? sender, bool isScrolling)
        {
            if (isScrolling)
            {
                CancelDebounce();
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                    return;

                if (_pending)
                    ScheduleDebouncedWorkLocked();
            }
        }

        private void ScheduleDebouncedWorkLocked()
        {
            if (_debounceCts != null)
                return;

            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = RunAsync(token);
        }

        private async Task RunAsync(CancellationToken token)
        {
            try
            {
                var delay = _debounce;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);

                if (token.IsCancellationRequested)
                    return;

                lock (_gate)
                {
                    if (_disposed || !_pending || _stateProvider.IsScrolling)
                        return;

                    _pending = false;
                }

                await _work();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    if (_debounceCts != null)
                    {
                        _debounceCts.Dispose();
                        _debounceCts = null;
                    }

                    if (!_disposed && _pending && !_stateProvider.IsScrolling)
                        ScheduleDebouncedWorkLocked();
                }
            }
        }

        private void CancelDebounce()
        {
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                if (_debounceCts == null)
                    return;

                cts = _debounceCts;
                _debounceCts = null;
            }

            try { cts.Cancel(); } catch { }
            try { cts.Dispose(); } catch { }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }

            _stateProvider.ScrollingStateChanged -= OnScrollingStateChanged;
            CancelDebounce();
        }
    }
}
