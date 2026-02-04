using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Biliardo.App.Infrastructure.Home;
using Plugin.Firebase.Firestore;

namespace Biliardo.App.Servizi_Firebase
{
    public sealed class FirestoreRealtimeService
    {
        private const int TypingStaleSeconds = 6;
        private readonly IFirebaseFirestore _db;

        public FirestoreRealtimeService()
        {
            _db = CrossFirebaseFirestore.Current;
        }

        public IDisposable SubscribeHomePosts(
            int limit,
            Action<IReadOnlyList<FirestoreHomeFeedService.HomePostItem>> onChanged,
            Action<Exception>? onError = null)
        {
            if (limit <= 0) limit = 20;

            var query = _db
                .GetCollection("home_posts")
                .WhereEqualsTo("deleted", false)
                .OrderBy("createdAt", true)
                .LimitedTo(limit);

            return query.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    var items = new List<FirestoreHomeFeedService.HomePostItem>();
                    foreach (var doc in snapshot.Documents)
                    {
                        var post = MapHomePost(doc);
                        if (post != null)
                            items.Add(post);
                    }

                    onChanged(items);
                },
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        public IDisposable SubscribeUserPublic(
            string uid,
            Action<FirestoreDirectoryService.UserPublicItem?> onChanged,
            Action<Exception>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(uid))
                throw new ArgumentException("uid required", nameof(uid));

            var doc = _db.GetDocument($"users_public/{uid.Trim()}");
            return doc.AddSnapshotListener<Dictionary<string, object>>(
                snapshot => onChanged(MapUserPublic(uid, snapshot)),
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        public IDisposable SubscribeComments(
            string postId,
            int limit,
            Action<IReadOnlyList<FirestoreHomeFeedService.HomeCommentItem>> onChanged,
            Action<Exception>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(postId))
                throw new ArgumentException("postId required", nameof(postId));

            if (limit <= 0) limit = 30;

            var query = _db
                .GetCollection($"home_posts/{postId}/comments")
                .OrderBy("createdAt", true)
                .LimitedTo(limit);

            return query.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    var items = new List<FirestoreHomeFeedService.HomeCommentItem>();
                    foreach (var doc in snapshot.Documents)
                    {
                        var item = MapHomeComment(doc);
                        if (item != null)
                            items.Add(item);
                    }

                    onChanged(items);
                },
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        public IDisposable SubscribeChatList(
            string myUid,
            int limit,
            Action<IReadOnlyList<FirestoreChatService.ChatItem>> onChanged,
            Action<Exception>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(myUid))
                throw new ArgumentException("myUid required", nameof(myUid));

            if (limit <= 0) limit = 60;

            var query = _db
                .GetCollection("chats")
                .WhereArrayContains("members", myUid)
                .OrderBy("updatedAt", true)
                .LimitedTo(limit);

            return query.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    var items = new List<FirestoreChatService.ChatItem>();
                    foreach (var doc in snapshot.Documents)
                    {
                        var item = MapChat(doc, myUid);
                        if (item != null)
                            items.Add(item);
                    }

                    onChanged(items);
                },
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        public IDisposable SubscribeChatMessages(
            string chatId,
            int limit,
            Action<IReadOnlyList<FirestoreChatService.MessageItem>> onChanged,
            Action<Exception>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                throw new ArgumentException("chatId required", nameof(chatId));

            if (limit <= 0) limit = 80;

            var query = _db
                .GetCollection($"chats/{chatId}/messages")
                .OrderBy("createdAt", false)
                .LimitedTo(limit);

            return query.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    var items = new List<FirestoreChatService.MessageItem>();
                    foreach (var doc in snapshot.Documents)
                    {
                        var item = MapChatMessage(doc);
                        if (item != null)
                            items.Add(item);
                    }

                    onChanged(items.OrderBy(x => x.CreatedAtUtc).ToList());
                },
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        public IDisposable SubscribeChatTyping(
            string chatId,
            string myUid,
            Action<bool> onChanged,
            Action<Exception>? onError = null)
        {
            if (string.IsNullOrWhiteSpace(chatId))
                throw new ArgumentException("chatId required", nameof(chatId));
            if (string.IsNullOrWhiteSpace(myUid))
                throw new ArgumentException("myUid required", nameof(myUid));

            var doc = _db.GetDocument($"chats/{chatId}");
            return doc.AddSnapshotListener<Dictionary<string, object>>(
                snapshot =>
                {
                    var data = snapshot.Data;
                    if (data == null)
                    {
                        onChanged(false);
                        return;
                    }

                    var payload = ReadDictionary(data, "payload");
                    var peerUid = GetPeerUidFromMembers(data, myUid);
                    var (isTyping, _) = ComputePeerTyping(payload, myUid, peerUid);
                    onChanged(isTyping);
                },
                ex => onError?.Invoke(ex),
                includeMetadataChanges: false);
        }

