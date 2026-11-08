// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using Debug = System.Diagnostics.Debug;
using Microsoft.Extensions.DependencyInjection;

namespace ASLM.Pages
{
    /// <summary>
    /// Initializes persisted app data and routes startup to the next page.
    /// </summary>
    public partial class LoadingPage : ContentPage, ILocalizable
    {
        // Fields

        /// <summary>
        /// Persisted application data; loaded first because every later startup step depends on the settings.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Legal acceptance state; decides whether the user must confirm the legal documents.
        /// </summary>
        private readonly LegalAcceptanceService _legalAcceptance;

        /// <summary>
        /// Notification list that is restored from disk at startup.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Persisted GitHub API usage that is restored before the first rate-limit refresh.
        /// </summary>
        private readonly GitHubRateLimitStore _rateLimitStore;

        /// <summary>
        /// Stored GitHub account whose token is loaded and verified at startup.
        /// </summary>
        private readonly GitHubAccountStore _githubAccountStore;

        /// <summary>
        /// GitHub client used to refresh the current rate limit at startup.
        /// </summary>
        private readonly GitHubUpdateClient _githubUpdateClient;

        /// <summary>
        /// Background scheduler of the periodic update checks.
        /// </summary>
        private readonly UpdateScheduler _updateScheduler;

        /// <summary>
        /// Local reverse proxy that is started here when the settings enable it.
        /// </summary>
        private readonly AslmMirrorServer _mirrorServer;

        /// <summary>
        /// Local JSON API through which modules query the module registry and request module starts.
        /// </summary>
        private readonly AslmModuleInteropServer _moduleInteropServer;

        /// <summary>
        /// Applies the theme stored in the settings once those are loaded.
        /// </summary>
        private readonly ThemeService _themeService;

        /// <summary>
        /// User-defined themes, loaded before the stored theme is applied.
        /// </summary>
        private readonly CustomThemesStore _customThemesStore;

        /// <summary>
        /// Module trust levels, initialized from the shipped configuration and the cached verified list.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// SUNRISE ID service that is initialized and synchronized with the cloud account when SUNRISE is enabled.
        /// </summary>
        private readonly SunriseService _sunriseService;

        /// <summary>
        /// Applies the stored UI language and keeps the flow direction in sync after the page is replaced.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Service provider that creates the page shown after startup.
        /// </summary>
        private readonly IServiceProvider _services;

        /// <summary>
        /// Set on the first appearance so repeated appearances do not run the startup sequence again.
        /// </summary>
        private bool _initialized;


        // Initialization

        /// <summary>
        /// Creates the startup loading page and attaches its localization hook.
        /// </summary>
        public LoadingPage(
            AppDataStore appData,
            LegalAcceptanceService legalAcceptance,
            NotificationCenter notifications,
            GitHubRateLimitStore rateLimitStore,
            GitHubAccountStore githubAccountStore,
            GitHubUpdateClient githubUpdateClient,
            UpdateScheduler updateScheduler,
            AslmMirrorServer mirrorServer,
            AslmModuleInteropServer moduleInteropServer,
            ThemeService themeService,
            CustomThemesStore customThemesStore,
            ModuleTrustService moduleTrustService,
            SunriseService sunriseService,
            AppLocalizationService localization,
            IServiceProvider services)
        {
            InitializeComponent();
            _appData = appData;
            _legalAcceptance = legalAcceptance;
            _notifications = notifications;
            _rateLimitStore = rateLimitStore;
            _githubAccountStore = githubAccountStore;
            _githubUpdateClient = githubUpdateClient;
            _updateScheduler = updateScheduler;
            _mirrorServer = mirrorServer;
            _moduleInteropServer = moduleInteropServer;
            _themeService = themeService;
            _customThemesStore = customThemesStore;
            _moduleTrustService = moduleTrustService;
            _sunriseService = sunriseService;
            _localization = localization;
            _services = services;
            LocalizableAttach.Hook(this, _localization, this);
        }


        // Localization

        /// <summary>
        /// Applies the loading label text for the current culture.
        /// </summary>
        public void ApplyLocalization() =>
            LoadingLabel.Text = L.Get(LocalizationKeys.Loading_Text);


        // Lifecycle

        /// <summary>
        /// Runs the startup initialization once and replaces the current page.
        /// </summary>
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // OnAppearing can be raised more than once; the startup sequence must run only once.
            if (_initialized)
            {
                return;
            }

            // Load the settings and apply the stored UI language before anything else needs them.
            _initialized = true;
            await Task.Run(() => _appData.InitializeAsync());
            _localization.ApplyCulture();

            // SUNRISE ID: load its data, then refresh the cloud account with a time limit so a slow server cannot stall startup.
            if (SunriseService.IsEnabled)
            {
                await Task.Run(() => _sunriseService.InitializeAsync());
                try
                {
                    using var cloudSyncCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var cloudSync = await _sunriseService.SynchronizeCloudAccountAsync(cloudSyncCts.Token);
                    if (!cloudSync.Success)
                    {
                        Debug.WriteLine($"SUNRISE cloud-account synchronization failed: {cloudSync.Error}");
                    }
                }
                catch (OperationCanceledException)
                {
                    // Continue with cached account data when SUNRISE is unavailable at startup.
                    Debug.WriteLine("SUNRISE cloud-account synchronization timed out during startup.");
                }
                catch (Exception ex)
                {
                    // A temporary account/network failure must not prevent ASLM from starting.
                    Debug.WriteLine($"SUNRISE cloud-account synchronization failed during startup: {ex.Message}");
                }
            }

            // Restore the persisted stores and start the local servers, one step after another off the UI thread.
            await Task.Run(() => _legalAcceptance.InitializeAsync());
            await Task.Run(() => _moduleTrustService.InitializeAsync());
            await Task.Run(() => _customThemesStore.LoadAsync());
            await Task.Run(() => _notifications.InitializeAsync());
            await Task.Run(() => _mirrorServer.StartIfEnabledAsync());
            await Task.Run(() => _moduleInteropServer.EnsureStartedAsync());
            await Task.Run(() => _rateLimitStore.InitializeAsync());
            await Task.Run(() => _githubAccountStore.InitializeAsync());

            // The rate-limit refresh needs the network; a failure only leaves the stored values in place.
            try
            {
                await Task.Run(() => _githubUpdateClient.RefreshRateLimitAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub rate-limit refresh failed during startup: {ex.Message}");
            }

            // Start the update checks, then apply the stored theme (it needs the main thread, so it runs outside Task.Run).
            await Task.Run(_updateScheduler.Start);
            _themeService.ApplyFromSettings();

            // Decide whether the legal documents need manual acceptance; the next page shows the acceptance overlay when they do.
            await _legalAcceptance.ResolveStartupAcceptanceAsync(_appData);

            // Choose the next page: the setup wizard on the first run, the application shell otherwise.
            Page nextPage = _appData.IsFirstRun
                ? _services.GetRequiredService<SetupWizardPage>()
                : _services.GetRequiredService<AppShellPage>();

            // Localize the new page before it becomes visible, then swap it in as the window root.
            if (nextPage is ILocalizable localizable)
            {
                localizable.ApplyLocalization();
            }

            if (Window != null)
            {
                Window.Page = nextPage;
                _localization.SyncFlowDirection();
            }
        }
    }
}
