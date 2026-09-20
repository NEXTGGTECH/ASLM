// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace ASLM.Controls.Downloads;

/// <summary>Owns the native subscriptions and coalesces layout notifications on the UI dispatcher.</summary>
internal sealed class DownloadInfoPreviewHost : IDisposable
{
    private readonly WebView2 _view;
    private readonly Action<double> _setHeight;
    private readonly IDispatcherTimer _timer;
    private readonly PointerEventHandler _wheelHandler = OnPointerWheelChanged;
    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _layoutEvents;
    private CoreWebView2DevToolsProtocolEventReceiver? _documentEvents;
    private DownloadInfoPreview? _preview;
    private Task _initialization = Task.CompletedTask;
    private string? _source;
    private int _generation;
    private int _navigationVersion;
    private bool _active;
    private bool _disposed;
    private bool _measuring;
    private bool _pending;

    public DownloadInfoPreviewHost(WebView2 view, IDispatcher dispatcher, Action<double> setHeight)
    {
        _view = view;
        _setHeight = setHeight;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.IsRepeating = false;
        _timer.Tick += OnMeasureTick;
        view.CoreWebView2Initialized += OnCoreInitialized;
        view.SizeChanged += OnSizeChanged;
        ConnectCore();
    }

    public WebView2 PlatformView => _view;

    public void SetSource(string? source)
    {
        if (_active && string.Equals(_source, source, StringComparison.Ordinal))
        {
            RequestMeasure();
            return;
        }

        _source = source;
        _active = source != null;
        _generation++;
        _navigationVersion++;
        _preview?.ResetDocument();
        _timer.Stop();
        _pending = false;
        if (_active) _ = NavigateAsync(_navigationVersion);
    }

    private async Task NavigateAsync(int version)
    {
        try
        {
            await _view.EnsureCoreWebView2Async();
            await _initialization;
            if (_disposed || !_active || version != _navigationVersion || _core == null) return;

            // WinUI's Source (Uri) round-trip can corrupt UTF-8 escapes in local paths.
            // Navigate with the original escaped string instead, keeping Unicode intact.
            _core.Navigate(_source);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Download preview navigation failed: {ex.Message}");
        }
    }

    public void Pause()
    {
        _active = false;
        _generation++;
        _navigationVersion++;
        _pending = false;
        _timer.Stop();
    }

    private void OnCoreInitialized(WebView2 sender, CoreWebView2InitializedEventArgs args) => ConnectCore();

    private void ConnectCore()
    {
        var core = _view.CoreWebView2;
        if (_disposed || core == null || ReferenceEquals(core, _core)) return;
        DisconnectCore();
        _core = core;
        _preview = new DownloadInfoPreview(async (method, parameters) =>
            await core.CallDevToolsProtocolMethodAsync(method, parameters));
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

    private static async Task InitializeAsync(DownloadInfoPreview preview)
    {
        try { await preview.InitializeAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Download preview initialization failed: {ex.Message}"); }
    }

    private void OnLayoutChanged(CoreWebView2 sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args) => RequestMeasure();

    private void OnDocumentChanged(CoreWebView2 sender,
        CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        _generation++;
        _preview?.ResetDocument();
        RequestMeasure();
    }

    private void OnDocumentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args) => RequestMeasure();

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        if (args.IsSuccess) RequestMeasure();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.PreviousSize.Width != args.NewSize.Width) RequestMeasure();
    }

    private bool IsCurrentSource =>
        Uri.TryCreate(_source, UriKind.Absolute, out var expected) &&
        Uri.TryCreate(_core?.Source, UriKind.Absolute, out var actual) && expected.Equals(actual);

    private bool CanMeasure => !_disposed && _active && _view.ActualWidth > 0 && IsCurrentSource;

    private void RequestMeasure()
    {
        if (!CanMeasure) return;
        if (_measuring) { _pending = true; return; }
        if (!_timer.IsRunning) _timer.Start();
    }

    private async void OnMeasureTick(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (!CanMeasure || _measuring || _preview is not { } preview) return;
        var generation = _generation;
        _measuring = true;
        _pending = false;
        try
        {
            await _initialization;
            if (!CanMeasure || generation != _generation) return;
            var size = await preview.MeasureAsync();
            if (CanMeasure && generation == _generation)
                _setHeight(size.Height * _view.ActualWidth / size.ViewportWidth);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            preview.ResetDocument();
            System.Diagnostics.Debug.WriteLine($"Download preview measurement failed: {ex.Message}");
        }
        finally
        {
            _measuring = false;
            if (_pending) RequestMeasure();
        }
    }

    private static void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not WebView2 view) return;
        var pointer = args.GetCurrentPoint(view).Properties;
        if (pointer.MouseWheelDelta != 0 && !pointer.IsHorizontalMouseWheel &&
            (args.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) == 0)
            args.Handled = false;
    }

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
