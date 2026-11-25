// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Hosts the shared sidebar, system views, and module pages.
    /// Covers startup, module list refresh, sidebar, navigation, overlays, toasts, update notification actions and
    /// the embedded module browser; platform folders add native button alignment.
    /// </summary>
    public partial class AppShellPage : ContentPage, INotifyPropertyChanged, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Sidebar width when it shows icons and labels.
        /// </summary>
        private const double PanelExpandedWidth = 240;

        /// <summary>
        /// Coordinator that shows the GitHub rate-limit notice dialog and its settings shortcut.
        /// </summary>
        private readonly GitHubRateLimitNotice _gitHubLimitNotice;

        /// <summary>
        /// Sidebar width when it shows icons only.
        /// </summary>
        private const double PanelCollapsedWidth = 48;

        /// <summary>
        /// Size in device-independent units that sidebar icons are rendered at.
        /// </summary>
        private const double SidebarIconLogicalSize = 20;

        /// <summary>
        /// Packaged image of the sidebar collapse button.
        /// </summary>
        private const string IconMenu = "icon_menu.png";

        /// <summary>
        /// Packaged image of the Home navigation button.
        /// </summary>
        private const string IconHome = "icon_home.png";

        /// <summary>
        /// Packaged image of the Consoles navigation button.
        /// </summary>
        private const string IconConsole = "icon_console.png";

        /// <summary>
        /// Packaged image of the Modules navigation button.
        /// </summary>
        private const string IconModules = "icon_modules.png";

        /// <summary>
        /// Packaged image of the ASLM API navigation button.
        /// </summary>
        private const string IconApi = "icon_api.png";

        /// <summary>
        /// Packaged image of the Notifications navigation button.
        /// </summary>
        private const string IconNotifications = "icon_notifications.png";

        /// <summary>
        /// Packaged image of the Downloads navigation button.
        /// </summary>
        private const string IconDownload = "icon_download.png";

        /// <summary>
        /// Packaged image of the Settings navigation button.
        /// </summary>
        private const string IconSettings = "icon_settings.png";

        /// <summary>
        /// Packaged image of the buttons that open module pages.
        /// </summary>
        private const string IconPage = "icon_page.png";

        /// <summary>
        /// Installer used to discover installed modules and to react to changes of their set.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Runner that executes module commands and tracks their processes; handed to the Modules view.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Persisted application data, including the last shell page and the navigation and console preferences.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Registry that allocates per-module ports and resolves the URL of a module page.
        /// </summary>
        private readonly PortRegistry _ports;

        /// <summary>
        /// Notification service that feeds the toasts and the notifications popover.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Update manager that checks, prepares and applies engine and ASLM updates requested from notifications.
        /// </summary>
        private readonly UpdateManager _updateManager;

        /// <summary>
        /// Scheduler of the background update checks; supplies the retained ASLM and Ollama update candidates.
        /// </summary>
        private readonly UpdateScheduler _updateScheduler;

        /// <summary>
        /// Guards notification actions while their source is being resolved or updated.
        /// </summary>
        private readonly HashSet<string> _notificationUpdates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Service that resolves module trust levels.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// Local ASLM API mirror server whose state drives the ASLM API navigation button.
        /// </summary>
        private readonly AslmMirrorServer _mirrorServer;

        /// <summary>
        /// Coordinator that runs the module launch sequence (first run, enabling, run commands).
        /// </summary>
        private readonly ModuleLaunchCoordinator _moduleLaunchCoordinator;

        /// <summary>
        /// Settings service; the shell uses it to stop all modules and start the launcher for a self-update.
        /// </summary>
        private readonly SettingsService _settingsService;

        /// <summary>
        /// Service that tracks acceptance of the legal documents.
        /// </summary>
        private readonly LegalAcceptanceService _legalAcceptance;

        /// <summary>
        /// Localization service used to re-apply texts when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Service provider used to resolve the shell views and dialogs on demand.
        /// </summary>
        private readonly IServiceProvider _services;

        /// <summary>
        /// Shares HTTP readiness with the dedicated background worker for module pages.
        /// </summary>
        private readonly ModulePagePreparationQueue _modulePages;

        /// <summary>
        /// Installed modules from the last refresh, including modules that are still installing.
        /// </summary>
        private List<ModuleConfig> _allModules = [];

        /// <summary>
        /// Content hashes of the readable module sidebar icons in the latest discovery snapshot.
        /// </summary>
        private Dictionary<string, string> _moduleSidebarIconHashes = new(StringComparer.Ordinal);

        /// <summary>
        /// Module whose page is currently shown, or null when a system view is active.
        /// </summary>
        private ModuleConfig? _activeModule;

        /// <summary>
        /// Whether the sidebar shows labels next to the icons.
        /// </summary>
        private bool _panelExpanded;

        /// <summary>
        /// Whether the first load already ran, so later loads of the page do not repeat startup work.
        /// </summary>
        private bool _hasLoaded;

        /// <summary>
        /// Lazily created Home view.
        /// </summary>
        private View? _homeView;

        /// <summary>
        /// Lazily created Consoles view.
        /// </summary>
        private View? _consolesView;

        /// <summary>
        /// Lazily created Modules view.
        /// </summary>
        private View? _modulesView;

        /// <summary>
        /// Lazily created ASLM API view.
        /// </summary>
        private View? _aslmApiView;

        /// <summary>
        /// Lazily created notifications popover view.
        /// </summary>
        private View? _notificationsView;

        /// <summary>
        /// Lazily created Downloads view.
        /// </summary>
        private View? _downloadsView;

        /// <summary>
        /// Lazily created Settings view.
        /// </summary>
        private View? _settingsView;

        /// <summary>
        /// Lazily created module information view.
        /// </summary>
        private ModuleInfo? _moduleInfoView;

        /// <summary>
        /// Sidebar navigation button of the active system view, or null when a module page is active.
        /// </summary>
        private Button? _activeNavButton;

        /// <summary>
        /// Sidebar navigation buttons of the system views.
        /// </summary>
        private Button[] _navButtons = [];

        /// <summary>
        /// Cancels the pending delayed sidebar button layout pass when a new one is scheduled.
        /// </summary>
        private CancellationTokenSource? _sidebarButtonLayoutCts;

        /// <summary>
        /// Ensures only one module list refresh runs at a time.
        /// </summary>
        private readonly SemaphoreSlim _moduleRefreshLock = new(1, 1);

        /// <summary>
        /// Whether the shell-wide service events are currently subscribed.
        /// </summary>
        private bool _shellEventsHooked;

        /// <summary>
        /// Non-zero when a module refresh was requested while another one was running; accessed through
        /// <see cref="Interlocked"/>.
        /// </summary>
        private int _moduleRefreshQueued;


        // Initialization

        /// <summary>
        /// Creates the application shell and restores the saved sidebar state.
        /// </summary>
        /// <param name="moduleInstaller">Installer used to discover installed modules.</param>
        /// <param name="moduleRunner">Runner that executes module commands and tracks their processes.</param>
        /// <param name="appData">Persisted application data.</param>
        /// <param name="ports">Registry that allocates module ports and resolves module page URLs.</param>
        /// <param name="notifications">Notification service for toasts and the notifications popover.</param>
        /// <param name="updateManager">Update manager used by update notification actions.</param>
        /// <param name="updateScheduler">Scheduler of the background update checks.</param>
        /// <param name="moduleTrustService">Service that resolves module trust levels.</param>
        /// <param name="mirrorServer">Local ASLM API mirror server.</param>
        /// <param name="moduleLaunchCoordinator">Coordinator that runs the module launch sequence.</param>
        /// <param name="settingsService">Settings service used to stop modules and start the update launcher.</param>
        /// <param name="legalAcceptance">Service that tracks acceptance of the legal documents.</param>
        /// <param name="localization">Localization service whose language changes re-apply the texts.</param>
        /// <param name="services">Service provider used to resolve views and dialogs on demand.</param>
        public AppShellPage(
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            AppDataStore appData,
            PortRegistry ports,
            NotificationCenter notifications,
            UpdateManager updateManager,
            UpdateScheduler updateScheduler,
            ModuleTrustService moduleTrustService,
            AslmMirrorServer mirrorServer,
            ModuleLaunchCoordinator moduleLaunchCoordinator,
            SettingsService settingsService,
            LegalAcceptanceService legalAcceptance,
            AppLocalizationService localization,
            IServiceProvider services)
        {
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
            _appData = appData;
            _ports = ports;
            _notifications = notifications;
            _updateManager = updateManager;
            _updateScheduler = updateScheduler;
            _moduleTrustService = moduleTrustService;
            _mirrorServer = mirrorServer;
            _moduleLaunchCoordinator = moduleLaunchCoordinator;
            _settingsService = settingsService;
            _legalAcceptance = legalAcceptance;
            _localization = localization;
            _services = services;
            _modulePages = services.GetRequiredService<ModulePagePreparationQueue>();

            // Build the XAML tree, then wire the dialog host, the rate-limit notice and localization to it.
            InitializeComponent();
            Dialogs.Initialize(this, ShellSurface, _services);
            _gitHubLimitNotice = new(this, Dialogs, _services,
                () => OpenSettingsOverlay(SettingsCategoryKind.Accounts),
                () => !_legalAcceptance.ManualAcceptanceRequired);
            LocalizableAttach.Hook(this, _localization, this);
            BindingContext = this;
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

            // Restore the sidebar width before the first render so the shell opens in the saved state.
            _panelExpanded = _appData.Data.Navigation.SidebarExpanded;
            SidePanel.WidthRequest = _panelExpanded ? PanelExpandedWidth : PanelCollapsedWidth;

            // The buttons of the system views share styling, alignment and active-state handling.
            _navButtons = [HomeButton, ConsolesButton, ModulesButton, AslmApiButton, NotificationsButton, DownloadsButton, SettingsButton];

            // Initialize the built-in buttons before the first selection and keep their native content aligned.
            CollapseButton.HandlerChanged += OnSidebarButtonHandlerChanged;
            foreach (var button in _navButtons)
            {
                button.HandlerChanged += OnSidebarButtonHandlerChanged;
                ApplyShellNavInactiveStyle(button);
            }

            ApplySidebarButtonIconFromPalette(CollapseButton, "LabelPrimary");

            // Lay out the sidebar buttons now and once more after the native handlers exist.
            ApplySidebarButtonLayout();
            ScheduleSidebarButtonLayoutRefresh();

            // Show or hide the optional system views according to the current settings.
            ApplyAslmApiNavigationState();
            ApplyConsoleNavigationState();
            ApplyHomeNavigationState();

            _localization.CultureChanged += OnLocalizationCultureChanged;
        }


        // Property change

        /// <summary>
        /// Raised when a bindable property of the shell changes.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises the shell-level property changed event.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // Startup

        /// <summary>
        /// Resubscribes the shell events on every load and, on the first load only, loads the module state,
        /// restores the configured initial shell view and starts the enabled modules.
        /// </summary>
        private async void OnPageLoaded(object? sender, EventArgs e)
        {
            // Events are unhooked when the page unloads, so every load subscribes again.
            HookShellEvents();

            if (_hasLoaded)
            {
                if (_activeModule is { } activeModule) ActivateModulePage(activeModule);
                return;
            }

            // First load: show the legal and rate-limit overlays without blocking the rest of the startup.
            _hasLoaded = true;
            _ = PresentStartupOverlaysAsync();
            ScheduleSidebarButtonLayoutRefresh();

            // The module list must be known before navigation visibility and the initial page are decided.
            await RefreshModulesAsync();
            ApplyAslmApiNavigationState();
            ApplyConsoleNavigationState();
            ApplyHomeNavigationState();
            ScheduleEnsureModuleBrowserLeftToRight();
            RestoreInitialPage();
            _ = StartModulesAndPreparationAsync();
        }

        /// <summary>
        /// Shows the legal acceptance overlay when required, then lets the GitHub rate-limit notice re-evaluate
        /// whether it may open now that legal acceptance is settled.
        /// </summary>
        private async Task PresentStartupOverlaysAsync()
        {
            await LegalAcceptanceOverlay.PresentIfRequiredAsync(OverlayContainer, _legalAcceptance, _services);
            _gitHubLimitNotice.Refresh();
        }

        /// <summary>
        /// Unhooks the localization and shell-wide service events, and on Windows the module browser helpers,
        /// when the page leaves the visual tree.
        /// </summary>
        private void OnPageUnloaded(object? sender, EventArgs e)
        {
            _localization.CultureChanged -= OnLocalizationCultureChanged;
            UnhookShellEvents();
            ClearModuleBrowserNavigationTarget();
#if WINDOWS
            CancelModuleBrowserRetry();
            ReleaseModuleWebViewDropTarget();
#endif
        }

        /// <summary>
        /// Hooks shell-wide service events once per visual lifetime.
        /// </summary>
        private void HookShellEvents()
        {
            if (_shellEventsHooked)
            {
                return;
            }

            _notifications.NotificationPublished += OnNotificationPublished;
            _notifications.NotificationWithdrawn += OnNotificationWithdrawn;
            _notifications.UpdateNotificationActionRequested += OnUpdateNotificationActionRequested;
            _mirrorServer.StateChanged += OnMirrorServerStateChanged;
            _moduleInstaller.ModulesChanged += OnModulesChanged;
            _ports.PortsRedistributed += OnPortsRedistributed;
            _moduleRunner.ConsoleStore.ModulePageStateChanged += OnModuleBrowserProcessesChanged;
            ThemeService.PaletteApplied += OnAppPaletteApplied;
            _shellEventsHooked = true;
        }

        /// <summary>
        /// Unhooks shell-wide service events when the page leaves the visual tree.
        /// </summary>
        private void UnhookShellEvents()
        {
            if (!_shellEventsHooked)
            {
                return;
            }

            _notifications.NotificationPublished -= OnNotificationPublished;
            _notifications.NotificationWithdrawn -= OnNotificationWithdrawn;
            _notifications.UpdateNotificationActionRequested -= OnUpdateNotificationActionRequested;
            _mirrorServer.StateChanged -= OnMirrorServerStateChanged;
            _moduleInstaller.ModulesChanged -= OnModulesChanged;
            _ports.PortsRedistributed -= OnPortsRedistributed;
            _moduleRunner.ConsoleStore.ModulePageStateChanged -= OnModuleBrowserProcessesChanged;
            ThemeService.PaletteApplied -= OnAppPaletteApplied;
            _shellEventsHooked = false;
        }

        /// <summary>
        /// Refreshes sidebar icon tints after the application palette is rewritten.
        /// </summary>
        private void OnAppPaletteApplied()
        {
            ApplySidebarButtonLayout();
            ScheduleSidebarButtonLayoutRefresh();

            // Apply the new palette directly in each button's current selection state.
            var selected = GetActiveSidebarButton();
            foreach (var button in _navButtons.Concat(ModulePagePanel.Children.OfType<Button>()))
            {
                if (button == selected)
                    ApplyShellNavActiveStyle(button);
                else
                    ApplyShellNavInactiveStyle(button);
            }
        }


        // Module refresh

        /// <summary>
        /// Reloads modules and their sidebar artwork before updating the shell and loaded module views.
        /// Concurrent requests are coalesced: while one refresh runs, further requests only set a flag and the
        /// running refresh repeats once more, so the last change is never lost and refreshes never overlap.
        /// </summary>
        private async Task RefreshModulesAsync()
        {
            // Record the request first, then bail out when another refresh already owns the lock.
            Interlocked.Exchange(ref _moduleRefreshQueued, 1);
            if (!await _moduleRefreshLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                // Clear the flag before discovery starts so a request made during discovery triggers another pass.
                do
                {
                    Interlocked.Exchange(ref _moduleRefreshQueued, 0);
                    var (modules, iconHashes) = await Task.Run(async () =>
                    {
                        var discovered = await _moduleInstaller.DiscoverInstalledModulesAsync(includeInstalling: true);
                        return (discovered, ReadModuleSidebarIconHashes(discovered));
                    });
                    await MainThread.InvokeOnMainThreadAsync(() => ApplyModules(modules, iconHashes));
                }
                while (Interlocked.Exchange(ref _moduleRefreshQueued, 0) == 1);
            }
            finally
            {
                _moduleRefreshLock.Release();
            }
        }

        /// <summary>
        /// Applies the latest module API snapshot to the sidebar and loaded module-backed views.
        /// </summary>
        /// <param name="modules">Fresh list of installed modules.</param>
        /// <param name="iconHashes">Fingerprints of the sidebar files found in this snapshot.</param>
        private void ApplyModules(List<ModuleConfig> modules, Dictionary<string, string> iconHashes)
        {
            // Swap in the new list and re-resolve the active module by source path in the new instances.
            var activeModuleSourcePath = _activeModule?.SourcePath;
            _allModules = modules;
            _moduleSidebarIconHashes = iconHashes;

            if (!string.IsNullOrWhiteSpace(activeModuleSourcePath))
            {
                _activeModule = _allModules.FirstOrDefault(module =>
                    string.Equals(module.SourcePath, activeModuleSourcePath, StringComparison.OrdinalIgnoreCase));
            }

            if (UpdateModulePageButtons())
                ScheduleSidebarButtonLayoutRefresh();

            // Leave the module page if the module was removed or disabled; otherwise make sure the browser still
            // shows the module's current URL.
            if ((!string.IsNullOrWhiteSpace(activeModuleSourcePath) && _activeModule == null) ||
                _activeModule is { HasPage: false } or { Status.Enabled: false })
            {
                ClearModuleBrowserNavigationTarget();
                _moduleBrowserModule = null;
                NavigateTo(HomeButton);
            }
            else if (_activeModule is { HasPage: true, Status.Enabled: true } activeModule && Browser.IsVisible)
            {
                _moduleBrowserModule = activeModule;
                RefreshModuleBrowserPage();
            }

            // Push the new snapshot into every system view that has already been created.
            if (_modulesView is ModulesView modulesView)
            {
                modulesView.RefreshModules(_allModules, _moduleInstaller, _moduleRunner);
            }

            if (_consolesView is ConsolesView consolesView)
            {
                _ = consolesView.RefreshAsync();
            }

            if (_homeView is HomeView homeView)
            {
                _ = homeView.RefreshAsync();
            }

            if (_aslmApiView is AslmApiView aslmApiView)
            {
                _ = aslmApiView.RefreshAsync();
            }
        }

        /// <summary>
        /// Refreshes module-backed UI when module metadata or the set of installed modules changes.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e)
        {
            _ = RefreshModulesAsync();
        }

        /// <summary>
        /// Refreshes the module list and page buttons after a module changes state.
        /// </summary>
        internal void OnModuleStateChanged()
        {
            _ = RefreshModulesAsync();
        }

        /// <summary>
        /// Reloads the embedded module browser on the main thread when the shared port map changes,
        /// because the active module page may now live on a different port.
        /// </summary>
        private void OnPortsRedistributed(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_activeModule is not { HasPage: true } || !Browser.IsVisible)
                {
                    return;
                }

                RefreshModuleBrowserPage();
            });
        }


        // Sidebar

        /// <summary>
        /// Refreshes ASLM API sidebar visibility on the main thread when the mirror server state changes.
        /// </summary>
        private void OnMirrorServerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(ApplyAslmApiNavigationState);
        }

        /// <summary>
        /// Shows the ASLM API navigation item only when the API server is enabled,
        /// and leaves the ASLM API view for Home when it was active and gets hidden.
        /// </summary>
        private void ApplyAslmApiNavigationState()
        {
            var isVisible = _mirrorServer.IsEnabled;
            AslmApiButton.IsVisible = isVisible;

            if (!isVisible && _activeNavButton == AslmApiButton)
            {
                NavigateTo(HomeButton);
            }
        }

        /// <summary>
        /// Shows the consoles navigation item according to the saved user preference,
        /// and leaves the consoles view for Home when it was active and gets hidden.
        /// </summary>
        private void ApplyConsoleNavigationState()
        {
            _appData.Data.Consoles.Normalize();
            var isVisible = _appData.Data.Consoles.SidebarVisible;
            ConsolesButton.IsVisible = isVisible;

            if (!isVisible && _activeNavButton == ConsolesButton)
            {
                NavigateTo(HomeButton);
            }
        }

        /// <summary>
        /// Hides the unfinished home page and leaves it when the setting is saved.
        /// When Home is the active view and gets hidden, the shell switches to Modules.
        /// </summary>
        private void ApplyHomeNavigationState()
        {
            HomeButton.IsVisible = !_appData.Data.Navigation.DisableHomePage;
            if (!HomeButton.IsVisible && _activeNavButton == HomeButton)
                NavigateTo(ModulesButton);
        }

        /// <summary>
        /// Applies the Home page visibility when the navigation settings are saved in the settings overlay.
        /// </summary>
        private void OnNavigationSettingsSaved(object? sender, EventArgs e) => ApplyHomeNavigationState();

        /// <summary>
        /// Expands or collapses the sidebar and updates every visible button.
        /// </summary>
        private void OnCollapseClicked(object? sender, EventArgs e)
        {
            // Toggle and remember the state so the next start opens the same way; like the last page, it is written
            // to the application data when ASLM closes.
            _panelExpanded = !_panelExpanded;
            _appData.Data.Navigation.SidebarExpanded = _panelExpanded;

            // Animate the panel width, then re-lay out the buttons for the new mode.
            var targetWidth = _panelExpanded ? PanelExpandedWidth : PanelCollapsedWidth;
            var animation = new Animation(
                value => SidePanel.WidthRequest = value,
                SidePanel.WidthRequest,
                targetWidth);

            animation.Commit(this, "SidebarAnimation", 16, 150, Easing.CubicOut);
            ApplySidebarButtonLayout();
            ScheduleSidebarButtonLayoutRefresh();
        }


        // Shell navigation

        /// <summary>
        /// Opens the overlay behind the Downloads, Notifications and Settings buttons,
        /// and navigates to the matching view for every other sidebar button.
        /// </summary>
        private void OnNavClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
                // These three buttons open overlays and never become the active view.
                if (button == DownloadsButton)
                {
                    OpenDownloadOverlay();
                    return;
                }

                if (button == NotificationsButton)
                {
                    OpenNotificationsOverlay();
                    return;
                }

                if (button == SettingsButton)
                {
                    OpenSettingsOverlay();
                    return;
                }

                NavigateTo(button);
            }
        }


        // Overlay navigation

        /// <summary>
        /// Shows prepared settings without rediscovering modules or restarting an in-flight load.
        /// </summary>
        /// <param name="category">Settings category to open directly, or null to just refresh the view.</param>
        private void OpenSettingsOverlay(SettingsCategoryKind? category = null)
        {
            var settingsView = EnsureSettingsHost();
            OverlayContainer.IsVisible = false;
            SettingsHost.Opacity = 1;
            SettingsHost.InputTransparent = false;
            AutomationProperties.SetExcludedWithChildren(SettingsHost, false);
            _ = settingsView.OpenAsync(category);
        }

        /// <summary>
        /// Creates the persistent native settings host without making it interactive during preparation.
        /// </summary>
        private SettingsView EnsureSettingsHost()
        {
            _settingsView ??= _services.GetRequiredService<SettingsView>();
            var settingsView = (SettingsView)_settingsView;
            settingsView.CloseRequested -= OnSettingsCloseRequested;
            settingsView.CloseRequested += OnSettingsCloseRequested;
            settingsView.NavigationSettingsSaved -= OnNavigationSettingsSaved;
            settingsView.NavigationSettingsSaved += OnNavigationSettingsSaved;
            if (!ReferenceEquals(SettingsHost.Content, settingsView))
            {
                SettingsHost.IsEnabled = true;
                SettingsHost.Content = settingsView;
            }
            return settingsView;
        }

        /// <summary>
        /// Makes cached controls noninteractive without detaching their native tree.
        /// </summary>
        private void HideSettingsHost()
        {
            if (_settingsView is SettingsView settingsView) settingsView.HideOverlay();
            SettingsHost.Opacity = 0;
            SettingsHost.InputTransparent = true;
            AutomationProperties.SetExcludedWithChildren(SettingsHost, true);
        }

        /// <summary>
        /// Opens the shared notifications overlay and refreshes it before showing.
        /// </summary>
        private void OpenNotificationsOverlay()
        {
            HideSettingsHost();
            // The popover opens next to the sidebar button that was clicked.
            _notificationsView ??= _services.GetRequiredService<NotificationsView>();
            if (_notificationsView is NotificationsView notificationsView)
            {
                notificationsView.CloseRequested -= OnNotificationsCloseRequested;
                notificationsView.CloseRequested += OnNotificationsCloseRequested;
                var anchorBounds = GetElementBoundsInShell(NotificationsButton);
                _ = notificationsView.OpenAtAsync(anchorBounds, Width, Height);
            }

            OverlayContainer.Content = _notificationsView;
            OverlayContainer.IsVisible = true;
        }

        /// <summary>
        /// Opens the shared download overlay and refreshes it before showing.
        /// </summary>
        private void OpenDownloadOverlay()
        {
            HideSettingsHost();
            _downloadsView ??= _services.GetRequiredService<DownloadsView>();
            if (_downloadsView is DownloadsView downloadsView)
            {
                downloadsView.CloseRequested -= OnDownloadCloseRequested;
                downloadsView.CloseRequested += OnDownloadCloseRequested;
                _ = downloadsView.OpenAsync();
            }

            if (!ReferenceEquals(OverlayContainer.Content, _downloadsView)) OverlayContainer.Content = _downloadsView;
            OverlayContainer.IsVisible = true;
        }

        /// <summary>
        /// Opens module metadata or configuration without starting a maintenance operation.
        /// </summary>
        /// <param name="module">Module to show.</param>
        /// <param name="section">Section of the module information view to open first.</param>
        internal void OpenModuleInfoOverlay(ModuleConfig module, ModuleInfo.Section section)
        {
            _ = OpenModuleInfoOverlayAsync(module, section);
        }

        /// <summary>
        /// Hosts the same ModuleInfo view used by Downloads, with an explicit entry section.
        /// </summary>
        /// <param name="module">Module to show.</param>
        /// <param name="section">Section of the module information view to open first.</param>
        private async Task OpenModuleInfoOverlayAsync(ModuleConfig module, ModuleInfo.Section section)
        {
            HideSettingsHost();
            // Create the view on first use, show it in the overlay, then load the module into it.
            _moduleInfoView ??= _services.GetRequiredService<ModuleInfo>();
            _moduleInfoView.CloseRequested -= OnModuleInfoCloseRequested;
            _moduleInfoView.CloseRequested += OnModuleInfoCloseRequested;
            OverlayContainer.Content = _moduleInfoView;
            OverlayContainer.IsVisible = true;
            await _moduleInfoView.OpenAsync(module, section);
        }

        /// <summary>
        /// Hides the overlay container when the settings view requests close, then applies console visibility and
        /// refreshes the Consoles and Home views, if created, so they pick up changes made in the settings.
        /// </summary>
        private void OnSettingsCloseRequested(object? sender, EventArgs e)
        {
            HideSettingsHost();
            ApplyConsoleNavigationState();

            if (_consolesView is ConsolesView consolesView)
            {
                _ = consolesView.RefreshAsync();
            }

            if (_homeView is HomeView homeView)
            {
                _ = homeView.RefreshAsync();
            }
        }

        /// <summary>
        /// Hides the overlay container and releases the popover content when the notifications view requests close.
        /// </summary>
        private void OnNotificationsCloseRequested(object? sender, EventArgs e)
        {
            OverlayContainer.IsVisible = false;
            OverlayContainer.Content = null;
        }

        /// <summary>
        /// Hides the overlay container when the download view requests close.
        /// </summary>
        private void OnDownloadCloseRequested(object? sender, EventArgs e)
        {
            OverlayContainer.IsVisible = false;
        }

        /// <summary>
        /// Hides ModuleInfo and refreshes card preferences and any newly discovered update badge.
        /// </summary>
        private void OnModuleInfoCloseRequested(object? sender, EventArgs e)
        {
            OverlayContainer.IsVisible = false;
            OnModuleStateChanged();
        }


        // Toast notifications

        /// <summary>
        /// Shows an in-app toast on the main thread when a new notification is published.
        /// </summary>
        private void OnNotificationPublished(object? sender, AppNotification notification) => ShowToast(notification);

        /// <summary>
        /// Removes the toast when its journal entry is withdrawn or superseded.
        /// </summary>
        private void OnNotificationWithdrawn(object? sender, AppNotification notification) =>
            RemoveToastById(notification.Id);

        /// <summary>
        /// Removes a previous presentation of the same notification before changing the toast stack.
        /// </summary>
        private void RemoveToastById(string id)
        {
            var toast = ToastPanel.Children.OfType<Border>().FirstOrDefault(view =>
                view.BindingContext is AppNotification item && string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (toast != null) ToastPanel.Children.Remove(toast);
        }

        /// <summary>
        /// Adds one toast card to the bottom-right stack. It disappears after a short fixed lifetime unless the
        /// notification asks to stay until the user dismisses it.
        /// </summary>
        /// <param name="notification">Notification to show.</param>
        private void ShowToast(AppNotification notification)
        {
            // Replace the prior toast for this event before applying the stack limit.
            RemoveToastById(notification.Id);
            var template = (DataTemplate)Resources["NotificationToastTemplate"];
            var toast = (Border)template.CreateContent();
            toast.BindingContext = notification;
            ToastPanel.Children.Insert(0, toast);

            while (ToastPanel.Children.Count > 4)
            {
                ToastPanel.Children.RemoveAt(ToastPanel.Children.Count - 1);
            }

            // Fade the toast in and schedule its automatic removal.
            toast.Opacity = 0;
            _ = toast.FadeToAsync(1, 120, Easing.CubicOut);
            if (!notification.SuppressToastAutoDismiss)
            {
                _ = RemoveToastAfterDelayAsync(toast, TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// Opens the journal when the user taps the content of a visible toast.
        /// </summary>
        private void OnToastBodyTapped(object? sender, TappedEventArgs e)
        {
            if (e.Parameter is not Border toast) return;

            RemoveToast(toast);
            OpenNotificationsOverlay();
        }

        /// <summary>
        /// Closes the clicked toast while preserving its journal entry.
        /// </summary>
        private void OnToastCloseClicked(object? sender, EventArgs e)
        {
            if (sender is Button { CommandParameter: Border toast })
                RemoveToast(toast);
        }

        /// <summary>
        /// Forwards the active toast's update action to the notification center.
        /// </summary>
        private void OnToastUpdateNowClicked(object? sender, EventArgs e)
        {
            if (sender is Button { BindingContext: AppNotification notification })
                _notifications.RequestUpdateNotificationAction(notification, updateNow: true);
        }

        /// <summary>
        /// Defers the module update offered by the active toast.
        /// </summary>
        private void OnToastUpdateLaterClicked(object? sender, EventArgs e)
        {
            if (sender is Button { BindingContext: AppNotification notification })
                _notifications.RequestUpdateNotificationAction(notification, updateNow: false);
        }

        /// <summary>
        /// Routes immediate updates from the journal and toasts through the same path.
        /// </summary>
        private void OnUpdateNotificationActionRequested(object? sender, UpdateNotificationActionEventArgs e) =>
            _ = ProcessUpdateNotificationNowAsync(e.Notification);

        /// <summary>
        /// Resolves an actionable source once and dispatches the update without opening a module overlay.
        /// </summary>
        private async Task ProcessUpdateNotificationNowAsync(AppNotification notification)
        {
            var key = NotificationCenter.BuildOperationKey(notification.SourceKind, notification.SourceId);
            if (!_notificationUpdates.Add(key)) return;

            try
            {
                switch (notification.SourceKind.ToLowerInvariant())
                {
                    case "module":
                        await RunModuleUpdateFromNotificationAsync(notification);
                        break;
                    case "engine":
                        await RunEngineUpdateFromNotificationAsync(notification);
                        break;
                    case "app":
                        await RunAppSelfUpdateFromNotificationAsync(notification);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateNotification] Action failed: {ex.Message}");
                if (_notifications.Notifications.Contains(notification) ||
                    notification.SourceKind == "app" && _updateManager.HasPendingAppUpdate)
                {
                    _notifications.PublishSystemToast(L.Get(LocalizationKeys.Notifications_UpdateActionTitle),
                        ex.Message, L.Get(LocalizationKeys.Notifications_ActionFailed), "update-action-failed",
                        key, AppNotificationSeverity.Error);
                }
            }
            finally
            {
                _notificationUpdates.Remove(key);
            }
        }

        /// <summary>
        /// Starts the same guarded module update as the card after resolving its installed manifest.
        /// </summary>
        private async Task RunModuleUpdateFromNotificationAsync(AppNotification notification)
        {
            var modules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync());
            if (!_notifications.Notifications.Contains(notification)) return;

            var module = modules.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, notification.SourceId, StringComparison.OrdinalIgnoreCase));
            if (module == null)
            {
                _notifications.WithdrawUpdateOffer(notification);
                _notifications.PublishSystemToast(L.Get(LocalizationKeys.Notifications_UpdateActionTitle),
                    L.Get(LocalizationKeys.Notifications_ModuleNotInstalledMessage),
                    L.Get(LocalizationKeys.Notifications_Unavailable), "module-update-unavailable",
                    notification.SourceId, AppNotificationSeverity.Warning);
                return;
            }

            if (_moduleInstaller.GetRemovalState(module.Id) == null && !_moduleInstaller.IsInstalling(module.Id) &&
                _moduleRunner.ConsoleStore.GetActivity(module.SourcePath) != ModuleActivity.Updating)
            {
                await Task.Run(() => _updateManager.UpdateModuleFromNotificationAsync(module));
                OnModuleStateChanged();
            }
        }

        /// <summary>
        /// Applies the current candidate for an engine and clears the scheduler's Ollama candidate when applied.
        /// </summary>
        private async Task RunEngineUpdateFromNotificationAsync(AppNotification notification)
        {
            var engineId = notification.SourceId;
            var channelRevision = _updateManager.SharedUpdateChannelRevision;
            var engines = await _updateManager.DiscoverInstalledEnginesAsync();
            if (!_notifications.Notifications.Contains(notification)) return;

            var engine = engines.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, engineId, StringComparison.OrdinalIgnoreCase));
            var isOllama = string.Equals(engineId, "ollama-service", StringComparison.OrdinalIgnoreCase);
            var candidate = engine != null && isOllama ? _updateScheduler.GetAvailableOllamaUpdate() : null;

            // An offer can arrive before the scheduler stores the candidate. Resolve it from the current channel.
            if (engine != null && !MatchesOfferedVersion(notification, candidate))
            {
                candidate = await Task.Run(() => _updateManager.CheckEngineUpdateAsync(
                    engine, publishUpdateNotification: false, isManualRequest: true));
            }

            if (channelRevision != _updateManager.SharedUpdateChannelRevision) return;

            if (candidate == null)
            {
                _notifications.WithdrawUpdateOffer(notification);
                _notifications.PublishSystemToast(L.Get(LocalizationKeys.Notifications_EngineUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_EngineUpdateNotAvailable),
                    L.Get(LocalizationKeys.Notifications_Unavailable), "engine-update-unavailable",
                    engineId, AppNotificationSeverity.Warning);
                return;
            }

            if (!_notifications.Notifications.Contains(notification)) return;

            var applied = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(candidate, isManualRequest: true));
            if (applied && isOllama && channelRevision == _updateManager.SharedUpdateChannelRevision)
                await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
        }

        /// <summary>
        /// Prepares ASLM if needed and hands the staged release to the launcher.
        /// </summary>
        private async Task RunAppSelfUpdateFromNotificationAsync(AppNotification notification)
        {
            var channelRevision = _updateManager.SharedUpdateChannelRevision;
            var candidate = _updateScheduler.GetAvailableAppUpdate();

            // Recheck when the visible offer is newer than the scheduler's cached candidate.
            if (!_updateManager.HasPendingAppUpdate && !MatchesOfferedVersion(notification, candidate))
            {
                candidate = await Task.Run(() => _updateManager.CheckAppUpdateAsync(
                    publishUpdateNotification: false, isManualRequest: true));
                if (channelRevision != _updateManager.SharedUpdateChannelRevision) return;
            }

            if (candidate == null && !_updateManager.HasPendingAppUpdate)
            {
                _notifications.WithdrawUpdateOffer(notification);
                _notifications.PublishSystemToast(L.Get(LocalizationKeys.Notifications_AslmUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_AslmUpdateNotAvailable),
                    L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate), "app-update-unavailable",
                    notification.SourceId, AppNotificationSeverity.Warning);
                return;
            }

            // Staging publishes progress and its own outcome. The launcher reports installation on the next startup.
            if (!_updateManager.HasPendingAppUpdate && candidate != null)
            {
                if (!_notifications.Notifications.Contains(notification)) return;

                var prepared = await Task.Run(() => _updateManager.PrepareAppUpdateAsync(candidate, isManualRequest: true));
                if (!prepared) return;

                if (channelRevision == _updateManager.SharedUpdateChannelRevision)
                    await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
            }

            _notifications.WithdrawUpdateOffer(notification);
            await _settingsService.StopAllModulesAsync();
            await Task.Run(SettingsService.StartLauncherForSelfUpdate);
            Application.Current?.Quit();
        }

        /// <summary>
        /// Checks that a cached release is the one the current notification offers.
        /// </summary>
        private static bool MatchesOfferedVersion(AppNotification notification, UpdateCandidate? candidate) =>
            candidate != null && string.Equals(notification.VersionKey, candidate.ReleaseTag ?? candidate.RemoteVersion,
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Removes a toast after the requested delay unless it has already been dismissed.
        /// </summary>
        /// <param name="toast">Toast card to remove.</param>
        /// <param name="delay">Time the toast stays visible.</param>
        private async Task RemoveToastAfterDelayAsync(Border toast, TimeSpan delay)
        {
            await Task.Delay(delay);
            await MainThread.InvokeOnMainThreadAsync(() => RemoveToast(toast));
        }

        /// <summary>
        /// Animates and removes one toast from the stack.
        /// </summary>
        /// <param name="toast">Toast card to remove.</param>
        private void RemoveToast(Border toast)
        {
            // Already removed, for example by the close button before the timer fired.
            if (!ToastPanel.Children.Contains(toast))
            {
                return;
            }

            // Fade out first, then remove the card from the stack on the main thread.
            _ = toast.FadeToAsync(0, 120, Easing.CubicIn).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() => ToastPanel.Children.Remove(toast)));
        }


        // Overlay geometry

        /// <summary>
        /// Calculates one child element's bounds in the shell coordinate space.
        /// </summary>
        /// <param name="element">Element whose bounds are translated into shell coordinates.</param>
        private Rect GetElementBoundsInShell(VisualElement element)
        {
            var x = element.Bounds.X;
            var y = element.Bounds.Y;
            var parent = element.Parent;

            // Walk the MAUI visual parent chain so popovers can anchor to controls inside nested layouts.
            while (parent is VisualElement parentElement && parentElement != this)
            {
                x += parentElement.Bounds.X;
                y += parentElement.Bounds.Y;
                parent = parentElement.Parent;
            }

            return new Rect(x, y, element.Bounds.Width, element.Bounds.Height);
        }


        // View activation

        /// <summary>
        /// Activates one shell view and updates button highlighting.
        /// Leaves any active module page while retaining its document for a later return.
        /// </summary>
        /// <param name="navButton">
        /// Sidebar button of the view to show; Home is replaced by Modules when the Home page is disabled.
        /// </param>
        private void NavigateTo(Button navButton)
        {
            // Home may be disabled by the user, in which case Modules is the landing view.
            if (navButton == HomeButton && _appData.Data.Navigation.DisableHomePage)
                navButton = ModulesButton;

            var previousSelection = GetActiveSidebarButton();

            // Release an unfinished page to background preparation and retain any already loaded document.
            if (_activeModule != null && !_moduleBrowserDocumentReady)
                ClearModuleBrowserNavigationTarget();
            else
                _modulePages.SetVisibleModulePage(null);
            _activeModule = null;
            Browser.IsVisible = false;
            ModuleStarting.IsVisible = false;

            // Highlight the new button, show its view and remember it as the page to restore on the next start.
            _activeNavButton = navButton;
            UpdateSidebarSelection(previousSelection, navButton);

            ContentArea.Content = GetViewForButton(navButton);
            RememberPage(GetRouteForButton(navButton));
        }

        /// <summary>
        /// Restores the persisted stable shell page after module discovery and sidebar visibility are ready.
        /// </summary>
        private void RestoreInitialPage()
        {
            // Restore the saved route only if its sidebar entry (or module page) is still available;
            // anything else falls back to Home at the end.
            _appData.Data.Navigation.Normalize();
            var route = _appData.Data.Navigation.GetInitialPage();
            if (string.Equals(route, ShellNavigationRoute.Consoles, StringComparison.Ordinal) &&
                ConsolesButton.IsVisible)
            {
                NavigateTo(ConsolesButton);
                return;
            }

            if (string.Equals(route, ShellNavigationRoute.Modules, StringComparison.Ordinal))
            {
                NavigateTo(ModulesButton);
                return;
            }

            if (string.Equals(route, ShellNavigationRoute.AslmApi, StringComparison.Ordinal) &&
                AslmApiButton.IsVisible)
            {
                NavigateTo(AslmApiButton);
                return;
            }

            if (ShellNavigationRoute.TryGetModuleId(route, out var moduleId))
            {
                var module = _allModules.FirstOrDefault(candidate =>
                    candidate.HasPage &&
                    candidate.Status.Enabled &&
                    string.Equals(candidate.Id, moduleId, StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    ActivateModulePage(module);
                    return;
                }
            }

            NavigateTo(HomeButton);
        }

        /// <summary>
        /// Maps one stable built-in shell button to its persisted route; buttons that are not restorable map to null.
        /// </summary>
        /// <param name="navButton">Sidebar button to map.</param>
        private string? GetRouteForButton(Button navButton)
        {
            if (navButton == HomeButton)
            {
                return ShellNavigationRoute.Home;
            }

            if (navButton == ConsolesButton)
            {
                return ShellNavigationRoute.Consoles;
            }

            if (navButton == ModulesButton)
            {
                return ShellNavigationRoute.Modules;
            }

            return navButton == AslmApiButton ? ShellNavigationRoute.AslmApi : null;
        }

        /// <summary>
        /// Updates the in-memory last-page state; the application flushes it during graceful shutdown.
        /// </summary>
        /// <param name="route">
        /// Route of the page that was opened; it is normalized before storing and empty routes are ignored.
        /// </param>
        private void RememberPage(string? route)
        {
            if (string.IsNullOrWhiteSpace(route))
            {
                return;
            }

            _appData.Data.Navigation.Normalize();
            var normalizedRoute = ShellNavigationRoute.Normalize(route);
            if (!string.Equals(_appData.Data.Navigation.LastPage, normalizedRoute, StringComparison.Ordinal))
            {
                _appData.Data.Navigation.LastPage = normalizedRoute;
            }
        }

        /// <summary>
        /// Opens the home dashboard.
        /// </summary>
        internal void OpenHome()
        {
            NavigateTo(HomeButton);
        }

        /// <summary>
        /// Opens the enabled consoles page and optionally focuses one module's merged output.
        /// </summary>
        /// <param name="moduleSourcePath">Manifest source path of the module to focus, or null to just refresh.</param>
        internal void OpenConsoles(string? moduleSourcePath = null)
        {
            // Programmatic shortcuts must respect the same preference as the sidebar.
            if (!_appData.Data.Consoles.SidebarVisible)
            {
                return;
            }

            NavigateTo(ConsolesButton);

            if (_consolesView is not ConsolesView consolesView)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(moduleSourcePath))
            {
                _ = consolesView.RefreshAsync();
                return;
            }

            _ = consolesView.ShowModuleAsync(moduleSourcePath);
        }

        /// <summary>
        /// Opens the modules page and optionally scrolls one module card into view.
        /// </summary>
        /// <param name="moduleSourcePath">
        /// Manifest source path of the module card to focus, or null to just open the page.
        /// </param>
        internal void OpenModules(string? moduleSourcePath = null)
        {
            NavigateTo(ModulesButton);

            if (string.IsNullOrWhiteSpace(moduleSourcePath) ||
                _modulesView is not ModulesView modulesView)
            {
                return;
            }

            modulesView.FocusModule(moduleSourcePath);
        }


        // View resolution

        /// <summary>
        /// Returns the cached shell view for the selected navigation button, creating it on first use.
        /// The Consoles and ASLM API views are refreshed on every activation; buttons that open overlays keep the
        /// view that is already shown.
        /// </summary>
        /// <param name="button">Sidebar button whose view is needed.</param>
        private View GetViewForButton(Button button)
        {
            if (button == HomeButton)
            {
                if (_homeView == null)
                {
                    var homeView = _services.GetRequiredService<HomeView>();
                    homeView.Initialize(this);
                    _homeView = homeView;
                }

                return _homeView;
            }

            if (button == ConsolesButton)
            {
                _consolesView ??= _services.GetRequiredService<ConsolesView>();
                if (_consolesView is ConsolesView consolesView)
                {
                    _ = consolesView.RefreshAsync();
                }

                return _consolesView;
            }

            if (button == ModulesButton)
            {
                if (_modulesView == null)
                {
                    var modulesView = _services.GetRequiredService<ModulesView>();
                    modulesView.Initialize(this, _allModules, _moduleInstaller, _moduleRunner);
                    _modulesView = modulesView;
                }

                return _modulesView;
            }

            if (button == DownloadsButton || button == NotificationsButton)
            {
                return ContentArea.Content ?? GetViewForButton(
                    _appData.Data.Navigation.DisableHomePage ? ModulesButton : HomeButton);
            }

            if (button == AslmApiButton)
            {
                _aslmApiView ??= _services.GetRequiredService<AslmApiView>();
                if (_aslmApiView is AslmApiView aslmApiView)
                {
                    _ = aslmApiView.RefreshAsync();
                }

                return _aslmApiView;
            }

            // Fallback for buttons without a view, for example Settings, which only opens an overlay.
            var unknown = new Label { Text = "Unknown page" };
            unknown.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");
            return unknown;
        }


        // Button labels

        /// <summary>
        /// Applies the localized page title and rebuilds the sidebar button labels.
        /// </summary>
        public void ApplyLocalization()
        {
            Title = L.Get(LocalizationKeys.AppShell_Title);
            ApplySidebarButtonLayout();
        }

        /// <summary>
        /// Returns the display label for one static shell button.
        /// </summary>
        /// <param name="button">Sidebar button of a system view.</param>
        private string GetButtonLabel(Button button)
        {
            // One localized caption per static sidebar button.
            if (button == HomeButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Home);
            }

            if (button == ConsolesButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Consoles);
            }

            if (button == ModulesButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Modules);
            }

            if (button == AslmApiButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Api);
            }

            if (button == NotificationsButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Notifications);
            }

            if (button == DownloadsButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Download);
            }

            if (button == SettingsButton)
            {
                return L.Get(LocalizationKeys.AppShell_Nav_Settings);
            }

            return string.Empty;
        }


        // Module page buttons

        /// <summary>
        /// Reads sidebar icon fingerprints off the UI thread so replacements at the same path are detected.
        /// </summary>
        private static Dictionary<string, string> ReadModuleSidebarIconHashes(IEnumerable<ModuleConfig> modules)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = modules.Where(module => module.HasPage && module.Status.Enabled)
                .Select(module => module.SidebarIconFullPath).OfType<string>().Distinct(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                try
                {
                    using var input = File.OpenRead(path);
                    hashes.Add(path, Convert.ToHexString(SHA256.HashData(input)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A missing or unreadable icon uses the packaged module page icon for this snapshot.
                }
            }

            return hashes;
        }

        /// <summary>
        /// Updates the module buttons in place and reports whether native icon alignment needs another pass.
        /// </summary>
        private bool UpdateModulePageButtons()
        {
            var children = ModulePagePanel.Children;
            var pageModules = _allModules
                .Where(module => module.HasPage && module.Status.Enabled)
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(module => module.SourcePath, StringComparer.OrdinalIgnoreCase)
                .DistinctBy(module => module.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var wanted = pageModules.Select(module => module.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var buttons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
            var layoutChanged = false;

            // Remove obsolete entries first so an insertion or removal does not move the other buttons.
            for (var index = children.Count - 1; index >= 0; index--)
            {
                if (children[index] is not Button { BindingContext: ModuleConfig module } button)
                    continue;

                if (wanted.Contains(module.SourcePath))
                {
                    buttons.Add(module.SourcePath, button);
                    continue;
                }

                button.Clicked -= OnModulePageClicked;
                button.HandlerChanged -= OnSidebarButtonHandlerChanged;
                children.RemoveAt(index);
                layoutChanged = true;
            }

            // Keep each live button mounted unless its position actually needs to change.
            for (var index = 0; index < pageModules.Count; index++)
            {
                var module = pageModules[index];
                if (buttons.TryGetValue(module.SourcePath, out var button))
                {
                    button.BindingContext = module;
                    var text = _panelExpanded ? module.Name : string.Empty;
                    if (button.Text != text) button.Text = text;
                    layoutChanged |= ApplySidebarButtonIconFromPalette(button, "LabelPrimary");

                    if (children[index] == button) continue;
                    children.Remove(button);
                }
                else
                {
                    button = CreateModulePageButton(module);
                }

                children.Insert(index, button);
                layoutChanged = true;
            }

            return layoutChanged;
        }

        /// <summary>
        /// Creates a sidebar button for a newly available page and gives it the current selection style.
        /// </summary>
        private Button CreateModulePageButton(ModuleConfig module)
        {
            var button = new Button
            {
                Text = _panelExpanded ? module.Name : string.Empty,
                AutomationId = module.Id,
                BindingContext = module,
                ContentLayout = new Button.ButtonContentLayout(
                    Button.ButtonContentLayout.ImagePosition.Left, _panelExpanded ? 14 : 0),
                Style = (Style)Application.Current!.Resources["SidebarButton"],
                HeightRequest = 36,
                HorizontalOptions = LayoutOptions.Fill
            };

            if (string.Equals(module.SourcePath, _activeModule?.SourcePath, StringComparison.OrdinalIgnoreCase))
                ApplyShellNavActiveStyle(button);
            else
                ApplyShellNavInactiveStyle(button);

            button.Clicked += OnModulePageClicked;
            button.HandlerChanged += OnSidebarButtonHandlerChanged;
            return button;
        }

        /// <summary>
        /// Schedules deferred sidebar layout passes until image-backed buttons finish their first native measure.
        /// </summary>
        private void ScheduleSidebarButtonLayoutRefresh()
        {
            _sidebarButtonLayoutCts?.Cancel();
            _sidebarButtonLayoutCts?.Dispose();
            _sidebarButtonLayoutCts = new CancellationTokenSource();
            _ = RefreshSidebarButtonLayoutAsync(_sidebarButtonLayoutCts.Token);
        }

        /// <summary>
        /// Re-applies sidebar button layout after image-backed buttons finish their first native measure.
        /// </summary>
        /// <param name="ct">Cancelled when a newer pass is scheduled, which stops the remaining delayed layouts.</param>
        private async Task RefreshSidebarButtonLayoutAsync(CancellationToken ct)
        {
            try
            {
                // Several passes at growing delays: the native button size is not known right after creation.
                foreach (var delay in new[] { 16, 80, 160, 320 })
                {
                    await Task.Delay(delay, ct);
                    Dispatcher.Dispatch(ApplySidebarButtonLayout);
                }
            }
            catch (OperationCanceledException)
            {
                // A newer pass superseded this one, so there is nothing left to do here.
            }
        }

        /// <summary>
        /// Applies text/icon spacing and native alignment to every sidebar button, including the collapse toggle.
        /// </summary>
        private void ApplySidebarButtonLayout()
        {
            // The icon-to-label gap only exists while the sidebar is expanded and labels are visible.
            var spacing = _panelExpanded ? 14 : 0;

            // The collapse toggle never shows a label.
            CollapseButton.Text = string.Empty;
            CollapseButton.ContentLayout = new Button.ButtonContentLayout(
                Button.ButtonContentLayout.ImagePosition.Left,
                0);
            CollapseButton.HorizontalOptions = LayoutOptions.Fill;
            UpdateButtonAlignment(CollapseButton);
            ApplySidebarButtonIconFromPalette(CollapseButton, "LabelPrimary");

            // System view buttons show their localized label only while expanded.
            foreach (var button in _navButtons)
            {
                button.Text = _panelExpanded ? GetButtonLabel(button) : string.Empty;
                button.ContentLayout = new Button.ButtonContentLayout(
                    Button.ButtonContentLayout.ImagePosition.Left,
                    spacing);
                button.HorizontalOptions = LayoutOptions.Fill;
                UpdateButtonAlignment(button);
            }

            // Module page buttons show the module name while expanded.
            foreach (var child in ModulePagePanel.Children)
            {
                if (child is not Button button)
                {
                    continue;
                }

                button.Text = _panelExpanded && button.BindingContext is ModuleConfig module
                    ? module.Name
                    : string.Empty;
                button.ContentLayout = new Button.ButtonContentLayout(
                    Button.ButtonContentLayout.ImagePosition.Left,
                    spacing);
                button.HorizontalOptions = LayoutOptions.Fill;
                UpdateButtonAlignment(button);
            }
        }

        /// <summary>
        /// Re-applies sidebar layout once the native button handler is attached.
        /// </summary>
        private void OnSidebarButtonHandlerChanged(object? sender, EventArgs e)
        {
            if (sender is not Button button || button.Handler is null)
            {
                return;
            }

            UpdateButtonAlignment(button);
            Dispatcher.Dispatch(ApplySidebarButtonLayout);
        }


        // Module page activation

        /// <summary>
        /// Opens the page using the button's latest configuration after any intervening module refresh.
        /// </summary>
        private void OnModulePageClicked(object? sender, EventArgs e)
        {
            if (sender is Button { BindingContext: ModuleConfig module }) ActivateModulePage(module);
        }

        /// <summary>
        /// Finds the selected entry from the open view or the installed module's manifest path.
        /// </summary>
        private Button? GetActiveSidebarButton() => _activeNavButton ?? ModulePagePanel.Children.OfType<Button>()
            .FirstOrDefault(button => button.BindingContext is ModuleConfig module &&
                string.Equals(module.SourcePath, _activeModule?.SourcePath, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Updates only the buttons that gained or lost selection.
        /// </summary>
        private void UpdateSidebarSelection(Button? previous, Button? selected)
        {
            if (previous == selected) return;
            if (previous is not null) ApplyShellNavInactiveStyle(previous);
            if (selected is not null) ApplyShellNavActiveStyle(selected);
        }

        /// <summary>
        /// Opens one module page inside the embedded browser and updates highlighting.
        /// </summary>
        /// <param name="module">Latest discovered configuration of the page to open.</param>
        private void ActivateModulePage(ModuleConfig module)
        {
            var previousSelection = GetActiveSidebarButton();

            // Reuse the current document when returning from a built-in view of the same live module.
            var sameModule = string.Equals(_moduleBrowserModule?.SourcePath,
                module.SourcePath, StringComparison.OrdinalIgnoreCase);
            _activeModule = module;
            _moduleBrowserModule = module;
            ContentArea.Content = null;
            Browser.IsVisible = true;
            EnsureModuleBrowserLeftToRight();
            RefreshModuleBrowserPage(force: !sameModule);
            ScheduleEnsureModuleBrowserLeftToRight();

            _activeNavButton = null;
            UpdateSidebarSelection(previousSelection, GetActiveSidebarButton());
            RememberPage(ShellNavigationRoute.ForModule(module.Id));
        }


        // Module startup

        /// <summary>
        /// Starts enabled modules that expose run commands when the shell opens.
        /// Starts preparation alongside module autostart; lifecycle reservations give each module launch precedence.
        /// </summary>
        private async Task StartModulesAndPreparationAsync()
        {
            var starts = StartEnabledModulesAsync();
            _services.GetRequiredService<DataPreparationService>().Start();
            await Task.Yield();
            EnsureSettingsHost();
            await starts;
        }

        /// <summary>
        /// Starts enabled modules through their ordinary lifecycle coordinator.
        /// </summary>
        private async Task StartEnabledModulesAsync()
        {
            var enabledModules = _allModules
                .Where(module => module.Status.Enabled && module.Commands.Run.Count > 0)
                .ToList();

            if (enabledModules.Count == 0)
            {
                return;
            }

            // Launch every module, or confirm it already runs, and log the ones that did not start.
            var startTasks = enabledModules.Select(async module =>
            {
                var logProgress = new Progress<string>(message =>
                    System.Diagnostics.Debug.WriteLine($"[ModuleStart:{module.Name}] {message}"));
                var result = await _moduleLaunchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    module.SourcePath,
                    logProgress,
                    CancellationToken.None);

                if (result.Status is not ModuleLaunchStatus.Started and not ModuleLaunchStatus.AlreadyRunning)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[ModuleStart:{module.Name}] Launch failed ({result.Status}): {result.Message}");
                }
            });

            await Task.WhenAll(startTasks);
        }


        // Module WebView

        /// <summary>
        /// Reapplies LTR on the module browser after shell RTL layout runs.
        /// </summary>
        private void OnLocalizationCultureChanged(object? sender, EventArgs e) =>
            ScheduleEnsureModuleBrowserLeftToRight();

        /// <summary>
        /// Schedules a post-layout pass that pins the module WebView to LTR.
        /// </summary>
        private void ScheduleEnsureModuleBrowserLeftToRight()
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(0), EnsureModuleBrowserLeftToRight);
        }

        /// <summary>
        /// Keeps the embedded module browser in LTR at the MAUI and WinUI layers.
        /// </summary>
        private void EnsureModuleBrowserLeftToRight()
        {
            Browser.FlowDirection = FlowDirection.LeftToRight;

#if WINDOWS
            if (Browser.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.WebView2 native)
            {
                native.FlowDirection = Microsoft.UI.Xaml.FlowDirection.LeftToRight;
            }
#endif
        }


        // Module browser state

        /// <summary>
        /// URL most recently applied to the module browser, or null when none is loaded.
        /// </summary>
        private string? _moduleBrowserUrl;

        /// <summary>
        /// URL the module browser is supposed to show for the active module page, or null when none is expected.
        /// Navigations and completion callbacks that do not match it are stale and are ignored or corrected.
        /// </summary>
        private string? _moduleBrowserExpectedUrl;

        /// <summary>
        /// Version of the latest module browser navigation request; bumping it invalidates older queued navigations.
        /// </summary>
        private int _moduleBrowserNavigationSequence;

        /// <summary>
        /// Module whose document is retained in the shared browser, including while a built-in view is open.
        /// </summary>
        private ModuleConfig? _moduleBrowserModule;

        /// <summary>
        /// Process session and URL whose readiness can be shared with the preparation worker.
        /// </summary>
        private ModulePageTarget? _moduleBrowserTarget;

        /// <summary>
        /// Whether the current document completed after the module's HTTP listener became available.
        /// </summary>
        private bool _moduleBrowserDocumentReady;

        /// <summary>
        /// Cancels only page requests, never a module command or lifecycle operation.
        /// </summary>
        private CancellationTokenSource? _moduleBrowserLoad;

        /// <summary>
        /// Navigation sequence for which a transport failure retry is queued.
        /// </summary>
        private int _moduleBrowserRetrySequence = -1;

#if WINDOWS
        /// <summary>
        /// Native WebView2 control behind the module browser, once its handler exists.
        /// </summary>
        private Microsoft.UI.Xaml.Controls.WebView2? _moduleWebView2;

        /// <summary>
        /// Whether the shell already subscribed to the navigation completion event of the WebView2 core.
        /// </summary>
        private bool _moduleWebViewNavigationHooked;

        /// <summary>
        /// Native navigation identifier that belongs to the current page request.
        /// </summary>
        private ulong _moduleBrowserNativeNavigationId;

        /// <summary>
        /// Shell request that started the current native navigation.
        /// </summary>
        private int _moduleBrowserNativeSequence;
#endif


        // Module browser navigation

        /// <summary>
        /// Refreshes only the browser presentation when existing console events announce process changes.
        /// </summary>
        private void OnModuleBrowserProcessesChanged(object? sender, EventArgs e)
        {
            if (_activeModule is null) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_shellEventsHooked && _activeModule is not null) RefreshModuleBrowserPage();
            });
        }

        /// <summary>
        /// Retains the current page until its launch or address changes.
        /// </summary>
        private void RefreshModuleBrowserPage(bool force = false)
        {
            if (_moduleBrowserModule is not { } module) return;
            var target = _modulePages.RefreshModulePageCheck(module);
            var changed = force || target != _moduleBrowserTarget;

            // A different launch or address invalidates both the document and its pending callbacks.
            if (changed)
            {
                _moduleBrowserTarget = target;
                _moduleBrowserDocumentReady = false;
                CancelModuleBrowserLoad();
                CancelModuleBrowserRetry();
                _moduleBrowserExpectedUrl = target?.Url;
                _moduleBrowserUrl = null;
                Browser.Source = null;
            }

            if (_activeModule is null) return;
            _modulePages.SetVisibleModulePage(target);
            ModuleStartingText.Text = $"{module.Name} is starting...";
            if (_moduleBrowserLoad is null && _moduleBrowserRetrySequence == -1)
            {
                // A verified run can start navigation with the browser already visible.
                var knownReady = target is { } page && _modulePages.IsModulePageReady(page);
                SetModuleStartingVisible(!_moduleBrowserDocumentReady && !knownReady);
            }

            if (!_moduleBrowserDocumentReady && _moduleBrowserLoad is null && _moduleBrowserRetrySequence == -1 &&
                target is { } pending)
                NavigateModuleBrowser(pending);
        }

        /// <summary>
        /// Updates the startup cover and its browser input state together.
        /// </summary>
        private void SetModuleStartingVisible(bool visible)
        {
            ModuleStarting.IsVisible = visible;
            Browser.Opacity = visible ? 0 : 1;
            Browser.InputTransparent = visible;
        }

        /// <summary>
        /// Clears the module browser target so handler re-creation cannot restore a stale page.
        /// Bumping the navigation sequence also invalidates navigations that were queued but have not run yet.
        /// </summary>
        private void ClearModuleBrowserNavigationTarget()
        {
            CancelModuleBrowserLoad();
            _modulePages.SetVisibleModulePage(null);
            _moduleBrowserTarget = null;
            _moduleBrowserDocumentReady = false;
            _moduleBrowserExpectedUrl = null;
            _moduleBrowserUrl = null;
            CancelModuleBrowserRetry();
            Browser.Source = null;
        }

        /// <summary>
        /// Compares two local module base URLs by scheme, host, and port.
        /// Values that are not absolute URIs are compared as text, ignoring a trailing slash and case.
        /// </summary>
        /// <param name="actual">URL the browser currently shows.</param>
        /// <param name="expected">URL the browser should show.</param>
        private static bool ModuleBrowserUrlsMatch(string? actual, string? expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            // Not both parseable: fall back to a plain text comparison.
            if (!Uri.TryCreate(actual, UriKind.Absolute, out var actualUri) ||
                !Uri.TryCreate(expected, UriKind.Absolute, out var expectedUri))
            {
                return string.Equals(
                    actual.TrimEnd('/'),
                    expected.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase);
            }

            return actualUri.Scheme == expectedUri.Scheme &&
                   actualUri.Host.Equals(expectedUri.Host, StringComparison.OrdinalIgnoreCase) &&
                   actualUri.Port == expectedUri.Port;
        }

        /// <summary>
        /// Starts the browser's load for one run once its shared HTTP check succeeds.
        /// </summary>
        private void NavigateModuleBrowser(ModulePageTarget target)
        {
            CancelModuleBrowserLoad();
            CancelModuleBrowserRetry();
            var sequence = _moduleBrowserNavigationSequence;
            _moduleBrowserExpectedUrl = target.Url;
            _moduleBrowserUrl = null;
            _moduleBrowserDocumentReady = false;
            _moduleBrowserLoad = new CancellationTokenSource();
            var token = _moduleBrowserLoad.Token;
            _ = LoadModuleBrowserPageAsync(sequence, target, token);
        }

        /// <summary>
        /// Reveals the browser as soon as the current module answers HTTP and starts loading its document.
        /// </summary>
        private async Task LoadModuleBrowserPageAsync(int sequence, ModulePageTarget target, CancellationToken token)
        {
            try
            {
                var ready = await _modulePages.WaitForModulePageAsync(target, token).ConfigureAwait(false);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (token.IsCancellationRequested || sequence != _moduleBrowserNavigationSequence) return;
                    if (!ready || _moduleBrowserModule is not { } module ||
                        !_modulePages.IsModulePageCurrent(module, target))
                    {
                        CancelModuleBrowserLoad();
                        RefreshModuleBrowserPage();
                        return;
                    }
                    SetModuleStartingVisible(false);
                    ApplyModuleBrowserNavigation(sequence, target.Url, force: true);
                });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _modulePages.IsStopping)
            {
                // Leaving or restarting this page cancels its requests without affecting the module.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ModulePage] {ex}");
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!token.IsCancellationRequested && sequence == _moduleBrowserNavigationSequence)
                        CompleteModuleBrowserNavigation(hasResponse: false);
                });
            }
        }

        /// <summary>
        /// Cancels pending browser work and invalidates callbacks without interacting with the module runner.
        /// </summary>
        private void CancelModuleBrowserLoad()
        {
            Interlocked.Increment(ref _moduleBrowserNavigationSequence);
            _moduleBrowserLoad?.Cancel();
            _moduleBrowserLoad?.Dispose();
            _moduleBrowserLoad = null;
        }

        /// <summary>
        /// Applies one module-browser navigation on the UI thread.
        /// </summary>
        /// <param name="sequence">Sequence number the request was stamped with; stale requests are dropped.</param>
        /// <param name="url">Module page URL to show.</param>
        /// <param name="force">True to navigate even when the browser already shows the same address.</param>
        private void ApplyModuleBrowserNavigation(int sequence, string url, bool force = false)
        {
            // A newer request or a page switch changed the target after this one was queued.
            if (sequence != _moduleBrowserNavigationSequence ||
                !string.Equals(_moduleBrowserExpectedUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _moduleBrowserUrl = url;

#if WINDOWS
            // Before the WebView2 core exists, assigning the source starts the load once the core is ready.
            if (_moduleWebView2?.CoreWebView2 is not Microsoft.Web.WebView2.Core.CoreWebView2 core)
            {
                Browser.Source = new UrlWebViewSource { Url = url };
                return;
            }

            // The core is ready: navigate it directly, unless it already shows this address and no reload is forced.
            WireModuleWebViewNavigation(core);

            if (!force && ModuleBrowserUrlsMatch(core.Source, url))
            {
                return;
            }

            var request = core.Environment.CreateWebResourceRequest(url, "GET", null,
                "Cache-Control: no-cache, no-store\r\n");
            core.NavigateWithWebResourceRequest(request);
#else
            Browser.Source = new UrlWebViewSource { Url = url };
#endif
        }

#if WINDOWS
        /// <summary>
        /// Subscribes to WebView2 navigation completion so stale async loads can be corrected.
        /// </summary>
        /// <param name="core">WebView2 core of the module browser.</param>
        private void WireModuleWebViewNavigation(Microsoft.Web.WebView2.Core.CoreWebView2 core)
        {
            if (_moduleWebViewNavigationHooked)
            {
                return;
            }

            core.NavigationStarting += OnModuleBrowserNavigationStarting;
            core.NavigationCompleted += OnModuleBrowserNavigationCompleted;
            _moduleWebViewNavigationHooked = true;
        }

        /// <summary>
        /// Associates native completion with its page request so cancelled loads cannot reveal a newer page.
        /// </summary>
        private void OnModuleBrowserNavigationStarting(object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
        {
            if (_moduleBrowserLoad is null || _moduleBrowserUrl is null || e.IsRedirected ||
                !ModuleBrowserUrlsMatch(e.Uri, _moduleBrowserExpectedUrl))
                return;
            _moduleBrowserNativeNavigationId = e.NavigationId;
            _moduleBrowserNativeSequence = _moduleBrowserNavigationSequence;
        }

        /// <summary>
        /// Accepts any HTTP document, including error responses, and retries transport failures.
        /// </summary>
        private void OnModuleBrowserNavigationCompleted(object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            // WinRT can project the same native browser as different managed objects for the event and property.
            // Match the navigation instead of comparing CoreWebView2 wrappers.
            if (e.NavigationId != _moduleBrowserNativeNavigationId ||
                _moduleBrowserNativeSequence != _moduleBrowserNavigationSequence)
                return;

            CompleteModuleBrowserNavigation(e.IsSuccess || e.HttpStatusCode > 0);
        }
#endif

        /// <summary>
        /// Receives document completion from MAUI on platforms without native HTTP completion handling.
        /// </summary>
        private void Browser_Navigated(object? sender, WebNavigatedEventArgs e)
        {
#if !WINDOWS
            if (_moduleBrowserUrl is not null && ModuleBrowserUrlsMatch(e.Url, _moduleBrowserExpectedUrl) &&
                e.Result != WebNavigationResult.Cancel)
                CompleteModuleBrowserNavigation(e.Result == WebNavigationResult.Success);
#endif
        }

        /// <summary>
        /// Records when the first document can be reused or retries startup navigation after a failure.
        /// </summary>
        private void CompleteModuleBrowserNavigation(bool hasResponse)
        {
            if (_moduleBrowserLoad is null || _moduleBrowserTarget is not { } target) return;
            if (!hasResponse)
            {
                CancelModuleBrowserLoad();
                _moduleBrowserUrl = null;
                if (_activeModule is not null)
                    ScheduleModuleBrowserRetry(_moduleBrowserNavigationSequence, target);
                return;
            }

            CancelModuleBrowserRetry();
            _moduleBrowserDocumentReady = true;
            _moduleBrowserLoad?.Dispose();
            _moduleBrowserLoad = null;
        }

        /// <summary>
        /// Retries one failed local navigation without overlapping requests or surviving a page switch.
        /// </summary>
        /// <param name="sequence">Navigation sequence number of the failed request.</param>
        /// <param name="target">Launch and URL that failed to load.</param>
        private void ScheduleModuleBrowserRetry(int sequence, ModulePageTarget target)
        {
            // Only one retry per navigation is pending at a time.
            if (_moduleBrowserRetrySequence == sequence)
            {
                return;
            }

            // After a short delay, retry only if the retry was not cancelled and the page is still the expected one.
            _moduleBrowserRetrySequence = sequence;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(200), () =>
            {
                if (_moduleBrowserRetrySequence != sequence)
                {
                    return;
                }

                _moduleBrowserRetrySequence = -1;
                if (sequence == _moduleBrowserNavigationSequence &&
                    _moduleBrowserTarget == target &&
                    _activeModule is not null)
                {
                    NavigateModuleBrowser(target);
                }
            });
        }

        /// <summary>
        /// Cancels the single pending module-browser retry, if any.
        /// </summary>
        private void CancelModuleBrowserRetry()
        {
            _moduleBrowserRetrySequence = -1;
        }

        /// <summary>
        /// Wires the native WebView2 once the MAUI handler attaches so module pages can receive drag-and-drop.
        /// </summary>
        private void Browser_HandlerChanged(object? sender, EventArgs e)
        {
#if WINDOWS
            // Drop the previous native control before adopting the new one.
            ReleaseModuleWebViewDropTarget();

            if (Browser.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 native)
            {
                return;
            }

            _moduleWebView2 = native;
            _moduleWebView2.CoreWebView2Initialized += OnModuleWebViewCoreInitialized;
            ApplyModuleWebViewDropTarget(_moduleWebView2);
            EnsureModuleBrowserLeftToRight();

            // A re-created handler restores a page only after its HTTP preflight has allowed navigation.
            var targetUrl = _moduleBrowserUrl;
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                ApplyModuleBrowserNavigation(_moduleBrowserNavigationSequence, targetUrl);
            }
#endif
        }

#if WINDOWS
        /// <summary>
        /// Enables drag-and-drop on the module WebView2 after the core is initialized
        /// and hooks its navigation completion event.
        /// </summary>
        private void OnModuleWebViewCoreInitialized(
            Microsoft.UI.Xaml.Controls.WebView2 sender,
            Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs e)
        {
            ApplyModuleWebViewDropTarget(sender);
            EnsureModuleBrowserLeftToRight();

            if (sender.CoreWebView2 is not Microsoft.Web.WebView2.Core.CoreWebView2 core)
            {
                return;
            }

            WireModuleWebViewNavigation(core);
        }

        /// <summary>
        /// Unhooks the WebView2 navigation and initialization handlers and forgets the native control when the
        /// browser handler is released.
        /// </summary>
        private void ReleaseModuleWebViewDropTarget()
        {
            if (_moduleWebView2 is null)
            {
                return;
            }

            if (_moduleWebView2.CoreWebView2 is Microsoft.Web.WebView2.Core.CoreWebView2 core)
            {
                core.NavigationStarting -= OnModuleBrowserNavigationStarting;
                core.NavigationCompleted -= OnModuleBrowserNavigationCompleted;
            }

            _moduleWebView2.CoreWebView2Initialized -= OnModuleWebViewCoreInitialized;
            _moduleWebView2 = null;
            _moduleWebViewNavigationHooked = false;
        }

        /// <summary>
        /// Sets AllowDrop on the native WebView2 so module pages accept HTML5 drag-and-drop.
        /// </summary>
        /// <param name="native">Native WebView2 control of the module browser.</param>
        private static void ApplyModuleWebViewDropTarget(Microsoft.UI.Xaml.Controls.WebView2 native)
        {
            native.AllowDrop = true;
        }
#endif


        // Browser safety

        /// <summary>
        /// Keeps the embedded browser on local module pages and opens external links outside the app.
        /// </summary>
        private void Browser_Navigating(object? sender, WebNavigatingEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Url) || e.Url == "about:blank")
            {
                return;
            }

            // Addresses that cannot be parsed are left to the browser.
            Uri uri;
            try
            {
                uri = new Uri(e.Url);
            }
            catch
            {
                return;
            }

            // Only loopback http(s) pages count as module pages; anything else opens in the system browser.
            var isInternal =
                (uri.Scheme == "http" || uri.Scheme == "https") &&
                (uri.Host == "127.0.0.1" || uri.Host == "localhost");

            if (!isInternal)
            {
                e.Cancel = true;
                _ = Launcher.OpenAsync(uri);
            }
        }


        // Native alignment

        /// <summary>
        /// Aligns native button content to the left to match the sidebar layout.
        /// </summary>
        /// <param name="button">Sidebar button to align.</param>
        private void UpdateButtonAlignment(Button button)
        {
            ApplyPlatformButtonAlignment(button);
#if WINDOWS
            if (button.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Button nativeButton)
            {
                nativeButton.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
                ConstrainSidebarIconSize(nativeButton);
            }
#endif
        }

        /// <summary>
        /// Applies optional sidebar content alignment supplied by the active platform folder.
        /// </summary>
        /// <param name="button">Sidebar button to align.</param>
        partial void ApplyPlatformButtonAlignment(Button button);

