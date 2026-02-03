using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure
{
    public static class AppReleaseInfo
    {
        public const string Version = "1.0";
        public const string ReleaseChannel = "stable";
        public const string StorageBucket = FirebaseStorageRestClient.DefaultStorageBucket;
        public const string SupportContact = "support@biliardoapp.local";
    }
}
