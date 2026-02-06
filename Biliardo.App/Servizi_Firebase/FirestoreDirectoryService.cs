using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Diagnostics;
using Plugin.Firebase.Firestore;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Directory utenti (public) su Firestore SDK.
    /// Collezione: users_public/{uid}
    /// Campi attesi (supporto multi-nome):
    /// - nickname, nicknameLower
    /// - firstName/lastName oppure nome/cognome
    /// - photoUrl/avatarUrl (opzionale)
    /// </summary>
    public static class FirestoreDirectoryService
    {
        public sealed class UserPublicItem
        {
            public string Uid { get; set; } = "";
            public string Nickname { get; set; } = "";
            public string NicknameLower { get; set; } = "";

            public string FirstName { get; set; } = "";
            public string LastName { get; set; } = "";
            public string PhotoUrl { get; set; } = "";
            public string PhotoLocalPath { get; set; } = "";
            public string AvatarUrl { get; set; } = "";
            public string AvatarPath { get; set; } = "";

            public string FullNameOrPlaceholder
            {
                get
                {
                    var fn = (FirstName ?? "").Trim();
                    var ln = (LastName ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(fn) || !string.IsNullOrWhiteSpace(ln))
                        return $"{fn} {ln}".Trim();
                    return "";
                }
            }
        }

        public sealed class SearchUsersRes
        {
            public List<UserPublicItem> Items { get; set; } = new();
            public string? NextCursor { get; set; }
        }

        /// <summary>
        /// Ricerca per prefisso su nicknameLower con query range (Firestore SDK).
        /// Paginazione: cursor = nicknameLower dell’ultimo elemento restituito.
        /// </summary>
        public static async Task<SearchUsersRes> SearchUsersPublicAsync(
            string prefix,
            int take = 50,
            string? after = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("prefix richiesto.", nameof(prefix));

            if (take < 1 || take > 200) take = 50;

            var prefixLower = prefix.Trim().ToLowerInvariant();
            var high = prefixLower + "\uf8ff";
            var afterLower = string.IsNullOrWhiteSpace(after) ? null : after.Trim().ToLowerInvariant();

            DiagLog.Note("Directory.Search.Prefix", prefixLower);
            DiagLog.Note("Directory.Search.Take", take.ToString());
            DiagLog.Note("Directory.Search.After", afterLower ?? "");

            var query = CrossFirebaseFirestore.Current
                .GetCollection("users_public")
                .OrderBy("nicknameLower", false)
                .WhereGreaterThan("nicknameLower", prefixLower)
                .WhereLessThan("nicknameLower", high)
                .LimitedTo(take + 1);

            if (!string.IsNullOrWhiteSpace(afterLower))
                query = query.WhereGreaterThan("nicknameLower", afterLower);

            var rangeItems = await FetchUsersAsync(query, ct).ConfigureAwait(false);

            var exactQuery = CrossFirebaseFirestore.Current
                .GetCollection("users_public")
                .WhereEqualsTo("nicknameLower", prefixLower)
                .LimitedTo(1);

            var exactItems = await FetchUsersAsync(exactQuery, ct).ConfigureAwait(false);

            var merged = rangeItems
                .Concat(exactItems)
                .Where(u => !string.IsNullOrWhiteSpace(u.NicknameLower))
                .GroupBy(u => u.Uid, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(u => u.NicknameLower, StringComparer.Ordinal)
                .ToList();

            string? next = null;
            if (merged.Count > take)
            {
                var page = merged.Take(take).ToList();
                next = page.Count > 0 ? page[^1].NicknameLower : null;
                DiagLog.Note("Directory.Search.NextCursor", next ?? "");
                DiagLog.Note("Directory.Search.Count", page.Count.ToString());
                return new SearchUsersRes { Items = page, NextCursor = next };
            }

            DiagLog.Note("Directory.Search.NextCursor", "");
            DiagLog.Note("Directory.Search.Count", merged.Count.ToString());
            return new SearchUsersRes { Items = merged, NextCursor = null };
        }

        public static async Task<UserPublicItem?> GetUserPublicAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return null;

            var doc = CrossFirebaseFirestore.Current.GetDocument($"users_public/{uid.Trim()}");
            var snapshot = await doc.GetDocumentSnapshotAsync<Dictionary<string, object>>(Source.Default);
            if (snapshot.Data == null)
                return null;

            return MapUserPublic(uid, snapshot);
        }

        private static async Task<List<UserPublicItem>> FetchUsersAsync(IQuery query, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<List<UserPublicItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
            IDisposable? listener = null;

            void Cleanup()
            {
                try { listener?.Dispose(); } catch { }
            }

            try
            {
                listener = query.AddSnapshotListener<Dictionary<string, object>>(
                    snapshot =>
                    {
                        var list = new List<UserPublicItem>();
                        foreach (var doc in snapshot.Documents)
                        {
                            var item = MapUserPublic(doc.Reference.Id, doc);
                            if (item != null)
                                list.Add(item);
                        }

                        tcs.TrySetResult(list);
                        Cleanup();
                    },
                    ex =>
                    {
                        DiagLog.Exception("Directory.FetchUsers", ex);
                        tcs.TrySetException(ex);
                        Cleanup();
                    });
            }
            catch (Exception ex)
            {
                Cleanup();
                DiagLog.Exception("Directory.FetchUsers", ex);
                tcs.TrySetException(ex);
            }

            using (ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                Cleanup();
            }))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }

        private static UserPublicItem? MapUserPublic(string uid, IDocumentSnapshot<Dictionary<string, object>> snapshot)
        {
            var data = snapshot.Data;
            if (data == null)
                return null;

            var nickname = ReadString(data, "nickname") ?? "";
            var nicknameLower = ReadString(data, "nicknameLower")
                                ?? (string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant());

            var firstName = ReadString(data, "firstName") ?? ReadString(data, "nome") ?? "";
            var lastName = ReadString(data, "lastName") ?? ReadString(data, "cognome") ?? "";

            var avatarUrl = ReadString(data, "avatarUrl") ?? ReadString(data, "photoUrl") ?? "";
            var avatarPath = ReadString(data, "avatarPath") ?? "";

            return new UserPublicItem
            {
                Uid = uid.Trim(),
                Nickname = nickname,
                NicknameLower = nicknameLower,
                FirstName = firstName,
                LastName = lastName,
                PhotoUrl = avatarUrl,
                AvatarUrl = avatarUrl,
                AvatarPath = avatarPath
            };
        }

        private static string? ReadString(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? value?.ToString() : null;
    }
}
