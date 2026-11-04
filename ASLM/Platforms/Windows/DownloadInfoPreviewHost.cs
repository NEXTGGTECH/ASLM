// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Owns the native subscriptions and coalesces layout notifications on the UI dispatcher.
    /// It drives a MAUI-created <see cref="WebView2"/> to show a repository document, measures the document height
    /// through <see cref="DownloadInfoPreview"/> and reports it so the surrounding page can size the browser.
    /// </summary>
    internal sealed class DownloadInfoPreviewHost : IDisposable
    {
        // Fields

        /// <summary>
        /// Native WebView2 control that renders the document.
        /// </summary>
        private readonly WebView2 _view;

        /// <summary>
        /// Callback that applies the measured document height, in control units, to the hosting view.
        /// </summary>
        private readonly Action<double> _setHeight;

        /// <summary>
        /// One-shot timer that delays measurement by a short interval, so bursts of layout events cause one measurement.
        /// </summary>
        private readonly IDispatcherTimer _timer;

        /// <summary>
        /// Wheel handler registered on the view; it hands plain vertical wheel input to the surrounding scroll view.
        /// </summary>
        private readonly PointerEventHandler _wheelHandler = OnPointerWheelChanged;

        /// <summary>
        /// Core browser object of the connected view, or null while none is connected.
        /// </summary>
        private CoreWebView2? _core;

        /// <summary>
        /// DevTools receiver for <c>LayerTree.layerTreeDidChange</c> events, which signal layout changes.
        /// </summary>
        private CoreWebView2DevToolsProtocolEventReceiver? _layoutEvents;

        /// <summary>
        /// DevTools receiver for <c>DOM.documentUpdated</c> events, which signal that the document was replaced.
        /// </summary>
        private CoreWebView2DevToolsProtocolEventReceiver? _documentEvents;

        /// <summary>
        /// Measurement helper bound to the connected core, or null while none is connected.
        /// </summary>
        private DownloadInfoPreview? _preview;

        /// <summary>
        /// Completes when the protocol domains of the connected core are enabled; measurements and navigation wait for it.
        /// </summary>
        private Task _initialization = Task.CompletedTask;

        /// <summary>
        /// URL currently shown or being loaded, or null when nothing is shown.
        /// </summary>
        private string? _source;

        /// <summary>
        /// In-memory HTML document shown instead of the URL, or null when the source is a URL.
        /// </summary>
        private string? _html;

        /// <summary>
        /// Incremented whenever the displayed document changes or pauses, so a measurement of an older document is dropped.
        /// </summary>
        private int _generation;

        /// <summary>
        /// Incremented for every navigation request, so only the latest request navigates.
        /// </summary>
        private int _navigationVersion;

        /// <summary>
        /// Whether a document is selected and measurements are allowed.
        /// </summary>
        private bool _active;

        /// <summary>
        /// Whether the host was disposed; all work stops once it is set.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Whether a measurement is currently running.
        /// </summary>
        private bool _measuring;

        /// <summary>
        /// Whether another measurement was requested while one was running and must follow it.
        /// </summary>
        private bool _pending;


        // Initialization

        /// <summary>
        /// Connects to the view and its core browser, creating the measurement timer on the given dispatcher.
        /// </summary>
        /// <param name="view">Native WebView2 control created by the MAUI handler.</param>
        /// <param name="dispatcher">UI dispatcher that raises the measurement timer.</param>
        /// <param name="setHeight">Callback that applies the measured document height.</param>
        public DownloadInfoPreviewHost(WebView2 view, IDispatcher dispatcher, Action<double> setHeight)
        {
            _view = view;
            _setHeight = setHeight;

            // A 50 ms one-shot timer coalesces bursts of layout events into one measurement.
            _timer = dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(50);
            _timer.IsRepeating = false;
            _timer.Tick += OnMeasureTick;

            // The core browser may not exist yet; connect now if it does, otherwise when it is initialized.
            view.CoreWebView2Initialized += OnCoreInitialized;
            view.SizeChanged += OnSizeChanged;
            ConnectCore();
        }

        /// <summary>
        /// Native WebView2 control this host drives; the owner compares it to detect a replaced handler.
        /// </summary>
        public WebView2 PlatformView => _view;


        // Navigation

        /// <summary>
        /// Selects a local or remote preview URL, clearing any previous in-memory document.
        /// </summary>
        /// <param name="source">URL to show, or null to deactivate the preview.</param>
        public void SetSource(string? source)
        {
            // Showing the same URL again only needs a fresh measurement.
            if (_active && _html == null && string.Equals(_source, source, StringComparison.Ordinal))
            {
                RequestMeasure();
                return;
            }

            // Switch documents: invalidate older measurements and navigations, then load the new URL.
            _html = null;
            _source = source;
            _active = source != null;
            _generation++;
            _navigationVersion++;
            _preview?.ResetDocument();
            _timer.Stop();
            _pending = false;
            if (_active) _ = NavigateAsync(_navigationVersion);
        }

        /// <summary>
        /// Displays a host-generated document using the same native measurement and wheel forwarding as URL previews.
        /// </summary>
        /// <param name="html">Complete HTML document to display.</param>
        public void SetHtml(string html)
        {
            // Showing the same document again only needs a fresh measurement.
            if (_active && _html == html) { RequestMeasure(); return; }

            // NavigateToString avoids temporary files and the Unicode path round-trip in WinUI.Source.
            _html = html;
            _source = "about:blank";
            _active = true;
            _generation++;
            _navigationVersion++;
            _preview?.ResetDocument();
            _timer.Stop();
            _pending = false;
            _ = NavigateAsync(_navigationVersion);
        }

        /// <summary>
        /// Navigates only the latest request after the native browser and measurement protocol are ready.
        /// </summary>
        /// <param name="version">Navigation version of the request; a newer request makes this one a no-op.</param>
        private async Task NavigateAsync(int version)
        {
            try
            {
                // Wait for the browser and the protocol setup, then drop the request if it became outdated meanwhile.
                await _view.EnsureCoreWebView2Async();
                await _initialization;
                if (_disposed || !_active || version != _navigationVersion || _core == null) return;

                // WinUI's Source (Uri) round-trip can corrupt UTF-8 escapes in local paths.
                // Navigate with the original escaped string instead, keeping Unicode intact.
                if (_html != null) _core.NavigateToString(_html);
                else _core.Navigate(_source);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Download preview navigation failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Deactivates the preview and cancels pending navigation and measurement work without disposing the host.
        /// </summary>
        public void Pause()
        {
            _active = false;
            _generation++;
            _navigationVersion++;
            _pending = false;
            _timer.Stop();
        }


        // Browser connection

        /// <summary>
        /// Connects to the core browser once WebView2 finishes creating it.
        /// </summary>
        private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args) => ConnectCore();

        /// <summary>
        /// Binds the host to the view's current core browser: replaces any previous connection, subscribes to layout
        /// and navigation events and starts the DevTools protocol setup.
        /// </summary>
        private void ConnectCore()
        {
            // Nothing to do without a core browser or when this one is already connected.
            var core = _view.CoreWebView2;
            if (_disposed || core == null || ReferenceEquals(core, _core)) return;
            DisconnectCore();
            _core = core;
            _preview = new DownloadInfoPreview(async (method, parameters) =>
                await core.CallDevToolsProtocolMethodAsync(method, parameters));

            // Layout and document-replacement events trigger measurements without polling.
            _layoutEvents = core.GetDevToolsProtocolEventReceiver("LayerTree.layerTreeDidChange");
            _documentEvents = core.GetDevToolsProtocolEventReceiver("DOM.documentUpdated");
            _layoutEvents.DevToolsProtocolEventReceived += OnLayoutChanged;
            _documentEvents.DevToolsProtocolEventReceived += OnDocumentChanged;
            core.DOMContentLoaded += OnDocumentLoaded;
            core.NavigationCompleted += OnNavigationCompleted;

            // WebView2 registers its wheel handler during core creation; ours must run after it.
            _view.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
            _view.AddHandler(UIElement.PointerWheelChangedEvent, _wheelHandler, true);
            _initialization = InitializeAsync(_preview);
            RequestMeasure();
        }

        /// <summary>
        /// Runs the DevTools protocol setup of one preview and logs a failure instead of throwing,
        /// so a failed setup never breaks navigation, which awaits it.
        /// </summary>
        private static async Task InitializeAsync(DownloadInfoPreview preview)
        {
            try { await preview.InitializeAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Download preview initialization failed: {ex.Message}"); }
        }


        // Layout events

        /// <summary>
        /// Requests a measurement when the browser reports a layout tree change.
        /// </summary>
        private void OnLayoutChanged(CoreWebView2 sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs args) => RequestMeasure();

        /// <summary>
        /// Invalidates measurements of the replaced document and measures the new one.
        /// </summary>
        private void OnDocumentChanged(CoreWebView2 sender,
            CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
        {
            _generation++;
            _preview?.ResetDocument();
            RequestMeasure();
        }

        /// <summary>
        /// Requests a measurement once the DOM content of the page is loaded.
        /// </summary>
        private void OnDocumentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => RequestMeasure();

        /// <summary>
        /// Requests a measurement after a navigation completed successfully.
        /// </summary>
        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) RequestMeasure();
        }

        /// <summary>
        /// Requests a measurement when the control width changes, because text reflows with the width.
        /// </summary>
        private void OnSizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (args.PreviousSize.Width != args.NewSize.Width) RequestMeasure();
        }


        // Measurement

        /// <summary>
        /// Whether the browser currently shows the requested source, so a measurement cannot read a stale page.
        /// </summary>
        private bool IsCurrentSource =>
            Uri.TryCreate(_source, UriKind.Absolute, out var expected) &&
            Uri.TryCreate(_core?.Source, UriKind.Absolute, out var actual) && expected.Equals(actual);

        /// <summary>
        /// Whether a measurement is meaningful: the host is alive and active, the control has a width
        /// and the browser shows the requested source.
        /// </summary>
        private bool CanMeasure => !_disposed && _active && _view.ActualWidth > 0 && IsCurrentSource;

        /// <summary>
        /// Schedules a measurement on the timer; a request that arrives during a running measurement is remembered
        /// and handled after it.
        /// </summary>
        private void RequestMeasure()
        {
            if (!CanMeasure) return;
            if (_measuring) { _pending = true; return; }
            if (!_timer.IsRunning) _timer.Start();
        }

        /// <summary>
        /// Measures the document when the timer fires and reports its height, unless the document changed meanwhile.
        /// </summary>
        private async void OnMeasureTick(object? sender, EventArgs args)
        {
            // The timer is one-shot. Skip when a measurement is not possible or already running;
            // a request made during a running measurement is covered by the pending flag.
            _timer.Stop();
            if (!CanMeasure || _measuring || _preview is not { } preview) return;
            var generation = _generation;
            _measuring = true;
            _pending = false;
            try
            {
                // Apply the result only if the same document is still displayed; the browser viewport width
                // can differ from the control width, so the height is scaled to the control width.
                await _initialization;
                if (!CanMeasure || generation != _generation) return;
                var size = await preview.MeasureAsync();
                if (CanMeasure && generation == _generation)
                    _setHeight(size.Height * _view.ActualWidth / size.ViewportWidth);
            }
            catch (OperationCanceledException)
            {
                // The document was replaced during the measurement, so its result is obsolete.
            }
            catch (Exception ex)
            {
                preview.ResetDocument();
                System.Diagnostics.Debug.WriteLine($"Download preview measurement failed: {ex.Message}");
            }
            finally
            {
                // Run the measurement that was requested while this one was in progress.
                _measuring = false;
                if (_pending) RequestMeasure();
            }
        }


        // Wheel forwarding

        /// <summary>
        /// Marks plain vertical wheel input as unhandled so the surrounding scroll view scrolls the page
        /// instead of the embedded browser; horizontal wheel and Ctrl/Shift (zoom, horizontal scroll) stay with the browser.
        /// </summary>
        private static void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
        {
            if (sender is not WebView2 view) return;
            var pointer = args.GetCurrentPoint(view).Properties;
            if (pointer.MouseWheelDelta != 0 && !pointer.IsHorizontalMouseWheel &&
                (args.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) == 0)
                args.Handled = false;
        }


        // Disposal

        /// <summary>
        /// Unsubscribes from the connected core browser and drops all references to it.
        /// </summary>
        private void DisconnectCore()
        {
            _generation++;
            _preview?.ResetDocument();
            if (_layoutEvents != null) _layoutEvents.DevToolsProtocolEventReceived -= OnLayoutChanged;
            if (_documentEvents != null) _documentEvents.DevToolsProtocolEventReceived -= OnDocumentChanged;
            if (_core != null)
            {
                _core.DOMContentLoaded -= OnDocumentLoaded;
                _core.NavigationCompleted -= OnNavigationCompleted;
            }

            _layoutEvents = null;
            _documentEvents = null;
            _core = null;
            _preview = null;
        }

        /// <summary>
        /// Stops all work and detaches every handler this host registered on the view.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            Pause();
            _disposed = true;
            DisconnectCore();
            _timer.Tick -= OnMeasureTick;
            _view.CoreWebView2Initialized -= OnCoreInitialized;
            _view.SizeChanged -= OnSizeChanged;
            _view.RemoveHandler(UIElement.PointerWheelChangedEvent, _wheelHandler);
        }
    }
}
