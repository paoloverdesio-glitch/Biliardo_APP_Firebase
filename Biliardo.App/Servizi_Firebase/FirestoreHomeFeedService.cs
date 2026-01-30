using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Servizi_Firebase
{
    public sealed class FirestoreHomeFeedService
    {
        public sealed record HomeAttachment(
            string Type,
            string? StoragePath,
            string? DownloadUrl,
            string? FileName,
            string? ContentType,
            long SizeBytes,
            long DurationMs,
            Dictionary<string, object>? Extra,
            string? ThumbStoragePath = null,
            string? LqipBase64 = null,
            string? PreviewType = null,
            int? ThumbWidth = null,
            int? ThumbHeight = null,
            IReadOnlyList<int>? Waveform = null);

        public sealed record HomePostItem(
            string PostId,
            string AuthorUid,
            string AuthorNickname,
            string AuthorFirstName,
            string AuthorLastName,
            string? AuthorAvatarPath,
            string? AuthorAvatarUrl,
            DateTimeOffset CreatedAtUtc,
            string Text,
            IReadOnlyList<HomeAttachment> Attachments,
            int LikeCount,
            int CommentCount,
            int ShareCount,
            bool Deleted,
            DateTimeOffset? DeletedAtUtc,
            string? RepostOfPostId,
            string? ClientNonce,
            bool IsLiked = false
        );

        public sealed record LikeToggleResult(bool IsLikedNow, int LikeCount);

        public sealed record HomeCommentItem(
            string CommentId,
            string AuthorUid,
            string AuthorNickname,
            string? AuthorAvatarPath,
            string? AuthorAvatarUrl,
            DateTimeOffset CreatedAtUtc,
            string Text);

        public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);

        public async Task<string> CreatePostAsync(
            string text,
            IReadOnlyList<HomeAttachment> attachments,
            string? repostOfPostId = null,
            string? clientNonce = null,
            CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var nickname = FirebaseSessionePersistente.GetDisplayName() ?? "Utente";

            string? avatarPath = null;
            string? avatarUrl = null;
            string? firstName = null;
            string? lastName = null;

            // Best-effort: se DirectoryService � protetto da rules, non deve bloccare la creazione post
            try
            {
                var profile = await FirestoreDirectoryService.GetUserPublicAsync(myUid, ct);
                if (profile != null)
                {
                    avatarPath = profile.PhotoUrl;
                    avatarUrl = profile.PhotoUrl;
                    firstName = profile.FirstName;
                    lastName = profile.LastName;
                }
            }
            catch
            {
                // ignore
            }

            var now = DateTimeOffset.UtcNow;

            var fields = new Dictionary<string, object>
            {
                ["authorUid"] = FirestoreRestClient.VString(myUid),
                ["authorNickname"] = FirestoreRestClient.VString(nickname),
                ["authorFirstName"] = string.IsNullOrWhiteSpace(firstName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(firstName),
                ["authorLastName"] = string.IsNullOrWhiteSpace(lastName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(lastName),
                ["authorAvatarPath"] = string.IsNullOrWhiteSpace(avatarPath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarPath),
                ["authorAvatarUrl"] = string.IsNullOrWhiteSpace(avatarUrl) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarUrl),
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["text"] = FirestoreRestClient.VString(text ?? ""),
                ["attachments"] = FirestoreRestClient.VArray(attachments?.Select(ToFirestoreAttachment).ToArray() ?? Array.Empty<object>()),
                ["likeCount"] = FirestoreRestClient.VInt(0),
                ["commentCount"] = FirestoreRestClient.VInt(0),
                ["shareCount"] = FirestoreRestClient.VInt(0),
                ["deleted"] = FirestoreRestClient.VBool(false),
                ["deletedAt"] = FirestoreRestClient.VNull(),
                ["repostOfPostId"] = string.IsNullOrWhiteSpace(repostOfPostId) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(repostOfPostId),
                ["clientNonce"] = string.IsNullOrWhiteSpace(clientNonce) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(clientNonce)
            };

            var res = await FirestoreRestClient.CreateDocumentAsync("home_posts", null, fields, idToken, ct);
            var name = ReadString(res.RootElement, "name") ?? "";
            return ExtractLastPathSegment(name);
        }

        /// <summary>
        /// LISTA FEED.
        /// Nota: qui popoliamo anche IsLiked leggendo (best-effort) il doc like dell'utente corrente.
        /// </summary>
        public async Task<PageResult<HomePostItem>> ListPostsAsync(int pageSize, string? cursor, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            if (pageSize <= 0) pageSize = 20;
            if (pageSize > 20) pageSize = 20;

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new object[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "home_posts" }
                },
                ["where"] = new Dictionary<string, object>
                {
                    ["fieldFilter"] = new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "deleted" },
                        ["op"] = "EQUAL",
                        ["value"] = FirestoreRestClient.VBool(false)
                    }
                },
                ["orderBy"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "createdAt" },
                        ["direction"] = "DESCENDING"
                    }
                },
                ["limit"] = pageSize
            };

            if (!string.IsNullOrWhiteSpace(cursor))
            {
                var ts = ParseCursorTimestamp(cursor);
                structuredQuery["startAt"] = new Dictionary<string, object>
                {
                    ["values"] = new object[]
                    {
        FirestoreRestClient.VTimestamp(ts)
                    },
                    ["before"] = false
                };

            }

            using var json = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, ct);
            var items = ParsePosts(json);

            string? next = null;
            if (items.Count > 0 && items.Count >= pageSize)
            {
                var last = items[^1];
                next = BuildCursor(last.CreatedAtUtc);
            }

            return new PageResult<HomePostItem>(items, next);
        }

        /// <summary>
        /// Compat: mantiene la vecchia API (senza ritorno).
        /// </summary>
        public async Task ToggleLikeAsync(string postId, CancellationToken ct = default)
        {
            _ = await ToggleLikeWithResultAsync(postId, ct);
        }

        /// <summary>
        /// Toggle Like con ritorno stato + contatore "autoritativo" letto dal post dopo la commit.
        /// </summary>
        public async Task<LikeToggleResult> ToggleLikeWithResultAsync(string postId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(postId))
                throw new ArgumentException("postId vuoto", nameof(postId));

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            if (string.IsNullOrWhiteSpace(myUid))
                throw new InvalidOperationException("Utente non valido. Rifai login.");

            var likeDocPath = $"home_posts/{postId}/likes/{myUid}";

            var alreadyLiked = await LikeExistsAsync(postId, myUid, idToken, ct);

            if (alreadyLiked)
            {
                await FirestoreRestClient.DeleteDocumentAsync(likeDocPath, idToken, ct);

                await FirestoreRestClient.CommitAsync(
                    $"home_posts/{postId}",
                    new[]
                    {
                        FirestoreRestClient.TransformIncrement("likeCount", -1)
                    },
                    idToken,
                    ct);
            }
            else
            {
                var fields = new Dictionary<string, object>
                {
                    ["createdAt"] = FirestoreRestClient.VTimestamp(DateTimeOffset.UtcNow)
                };

                await FirestoreRestClient.CreateDocumentAsync($"home_posts/{postId}/likes", myUid, fields, idToken, ct);

                await FirestoreRestClient.CommitAsync(
                    $"home_posts/{postId}",
                    new[]
                    {
                        FirestoreRestClient.TransformIncrement("likeCount", 1)
                    },
                    idToken,
                    ct);
            }

            // Lettura contatore dal post (stato "vero")
            var likeCount = await GetPostLikeCountAsync(postId, idToken, ct);

            return new LikeToggleResult(
                IsLikedNow: !alreadyLiked,
                LikeCount: Math.Max(0, likeCount));
        }

        public async Task<LikeToggleResult> ToggleLikeOptimisticAsync(
            string postId,
            bool alreadyLiked,
            int likeCountBefore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(postId))
                throw new ArgumentException("postId vuoto", nameof(postId));

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            if (string.IsNullOrWhiteSpace(myUid))
                throw new InvalidOperationException("Utente non valido. Rifai login.");

            var likeDocPath = $"home_posts/{postId}/likes/{myUid}";

            if (alreadyLiked)
            {
                await FirestoreRestClient.DeleteDocumentAsync(likeDocPath, idToken, ct);

                await FirestoreRestClient.CommitAsync(
                    $"home_posts/{postId}",
                    new[]
                    {
                        FirestoreRestClient.TransformIncrement("likeCount", -1)
                    },
                    idToken,
                    ct);

                return new LikeToggleResult(IsLikedNow: false, LikeCount: Math.Max(0, likeCountBefore - 1));
            }

            var fields = new Dictionary<string, object>
            {
                ["createdAt"] = FirestoreRestClient.VTimestamp(DateTimeOffset.UtcNow)
            };

            await FirestoreRestClient.CreateDocumentAsync($"home_posts/{postId}/likes", myUid, fields, idToken, ct);

            await FirestoreRestClient.CommitAsync(
                $"home_posts/{postId}",
                new[]
                {
                    FirestoreRestClient.TransformIncrement("likeCount", 1)
                },
                idToken,
                ct);

            return new LikeToggleResult(IsLikedNow: true, LikeCount: likeCountBefore + 1);
        }

        private static async Task<int> GetPostLikeCountAsync(string postId, string idToken, CancellationToken ct)
        {
            using var doc = await FirestoreRestClient.GetDocumentAsync($"home_posts/{postId}", idToken, ct);

            if (!doc.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                return 0;

            return (int)(ReadIntField(fields, "likeCount") ?? 0);
        }

        private static async Task<bool> LikeExistsAsync(string postId, string likeUid, string idToken, CancellationToken ct)
        {
            var likeDoc = $"home_posts/{postId}/likes/{likeUid}";
            try
            {
                await FirestoreRestClient.GetDocumentAsync(likeDoc, idToken, ct);
                return true;
            }
            catch (Exception ex)
            {
                if (IsFirestoreNotFound(ex))
                    return false;

                // Qualsiasi altro errore NON � "non esiste": va propagato (es. 403 rules)
                throw;
            }
        }

        private static bool IsFirestoreNotFound(Exception ex)
        {
            var msg = (ex?.Message ?? "");
            return msg.Contains(" 404") || msg.Contains("404.") || msg.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase);
        }

        public async Task AddCommentAsync(string postId, string text, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var nickname = FirebaseSessionePersistente.GetDisplayName() ?? "";

            string? avatarPath = null;
            try
            {
                var profile = await FirestoreDirectoryService.GetUserPublicAsync(myUid, ct);
                avatarPath = profile?.PhotoUrl;
            }
            catch
            {
                // ignore
            }

            var fields = new Dictionary<string, object>
            {
                ["authorUid"] = FirestoreRestClient.VString(myUid),
                ["authorNickname"] = FirestoreRestClient.VString(nickname),
                ["authorAvatarPath"] = string.IsNullOrWhiteSpace(avatarPath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarPath),
                ["authorAvatarUrl"] = string.IsNullOrWhiteSpace(avatarPath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarPath),
                ["createdAt"] = FirestoreRestClient.VTimestamp(DateTimeOffset.UtcNow),
                ["text"] = FirestoreRestClient.VString(text ?? "")
            };

            await FirestoreRestClient.CreateDocumentAsync($"home_posts/{postId}/comments", null, fields, idToken, ct);

            await FirestoreRestClient.CommitAsync(
                $"home_posts/{postId}",
                new[]
                {
                    FirestoreRestClient.TransformIncrement("commentCount", 1)
                },
                idToken,
                ct);
        }

        public async Task<PageResult<HomeCommentItem>> ListCommentsAsync(string postId, int pageSize, string? cursor, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            if (pageSize <= 0) pageSize = 20;
            if (pageSize > 20) pageSize = 20;

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new object[]
                {
                    new Dictionary<string, object> { ["collectionId"] = "comments" }
                },
                ["orderBy"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "createdAt" },
                        ["direction"] = "DESCENDING"
                    }
                },
                ["limit"] = pageSize
            };

            if (!string.IsNullOrWhiteSpace(cursor))
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(cursor, CultureInfo.InvariantCulture));
                structuredQuery["startAt"] = new Dictionary<string, object>
                {
                    ["values"] = new object[]
                    {
        FirestoreRestClient.VTimestamp(ts)
                    },
                    ["before"] = false
                };

            }

            using var json = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, $"home_posts/{postId}", ct);
            var items = ParseComments(json);

            string? next = null;
            if (items.Count > 0 && items.Count >= pageSize)
                next = items[^1].CreatedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

            return new PageResult<HomeCommentItem>(items, next);
        }

        public async Task<string> CreateRepostAsync(string postId, string? optionalText, CancellationToken ct = default)
        {
            var newPostId = await CreatePostAsync(optionalText ?? "", Array.Empty<HomeAttachment>(), repostOfPostId: postId, clientNonce: null, ct: ct);

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            await FirestoreRestClient.CommitAsync(
                $"home_posts/{postId}",
                new[]
                {
                    FirestoreRestClient.TransformIncrement("shareCount", 1)
                },
                idToken,
                ct);

            return newPostId;
        }

        public async Task SoftDeletePostAsync(string postId, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var fields = new Dictionary<string, object>
            {
                ["deleted"] = FirestoreRestClient.VBool(true),
                ["deletedAt"] = FirestoreRestClient.VTimestamp(DateTimeOffset.UtcNow)
            };

            await FirestoreRestClient.PatchDocumentAsync($"home_posts/{postId}", fields, new[] { "deleted", "deletedAt" }, idToken, ct);
        }

        private static object ToFirestoreAttachment(HomeAttachment attachment)
        {
            var fields = new Dictionary<string, object>
            {
                ["type"] = FirestoreRestClient.VString(attachment.Type),
                ["storagePath"] = string.IsNullOrWhiteSpace(attachment.StoragePath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.StoragePath),
                ["downloadUrl"] = string.IsNullOrWhiteSpace(attachment.DownloadUrl) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.DownloadUrl),
                ["fileName"] = string.IsNullOrWhiteSpace(attachment.FileName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.FileName),
                ["contentType"] = string.IsNullOrWhiteSpace(attachment.ContentType) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.ContentType),
                ["sizeBytes"] = FirestoreRestClient.VInt(attachment.SizeBytes),
                ["durationMs"] = FirestoreRestClient.VInt(attachment.DurationMs),
                ["extra"] = attachment.Extra == null ? FirestoreRestClient.VNull() : FirestoreRestClient.VMap(attachment.Extra),
                ["thumbStoragePath"] = string.IsNullOrWhiteSpace(attachment.ThumbStoragePath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.ThumbStoragePath),
                ["lqipBase64"] = string.IsNullOrWhiteSpace(attachment.LqipBase64) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.LqipBase64),
                ["previewType"] = string.IsNullOrWhiteSpace(attachment.PreviewType) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.PreviewType),
                ["thumbWidth"] = attachment.ThumbWidth == null ? FirestoreRestClient.VNull() : FirestoreRestClient.VInt(attachment.ThumbWidth.Value),
                ["thumbHeight"] = attachment.ThumbHeight == null ? FirestoreRestClient.VNull() : FirestoreRestClient.VInt(attachment.ThumbHeight.Value),
                ["waveform"] = attachment.Waveform == null ? FirestoreRestClient.VNull() : FirestoreRestClient.VArray(attachment.Waveform.Select(x => FirestoreRestClient.VInt(x)).ToArray())
            };

            return FirestoreRestClient.VMap(fields);
        }

        private static List<HomePostItem> ParsePosts(JsonDocument doc)
        {
            var list = new List<HomePostItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object) continue;

                var name = ReadString(d, "name") ?? "";
                var postId = ExtractLastPathSegment(name);
                if (!d.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;

                var deleted = ReadBoolField(fields, "deleted") ?? false;
                if (deleted)
                    continue;

                list.Add(new HomePostItem(
                    PostId: postId,
                    AuthorUid: ReadStringField(fields, "authorUid") ?? "",
                    AuthorNickname: ReadStringField(fields, "authorNickname") ?? "",
                    AuthorFirstName: ReadStringField(fields, "authorFirstName") ?? "",
                    AuthorLastName: ReadStringField(fields, "authorLastName") ?? "",
                    AuthorAvatarPath: ReadStringField(fields, "authorAvatarPath"),
                    AuthorAvatarUrl: ReadStringField(fields, "authorAvatarUrl"),
                    CreatedAtUtc: ReadTimestampField(fields, "createdAt") ?? DateTimeOffset.UtcNow,
                    Text: ReadStringField(fields, "text") ?? "",
                    Attachments: ReadAttachments(fields),
                    LikeCount: (int)(ReadIntField(fields, "likeCount") ?? 0),
                    CommentCount: (int)(ReadIntField(fields, "commentCount") ?? 0),
                    ShareCount: (int)(ReadIntField(fields, "shareCount") ?? 0),
                    Deleted: deleted,
                    DeletedAtUtc: ReadTimestampField(fields, "deletedAt"),
                    RepostOfPostId: ReadStringField(fields, "repostOfPostId"),
                    ClientNonce: ReadStringField(fields, "clientNonce"),
                    IsLiked: false
                ));
            }

            return list;
        }

        private static List<HomeCommentItem> ParseComments(JsonDocument doc)
        {
            var list = new List<HomeCommentItem>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("document", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(d, "name") ?? "";
                var commentId = ExtractLastPathSegment(name);
                if (!d.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object) continue;

                list.Add(new HomeCommentItem(
                    CommentId: commentId,
                    AuthorUid: ReadStringField(fields, "authorUid") ?? "",
                    AuthorNickname: ReadStringField(fields, "authorNickname") ?? "",
                    AuthorAvatarPath: ReadStringField(fields, "authorAvatarPath"),
                    AuthorAvatarUrl: ReadStringField(fields, "authorAvatarUrl"),
                    CreatedAtUtc: ReadTimestampField(fields, "createdAt") ?? DateTimeOffset.UtcNow,
                    Text: ReadStringField(fields, "text") ?? ""
                ));
            }

            return list;
        }

        private static IReadOnlyList<HomeAttachment> ReadAttachments(JsonElement fields)
        {
            var list = new List<HomeAttachment>();
            if (!fields.TryGetProperty("attachments", out var arr) || arr.ValueKind != JsonValueKind.Object) return list;
            if (!arr.TryGetProperty("arrayValue", out var arrayValue)) return list;
            if (!arrayValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return list;

            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("mapValue", out var mapVal)) continue;
                if (!mapVal.TryGetProperty("fields", out var mapFields) || mapFields.ValueKind != JsonValueKind.Object) continue;

                list.Add(new HomeAttachment(
                    Type: ReadStringField(mapFields, "type") ?? "",
                    StoragePath: ReadStringField(mapFields, "storagePath"),
                    DownloadUrl: ReadStringField(mapFields, "downloadUrl"),
                    FileName: ReadStringField(mapFields, "fileName"),
                    ContentType: ReadStringField(mapFields, "contentType"),
                    SizeBytes: ReadIntField(mapFields, "sizeBytes") ?? 0,
                    DurationMs: ReadIntField(mapFields, "durationMs") ?? 0,
                    Extra: ReadMapField(mapFields, "extra"),
                    ThumbStoragePath: ReadStringField(mapFields, "thumbStoragePath"),
                    LqipBase64: ReadStringField(mapFields, "lqipBase64"),
                    PreviewType: ReadStringField(mapFields, "previewType"),
                    ThumbWidth: ReadIntField(mapFields, "thumbWidth") is { } tw ? (int)tw : null,
                    ThumbHeight: ReadIntField(mapFields, "thumbHeight") is { } th ? (int)th : null,
                    Waveform: ReadIntArrayField(mapFields, "waveform")
                ));
            }

            return list;
        }

        private static string? BuildCursor(DateTimeOffset createdAtUtc)
            => createdAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture);

        public static string BuildCursorFrom(DateTimeOffset createdAtUtc)
            => BuildCursor(createdAtUtc) ?? "";

        private static DateTimeOffset ParseCursorTimestamp(string cursor)
        {
            var first = (cursor ?? "").Split('|')[0];

            if (!long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                ticks = DateTimeOffset.UtcNow.UtcTicks;

            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }

        private static string ExtractLastPathSegment(string name)
        {
            var idx = name.LastIndexOf("/", StringComparison.Ordinal);
            return idx >= 0 ? name[(idx + 1)..] : name;
        }

        private static string? ReadString(JsonElement obj, string prop)
        {
            return obj.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        }

        private static string? ReadStringField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("stringValue", out var s)) return s.GetString();
            return null;
        }

        private static long? ReadIntField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("integerValue", out var v) && v.ValueKind == JsonValueKind.String)
            {
                if (long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;
            }
            return null;
        }

        private static IReadOnlyList<int>? ReadIntArrayField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty("arrayValue", out var arr) || arr.ValueKind != JsonValueKind.Object) return null;
            if (!arr.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return null;

            var list = new List<int>();
            foreach (var item in values.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("integerValue", out var v) && v.ValueKind == JsonValueKind.String)
                {
                    if (int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        list.Add(n);
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static bool? ReadBoolField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (el.TryGetProperty("booleanValue", out var v) && v.ValueKind == JsonValueKind.True) return true;
            if (el.TryGetProperty("booleanValue", out var v2) && v2.ValueKind == JsonValueKind.False) return false;
            return null;
        }

        private static DateTimeOffset? ReadTimestampField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty("timestampValue", out var v) || v.ValueKind != JsonValueKind.String) return null;
            if (DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt;
            return null;
        }

        private static Dictionary<string, object>? ReadMapField(JsonElement fields, string field)
        {
            if (!fields.TryGetProperty(field, out var el) || el.ValueKind != JsonValueKind.Object) return null;
            if (!el.TryGetProperty("mapValue", out var mapVal)) return null;
            if (!mapVal.TryGetProperty("fields", out var mapFields) || mapFields.ValueKind != JsonValueKind.Object) return null;

            var dict = new Dictionary<string, object>();
            foreach (var prop in mapFields.EnumerateObject())
            {
                dict[prop.Name] = ParseFirestoreValue(prop.Value);
            }
            return dict;
        }

        private static object? ParseFirestoreValue(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) return null;

            if (value.TryGetProperty("stringValue", out var s) && s.ValueKind == JsonValueKind.String)
                return s.GetString();
            if (value.TryGetProperty("integerValue", out var i) && i.ValueKind == JsonValueKind.String &&
                long.TryParse(i.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                return l;
            if (value.TryGetProperty("doubleValue", out var d) && d.ValueKind == JsonValueKind.Number)
                return d.GetDouble();
            if (value.TryGetProperty("booleanValue", out var b))
            {
                if (b.ValueKind == JsonValueKind.True) return true;
                if (b.ValueKind == JsonValueKind.False) return false;
            }

            return null;
        }
    }
}
