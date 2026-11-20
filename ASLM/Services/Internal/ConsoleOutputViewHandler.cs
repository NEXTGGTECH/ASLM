// Copyright NEXTGGTECH. Apache License 2.0.

#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIColor = Windows.UI.Color;
using WinUICornerRadius = Microsoft.UI.Xaml.CornerRadius;
using WinUIHorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using WinUISolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
using WinUIThickness = Microsoft.UI.Xaml.Thickness;
using WinUIVerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Maps <see cref="ConsoleOutputView"/> to a native WinUI text box with stable selection, sizing, and scrolling.
    /// </summary>
    /// <remarks>
    /// WinUI text selection can lag at very high USB mouse report rates (for example 1000 Hz).
    /// The native host forwards mouse <see cref="UIElement.PointerMoved"/> to the base implementation
    /// at a bounded rate during left-button drags so selection work is not invoked once per USB report.
    /// </remarks>
    public sealed class ConsoleOutputViewHandler : ViewHandler<ConsoleOutputView, RichEditBox>
    {
        // Fields and constants

        /// <summary>
        /// Palette key of the console background color.
        /// </summary>
        private const string ConsoleSurfaceColorKey = "BackgroundSecondary";

        /// <summary>
        /// Palette key of the console text color.
        /// </summary>
        private const string ConsoleTextColorKey = "LabelPrimary";

        /// <summary>
        /// Palette key of the text selection highlight color.
        /// </summary>
        private const string ConsoleSelectionColorKey = "SystemBlueOverlay";

        /// <summary>
        /// Maps the scrollbar mode to the native text box.
        /// </summary>
        public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
            new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewHandler.ViewMapper)
            {
                [nameof(ConsoleOutputView.UseCustomScrollBar)] = static (handler, view) => handler.ApplyScrollChrome()
            };

        /// <summary>
        /// Scroll viewer found inside the text box template, or null until it is available.
        /// </summary>
        private ScrollViewer? _scrollViewer;

        /// <summary>
        /// Pending scroll to the end of the output after native layout.
        /// </summary>
        private bool _pendingScrollToEnd;

        /// <summary>
        /// Text assigned to the native control, used to translate source offsets to RichEdit positions.
        /// </summary>
        private string _renderedText = string.Empty;

        /// <summary>
        /// Indicates that a post-layout viewport pass is already queued.
        /// </summary>
        private bool _viewportRefreshQueued;

        /// <summary>
        /// Invalidates viewport callbacks of a disconnected native text box.
        /// </summary>
        private int _connectionGeneration;

        /// <summary>
        /// Whether this text box is currently connected to the virtual console.
        /// </summary>
        private bool _connected;


        // Initialization

        /// <summary>
        /// Creates the handler instance for the native console host.
        /// </summary>
        public ConsoleOutputViewHandler() : base(Mapper)
        {
        }


        // Handler lifecycle

        /// <summary>
        /// Creates the native WinUI text box used to render console output.
        /// </summary>
        protected override RichEditBox CreatePlatformView()
        {
            // Read-only, wrapping monospace text that fills the available space; spell checking and prediction
            // are off because the content is program output.
            var textBox = new ConsoleOutputHost
            {
                AcceptsReturn = true,
                IsReadOnly = true,
                ClipboardCopyFormat = RichEditClipboardFormat.PlainText,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = WinUIHorizontalAlignment.Stretch,
                VerticalAlignment = WinUIVerticalAlignment.Stretch,
                HorizontalContentAlignment = WinUIHorizontalAlignment.Stretch,
                VerticalContentAlignment = WinUIVerticalAlignment.Stretch,
                BorderThickness = new WinUIThickness(0),
                Padding = new WinUIThickness(10, 8, 10, 8),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextReadingOrder = TextReadingOrder.DetectFromContent,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                MinHeight = 0,
                CornerRadius = new WinUICornerRadius(10)
            };

            textBox.Document.UndoLimit = 0;
            ApplyConsoleTheme(textBox);
            return textBox;
        }

        /// <summary>
        /// Hooks native events and applies the initial virtual view state.
        /// </summary>
        protected override void ConnectHandler(RichEditBox platformView)
        {
            base.ConnectHandler(platformView);

            // Follow the OS appearance and palette changes; unsubscribing first keeps a reconnect from subscribing twice.
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;

            ApplyConsoleTheme(platformView);

            // Observe handled pointer events too, because the text box owns the native selection gesture.
            _connected = true;
            platformView.Loaded += OnPlatformViewLoaded;
            platformView.AddHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnPlatformViewPointerPressed, true);
            platformView.AddHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)OnPlatformViewPointerEnded, true);
            platformView.AddHandler(UIElement.PointerCanceledEvent, (PointerEventHandler)OnPlatformViewPointerEnded, true);
            platformView.AddHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)OnPlatformViewPointerEnded, true);
            platformView.AddHandler(UIElement.PointerWheelChangedEvent, (PointerEventHandler)OnPlatformViewWheelChanged, true);
            VirtualView.ScrollToOffset = ScrollTo;
            VirtualView.ConnectOutput(ApplyOutput);
        }

        /// <summary>
        /// Unhooks native events and releases the cached scroll viewer.
        /// </summary>
        protected override void DisconnectHandler(RichEditBox platformView)
        {
            // Unsubscribe from everything subscribed in ConnectHandler and EnsureScrollViewer.
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;

            _connected = false;
            _connectionGeneration++;
            _viewportRefreshQueued = false;
            _renderedText = string.Empty;
            _pendingScrollToEnd = false;
            platformView.Loaded -= OnPlatformViewLoaded;
            platformView.RemoveHandler(UIElement.PointerPressedEvent, (PointerEventHandler)OnPlatformViewPointerPressed);
            platformView.RemoveHandler(UIElement.PointerReleasedEvent, (PointerEventHandler)OnPlatformViewPointerEnded);
            platformView.RemoveHandler(UIElement.PointerCanceledEvent, (PointerEventHandler)OnPlatformViewPointerEnded);
            platformView.RemoveHandler(UIElement.PointerCaptureLostEvent, (PointerEventHandler)OnPlatformViewPointerEnded);
            platformView.RemoveHandler(UIElement.PointerWheelChangedEvent, (PointerEventHandler)OnPlatformViewWheelChanged);

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
                _scrollViewer.SizeChanged -= OnScrollMetricsChanged;
                _scrollViewer.LayoutUpdated -= OnScrollMetricsChanged;
                _scrollViewer = null;
            }

            // The shared scrollbar must no longer call into a detached handler.
            VirtualView.ScrollToOffset = null;
            VirtualView.DisconnectOutput();

            base.DisconnectHandler(platformView);
        }


        // Theme

        /// <summary>
        /// Reapplies console colors when the OS appearance changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            RefreshConsoleTheme();
        }

        /// <summary>
        /// Reapplies console colors after <see cref="ThemeService"/> rewrites the active palette.
        /// </summary>
        private void OnPaletteApplied()
        {
            RefreshConsoleTheme();
        }

        /// <summary>
        /// Reapplies console colors on the UI thread.
        /// </summary>
        private void RefreshConsoleTheme()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (PlatformView != null)
                {
                    ApplyConsoleTheme(PlatformView);
                }
            });
        }

        /// <summary>
        /// Applies console surface, text, selection, and WinUI text control chrome from the active ASLM palette.
        /// </summary>
        private static void ApplyConsoleTheme(RichEditBox textBox)
        {
            var surface = ToWinUiColor(ResolveThemeColor(ConsoleSurfaceColorKey));
            var text = ToWinUiColor(ResolveThemeColor(ConsoleTextColorKey));
            var selection = ToWinUiColor(ResolveThemeColor(ConsoleSelectionColorKey));

            textBox.Background = new WinUISolidColorBrush(surface);
            textBox.Foreground = new WinUISolidColorBrush(text);
            textBox.SelectionHighlightColor = new WinUISolidColorBrush(selection);
            ApplyConsoleChrome(textBox, surface, text);
        }

        /// <summary>
        /// Resolves a palette color from <see cref="Application.Current"/> resources, falling back to <see cref="ThemePaletteResolver"/>.
        /// </summary>
        private static Color ResolveThemeColor(string key)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true &&
                value is Color resourceColor)
            {
                return resourceColor;
            }

            var isDark = Microsoft.Maui.Controls.Application.Current?.RequestedTheme != AppTheme.Light;
            var palette = isDark
                ? ThemePaletteResolver.BuildDarkPalette()
                : ThemePaletteResolver.BuildLightPalette();
            return palette.TryGetValue(key, out var paletteColor) ? paletteColor : Colors.Transparent;
        }

        /// <summary>
        /// Converts a MAUI color into the WinUI color type used by the native text box.
        /// </summary>
        private static WinUIColor ToWinUiColor(Color color) =>
            WinUIColor.FromArgb(
                (byte)Math.Clamp(color.Alpha * 255f, 0, 255),
                (byte)Math.Clamp(color.Red * 255f, 0, 255),
                (byte)Math.Clamp(color.Green * 255f, 0, 255),
                (byte)Math.Clamp(color.Blue * 255f, 0, 255));

        /// <summary>
        /// Applies text control resource brushes so hover and focus states match the console surface and text colors.
        /// </summary>
        private static void ApplyConsoleChrome(RichEditBox textBox, WinUIColor surfaceColor, WinUIColor textColor)
        {
            var surfaceBrush = new WinUISolidColorBrush(surfaceColor);
            var textBrush = new WinUISolidColorBrush(textColor);
            var transparentBrush = new WinUISolidColorBrush(WinUIColor.FromArgb(0, 0, 0, 0));

            textBox.Resources["TextControlBackground"] = surfaceBrush;
            textBox.Resources["TextControlBackgroundPointerOver"] = surfaceBrush;
            textBox.Resources["TextControlBackgroundFocused"] = surfaceBrush;
            textBox.Resources["TextControlBackgroundDisabled"] = surfaceBrush;
            textBox.Resources["TextControlForeground"] = textBrush;
            textBox.Resources["TextControlForegroundPointerOver"] = textBrush;
            textBox.Resources["TextControlForegroundFocused"] = textBrush;
            textBox.Resources["TextControlForegroundDisabled"] = textBrush;
            textBox.Resources["TextControlBorderBrush"] = transparentBrush;
            textBox.Resources["TextControlBorderBrushPointerOver"] = transparentBrush;
            textBox.Resources["TextControlBorderBrushFocused"] = transparentBrush;
            textBox.Resources["TextControlBorderBrushDisabled"] = transparentBrush;
            textBox.Resources["TextControlHeaderForeground"] = textBrush;
            textBox.Resources["TextControlHeaderForegroundFocused"] = textBrush;
            textBox.Resources["TextControlPlaceholderForeground"] = textBrush;
        }


        // Platform events

        /// <summary>
        /// Resolves the template scroll viewer before applying the initial viewport position.
        /// </summary>
        private void OnPlatformViewLoaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            QueueViewportRefresh();
        }

        /// <summary>
        /// Publishes current geometry when the reader scrolls.
        /// </summary>
        private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => PublishScrollMetrics();

        /// <summary>
        /// Cancels an already queued follow when a native selection gesture starts.
        /// </summary>
        private void OnPlatformViewPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if ((int)e.Pointer.PointerDeviceType == (int)Windows.Devices.Input.PointerDeviceType.Mouse &&
                !e.GetCurrentPoint(PlatformView).Properties.IsLeftButtonPressed)
            {
                return;
            }

            _pendingScrollToEnd = false;
            VirtualView.SetSelectionGesture(true);
        }

        /// <summary>
        /// Releases the gesture guard when WinUI ends or cancels the native pointer sequence.
        /// </summary>
        private void OnPlatformViewPointerEnded(object sender, PointerRoutedEventArgs e)
        {
            VirtualView.SetSelectionGesture(false);
        }

        /// <summary>
        /// Gives scrolling by wheel priority over an automatic viewport update already queued.
        /// </summary>
        private void OnPlatformViewWheelChanged(object sender, PointerRoutedEventArgs e) => _pendingScrollToEnd = false;


        // Output and viewport

        /// <summary>
        /// Updates native document ranges and offsets changes above the reader's viewport.
        /// </summary>
        private void ApplyOutput(ConsoleOutput output, ConsoleOutput? previous, bool sessionChanged)
        {
            EnsureScrollViewer();
            var viewer = _scrollViewer;
            var offset = viewer?.VerticalOffset ?? 0;
            var follow = sessionChanged || _pendingScrollToEnd ||
                         VirtualView.ShouldFollowOutput(offset, viewer?.ScrollableHeight ?? 0);
            var position = follow ? default : output.PreservePosition(previous, offset, GetLineTop);

            WriteOutput(output, previous, sessionChanged);

            _renderedText = output.Text;
            if (follow)
            {
                _pendingScrollToEnd = true;
                QueueViewportRefresh();
            }
            else if (viewer != null)
            {
                if (position.CharacterIndex >= 0) PlatformView.UpdateLayout();
                var target = Math.Clamp(position.Resolve(GetLineTop), 0, viewer.ScrollableHeight);
                if (Math.Abs(viewer.VerticalOffset - target) >= 0.5)
                    viewer.ChangeView(null, target, null, disableAnimation: true);
            }
        }

        /// <summary>
        /// Edits document ranges with the control writable for this synchronous operation only.
        /// </summary>
        private void WriteOutput(ConsoleOutput output, ConsoleOutput? previous, bool sessionChanged)
        {
            var editor = PlatformView;
            var document = editor.Document;

            // WinUI rejects document writes with IsReadOnly set. No input is dispatched during the write.
            document.BatchDisplayUpdates();
            try
            {
                editor.IsReadOnly = false;
                if (sessionChanged || previous == null)
                {
                    document.SetText(TextSetOptions.None, output.Text);
                    document.Selection.SetRange(0, 0);
                    return;
                }

                // Independent ranges keep the active selection and the native drag anchor in the document.
                foreach (var edit in output.GetEdits(previous))
                {
                    var start = GetDocumentIndex(edit.Start);
                    var end = GetDocumentIndex(edit.End);
                    document.GetRange(start, end).SetText(TextSetOptions.None, edit.Text);
                }
            }
            finally
            {
                editor.IsReadOnly = true;
                document.ApplyDisplayUpdates();
            }
        }

        /// <summary>
        /// Converts a UTF-16 source index to a RichEdit position, where CRLF counts as one character.
        /// </summary>
        private int GetDocumentIndex(int index) => index - _renderedText.AsSpan(0, index).Count("\r\n");

        /// <summary>
        /// Gets the line top in RichEdit's full text area, even when the line is outside the visible viewport.
        /// </summary>
        private double? GetLineTop(int index)
        {
            if (_scrollViewer == null || index >= _renderedText.Length) return null;

            var documentIndex = GetDocumentIndex(index);
            try
            {
                var range = PlatformView.Document.GetRange(documentIndex, documentIndex);
                range.GetPoint(HorizontalCharacterAlignment.Left, VerticalCharacterAlignment.Top,
                    PointOptions.Start | PointOptions.ClientCoordinates | PointOptions.AllowOffClient, out var point);
                return double.IsFinite(point.Y) ? point.Y : null;
            }
            catch (Exception ex) when (ex is ArgumentException or COMException)
            {
                // The old offset is still usable when the native document has no line rectangle yet.
                return null;
            }
        }

        /// <summary>
        /// Coalesces layout and scroll work into one pass bound to the current native connection.
        /// </summary>
        private void QueueViewportRefresh()
        {
            if (!_connected || !_pendingScrollToEnd || _viewportRefreshQueued) return;

            _viewportRefreshQueued = true;
            var generation = _connectionGeneration;
            if (!PlatformView.DispatcherQueue.TryEnqueue(() =>
            {
                if (generation != _connectionGeneration) return;
                _viewportRefreshQueued = false;
                ApplyViewport();
            }))
            {
                _viewportRefreshQueued = false;
            }
        }

        /// <summary>
        /// Follows the latest output after layout, unless native input canceled the pending request.
        /// </summary>
        private void ApplyViewport()
        {
            if (!_pendingScrollToEnd) return;
            EnsureScrollViewer();
            if (_scrollViewer is not { } viewer) return;

            PlatformView.UpdateLayout();
            _pendingScrollToEnd = false;
            viewer.ChangeView(null, viewer.ScrollableHeight, null, disableAnimation: true);
            PublishScrollMetrics();
        }

        /// <summary>
        /// Moves the scroll viewer in response to a drag on ASLM's scrollbar.
        /// </summary>
        private void ScrollTo(double offset)
        {
            _pendingScrollToEnd = false;
            EnsureScrollViewer();
            _scrollViewer?.ChangeView(null, offset, null, disableAnimation: true);
        }

        /// <summary>
        /// Finds and caches the inner scroll viewer hosted by the native text box.
        /// </summary>
        private void EnsureScrollViewer()
        {
            if (PlatformView == null)
            {
                return;
            }

            if (_scrollViewer != null)
            {
                return;
            }

            // The template creates the inner scroll viewer lazily, so it may be missing until the text box is loaded.
            var resolvedScrollViewer = FindDescendantScrollViewer(PlatformView);
            if (ReferenceEquals(_scrollViewer, resolvedScrollViewer))
            {
                return;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
            }

            // Subscribe to the new viewer: view changes track the bottom position, size and layout changes
            // feed the shared scrollbar, and the scrollbar chrome is applied to it.
            _scrollViewer = resolvedScrollViewer;
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
                _scrollViewer.SizeChanged += OnScrollMetricsChanged;
                _scrollViewer.LayoutUpdated += OnScrollMetricsChanged;
                ApplyScrollChrome();
            }
        }

        /// <summary>
        /// Publishes the scroll geometry again when the inner scroll viewer changes size or layout.
        /// </summary>
        private void OnScrollMetricsChanged(object? sender, object e) => PublishScrollMetrics();

        /// <summary>
        /// Reports viewport height, content height and offset to the virtual view when the shared ASLM scrollbar is in use.
        /// </summary>
        private void PublishScrollMetrics()
        {
            if (VirtualView?.UseCustomScrollBar == true && _scrollViewer is { } viewer)
                VirtualView.SetScrollMetrics(viewer.ViewportHeight, viewer.ExtentHeight, viewer.VerticalOffset);
        }

        /// <summary>
        /// Hides the native vertical scrollbar when the shared ASLM scrollbar is used and shows it automatically otherwise.
        /// </summary>
        private void ApplyScrollChrome()
        {
            if (_scrollViewer == null) return;
            _scrollViewer.VerticalScrollBarVisibility = VirtualView?.UseCustomScrollBar == true
                ? Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden : Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto;
            PublishScrollMetrics();
        }


        // Visual tree

        /// <summary>
        /// Recursively searches the WinUI visual tree for the first nested scroll viewer.
        /// </summary>
        internal static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                var result = FindDescendantScrollViewer(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }


        // Native host

        /// <summary>
        /// Caps WinUI mouse-move processing during a left-button drag while retaining native text selection.
        /// </summary>
        private sealed class ConsoleOutputHost : RichEditBox
        {
            /// <summary>
            /// Minimum time between forwarded mouse moves during a selection drag, in milliseconds.
            /// </summary>
            private const int PointerMoveMinIntervalMs = 10;

            /// <summary>
            /// <see cref="Environment.TickCount64"/> value of the last pointer-move event that was passed to the base implementation.
            /// </summary>
            private long _lastForwardedPointerMoveTicks;

            /// <summary>
            /// Resets the move throttle when a press starts so the first move of a drag is always forwarded.
            /// </summary>
            protected override void OnPointerPressed(PointerRoutedEventArgs e)
            {
                // Allow the first move after a press to reach the base implementation even if a prior gesture forwarded recently.
                _lastForwardedPointerMoveTicks = Environment.TickCount64 - PointerMoveMinIntervalMs;
                base.OnPointerPressed(e);
            }

            /// <summary>
            /// Forwards pointer moves to the text box, but while the left mouse button is held (text selection)
            /// at most once per <see cref="PointerMoveMinIntervalMs"/>; the moves in between are marked as handled.
            /// </summary>
            protected override void OnPointerMoved(PointerRoutedEventArgs e)
            {
                // Touch, pen and other non-mouse pointers are not throttled.
                // WinRT projects PointerDeviceType under a distinct enum type from Windows.Devices.Input; values align with Mouse == 0.
                if ((int)e.Pointer.PointerDeviceType != (int)Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    _lastForwardedPointerMoveTicks = Environment.TickCount64;
                    base.OnPointerMoved(e);
                    return;
                }

                // Mouse moves without the left button cannot extend a selection, so they pass through.
                var current = e.GetCurrentPoint(this);
                if (!current.Properties.IsLeftButtonPressed)
                {
                    _lastForwardedPointerMoveTicks = Environment.TickCount64;
                    base.OnPointerMoved(e);
                    return;
                }

                // Left-button drag: forward the move only when enough time passed since the last forwarded one
                // (a negative interval means the tick counter was reset, so the move is forwarded as well).
                var now = Environment.TickCount64;
                var elapsed = now - _lastForwardedPointerMoveTicks;
                if (elapsed >= PointerMoveMinIntervalMs || elapsed < 0)
                {
                    _lastForwardedPointerMoveTicks = now;
                    base.OnPointerMoved(e);
                    return;
                }

                // Do not invoke RichEditBox pointer-move selection for this report (high-Hz mice).
                e.Handled = true;
            }
        }
    }
}
#endif
