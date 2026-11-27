// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Windowing;
using NativeApplication = Microsoft.UI.Xaml.Application;
using NativeWindow = Microsoft.UI.Xaml.Window;

namespace ASLM
{
    /// <summary>
    /// Coordinates window close, explicit quit and restart before WinUI stops dispatching.
    /// </summary>
    public partial class App
    {
        /// <summary>
        /// Allows the final native close once application state and active file operations are settled.
        /// </summary>
        private bool _windowsCloseApproved;

        /// <summary>
        /// Routes MAUI's quit command through the same shutdown path as the window close button.
        /// </summary>
        internal static void ConfigureWindowsLifetime()
        {
            ApplicationHandler.CommandMapper["Terminate"] = static (_, application, _) =>
            {
                if (application is App app) _ = app.RequestWindowsShutdownAsync();
            };
        }

        /// <summary>
        /// Intercepts native close requests while the window and its dispatcher can still be used.
        /// </summary>
        private void OnWindowsWindowCreated(object? sender, EventArgs e)
        {
            if (sender is Window { Handler.PlatformView: NativeWindow native })
                native.AppWindow.Closing += OnWindowsWindowClosing;
        }

        /// <summary>
        /// Defers native close while the one shutdown operation is in progress.
        /// </summary>
        private void OnWindowsWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_windowsCloseApproved) return;
            args.Cancel = true;
            _ = RequestWindowsShutdownAsync();
        }

        /// <summary>
        /// Starts or joins the shutdown operation on the UI thread.
        /// </summary>
        private Task RequestWindowsShutdownAsync(bool restart = false) =>
            _shutdownTask ??= ShutdownWindowsAsync(restart);

        /// <summary>
        /// Discards session-only work and lets persistent operations settle before closing the process group.
        /// </summary>
        private async Task ShutdownWindowsAsync(bool restart)
        {
            // Return to WinUI before closing again and publish the shared task before another request can arrive.
            await Task.Yield();
            var window = Windows.FirstOrDefault();
            if (restart)
            {
                try
                {
                    var tracker = _services.GetRequiredService<ProcessTracker>();
                    await Task.Run(() => SettingsService.StartLauncherForApplicationRestart(tracker.OwnsCurrentProcess));
                }
                catch
                {
                    // A failed launcher start leaves the current session usable and allows another restart attempt.
                    _shutdownTask = null;
                    throw;
                }
            }

            // Dispatch cancellations independently so an unfinished probe or metadata read cannot hold up exit.
            _ = StopWindowsPreparationAsync<ModulePagePreparationQueue>();
            _ = StopWindowsPreparationAsync<DataPreparationService>();
            _ = StopWindowsPreparationAsync<DataPreparationQueue>();
            _services.GetRequiredService<UpdateScheduler>().CancelForShutdown();

            try
            {
                // File operations use their existing cancellation and commit boundaries, including catalog rollback.
                var setup = window?.Page is SetupWizardPage wizard ? wizard.StopInstallationAsync() : Task.CompletedTask;
                await Task.WhenAll(setup, _services.GetRequiredService<DownloadQueue>().StopAsync(),
                    _services.GetRequiredService<UpdateManager>().StopAsync());

                if (window is { Page: AppShellPage })
                    await _services.GetRequiredService<AppDataStore>().SaveAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Windows shutdown cleanup failed: {ex}");
            }

            // Let WinUI close its window, then release ASLM's job as the last process cleanup step.
            _windowsCloseApproved = true;
            try
            {
                if (window?.Handler?.PlatformView is NativeWindow native) native.Close();
                else NativeApplication.Current.Exit();
            }
            finally { DisposeService<ProcessTracker>(); }
        }

        /// <summary>
        /// Signals cancellation and observes cleanup failures independently of the window close operation.
        /// </summary>
        private async Task StopWindowsPreparationAsync<T>() where T : class, IAsyncDisposable
        {
            try { await _services.GetRequiredService<T>().DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Debug.WriteLine($"[App] {typeof(T).Name} shutdown failed: {ex}"); }
        }
    }
}
