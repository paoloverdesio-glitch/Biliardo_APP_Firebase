using System;

namespace Biliardo.App.Componenti_UI
{
    public interface IScrollStateProvider
    {
        bool IsScrolling { get; }
        event EventHandler<bool>? ScrollingStateChanged;
    }
}
