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
        private readonly AppDataStore _appData;
        private readonly LegalAcceptanceService _legalAcceptance;
        private readonly NotificationCenter _notifications;
        private readonly GitHubRateLimitStore _rateLimitStore;
        private readonly GitHubAccountStore _githubAccountStore;
        private readonly GitHubUpdateClient _githubUpdateClient;
        private readonly UpdateScheduler _updateScheduler;
        private readonly AslmMirrorServer _mirrorServer;
        private readonly AslmModuleInteropServer _moduleInteropServer;
        private readonly ThemeService _themeService;
        private readonly CustomThemesStore _customThemesStore;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly SunriseService _sunriseService;
        private readonly AppLocalizationService _localization;
        private readonly IServiceProvider _services;
        private bool _initialized;


        // Initialization

        /// <summary>
        /// Creates the startup loading page.
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
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            await Task.Run(() => _appData.InitializeAsync());
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
            await Task.Run(() => _legalAcceptance.InitializeAsync());
            _localization.ApplyCulture();
            await Task.Run(() => _moduleTrustService.InitializeAsync());
            await Task.Run(() => _customThemesStore.LoadAsync());
            await Task.Run(() => _notifications.InitializeAsync());
            await Task.Run(() => _mirrorServer.StartIfEnabledAsync());
            await Task.Run(() => _moduleInteropServer.EnsureStartedAsync());
            await Task.Run(() => _rateLimitStore.InitializeAsync());
            await Task.Run(() => _githubAccountStore.InitializeAsync());
            try
            {
                await Task.Run(() => _githubUpdateClient.RefreshRateLimitAsync());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GitHub rate-limit refresh failed during startup: {ex.Message}");
            }
            await Task.Run(_updateScheduler.Start);
            _themeService.ApplyFromSettings();

            await _legalAcceptance.ResolveStartupAcceptanceAsync(_appData);

            Page nextPage = _appData.IsFirstRun
                ? _services.GetRequiredService<SetupWizardPage>()
                : _services.GetRequiredService<AppShellPage>();

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
