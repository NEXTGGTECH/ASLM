// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Controls;
using System.Text;

namespace ASLM.Controls.Downloads;

/// <summary>
/// Displays passive repository documentation at content height inside the parent's custom scroll area.
/// </summary>
public sealed class RepositoryDocumentView : ContentView
{
    private readonly WebView _browser;
    private string? _fragment;
    private string? _rendered;
    private bool _attached;
#if WINDOWS
    private DownloadInfoPreviewHost? _preview;
#elif MACCATALYST
    private IDisposable? _sizeObserver;
    private WebKit.WKWebView? _webKit;
    private string? _loadedHtml;
#elif LINUX
    private LinuxDocumentPreview? _linuxPreview;
    private object? _linuxBrowser;
#endif

    /// <summary>
    /// Creates one browser and binds native resources to its visual lifecycle.
    /// </summary>
    public RepositoryDocumentView()
    {
        _browser = new WebView { HeightRequest = 1, FlowDirection = FlowDirection.LeftToRight };
        Content = _browser;
        _browser.HandlerChanged += OnHandlerChanged;
        _browser.Navigating += OnNavigating;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Replaces the selected document without retaining another module's content during a network request.
    /// </summary>
    public void SetDocument(string? html)
    {
        _fragment = html;
        _rendered = null;
        _browser.HeightRequest = 1;
        IsVisible = html != null;
        Render();
    }

    /// <summary>
    /// Subscribes to palette changes only while the preview is attached.
    /// </summary>
    private void OnLoaded(object? sender, EventArgs e)
    {
        _attached = true;
        ThemeService.PaletteApplied -= OnPaletteApplied;
        ThemeService.PaletteApplied += OnPaletteApplied;
        if (Application.Current is { } app)
        {
            app.RequestedThemeChanged -= OnRequestedThemeChanged;
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        }
        ConnectBrowser();
        Render();
    }

    /// <summary>
    /// Releases native observers so hidden overlays do not keep measuring repository documents.
    /// </summary>
    private void OnUnloaded(object? sender, EventArgs e)
    {
        _attached = false;
        ThemeService.PaletteApplied -= OnPaletteApplied;
        if (Application.Current is { } app) app.RequestedThemeChanged -= OnRequestedThemeChanged;
#if WINDOWS
        _preview?.Dispose();
        _preview = null;
#elif MACCATALYST
        _sizeObserver?.Dispose();
        _sizeObserver = null;
        _webKit = null;
        _loadedHtml = null;
#elif LINUX
        _linuxPreview?.Dispose();
        _linuxPreview = null;
        _linuxBrowser = null;
#endif
    }

    /// <summary>
    /// Reconnects measurement when MAUI replaces the platform browser.
    /// </summary>
    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        ConnectBrowser();
        Render();
    }

    /// <summary>
    /// Reuses the download preview host on Windows and observes native WebKit geometry on macOS.
    /// </summary>
    private void ConnectBrowser()
    {
        if (!_attached) return;
#if WINDOWS
        var native = _browser.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.WebView2;
        if (ReferenceEquals(native, _preview?.PlatformView)) return;
        _preview?.Dispose();
        _preview = native == null ? null : new DownloadInfoPreviewHost(native, Dispatcher, SetHeight);
#elif MACCATALYST
        var native = _browser.Handler?.PlatformView as WebKit.WKWebView;
        if (ReferenceEquals(native, _webKit)) return;
        _sizeObserver?.Dispose();
        _sizeObserver = null;
        _webKit = native;
        _loadedHtml = null;
        if (native != null)
        {
            // The outer MAUI ScrollView owns scrolling; images and window resizing may change document height later.
            native.ScrollView.ScrollEnabled = false;
            _sizeObserver = native.ScrollView.AddObserver("contentSize", Foundation.NSKeyValueObservingOptions.New,
                _ => Dispatcher.Dispatch(() =>
                {
                    if (ReferenceEquals(native, _webKit)) SetHeight(native.ScrollView.ContentSize.Height);
                }));
        }
#elif LINUX
        var native = _browser.Handler?.PlatformView;
        if (ReferenceEquals(native, _linuxBrowser)) return;
        _linuxPreview?.Dispose();
        _linuxBrowser = native;
        _linuxPreview = LinuxDocumentPreview.Create(_browser, SetHeight);
#endif
    }

