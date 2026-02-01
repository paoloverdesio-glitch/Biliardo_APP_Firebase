namespace Biliardo.App.Infrastructure.Media
{
    public static class AppMediaOptions
    {
        // =====================================================================
        // LIMITI UPLOAD (byte)
        // =====================================================================
        public const long MaxImageBytes = 10 * 1024 * 1024;
        public const long MaxVideoBytes = 100 * 1024 * 1024;
        public const long MaxAudioBytes = 20 * 1024 * 1024;
        public const long MaxDocumentBytes = 25 * 1024 * 1024;
        public const long MaxVideoDurationMs = 180_000; // 3 minuti (sensato)

        // =====================================================================
        // COMPRESSIONE (switch)
        // =====================================================================
        public const bool CompressImagesBeforeUpload = true;
        public const bool CompressVideosBeforeUpload = false; // predisposizione, non implementare compressione video completa ora
        public const int ImageUploadMaxLongSidePx = 1920;
        public const int ImageUploadJpegQuality = 85;

        // =====================================================================
        // THUMB / LQIP
        // =====================================================================
        public const int ThumbMaxLongSidePx = 1920;
        public const int ThumbJpegQuality = 100;
        public const int LqipMaxLongSidePx = 32;
        public const int LqipJpegQuality = 35;
        public const bool StoreLqipInFirestore = false;
        public const bool StoreThumbInStorage = true;
        public const int MaxLqipBase64Bytes = 10_000; // se supera, non salvare lqip (evita Firestore payload grossi)

        // =====================================================================
        // CACHE DISCO
        // =====================================================================
        public const long CacheMaxBytes = 1_073_741_824; // 1GB
        public const int DownloadConcurrency = 2;
        public const bool PrefetchThumbsOnScroll = true;
        public const bool DownloadOriginalOnScroll = true;

        // =====================================================================
        // AUTOPLAY VIDEO (per pagina)
        // =====================================================================
        public const bool HomeVideoAutoplay = true;
        public const bool ChatVideoAutoplay = false;
        public const int MaxSimultaneousPlayingVideosInList = 1;

        // =====================================================================
        // FALLBACK/COMPAT
        // =====================================================================
        public const bool GenerateThumbIfMissingAfterDownload = true;
        public const bool GenerateLqipIfMissingAfterDownload = false;
        public const bool TryUpdateFirestoreWithGeneratedThumbInfo = false; // IMPORTANT: non aggiornare payload dopo create (rules + semplicità)

        // =====================================================================
        // AUDIO: waveform
        // =====================================================================
        public const int AudioWaveformMaxSamples = 60;
        public const int AudioWaveformSampleIntervalMs = 100;

        // =====================================================================
        // SCROLL/SMOOTHNESS (tuning UX)
        // =====================================================================
        public const int ScrollBusyHoldMs = 450;
        public const int ScrollIdleDelayMs = 320;
        public const double ScrollAccelerationFactor = 1.0;
        public const double ScrollDecelerationFactor = 1.0;
    }
}
