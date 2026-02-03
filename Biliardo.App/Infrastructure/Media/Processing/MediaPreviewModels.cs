using System;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Infrastructure.Media.Processing
{
    public enum MediaKind
    {
        Image,
        Video,
        Audio,
        File,
        Pdf,
        Location,
        Contact,
        Unknown
    }

    public sealed record MediaPreviewRequest(
        string LocalFilePath,
        MediaKind Kind,
        string? ContentType,
        string? FileName,
        string SourceContext,
        double? Latitude,
        double? Longitude);

    public sealed record MediaPreviewResult(
        string ThumbLocalPath,
        string? LqipBase64,
        int Width,
        int Height,
        string PreviewType);

    public interface IMediaPreviewGenerator
    {
        Task<MediaPreviewResult?> GenerateAsync(MediaPreviewRequest req, CancellationToken ct);
    }
}
