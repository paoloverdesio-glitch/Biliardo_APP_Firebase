using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.SQLite;

namespace Biliardo.App.Cache_Locale.Home
{
    public sealed class HomeFeedLocalCache
    {
        public sealed record CachedHomePost(
            string PostId,
            string? AuthorName,
            string? Text,
            string? ThumbKey,
            DateTimeOffset CreatedAtUtc);

        private readonly HomeFeedCacheStore _store = new();

        public async Task<IReadOnlyList<CachedHomePost>> LoadAsync(CancellationToken ct = default)
        {
            var rows = await _store.ListPostsAsync(limit: 30, ct);
            var list = new List<CachedHomePost>(rows.Count);
            foreach (var row in rows)
                list.Add(new CachedHomePost(row.PostId, row.AuthorName, row.Text, row.ThumbKey, row.CreatedAtUtc));
            return list;
        }

        public async Task SaveAsync(IReadOnlyList<CachedHomePost> posts, CancellationToken ct = default)
        {
            if (posts == null || posts.Count == 0)
                return;

            foreach (var post in posts)
            {
                if (post == null) continue;
                await _store.UpsertPostAsync(
                    new HomeFeedCacheStore.HomePostRow(post.PostId, post.AuthorName, post.Text, post.ThumbKey, post.CreatedAtUtc),
                    ct);
            }
        }

        public Task UpsertTop(CachedHomePost post, CancellationToken ct = default)
        {
            if (post == null)
                return Task.CompletedTask;

            return _store.UpsertPostAsync(
                new HomeFeedCacheStore.HomePostRow(post.PostId, post.AuthorName, post.Text, post.ThumbKey, post.CreatedAtUtc),
                ct);
        }

        public Task MergeNewTop(IEnumerable<CachedHomePost> newOnes, CancellationToken ct = default)
        {
            if (newOnes == null)
                return Task.CompletedTask;

            var tasks = new List<Task>();
            foreach (var post in newOnes)
            {
                if (post == null) continue;
                tasks.Add(_store.UpsertPostAsync(
                    new HomeFeedCacheStore.HomePostRow(post.PostId, post.AuthorName, post.Text, post.ThumbKey, post.CreatedAtUtc),
                    ct));
            }

            return Task.WhenAll(tasks);
        }
    }
}