        private static FirestoreHomeFeedService.HomePostItem? MapHomePost(IDocumentSnapshot<Dictionary<string, object>> doc)
        {
            var data = doc.Data;
            if (data == null)
                return null;

            var createdAt = ReadTimestamp(data.TryGetValue("createdAt", out var created) ? created : null) ?? DateTimeOffset.UtcNow;

            var attachments = new List<FirestoreHomeFeedService.HomeAttachment>();
            if (data.TryGetValue("attachments", out var attachmentsObj) && attachmentsObj is IEnumerable<object> list)
            {
                foreach (var item in list)
                {
                    if (item is IDictionary<string, object> map)
                    {
                        attachments.Add(new FirestoreHomeFeedService.HomeAttachment(
                            Type: ReadString(map, "type") ?? "",
                            StoragePath: ReadString(map, "storagePath"),
                            DownloadUrl: ReadString(map, "downloadUrl"),
                            FileName: ReadString(map, "fileName"),
                            ContentType: ReadString(map, "contentType"),
                            SizeBytes: ReadInt64(map, "sizeBytes"),
                            DurationMs: ReadInt64(map, "durationMs"),
                            Extra: ReadDictionary(map, "extra"),
                            ThumbStoragePath: ReadString(map, "thumbStoragePath"),
                            LqipBase64: ReadString(map, "lqipBase64"),
                            PreviewType: ReadString(map, "previewType"),
                            ThumbWidth: ReadInt(map, "thumbWidth"),
                            ThumbHeight: ReadInt(map, "thumbHeight"),
                            Waveform: ReadIntArray(map, "waveform")));
                    }
                }
            }

            return new FirestoreHomeFeedService.HomePostItem(
                PostId: doc.Reference.Id,
                AuthorUid: ReadString(data, "authorUid") ?? "",
                AuthorNickname: ReadString(data, "authorNickname") ?? "",
                AuthorFirstName: ReadString(data, "authorFirstName") ?? "",
                AuthorLastName: ReadString(data, "authorLastName") ?? "",
                AuthorAvatarPath: ReadString(data, "authorAvatarPath"),
                AuthorAvatarUrl: ReadString(data, "authorAvatarUrl"),
                CreatedAtUtc: createdAt,
                Text: ReadString(data, "text") ?? "",
                Attachments: attachments,
                LikeCount: ReadInt(data, "likeCount") ?? 0,
                CommentCount: ReadInt(data, "commentCount") ?? 0,
                ShareCount: ReadInt(data, "shareCount") ?? 0,
                Deleted: ReadBool(data, "deleted") ?? false,
                DeletedAtUtc: ReadTimestamp(data.TryGetValue("deletedAt", out var deletedAt) ? deletedAt : null),
                RepostOfPostId: ReadString(data, "repostOfPostId"),
                ClientNonce: ReadString(data, "clientNonce"),
                SchemaVersion: ReadInt(data, "schemaVersion") ?? 0,
                Ready: ReadBool(data, "ready") ?? true,
                IsLiked: false);
        }

        private static FirestoreHomeFeedService.HomeCommentItem? MapHomeComment(IDocumentSnapshot<Dictionary<string, object>> doc)
        {
            var data = doc.Data;
            if (data == null)
                return null;

            var createdAt = ReadTimestamp(data.TryGetValue("createdAt", out var created) ? created : null) ?? DateTimeOffset.UtcNow;

            return new FirestoreHomeFeedService.HomeCommentItem(
                CommentId: doc.Reference.Id,
                AuthorUid: ReadString(data, "authorUid") ?? "",
                AuthorNickname: ReadString(data, "authorNickname") ?? "",
                AuthorAvatarPath: ReadString(data, "authorAvatarPath"),
                AuthorAvatarUrl: ReadString(data, "authorAvatarUrl"),
                CreatedAtUtc: createdAt,
                Text: ReadString(data, "text") ?? "",
                Attachments: Array.Empty<FirestoreHomeFeedService.HomeAttachment>(),
                SchemaVersion: ReadInt(data, "schemaVersion") ?? HomePostValidatorV2.SchemaVersion,
                Ready: ReadBool(data, "ready") ?? true);
        }

