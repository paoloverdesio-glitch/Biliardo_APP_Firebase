// File: Biliardo.App/Cache_Locale/Profili/UserPublicLocalCache.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Cache_Locale.SQLite;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Cache_Locale.Profili
{
    public sealed class UserPublicLocalCache
    {
        private readonly ProfileCacheStore _store = new();

        public async Task<FirestoreDirectoryService.UserPublicItem?> TryGetAsync(string uid, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return null;

            var row = await _store.GetProfileAsync(uid, ct);
            if (row == null)
                return null;

            var nickname = row.Nickname ?? "";
            return new FirestoreDirectoryService.UserPublicItem
            {
                Uid = row.Uid,
                Nickname = nickname,
                NicknameLower = string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant(),
                FirstName = row.FirstName ?? "",
                LastName = row.LastName ?? "",
                PhotoUrl = row.PhotoUrl ?? "",
                PhotoLocalPath = row.PhotoLocalPath ?? "",
                AvatarUrl = row.PhotoUrl ?? "",
                AvatarPath = ""
            };
        }

        public Task UpsertAsync(string uid, FirestoreDirectoryService.UserPublicItem profile, CancellationToken ct)
        {
            if (profile == null || string.IsNullOrWhiteSpace(uid))
                return Task.CompletedTask;

            var row = new ProfileCacheStore.ProfileRow(
                uid,
                profile.Nickname,
                profile.FirstName,
                profile.LastName,
                profile.PhotoUrl,
                profile.PhotoLocalPath,
                DateTimeOffset.UtcNow);

            return _store.UpsertProfileAsync(row, ct);
        }
    }
}
