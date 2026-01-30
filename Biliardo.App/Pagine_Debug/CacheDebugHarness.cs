using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.Home;
using Biliardo.App.Infrastructure;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Pagine_Debug
{
    internal static class CacheDebugHarness
    {
        public static async Task RunAsync()
        {
            await TestChatCacheUpsertAsync();
            await TestHomeCacheMergeAsync();
        }

        private static async Task TestChatCacheUpsertAsync()
        {
            var chatCache = new ChatLocalCache();
            var key = chatCache.GetCacheKey("debug_harness", "peer_debug");
            var now = DateTimeOffset.UtcNow;

            var msg = new FirestoreChatService.MessageItem(
                MessageId: "m1",
                SenderId: "u1",
                Type: "text",
                Text: "test",
                CreatedAtUtc: now,
                DeliveredTo: Array.Empty<string>(),
                ReadBy: Array.Empty<string>(),
                DeletedForAll: false,
                DeletedFor: Array.Empty<string>(),
                DeletedAtUtc: null,
                StoragePath: null,
                DurationMs: 0,
                FileName: null,
                ContentType: null,
                SizeBytes: 0,
                ThumbStoragePath: null,
                LqipBase64: null,
                ThumbWidth: null,
                ThumbHeight: null,
                PreviewType: null,
                Waveform: null,
                Latitude: null,
                Longitude: null,
                ContactName: null,
                ContactPhone: null);

            await chatCache.UpsertAppendAsync(key, new[] { msg, msg }, 200, CancellationToken.None);
            var loaded = await chatCache.TryReadAsync(key, CancellationToken.None);

            Debug.Assert(loaded.Count <= 200, "Chat cache trim > 200");
            Debug.Assert(loaded.Select(x => x.MessageId).Distinct().Count() == loaded.Count, "Chat cache dedup failed");
        }

        private static async Task TestHomeCacheMergeAsync()
        {
            var homeCache = new HomeFeedLocalCache();
            var now = DateTimeOffset.UtcNow;

            var post1 = new HomeFeedLocalCache.CachedHomePost(
                "p1",
                "test",
                "t1",
                null,
                now);

            var post2 = new HomeFeedLocalCache.CachedHomePost(
                "p2",
                "test",
                "t2",
                null,
                now.AddSeconds(1));

            await homeCache.SaveAsync(new[] { post1 }, CancellationToken.None);
            await homeCache.MergeNewTop(new[] { post2 }, CancellationToken.None);

            var loaded = await homeCache.LoadAsync(CancellationToken.None);
            Debug.Assert(loaded.Any(x => x.PostId == "p2"), "Home cache merge failed");
        }
    }
}
