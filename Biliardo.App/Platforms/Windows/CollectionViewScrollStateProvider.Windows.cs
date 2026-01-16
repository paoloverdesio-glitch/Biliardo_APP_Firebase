using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Biliardo.App.Componenti_UI
{
    public sealed partial class CollectionViewScrollStateProvider
    {
        private ScrollViewer? _scrollViewer;

        partial void AttachPlatform(object? platformView)
        {
            if (_disposed)
                return;

            var root = platformView as DependencyObject;
            var viewer = FindScrollViewer(root);
            if (viewer == null)
                return;

            _scrollViewer = viewer;
            _scrollViewer.ViewChanged += OnViewChanged;
            _scrollViewer.DirectManipulationStarted += OnDirectManipulationStarted;
            _scrollViewer.DirectManipulationCompleted += OnDirectManipulationCompleted;
        }

        partial void DetachPlatform()
        {
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnViewChanged;
                _scrollViewer.DirectManipulationStarted -= OnDirectManipulationStarted;
                _scrollViewer.DirectManipulationCompleted -= OnDirectManipulationCompleted;
            }

            _scrollViewer = null;
        }

        private void OnDirectManipulationStarted(object sender, object e)
        {
            UpdateScrollState("DRAGGING", true);
        }

        private void OnDirectManipulationCompleted(object sender, object e)
        {
            UpdateScrollState("IDLE", false);
        }

        private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate)
                UpdateScrollState("SETTLING", true);
            else
                UpdateScrollState("IDLE", false);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject? root)
        {
            if (root == null)
                return null;

            if (root is ScrollViewer viewer)
                return viewer;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var found = FindScrollViewer(child);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
