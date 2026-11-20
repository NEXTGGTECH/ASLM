// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Renders selectable console output in GTK while publishing metrics to ASLM's shared scrollbar.
    /// </summary>
    public sealed class ConsoleOutputViewHandler : GtkViewHandler<ConsoleOutputView, Gtk.ScrolledWindow>
    {
        // Fields

        /// <summary>
        /// Maps the scrollbar mode to the native scrolled window.
        /// </summary>
        public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
            new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewMapper)
            {
                [nameof(ConsoleOutputView.UseCustomScrollBar)] = (handler, _) => handler.ApplyScrollChrome()
            };

        /// <summary>
        /// Native text view that displays the console output, or null before the platform view is created.
        /// </summary>
        private Gtk.TextView? _textView;

        /// <summary>
        /// Vertical adjustment of the scrolled window, or null while the handler is disconnected.
        /// </summary>
        private Gtk.Adjustment? _adjustment;

        /// <summary>
        /// Text currently shown in the buffer, used to append only the new tail of the output.
        /// </summary>
        private string _rendered = string.Empty;

        /// <summary>
        /// Capture-phase input observer that leaves GTK's text selection gestures in charge.
        /// </summary>
        private Gtk.EventControllerLegacy? _pointerEvents;

        /// <summary>
        /// Whether the current output update should scroll to the bottom after layout.
        /// </summary>
        private bool _pendingFollow;

        /// <summary>
        /// Position to restore once GTK has recalculated the scrollable extent.
        /// </summary>
        private ConsoleScrollPosition? _pendingPosition;

        /// <summary>
        /// Suppresses adjustment notifications while the native buffer is being modified.
        /// </summary>
        private bool _applyingOutput;

        /// <summary>
        /// Whether a post-layout viewport update is already queued.
        /// </summary>
        private bool _viewportQueued;

        /// <summary>
        /// Invalidates queued viewport callbacks when the native widget disconnects.
        /// </summary>
        private int _generation;


        // Initialization

        /// <summary>
        /// Creates the console property mapper without introducing a separate MAUI control.
        /// </summary>
        public ConsoleOutputViewHandler() : base(Mapper) { }


        // Native view lifecycle

        /// <summary>
        /// Creates a read-only, selectable text view whose viewport is constrained by MAUI layout.
        /// </summary>
        protected override Gtk.ScrolledWindow CreatePlatformView()
        {
            // Read-only monospace text with the cursor hidden; the view stays selectable for copying log lines.
            _textView = Gtk.TextView.New();
            _textView.SetEditable(false);
            _textView.SetCursorVisible(false);
            _textView.GetBuffer().SetEnableUndo(false);
            _textView.SetMonospace(true);
            _textView.SetWrapMode(Gtk.WrapMode.WordChar);
            _textView.SetLeftMargin(10);
            _textView.SetRightMargin(10);
            _textView.SetTopMargin(8);
            _textView.SetBottomMargin(8);

            // Do not propagate the text size to the scrolled window, otherwise long output would grow the control
            // beyond the size MAUI layout gives it instead of scrolling.
            var scroll = Gtk.ScrolledWindow.New();
            scroll.SetPropagateNaturalHeight(false);
            scroll.SetPropagateNaturalWidth(false);
            scroll.SetChild(_textView);
            return scroll;
        }

        /// <summary>
        /// Connects scroll and palette notifications for the current native host.
        /// </summary>
        protected override void ConnectHandler(Gtk.ScrolledWindow platformView)
        {
            base.ConnectHandler(platformView);

            // Start a new generation so callbacks queued by an earlier connection are ignored.
            _generation++;

            // Observe the vertical adjustment: "changed" fires when content or viewport size changes,
            // "value-changed" when the user or the scrollbar moves the viewport.
            _adjustment = platformView.GetVadjustment();
            _adjustment.OnChanged += OnGeometryChanged;
            _adjustment.OnValueChanged += OnScrollChanged;

            // Let the shared ASLM scrollbar drive this native viewport and follow palette changes.
            VirtualView.ScrollToOffset = ScrollTo;
            ThemeService.PaletteApplied += OnPaletteApplied;
            ApplyTheme();
            ApplyScrollChrome();

            if (_textView is { } textView)
            {
                _pointerEvents = Gtk.EventControllerLegacy.New();
                _pointerEvents.SetPropagationPhase(Gtk.PropagationPhase.Capture);
                _pointerEvents.OnEvent += OnPointerEvent;
                textView.AddController(_pointerEvents);
            }

            VirtualView.ConnectOutput(ApplyOutput);
        }

        /// <summary>
        /// Releases subscriptions and invalidates queued work before native widgets are detached.
        /// </summary>
        protected override void DisconnectHandler(Gtk.ScrolledWindow platformView)
        {
            // Invalidate queued dispatcher callbacks so they cannot touch the detached widgets.
            _generation++;
            _viewportQueued = false;
            _pendingPosition = null;
            _pendingFollow = false;
            _rendered = string.Empty;

            if (_textView is { } textView)
            {
                if (_pointerEvents is { } pointerEvents)
                {
                    pointerEvents.OnEvent -= OnPointerEvent;
                    textView.RemoveController(pointerEvents);
                    _pointerEvents = null;
                }
            }

            // Detach the adjustment signals before the native widgets go away.
            if (_adjustment != null)
            {
                _adjustment.OnChanged -= OnGeometryChanged;
                _adjustment.OnValueChanged -= OnScrollChanged;
            }

            _adjustment = null;
            VirtualView.ScrollToOffset = null;
            VirtualView.DisconnectOutput();
            ThemeService.PaletteApplied -= OnPaletteApplied;
            base.DisconnectHandler(platformView);
        }


        // Native selection

        /// <summary>
        /// Tracks pointer and touch sequences without claiming or consuming GTK events.
        /// </summary>
        private bool OnPointerEvent(Gtk.EventControllerLegacy sender, Gtk.EventControllerLegacy.EventSignalArgs args)
        {
            switch (args.Event.GetEventType())
            {
                case Gdk.EventType.ButtonPress:
                case Gdk.EventType.TouchBegin:
                    _pendingFollow = false;
                    _pendingPosition = null;
                    VirtualView.SetSelectionGesture(true);
                    break;
                case Gdk.EventType.ButtonRelease:
                case Gdk.EventType.TouchEnd:
                case Gdk.EventType.TouchCancel:
                case Gdk.EventType.GrabBroken:
                    VirtualView.SetSelectionGesture(false);
                    break;
                case Gdk.EventType.Scroll:
                    _pendingFollow = false;
                    _pendingPosition = null;
                    break;
            }

            return false;
        }


        // Output and viewport

        /// <summary>
        /// Changes buffer ranges in place so GTK keeps its selection marks on retained text.
        /// </summary>
        private void ApplyOutput(ConsoleOutput output, ConsoleOutput? previous, bool sessionChanged)
        {
            if (_textView == null || _adjustment is not { } adjustment) return;

            // Resolve a preceding viewport correction before capturing the next one.
            if (_pendingPosition.HasValue && !sessionChanged) ApplyViewport();
            var offset = adjustment.GetValue();
            _pendingFollow = sessionChanged || _pendingFollow || VirtualView.ShouldFollowOutput(offset, GetMaximumOffset(adjustment));
            var appendOnly = output.Text.StartsWith(_rendered, StringComparison.Ordinal);
            _pendingPosition = _pendingFollow || appendOnly ? null : output.PreservePosition(previous, offset, GetLineTop);

            var buffer = _textView.GetBuffer();
            _applyingOutput = true;
            try
            {
                if (sessionChanged || previous == null)
                {
                    buffer.SetText(output.Text, -1);
                }
                else
                {
                    foreach (var edit in output.GetEdits(previous))
                    {
                        buffer.GetIterAtOffset(out var start, GetBufferIndex(edit.Start));
                        if (edit.End > edit.Start)
                        {
                            buffer.GetIterAtOffset(out var end, GetBufferIndex(edit.End));
                            buffer.Delete(start, end);
                            buffer.GetIterAtOffset(out start, GetBufferIndex(edit.Start));
                        }

                        if (edit.Text.Length > 0) buffer.Insert(start, edit.Text, -1);
                    }
                }

                _rendered = output.Text;
            }
            finally
            {
                _applyingOutput = false;
            }

            QueueViewportRefresh();
        }

        /// <summary>
        /// Converts a UTF-16 source index to an offset in Unicode characters in the GTK buffer.
        /// </summary>
        private int GetBufferIndex(int index)
        {
            var nativeIndex = 0;
            foreach (var _ in _rendered.AsSpan(0, index).EnumerateRunes()) nativeIndex++;
            return nativeIndex;
        }

        /// <summary>
        /// Gets a line's vertical position in the full GTK text buffer.
        /// </summary>
        private double? GetLineTop(int index)
        {
            if (_textView == null || index > _rendered.Length) return null;
            _textView.GetBuffer().GetIterAtOffset(out var iter, GetBufferIndex(index));
            _textView.GetLineYrange(iter, out var y, out _);
            return y;
        }

        /// <summary>
        /// Applies the viewport after pending text geometry work, even if the extent did not change.
        /// </summary>
        private void QueueViewportRefresh()
        {
            if (_viewportQueued) return;

            _viewportQueued = true;
            var generation = _generation;
            if (!VirtualView.Dispatcher.Dispatch(() =>
            {
                if (generation != _generation) return;
                ApplyViewport();
                _pendingPosition = null;
                _pendingFollow = false;
                _viewportQueued = false;
            }))
            {
                _viewportQueued = false;
            }
        }

        /// <summary>
        /// Hides native chrome without disabling GTK's scrollable viewport.
        /// </summary>
        private void ApplyScrollChrome() => PlatformView?.SetPolicy(Gtk.PolicyType.Never,
            VirtualView.UseCustomScrollBar ? Gtk.PolicyType.External : Gtk.PolicyType.Automatic);

        /// <summary>
        /// Applies the pending position again if layout changes the extent before the queued viewport pass.
        /// </summary>
        private void OnGeometryChanged(Gtk.Adjustment sender, EventArgs args)
        {
            if (!_applyingOutput) ApplyViewport();
        }

        /// <summary>
        /// Publishes native scrolling to the shared scrollbar.
        /// </summary>
        private void OnScrollChanged(Gtk.Adjustment sender, EventArgs args) => PublishScrollMetrics();

        /// <summary>
        /// Follows output or compensates for a change of text above the current viewport.
        /// </summary>
        private void ApplyViewport()
        {
            if (_adjustment is not { } adjustment) return;
            var maximum = GetMaximumOffset(adjustment);
            if (_pendingFollow) adjustment.SetValue(maximum);
            else if (_pendingPosition is { } position)
                adjustment.SetValue(Math.Clamp(position.Resolve(GetLineTop), 0, maximum));
            PublishScrollMetrics();
        }

        /// <summary>
        /// Returns the largest valid offset of the native scroll adjustment.
        /// </summary>
        private static double GetMaximumOffset(Gtk.Adjustment adjustment) =>
            Math.Max(0, adjustment.GetUpper() - adjustment.GetPageSize());

        /// <summary>
        /// Gives the shared scrollbar control of the native viewport.
        /// </summary>
        private void ScrollTo(double offset)
        {
            if (_adjustment is not { } adjustment) return;
            _pendingFollow = false;
            _pendingPosition = null;
            adjustment.SetValue(Math.Clamp(offset, 0, GetMaximumOffset(adjustment)));
        }

        /// <summary>
        /// Publishes native viewport dimensions without measuring or replacing the console text.
        /// </summary>
        private void PublishScrollMetrics()
        {
            if (_adjustment is { } adjustment)
                VirtualView.SetScrollMetrics(adjustment.GetPageSize(), adjustment.GetUpper(), adjustment.GetValue());
        }


        // Theming

        /// <summary>
        /// Marshals palette updates to the GTK thread and ignores disconnected views.
        /// </summary>
        private void OnPaletteApplied() => VirtualView.Dispatcher.Dispatch(() =>
        {
            if (_adjustment != null) ApplyTheme();
        });

        /// <summary>
        /// Applies the same surface and text palette as the other console handlers.
        /// </summary>
        private void ApplyTheme()
        {
            var surface = IconTintHelper.ResolvePaletteColor("BackgroundSecondary");
            var foreground = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            UpdateCssWithSelector(_textView, "textview, textview text",
                $"background-color: {surface.ToRgbaHex()}; color: {foreground.ToRgbaHex()}; font-family: monospace; font-size: 12px;");
        }
    }
}
