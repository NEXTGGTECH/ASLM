// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;

namespace ASLM.Controls
{
    /// <summary>
    /// Displays a shared scrollbar for application scroll views and native consoles.
    /// </summary>
    public sealed partial class AppScrollBar : Grid
    {
        // Bindable properties

        /// <summary>
        /// Identifies the bindable property that holds the scroll view or console this scrollbar mirrors.
        /// Changing it reconnects the scrollbar to the new target.
        /// </summary>
        public static readonly BindableProperty TargetProperty = BindableProperty.Create(
            nameof(Target), typeof(VisualElement), typeof(AppScrollBar), null,
            propertyChanged: (bindable, _, _) => ((AppScrollBar)bindable).Reconnect());


        // Fields

        /// <summary>
        /// Draggable thumb whose height and position follow the target's scroll state.
        /// </summary>
        private readonly BoxView _thumb = new();

        /// <summary>
        /// Target whose events are currently subscribed, or null while disconnected.
        /// </summary>
        private VisualElement? _connected;

        /// <summary>
        /// Content of the connected scroll view whose size changes are observed, or null.
        /// </summary>
        private View? _content;

        /// <summary>
        /// Re-entrancy guard for <see cref="Refresh"/>, which triggers size notifications itself.
        /// </summary>
        private bool _updating;

        /// <summary>
        /// Thumb translation captured when the current drag gesture started.
        /// </summary>
        private double _dragStart;


        // Properties

        /// <summary>
        /// Scroll view or native console whose scroll position this scrollbar displays and controls.
        /// </summary>
        public VisualElement? Target
        {
            get => (VisualElement?)GetValue(TargetProperty);
            set => SetValue(TargetProperty, value);
        }


        // Initialization

        /// <summary>
        /// Creates the thumb and connects dragging, layout, and target-lifetime handlers.
        /// </summary>
        public AppScrollBar()
        {
            // The bar stays hidden and transparent until a target actually needs scrolling.
            WidthRequest = MinimumWidthRequest = 12;
            BackgroundColor = Colors.Transparent;
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Fill;
            IsVisible = false;

            // The thumb takes its look from the themed style; dragging it scrolls the target.
            _thumb.SetDynamicResource(StyleProperty, "AppScrollBarThumbStyle");
            Children.Add(_thumb);
            var pan = new PanGestureRecognizer();
            pan.PanUpdated += OnPanUpdated;
            GestureRecognizers.Add(pan);

            // Subscribe to the target only while the bar is part of the visual tree.
            Loaded += (_, _) => Reconnect();
            Unloaded += (_, _) => Disconnect();
            SizeChanged += (_, _) => Refresh();
        }


        // Target connection

        /// <summary>
        /// Observes the current target and replaces its native scrollbar with the shared thumb.
        /// </summary>
        private void Reconnect()
        {
            // Drop the previous target first, then subscribe to the geometry events every target type raises.
            Disconnect();
            _connected = Target;
            if (_connected == null) return;
            _connected.SizeChanged += OnGeometryChanged;
            _connected.PropertyChanged += OnTargetPropertyChanged;
            _connected.HandlerChanged += OnHandlerChanged;

            // Native consoles expose metrics directly; regular scroll views report content geometry.
            if (_connected is ScrollView scroll)
            {
                scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Never;
                scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Never;
                scroll.Scrolled += OnScrolled;
                ConnectContent(scroll.Content);
            }
            else if (_connected is ConsoleOutputView console)
            {
                console.UseCustomScrollBar = true;
                console.ScrollMetricsChanged += OnGeometryChanged;
            }

            ApplyNativeChrome();
            Refresh();
        }

        /// <summary>
        /// Removes target and content subscriptions before reconnection or unloading.
        /// </summary>
        private void Disconnect()
        {
            if (_connected != null)
            {
                _connected.SizeChanged -= OnGeometryChanged;
                _connected.PropertyChanged -= OnTargetPropertyChanged;
                _connected.HandlerChanged -= OnHandlerChanged;
            }

            // Undo the target-type specific subscriptions made in Reconnect, including the content size handler.
            if (_connected is ScrollView scroll) scroll.Scrolled -= OnScrolled;
            if (_connected is ConsoleOutputView console) console.ScrollMetricsChanged -= OnGeometryChanged;
            ConnectContent(null);
            _connected = null;
        }

        /// <summary>
        /// Tracks content-size changes when a scroll view replaces its content.
        /// </summary>
        private void ConnectContent(View? content)
        {
            if (_content != null) _content.SizeChanged -= OnGeometryChanged;
            _content = content;
            if (_content != null) _content.SizeChanged += OnGeometryChanged;
        }

