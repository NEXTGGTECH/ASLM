// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM
{
    /// <summary>
    /// Verifies the shipped MAUI window and sandboxed WebKit engine without starting modules or external services.
    /// </summary>
    internal sealed class LinuxCompatibilityPage : ContentPage
    {
        // Verification state

        /// <summary>
        /// Whether this invocation requested the release workflow's desktop compatibility check.
        /// </summary>
        internal static bool Enabled { get; set; }

        /// <summary>
        /// Browser created through the same MAUI handler as normal application pages.
        /// </summary>
        private readonly WebView _browser;

        /// <summary>
        /// Prevents duplicate navigation events from completing the same check twice.
        /// </summary>
        private bool _completed;


        // Window and browser check

        /// <summary>
        /// Loads a local document whose JavaScript result is checked after WebKit reports navigation completion.
        /// </summary>
        internal LinuxCompatibilityPage()
        {
            Title = "ASLM compatibility check";
            _browser = new WebView();
            _browser.Navigated += VerifyAsync;
            Content = _browser;
            _browser.Source = new HtmlWebViewSource
            {
                Html = """
                    <!doctype html><html><body><p id="result">pending</p>
                    <script>document.getElementById('result').textContent = String(6 * 7);</script>
                    </body></html>
                    """
            };
        }

        /// <summary>
        /// Requires a mapped GTK window and a real JavaScript result before reporting success and exiting.
        /// </summary>
        private async void VerifyAsync(object? sender, WebNavigatedEventArgs args)
        {
            if (_completed) return;
            _completed = true;
            try
            {
                if (args.Result != WebNavigationResult.Success || _browser.Handler?.PlatformView is not WebKit.WebView webkit)
                    throw new InvalidOperationException("WebKit could not load the local verification page.");

                // Navigation can finish before the first frame reaches the display server.
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (Window?.Handler?.PlatformView is not Gtk.Window window || !window.GetMapped())
                {
                    if (DateTime.UtcNow >= deadline)
                        throw new InvalidOperationException("The ASLM GTK window did not become visible.");
                    await Task.Delay(100);
                }

                using var result = await webkit.EvaluateJavascriptAsync("document.getElementById('result').textContent");
                if (result?.ToString() != "42")
                    throw new InvalidOperationException("WebKit did not return the expected JavaScript result.");
                Console.WriteLine("ASLM_LINUX_COMPATIBILITY_OK");
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ASLM Linux compatibility check failed: " + ex);
                Environment.ExitCode = 1;
            }
            finally { Dispatcher.Dispatch(() => Application.Current?.Quit()); }
        }
    }
}
