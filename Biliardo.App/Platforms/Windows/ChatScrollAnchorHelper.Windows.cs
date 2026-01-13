using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public static partial class ChatScrollAnchorHelper
    {
        static partial ChatScrollAnchor? CapturePlatform(CollectionView view, int fallbackIndex, string? messageId)
        {
            return null;
        }

        static partial void RestorePlatform(CollectionView view, ChatScrollAnchor anchor)
        {
            var index = anchor.FallbackIndex < 0 ? 0 : anchor.FallbackIndex;
            view.ScrollTo(index, position: ScrollToPosition.Start, animate: false);
        }
    }
}
