using System;
using System.Collections.Generic;

namespace Biliardo.App.Infrastructure.Home
{
    public static class HomePostValidatorV2
    {
        public const int SchemaVersion = 2;

        public static bool IsServerReady(HomePostContractV2 post) => IsServerReady(post, out _);

        public static bool IsRenderSafe(HomePostContractV2 post) => IsRenderSafe(post, out _);

        public static bool IsCacheSafe(HomePostContractV2 post) => IsCacheSafe(post, out _);

        public static bool IsServerReady(HomePostContractV2 post, out string reason)
        {
            if (post == null)
            {
                reason = "post null";
                return false;
            }

            if (post.Deleted)
            {
                reason = "deleted";
                return false;
            }

            if (string.IsNullOrWhiteSpace(post.AuthorNickname))
            {
                reason = "authorNickname missing";
                return false;
            }

            if (!HasPayload(post))
            {
                reason = "missing text/attachments/repost";
                return false;
            }

            if (!AttachmentsAreValid(post.Attachments, out reason))
                return false;

            reason = "";
            return true;
        }

        public static bool IsRenderSafe(HomePostContractV2 post, out string reason)
        {
            if (post == null)
            {
                reason = "post null";
                return false;
            }

            if (post.Deleted)
            {
                reason = "deleted";
                return false;
            }

            if (post.SchemaVersion != SchemaVersion)
            {
                reason = "schemaVersion mismatch";
                return false;
            }

            if (!post.Ready)
            {
                reason = "ready=false";
                return false;
            }

            if (string.IsNullOrWhiteSpace(post.AuthorNickname))
            {
                reason = "authorNickname missing";
                return false;
            }

            if (!HasPayload(post))
            {
                reason = "missing text/attachments/repost";
                return false;
            }

            if (!AttachmentsAreValid(post.Attachments, out reason))
                return false;

            reason = "";
            return true;
        }

        public static bool IsCacheSafe(HomePostContractV2 post, out string reason)
        {
            return IsRenderSafe(post, out reason);
        }

        private static bool HasPayload(HomePostContractV2 post)
        {
            var hasText = !string.IsNullOrWhiteSpace(post.Text);
            var hasAttachments = post.Attachments != null && post.Attachments.Count > 0;
            var hasRepost = !string.IsNullOrWhiteSpace(post.RepostOfPostId);
            return hasText || hasAttachments || hasRepost;
        }

        private static bool AttachmentsAreValid(IReadOnlyList<HomeAttachmentContractV2>? attachments, out string reason)
        {
            if (attachments == null || attachments.Count == 0)
            {
                reason = "";
                return true;
            }

            foreach (var att in attachments)
            {
                if (att == null)
                {
                    reason = "attachment null";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(att.Type))
                {
                    reason = "attachment type missing";
                    return false;
                }

                if (RequiresFullRemote(att) && string.IsNullOrWhiteSpace(att.FullStoragePath))
                {
                    reason = $"attachment full path missing ({att.Type})";
                    return false;
                }

                if (RequiresPreview(att) && string.IsNullOrWhiteSpace(att.PreviewStoragePath))
                {
                    reason = $"attachment preview path missing ({att.Type})";
                    return false;
                }
            }

            reason = "";
            return true;
        }

        private static bool RequiresFullRemote(HomeAttachmentContractV2 att)
        {
            return att.Type is "image" or "video" or "audio" or "file";
        }

        private static bool RequiresPreview(HomeAttachmentContractV2 att)
        {
            if (att.Type is "image" or "video")
                return true;

            if (att.Type == "file" && string.Equals(att.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
