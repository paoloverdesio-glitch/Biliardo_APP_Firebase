using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Infrastructure
{
    public readonly record struct ChatScrollAnchor(string? MessageId, int FallbackIndex, int Offset);

    public static partial class ChatScrollAnchorHelper
    {
        public static ChatScrollAnchor Capture(CollectionView view, int fallbackIndex, string? messageId)
        {
            var anchor = CapturePlatform(view, fallbackIndex, messageId);
            return anchor ?? new ChatScrollAnchor(messageId, fallbackIndex, 0);
        }

        public static void Restore(CollectionView view, ChatScrollAnchor anchor, Func<string?, int> findIndex)
        {
            var index = anchor.FallbackIndex;
            if (!string.IsNullOrWhiteSpace(anchor.MessageId))
            {
                var resolved = findIndex(anchor.MessageId);
                if (resolved >= 0)
                    index = resolved;
            }

            RestorePlatform(view, new ChatScrollAnchor(anchor.MessageId, index, anchor.Offset));
        }

        private static partial ChatScrollAnchor? CapturePlatform(CollectionView view, int fallbackIndex, string? messageId);
        private static partial void RestorePlatform(CollectionView view, ChatScrollAnchor anchor);
    }
}
