using AndroidX.RecyclerView.Widget;
using Biliardo.App.Infrastructure;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class CollectionViewNativeScrollStateTracker
    {
        private RecyclerView? _androidRecyclerView;
        private ScrollListener? _androidListener;

        partial void AttachNative()
        {
            if (_collectionView.Handler?.PlatformView is not RecyclerView recyclerView)
                return;

            _androidRecyclerView = recyclerView;
            _androidListener = new ScrollListener(_coordinator);
            recyclerView.AddOnScrollListener(_androidListener);
        }

        partial void DetachNative()
        {
            if (_androidRecyclerView != null && _androidListener != null)
                _androidRecyclerView.RemoveOnScrollListener(_androidListener);

            _androidListener = null;
            _androidRecyclerView = null;
        }

        private sealed class ScrollListener : RecyclerView.OnScrollListener
        {
            private readonly ScrollWorkCoordinator _coordinator;

            public ScrollListener(ScrollWorkCoordinator coordinator)
            {
                _coordinator = coordinator;
            }

            public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
            {
                var isScrolling = newState != RecyclerView.ScrollStateIdle;
                _coordinator.SetNativeScrolling(isScrolling);

                if (isScrolling)
                    _coordinator.NotifyActivity();
            }

            public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
            {
                if (dx != 0 || dy != 0)
                    _coordinator.NotifyActivity();
            }
        }
    }
}
