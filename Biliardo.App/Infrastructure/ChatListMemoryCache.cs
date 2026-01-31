using System;
using System.Collections.Generic;
using System.Linq;
using Biliardo.App.Pagine_Messaggi;

namespace Biliardo.App.Infrastructure
{
    public sealed class ChatListMemoryCache
    {
        public static ChatListMemoryCache Instance { get; } = new();

        private readonly object _lock = new();
        private List<ChatPreview> _items = new();
        private DateTimeOffset _lastUpdatedUtc;

        public bool TryGet(out IReadOnlyList<ChatPreview> items)
        {
            lock (_lock)
            {
                if (_items.Count == 0)
                {
                    items = Array.Empty<ChatPreview>();
                    return false;
                }

                items = _items.ToList();
                return true;
            }
        }

        public void Set(IReadOnlyList<ChatPreview> items)
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
