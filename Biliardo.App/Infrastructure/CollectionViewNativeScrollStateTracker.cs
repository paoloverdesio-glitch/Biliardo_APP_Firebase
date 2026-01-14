using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class CollectionViewNativeScrollStateTracker : IDisposable
    {
        private readonly CollectionView _collectionView;
        private readonly ScrollWorkCoordinator _coordinator;

        private CollectionViewNativeScrollStateTracker(CollectionView collectionView, ScrollWorkCoordinator coordinator)
        {
            _collectionView = collectionView ?? throw new ArgumentNullException(nameof(collectionView));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public static CollectionViewNativeScrollStateTracker Attach(CollectionView collectionView, ScrollWorkCoordinator coordinator)
        {
            var tracker = new CollectionViewNativeScrollStateTracker(collectionView, coordinator);
            tracker.Attach();
            return tracker;
        }

        private void Attach()
        {
            _collectionView.HandlerChanged += OnHandlerChanged;
            AttachNative();
        }

        public void Dispose()
        {
            _collectionView.HandlerChanged -= OnHandlerChanged;
            DetachNative();
        }

        private void OnHandlerChanged(object? sender, EventArgs e)
        {
            DetachNative();
            AttachNative();
        }

        partial void AttachNative();
        partial void DetachNative();
    }
}
