// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Pages;
using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Maui.Handlers;
#endif

namespace ASLM
{
    // MAUI bootstrap

    /// <summary>
    /// Configures the MAUI host, services, and page registrations.
    /// </summary>
    public static class MauiProgram
    {
        // App builder

        /// <summary>
        /// Creates the configured MAUI application instance.
        /// </summary>
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            // Configure the shared application host and bundled fonts.
            builder
                .UseMauiApp<App>()
                .ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler<ConsoleOutputView, ConsoleOutputViewHandler>();
                })
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("SegoeUI-Semibold.ttf", "SegoeSemibold");
                    fonts.AddFont("FluentSystemIcons-Regular.ttf", "FluentUI");
                });

#if DEBUG
            // Keep debug logging enabled in development builds.
            builder.Logging.AddDebug();
            builder.Services.AddLogging(configure => configure.AddDebug());
#endif

            // Service registrations
            builder.Services.AddSingleton<AppDataStore>();
            builder.Services.AddSingleton<LegalAcceptanceService>();
            builder.Services.AddSingleton<EngineInstaller>();
            builder.Services.AddSingleton<ModuleEnvironmentResolver>();
            builder.Services.AddSingleton<ModuleTrustService>();
            builder.Services.AddSingleton<ModuleInstaller>();
            builder.Services.AddSingleton<ModuleConsoleStore>();
            builder.Services.AddSingleton<ProcessSnapshotReader>();
            builder.Services.AddSingleton<ProcessTracker>();
            builder.Services.AddSingleton<ModuleThemePayloadBuilder>();
            builder.Services.AddSingleton<ModuleLocalePayloadBuilder>();
            builder.Services.AddSingleton<AppLocalizationService>();
            builder.Services.AddSingleton<ModuleInteropHostState>();
            builder.Services.AddSingleton<ModuleStartThrottle>();
            builder.Services.AddSingleton<PortRegistry>();
            builder.Services.AddSingleton<ModuleRunner>();
            builder.Services.AddSingleton<ModuleDependencyService>();
            builder.Services.AddSingleton<ModuleDownloadBridge>();
            builder.Services.AddSingleton<DownloadStateStore>();
            builder.Services.AddSingleton<DownloadCatalog>();
            builder.Services.AddSingleton<DownloadInstaller>();
            builder.Services.AddSingleton<NotificationCenter>();
            builder.Services.AddSingleton<OllamaSettingsStore>();
            builder.Services.AddSingleton<GitHubAccountStore>();
            builder.Services.AddSingleton<GitHubRateLimitStore>();
            builder.Services.AddSingleton<GitHubUpdateClient>();
            builder.Services.AddSingleton<UpdateManager>();
            builder.Services.AddSingleton<UpdateScheduler>();
            builder.Services.AddSingleton<ModuleLaunchCoordinator>();
            builder.Services.AddSingleton<AslmModuleInteropServer>();
            builder.Services.AddSingleton<AslmMirrorServer>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<CustomThemesStore>();
            builder.Services.AddSingleton<ThemeService>();
            builder.Services.AddSingleton<SunriseService>();

            // Page registrations
            builder.Services.AddTransient<AppShellPage>();
            builder.Services.AddTransient<SetupWizardPage>();
            builder.Services.AddTransient<LoadingPage>();
            builder.Services.AddTransient<LegalAcceptanceView>();

            // Content view registrations
            builder.Services.AddTransient<HomeView>();
            builder.Services.AddTransient<ConsolesView>();
            builder.Services.AddTransient<ModulesView>();
            builder.Services.AddTransient<AslmApiView>();
            builder.Services.AddTransient<NotificationsView>();
            builder.Services.AddTransient<DownloadsView>();
            builder.Services.AddTransient<SettingsView>();
            builder.Services.AddTransient<ModuleUpdateView>();

#if WINDOWS
            ConfigureWindowsCompactControlSizing();
#endif

            var app = builder.Build();
            var localization = app.Services.GetRequiredService<AppLocalizationService>();
            Localization.L.Initialize(localization);
            return app;
        }

#if WINDOWS
        // Windows control sizing

        /// <summary>
        /// Removes WinUI default minimum sizes and inner padding so compact checkboxes render centered.
        /// </summary>
        private static void ConfigureWindowsCompactControlSizing()
        {
            CheckBoxHandler.Mapper.AppendToMapping("CompactWindowsCheckBox", (handler, view) =>
            {
                handler.PlatformView.MinWidth = 16;
                handler.PlatformView.MinHeight = 16;
                handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(0);
            });

            SwitchHandler.Mapper.AppendToMapping("CompactWindowsSwitch", (handler, view) =>
            {
                handler.PlatformView.MinWidth = 16;
                handler.PlatformView.MinHeight = 16;
                handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(0);
            });
        }
#endif
    }
}
