// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.Maui.Platform;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Completes the GTK preview's shared grid/header and managed empty-state presentations.
    /// </summary>
    public sealed class LinuxCollectionViewHandler : CollectionViewHandler
    {
        // Fields

        /// <summary>
        /// MAUI host that renders the empty view or template, or null while none is shown.
        /// </summary>
        private ContentView? _emptyHost;

        /// <summary>
        /// Empty view object the current host was built from, used to detect when it must be rebuilt.
        /// </summary>
        private object? _emptySource;

        /// <summary>
        /// Empty view template the current host was built from, used to detect when it must be rebuilt.
        /// </summary>
        private DataTemplate? _emptyTemplate;

        /// <summary>
        /// Native widget of <see cref="_emptyHost"/> that is placed into the scrolled window.
        /// </summary>
        private Gtk.Widget? _emptyWidget;

        /// <summary>
        /// Items source currently observed for live collection changes, or null when it is not observable.
        /// </summary>
        private INotifyCollectionChanged? _observedItems;

        /// <summary>
        /// Grid layout instance currently observed for span and spacing changes.
        /// </summary>
        private GridItemsLayout? _observedLayout;

        /// <summary>
        /// Virtualized grid presentation, or null while the backend's own list renders the collection.
        /// </summary>
        private LinuxCollectionGrid? _grid;

        /// <summary>
        /// Whether a presentation refresh is already queued on the GTK main loop.
        /// </summary>
        private bool _refreshPending;

        /// <summary>
        /// Incremented on disconnect so a refresh queued earlier can be recognized and dropped.
        /// </summary>
        private int _generation;


        // Mapping

        /// <summary>
        /// Reapplies shared presentations after backend updates that can replace the scroller's child.
        /// </summary>
        static LinuxCollectionViewHandler()
        {
            // Wrap every property mapping that can change what the scroller shows: the backend runs first (unless
            // the grid presentation owns the content), then the shared presentation is restored from the main loop.
            foreach (var property in new[] { "ItemsSource", "EmptyView", "EmptyViewTemplate", "ItemTemplate", "IsGrouped",
                "GroupHeaderTemplate", "ItemsLayout", "Header", "HeaderTemplate", "Footer", "FooterTemplate", "SelectionMode" })
                Mapper.ModifyMapping(property, static (handler, view, original) =>
                {
                    if (handler is LinuxCollectionViewHandler linux)
                    {
                        // Do not inflate a second, hidden linear list for collections rendered as grid rows.
                        if (!linux.UsesGridPresentation()) original?.Invoke(handler, view);
                        linux.ObserveItems();
                        linux.QueueRefresh();
                    }
                    else original?.Invoke(handler, view);
                });
        }

        /// <summary>
        /// Identifies the shared non-selecting vertical grids whose column layout the preview backend does not implement.
        /// </summary>
        private bool UsesGridPresentation() => VirtualView is CollectionView
        {
            ItemsLayout: GridItemsLayout { Orientation: ItemsLayoutOrientation.Vertical },
            IsGrouped: false,
            SelectionMode: SelectionMode.None
        };


        // Lifecycle

        /// <summary>
        /// Restores the presentation after every initial mapping has run, not only after a live collection change.
        /// </summary>
        protected override void ConnectHandler(Gtk.ScrolledWindow platformView)
        {
            base.ConnectHandler(platformView);
            ObserveItems();
            QueueRefresh();
        }


        // Change observation

        /// <summary>
        /// Observes live list changes that the backend processes directly without invoking its property mapper again.
        /// </summary>
        private void ObserveItems()
        {
            var items = (VirtualView as CollectionView)?.ItemsSource as INotifyCollectionChanged;
            if (ReferenceEquals(items, _observedItems)) return;

            // Move the subscription from the previous source to the new one.
            if (_observedItems != null) _observedItems.CollectionChanged -= OnItemsChanged;
            _observedItems = items;
            if (_observedItems != null) _observedItems.CollectionChanged += OnItemsChanged;
        }

        /// <summary>
        /// Tracks span and spacing changes on the existing layout instance, including responsive column bindings.
        /// </summary>
        private void ObserveLayout()
        {
            var layout = (VirtualView as CollectionView)?.ItemsLayout as GridItemsLayout;
            if (ReferenceEquals(layout, _observedLayout)) return;

            // Move the subscription from the previous layout to the new one.
            if (_observedLayout != null) _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = layout;
            if (_observedLayout != null) _observedLayout.PropertyChanged += OnLayoutChanged;
        }

        /// <summary>
        /// Regroups cards after the page recalculates its column count or grid spacing.
        /// </summary>
        private void OnLayoutChanged(object? sender, PropertyChangedEventArgs args) => QueueRefresh();

        /// <summary>
        /// Restores the managed empty view after the backend finishes changing its native list model.
        /// </summary>
        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueRefresh();


        // Presentation

        /// <summary>
        /// Coalesces refreshes after backend mappings that call MapItemsSource directly and bypass its mapper extension.
        /// </summary>
        private void QueueRefresh()
        {
            if (_refreshPending) return;
            _refreshPending = true;

            // Run on an idle callback so several changes in one pass cause a single refresh; a callback
            // that outlives its connection (generation changed) does nothing.
            var generation = _generation;
            GLib.Functions.IdleAdd(0, () =>
            {
                if (generation != _generation) return false;
                _refreshPending = false;
                ApplyPresentation();
                return false;
            });
        }

        /// <summary>
        /// Keeps flat, non-selecting grids virtualized while leaving ordinary lists and selection to the backend.
        /// </summary>
        private void ApplyPresentation()
        {
            if (VirtualView is not CollectionView collection || MauiContext == null) return;
            ObserveLayout();

            // Grid layouts: show the virtualized grid in place of the backend's linear list.
            if (_observedLayout is { } layout && UsesGridPresentation())
            {
                ReleaseEmptyView();
                _grid ??= new LinuxCollectionGrid(collection, MauiContext);
                _grid.Refresh(layout);
                PlatformView.SetChild(_grid.Widget);
                return;
            }

            // Any other layout: leave the grid and let the backend render the collection again.
            if (_grid != null)
            {
                _grid.Dispose();
                _grid = null;

                // Restore the backend list when a collection changes its layout or selection contract.
                MapItemsSource(this, collection);
            }

            ApplyEmptyView();
        }


        // Empty view

        /// <summary>
        /// Inflates a shared view or data template once and lets the normal MAUI handlers render its contents.
        /// </summary>
        private void ApplyEmptyView()
        {
            if (VirtualView is not CollectionView collection || MauiContext == null) return;

            // Without a configured empty state, release any previously shown one.
            if (collection.EmptyView == null && collection.EmptyViewTemplate == null)
            {
                ReleaseEmptyView();
                return;
            }

            // The empty state is only shown for a collection without items.
            if (HasItems(collection.ItemsSource)) return;

            // Build the empty content once; it is rebuilt only when the configured view or template changed.
            if (_emptyHost == null || !ReferenceEquals(_emptySource, collection.EmptyView) || _emptyTemplate != collection.EmptyViewTemplate)
            {
                ReleaseEmptyView();
                _emptySource = collection.EmptyView;
                _emptyTemplate = collection.EmptyViewTemplate;
                var content = _emptyTemplate?.CreateContent() as View ?? _emptySource as View
                    ?? new Label { Text = _emptySource?.ToString(), HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
                if (_emptyTemplate != null) content.BindingContext = _emptySource;
                _emptyHost = new ContentView { Content = content };
                collection.AddLogicalChild(_emptyHost);
                _emptyWidget = (Gtk.Widget)_emptyHost.ToPlatform(MauiContext);
                _emptyWidget.SetHexpand(true);
                _emptyWidget.SetVexpand(true);

                // The collection supplies the empty state's viewport during PlatformArrange, just as it does for rows.
            }

            // GtkScrolledWindow inserts a viewport around managed content. Detach from a previous viewport
            // before restoring a cached empty view; a GTK widget cannot belong to two parents.
            var parent = _emptyWidget!.GetParent();
            if (PlatformView.GetChild() != _emptyWidget && (parent is not Gtk.Viewport viewport || viewport.GetParent() != PlatformView))
            {
                if (parent is Gtk.Viewport previous) previous.SetChild(null);
                PlatformView.SetChild(_emptyWidget);
            }

            // Size the empty content to the current frame and ask MAUI to measure the collection again.
            ArrangeEmptyView(((IView)collection).Frame.Size);
            ((IView)collection).InvalidateMeasure();
        }

        /// <summary>
        /// Checks only the first item rather than materializing a potentially large or lazy collection.
        /// </summary>
        private static bool HasItems(IEnumerable? source)
        {
            if (source == null) return false;
            var enumerator = source.GetEnumerator();
            try { return enumerator.MoveNext(); }
            finally { (enumerator as IDisposable)?.Dispose(); }
        }

        /// <summary>
        /// Keeps the empty presentation measured against the allocated collection viewport after a resize.
        /// </summary>
        public override void PlatformArrange(Rect rect)
        {
            base.PlatformArrange(rect);
            ArrangeEmptyView(rect.Size);
        }

        /// <summary>
        /// Arranges managed empty-state content so alignment, wrapping and localized labels keep working.
        /// </summary>
        private void ArrangeEmptyView(Size size)
        {
            if (_emptyHost == null || _emptyWidget?.GetParent() == null || size.Width <= 0 || size.Height <= 0) return;
            ((IView)_emptyHost).Measure(size.Width, size.Height);
            ((IView)_emptyHost).Arrange(new Rect(0, 0, size.Width, size.Height));
        }

        /// <summary>
        /// Releases the logical child and its native handlers when the empty template or owning handler changes.
        /// </summary>
        private void ReleaseEmptyView()
        {
            if (_emptyHost == null) return;

            // Detach the native widget, either from the scrolled window or from a viewport it was wrapped in.
            var parent = _emptyWidget?.GetParent();
            if (PlatformView.GetChild() == _emptyWidget || parent is Gtk.Viewport viewport && viewport.GetParent() == PlatformView)
                PlatformView.SetChild(null);
            else if (parent is Gtk.Viewport detached) detached.SetChild(null);

            // Then release the MAUI side and forget the cached empty state.
            if (VirtualView is CollectionView collection) collection.RemoveLogicalChild(_emptyHost);
            _emptyHost.DisconnectHandlers();
            _emptyHost = null;
            _emptyWidget = null;
            _emptySource = null;
            _emptyTemplate = null;
        }


        // Disconnection

        /// <summary>
        /// Tears down the managed empty presentation alongside the backend's realized list items.
        /// </summary>
        protected override void DisconnectHandler(Gtk.ScrolledWindow platformView)
        {
            // Invalidate a queued refresh so it cannot touch the released widgets.
            _generation++;
            _refreshPending = false;

            // Stop observing the items source and layout.
            if (_observedItems != null) _observedItems.CollectionChanged -= OnItemsChanged;
            _observedItems = null;
            if (_observedLayout != null) _observedLayout.PropertyChanged -= OnLayoutChanged;
            _observedLayout = null;

            // Release the grid and empty view before the base handler tears down the backend's list.
            _grid?.Dispose();
            _grid = null;
            ReleaseEmptyView();
            base.DisconnectHandler(platformView);
        }
    }
}
