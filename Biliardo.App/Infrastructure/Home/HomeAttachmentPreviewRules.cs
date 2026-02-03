using System;
using System.IO;

namespace Biliardo.App.Infrastructure.Home
{
    public static class HomeAttachmentPreviewRules
    {
        public static bool RequiresPreview(string type, string? contentType, string? fileName)
        {
            if (string.Equals(type, "image", StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(type, "video", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                    return true;

                var ext = Path.GetExtension(fileName ?? string.Empty);
                if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
