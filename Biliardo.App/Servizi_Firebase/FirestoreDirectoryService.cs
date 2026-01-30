using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Firebase
{
    /// <summary>
    /// Directory utenti (public) su Firestore via REST.
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
        /// Legge un profilo users_public/{uid}.
        /// </summary>
        public static async Task<UserPublicItem?> GetUserPublicAsync(string uid, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(uid))
                return null;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

            using var doc = await FirestoreRestClient.GetDocumentAsync($"users_public/{uid.Trim()}", idToken, ct);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                return null;

            var nickname = ReadStringField(fields, "nickname") ?? "";
            var nicknameLower = ReadStringField(fields, "nicknameLower") ?? (string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant());

            var firstName =
                ReadStringField(fields, "firstName")
                ?? ReadStringField(fields, "nome")
                ?? "";

            var lastName =
                ReadStringField(fields, "lastName")
                ?? ReadStringField(fields, "cognome")
                ?? "";

            var photoUrl =
                ReadStringField(fields, "photoUrl")
                ?? ReadStringField(fields, "avatarUrl")
                ?? ReadStringField(fields, "avatarPath")
                ?? "";

            return new UserPublicItem
            {
                Uid = uid.Trim(),
                Nickname = nickname,
                NicknameLower = nicknameLower,
                FirstName = firstName,
                LastName = lastName,
                PhotoUrl = photoUrl
            };
        }

        /// <summary>
        /// Ricerca per prefisso su nicknameLower con query range.
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

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione Firebase assente/scaduta. Rifai login.");

            var prefixLower = prefix.Trim().ToLowerInvariant();
            var high = prefixLower + "\uf8ff";

            var structuredQuery = BuildPrefixQuery(prefixLower, high, takePlusOne: take + 1, afterLower: after);

            using var json = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, ct);

            var all = ParseRunQueryUsers(json);

            var afterLower = string.IsNullOrWhiteSpace(after) ? null : after.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(afterLower))
                all = all.Where(u => string.CompareOrdinal(u.NicknameLower, afterLower) > 0).ToList();

            string? next = null;
            if (all.Count > take)
            {
                var page = all.Take(take).ToList();
                next = page.Count > 0 ? page[^1].NicknameLower : null;
                return new SearchUsersRes { Items = page, NextCursor = next };
            }

            return new SearchUsersRes { Items = all, NextCursor = null };
        }

        private static object BuildPrefixQuery(string prefixLower, string high, int takePlusOne, string? afterLower)
        {
            var filters = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "nicknameLower" },
                        ["op"] = "GREATER_THAN_OR_EQUAL",
                        ["value"] = new Dictionary<string, object> { ["stringValue"] = prefixLower }
                    }
                },
                new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "nicknameLower" },
                        ["op"] = "LESS_THAN",
                        ["value"] = new Dictionary<string, object> { ["stringValue"] = high }
                    }
                }
            };

            var query = new Dictionary<string, object>
            {
                ["from"] = new object[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "users_public" }
                },
                ["where"] = new Dictionary<string, object>
                {
                    ["compositeFilter"] = new Dictionary<string, object>
                    {
                        ["op"] = "AND",
                        ["filters"] = filters.ToArray()
                    }
                },
                ["orderBy"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "nicknameLower" },
                        ["direction"] = "ASCENDING"
                    }
                },
                ["limit"] = takePlusOne
            };

            if (!string.IsNullOrWhiteSpace(afterLower))
            {
                query["startAt"] = new Dictionary<string, object>
                {
                    ["values"] = new object[]
                    {
                        new Dictionary<string, object> { ["stringValue"] = afterLower.Trim().ToLowerInvariant() }
                    },
                    ["before"] = false
                };
            }

            return query;
        }

        private static List<UserPublicItem> ParseRunQueryUsers(JsonDocument runQueryResponse)
        {
            var list = new List<UserPublicItem>();

            if (runQueryResponse.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var el in runQueryResponse.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;

                if (!el.TryGetProperty("document", out var docEl)) continue;
                if (docEl.ValueKind != JsonValueKind.Object) continue;

                var name = TryGetString(docEl, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var uid = name!.Split('/').LastOrDefault() ?? "";
                if (string.IsNullOrWhiteSpace(uid)) continue;

                if (!docEl.TryGetProperty("fields", out var fieldsEl) || fieldsEl.ValueKind != JsonValueKind.Object)
                    continue;

                var nickname = ReadStringField(fieldsEl, "nickname") ?? "";
                var nicknameLower = ReadStringField(fieldsEl, "nicknameLower")
                                    ?? (string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant());

                if (string.IsNullOrWhiteSpace(nicknameLower))
                    continue;

                var firstName =
                    ReadStringField(fieldsEl, "firstName")
                    ?? ReadStringField(fieldsEl, "nome")
                    ?? "";

                var lastName =
                    ReadStringField(fieldsEl, "lastName")
                    ?? ReadStringField(fieldsEl, "cognome")
                    ?? "";

                var photoUrl =
                    ReadStringField(fieldsEl, "photoUrl")
                    ?? ReadStringField(fieldsEl, "avatarUrl")
                    ?? ReadStringField(fieldsEl, "avatarPath")
                    ?? "";

                list.Add(new UserPublicItem
                {
                    Uid = uid,
                    Nickname = nickname,
                    NicknameLower = nicknameLower,
                    FirstName = firstName,
                    LastName = lastName,
                    PhotoUrl = photoUrl
                });
            }

            return list;
        }

        private static string? TryGetString(JsonElement obj, string prop)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(prop, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        private static string? ReadStringField(JsonElement fieldsEl, string fieldName)
        {
            if (fieldsEl.ValueKind != JsonValueKind.Object) return null;
            if (!fieldsEl.TryGetProperty(fieldName, out var f)) return null;
            if (f.ValueKind != JsonValueKind.Object) return null;

            if (f.TryGetProperty("stringValue", out var sv) && sv.ValueKind == JsonValueKind.String)
                return sv.GetString();

            return null;
        }
    }
}
