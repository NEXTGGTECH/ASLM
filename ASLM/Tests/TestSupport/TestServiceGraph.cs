// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Engines;
using ASLM.Services.Internal;
using ASLM.Services.Modules;
using ASLM.Services.Sunrise;
using Microsoft.Extensions.Logging;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Builds the real production service graph over one <see cref="UniqueAslmRoot"/> with stub
    /// HTTP transports, so integration scenarios exercise genuine service code without network
    /// or MAUI. Create the graph before anything else in the test and dispose it at the end.
    /// </summary>
    internal sealed class TestServiceGraph : IDisposable
    {
        // Fields

        /// <summary>
        /// Process tracker handed to the runner; created without tracking the test host process.
        /// </summary>
        private readonly ProcessTracker _processTracker;

        /// <summary>
        /// Service provider the runner uses to look up services that cannot be passed through its constructor.
        /// </summary>
        private readonly ServiceRegistry _services;

        /// <summary>
        /// Whether the graph created <see cref="Root"/> itself and therefore deletes it on dispose.
        /// </summary>
        private readonly bool _ownsRoot;


        // Isolated storage

        /// <summary>
        /// Gets the throwaway ASLM root all stores and services of the graph read and write under.
        /// </summary>
        public UniqueAslmRoot Root { get; }

        /// <summary>
        /// Gets the store of persisted application settings.
        /// </summary>
        public AppDataStore AppData { get; }

        /// <summary>
        /// Gets the store of recorded GitHub API usage, backed by a file in the data folder.
        /// </summary>
        public GitHubRateLimitStore RateLimits { get; }

        /// <summary>
        /// Gets the GitHub account state, which verifies tokens through <see cref="GitHubHttp"/>.
        /// </summary>
        public GitHubAccountStore GitHubAccounts { get; }

        /// <summary>
        /// Gets the disk cache of catalog icons under the root's download folder.
        /// </summary>
        public ModuleIconCache IconCache { get; }


        // GitHub and engines

        /// <summary>
        /// Gets the stub transport behind <see cref="GitHub"/>; register routes before use.
        /// It also serves the account store, the engine installer and the SUNRISE service.
        /// </summary>
        public StubHttpHandler GitHubHttp { get; } = new();

        /// <summary>
        /// Gets the GitHub API client whose requests go through <see cref="GitHubHttp"/>.
        /// </summary>
        public GitHubUpdateClient GitHub { get; }

        /// <summary>
        /// Gets the stub transport behind <see cref="Downloads"/>; register routes before use.
        /// </summary>
        public StubHttpHandler DownloadHttp { get; } = new();

        /// <summary>
        /// Gets the engine installer; its downloads go through <see cref="GitHubHttp"/>.
        /// </summary>
        public EngineInstaller Engines { get; }


        // Module support services

        /// <summary>
        /// Gets the store of user-defined themes.
        /// </summary>
        public CustomThemesStore Themes { get; }

        /// <summary>
        /// Gets the builder of the theme snapshot passed to modules.
        /// </summary>
        public ModuleThemePayloadBuilder ThemePayload { get; }

        /// <summary>
        /// Gets the builder of the UI language snapshot passed to modules.
        /// </summary>
        public ModuleLocalePayloadBuilder LocalePayload { get; }

        /// <summary>
        /// Gets the in-memory console sessions of modules.
        /// </summary>
        public ModuleConsoleStore Consoles { get; }

        /// <summary>
        /// Gets the port registry, persisted in the root's data folder.
        /// </summary>
        public PortRegistry Ports { get; }

        /// <summary>
        /// Gets the registry of installed module ids stored under the root.
        /// </summary>
        public ModuleRegistry ModuleIds { get; }

        /// <summary>
        /// Gets the stub transport behind <see cref="Trust"/>; register routes before use.
        /// </summary>
        public StubHttpHandler TrustHttp { get; } = new();

        /// <summary>
        /// Gets the module trust resolver that downloads its reviewed list through <see cref="TrustHttp"/>.
        /// </summary>
        public ModuleTrustService Trust { get; }

        /// <summary>
        /// Gets the resolver of per-module engine dependency environments.
        /// </summary>
        public ModuleEnvironmentResolver Environments { get; }

        /// <summary>
        /// Gets the reader of the operating-system process table.
        /// </summary>
        public ProcessSnapshotReader ProcessSnapshots { get; } = new();

        /// <summary>
        /// Gets the holder of the interop listener address that is injected into module processes.
        /// </summary>
        public ModuleInteropHostState InteropHost { get; } = new();

        /// <summary>
        /// Gets the SUNRISE ID service; its HTTP traffic goes through <see cref="GitHubHttp"/>.
        /// </summary>
        public SunriseService Sunrise { get; }


        // Module lifecycle

        /// <summary>
        /// Gets the runner that starts and stops module processes.
        /// </summary>
        public ModuleRunner Runner { get; }

        /// <summary>
        /// Gets the installer and reinstaller of module engine dependencies.
        /// </summary>
        public ModuleEngineReconciler EngineReconciler { get; }

        /// <summary>
        /// Gets the module installer and discovery service.
        /// </summary>
        public ModuleInstaller Installer { get; }

        /// <summary>
        /// Gets the service that makes module dependencies ready before a dependent module runs.
        /// </summary>
        public ModuleDependencyService Dependencies { get; }

        /// <summary>
        /// Gets the limiter of concurrent module launches.
        /// </summary>
        public ModuleStartThrottle Throttle { get; } = new();

        /// <summary>
        /// Gets the coordinator that runs the full launch sequence of a module.
        /// </summary>
        public ModuleLaunchCoordinator Coordinator { get; }


        // Ollama, notifications and updates

        /// <summary>
        /// Gets the stub transport behind <see cref="Ollama"/>; register routes before use.
        /// </summary>
        public StubHttpHandler OllamaHttp { get; } = new();

        /// <summary>
        /// Gets the Ollama account and settings store, which talks to the runtime through <see cref="OllamaHttp"/>.
        /// </summary>
        public OllamaSettingsStore Ollama { get; }

        /// <summary>
        /// Gets the notification center; it runs its list mutations inline instead of on a UI thread.
        /// </summary>
        public NotificationCenter Notifications { get; }

        /// <summary>
        /// Gets the session-only download queue shared by the update manager and the download installer.
        /// </summary>
        public DownloadQueue Queue { get; } = new();

        /// <summary>
        /// Gets the manager that checks and applies ASLM, module and engine updates.
        /// </summary>
        public UpdateManager Updates { get; }


        // Downloads

        /// <summary>
        /// Gets the store of the persisted download catalog installation state.
        /// </summary>
        public DownloadStateStore DownloadState { get; }

        /// <summary>
        /// Gets the bridge that queries module bridge commands for download catalog data.
        /// </summary>
        public ModuleDownloadBridge Bridge { get; }

        /// <summary>
        /// Gets the installer of download catalog items; its transfers go through <see cref="DownloadHttp"/>.
        /// </summary>
        public DownloadInstaller Downloads { get; }

        /// <summary>
        /// Gets the download catalog merged from all installed module bridges.
        /// </summary>
        public DownloadCatalog Catalog { get; }


        // Initialization

        /// <summary>
        /// Wires the whole production service graph. Every stub transport starts without routes, so requests to
        /// unregistered URLs fail with the synthetic 599 status until a test registers them.
        /// </summary>
        /// <param name="root">Existing root to build on; when null the graph creates and later deletes its own.</param>
        public TestServiceGraph(UniqueAslmRoot? root = null)
        {
            _ownsRoot = root == null;
            Root = root ?? new UniqueAslmRoot();

            // Stores and GitHub layer: file-backed stores first, then the GitHub clients that depend on them.
            AppData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            RateLimits = new GitHubRateLimitStore(
                TestLoggerFactory.Create<GitHubRateLimitStore>(),
                Path.Combine(Root.DataAppDir, "ASLM_GitHubRateLimit.json"),
                TimeProvider.System);
            GitHubAccounts = new GitHubAccountStore(
                AppData, RateLimits, TestLoggerFactory.Create<GitHubAccountStore>(), new HttpClient(GitHubHttp));
            IconCache = new ModuleIconCache(
                Path.Combine(Root.Root, "Data", "Downloads", "IconModules"),
                TimeProvider.System,
                TestLoggerFactory.Create<ModuleIconCache>());
            GitHub = new GitHubUpdateClient(RateLimits, GitHubAccounts, new HttpClient(GitHubHttp), IconCache);
            Engines = new EngineInstaller(GitHub, new HttpClient(GitHubHttp));

            // Module support services: payload builders, consoles, ports, trust, environments and SUNRISE.
            Themes = new CustomThemesStore(TestLoggerFactory.Create<CustomThemesStore>());
            ThemePayload = new ModuleThemePayloadBuilder(AppData, Themes, TestLoggerFactory.Create<ModuleThemePayloadBuilder>());
            LocalePayload = new ModuleLocalePayloadBuilder(AppData, TestLoggerFactory.Create<ModuleLocalePayloadBuilder>());
            Consoles = new ModuleConsoleStore();
            ModuleIds = new ModuleRegistry(Root.Root);
            Ports = new PortRegistry(AppData, Path.Combine(Root.DataAppDir, "ASLM_Ports.json"), ModuleIds);
            Trust = new ModuleTrustService(TestLoggerFactory.Create<ModuleTrustService>(), new HttpClient(TrustHttp));
            Environments = new ModuleEnvironmentResolver(Engines);
            Sunrise = new SunriseService(
                TestLoggerFactory.Create<SunriseService>(),
                AppData,
                new Lazy<HttpClient>(() => new HttpClient(GitHubHttp)));

            // Runner and module lifecycle. A real kill-on-close job would take the test host down with it on
            // dispose, so the tracker does not adopt the current process.
            _processTracker = new ProcessTracker(TestLoggerFactory.Create<ProcessTracker>(), trackCurrentProcess: false);
            Runner = new ModuleRunner(
                Engines, Environments, Ports, _processTracker, Consoles, ProcessSnapshots,
                ThemePayload, LocalePayload, Trust, GitHubAccounts, Sunrise, InteropHost,
                _services = new ServiceRegistry(),
                TestLoggerFactory.Create<ModuleRunner>());
            EngineReconciler = new ModuleEngineReconciler(Engines);
            Installer = new ModuleInstaller(Runner, Trust, EngineReconciler, Engines, GitHub, Ports);
            Dependencies = new ModuleDependencyService(Installer, Runner, TestLoggerFactory.Create<ModuleDependencyService>());

            // The dependency service is built from the runner, while the runner resolves the dependency service
            // through the registry, so it is registered only after both exist.
            _services.Register(Dependencies);
            Coordinator = new ModuleLaunchCoordinator(Installer, Runner, Throttle, TestLoggerFactory.Create<ModuleLaunchCoordinator>());

            // Ollama, notifications and updates; notification changes run inline through the test dispatcher.
            Ollama = new OllamaSettingsStore(Engines, TestLoggerFactory.Create<OllamaSettingsStore>(), new HttpClient(OllamaHttp), null);
            Notifications = new NotificationCenter(new InlineDispatcher());
            Updates = new UpdateManager(
                AppData, Installer, Engines, Runner, Coordinator, Trust, EngineReconciler, Ollama,
                GitHub, Notifications, TestLoggerFactory.Create<UpdateManager>(), Queue);

            // Download catalog services on top of the module bridge.
            DownloadState = new DownloadStateStore(TestLoggerFactory.Create<DownloadStateStore>());
            Bridge = new ModuleDownloadBridge(Engines, Environments, Runner, TestLoggerFactory.Create<ModuleDownloadBridge>());
            Downloads = new DownloadInstaller(
                Installer, Bridge, Engines, Environments, DownloadState, Notifications,
                TestLoggerFactory.Create<DownloadInstaller>(), Queue, new HttpClient(DownloadHttp));
            Catalog = new DownloadCatalog(Installer, Bridge, DownloadState, TestLoggerFactory.Create<DownloadCatalog>());
        }


        // Cleanup

        /// <summary>
        /// Stops every module the runner started, releases process tracking, and deletes the root when the
        /// graph created it. A root passed to the constructor stays alive for its owner to dispose.
        /// </summary>
        public void Dispose()
        {
            // Give running modules up to 15 seconds to stop; a failure must not abort the remaining cleanup.
            try
            {
                Runner.StopAllModulesAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort: leaked module processes are reaped by their own console session lifetime.
            }

            Runner.Dispose();
            _processTracker.Dispose();
            if (_ownsRoot)
            {
                Root.Dispose();
            }
        }


        // Nested types

        /// <summary>
        /// A minimal provider that resolves exactly the services the runner requires.
        /// </summary>
        private sealed class ServiceRegistry : IServiceProvider
        {
            /// <summary>
            /// Registered service instances by their registration type.
            /// </summary>
            private readonly Dictionary<Type, object> _services = [];

            /// <summary>
            /// Registers the instance under <typeparamref name="T"/>, replacing an earlier registration.
            /// </summary>
            public void Register<T>(T instance) where T : class => _services[typeof(T)] = instance;

            /// <summary>
            /// Returns the registered instance for the type, or null when none was registered.
            /// </summary>
            public object? GetService(Type serviceType) =>
                _services.TryGetValue(serviceType, out var service) ? service : null;
        }
    }
}
