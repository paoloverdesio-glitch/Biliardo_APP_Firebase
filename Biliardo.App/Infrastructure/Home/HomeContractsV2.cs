using System;
using System.Collections.Generic;

namespace Biliardo.App.Infrastructure.Home
{
    public sealed record HomeAttachmentContractV2(
        string Type,
        string? FileName,
        string? ContentType,
        long SizeBytes,
        long DurationMs,
        Dictionary<string, object>? Extra,
        string? PreviewStoragePath,
        string? FullStoragePath,
        string? DownloadUrl,
        string? PreviewLocalPath,
        string? FullLocalPath,
        string? LqipBase64,
        string? PreviewType,
        int? PreviewWidth,
        int? PreviewHeight,
        IReadOnlyList<int>? Waveform)
    {
        public string? GetPreviewRemotePath() => PreviewStoragePath;

        public bool RequiresPreview => HomeAttachmentPreviewRules.RequiresPreview(Type, ContentType, FileName);
    }

    public sealed record HomePostContractV2(
        string PostId,
        DateTimeOffset CreatedAtUtc,
        string AuthorUid,
        string AuthorNickname,
        string? AuthorFirstName,
        string? AuthorLastName,
        string? AuthorAvatarPath,
        string? AuthorAvatarUrl,
        string Text,
        IReadOnlyList<HomeAttachmentContractV2> Attachments,
        bool Deleted,
        DateTimeOffset? DeletedAtUtc,
        string? RepostOfPostId,
        string? ClientNonce,
        int LikeCount,
        int CommentCount,
        int ShareCount,
        int SchemaVersion,
        bool Ready);

    public sealed record HomeCommentContractV2(
        string CommentId,
        string AuthorUid,
        string AuthorNickname,
        string? AuthorAvatarPath,
        string? AuthorAvatarUrl,
        DateTimeOffset CreatedAtUtc,
        string Text,
        IReadOnlyList<HomeAttachmentContractV2> Attachments,
        int SchemaVersion,
        bool Ready);
}
