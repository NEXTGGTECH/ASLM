// Copyright NEXTGGTECH. Apache License 2.0.

using GtkApplicationHandler = Microsoft.Maui.Platforms.Linux.Gtk4.Handlers.ApplicationHandler;

namespace ASLM
{
    /// <summary>
    /// Linux part: handles explicit quit commands and window-manager close within the GTK application lifetime.
    /// </summary>
    public partial class App
    {
        // Fields

        /// <summary>
        /// Set once an explicit quit has started, so repeated Terminate commands do not run the cleanup twice.
        /// </summary>
        private bool _linuxQuitRequested;


        // Application commands

        /// <summary>
        /// Completes the GTK preview's missing Application.Quit command without replacing its window lifecycle handler.
        /// </summary>
        internal static void ConfigureLinuxLifetime()
        {
            GtkApplicationHandler.CommandMapper["Terminate"] = static (_, application, _) =>
            {
                if (application is App app) _ = app.QuitLinuxAsync();
            };
        }

        /// <summary>
        /// Keeps GTK dispatching during cleanup, then closes native windows and exits the actual application loop.
        /// </summary>
        private async Task QuitLinuxAsync()
        {
            // Run the quit sequence only once, even if the command arrives repeatedly.
            if (_linuxQuitRequested) return;
            _linuxQuitRequested = true;
            var window = Windows.FirstOrDefault();

            // Hold the GTK application so its main loop survives until the asynchronous cleanup has finished.
            var native = GetLinuxApplication(window);
            native.Hold();
            try
            {
                await ShutdownAsync(window);

                // The backend application handler owns a placeholder Gtk.Application, not the running instance.
                // Close the real windows only after services have finished using the UI dispatcher.
                foreach (var current in Windows.ToArray())
                    if (current.Handler?.PlatformView is Gtk.Window platformWindow) platformWindow.Close();
                native.Quit();
            }
            catch (Exception ex)
            {
                // Allow another quit attempt after a failed one instead of leaving the app stuck.
                System.Diagnostics.Debug.WriteLine($"[LinuxLifetime] Quit failed: {ex}");
                _linuxQuitRequested = false;
            }
            finally { native.Release(); }
        }


        // Window-manager close

        /// <summary>
        /// Lets the window close immediately while retaining the GTK loop until asynchronous service cleanup completes.
        /// </summary>
        private async void OnLinuxWindowDestroying(object? sender, EventArgs args)
        {
            var window = sender as Window;
            var native = GetLinuxApplication(window);
            native.Hold();
            try { await ShutdownAsync(window); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[LinuxLifetime] Close cleanup failed: {ex}"); }
            finally { native.Release(); }
        }

        /// <summary>
        /// Resolves the registered GTK application from a real window, with the process default as a closing-window fallback.
        /// </summary>
        private static Gio.Application GetLinuxApplication(Window? window) =>
            (window?.Handler?.PlatformView as Gtk.Window)?.GetApplication() ?? Gio.Application.GetDefault()
            ?? throw new InvalidOperationException("The ASLM GTK application is unavailable.");
    }
}
