using System;
using System.Collections.Generic;

namespace Biliardo.App.Infrastructure.Sync
{
    public static class PushPayloadValidator
    {
        public static bool TryGetContentId(IReadOnlyDictionary<string, string> data, out string contentId)
        {
            contentId = "";
            if (data == null)
                return false;

            if (data.TryGetValue("contentId", out var cid) && !string.IsNullOrWhiteSpace(cid))
            {
                contentId = cid;
                return true;
            }

            if (data.TryGetValue("postId", out var postId) && !string.IsNullOrWhiteSpace(postId))
            {
                contentId = postId;
                return true;
            }

            if (data.TryGetValue("messageId", out var messageId) && !string.IsNullOrWhiteSpace(messageId))
            {
                contentId = messageId;
                return true;
            }

            if (data.TryGetValue("documentId", out var documentId) && !string.IsNullOrWhiteSpace(documentId))
            {
                contentId = documentId;
                return true;
            }

            return false;
        }

        public static bool IsPayloadComplete(IReadOnlyDictionary<string, string> data, out string kind, out string contentId)
        {
            kind = data != null && data.TryGetValue("kind", out var k) ? k : "";
            contentId = "";

            if (data == null || data.Count == 0)
                return false;

            if (string.IsNullOrWhiteSpace(kind))
                return false;

            if (!TryGetContentId(data, out contentId))
                return false;

            var hasTitle = data.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title);
            var hasBody = data.TryGetValue("body", out var body) && !string.IsNullOrWhiteSpace(body);
            var hasTimestamp = data.TryGetValue("serverTimestamp", out var ts) && !string.IsNullOrWhiteSpace(ts);
            var hasAuthor = data.TryGetValue("authorId", out var author) && !string.IsNullOrWhiteSpace(author);

            return hasTitle && hasBody && hasTimestamp && hasAuthor;
        }
    }
}
