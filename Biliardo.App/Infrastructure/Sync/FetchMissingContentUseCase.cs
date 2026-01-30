using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Biliardo.App.Infrastructure.Sync
{
    public sealed class FetchMissingContentUseCase
    {
        private readonly MissingContentQueueStore _store = new();

        public Task EnqueueAsync(string contentId, string kind, IReadOnlyDictionary<string, string> payload, int priority, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(contentId) || string.IsNullOrWhiteSpace(kind) || payload == null)
                return Task.CompletedTask;

            var item = new MissingContentQueueStore.MissingContentItem(
                contentId,
                kind,
                MissingContentQueueStore.SerializePayload(payload),
                priority,
                DateTimeOffset.UtcNow,
                retryCount: 0,
                lastAttemptUtc: null);

            return _store.EnqueueAsync(item, ct);
        }
    }
}
