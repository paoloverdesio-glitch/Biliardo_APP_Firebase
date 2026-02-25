using System;

namespace Biliardo.App.Infrastructure
{
    public sealed partial class HomeFeedJankMonitor
    {
        public void Start() => StartPlatform();

        public void Stop() => StopPlatform();

        partial void StartPlatform();
        partial void StopPlatform();
    }
}
