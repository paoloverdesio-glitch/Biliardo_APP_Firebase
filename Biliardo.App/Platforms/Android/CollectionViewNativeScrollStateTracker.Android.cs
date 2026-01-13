using System;
using System.Runtime.CompilerServices;
using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;
using Biliardo.App.Infrastructure;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class CollectionViewNativeScrollStateTracker
    {
        private static readonly ConditionalWeakTable<RecyclerView, RecyclerScrollListener> Listeners = new();

        static partial void AttachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            coordinator.SetNativeTrackingActive(true);

            if (!Listeners.TryGetValue(recycler, out _))
            {
                var listener = new RecyclerScrollListener(coordinator);
                Listeners.Add(recycler, listener);
                recycler.AddOnScrollListener(listener);
            }

            var animator = recycler.GetItemAnimator();
            if (animator is SimpleItemAnimator simpleAnimator)
                simpleAnimator.SupportsChangeAnimations = false;
            else
                recycler.SetItemAnimator(null);

            recycler.SetItemViewCacheSize(20);
            recycler.HasFixedSize = true;
        }

        static partial void DetachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            if (Listeners.TryGetValue(recycler, out var listener))
            {
                recycler.RemoveOnScrollListener(listener);
                Listeners.Remove(recycler);
            }

            coordinator.SetNativeTrackingActive(false);
        }

        private sealed class RecyclerScrollListener : RecyclerView.OnScrollListener
        {
            private readonly ScrollWorkCoordinator _coordinator;

            public RecyclerScrollListener(ScrollWorkCoordinator coordinator)
            {
                _coordinator = coordinator;
            }

            public override void OnScrollStateChanged(RecyclerView recyclerView, int newState)
            {
                base.OnScrollStateChanged(recyclerView, newState);
                var state = newState switch
                {
                    RecyclerView.ScrollStateDragging => "DRAGGING",
                    RecyclerView.ScrollStateSettling => "SETTLING",
                    _ => "IDLE"
                };

                if (state == "IDLE")
                    _coordinator.EnterIdle();
                else
                    _coordinator.EnterScrolling(state);
            }
        }
    }
}
