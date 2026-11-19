// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Coordinates output and selection across native console handlers and reports their viewport geometry
    /// to the shared scrollbar.
    /// </summary>
    public sealed class ConsoleOutputView : View
    {
        // Bindable property identifiers

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


        // Output and selection

        /// <summary>
        /// Latest snapshot supplied by the page.
        /// </summary>
        private ConsoleOutput _output = ConsoleOutput.Empty;

        /// <summary>
        /// Snapshot associated with the text currently rendered by the native view.
        /// </summary>
        private ConsoleOutput? _renderedOutput;

        /// <summary>
        /// Callback belonging to the current native handler.
        /// </summary>
        private Action<ConsoleOutput, ConsoleOutput?, bool>? _renderOutput;

        /// <summary>
        /// Whether a native selection gesture is in progress, including before the range becomes nonempty.
        /// </summary>
        private bool _selectionGesture;

        /// <summary>
        /// Whether one deferred native output update is already scheduled.
        /// </summary>
        private bool _outputQueued;

        /// <summary>
        /// Invalidates queued callbacks when a native handler is detached.
        /// </summary>
        private int _outputGeneration;

        /// <summary>
        /// Allows following new output only when the gesture has ended and the viewport is at the bottom.
        /// </summary>
        internal bool ShouldFollowOutput(double offset, double maximumOffset) =>
            !_selectionGesture && maximumOffset - offset <= 1;

        /// <summary>
        /// Accepts new output regardless of what the user has selected.
        /// </summary>
        internal void SetOutput(ConsoleOutput output)
        {
            _output = output;
            QueueOutput();
        }

        /// <summary>
        /// Attaches the platform renderer and schedules the latest snapshot.
        /// </summary>
        internal void ConnectOutput(Action<ConsoleOutput, ConsoleOutput?, bool> renderOutput)
        {
            _renderOutput = renderOutput;
            QueueOutput();
        }

        /// <summary>
        /// Detaches the renderer and clears native interaction state from the old connection.
        /// </summary>
        internal void DisconnectOutput()
        {
            _outputGeneration++;
            _renderOutput = null;
            _renderedOutput = null;
            _outputQueued = false;
            _selectionGesture = false;
        }

        /// <summary>
        /// Holds automatic following during a native selection gesture.
        /// </summary>
        internal void SetSelectionGesture(bool pressed) => _selectionGesture = pressed;

        /// <summary>
        /// Coalesces output and waits for native input handling to finish before touching the text.
        /// </summary>
        private void QueueOutput()
        {
            if (_renderOutput == null || _outputQueued || ReferenceEquals(_renderedOutput, _output)) return;

            _outputQueued = true;
            var generation = _outputGeneration;
            if (!Dispatcher.Dispatch(() =>
            {
                if (generation != _outputGeneration) return;
                _outputQueued = false;
                ApplyOutput();
            }))
            {
                _outputQueued = false;
            }
        }

        /// <summary>
        /// Applies only the latest snapshot and lets a session change discard the old selection.
        /// </summary>
        private void ApplyOutput()
        {
            var output = _output;
            var previous = _renderedOutput;
            var sessionChanged = output.SessionKey != previous?.SessionKey;

            _renderedOutput = output;

            if (sessionChanged || output.Text != previous?.Text)
            {
                _renderOutput?.Invoke(output, previous, sessionChanged);
            }
        }
    }
}