    /// <summary>
    /// Accepts finite document dimensions and avoids re-layout for subpixel changes.
    /// </summary>
    private void SetHeight(double height)
    {
        if (_attached && _fragment != null && DownloadInfoPreview.IsValidHeight(height) &&
            Math.Abs(_browser.HeightRequest - height) >= .5)
            _browser.HeightRequest = height;
    }

    /// <summary>
    /// Regenerates only the presentation wrapper when the user changes the palette.
    /// </summary>
    private void OnPaletteApplied() => Dispatcher.Dispatch(() => { _rendered = null; Render(); });

    /// <summary>
    /// Applies system theme transitions in the same way as explicit palette changes.
    /// </summary>
    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => OnPaletteApplied();

    /// <summary>
    /// Renders with LabelPrimary and BackgroundSecondary; no GitHub request is needed for a theme change.
    /// </summary>
    private void Render()
    {
        if (!_attached) return;
        if (_fragment == null)
        {
            // Empty/closed previews must not keep the native layout timer running.
#if WINDOWS
            _preview?.Pause();
#elif MACCATALYST
            _webKit?.StopLoading();
            _loadedHtml = null;
#else
            _browser.Source = null;
#endif
            return;
        }
        _rendered ??= RepositoryDocumentRenderer.Build(_fragment,
            IconTintHelper.ResolvePaletteColor("LabelPrimary").ToHex(),
            IconTintHelper.ResolvePaletteColor("BackgroundSecondary").ToHex(),
            IconTintHelper.ResolvePaletteColor("ActionBlue").ToHex(),
            IconTintHelper.ResolvePaletteColor("Separator").ToHex());
#if WINDOWS
        _preview?.SetHtml(_rendered);
#elif MACCATALYST
        if (_webKit != null && _loadedHtml != _rendered)
        {
            // MAUI's HtmlWebViewSource defaults to the local app bundle URL. An explicit null WebKit base
            // keeps this passive document at about:blank without granting it a local-file navigation exception.
            _loadedHtml = _rendered;
            _webKit.LoadHtmlString(_rendered, null!);
        }
#else
        // Avoid navigating again for a repeat native lifecycle notification.
        if (_browser.Source is not HtmlWebViewSource current || current.Html != _rendered)
            _browser.Source = new HtmlWebViewSource { Html = _rendered };
#endif
    }

    /// <summary>
    /// Keeps document links in the external browser and rejects local/custom URI schemes.
    /// </summary>
    private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // NavigateToString announces its HTML as a data URI before the resulting page becomes about:blank.
        // Canceling that initial navigation leaves an empty browser at its initial one-pixel height.
        if (IsDocumentNavigation(e.Url, _rendered)) return;
        e.Cancel = true;
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { await Launcher.Default.OpenAsync(uri); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not open document link: {ex.Message}"); }
    }

    /// <summary>
    /// Permits the current host-generated HTML and its internal anchors without allowing arbitrary data documents.
    /// </summary>
    internal static bool IsDocumentNavigation(string? url, string? html)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url == "about:blank" || url.StartsWith("about:blank#", StringComparison.Ordinal)) return true;

        // Compare the full UTF-8 payload, not just the scheme: old selections and repository-provided data links
        // must not replace the current document or bypass the host's content-security policy.
        const string prefix = "data:text/html;charset=utf-8;base64,";
        return html != null && url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            url.AsSpan(prefix.Length).SequenceEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes(html)));
    }
}
