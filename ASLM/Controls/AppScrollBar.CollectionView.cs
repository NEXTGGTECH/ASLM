// Copyright NEXTGGTECH. Apache License 2.0.

#if WINDOWS
using WinList = Microsoft.UI.Xaml.Controls.ListViewBase;
using WinScrollViewer = Microsoft.UI.Xaml.Controls.ScrollViewer;
#elif MACCATALYST
using CoreGraphics;
using Foundation;
using UIKit;
#endif

namespace ASLM.Controls
{
    /// <summary>
    /// Reads and controls the native viewport of a virtualized MAUI collection.
    /// </summary>
    public sealed partial class AppScrollBar
    {
        // Collection connection

        /// <summary>
        /// Reads the current native viewport, extent and offset while a list is connected.
        /// </summary>
        private Func<(double Viewport, double Content, double Offset)>? _readCollectionMetrics;

        /// <summary>
        /// Moves the connected list by an exact vertical offset while dragging the thumb.
        /// </summary>
        private Action<double>? _scrollCollectionTo;

        /// <summary>
        /// Releases the native observers belonging to the current list.
        /// </summary>
        private Action? _releaseCollectionObservers;

        /// <summary>
        /// Clears the native list connection before changing targets or leaving the visual tree.
        /// </summary>
        private void DisconnectCollectionScroll()
        {
            _readCollectionMetrics = null;
            _scrollCollectionTo = null;
            _releaseCollectionObservers?.Invoke();
            _releaseCollectionObservers = null;
        }

        /// <summary>
        /// Connects the shared bar to the collection's native viewport without changing item realization or selection.
        /// </summary>
        private void ConnectCollectionScroll(CollectionView collection)
        {
#if WINDOWS
            if (collection.Handler?.PlatformView is not WinList list) return;

            // The ScrollViewer lives inside the list template and may only exist after native layout.
            void AttachViewer(object? sender, object? args)
            {
                if (ConsoleOutputViewHandler.FindDescendantScrollViewer(list) is not { } viewer) return;
                list.LayoutUpdated -= AttachViewer;

                // Observe scrolling and metric changes, including item insertion while the offset stays the same.
                void OnViewChanged(object? sender, Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs args) =>
                    QueueRefresh();

                viewer.ViewChanged += OnViewChanged;
                var viewportToken = viewer.RegisterPropertyChangedCallback(WinScrollViewer.ViewportHeightProperty,
                    (_, _) => QueueRefresh());
                var extentToken = viewer.RegisterPropertyChangedCallback(WinScrollViewer.ExtentHeightProperty,
                    (_, _) => QueueRefresh());
                _readCollectionMetrics = () => (viewer.ViewportHeight, viewer.ExtentHeight, viewer.VerticalOffset);
                _scrollCollectionTo = offset => viewer.ChangeView(null, offset, null, disableAnimation: true);
                _releaseCollectionObservers = () =>
                {
                    viewer.ViewChanged -= OnViewChanged;
                    viewer.UnregisterPropertyChangedCallback(WinScrollViewer.ViewportHeightProperty, viewportToken);
                    viewer.UnregisterPropertyChangedCallback(WinScrollViewer.ExtentHeightProperty, extentToken);
                };
                Refresh();
            }

            list.LayoutUpdated += AttachViewer;
            _releaseCollectionObservers = () => list.LayoutUpdated -= AttachViewer;
            AttachViewer(null, null);
#elif MACCATALYST
            if (collection.Handler?.PlatformView is not UIView host || FindCollectionView(host) is not { } viewer) return;

            // Observe the native values without taking over MAUI's collection delegate.
            _readCollectionMetrics = () =>
            {
                var insets = viewer.AdjustedContentInset;
                return (viewer.Bounds.Height, viewer.ContentSize.Height + insets.Top + insets.Bottom,
                    viewer.ContentOffset.Y + insets.Top);
            };
            _scrollCollectionTo = offset => viewer.SetContentOffset(
                new CGPoint(viewer.ContentOffset.X, offset - viewer.AdjustedContentInset.Top), animated: false);
            var sizeObserver = viewer.AddObserver("contentSize", NSKeyValueObservingOptions.New, _ => QueueRefresh());
            var offsetObserver = viewer.AddObserver("contentOffset", NSKeyValueObservingOptions.New, _ => QueueRefresh());
            _releaseCollectionObservers = () =>
            {
                sizeObserver.Dispose();
                offsetObserver.Dispose();
            };
#elif LINUX
            if (collection.Handler?.PlatformView is not Gtk.ScrolledWindow viewer ||
                viewer.GetVadjustment() is not { } adjustment) return;

            // An external vertical scrollbar leaves GTK's viewport scrollable and hides its native bar.
            viewer.VscrollbarPolicy = Gtk.PolicyType.External;
            _readCollectionMetrics = () => (adjustment.GetPageSize(), adjustment.GetUpper(), adjustment.GetValue());
            _scrollCollectionTo = adjustment.SetValue;
            adjustment.OnChanged += OnGeometryChanged;
            adjustment.OnValueChanged += OnGeometryChanged;
            _releaseCollectionObservers = () =>
            {
                adjustment.OnChanged -= OnGeometryChanged;
                adjustment.OnValueChanged -= OnGeometryChanged;
            };
#endif
        }

#if MACCATALYST
        /// <summary>
        /// Finds the list inside a native collection handler's host view.
        /// </summary>
        private static UICollectionView? FindCollectionView(UIView root)
        {
            if (root is UICollectionView collection) return collection;
            foreach (var child in root.Subviews)
            {
                if (FindCollectionView(child) is { } found) return found;
            }

            return null;
        }
#endif
    }
}
