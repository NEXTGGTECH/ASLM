// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Controls.Downloads;

namespace ASLM.Services.Controls;

/// <summary>
/// Measures a WebKit document inside ASLM's outer scroll view without a permanent polling timer.
/// </summary>
internal sealed class LinuxDocumentPreview : IDisposable
{
    private readonly WebView _view;
    private readonly WebKit.WebView _browser;
    private readonly Action<double> _setHeight;
    private bool _disposed;
    private bool _pending;
    private int _documentVersion;
    private double _width;

    /// <summary>
    /// Connects document completion and viewport-width changes to deferred measurement.
    /// </summary>
    private LinuxDocumentPreview(WebView view, Action<double> setHeight)
    {
        _view = view;
        _browser = (WebKit.WebView)view.Handler!.PlatformView!;
        _setHeight = setHeight;
        _browser.OnLoadChanged += OnLoadChanged;
        _view.SizeChanged += OnSizeChanged;
        RequestMeasurement();
    }

    /// <summary>
    /// Connects a GTK browser without exposing native WebKit types to shared pages.
    /// </summary>
    internal static LinuxDocumentPreview? Create(WebView view, Action<double> setHeight) =>
        view.Handler?.PlatformView is WebKit.WebView ? new LinuxDocumentPreview(view, setHeight) : null;

    /// <summary>
    /// Invalidates old JavaScript results when WebKit begins another document.
    /// </summary>
    private void OnLoadChanged(WebKit.WebView sender, WebKit.WebView.LoadChangedSignalArgs args)
    {
        if (args.LoadEvent == WebKit.LoadEvent.Started) _documentVersion++;
        if (args.LoadEvent == WebKit.LoadEvent.Finished) RequestMeasurement();
    }

    /// <summary>
    /// Remeasures wrapping only for width changes, avoiding a height-measurement feedback loop.
    /// </summary>
    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Math.Abs(_view.Width - _width) < .5) return;
        _width = _view.Width;
        RequestMeasurement();
    }

    /// <summary>
    /// Coalesces native layout notifications on the owning UI dispatcher.
    /// </summary>
    private void RequestMeasurement()
    {
        if (_disposed || _pending) return;
        _pending = true;
        _view.Dispatcher.Dispatch(async () =>
        {
            _pending = false;
            if (!_disposed) await MeasureAsync();
        });
    }

    /// <summary>
    /// Reads content bounds rather than viewport height so shorter documents can shrink again.
    /// </summary>
    private async Task MeasureAsync()
    {
        var version = _documentVersion;
        try
        {
            // Host evaluation does not enable scripts supplied by a repository; its CSP remains unchanged.
            using var result = await _browser.EvaluateJavascriptAsync("""
                (() => {
                    if (!document.body) return 1;
                    document.documentElement.style.overflow = 'hidden';
                    document.body.style.overflow = 'hidden';
                    window.scrollTo(0, 0);
                    let bottom = document.body.getBoundingClientRect().bottom;
                    for (const element of document.body.querySelectorAll('*')) {
                        const box = element.getBoundingClientRect();
                        const margin = parseFloat(getComputedStyle(element).marginBottom) || 0;
                        bottom = Math.max(bottom, box.bottom + Math.max(0, margin));
                    }
                    return Math.max(1, Math.ceil(bottom));
                })()
                """);
            if (!_disposed && version == _documentVersion &&
                double.TryParse(result?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var height) &&
                DownloadInfoPreview.IsValidHeight(height) && Math.Abs(_view.HeightRequest - height) >= .5)
            {
                _setHeight(height);
                // This backend does not propagate a child's MAUI invalidation to GTK root layout drivers.
                // Mark the existing ancestors dirty only when the measured document actually changes size.
                for (Gtk.Widget? widget = _browser; widget != null; widget = widget.GetParent())
                {
                    if (widget is Microsoft.Maui.Platforms.Linux.Gtk4.Platform.GtkLayoutPanel panel)
                        panel.LayoutDirty = true;
                }
                _browser.QueueResize();
            }
        }
        catch (Exception ex)
        {
            // Navigating or destroying a browser can cancel an outstanding WebKit evaluation.
            System.Diagnostics.Debug.WriteLine($"[LinuxDocumentPreview] Measurement interrupted: {ex.Message}");
        }
    }

    /// <summary>
    /// Releases event subscriptions and rejects in-flight results from a closed preview.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _documentVersion++;
        _browser.OnLoadChanged -= OnLoadChanged;
        _view.SizeChanged -= OnSizeChanged;
    }
}
