using AndroidX.RecyclerView.Widget;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public static partial class ChatScrollAnchorHelper
    {
        private static partial ChatScrollAnchor? CapturePlatform(CollectionView view, int fallbackIndex, string? messageId)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return null;

            if (recycler.GetLayoutManager() is not LinearLayoutManager layout)
                return null;

            var index = layout.FindFirstVisibleItemPosition();
            if (index < 0)
                return null;

            var child = layout.FindViewByPosition(index);
            var offset = child?.Top ?? 0;

            return new ChatScrollAnchor(messageId, index, offset);
        }

        private static partial void RestorePlatform(CollectionView view, ChatScrollAnchor anchor)
        {
            if (view.Handler?.PlatformView is not RecyclerView recycler)
                return;

            if (recycler.GetLayoutManager() is not LinearLayoutManager layout)
                return;

            var index = anchor.FallbackIndex < 0 ? 0 : anchor.FallbackIndex;
            layout.ScrollToPositionWithOffset(index, anchor.Offset);
        }
    }
}