        /// <summary>
        /// Refreshes target-specific subscriptions and chrome only for relevant property changes.
        /// </summary>
        private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_connected is ScrollView scroll && e.PropertyName == nameof(ScrollView.Content)) ConnectContent(scroll.Content);
            if (e.PropertyName == nameof(ScrollView.Orientation)) ApplyNativeChrome();
            if (e.PropertyName is nameof(ScrollView.ContentSize) or nameof(IsVisible) or nameof(ScrollView.Content)) Refresh();
        }

        /// <summary>
        /// Reapplies native chrome after MAUI creates or replaces the target handler.
        /// </summary>
        private void OnHandlerChanged(object? sender, EventArgs e) { ApplyNativeChrome(); Refresh(); }

        /// <summary>
        /// Recomputes the thumb when the viewport or its content changes size.
        /// </summary>
        private void OnGeometryChanged(object? sender, EventArgs e) => Refresh();

        /// <summary>
        /// Moves the thumb to follow a scroll initiated by the native view.
        /// </summary>
        private void OnScrolled(object? sender, ScrolledEventArgs e) => Refresh();

        /// <summary>
        /// Removes native scroll chrome while retaining the target's constrained scrolling orientation.
        /// </summary>
        private void ApplyNativeChrome()
        {
            ApplyPlatformChrome();
#if WINDOWS
            if (_connected is ScrollView { Handler.PlatformView: Microsoft.UI.Xaml.Controls.ScrollViewer viewer } scroll)
            {
                // Strip the WinUI scroll viewer's own background, border and padding.
                var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                viewer.Background = viewer.BorderBrush = transparent;
                viewer.BorderThickness = viewer.Padding = new Microsoft.UI.Xaml.Thickness(0);

                // Hidden permits unbounded content on that axis. Preserve MAUI's orientation mapping:
                // a vertical scroller must DISABLE horizontal scrolling so text wraps to the viewport.
                Microsoft.Maui.Platform.ScrollViewerExtensions.UpdateScrollBarVisibility(
                    viewer, ((IScrollView)scroll).Orientation, ScrollBarVisibility.Never);
            }
#endif
        }

        /// <summary>
        /// Applies optional platform chrome supplied by the active platform folder.
        /// </summary>
        partial void ApplyPlatformChrome();


        // Thumb geometry

        /// <summary>
        /// Viewport length, content length and scroll offset of the connected target, or zeros when disconnected.
        /// </summary>
        private (double Viewport, double Content, double Offset) Metrics => _connected switch
        {
            ScrollView scroll => (scroll.Height, Math.Max(scroll.ContentSize.Height, scroll.Content?.Height ?? 0), scroll.ScrollY),
            ConsoleOutputView console => (console.ScrollViewport, console.ScrollExtent, console.ScrollOffset),
            _ => default
        };

        /// <summary>
        /// Computes a bounded thumb height and position, returning no thumb when content fits.
        /// The thumb is at least 24 units tall and travels along <c>track</c> (or the viewport when no track is given).
        /// </summary>
        internal static (double Height, double Top) CalculateThumb(double viewport, double content, double offset, double track = 0)
        {
            if (viewport <= 0 || content <= viewport + 1) return default;
            var length = track > 0 ? track : viewport;
            var height = Math.Min(length, Math.Max(24, length * viewport / content));
            return (height, Math.Clamp(offset / (content - viewport) * (length - height), 0, length - height));
        }

        /// <summary>
        /// Updates visible thumb geometry without reentering on the resulting size notifications.
        /// </summary>
        internal void Refresh()
        {
            if (_updating) return;
            _updating = true;
            var changed = false;
            try
            {
                // Hide the bar when the target is hidden or its content fits; otherwise size and place the thumb.
                var metrics = Metrics;
                var (height, top) = CalculateThumb(metrics.Viewport, metrics.Content, metrics.Offset, Height);
                var visible = height > 0 && _connected?.IsVisible == true;
                changed = IsVisible != visible;
                IsVisible = visible;
                if (visible) _thumb.HeightRequest = height;
                _thumb.TranslationY = top;
            }
            finally { _updating = false; }

            // Visibility affects the available track size; use the next layout pass to settle it.
            if (changed) Dispatcher.Dispatch(Refresh);
        }


        // Dragging

        /// <summary>
        /// Converts a dragged thumb position into the target's scroll offset.
        /// </summary>
        private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            if (e.StatusType == GestureStatus.Started) _dragStart = _thumb.TranslationY;
            else if (e.StatusType == GestureStatus.Running)
            {
                var metrics = Metrics;
                var travel = Math.Max(0, Height - _thumb.Height);
                var top = Math.Clamp(_dragStart + e.TotalY, 0, travel);

                // Normalize against thumb travel, not content height, so both endpoints remain reachable.
                var offset = travel > 0 ? top / travel * Math.Max(0, metrics.Content - metrics.Viewport) : 0;
                _thumb.TranslationY = top;
                if (_connected is ScrollView scroll) _ = scroll.ScrollToAsync(0, offset, false);
                else if (_connected is ConsoleOutputView console) console.ScrollToOffset?.Invoke(offset);
            }
            else Refresh();
        }
    }
}
