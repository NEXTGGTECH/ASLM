// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Engines;
using ASLM.Services.Internal;
using ASLM.Services.Modules;
using ASLM.Services.Sunrise;
using Microsoft.Extensions.Logging;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Builds the real production service graph over one <see cref="UniqueAslmRoot"/> with stub
/// HTTP transports, so integration scenarios exercise genuine service code without network
/// or MAUI. Create the graph before anything else in the test and dispose it at the end.
/// </summary>
internal sealed class TestServiceGraph : IDisposable
{
    private readonly ProcessTracker _processTracker;
    private readonly ServiceRegistry _services;
    private readonly bool _ownsRoot;

    public UniqueAslmRoot Root { get; }

    public AppDataStore AppData { get; }

    public GitHubRateLimitStore RateLimits { get; }

    public GitHubAccountStore GitHubAccounts { get; }

    public ModuleIconCache IconCache { get; }

    /// <summary>
    /// Gets the stub transport behind <see cref="GitHub"/>; register routes before use.
    /// </summary>
    public StubHttpHandler GitHubHttp { get; } = new();

    public GitHubUpdateClient GitHub { get; }

    /// <summary>
    /// Gets the stub transport behind <see cref="Downloads"/>; register routes before use.
    /// </summary>
    public StubHttpHandler DownloadHttp { get; } = new();

    public EngineInstaller Engines { get; }

    public CustomThemesStore Themes { get; }

    public ModuleThemePayloadBuilder ThemePayload { get; }

    public ModuleLocalePayloadBuilder LocalePayload { get; }

    public ModuleConsoleStore Consoles { get; }

    public PortRegistry Ports { get; }

    public ModuleRegistry ModuleIds { get; }

    /// <summary>
    /// Gets the stub transport behind <see cref="Trust"/>; register routes before use.
    /// </summary>
    public StubHttpHandler TrustHttp { get; } = new();

    public ModuleTrustService Trust { get; }

    public ModuleEnvironmentResolver Environments { get; }

    public ProcessSnapshotReader ProcessSnapshots { get; } = new();

    public ModuleInteropHostState InteropHost { get; } = new();

    public SunriseService Sunrise { get; }

    public ModuleRunner Runner { get; }

    public ModuleEngineReconciler EngineReconciler { get; }

    public ModuleInstaller Installer { get; }

    public ModuleDependencyService Dependencies { get; }

    public ModuleStartThrottle Throttle { get; } = new();

    public ModuleLaunchCoordinator Coordinator { get; }

    /// <summary>
    /// Gets the stub transport behind <see cref="Ollama"/>; register routes before use.
    /// </summary>
    public StubHttpHandler OllamaHttp { get; } = new();

    public OllamaSettingsStore Ollama { get; }

    public NotificationCenter Notifications { get; }

    public DownloadQueue Queue { get; } = new();

    public UpdateManager Updates { get; }

    public DownloadStateStore DownloadState { get; }

    public ModuleDownloadBridge Bridge { get; }

    public DownloadInstaller Downloads { get; }

    public DownloadCatalog Catalog { get; }

    public TestServiceGraph(UniqueAslmRoot? root = null)
    {
        _ownsRoot = root == null;
        Root = root ?? new UniqueAslmRoot();
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
        // A real kill-on-close job would take the test host down with it on dispose.
        _processTracker = new ProcessTracker(TestLoggerFactory.Create<ProcessTracker>(), trackCurrentProcess: false);
        Runner = new ModuleRunner(
            Engines, Environments, Ports, _processTracker, Consoles, ProcessSnapshots,
            ThemePayload, LocalePayload, Trust, GitHubAccounts, Sunrise, InteropHost,
            _services = new ServiceRegistry(),
            TestLoggerFactory.Create<ModuleRunner>());
        EngineReconciler = new ModuleEngineReconciler(Engines);
        Installer = new ModuleInstaller(Runner, Trust, EngineReconciler, Engines, GitHub, Ports);
        Dependencies = new ModuleDependencyService(Installer, Runner, TestLoggerFactory.Create<ModuleDependencyService>());
        _services.Register(Dependencies);
        Coordinator = new ModuleLaunchCoordinator(Installer, Runner, Throttle, TestLoggerFactory.Create<ModuleLaunchCoordinator>());
        Ollama = new OllamaSettingsStore(Engines, TestLoggerFactory.Create<OllamaSettingsStore>(), new HttpClient(OllamaHttp), null);
        Notifications = new NotificationCenter(new InlineDispatcher());
        Updates = new UpdateManager(
            AppData, Installer, Engines, Runner, Coordinator, Trust, EngineReconciler, Ollama,
            GitHub, Notifications, TestLoggerFactory.Create<UpdateManager>(), Queue);
        DownloadState = new DownloadStateStore(TestLoggerFactory.Create<DownloadStateStore>());
        Bridge = new ModuleDownloadBridge(Engines, Environments, Runner, TestLoggerFactory.Create<ModuleDownloadBridge>());
        Downloads = new DownloadInstaller(
            Installer, Bridge, Engines, Environments, DownloadState, Notifications,
            TestLoggerFactory.Create<DownloadInstaller>(), Queue, new HttpClient(DownloadHttp));
        Catalog = new DownloadCatalog(Installer, Bridge, DownloadState, TestLoggerFactory.Create<DownloadCatalog>());
    }

    /// <summary>
    /// Stops every module the runner started and releases process tracking.
    /// </summary>
    public void Dispose()
    {
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

    /// <summary>
    /// A minimal provider that resolves exactly the services the runner requires.
    /// </summary>
    private sealed class ServiceRegistry : IServiceProvider
    {
        private readonly Dictionary<Type, object> _services = [];

        public void Register<T>(T instance) where T : class => _services[typeof(T)] = instance;

        public object? GetService(Type serviceType) =>
            _services.TryGetValue(serviceType, out var service) ? service : null;
    }
}
