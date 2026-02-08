using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Biliardo.App.Servizi_Diagnostics;

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

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

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
                        ["op"] = "LESS_THAN_OR_EQUAL",
                        ["value"] = new Dictionary<string, object> { ["stringValue"] = high }
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(afterLower))
            {
                filters.Add(new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "nicknameLower" },
                        ["op"] = "GREATER_THAN",
                        ["value"] = new Dictionary<string, object> { ["stringValue"] = afterLower }
                    }
                });
            }

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "users_public" }
                },
                ["orderBy"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "nicknameLower" },
                        ["direction"] = "ASCENDING"
                    }
                },
                ["where"] = new Dictionary<string, object>
                {
                    ["compositeFilter"] = new Dictionary<string, object>
                    {
                        ["op"] = "AND",
                        ["filters"] = filters.ToArray()
                    }
                },
                ["limit"] = take + 1
            };

            var queryDoc = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, ct);
            var merged = ParseUsersFromRunQuery(queryDoc)
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

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var doc = await FirestoreRestClient.GetDocumentAsync($"users_public/{uid.Trim()}", idToken, ct);
            if (!doc.RootElement.TryGetProperty("fields", out var fields))
                return null;

            return MapUserPublicFromFields(uid, fields);
        }

        private static List<UserPublicItem> ParseUsersFromRunQuery(JsonDocument doc)
        {
            var list = new List<UserPublicItem>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("document", out var docElement))
                    continue;
                if (!docElement.TryGetProperty("name", out var nameProp))
                    continue;
                if (!docElement.TryGetProperty("fields", out var fields))
                    continue;

                var name = nameProp.GetString();
                var uid = ExtractDocId(name);
                if (string.IsNullOrWhiteSpace(uid))
                    continue;

                var item = MapUserPublicFromFields(uid, fields);
                if (item != null)
                    list.Add(item);
            }

            return list;
        }

        private static UserPublicItem? MapUserPublicFromFields(string uid, JsonElement fields)
        {
            var nickname = ReadString(fields, "nickname") ?? "";
            var nicknameLower = ReadString(fields, "nicknameLower")
                                ?? (string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant());

            var firstName = ReadString(fields, "firstName") ?? ReadString(fields, "nome") ?? "";
            var lastName = ReadString(fields, "lastName") ?? ReadString(fields, "cognome") ?? "";

            var avatarUrl = ReadString(fields, "avatarUrl") ?? ReadString(fields, "photoUrl") ?? "";
            var avatarPath = ReadString(fields, "avatarPath") ?? "";

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

        private static string? ReadString(JsonElement fields, string key)
        {
            if (!fields.TryGetProperty(key, out var field))
                return null;
            if (field.TryGetProperty("stringValue", out var stringValue))
                return stringValue.GetString();
            return null;
        }

        private static string? ExtractDocId(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : null;
        }
    }
}
