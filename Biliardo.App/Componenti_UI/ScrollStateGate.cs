using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public enum ScrollState
    {
        Idle,
        Dragging,
        Settling
    }

    public sealed partial class ScrollStateGate : IDisposable
    {
        private CollectionView? _view;
        private bool _isScrolling;
        private ScrollState _state = ScrollState.Idle;
        private TaskCompletionSource<bool>? _idleTcs;

        public bool IsScrolling => _isScrolling;
        public ScrollState State => _state;

        public event EventHandler? ScrollBecameIdle;
        public event EventHandler<ScrollStateChangedEventArgs>? ScrollStateChanged;

        public void Attach(CollectionView view)
        {
            if (view == null)
                return;

            if (_view == view)
            {
                TryAttach();
                return;
            }

            Detach();
            _view = view;
            _view.HandlerChanged += OnHandlerChanged;
            TryAttach();
        }

        public void Detach()
        {
            if (_view != null)
                _view.HandlerChanged -= OnHandlerChanged;

            DetachPlatform();
            _view = null;
        }

        public Task WaitForIdleAsync(CancellationToken ct)
        {
            if (!IsScrolling)
                return Task.CompletedTask;

            _idleTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idleTcs.Task.WaitAsync(ct);
        }

        internal void UpdateState(ScrollState state)
        {
            if (_state == state)
                return;

            _state = state;
            _isScrolling = state != ScrollState.Idle;

            Debug.WriteLine($"[ScrollStateGate] state={state} isScrolling={_isScrolling} ts={DateTime.UtcNow:O}");
            ScrollStateChanged?.Invoke(this, new ScrollStateChangedEventArgs(state, _isScrolling));

            if (!_isScrolling)
            {
                _idleTcs?.TrySetResult(true);
                _idleTcs = null;
                ScrollBecameIdle?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnHandlerChanged(object? sender, EventArgs e)
        {
            TryAttach();
        }

        private void TryAttach()
        {
            if (_view?.Handler == null)
                return;

            AttachPlatform(_view);
        }

        public void Dispose()
        {
            Detach();
        }

        partial void AttachPlatform(CollectionView view);
        partial void DetachPlatform();
    }

    public sealed class ScrollStateChangedEventArgs : EventArgs
    {
        public ScrollStateChangedEventArgs(ScrollState state, bool isScrolling)
        {
            State = state;
            IsScrolling = isScrolling;
        }

        public ScrollState State { get; }
        public bool IsScrolling { get; }
    }
}
