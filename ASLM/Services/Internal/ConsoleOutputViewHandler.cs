// Copyright NEXTGGTECH. Apache License 2.0.

#if WINDOWS
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
    /// WinUI 3 <see cref="TextBox"/> text selection can lag at very high USB mouse report rates (for example 1000 Hz);
    /// most of that work runs inside the framework before application handlers. Lowering the device polling rate is a known workaround.
    /// The native host is a derived <see cref="TextBox"/> that forwards mouse <see cref="UIElement.PointerMoved"/> to the base implementation
    /// at a bounded rate during left-button drags so selection work is not invoked once per USB report.
    /// </remarks>
    public sealed class ConsoleOutputViewHandler : ViewHandler<ConsoleOutputView, TextBox>
    {
        // Fields and constants

        private const string ConsoleSurfaceColorKey = "BackgroundSecondary";
        private const string ConsoleTextColorKey = "LabelPrimary";
        private const string ConsoleSelectionColorKey = "SystemBlueOverlay";

        public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
            new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewHandler.ViewMapper)
            {
                [nameof(ConsoleOutputView.Text)] = static (handler, view) => handler.ApplyText(view.Text),
                [nameof(ConsoleOutputView.SessionKey)] = static (handler, view) => handler.ApplySessionKey(view.SessionKey)
            };

        private ScrollViewer? _scrollViewer;
        private bool _isNearBottom = true;
        private bool _forceScrollToEnd = true;
        private bool _pendingScrollToEnd = true;
        private bool _isApplyingProgrammaticScroll;
        private string _lastSessionKey = string.Empty;
        private int _viewportRefreshQueued;
        private int _queuedViewportPasses;
        private bool _pointerDownOnConsole;
        private bool _deferScrollAfterInteraction;
        private bool _suppressNativeTextChanged;


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
        protected override TextBox CreatePlatformView()
        {
            var textBox = new ConsoleOutputHostTextBox
            {
                AcceptsReturn = true,
                IsReadOnly = true,
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

            ApplyConsoleTheme(textBox);
            return textBox;
        }

        /// <summary>
        /// Hooks native events and applies the initial virtual view state.
        /// </summary>
        protected override void ConnectHandler(TextBox platformView)
        {
            base.ConnectHandler(platformView);

            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;

            ApplyConsoleTheme(platformView);

            platformView.Loaded += OnPlatformViewLoaded;
            platformView.SizeChanged += OnPlatformViewSizeChanged;
            platformView.TextChanged += OnPlatformViewTextChanged;
            platformView.PointerPressed += OnPlatformViewPointerPressed;
            platformView.PointerReleased += OnPlatformViewPointerEnded;
            platformView.PointerCanceled += OnPlatformViewPointerEnded;
            platformView.PointerCaptureLost += OnPlatformViewPointerCaptureLost;
            platformView.SelectionChanged += OnPlatformViewSelectionChanged;

            EnsureScrollViewer();
            ApplySessionKey(VirtualView?.SessionKey);
            ApplyText(VirtualView?.Text);
            QueueViewportRefresh(scrollToEnd: true, passCount: 3);
        }

        /// <summary>
        /// Unhooks native events and releases the cached scroll viewer.
        /// </summary>
        protected override void DisconnectHandler(TextBox platformView)
        {
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;

            platformView.Loaded -= OnPlatformViewLoaded;
            platformView.SizeChanged -= OnPlatformViewSizeChanged;
            platformView.TextChanged -= OnPlatformViewTextChanged;
            platformView.PointerPressed -= OnPlatformViewPointerPressed;
            platformView.PointerReleased -= OnPlatformViewPointerEnded;
            platformView.PointerCanceled -= OnPlatformViewPointerEnded;
            platformView.PointerCaptureLost -= OnPlatformViewPointerCaptureLost;
            platformView.SelectionChanged -= OnPlatformViewSelectionChanged;

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
                _scrollViewer = null;
            }

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
        private static void ApplyConsoleTheme(TextBox textBox)
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
        private static void ApplyConsoleChrome(TextBox textBox, WinUIColor surfaceColor, WinUIColor textColor)
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
        /// Refreshes the viewport after the native text box enters the visual tree.
        /// </summary>
        private void OnPlatformViewLoaded(object sender, RoutedEventArgs e)
        {
            EnsureScrollViewer();
            _pendingScrollToEnd = true;
            QueueViewportRefresh(scrollToEnd: true, passCount: 3);
        }

        /// <summary>
        /// Reapplies viewport sizing after the native text box changes size.
        /// </summary>
        private void OnPlatformViewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (UserBlocksAutoscrollForScroll())
            {
                return;
            }

            if (_pendingScrollToEnd || _isNearBottom)
            {
                QueueViewportRefresh(scrollToEnd: true, passCount: 2);
            }
            else
            {
                QueueViewportRefresh(scrollToEnd: false, passCount: 1);
            }
        }

        /// <summary>
        /// Continues deferred scroll-to-end work after the native text content changes.
        /// </summary>
        private void OnPlatformViewTextChanged(object sender, Microsoft.UI.Xaml.Controls.TextChangedEventArgs e)
        {
            if (_suppressNativeTextChanged)
            {
                return;
            }

            if (!_pendingScrollToEnd)
            {
                return;
            }

            if (UserBlocksAutoscrollForScroll())
            {
                _deferScrollAfterInteraction = true;
                return;
            }

            QueueViewportRefresh(scrollToEnd: true, passCount: 2);
        }

        /// <summary>
        /// Tracks whether the user is still near the bottom of the console output.
        /// </summary>
        private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_scrollViewer == null || _isApplyingProgrammaticScroll)
            {
                return;
            }

            _isNearBottom = IsNearBottom();
        }

        /// <summary>
        /// Marks the console as actively receiving pointer input.
        /// </summary>
        private void OnPlatformViewPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pointerDownOnConsole = true;
        }

        /// <summary>
        /// Clears pointer tracking and flushes any deferred scroll.
        /// </summary>
        private void OnPlatformViewPointerEnded(object sender, PointerRoutedEventArgs e)
        {
            _pointerDownOnConsole = false;
            TryFlushDeferredScroll();
        }

        /// <summary>
        /// Clears pointer tracking when capture is lost and flushes any deferred scroll.
        /// </summary>
        private void OnPlatformViewPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _pointerDownOnConsole = false;
            TryFlushDeferredScroll();
        }

        /// <summary>
        /// Attempts to flush deferred scroll after the text selection changes.
        /// </summary>
        private void OnPlatformViewSelectionChanged(object sender, RoutedEventArgs e)
        {
            TryFlushDeferredScroll();
        }


        // Scroll and viewport

        /// <summary>
        /// Returns true while the user is dragging, holding pointer, or has an active text selection.
        /// </summary>
        private bool UserBlocksAutoscrollForScroll()
        {
            if (PlatformView == null)
            {
                return false;
            }

            return _pointerDownOnConsole || PlatformView.SelectionLength > 0;
        }

        /// <summary>
        /// Runs a deferred scroll-to-end once pointer and selection no longer block autoscroll.
        /// </summary>
        private void TryFlushDeferredScroll()
        {
            if (!_deferScrollAfterInteraction || !_pendingScrollToEnd || PlatformView == null)
            {
                return;
            }

            if (UserBlocksAutoscrollForScroll())
            {
                return;
            }

            _deferScrollAfterInteraction = false;
            QueueViewportRefresh(scrollToEnd: true, passCount: 3);
        }

        /// <summary>
        /// Detects when the selected session changes so a new console opens pinned to the latest output.
        /// </summary>
        private void ApplySessionKey(string? sessionKey)
        {
            var resolvedSessionKey = sessionKey ?? string.Empty;
            if (string.Equals(_lastSessionKey, resolvedSessionKey, StringComparison.Ordinal))
            {
                return;
            }

            _lastSessionKey = resolvedSessionKey;
            _pointerDownOnConsole = false;
            _deferScrollAfterInteraction = false;
            _forceScrollToEnd = true;
            _pendingScrollToEnd = true;
            QueueViewportRefresh(scrollToEnd: true, passCount: 3);
        }

        /// <summary>
        /// Applies updated console text and decides whether the viewport should remain pinned to the bottom.
        /// </summary>
        private void ApplyText(string? text)
        {
            if (PlatformView == null)
            {
                return;
            }

            var resolvedText = text ?? string.Empty;
            var shouldPinToBottom = _forceScrollToEnd || IsNearBottom() || string.IsNullOrEmpty(PlatformView.Text);

            if (!string.Equals(PlatformView.Text, resolvedText, StringComparison.Ordinal))
            {
                if (UserBlocksAutoscrollForScroll())
                {
                    _deferScrollAfterInteraction = true;
                }

                _suppressNativeTextChanged = true;
                try
                {
                    PlatformView.Text = resolvedText;
                }
                finally
                {
                    _suppressNativeTextChanged = false;
                }
            }

            if (shouldPinToBottom)
            {
                _pendingScrollToEnd = true;
                if (UserBlocksAutoscrollForScroll())
                {
                    _deferScrollAfterInteraction = true;
                }
                else
                {
                    QueueViewportRefresh(scrollToEnd: true, passCount: 2);
                }
            }
            else
            {
                _pendingScrollToEnd = false;
                _deferScrollAfterInteraction = false;
                QueueViewportRefresh(scrollToEnd: false, passCount: 1);
            }

            _forceScrollToEnd = false;
        }

        /// <summary>
        /// Schedules one or more deferred viewport refresh passes on the native dispatcher queue.
        /// </summary>
        private void QueueViewportRefresh(bool scrollToEnd, int passCount)
        {
            if (PlatformView?.DispatcherQueue == null)
            {
                return;
            }

            if (scrollToEnd)
            {
                _pendingScrollToEnd = true;
            }

            _queuedViewportPasses = Math.Max(_queuedViewportPasses, Math.Clamp(passCount, 1, 3));
            if (Interlocked.Exchange(ref _viewportRefreshQueued, 1) == 1)
            {
                return;
            }

            QueueViewportRefreshPass();
        }

        /// <summary>
        /// Runs one deferred viewport refresh pass and reschedules the remainder when needed.
        /// </summary>
        private void QueueViewportRefreshPass()
        {
            if (PlatformView?.DispatcherQueue == null)
            {
                Interlocked.Exchange(ref _viewportRefreshQueued, 0);
                return;
            }

            if (!PlatformView.DispatcherQueue.TryEnqueue(() =>
            {
                var remainingPasses = _queuedViewportPasses;
                _queuedViewportPasses = Math.Max(0, remainingPasses - 1);

                var userBlocks = UserBlocksAutoscrollForScroll();
                if (!userBlocks || !_pendingScrollToEnd)
                {
                    RefreshViewport();
                }
                else
                {
                    _deferScrollAfterInteraction = true;
                }

                if (_pendingScrollToEnd && !userBlocks)
                {
                    ScrollToEnd();
                }
                else if (_pendingScrollToEnd && userBlocks)
                {
                    _deferScrollAfterInteraction = true;
                }

                if (_queuedViewportPasses > 0)
                {
                    QueueViewportRefreshPass();
                    return;
                }

                Interlocked.Exchange(ref _viewportRefreshQueued, 0);
                if (_queuedViewportPasses > 0 &&
                    Interlocked.Exchange(ref _viewportRefreshQueued, 1) == 0)
                {
                    QueueViewportRefreshPass();
                }
            }))
            {
                Interlocked.Exchange(ref _viewportRefreshQueued, 0);
            }
        }

        /// <summary>
        /// Forces the native text box and its inner scroll viewer to update layout.
        /// </summary>
        private void RefreshViewport()
        {
            if (PlatformView == null)
            {
                return;
            }

            if (_pointerDownOnConsole)
            {
                return;
            }

            if (PlatformView.SelectionLength > 0 && _pendingScrollToEnd)
            {
                return;
            }

            EnsureScrollViewer();
            PlatformView.UpdateLayout();
            _scrollViewer?.UpdateLayout();
        }

        /// <summary>
        /// Scrolls the inner viewer to the end without moving the caret, unless the scroll viewer is not ready yet.
        /// </summary>
        private void ScrollToEnd()
        {
            if (PlatformView == null)
            {
                return;
            }

            if (UserBlocksAutoscrollForScroll())
            {
                _deferScrollAfterInteraction = true;
                return;
            }

            if (_scrollViewer != null)
            {
                _isApplyingProgrammaticScroll = true;
                _scrollViewer.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
                _isApplyingProgrammaticScroll = false;
            }
            else
            {
                var textLength = PlatformView.Text?.Length ?? 0;
                PlatformView.Select(textLength, 0);
            }

            _isNearBottom = true;
            _pendingScrollToEnd = false;
            _deferScrollAfterInteraction = false;
        }

        /// <summary>
        /// Determines whether the current viewport is already close enough to the bottom to keep autoscroll enabled.
        /// </summary>
        private bool IsNearBottom()
        {
            EnsureScrollViewer();

            return _scrollViewer == null ||
                   _scrollViewer.ScrollableHeight <= 0 ||
                   _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 8;
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

            var resolvedScrollViewer = FindDescendantScrollViewer(PlatformView);
            if (ReferenceEquals(_scrollViewer, resolvedScrollViewer))
            {
                return;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
            }

            _scrollViewer = resolvedScrollViewer;
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
                _isNearBottom = IsNearBottom();
            }
        }


        // Visual tree

        /// <summary>
        /// Recursively searches the WinUI visual tree for the first nested scroll viewer.
        /// </summary>
        private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
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
        /// Native console <see cref="TextBox"/> that caps how often mouse-move updates reach WinUI text selection during LMB drags.
        /// </summary>
        private sealed class ConsoleOutputHostTextBox : TextBox
        {
            /// <summary>Minimum milliseconds between forwarded <see cref="UIElement.PointerMoved"/> events while selecting with the mouse.</summary>
            private const int PointerMoveMinIntervalMs = 10;

            private long _lastForwardedPointerMoveTicks;

            protected override void OnPointerPressed(PointerRoutedEventArgs e)
            {
                // Allow the first move after a press to reach the base implementation even if a prior gesture forwarded recently.
                _lastForwardedPointerMoveTicks = Environment.TickCount64 - PointerMoveMinIntervalMs;
                base.OnPointerPressed(e);
            }

            protected override void OnPointerMoved(PointerRoutedEventArgs e)
            {
                // WinRT projects PointerDeviceType under a distinct enum type from Windows.Devices.Input; values align with Mouse == 0.
                if ((int)e.Pointer.PointerDeviceType != (int)Windows.Devices.Input.PointerDeviceType.Mouse)
                {
                    _lastForwardedPointerMoveTicks = Environment.TickCount64;
                    base.OnPointerMoved(e);
                    return;
                }

                var current = e.GetCurrentPoint(this);
                if (!current.Properties.IsLeftButtonPressed)
                {
                    _lastForwardedPointerMoveTicks = Environment.TickCount64;
                    base.OnPointerMoved(e);
                    return;
                }

                var now = Environment.TickCount64;
                var elapsed = now - _lastForwardedPointerMoveTicks;
                if (elapsed >= PointerMoveMinIntervalMs || elapsed < 0)
                {
                    _lastForwardedPointerMoveTicks = now;
                    base.OnPointerMoved(e);
                    return;
                }

                // Do not invoke TextBox pointer-move selection for this report (high-Hz mice).
                e.Handled = true;
            }
        }
    }
}
#endif
