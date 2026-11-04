// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Controls;
using System.Text;

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Displays passive repository documentation at content height inside the parent's custom scroll area.
    /// </summary>
    public sealed class RepositoryDocumentView : ContentView
    {
        // Fields

        /// <summary>
        /// Web view that renders the document; its height is driven by the measured content height.
        /// </summary>
        private readonly WebView _browser;

        /// <summary>
        /// Repository document body to display, or null when no document is selected.
        /// </summary>
        private string? _fragment;

        /// <summary>
        /// Complete themed HTML built from <see cref="_fragment"/>, or null when it must be rebuilt
        /// (new document or palette).
        /// </summary>
        private string? _rendered;

        /// <summary>
        /// Whether the view is currently loaded, so native observers and rendering are only active on screen.
        /// </summary>
        private bool _attached;

#if WINDOWS
        /// <summary>
        /// Host that measures the WebView2 document height and forwards wheel input, or null while unattached.
        /// </summary>
        private DownloadInfoPreviewHost? _preview;
#elif MACCATALYST
        /// <summary>
        /// Key-value observer that reports WebKit content size changes, or null while unattached.
        /// </summary>
        private IDisposable? _sizeObserver;

        /// <summary>
        /// Native WebKit view whose content size is observed, or null while unattached.
        /// </summary>
        private WebKit.WKWebView? _webKit;

        /// <summary>
        /// HTML last loaded into WebKit, used to skip repeated loads of an unchanged document.
        /// </summary>
        private string? _loadedHtml;
#elif LINUX
        /// <summary>
        /// Helper that measures the document height on Linux, or null while unattached.
        /// </summary>
        private LinuxDocumentPreview? _linuxPreview;

        /// <summary>
        /// Native browser the Linux preview is currently connected to, used to detect a replaced handler.
        /// </summary>
        private object? _linuxBrowser;
#endif


        // Initialization

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


        // Document content

        /// <summary>
        /// Replaces the selected document without retaining another module's content during a network request.
        /// </summary>
        /// <param name="html">Document body to display, or null to clear and hide the view.</param>
        public void SetDocument(string? html)
        {
            // Reset the height to one pixel so the previous document's size never lingers, then render the new one.
            _fragment = html;
            _rendered = null;
            _browser.HeightRequest = 1;
            IsVisible = html != null;
            Render();
        }


        // Lifecycle

        /// <summary>
        /// Subscribes to palette changes only while the preview is attached.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            // Unsubscribe first so repeated Loaded events never register a handler twice.
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

            // Dispose the platform-specific measurement hooks created in ConnectBrowser.
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


        // Browser connection

        /// <summary>
        /// Reuses the download preview host on Windows and observes native WebKit geometry on macOS.
        /// </summary>
        private void ConnectBrowser()
        {
            // Each platform reconnects only when the native browser instance actually changed.
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


        // Sizing

        /// <summary>
        /// Accepts finite document dimensions and avoids re-layout for subpixel changes.
        /// </summary>
        private void SetHeight(double height)
        {
            if (_attached && _fragment != null && DownloadInfoPreview.IsValidHeight(height) &&
                Math.Abs(_browser.HeightRequest - height) >= .5)
                _browser.HeightRequest = height;
        }


        // Rendering

        /// <summary>
        /// Regenerates only the presentation wrapper when the user changes the palette.
        /// </summary>
        private void OnPaletteApplied() => Dispatcher.Dispatch(() => { _rendered = null; Render(); });

        /// <summary>
        /// Applies system theme transitions in the same way as explicit palette changes.
        /// </summary>
        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => OnPaletteApplied();

        /// <summary>
        /// Renders with the LabelPrimary, BackgroundSecondary, ActionBlue and Separator palette colors;
        /// no GitHub request is needed for a theme change.
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

            // Build the themed page once per document and palette, using the current theme colors.
            _rendered ??= RepositoryDocumentRenderer.Build(_fragment,
                IconTintHelper.ResolvePaletteColor("LabelPrimary").ToHex(),
                IconTintHelper.ResolvePaletteColor("BackgroundSecondary").ToHex(),
                IconTintHelper.ResolvePaletteColor("ActionBlue").ToHex(),
                IconTintHelper.ResolvePaletteColor("Separator").ToHex());

            // Hand the document to the native browser of the current platform.
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


        // Navigation

        /// <summary>
        /// Keeps document links in the external browser and rejects local/custom URI schemes.
        /// </summary>
        private async void OnNavigating(object? sender, WebNavigatingEventArgs e)
        {
            // NavigateToString announces its HTML as a data URI before the resulting page becomes about:blank.
            // Canceling that initial navigation leaves an empty browser at its initial one-pixel height.
            if (IsDocumentNavigation(e.Url, _rendered)) return;
            e.Cancel = true;

            // Only web links leave the document, and they open in the system browser.
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
}