        private static FirestoreDirectoryService.UserPublicItem? MapUserPublic(string uid, IDocumentSnapshot<Dictionary<string, object>> snapshot)
        {
            var data = snapshot.Data;
            if (data == null)
                return null;

            var nickname = ReadString(data, "nickname") ?? "";
            var nicknameLower = ReadString(data, "nicknameLower") ?? (string.IsNullOrWhiteSpace(nickname) ? "" : nickname.ToLowerInvariant());

            var firstName = ReadString(data, "firstName") ?? ReadString(data, "nome") ?? "";
            var lastName = ReadString(data, "lastName") ?? ReadString(data, "cognome") ?? "";

            var avatarUrl = ReadString(data, "avatarUrl") ?? ReadString(data, "photoUrl") ?? "";
            var avatarPath = ReadString(data, "avatarPath") ?? "";

            return new FirestoreDirectoryService.UserPublicItem
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

        private static FirestoreChatService.ChatItem? MapChat(IDocumentSnapshot<Dictionary<string, object>> doc, string myUid)
        {
            var data = doc.Data;
            if (data == null)
                return null;

            var members = ReadStringArray(data, "members");
            var peerUid = members.FirstOrDefault(x => !string.Equals(x, myUid, StringComparison.Ordinal)) ?? "";

            var memberNicknames = ReadDictionary(data, "memberNicknames");
            var peerNick = memberNicknames != null && memberNicknames.TryGetValue(peerUid, out var nickObj) ? nickObj?.ToString() ?? "" : "";

            var payload = ReadDictionary(data, "payload");
            var (isPeerTyping, typingAtUtc) = ComputePeerTyping(payload, myUid, peerUid);

            return new FirestoreChatService.ChatItem(
                ChatId: doc.Reference.Id,
                PeerUid: peerUid,
                PeerNickname: peerNick,
                LastText: ReadString(data, "lastMessageText") ?? "",
                LastType: ReadString(data, "lastMessageType") ?? "text",
                LastAtUtc: ReadTimestamp(data.TryGetValue("lastMessageAt", out var lastAt) ? lastAt : null),
                UpdatedAtUtc: ReadTimestamp(data.TryGetValue("updatedAt", out var updated) ? updated : null),
                IsPeerTyping: isPeerTyping,
                PeerTypingAtUtc: typingAtUtc);
        }

        private static FirestoreChatService.MessageItem? MapChatMessage(IDocumentSnapshot<Dictionary<string, object>> doc)
        {
            var data = doc.Data;
            if (data == null)
                return null;

            var createdAt = ReadTimestamp(data.TryGetValue("createdAt", out var created) ? created : null) ?? DateTimeOffset.MinValue;

            var senderId = ReadString(data, "senderId") ?? ReadString(data, "fromUid") ?? "";
            var type = ReadString(data, "type") ?? "text";

            var payload = ReadDictionary(data, "payload") ?? new Dictionary<string, object>();
            var text = ReadString(payload, "text") ?? ReadString(data, "text") ?? "";

            var preview = ReadDictionary(payload, "preview");

            return new FirestoreChatService.MessageItem(
                MessageId: doc.Reference.Id,
                SenderId: senderId,
                Type: type,
                Text: text,
                CreatedAtUtc: createdAt,
                DeliveredTo: ReadStringArray(data, "deliveredTo"),
                ReadBy: ReadStringArray(data, "readBy"),
                DeletedForAll: ReadBool(data, "deletedForAll") ?? false,
                DeletedFor: ReadStringArray(data, "deletedFor"),
                DeletedAtUtc: ReadTimestamp(data.TryGetValue("deletedAt", out var deletedAt) ? deletedAt : null),
                StoragePath: ReadString(payload, "storagePath"),
                DurationMs: ReadInt64(payload, "durationMs"),
                FileName: ReadString(payload, "fileName"),
                ContentType: ReadString(payload, "contentType"),
                SizeBytes: ReadInt64(payload, "sizeBytes"),
                ThumbStoragePath: ReadString(preview, "thumbStoragePath"),
                LqipBase64: ReadString(preview, "lqipBase64"),
                ThumbWidth: ReadInt(preview, "thumbWidth"),
                ThumbHeight: ReadInt(preview, "thumbHeight"),
                PreviewType: ReadString(preview, "previewType"),
                Waveform: ReadIntArray(payload, "waveform"),
                Latitude: ReadDouble(payload, "lat"),
                Longitude: ReadDouble(payload, "lon"),
                ContactName: ReadString(payload, "contactName"),
                ContactPhone: ReadString(payload, "contactPhone"));
        }

        private static string? ReadString(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? value?.ToString() : null;

        private static bool? ReadBool(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? value as bool? ?? TryParseBool(value) : null;

        private static int? ReadInt(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? TryParseInt(value) : null;

        private static int? ReadInt(IDictionary<string, object>? map, string key, int? fallback)
            => ReadInt(map, key) ?? fallback;

        private static long ReadInt64(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? TryParseInt64(value) : 0;

        private static double? ReadDouble(IDictionary<string, object>? map, string key)
            => map != null && map.TryGetValue(key, out var value) ? TryParseDouble(value) : null;

        private static IReadOnlyList<int>? ReadIntArray(IDictionary<string, object>? map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value))
                return null;

            if (value is IEnumerable<int> intList)
                return intList.ToList();

            if (value is not IEnumerable<object> list)
                return null;

            var outList = new List<int>();
            foreach (var item in list)
            {
                var v = TryParseInt(item);
                if (v != null)
                    outList.Add(v.Value);
            }

            return outList.Count == 0 ? null : outList;
        }

        private static List<string> ReadStringArray(IDictionary<string, object>? map, string key)
        {
            var list = new List<string>();
            if (map == null || !map.TryGetValue(key, out var value))
                return list;

            if (value is IEnumerable<string> strings)
            {
                foreach (var item in strings)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        list.Add(item);
                }
                return list;
            }

            if (value is not IEnumerable<object> items)
                return list;

            foreach (var item in items)
            {
                var s = item?.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }

            return list;
        }

        private static Dictionary<string, object>? ReadDictionary(IDictionary<string, object>? map, string key)
        {
            if (map == null || !map.TryGetValue(key, out var value))
                return null;

            if (value is Dictionary<string, object> dict)
                return dict;

            if (value is IDictionary<string, object> idict)
                return new Dictionary<string, object>(idict);

            return null;
        }

        private static (bool IsPeerTyping, DateTimeOffset? TypingAtUtc) ComputePeerTyping(
            IDictionary<string, object>? payload,
            string myUid,
            string peerUid)
        {
            if (string.IsNullOrWhiteSpace(peerUid))
                return (false, null);

            var typingUid = ReadString(payload, "typingUid");
            if (string.IsNullOrWhiteSpace(typingUid) || !string.Equals(typingUid, peerUid, StringComparison.Ordinal))
                return (false, null);

            var typingAt = payload != null && payload.TryGetValue("typingAt", out var at) ? ReadTimestamp(at) : null;
            if (typingAt == null)
                return (false, null);

            var isFresh = DateTimeOffset.UtcNow - typingAt.Value <= TimeSpan.FromSeconds(TypingStaleSeconds);
            return (isFresh, typingAt);
        }

        private static string GetPeerUidFromMembers(IDictionary<string, object> data, string myUid)
        {
            var members = ReadStringArray(data, "members");
            return members.FirstOrDefault(x => !string.Equals(x, myUid, StringComparison.Ordinal)) ?? "";
        }

        private static DateTimeOffset? ReadTimestamp(object? value)
        {
            if (value == null)
                return null;

            if (value is DateTimeOffset dto)
                return dto;

            if (value is DateTime dt)
                return new DateTimeOffset(dt);

            if (value is long l)
                return DateTimeOffset.FromUnixTimeMilliseconds(l);

            if (value is double d)
                return DateTimeOffset.FromUnixTimeMilliseconds((long)d);

            if (DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;

            return null;
        }

        private static bool? TryParseBool(object? value)
        {
            if (value is bool b)
                return b;

            if (bool.TryParse(value?.ToString(), out var parsed))
                return parsed;

            return null;
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

        private static long TryParseInt64(object? value)
        {
            if (value is long l)
                return l;
            if (value is int i)
                return i;
            if (value is double d)
                return (long)d;
            if (long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return 0;
        }

        private static double? TryParseDouble(object? value)
        {
            if (value is double d)
                return d;
            if (value is float f)
                return f;
            if (value is int i)
                return i;
            if (double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }
    }
}
