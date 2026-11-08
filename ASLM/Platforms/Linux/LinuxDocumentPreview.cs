// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Controls.Downloads;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Measures a WebKit document inside ASLM's outer scroll view without a permanent polling timer.
    /// </summary>
    internal sealed class LinuxDocumentPreview : IDisposable
    {
        // Fields

        /// <summary>
        /// MAUI web view whose document height is measured.
        /// </summary>
        private readonly WebView _view;

        /// <summary>
        /// Native WebKit browser behind <see cref="_view"/>, used for loading events and script evaluation.
        /// </summary>
        private readonly WebKit.WebView _browser;

        /// <summary>
        /// Callback that applies the measured document height to the owning page.
        /// </summary>
        private readonly Action<double> _setHeight;

        /// <summary>
        /// Set by <see cref="Dispose"/>; pending and in-flight measurements are then ignored.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Whether a measurement is already queued on the dispatcher.
        /// </summary>
        private bool _pending;

        /// <summary>
        /// Incremented whenever a new document starts loading or the preview is disposed, so results measured
        /// for an older document can be recognized and discarded.
        /// </summary>
        private int _documentVersion;

        /// <summary>
        /// Last view width that triggered a measurement.
        /// </summary>
        private double _width;


        // Initialization

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


        // Measurement triggers

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


        // Measurement

        /// <summary>
        /// Reads content bounds rather than viewport height so shorter documents can shrink again.
        /// </summary>
        private async Task MeasureAsync()
        {
            // Remember which document is measured, so a result that arrives after a navigation is discarded.
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

                // Apply the height only for the same document, when it parses and when it differs noticeably
                // from the current request; this stops the layout from re-triggering itself.
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


        // Disposal

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
}
