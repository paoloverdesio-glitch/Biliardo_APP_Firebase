using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public sealed partial class ScrollStateGate
    {
        private RecyclerView? _androidRecycler;
        private ScrollListener? _scrollListener;

        partial void AttachPlatform(CollectionView view)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            if (_androidRecycler == recycler && _scrollListener != null)
                return;

            DetachPlatform();

            _androidRecycler = recycler;
            _scrollListener = new ScrollListener(this);
            recycler.AddOnScrollListener(_scrollListener);
        }

        partial void DetachPlatform()
        {
            if (_androidRecycler != null && _scrollListener != null)
                _androidRecycler.RemoveOnScrollListener(_scrollListener);

            _scrollListener = null;
            _androidRecycler = null;
        }

        private sealed class ScrollListener : RecyclerView.OnScrollListener
        {
            private readonly WeakReference<ScrollStateGate> _gate;

            public ScrollListener(ScrollStateGate gate)
            {
                _gate = new WeakReference<ScrollStateGate>(gate);
            }

            public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
            {
                if (!_gate.TryGetTarget(out var gate))
                    return;

                var state = newState switch
                {
                    RecyclerView.ScrollStateDragging => ScrollState.Dragging,
                    RecyclerView.ScrollStateSettling => ScrollState.Settling,
                    _ => ScrollState.Idle
                };

                gate.UpdateState(state);
            }
        }
    }
}
