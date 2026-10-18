// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Pages;

namespace ASLM
{
    // Application host

    /// <summary>
    /// Creates the main window and coordinates application shutdown.
    /// </summary>
    public partial class App : Application
    {
        private readonly IServiceProvider _services;
        private Task? _shutdownTask;

        // Initialization

        /// <summary>
        /// Creates the application instance.
        /// </summary>
        public App(IServiceProvider services)
        {
#if LINUX
            LinuxMainThread.Initialize(Dispatcher);
#endif
            InitializeComponent();
            _services = services;
        }


        // Window creation

        /// <summary>
        /// Creates the main application window.
        /// </summary>
        protected override Window CreateWindow(IActivationState? activationState)
        {
            var page = CreateStartupPage();
            var window = new Window(page)
            {
                Title = "ASLM",
                MinimumWidth = 1280,
                MinimumHeight = 720
            };

            // Trigger graceful shutdown cleanup when the main window is destroyed.
#if LINUX
            window.Destroying += OnLinuxWindowDestroying;
#else
            window.Destroying += OnWindowDestroying;
#endif

            return window;
        }

        // Startup page

        /// <summary>
        /// Creates the first page in the startup chain.
        /// </summary>
        public Page CreateStartupPage()
        {
            return _services.GetRequiredService<LoadingPage>();
        }


        // Shutdown

        /// <summary>
        /// Stops tracked module processes during application shutdown.
        /// </summary>
        private void OnWindowDestroying(object? sender, EventArgs e)
        {
            ShutdownAsync(sender as Window).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Saves navigation on the UI thread and starts one shared cleanup operation for close and quit requests.
        /// </summary>
        private Task ShutdownAsync(Window? window)
        {
            if (_shutdownTask != null) return _shutdownTask;

            try
            {
                // Persist navigation only after the shell has finished loading; closing during LoadingPage
                // must never overwrite existing application data with a not-yet-loaded default model.
                if (window is { Page: AppShellPage })
                {
                    _services.GetRequiredService<AppDataStore>().Save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to save app data during shutdown: {ex}");
            }

            // Native event loops must remain available while services finish outstanding UI-dispatched work.
            // Blocking disposal belongs on a worker too: both API servers synchronously wait for StopAsync.
            return _shutdownTask = Task.Run(StopServicesAsync);
        }

        /// <summary>
        /// Stops producers before module processes and releases each service even if another cleanup step fails.
        /// </summary>
        private async Task StopServicesAsync()
        {
            DisposeService<UpdateScheduler>();
            ModuleRunner? runner = null;
            try
            {
                // Stop module processes first so the tracker can dispose after active work ends.
                runner = _services.GetRequiredService<ModuleRunner>();
                await runner.StopAllModulesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Shutdown cleanup failed: {ex}");
            }
            finally
            {
                try { runner?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] Module runner disposal failed: {ex}"); }
            }

            DisposeService<ProcessTracker>();
            DisposeService<AslmMirrorServer>();
            DisposeService<AslmModuleInteropServer>();
            DisposeService<SunriseService>();
        }

        /// <summary>
        /// Releases one service without preventing the remaining shutdown cleanup after a disposal failure.
        /// </summary>
        private void DisposeService<T>() where T : class, IDisposable
        {
            try { _services.GetService<T>()?.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] {typeof(T).Name} disposal failed: {ex}"); }
        }
    }
}
