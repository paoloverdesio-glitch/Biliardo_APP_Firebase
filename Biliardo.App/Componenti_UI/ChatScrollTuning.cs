using System;
using Microsoft.Maui.Controls;

namespace Biliardo.App.Componenti_UI
{
    public static partial class ChatScrollTuning
    {
        public static void Apply(CollectionView view)
        {
            if (view == null)
                return;

            view.HandlerChanged -= OnHandlerChanged;
            view.HandlerChanged += OnHandlerChanged;

            if (view.Handler != null)
                ApplyPlatform(view);
        }

        private static void OnHandlerChanged(object? sender, EventArgs e)
        {
            if (sender is CollectionView view)
                ApplyPlatform(view);
        }

        static partial void ApplyPlatform(CollectionView view);
    }
}
