using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Infrastructure;
using Biliardo.App.Infrastructure.Home;

namespace Biliardo.App.Cache_Locale.Home
{
    public sealed class HomeFeedLocalCache
    {
        public sealed record CachedHomePost(
            string PostId,
            string AuthorUid,
            string AuthorNickname,
            string? AuthorFirstName,
            string? AuthorLastName,
            string? AuthorAvatarPath,
            string? AuthorAvatarUrl,
            string Text,
            string? ThumbKey,
            DateTimeOffset CreatedAtUtc,
            IReadOnlyList<HomeAttachmentContractV2> Attachments,
            int LikeCount,
            int CommentCount,
            int ShareCount,
            bool Deleted,
            DateTimeOffset? DeletedAtUtc,
            string? RepostOfPostId,
            string? ClientNonce,
            int SchemaVersion,
            bool Ready);

        private readonly HomeFeedCacheStore _store = new();

        public async Task<IReadOnlyList<CachedHomePost>> LoadAsync(CancellationToken ct = default)
        {
            var rows = await _store.ListPostsAsync(limit: 30, ct);
            var list = new List<CachedHomePost>(rows.Count);
            foreach (var row in rows)
                list.Add(MapRow(row));
            return list;
        }

        public async Task<IReadOnlyList<CachedHomePost>> LoadBeforeAsync(DateTimeOffset beforeUtc, int limit, CancellationToken ct = default)
        {
            var rows = await _store.ListPostsBeforeAsync(beforeUtc, limit, ct);
            var list = new List<CachedHomePost>(rows.Count);
            foreach (var row in rows)
                list.Add(MapRow(row));
            return list;
        }

        public async Task SaveAsync(IReadOnlyList<CachedHomePost> posts, CancellationToken ct = default)
        {
            if (posts == null || posts.Count == 0)
                return;

            foreach (var post in posts)
            {
                if (post == null) continue;
                if (!IsCacheSafe(post))
                    continue;

                await _store.UpsertPostAsync(MapPost(post), ct);
            }

            await _store.TrimOldestAsync(AppCacheOptions.MaxHomePosts, ct);
        }

        public Task UpsertTop(CachedHomePost post, CancellationToken ct = default)
        {
            if (post == null)
                return Task.CompletedTask;

            return UpsertAndTrimAsync(post, ct);
        }

        public Task MergeNewTop(IEnumerable<CachedHomePost> newOnes, CancellationToken ct = default)
        {
            if (newOnes == null)
                return Task.CompletedTask;

            var tasks = new List<Task>();
            foreach (var post in newOnes)
            {
                if (post == null) continue;
                if (!IsCacheSafe(post))
                    continue;

                tasks.Add(_store.UpsertPostAsync(MapPost(post), ct));
            }

            return TrimAfterBatchAsync(tasks, ct);
        }

        private async Task UpsertAndTrimAsync(CachedHomePost post, CancellationToken ct)
        {
            if (!IsCacheSafe(post))
                return;

            await _store.UpsertPostAsync(MapPost(post), ct);
            await _store.TrimOldestAsync(AppCacheOptions.MaxHomePosts, ct);
        }

        private async Task TrimAfterBatchAsync(IEnumerable<Task> tasks, CancellationToken ct)
        {
            await Task.WhenAll(tasks);
            await _store.TrimOldestAsync(AppCacheOptions.MaxHomePosts, ct);
        }

