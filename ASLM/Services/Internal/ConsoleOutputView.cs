// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Hosts the bindable state consumed by the native console output handler.
    /// </summary>
    public sealed class ConsoleOutputView : View
    {
        // Bindable properties

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

        public static readonly BindableProperty UseCustomScrollBarProperty =
            BindableProperty.Create(nameof(UseCustomScrollBar), typeof(bool), typeof(ConsoleOutputView), false);

        public bool UseCustomScrollBar
        {
            get => (bool)GetValue(UseCustomScrollBarProperty);
            set => SetValue(UseCustomScrollBarProperty, value);
        }

        internal event EventHandler? ScrollMetricsChanged;
        internal double ScrollViewport { get; private set; }
        internal double ScrollExtent { get; private set; }
        internal double ScrollOffset { get; private set; }
        internal Action<double>? ScrollToOffset { get; set; }

        internal void SetScrollMetrics(double viewport, double extent, double offset)
        {
            if (ScrollViewport == viewport && ScrollExtent == extent && ScrollOffset == offset) return;
            ScrollViewport = viewport;
            ScrollExtent = extent;
            ScrollOffset = offset;
            ScrollMetricsChanged?.Invoke(this, EventArgs.Empty);
        }

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
