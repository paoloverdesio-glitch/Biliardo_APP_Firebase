using System;
using System.Collections.Generic;

namespace Biliardo.App.Realtime
{
    public sealed class BusEventiRealtime
    {
        private readonly object _lock = new();

        public static BusEventiRealtime Instance { get; } = new();

        private BusEventiRealtime() { }

        public event EventHandler<RealtimeEventPayload>? NewChatMessageNotification;
        public event EventHandler<RealtimeEventPayload>? NewHomePostNotification;

        public void PublishChatMessage(IReadOnlyDictionary<string, string> data)
        {
            PublishEvent(NewChatMessageNotification, data);
        }

        public void PublishHomePost(IReadOnlyDictionary<string, string> data)
        {
            PublishEvent(NewHomePostNotification, data);
        }

        private void PublishEvent(EventHandler<RealtimeEventPayload>? handler, IReadOnlyDictionary<string, string> data)
        {
            if (handler == null)
                return;

            var payload = new RealtimeEventPayload(data ?? new Dictionary<string, string>(), DateTimeOffset.UtcNow);

            EventHandler<RealtimeEventPayload>? snapshot;
            lock (_lock)
            {
                snapshot = handler;
            }

            snapshot?.Invoke(this, payload);
        }
    }

    public sealed record RealtimeEventPayload(IReadOnlyDictionary<string, string> Data, DateTimeOffset ReceivedAtUtc);
}
