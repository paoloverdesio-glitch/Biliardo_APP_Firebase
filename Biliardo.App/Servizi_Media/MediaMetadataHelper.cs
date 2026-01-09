using System;

namespace Biliardo.App.Servizi_Media
{
    public static class MediaMetadataHelper
    {
        public static long TryGetDurationMs(string filePath)
        {
#if ANDROID
            try
            {
                using var mmr = new Android.Media.MediaMetadataRetriever();
                mmr.SetDataSource(filePath);
                var s = mmr.ExtractMetadata(Android.Media.MetadataKey.Duration);
                if (long.TryParse(s, out var ms) && ms > 0) return ms;
            }
            catch { }
#endif
            return 0;
        }

        public static string GuessContentType(string fileNameOrPath)
        {
            var x = (fileNameOrPath ?? "").Trim().ToLowerInvariant();

            if (x.EndsWith(".jpg") || x.EndsWith(".jpeg")) return "image/jpeg";
            if (x.EndsWith(".png")) return "image/png";
            if (x.EndsWith(".webp")) return "image/webp";

            if (x.EndsWith(".mp4")) return "video/mp4";
            if (x.EndsWith(".mov")) return "video/quicktime";

            if (x.EndsWith(".m4a")) return "audio/mp4";
            if (x.EndsWith(".aac")) return "audio/aac";
            if (x.EndsWith(".mp3")) return "audio/mpeg";
            if (x.EndsWith(".wav")) return "audio/wav";

            if (x.EndsWith(".pdf")) return "application/pdf";
            if (x.EndsWith(".txt")) return "text/plain";

            return "application/octet-stream";
        }
    }
}