        private static CachedHomePost MapRow(HomeFeedCacheStore.HomePostRow row)
        {
            return new CachedHomePost(
                PostId: row.PostId,
                AuthorUid: row.AuthorUid ?? "",
                AuthorNickname: row.AuthorNickname ?? row.AuthorName ?? "",
                AuthorFirstName: row.AuthorFirstName,
                AuthorLastName: row.AuthorLastName,
                AuthorAvatarPath: row.AuthorAvatarPath,
                AuthorAvatarUrl: row.AuthorAvatarUrl,
                Text: row.Text ?? "",
                ThumbKey: row.ThumbKey,
                CreatedAtUtc: row.CreatedAtUtc,
                Attachments: DeserializeAttachments(row.AttachmentsJson),
                LikeCount: row.LikeCount,
                CommentCount: row.CommentCount,
                ShareCount: row.ShareCount,
                Deleted: row.Deleted,
                DeletedAtUtc: row.DeletedAtUtc,
                RepostOfPostId: row.RepostOfPostId,
                ClientNonce: row.ClientNonce,
                SchemaVersion: row.SchemaVersion,
                Ready: row.Ready);
        }

        private static HomeFeedCacheStore.HomePostRow MapPost(CachedHomePost post)
        {
            var authorFullName = $"{post.AuthorFirstName} {post.AuthorLastName}".Trim();
            return new HomeFeedCacheStore.HomePostRow(
                PostId: post.PostId,
                AuthorName: string.IsNullOrWhiteSpace(post.AuthorNickname) ? null : post.AuthorNickname,
                AuthorFullName: string.IsNullOrWhiteSpace(authorFullName) ? null : authorFullName,
                AuthorUid: post.AuthorUid,
                AuthorNickname: post.AuthorNickname,
                AuthorFirstName: post.AuthorFirstName,
                AuthorLastName: post.AuthorLastName,
                AuthorAvatarPath: post.AuthorAvatarPath,
                AuthorAvatarUrl: post.AuthorAvatarUrl,
                Text: post.Text,
                ThumbKey: post.ThumbKey,
                CreatedAtUtc: post.CreatedAtUtc,
                SchemaVersion: post.SchemaVersion,
                Ready: post.Ready,
                Deleted: post.Deleted,
                DeletedAtUtc: post.DeletedAtUtc,
                LikeCount: post.LikeCount,
                CommentCount: post.CommentCount,
                ShareCount: post.ShareCount,
                RepostOfPostId: post.RepostOfPostId,
                ClientNonce: post.ClientNonce,
                AttachmentsJson: SerializeAttachments(post.Attachments));
        }

        private static bool IsCacheSafe(CachedHomePost post)
        {
            var contract = new HomePostContractV2(
                PostId: post.PostId,
                CreatedAtUtc: post.CreatedAtUtc,
                AuthorUid: post.AuthorUid,
                AuthorNickname: post.AuthorNickname,
                AuthorFirstName: post.AuthorFirstName,
                AuthorLastName: post.AuthorLastName,
                AuthorAvatarPath: post.AuthorAvatarPath,
                AuthorAvatarUrl: post.AuthorAvatarUrl,
                Text: post.Text,
                Attachments: post.Attachments ?? Array.Empty<HomeAttachmentContractV2>(),
                Deleted: post.Deleted,
                DeletedAtUtc: post.DeletedAtUtc,
                RepostOfPostId: post.RepostOfPostId,
                ClientNonce: post.ClientNonce,
                LikeCount: post.LikeCount,
                CommentCount: post.CommentCount,
                ShareCount: post.ShareCount,
                SchemaVersion: post.SchemaVersion,
                Ready: post.Ready);

            if (!HomePostValidatorV2.IsCacheSafe(contract, out var reason))
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[HomeFeedCache] skip postId={post.PostId} reason={reason}");
#endif
                return false;
            }

            return true;
        }

        private static IReadOnlyList<HomeAttachmentContractV2> DeserializeAttachments(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<HomeAttachmentContractV2>();

            try
            {
                return JsonSerializer.Deserialize<List<HomeAttachmentContractV2>>(json, SerializerOptions) ?? Array.Empty<HomeAttachmentContractV2>();
            }
            catch
            {
                return Array.Empty<HomeAttachmentContractV2>();
            }
        }

        private static string SerializeAttachments(IReadOnlyList<HomeAttachmentContractV2>? attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return "[]";

            return JsonSerializer.Serialize(attachments, SerializerOptions);
        }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }
}
