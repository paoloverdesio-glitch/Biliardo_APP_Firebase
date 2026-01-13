using System;
using Biliardo.App.Impostazioni;
using Microsoft.Maui.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Biliardo.App.Componenti_UI
{
    public static partial class ChatScrollTuning
    {
        static partial void ApplyPlatform(CollectionView view)
        {
            if (view.Handler?.PlatformView is not DependencyObject root)
                return;

            var scrollViewer = FindDescendant<ScrollViewer>(root);
            if (scrollViewer == null)
                return;

            scrollViewer.ManipulationMode |= ManipulationModes.TranslateY | ManipulationModes.TranslateRailsY;
            scrollViewer.ManipulationInertiaStarting -= OnManipulationInertiaStarting;
            scrollViewer.ManipulationInertiaStarting += OnManipulationInertiaStarting;
        }

        private static void OnManipulationInertiaStarting(object sender, ManipulationInertiaStartingRoutedEventArgs e)
        {
            try
            {
                var scale = ScrollTuning.GetWindowsInertiaScale();
                const double baseDeceleration = 0.0016;
                var desired = baseDeceleration / scale;
                desired = Math.Clamp(desired, 0.0002, 0.01);
                e.TranslationBehavior.DesiredDeceleration = desired;
            }
            catch { }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;

                var found = FindDescendant<T>(child);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
