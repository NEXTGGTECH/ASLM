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
        private bool _isShuttingDown;

        // Initialization

        /// <summary>
        /// Creates the application instance.
        /// </summary>
        public App(IServiceProvider services)
        {
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
            window.Destroying += OnWindowDestroying;

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
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;

            try
            {
                // Persist navigation only after the shell has finished loading; closing during LoadingPage
                // must never overwrite existing application data with a not-yet-loaded default model.
                if (sender is Window { Page: AppShellPage })
                {
                    _services.GetRequiredService<AppDataStore>().Save();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to save app data during shutdown: {ex}");
            }

            try
            {
                // Stop module processes first so the tracker can dispose after active work ends.
                var runner = _services.GetRequiredService<ModuleRunner>();
                runner.StopAllModulesAsync().GetAwaiter().GetResult();
                runner.Dispose();

                var tracker = _services.GetRequiredService<ProcessTracker>();
                tracker.Dispose();

                var updateScheduler = _services.GetService<UpdateScheduler>();
                updateScheduler?.Dispose();

                var mirrorServer = _services.GetService<AslmMirrorServer>();
                mirrorServer?.Dispose();

                var moduleInteropServer = _services.GetService<AslmModuleInteropServer>();
                moduleInteropServer?.Dispose();

                var sunriseService = _services.GetService<SunriseService>();
                sunriseService?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Shutdown cleanup failed: {ex}");
            }
        }
    }
}
