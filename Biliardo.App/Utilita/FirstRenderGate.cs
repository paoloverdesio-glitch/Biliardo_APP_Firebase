using System;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Utilita
{
    public sealed class FirstRenderGate : IDisposable
    {
        private readonly ContentPage _page;
        private readonly VisualElement? _sizeGateElement;
        private readonly TaskCompletionSource<bool> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public FirstRenderGate(ContentPage page, VisualElement? sizeGateElement = null)
        {
            _page = page ?? throw new ArgumentNullException(nameof(page));
            _sizeGateElement = sizeGateElement;

            _page.Loaded += OnLoaded;
            if (_sizeGateElement != null)
                _sizeGateElement.SizeChanged += OnSizeChanged;
        }

        public Task WaitAsync() => _tcs.Task;

        private async void OnLoaded(object? sender, EventArgs e)
        {
            try
            {
                await Task.Yield();
                TryComplete();
            }
            catch
            {
                TryComplete();
            }
        }

        private void OnSizeChanged(object? sender, EventArgs e)
        {
            TryComplete();
        }

        private void TryComplete()
        {
            if (_completed)
                return;

            if (_sizeGateElement != null)
            {
                if (_sizeGateElement.Width <= 0 || _sizeGateElement.Height <= 0)
                    return;
            }

            _completed = true;
            _page.Loaded -= OnLoaded;

            if (_sizeGateElement != null)
                _sizeGateElement.SizeChanged -= OnSizeChanged;

            _tcs.TrySetResult(true);
        }

        public void Dispose()
        {
            _page.Loaded -= OnLoaded;
            if (_sizeGateElement != null)
                _sizeGateElement.SizeChanged -= OnSizeChanged;
        }
    }
}
