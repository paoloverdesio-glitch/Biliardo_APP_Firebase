using AView = Android.Views.View;
using AViewGroup = Android.Views.ViewGroup;
using AndroidX.RecyclerView.Widget;

namespace Biliardo.App.Componenti_UI
{
    public sealed partial class CollectionViewScrollStateProvider
    {
        private RecyclerView? _recycler;
        private RecyclerView.OnScrollListener? _scrollListener;

        partial void AttachPlatform(object? platformView)
        {
            if (_disposed)
                return;

            var recycler = platformView as RecyclerView ?? FindRecyclerView(platformView as AView);
            if (recycler == null)
                return;

            _recycler = recycler;
            _scrollListener = new RecyclerScrollListener(this);
            _recycler.AddOnScrollListener(_scrollListener);
        }

        partial void DetachPlatform()
        {
            if (_recycler != null && _scrollListener != null)
            {
                try { _recycler.RemoveOnScrollListener(_scrollListener); } catch { }
            }

            _scrollListener = null;
            _recycler = null;
        }

        private static RecyclerView? FindRecyclerView(AView? root)
        {
            if (root == null)
                return null;

            if (root is RecyclerView recycler)
                return recycler;

            if (root is AViewGroup group)
            {
                for (int i = 0; i < group.ChildCount; i++)
                {
                    var child = group.GetChildAt(i);
                    var found = FindRecyclerView(child);
                    if (found != null)
                        return found;
                }
            }

            return null;
        }

        private sealed class RecyclerScrollListener : RecyclerView.OnScrollListener
        {
            private readonly CollectionViewScrollStateProvider _owner;

            public RecyclerScrollListener(CollectionViewScrollStateProvider owner)
            {
                _owner = owner;
            }

            public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
            {
                var stateName = newState switch
                {
                    RecyclerView.ScrollStateDragging => "DRAGGING",
                    RecyclerView.ScrollStateSettling => "SETTLING",
                    _ => "IDLE"
                };

                var isScrolling = newState == RecyclerView.ScrollStateDragging
                                  || newState == RecyclerView.ScrollStateSettling;

                _owner.UpdateScrollState(stateName, isScrolling);
            }
        }
    }
}
