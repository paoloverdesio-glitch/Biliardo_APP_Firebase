using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public sealed partial class CollectionViewScrollStateProvider : IScrollStateProvider, IDisposable
    {
        private readonly CollectionView _view;
        private bool _isScrolling;
        private bool _disposed;

        public CollectionViewScrollStateProvider(CollectionView view)
        {
            _view = view ?? throw new ArgumentNullException(nameof(view));
            _view.HandlerChanged += OnHandlerChanged;
            AttachPlatform(_view.Handler?.PlatformView);
        }

        public bool IsScrolling => _isScrolling;

        public event EventHandler<bool>? ScrollingStateChanged;

        public event EventHandler<string>? ScrollStateChangedDetailed;

        private void OnHandlerChanged(object? sender, EventArgs e)
        {
            DetachPlatform();
            AttachPlatform(_view.Handler?.PlatformView);
        }

        private void UpdateScrollState(string stateName, bool isScrolling)
        {
            if (_isScrolling == isScrolling && string.IsNullOrWhiteSpace(stateName))
                return;

            _isScrolling = isScrolling;
            ScrollingStateChanged?.Invoke(this, isScrolling);

            if (!string.IsNullOrWhiteSpace(stateName))
                ScrollStateChangedDetailed?.Invoke(this, stateName);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _view.HandlerChanged -= OnHandlerChanged;
            DetachPlatform();
        }

        partial void AttachPlatform(object? platformView);
        partial void DetachPlatform();
    }
}
