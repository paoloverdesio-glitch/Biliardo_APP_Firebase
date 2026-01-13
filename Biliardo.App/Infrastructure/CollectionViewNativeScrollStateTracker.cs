using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class CollectionViewNativeScrollStateTracker : IDisposable
    {
        private readonly CollectionView _view;
        private readonly ScrollWorkCoordinator _coordinator;
        private readonly TimeSpan _idleDelay;
        private bool _attached;

        private CollectionViewNativeScrollStateTracker(CollectionView view, ScrollWorkCoordinator coordinator, TimeSpan idleDelay)
        {
            _view = view;
            _coordinator = coordinator;
            _idleDelay = idleDelay;
        }

        public static CollectionViewNativeScrollStateTracker Attach(CollectionView view, ScrollWorkCoordinator coordinator, TimeSpan idleDelay)
        {
            var tracker = new CollectionViewNativeScrollStateTracker(view, coordinator, idleDelay);
            tracker.AttachCore();
            return tracker;
        }

        private void AttachCore()
        {
            if (_attached)
                return;

            _attached = true;
            _coordinator.ConfigureIdleTimer(_idleDelay);
            AttachPlatform(_view, _coordinator);
        }

        public void Dispose()
        {
            if (!_attached)
                return;

            DetachPlatform(_view, _coordinator);
            _attached = false;
        }

        static partial void AttachPlatform(CollectionView view, ScrollWorkCoordinator coordinator);
        static partial void DetachPlatform(CollectionView view, ScrollWorkCoordinator coordinator);
    }
}
