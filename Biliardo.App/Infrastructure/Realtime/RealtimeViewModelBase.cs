using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Biliardo.App.Infrastructure.Realtime
{
    public abstract class RealtimeViewModelBase : INotifyPropertyChanged, IDisposable
    {
        private readonly List<IDisposable> _listeners = new();

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected void RegisterListener(IDisposable? listener)
        {
            if (listener == null)
                return;

            _listeners.Add(listener);
        }

        public void ClearListeners()
        {
            foreach (var listener in _listeners)
            {
                try { listener.Dispose(); } catch { }
            }

            _listeners.Clear();
        }

        public virtual void Dispose()
        {
            ClearListeners();
        }
    }
}
