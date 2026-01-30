using System;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Utilita
{
    public sealed class DebounceAsync : IDisposable
    {
        private readonly object _lock = new();
        private CancellationTokenSource? _cts;

        public Task RunAsync(Func<CancellationToken, Task> action, TimeSpan delay, CancellationToken ct = default)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            CancellationTokenSource localCts;
            lock (_lock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                localCts = _cts;
            }

            return Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, localCts.Token).ConfigureAwait(false);
                    if (localCts.IsCancellationRequested)
                        return;

                    await action(localCts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // debounce canceled
                }
            }, localCts.Token);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
