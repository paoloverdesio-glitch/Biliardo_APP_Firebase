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
        private static readonly ConditionalWeakTable<CollectionView, EventHandler> HandlerChangedHandlers = new();

        static partial void AttachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            if (TryAttach(view, coordinator))
                return;

            if (HandlerChangedHandlers.TryGetValue(view, out _))
                return;

            EventHandler? handler = null;
            handler = (_, __) =>
            {
                if (!TryAttach(view, coordinator))
                    return;

                view.HandlerChanged -= handler;
                HandlerChangedHandlers.Remove(view);
            };

            HandlerChangedHandlers.Add(view, handler);
            view.HandlerChanged += handler;
        }

        static partial void DetachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            if (HandlerChangedHandlers.TryGetValue(view, out var handler))
            {
                view.HandlerChanged -= handler;
                HandlerChangedHandlers.Remove(view);
            }

            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            if (Listeners.TryGetValue(recycler, out var listener))
            {
                recycler.RemoveOnScrollListener(listener);
                Listeners.Remove(recycler);
            }

            coordinator.SetNativeTrackingActive(false);
        }

        private static bool TryAttach(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return false;

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
            return true;
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