#if WINDOWS
        /// <summary>
        /// Pins the WinUI image inside a sidebar button to the logical icon size (avoids stretch on first wide layout).
        /// </summary>
        /// <param name="root">Native visual root to search; the first image found is constrained.</param>
        private static void ConstrainSidebarIconSize(Microsoft.UI.Xaml.DependencyObject root)
        {
            var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Microsoft.UI.Xaml.Controls.Image image)
                {
                    image.Width = SidebarIconLogicalSize;
                    image.Height = SidebarIconLogicalSize;
                    image.Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform;
                    return;
                }

                ConstrainSidebarIconSize(child);
            }
        }
#endif

        /// <summary>
        /// Applies inactive sidebar styling to one navigation button: secondary text color, no background and the
        /// icon tinted with the primary label color.
        /// </summary>
        /// <param name="button">Sidebar button to style.</param>
        private void ApplyShellNavInactiveStyle(Button button)
        {
            button.SetDynamicResource(Button.TextColorProperty, "LabelSecondary");
            button.BackgroundColor = Colors.Transparent;
            ApplySidebarButtonIconFromPalette(button, "LabelPrimary");
        }

        /// <summary>
        /// Applies active sidebar styling to one navigation button: primary text color, tertiary background and an
        /// icon tinted with the accent color of its view (the label color for buttons without an accent).
        /// </summary>
        /// <param name="button">Sidebar button to style.</param>
        private void ApplyShellNavActiveStyle(Button button)
        {
            button.SetDynamicResource(Button.TextColorProperty, "LabelPrimary");
            button.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundTertiary");
            ApplySidebarButtonIconFromPalette(button, GetActiveNavIconPaletteKey(button));
        }

        /// <summary>
        /// Replaces the sidebar button image with a Skia-tinted PNG so colors track the palette
        /// without WinUI behavior crashes.
        /// </summary>
        /// <param name="button">
        /// Sidebar button whose icon is replaced; buttons without a known icon are left unchanged.
        /// </param>
        /// <param name="paletteResourceKey">Palette resource key of the tint color.</param>
        /// <returns>Whether the button's image changed.</returns>
        private bool ApplySidebarButtonIconFromPalette(Button button, string paletteResourceKey)
        {
            var path = ResolveSidebarIconFile(button);
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var tint = IconTintHelper.ResolvePaletteColor(paletteResourceKey);
            var source = PackagedIconTintCache.Get(path, tint, _moduleSidebarIconHashes.GetValueOrDefault(path));
            if (ReferenceEquals(button.ImageSource, source)) return false;

            button.ImageSource = source;
            return true;
        }

        /// <summary>
        /// Resolves the logical packaged icon file name or an absolute module asset path for one sidebar button.
        /// </summary>
        /// <param name="button">Sidebar button whose icon file is wanted.</param>
        /// <returns>The icon file name or path, or null when the button has no known icon.</returns>
        private string? ResolveSidebarIconFile(Button button)
        {
            // Built-in buttons use packaged icons.
            if (button == CollapseButton)
            {
                return IconMenu;
            }

            if (button == HomeButton)
            {
                return IconHome;
            }

            if (button == ConsolesButton)
            {
                return IconConsole;
            }

            if (button == ModulesButton)
            {
                return IconModules;
            }

            if (button == AslmApiButton)
            {
                return IconApi;
            }

            if (button == NotificationsButton)
            {
                return IconNotifications;
            }

            if (button == DownloadsButton)
            {
                return IconDownload;
            }

            if (button == SettingsButton)
            {
                return IconSettings;
            }

            // Module page buttons use the module's own icon file, or the generic page icon without one.
            if (button.BindingContext is ModuleConfig module)
            {
                if (module.SidebarIconFullPath is { } path && _moduleSidebarIconHashes.ContainsKey(path))
                {
                    return path;
                }

                return IconPage;
            }

            return null;
        }

        /// <summary>
        /// Returns the palette resource key used to tint the sidebar icon when this navigation row is active.
        /// Home, Consoles, Modules and ASLM API have their own accent; every other button uses the label color.
        /// </summary>
        /// <param name="button">Sidebar button that is being activated.</param>
        private string GetActiveNavIconPaletteKey(Button button)
        {
            if (button == HomeButton)
            {
                return "SystemBlue";
            }

            if (button == ConsolesButton)
            {
                return "SystemPurple";
            }

            if (button == ModulesButton)
            {
                return "SystemGreen";
            }

            if (button == AslmApiButton)
            {
                return "SystemOrange";
            }

            return "LabelPrimary";
        }
    }
}
