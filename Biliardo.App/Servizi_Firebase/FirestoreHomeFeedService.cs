using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Biliardo.App.Infrastructure.Home;
using Biliardo.App.Servizi_Diagnostics;
using Plugin.Firebase.Firestore;

namespace Biliardo.App.Servizi_Firebase
{
    public sealed class FirestoreHomeFeedService
    {
        private readonly IFirebaseFirestore _db;

        public FirestoreHomeFeedService()
        {
            _db = CrossFirebaseFirestore.Current;
        }

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
            IReadOnlyList<int>? Waveform = null)
        {
            public string? GetPreviewRemotePath() => ThumbStoragePath;
        }

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
            int SchemaVersion,
            bool Ready,
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
            string Text,
            IReadOnlyList<HomeAttachment> Attachments,
            int SchemaVersion,
            bool Ready);

        public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);

        public async Task<IReadOnlyList<HomePostItem>> GetHomePostsPageAsync(
            DateTimeOffset? startAfterUtc,
            int limit,
            CancellationToken ct = default)
        {
            if (limit <= 0) limit = 20;

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var structuredQuery = new Dictionary<string, object>
            {
                ["from"] = new[]
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
                ["orderBy"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["field"] = new Dictionary<string, object> { ["fieldPath"] = "createdAt" },
                        ["direction"] = "DESCENDING"
                    }
                },
                ["limit"] = limit
            };

            if (startAfterUtc.HasValue)
            {
                structuredQuery["startAt"] = new Dictionary<string, object>
                {
                    ["values"] = new[] { FirestoreRestClient.VTimestamp(startAfterUtc.Value) },
                    ["before"] = false
                };
            }

            var queryDoc = await FirestoreRestClient.RunQueryAsync(structuredQuery, idToken, ct);
            var items = ParsePostsFromRunQuery(queryDoc);
            return items;
        }

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
            var displayName = FirebaseSessionePersistente.GetDisplayName();
            var email = FirebaseSessionePersistente.GetEmail();
            var nickname = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : !string.IsNullOrWhiteSpace(email)
                    ? email
                    : myUid;

            string? avatarPath = null;
            string? avatarUrl = null;
            string? firstName = null;
            string? lastName = null;

            var now = DateTimeOffset.UtcNow;

            var safeText = text ?? "";
            var safeAttachments = attachments ?? Array.Empty<HomeAttachment>();

            if (string.IsNullOrWhiteSpace(safeText) && safeAttachments.Count == 0 && string.IsNullOrWhiteSpace(repostOfPostId))
                throw new InvalidOperationException("Post vuoto.");

            var draft = new HomePostContractV2(
                PostId: "draft",
                CreatedAtUtc: now,
                AuthorUid: myUid,
                AuthorNickname: nickname ?? "",
                AuthorFirstName: firstName,
                AuthorLastName: lastName,
                AuthorAvatarPath: avatarPath,
                AuthorAvatarUrl: avatarUrl,
                Text: safeText,
                Attachments: safeAttachments.Select(ToContractAttachment).ToArray(),
                Deleted: false,
                DeletedAtUtc: null,
                RepostOfPostId: repostOfPostId,
                ClientNonce: clientNonce,
                LikeCount: 0,
                CommentCount: 0,
                ShareCount: 0,
                SchemaVersion: HomePostValidatorV2.SchemaVersion,
                Ready: false);

            var readyCandidate = draft with { Ready = true };
            var isReady = HomePostValidatorV2.IsServerReady(readyCandidate);

            DiagLog.Note("Home.CreatePost.AuthorUid", myUid);
            DiagLog.Note("Home.CreatePost.AuthorNickname", nickname);
            DiagLog.Note("Home.CreatePost.Attachments", safeAttachments.Count.ToString(CultureInfo.InvariantCulture));

            var restFields = new Dictionary<string, object>
            {
                ["schemaVersion"] = FirestoreRestClient.VInt(HomePostValidatorV2.SchemaVersion),
                ["ready"] = FirestoreRestClient.VBool(isReady),
                ["authorUid"] = FirestoreRestClient.VString(myUid),
                ["authorNickname"] = FirestoreRestClient.VString(nickname ?? ""),
                ["authorFirstName"] = string.IsNullOrWhiteSpace(firstName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(firstName),
                ["authorLastName"] = string.IsNullOrWhiteSpace(lastName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(lastName),
                ["authorAvatarPath"] = string.IsNullOrWhiteSpace(avatarPath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarPath),
                ["authorAvatarUrl"] = string.IsNullOrWhiteSpace(avatarUrl) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(avatarUrl),
                ["createdAt"] = FirestoreRestClient.VTimestamp(now),
                ["text"] = FirestoreRestClient.VString(safeText),
                ["attachments"] = FirestoreRestClient.VArray(safeAttachments.Select(ToRestAttachmentValue).ToArray()),
                ["likeCount"] = FirestoreRestClient.VInt(0),
                ["commentCount"] = FirestoreRestClient.VInt(0),
                ["shareCount"] = FirestoreRestClient.VInt(0),
                ["deleted"] = FirestoreRestClient.VBool(false),
                ["deletedAt"] = FirestoreRestClient.VNull(),
                ["repostOfPostId"] = string.IsNullOrWhiteSpace(repostOfPostId) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(repostOfPostId),
                ["clientNonce"] = string.IsNullOrWhiteSpace(clientNonce) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(clientNonce)
            };

            var created = await FirestoreRestClient.CreateDocumentAsync("home_posts", null, restFields, idToken, ct);
            var postId = ParseDocumentId(created);
            return postId;
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

            var alreadyLiked = await LikeExistsAsync(postId, myUid, ct);

            var likeDoc = _db.GetDocument(likeDocPath);
            var postDoc = _db.GetDocument($"home_posts/{postId}");

            if (alreadyLiked)
            {
                await likeDoc.DeleteDocumentAsync();
                await postDoc.UpdateDataAsync(new[]
                {
                    ("likeCount", (object)FieldValue.IntegerIncrement(-1))
                });
            }
            else
            {
                await likeDoc.SetDataAsync(new Dictionary<string, object>
                {
                    ["createdAt"] = DateTimeOffset.UtcNow
                });
                await postDoc.UpdateDataAsync(new[]
                {
                    ("likeCount", (object)FieldValue.IntegerIncrement(1))
                });
            }

            // Lettura contatore dal post (stato "vero")
            var likeCount = await GetPostLikeCountAsync(postId, ct);

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

            var likeDoc = _db.GetDocument(likeDocPath);
            var postDoc = _db.GetDocument($"home_posts/{postId}");

            if (alreadyLiked)
            {
                await likeDoc.DeleteDocumentAsync();
                await postDoc.UpdateDataAsync(new[]
                {
                    ("likeCount", (object)FieldValue.IntegerIncrement(-1))
                });

                return new LikeToggleResult(IsLikedNow: false, LikeCount: Math.Max(0, likeCountBefore - 1));
            }

            await likeDoc.SetDataAsync(new Dictionary<string, object>
            {
                ["createdAt"] = DateTimeOffset.UtcNow
            });

            await postDoc.UpdateDataAsync(new[]
            {
                ("likeCount", (object)FieldValue.IntegerIncrement(1))
            });

            return new LikeToggleResult(IsLikedNow: true, LikeCount: likeCountBefore + 1);
        }

        private static async Task<int> GetPostLikeCountAsync(string postId, CancellationToken ct)
        {
            var doc = CrossFirebaseFirestore.Current.GetDocument($"home_posts/{postId}");
            var snapshot = await doc.GetDocumentSnapshotAsync<Dictionary<string, object>>(Source.Default);
            var count = 0;
            var data = snapshot.Data;
            if (data != null && data.TryGetValue("likeCount", out var value))
                count = TryParseInt(value) ?? 0;
            return count;
        }

        private static async Task<bool> LikeExistsAsync(string postId, string likeUid, CancellationToken ct)
        {
            var doc = CrossFirebaseFirestore.Current.GetDocument($"home_posts/{postId}/likes/{likeUid}");
            var snapshot = await doc.GetDocumentSnapshotAsync<Dictionary<string, object>>(Source.Default);
            return snapshot.Data != null;
        }

        private static int? TryParseInt(object? value)
        {
            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is double d)
                return (int)d;
            if (int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        public async Task AddCommentAsync(string postId, string text, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var myUid = FirebaseSessionePersistente.GetLocalId() ?? "";
            var nickname = FirebaseSessionePersistente.GetDisplayName() ?? "";

            string? avatarPath = null;
            string? avatarUrl = null;

            var fields = new Dictionary<string, object?>
            {
                ["authorUid"] = myUid,
                ["authorNickname"] = string.IsNullOrWhiteSpace(nickname) ? null : nickname,
                ["authorAvatarPath"] = string.IsNullOrWhiteSpace(avatarPath) ? null : avatarPath,
                ["authorAvatarUrl"] = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl,
                ["createdAt"] = DateTimeOffset.UtcNow,
                ["text"] = text ?? "",
                ["schemaVersion"] = HomePostValidatorV2.SchemaVersion,
                ["ready"] = true,
                ["attachments"] = new List<object>()
            };

            var commentCollection = _db.GetCollection($"home_posts/{postId}/comments");
            await commentCollection.AddDocumentAsync(fields);

            var postDoc = _db.GetDocument($"home_posts/{postId}");
            await postDoc.UpdateDataAsync(new[]
            {
                ("commentCount", (object)FieldValue.IntegerIncrement(1))
            });
        }

        public async Task<string> CreateRepostAsync(string postId, string? optionalText, CancellationToken ct = default)
        {
            var newPostId = await CreatePostAsync(optionalText ?? "", Array.Empty<HomeAttachment>(), repostOfPostId: postId, clientNonce: null, ct: ct);

            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var postDoc = _db.GetDocument($"home_posts/{postId}");
            await postDoc.UpdateDataAsync(new[]
            {
                ("shareCount", (object)FieldValue.IntegerIncrement(1))
            });

            return newPostId;
        }

        public async Task SoftDeletePostAsync(string postId, CancellationToken ct = default)
        {
            var idToken = await FirebaseSessionePersistente.GetIdTokenValidoAsync(ct);
            if (string.IsNullOrWhiteSpace(idToken))
                throw new InvalidOperationException("Sessione scaduta. Rifai login.");

            var postDoc = _db.GetDocument($"home_posts/{postId}");
            await postDoc.UpdateDataAsync(new[]
            {
                ("deleted", (object)true),
                ("deletedAt", (object)DateTimeOffset.UtcNow)
            });
        }

        private static Dictionary<string, object?> ToFirestoreAttachment(HomeAttachment attachment)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = attachment.Type,
                ["storagePath"] = string.IsNullOrWhiteSpace(attachment.StoragePath) ? null : attachment.StoragePath,
                ["downloadUrl"] = string.IsNullOrWhiteSpace(attachment.DownloadUrl) ? null : attachment.DownloadUrl,
                ["fileName"] = string.IsNullOrWhiteSpace(attachment.FileName) ? null : attachment.FileName,
                ["contentType"] = string.IsNullOrWhiteSpace(attachment.ContentType) ? null : attachment.ContentType,
                ["sizeBytes"] = attachment.SizeBytes,
                ["durationMs"] = attachment.DurationMs,
                ["extra"] = attachment.Extra,
                ["thumbStoragePath"] = string.IsNullOrWhiteSpace(attachment.ThumbStoragePath) ? null : attachment.ThumbStoragePath,
                ["lqipBase64"] = string.IsNullOrWhiteSpace(attachment.LqipBase64) ? null : attachment.LqipBase64,
                ["previewType"] = string.IsNullOrWhiteSpace(attachment.PreviewType) ? null : attachment.PreviewType,
                ["thumbWidth"] = attachment.ThumbWidth,
                ["thumbHeight"] = attachment.ThumbHeight,
                ["waveform"] = attachment.Waveform?.ToList()
            };
        }

        private static object ToRestAttachmentValue(HomeAttachment attachment)
        {
            var fields = new Dictionary<string, object>
            {
                ["type"] = FirestoreRestClient.VString(attachment.Type ?? ""),
                ["storagePath"] = string.IsNullOrWhiteSpace(attachment.StoragePath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.StoragePath),
                ["downloadUrl"] = string.IsNullOrWhiteSpace(attachment.DownloadUrl) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.DownloadUrl),
                ["fileName"] = string.IsNullOrWhiteSpace(attachment.FileName) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.FileName),
                ["contentType"] = string.IsNullOrWhiteSpace(attachment.ContentType) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.ContentType),
                ["sizeBytes"] = FirestoreRestClient.VInt(attachment.SizeBytes),
                ["durationMs"] = FirestoreRestClient.VInt(attachment.DurationMs),
                ["extra"] = attachment.Extra == null ? FirestoreRestClient.VNull() : ToRestValue(attachment.Extra),
                ["thumbStoragePath"] = string.IsNullOrWhiteSpace(attachment.ThumbStoragePath) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.ThumbStoragePath),
                ["lqipBase64"] = string.IsNullOrWhiteSpace(attachment.LqipBase64) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.LqipBase64),
                ["previewType"] = string.IsNullOrWhiteSpace(attachment.PreviewType) ? FirestoreRestClient.VNull() : FirestoreRestClient.VString(attachment.PreviewType),
                ["thumbWidth"] = attachment.ThumbWidth.HasValue ? FirestoreRestClient.VInt(attachment.ThumbWidth.Value) : FirestoreRestClient.VNull(),
                ["thumbHeight"] = attachment.ThumbHeight.HasValue ? FirestoreRestClient.VInt(attachment.ThumbHeight.Value) : FirestoreRestClient.VNull(),
                ["waveform"] = attachment.Waveform == null ? FirestoreRestClient.VNull() : FirestoreRestClient.VArray(attachment.Waveform.Select(v => FirestoreRestClient.VInt(v)).ToArray())
            };

            return FirestoreRestClient.VMap(fields);
        }

        private static object ToRestValue(object? value)
        {
            if (value == null)
                return FirestoreRestClient.VNull();

            switch (value)
            {
                case string s:
                    return FirestoreRestClient.VString(s);
                case bool b:
                    return FirestoreRestClient.VBool(b);
                case int i:
                    return FirestoreRestClient.VInt(i);
                case long l:
                    return FirestoreRestClient.VInt(l);
                case short sh:
                    return FirestoreRestClient.VInt(sh);
                case double d:
                    return FirestoreRestClient.VDouble(d);
                case float f:
                    return FirestoreRestClient.VDouble(f);
                case decimal m:
                    return FirestoreRestClient.VDouble((double)m);
                case DateTimeOffset dto:
                    return FirestoreRestClient.VTimestamp(dto);
                case DateTime dt:
                    return FirestoreRestClient.VTimestamp(new DateTimeOffset(dt));
                case IDictionary<string, object> map:
                    {
                        var fields = new Dictionary<string, object>();
                        foreach (var kv in map)
                            fields[kv.Key] = ToRestValue(kv.Value);
                        return FirestoreRestClient.VMap(fields);
                    }
                case IEnumerable<int> ints:
                    return FirestoreRestClient.VArray(ints.Select(v => FirestoreRestClient.VInt(v)).ToArray());
                case IEnumerable<object> objs:
                    return FirestoreRestClient.VArray(objs.Select(ToRestValue).ToArray());
                default:
                    return FirestoreRestClient.VString(value.ToString() ?? "");
            }
        }

        private static string ParseDocumentId(JsonDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (!document.RootElement.TryGetProperty("name", out var nameProp))
                throw new InvalidOperationException("Firestore CREATE: risposta senza nome documento.");

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Firestore CREATE: nome documento vuoto.");

            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : throw new InvalidOperationException("Firestore CREATE: ID documento non valido.");
        }

        private static HomeAttachmentContractV2 ToContractAttachment(HomeAttachment attachment)
        {
            return new HomeAttachmentContractV2(
                Type: attachment.Type ?? "",
                FileName: attachment.FileName,
                ContentType: attachment.ContentType,
                SizeBytes: attachment.SizeBytes,
                DurationMs: attachment.DurationMs,
                Extra: attachment.Extra,
                PreviewStoragePath: attachment.GetPreviewRemotePath(),
                FullStoragePath: attachment.StoragePath,
                DownloadUrl: attachment.DownloadUrl,
                PreviewLocalPath: null,
                FullLocalPath: null,
                LqipBase64: attachment.LqipBase64,
                PreviewType: attachment.PreviewType,
                PreviewWidth: attachment.ThumbWidth,
                PreviewHeight: attachment.ThumbHeight,
                Waveform: attachment.Waveform);
        }

        private static List<HomePostItem> ParsePostsFromRunQuery(JsonDocument doc)
        {
            var list = new List<HomePostItem>();
            if (doc == null)
                return list;

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("document", out var docElement))
                    continue;
                if (!docElement.TryGetProperty("name", out var nameProp))
                    continue;
                if (!docElement.TryGetProperty("fields", out var fields))
                    continue;

                var name = nameProp.GetString();
                var postId = ExtractDocumentId(name);
                if (string.IsNullOrWhiteSpace(postId))
                    continue;

                var createdAt = ReadTimestamp(fields, "createdAt") ?? DateTimeOffset.UtcNow;
                var attachments = ReadAttachments(fields);

                var item = new HomePostItem(
                    PostId: postId,
                    AuthorUid: ReadString(fields, "authorUid") ?? "",
                    AuthorNickname: ReadString(fields, "authorNickname") ?? "",
                    AuthorFirstName: ReadString(fields, "authorFirstName") ?? "",
                    AuthorLastName: ReadString(fields, "authorLastName") ?? "",
                    AuthorAvatarPath: ReadString(fields, "authorAvatarPath"),
                    AuthorAvatarUrl: ReadString(fields, "authorAvatarUrl"),
                    CreatedAtUtc: createdAt,
                    Text: ReadString(fields, "text") ?? "",
                    Attachments: attachments,
                    LikeCount: ReadInt(fields, "likeCount") ?? 0,
                    CommentCount: ReadInt(fields, "commentCount") ?? 0,
                    ShareCount: ReadInt(fields, "shareCount") ?? 0,
                    Deleted: ReadBool(fields, "deleted") ?? false,
                    DeletedAtUtc: ReadTimestamp(fields, "deletedAt"),
                    RepostOfPostId: ReadString(fields, "repostOfPostId"),
                    ClientNonce: ReadString(fields, "clientNonce"),
                    SchemaVersion: ReadInt(fields, "schemaVersion") ?? HomePostValidatorV2.SchemaVersion,
                    Ready: ReadBool(fields, "ready") ?? true,
                    IsLiked: false);

                list.Add(item);
            }

            return list
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();
        }

        private static string? ExtractDocumentId(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[^1] : null;
        }

        private static IReadOnlyList<HomeAttachment> ReadAttachments(JsonElement fields)
        {
            var list = new List<HomeAttachment>();
            if (!fields.TryGetProperty("attachments", out var attField))
                return list;
            if (!attField.TryGetProperty("arrayValue", out var arrayValue))
                return list;
            if (!arrayValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var val in values.EnumerateArray())
            {
                if (!val.TryGetProperty("mapValue", out var mapValue))
                    continue;
                if (!mapValue.TryGetProperty("fields", out var attFields))
                    continue;

                list.Add(new HomeAttachment(
                    Type: ReadString(attFields, "type") ?? "",
                    StoragePath: ReadString(attFields, "storagePath"),
                    DownloadUrl: ReadString(attFields, "downloadUrl"),
                    FileName: ReadString(attFields, "fileName"),
                    ContentType: ReadString(attFields, "contentType"),
                    SizeBytes: ReadInt64(attFields, "sizeBytes"),
                    DurationMs: ReadInt64(attFields, "durationMs"),
                    Extra: ReadMap(attFields, "extra"),
                    ThumbStoragePath: ReadString(attFields, "thumbStoragePath"),
                    LqipBase64: ReadString(attFields, "lqipBase64"),
                    PreviewType: ReadString(attFields, "previewType"),
                    ThumbWidth: ReadInt(attFields, "thumbWidth"),
                    ThumbHeight: ReadInt(attFields, "thumbHeight"),
                    Waveform: ReadIntArray(attFields, "waveform")));
            }

            return list;
        }

        private static string? ReadString(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (field.TryGetProperty("stringValue", out var value))
                return value.GetString();
            return null;
        }

        private static bool? ReadBool(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (field.TryGetProperty("booleanValue", out var value))
                return value.GetBoolean();
            return null;
        }

        private static int? ReadInt(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (!field.TryGetProperty("integerValue", out var value))
                return null;
            if (int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        private static long ReadInt64(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return 0;
            if (!field.TryGetProperty("integerValue", out var value))
                return 0;
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }

        private static DateTimeOffset? ReadTimestamp(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (!field.TryGetProperty("timestampValue", out var value))
                return null;
            if (DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            return null;
        }

        private static Dictionary<string, object>? ReadMap(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (!field.TryGetProperty("mapValue", out var mapValue))
                return null;
            if (!mapValue.TryGetProperty("fields", out var mapFields))
                return null;

            var dict = new Dictionary<string, object>();
            foreach (var prop in mapFields.EnumerateObject())
            {
                var val = ReadValue(prop.Value);
                if (val != null)
                    dict[prop.Name] = val;
            }

            return dict.Count == 0 ? null : dict;
        }

        private static IReadOnlyList<int>? ReadIntArray(JsonElement fields, string name)
        {
            if (!fields.TryGetProperty(name, out var field))
                return null;
            if (!field.TryGetProperty("arrayValue", out var arrayValue))
                return null;
            if (!arrayValue.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<int>();
            foreach (var item in values.EnumerateArray())
            {
                if (item.TryGetProperty("integerValue", out var intVal)
                    && int.TryParse(intVal.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    list.Add(parsed);
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static object? ReadValue(JsonElement value)
        {
            if (value.TryGetProperty("stringValue", out var s))
                return s.GetString() ?? "";
            if (value.TryGetProperty("integerValue", out var i))
                return long.TryParse(i.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            if (value.TryGetProperty("doubleValue", out var d))
                return d.GetDouble();
            if (value.TryGetProperty("booleanValue", out var b))
                return b.GetBoolean();
            if (value.TryGetProperty("timestampValue", out var t))
            {
                return DateTimeOffset.TryParse(t.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : null;
            }
            if (value.TryGetProperty("mapValue", out var mapValue) && mapValue.TryGetProperty("fields", out var mapFields))
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in mapFields.EnumerateObject())
                {
                    var inner = ReadValue(prop.Value);
                    if (inner != null)
                        dict[prop.Name] = inner;
                }
                return dict;
            }
            if (value.TryGetProperty("arrayValue", out var arrayValue) && arrayValue.TryGetProperty("values", out var values))
            {
                var list = new List<object>();
                foreach (var item in values.EnumerateArray())
                {
                    var inner = ReadValue(item);
                    if (inner != null)
                        list.Add(inner);
                }
                return list;
            }

            return null;
        }

    }
}
