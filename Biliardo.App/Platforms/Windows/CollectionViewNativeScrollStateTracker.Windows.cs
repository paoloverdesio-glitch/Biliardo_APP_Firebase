using Microsoft.Maui.Controls;
using Biliardo.App.Infrastructure;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class CollectionViewNativeScrollStateTracker
    {
        static partial void AttachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
            coordinator.SetNativeTrackingActive(false);
        }

        static partial void DetachPlatform(CollectionView view, ScrollWorkCoordinator coordinator)
        {
        }
    }
}
