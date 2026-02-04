using System;
using System.Collections.Generic;
using System.Linq;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure
{
    public sealed class HomeFeedMemoryCache
    {
        public static HomeFeedMemoryCache Instance { get; } = new();

        private readonly object _lock = new();
        private List<FirestoreHomeFeedService.HomePostItem> _items = new();
        private DateTimeOffset _lastUpdatedUtc;

        public bool TryGet(out IReadOnlyList<FirestoreHomeFeedService.HomePostItem> items)
        {
            lock (_lock)
            {
                if (_items.Count == 0)
                {
                    items = Array.Empty<FirestoreHomeFeedService.HomePostItem>();
                    return false;
                }

                items = _items.ToList();
                return true;
            }
        }

        public void Set(IReadOnlyList<FirestoreHomeFeedService.HomePostItem> items)
        {
            if (items == null)
                return;

            lock (_lock)
            {
                _items = items.ToList();
                _lastUpdatedUtc = DateTimeOffset.UtcNow;
            }
        }

        public DateTimeOffset LastUpdatedUtc
        {
            get
            {
                lock (_lock)
                    return _lastUpdatedUtc;
            }
        }
    }
}
