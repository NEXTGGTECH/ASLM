// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Hosts the bindable state consumed by the native console output handler. The handler renders the text and
    /// reports its scroll geometry back through this view so the shared ASLM scrollbar can follow it.
    /// </summary>
    public sealed class ConsoleOutputView : View
    {
        // Bindable property identifiers

        /// <summary>
        /// Identifies the bindable console text property.
        /// </summary>
        public static readonly BindableProperty TextProperty =
            BindableProperty.Create(nameof(Text), typeof(string), typeof(ConsoleOutputView), string.Empty);

        /// <summary>
        /// Identifies the bindable session key property used to detect selection changes.
        /// </summary>
        public static readonly BindableProperty SessionKeyProperty =
            BindableProperty.Create(nameof(SessionKey), typeof(string), typeof(ConsoleOutputView), string.Empty);

        /// <summary>
        /// Identifies the bindable property that switches the native scrollbar off in favor of the shared ASLM scrollbar.
        /// </summary>
        public static readonly BindableProperty UseCustomScrollBarProperty =
            BindableProperty.Create(nameof(UseCustomScrollBar), typeof(bool), typeof(ConsoleOutputView), false);


        // Custom scrollbar bridge

        /// <summary>
        /// Gets or sets whether the native scrollbar is hidden because the shared ASLM scrollbar drives scrolling.
        /// </summary>
        public bool UseCustomScrollBar
        {
            get => (bool)GetValue(UseCustomScrollBarProperty);
            set => SetValue(UseCustomScrollBarProperty, value);
        }

        /// <summary>
        /// Raised when the native handler reports changed viewport, extent or offset values.
        /// </summary>
        internal event EventHandler? ScrollMetricsChanged;

        /// <summary>
        /// Height of the visible console area, as last reported by the native handler.
        /// </summary>
        internal double ScrollViewport { get; private set; }

        /// <summary>
        /// Total height of the console content, as last reported by the native handler.
        /// </summary>
        internal double ScrollExtent { get; private set; }

        /// <summary>
        /// Vertical scroll position of the console content, as last reported by the native handler.
        /// </summary>
        internal double ScrollOffset { get; private set; }

        /// <summary>
        /// Callback installed by the native handler that scrolls the console to a vertical offset;
        /// null while no native view is connected.
        /// </summary>
        internal Action<double>? ScrollToOffset { get; set; }

        /// <summary>
        /// Stores the geometry reported by the native handler and raises <see cref="ScrollMetricsChanged"/>
        /// only when a value actually changed.
        /// </summary>
        /// <param name="viewport">Height of the visible console area.</param>
        /// <param name="extent">Total height of the console content.</param>
        /// <param name="offset">Current vertical scroll position.</param>
        internal void SetScrollMetrics(double viewport, double extent, double offset)
        {
            if (ScrollViewport == viewport && ScrollExtent == extent && ScrollOffset == offset) return;
            ScrollViewport = viewport;
            ScrollExtent = extent;
            ScrollOffset = offset;
            ScrollMetricsChanged?.Invoke(this, EventArgs.Empty);
        }


        // Bindable properties

        /// <summary>
        /// Gets or sets the console text rendered by the native host.
        /// </summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>
        /// Gets or sets the composite session key used to reset scroll position for a new session.
        /// </summary>
        public string SessionKey
        {
            get => (string)GetValue(SessionKeyProperty);
            set => SetValue(SessionKeyProperty, value);
        }
    }
}
