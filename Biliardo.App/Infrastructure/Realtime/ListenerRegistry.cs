using System;
using System.Collections.Generic;

namespace Biliardo.App.Infrastructure.Realtime
{
    public sealed class ListenerRegistry : IDisposable
    {
        private readonly List<IDisposable> _listeners = new();

        public void Add(IDisposable? listener)
        {
            if (listener == null)
                return;

            _listeners.Add(listener);
        }

        public void Clear()
        {
            foreach (var listener in _listeners)
            {
                try { listener.Dispose(); } catch { }
            }

            _listeners.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
