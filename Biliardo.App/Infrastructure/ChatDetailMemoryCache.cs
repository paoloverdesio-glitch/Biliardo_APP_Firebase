using System;
using System.Collections.Generic;
using System.Linq;
using Biliardo.App.Servizi_Firebase;

namespace Biliardo.App.Infrastructure
{
    public sealed class ChatDetailMemoryCache
    {
        public static ChatDetailMemoryCache Instance { get; } = new();

        private readonly object _lock = new();
        private string _cacheKey = "";
        private List<FirestoreChatService.MessageItem> _messages = new();
        private DateTimeOffset _lastUpdatedUtc;

        public bool TryGet(string cacheKey, out IReadOnlyList<FirestoreChatService.MessageItem> messages)
        {
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                messages = Array.Empty<FirestoreChatService.MessageItem>();
                return false;
            }

            lock (_lock)
            {
                if (!string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal) || _messages.Count == 0)
                {
                    messages = Array.Empty<FirestoreChatService.MessageItem>();
                    return false;
                }

                messages = _messages.ToList();
                return true;
            }
        }

        public void Set(string cacheKey, IReadOnlyList<FirestoreChatService.MessageItem> messages)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || messages == null)
                return;

            lock (_lock)
            {
                _cacheKey = cacheKey;
                _messages = messages.ToList();
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
