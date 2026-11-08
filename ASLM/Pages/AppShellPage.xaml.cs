// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls.Shapes;

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
        /// Transparent background used by navigation buttons that are not active.
        /// </summary>
        private static Color TransparentBackground => Colors.Transparent;

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
        /// Installed modules from the last refresh, including modules that are still installing.
        /// </summary>
        private List<ModuleConfig> _allModules = [];

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
            _panelExpanded = Preferences.Default.Get("SidebarExpanded", false);
            SidePanel.WidthRequest = _panelExpanded ? PanelExpandedWidth : PanelCollapsedWidth;

            // The buttons of the system views share styling, alignment and active-state handling.
            _navButtons = [HomeButton, ConsolesButton, ModulesButton, AslmApiButton, NotificationsButton, DownloadsButton, SettingsButton];

            // Hook alignment updates once so WinUI buttons keep the same content layout.
            CollapseButton.HandlerChanged += OnSidebarButtonHandlerChanged;
            foreach (var button in _navButtons)
            {
                button.HandlerChanged += OnSidebarButtonHandlerChanged;
            }

            // Assign all static sidebar icons up front.
            HomeButton.ImageSource = IconHome;
            ConsolesButton.ImageSource = IconConsole;
            ModulesButton.ImageSource = IconModules;
            AslmApiButton.ImageSource = IconApi;
            NotificationsButton.ImageSource = IconNotifications;
            DownloadsButton.ImageSource = IconDownload;
            SettingsButton.ImageSource = IconSettings;

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
            _ = StartEnabledModulesAsync();
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
            _notifications.UpdateNotificationActionRequested += OnUpdateNotificationActionRequested;
            _mirrorServer.StateChanged += OnMirrorServerStateChanged;
            _moduleInstaller.ModulesChanged += OnModulesChanged;
            _ports.PortsRedistributed += OnPortsRedistributed;
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
            _notifications.UpdateNotificationActionRequested -= OnUpdateNotificationActionRequested;
            _mirrorServer.StateChanged -= OnMirrorServerStateChanged;
            _moduleInstaller.ModulesChanged -= OnModulesChanged;
            _ports.PortsRedistributed -= OnPortsRedistributed;
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

            // Reset every sidebar button (system views and module pages) to the inactive style of the new palette.
            foreach (var button in _navButtons)
            {
                ApplyShellNavInactiveStyle(button);
            }

            foreach (var child in ModulePagePanel.Children)
            {
                if (child is Button button)
                {
                    ApplyShellNavInactiveStyle(button);
                }
            }

            // Re-highlight the active entry: the active system view, or the button of the active module page.
            if (_activeNavButton != null)
            {
                ApplyShellNavActiveStyle(_activeNavButton);
            }
            else if (_activeModule != null)
            {
                foreach (var child in ModulePagePanel.Children)
                {
                    if (child is Button button &&
                        button.ClassId == "PAGE" &&
                        string.Equals(button.AutomationId, _activeModule.Id, StringComparison.Ordinal))
                    {
                        ApplyShellNavActiveStyle(button);
                        break;
                    }
                }
            }
        }


        // Module refresh

        /// <summary>
        /// Reloads modules, rebuilds page buttons, and refreshes the dashboard if needed.
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
                    var modules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync(includeInstalling: true));
                    await MainThread.InvokeOnMainThreadAsync(() => ApplyModules(modules));
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
        private void ApplyModules(List<ModuleConfig> modules)
        {
            // Swap in the new list and re-resolve the active module by source path in the new instances.
            var activeModuleSourcePath = _activeModule?.SourcePath;
            _allModules = modules;

            if (!string.IsNullOrWhiteSpace(activeModuleSourcePath))
            {
                _activeModule = _allModules.FirstOrDefault(module =>
                    string.Equals(module.SourcePath, activeModuleSourcePath, StringComparison.OrdinalIgnoreCase));
            }

            BuildPageButtons();

            // Leave the module page if the module was removed or disabled; otherwise make sure the browser still
            // shows the module's current URL.
            if ((!string.IsNullOrWhiteSpace(activeModuleSourcePath) && _activeModule == null) ||
                _activeModule is { HasPage: true, Status.Enabled: false })
            {
                NavigateTo(HomeButton);
            }
            else if (_activeModule is { HasPage: true, Status.Enabled: true } activeModule && Browser.IsVisible)
            {
                var url = _ports.GetModuleUrl(activeModule);
                if (!ModuleBrowserUrlsMatch(_moduleBrowserExpectedUrl, url))
                {
                    NavigateModuleBrowser(url);
                }
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
                if (_activeModule is not { HasPage: true } activeModule || !Browser.IsVisible)
                {
                    return;
                }

                NavigateModuleBrowser(_ports.GetModuleUrl(activeModule));
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
            // Toggle and remember the state so the next start opens the same way.
            _panelExpanded = !_panelExpanded;
            Preferences.Default.Set("SidebarExpanded", _panelExpanded);

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
        /// Opens the shared settings overlay and refreshes it before showing.
        /// </summary>
        /// <param name="category">Settings category to open directly, or null to just refresh the view.</param>
        private void OpenSettingsOverlay(SettingsCategoryKind? category = null)
        {
            // Create the view on first use and subscribe once (unsubscribe-first avoids duplicate handlers).
            _settingsView ??= _services.GetRequiredService<SettingsView>();
            if (_settingsView is SettingsView settingsView)
            {
                settingsView.CloseRequested -= OnSettingsCloseRequested;
                settingsView.CloseRequested += OnSettingsCloseRequested;
                settingsView.NavigationSettingsSaved -= OnNavigationSettingsSaved;
                settingsView.NavigationSettingsSaved += OnNavigationSettingsSaved;
            }

            OverlayContainer.Content = _settingsView;
            OverlayContainer.IsVisible = true;

            // Jump to the requested category, or just refresh the view.
            if (_settingsView is SettingsView visibleSettingsView)
            {
                if (category.HasValue)
                    visibleSettingsView.SelectCategory(category.Value);
                else
                    _ = visibleSettingsView.RefreshAsync();
            }
        }

        /// <summary>
        /// Opens the shared notifications overlay and refreshes it before showing.
        /// </summary>
        private void OpenNotificationsOverlay()
        {
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
            _downloadsView ??= _services.GetRequiredService<DownloadsView>();
            if (_downloadsView is DownloadsView downloadsView)
            {
                downloadsView.CloseRequested -= OnDownloadCloseRequested;
                downloadsView.CloseRequested += OnDownloadCloseRequested;
                _ = downloadsView.OpenAsync();
            }

            OverlayContainer.Content = _downloadsView;
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
            OverlayContainer.IsVisible = false;
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
        private void OnNotificationPublished(object? sender, AppNotification notification)
        {
            MainThread.BeginInvokeOnMainThread(() => ShowToast(notification));
        }

        /// <summary>
        /// Adds one toast card to the bottom-right stack. It disappears after a short fixed lifetime unless the
        /// notification asks to stay until the user dismisses it.
        /// </summary>
        /// <param name="notification">Notification to show.</param>
        private void ShowToast(AppNotification notification)
        {
            // Newest toast on top; the stack keeps at most four toasts, dropping the oldest.
            var toast = CreateToast(notification);
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
        /// Builds the compact visual toast card for one notification.
        /// Tapping the body opens the notifications popover and dismisses the toast.
        /// The close button in the top-right corner only dismisses the toast.
        /// </summary>
        /// <param name="notification">Notification the toast presents.</param>
        private Border CreateToast(AppNotification notification)
        {
            // Assigned at the end; the button handlers below capture it to remove their own toast.
            Border toastHost = null!;

            // Text labels and the close button; colors come from dynamic resources so toasts follow the theme.
            var title = new Label
            {
                Text = notification.Title,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            title.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");

            var message = new Label
            {
                Text = notification.Message,
                FontSize = 11,
                MaxLines = 2,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            message.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");

            var detail = new Label
            {
                Text = notification.DetailLine,
                FontSize = 11,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            detail.SetDynamicResource(Label.TextColorProperty, "LabelSecondary");

            var closeButton = new Button
            {
                Text = "✕",
                FontSize = 11,
                WidthRequest = 22,
                HeightRequest = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                BackgroundColor = Colors.Transparent,
                CornerRadius = 4,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Start
            };
            closeButton.SetDynamicResource(Button.TextColorProperty, "LabelSecondary");

            // Layout: accent stripe, text and actions column, close button.
            var outerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(4)),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 6
            };

            outerGrid.Children.Add(new BoxView
            {
                BackgroundColor = notification.AccentColor,
                WidthRequest = 4,
                VerticalOptions = LayoutOptions.Fill
            });

            var textStack = new VerticalStackLayout { Spacing = 2 };
            textStack.Children.Add(title);
            textStack.Children.Add(message);
            textStack.Children.Add(detail);

            var bodyColumn = new VerticalStackLayout { Spacing = 8 };
            bodyColumn.Children.Add(textStack);

            // Update notifications offer inline actions: update now (routes the action) or later (just dismisses).
            if (notification.OffersUpdateActions)
            {
                var actionRow = new HorizontalStackLayout { Spacing = 8 };

                var updateNowButton = new Button
                {
                    Text = L.Get(LocalizationKeys.Notifications_UpdateNow),
                    FontSize = 11,
                    HeightRequest = 28,
                    MinimumHeightRequest = 28,
                    Padding = new Thickness(10, 0),
                    Margin = new Thickness(0),
                    CornerRadius = 6
                };
                updateNowButton.SetDynamicResource(Button.BackgroundColorProperty, "ActionBlue");
                updateNowButton.SetDynamicResource(Button.TextColorProperty, "White");
                updateNowButton.Clicked += (_, _) =>
                {
                    RemoveToast(toastHost);
                    _notifications.RequestUpdateNotificationAction(notification, updateNow: true);
                };

                var updateLaterButton = new Button
                {
                    Text = L.Get(LocalizationKeys.Notifications_UpdateLater),
                    FontSize = 11,
                    HeightRequest = 28,
                    MinimumHeightRequest = 28,
                    Padding = new Thickness(10, 0),
                    Margin = new Thickness(0),
                    CornerRadius = 6
                };
                updateLaterButton.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundTertiary");
                updateLaterButton.SetDynamicResource(Button.TextColorProperty, "LabelPrimary");
                updateLaterButton.Clicked += (_, _) => RemoveToast(toastHost);

                actionRow.Children.Add(updateNowButton);
                actionRow.Children.Add(updateLaterButton);
                bodyColumn.Children.Add(actionRow);
            }

            outerGrid.Children.Add(bodyColumn);
            Grid.SetColumn(bodyColumn, 1);

            outerGrid.Children.Add(closeButton);
            Grid.SetColumn(closeButton, 2);

            // The card itself: rounded border with a shadow, bound to the notification.
            toastHost = new Border
            {
                BindingContext = notification,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(10),
                Content = outerGrid,
                InputTransparent = false,
                Shadow = new Shadow
                {
                    Brush = Brush.Black,
                    Opacity = 0.35f,
                    Radius = 12,
                    Offset = new Point(0, 4)
                }
            };
            toastHost.SetDynamicResource(Border.BackgroundColorProperty, "BackgroundSecondary");
            toastHost.SetDynamicResource(Border.StrokeProperty, "Separator");

            // Close button: dismiss only, does not open the notifications panel.
            closeButton.Clicked += (_, _) => RemoveToast(toastHost);

            // Tap on the toast body: open notifications panel and dismiss the toast.
            var bodyTap = new TapGestureRecognizer();
            bodyTap.Tapped += (_, _) =>
            {
                RemoveToast(toastHost);
                OpenNotificationsOverlay();
            };
            textStack.GestureRecognizers.Add(bodyTap);

            return toastHost;
        }

        /// <summary>
        /// Routes update notification actions from toasts and the notifications popover:
        /// "later" clears the deferred actions of the notification, "now" starts the update flow.
        /// </summary>
        private void OnUpdateNotificationActionRequested(object? sender, UpdateNotificationActionEventArgs e)
        {
            if (!e.UpdateNow)
            {
                _notifications.ClearUpdateNotificationDeferredActions(e.Notification);
                return;
            }

            _ = ProcessUpdateNotificationNowAsync(e.Notification);
        }

        /// <summary>
        /// Opens module update configuration, applies an engine update or runs the ASLM self-update pipeline,
        /// depending on the source of the notification. Failures are reported as a toast.
        /// </summary>
        /// <param name="notification">Update notification whose source kind and id select the action.</param>
        private async Task ProcessUpdateNotificationNowAsync(AppNotification notification)
        {
            try
            {
                // Close the notifications popover first so it does not cover the dialogs and views opened below.
                if (OverlayContainer.IsVisible && ReferenceEquals(OverlayContainer.Content, _notificationsView))
                {
                    OverlayContainer.IsVisible = false;
                    OverlayContainer.Content = null;
                }

                // Dispatch by the kind of component the notification refers to.
                if (string.Equals(notification.SourceKind, "module", StringComparison.OrdinalIgnoreCase))
                {
                    await OpenModuleUpdateFromNotificationAsync(notification.SourceId);
                    return;
                }

                if (string.Equals(notification.SourceKind, "engine", StringComparison.OrdinalIgnoreCase))
                {
                    await RunEngineUpdateFromNotificationAsync(notification.SourceId);
                    return;
                }

                if (string.Equals(notification.SourceKind, "app", StringComparison.OrdinalIgnoreCase))
                {
                    await RunAppSelfUpdateFromNotificationAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateNotification] Action failed: {ex.Message}");
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_UpdateActionTitle),
                    ex.Message,
                    L.Get(LocalizationKeys.Notifications_ActionFailed),
                    "update-action");
            }
        }

        /// <summary>
        /// Opens ModuleInfo on its status section for the installed module referenced by an update notification,
        /// or reports that the module is no longer installed.
        /// </summary>
        /// <param name="moduleId">Id of the module the notification refers to.</param>
        private async Task OpenModuleUpdateFromNotificationAsync(string moduleId)
        {
            var modules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync());
            var config = modules.FirstOrDefault(module =>
                string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase));

            if (config == null)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_UpdateActionTitle),
                    L.Get(LocalizationKeys.Notifications_ModuleNotInstalledMessage),
                    L.Get(LocalizationKeys.Notifications_Unavailable),
                    moduleId);
                return;
            }

            OpenModuleInfoOverlay(config, ModuleInfo.Section.Status);
        }

        /// <summary>
        /// Applies an available engine update referenced by one notification.
        /// Each failed step (engine gone, no update, failed update) is reported as a toast instead of an exception.
        /// </summary>
        /// <param name="engineId">Id of the engine the notification refers to.</param>
        private async Task RunEngineUpdateFromNotificationAsync(string engineId)
        {
            // Resolve the installed engine the notification refers to.
            var engines = await _updateManager.DiscoverInstalledEnginesAsync();
            var engine = engines.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, engineId, StringComparison.OrdinalIgnoreCase));

            if (engine == null)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_EngineUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_EngineUpdateNotAvailable),
                    L.Get(LocalizationKeys.Notifications_Unavailable),
                    engineId);
                return;
            }

            // Ollama uses the candidate cached by the scheduler; other engines are checked on demand.
            var candidate = string.Equals(engineId, "ollama-service", StringComparison.OrdinalIgnoreCase)
                ? _updateScheduler.GetAvailableOllamaUpdate()
                : await Task.Run(() =>
                    _updateManager.CheckEngineUpdateAsync(
                        engine,
                        publishUpdateNotification: false,
                        isManualRequest: true));

            if (candidate == null)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_EngineUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_EngineUpdateNotAvailable),
                    L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate),
                    engineId);
                return;
            }

            // Apply the update; for Ollama the scheduler's cached candidate is cleared once it was applied.
            var success = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(
                candidate,
                isManualRequest: true));

            if (!success)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_EngineUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_EngineUpdateFailed),
                    L.Get(LocalizationKeys.Notifications_ActionFailed),
                    engineId);
                return;
            }

            if (string.Equals(engineId, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
            }
        }

        /// <summary>
        /// Prepares a pending ASLM build when needed and restarts through the launcher.
        /// </summary>
        private async Task RunAppSelfUpdateFromNotificationAsync()
        {
            // Without a retained release candidate and without an already prepared update there is nothing to install.
            var candidate = _updateScheduler.GetAvailableAppUpdate();

            if (candidate == null && !_updateManager.HasPendingAppUpdate)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_AslmUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_AslmUpdateNotAvailable),
                    L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate),
                    "aslm");
                return;
            }

            // Download and stage the new build first, unless a prepared one is already pending.
            if (!_updateManager.HasPendingAppUpdate && candidate != null)
            {
                var prepared = await Task.Run(() =>
                    _updateManager.PrepareAppUpdateAsync(
                        candidate,
                        null,
                        null,
                        isManualRequest: true,
                        ct: CancellationToken.None));
                if (!prepared)
                {
                    _notifications.PublishSystemToast(
                        L.Get(LocalizationKeys.Notifications_AslmUpdateTitle),
                        L.Get(LocalizationKeys.Notifications_AslmUpdatePrepareFailed),
                        L.Get(LocalizationKeys.Notifications_AslmUpdatePrepareFailedStatus),
                        "aslm");
                    return;
                }

                await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
            }

            // Stop every module, hand over to the launcher, which installs the staged build, and quit this instance.
            await _settingsService.StopAllModulesAsync();
            await Task.Run(SettingsService.StartLauncherForSelfUpdate);
            Application.Current?.Quit();
        }

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
        /// Leaves any active module page, so the module browser is hidden and its navigation target is cleared.
        /// </summary>
        /// <param name="navButton">
        /// Sidebar button of the view to show; Home is replaced by Modules when the Home page is disabled.
        /// </param>
        private void NavigateTo(Button navButton)
        {
            // Home may be disabled by the user, in which case Modules is the landing view.
            if (navButton == HomeButton && _appData.Data.Navigation.DisableHomePage)
                navButton = ModulesButton;

            // Leave the module page: hide the module browser and forget its navigation target.
            _activeModule = null;
            ClearModuleBrowserNavigationTarget();
            Browser.IsVisible = false;

            // Clear active styling from both fixed shell buttons and dynamic module buttons.
            foreach (var button in _navButtons)
            {
                ApplyShellNavInactiveStyle(button);
            }

            foreach (var child in ModulePagePanel.Children)
            {
                if (child is Button button)
                {
                    ApplyShellNavInactiveStyle(button);
                }
            }

            // Highlight the new button, show its view and remember it as the page to restore on the next start.
            ApplyShellNavActiveStyle(navButton);
            _activeNavButton = navButton;

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
        /// Rebuilds sidebar buttons for enabled modules that expose a page.
        /// </summary>
        private void BuildPageButtons()
        {
            ModulePagePanel.Children.Clear();

            var pageModules = _allModules
                .Where(module => module.HasPage && module.Status.Enabled)
                .ToList();

            foreach (var module in pageModules)
            {
                // Use the module's own sidebar icon when its file exists, otherwise the generic page icon.
                ImageSource sidebarIcon;
                if (!string.IsNullOrEmpty(module.SidebarIconFullPath) && File.Exists(module.SidebarIconFullPath))
                {
                    sidebarIcon = ImageSource.FromFile(module.SidebarIconFullPath);
                }
                else
                {
                    sidebarIcon = IconPage;
                }

                // The module id travels in AutomationId and the PAGE class marks the button as a module page.
                var button = new Button
                {
                    Text = _panelExpanded ? module.Name : string.Empty,
                    AutomationId = module.Id,
                    BindingContext = module,
                    ClassId = "PAGE",
                    ImageSource = sidebarIcon,
                    ContentLayout = new Button.ButtonContentLayout(Button.ButtonContentLayout.ImagePosition.Left, 0),
                    Style = (Style)Application.Current!.Resources["SidebarButton"],
                    BackgroundColor = TransparentBackground,
                    HeightRequest = 36,
                    HorizontalOptions = LayoutOptions.Fill
                };

                // The click handler captures its own module instance.
                var capturedModule = module;
                button.Clicked += (_, _) => ActivateModulePage(capturedModule);
                button.HandlerChanged += OnSidebarButtonHandlerChanged;
                ModulePagePanel.Children.Add(button);
                ApplyShellNavInactiveStyle(button);
            }

            ScheduleSidebarButtonLayoutRefresh();
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
        /// Opens one module page inside the embedded browser and updates highlighting.
        /// </summary>
        /// <param name="module">Module whose page to open; the latest discovered instance of it is used.</param>
        private void ActivateModulePage(ModuleConfig module)
        {
            var resolved = ResolveModuleConfig(module);
            if (resolved == null)
            {
                return;
            }

            // A full page switch must load a fresh document even when the native browser kept the same URL.
            var forceNavigation = !Browser.IsVisible ||
                !string.Equals(_activeModule?.Id, resolved.Id, StringComparison.OrdinalIgnoreCase);
            _activeModule = resolved;
            var url = _ports.GetModuleUrl(resolved);

            // Swap the content area for the browser and point the browser at the module page.
            ContentArea.Content = null;
            EnsureModuleBrowserLeftToRight();
            NavigateModuleBrowser(url, force: forceNavigation);
            Browser.IsVisible = true;
            ScheduleEnsureModuleBrowserLeftToRight();

            // Clear active styling from shell buttons before highlighting the module page button.
            foreach (var button in _navButtons)
            {
                ApplyShellNavInactiveStyle(button);
            }

            _activeNavButton = null;

            // Highlight only the button of the opened module page.
            foreach (var child in ModulePagePanel.Children)
            {
                if (child is not Button button)
                {
                    continue;
                }

                if (button.ClassId == "PAGE" && button.AutomationId == resolved.Id)
                {
                    ApplyShellNavActiveStyle(button);
                    continue;
                }

                ApplyShellNavInactiveStyle(button);
            }

            RememberPage(ShellNavigationRoute.ForModule(resolved.Id));
        }


        // Module startup

        /// <summary>
        /// Starts enabled modules that expose run commands when the shell opens.
        /// Modules are launched in parallel; failed launches are only written to the debug output.
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

#if WINDOWS
        /// <summary>
        /// URL that must be navigated to once the WebView2 control has initialized, or null when none is waiting.
        /// </summary>
        private string? _pendingModuleBrowserUrl;

        /// <summary>
        /// Native WebView2 control behind the module browser, once its handler exists.
        /// </summary>
        private Microsoft.UI.Xaml.Controls.WebView2? _moduleWebView2;

        /// <summary>
        /// Whether the shell already subscribed to the navigation completion event of the WebView2 core.
        /// </summary>
        private bool _moduleWebViewNavigationHooked;

        /// <summary>
        /// Navigation sequence for which a retry is scheduled; -1 when no retry is pending.
        /// </summary>
        private int _moduleBrowserRetrySequence = -1;
#endif


        // Module browser navigation

        /// <summary>
        /// Returns the latest discovered module matching <paramref name="module"/>.
        /// </summary>
        /// <param name="module">Module whose current instance from the last module refresh is wanted.</param>
        private ModuleConfig? ResolveModuleConfig(ModuleConfig module)
        {
            if (string.IsNullOrWhiteSpace(module.Id))
            {
                return module;
            }

            return _allModules.FirstOrDefault(candidate =>
                       string.Equals(candidate.Id, module.Id, StringComparison.OrdinalIgnoreCase))
                   ?? module;
        }

        /// <summary>
        /// Clears the module browser target so handler re-creation cannot restore a stale page.
        /// Bumping the navigation sequence also invalidates navigations that were queued but have not run yet.
        /// </summary>
        private void ClearModuleBrowserNavigationTarget()
        {
            Interlocked.Increment(ref _moduleBrowserNavigationSequence);
            _moduleBrowserExpectedUrl = null;
            _moduleBrowserUrl = null;
#if WINDOWS
            CancelModuleBrowserRetry();
            _pendingModuleBrowserUrl = null;
#endif
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
        /// Navigates the shared module browser, optionally reloading its URL after a full page switch.
        /// Each request gets a new sequence number; the navigation is queued to the UI thread and dropped there
        /// if a newer request or a page switch superseded it while it waited.
        /// </summary>
        /// <param name="url">Module page URL to show.</param>
        /// <param name="force">True to navigate even when the browser already shows the same address.</param>
        private void NavigateModuleBrowser(string url, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

#if WINDOWS
            CancelModuleBrowserRetry();
#endif

            // Stamp the request and record it as the expected target before queueing it.
            var sequence = Interlocked.Increment(ref _moduleBrowserNavigationSequence);
            _moduleBrowserExpectedUrl = url;
            MainThread.BeginInvokeOnMainThread(() => ApplyModuleBrowserNavigation(sequence, url, force));
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
            _pendingModuleBrowserUrl = url;

            if (_moduleWebView2?.CoreWebView2 is not Microsoft.Web.WebView2.Core.CoreWebView2 core)
            {
                Browser.Source = new UrlWebViewSource { Url = url };
                return;
            }

            // The core is ready: navigate it directly, unless it already shows this address and no reload is forced.
            _pendingModuleBrowserUrl = null;
            WireModuleWebViewNavigation(core);

            if (!force && ModuleBrowserUrlsMatch(core.Source, url))
            {
                return;
            }

            core.Navigate(url);
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

            core.NavigationCompleted += OnModuleBrowserNavigationCompleted;
            _moduleWebViewNavigationHooked = true;
        }

        /// <summary>
        /// Re-navigates when an older in-flight request finishes after a newer module was selected.
        /// Failed loads that may succeed later are retried, see <see cref="ShouldRetryModuleBrowserNavigation"/>.
        /// </summary>
        private void OnModuleBrowserNavigationCompleted(
            object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!Browser.IsVisible)
            {
                return;
            }

            // A failed load is retried when the module listener may simply not be bound yet.
            var expected = _moduleBrowserExpectedUrl;
            var actual = _moduleWebView2?.CoreWebView2?.Source;
            if (!e.IsSuccess)
            {
                if (!string.IsNullOrWhiteSpace(expected) &&
                    ShouldRetryModuleBrowserNavigation(e.WebErrorStatus))
                {
                    ScheduleModuleBrowserRetry(
                        _moduleBrowserNavigationSequence,
                        expected);
                }

                return;
            }

            // The load succeeded, so no retry is needed. When it landed on the expected page there is nothing to fix.
            CancelModuleBrowserRetry();
            if (string.IsNullOrWhiteSpace(expected) || ModuleBrowserUrlsMatch(actual, expected))
            {
                return;
            }

            // An older request completed after a newer module was selected: navigate again to the expected page.
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (string.IsNullOrWhiteSpace(_moduleBrowserExpectedUrl) ||
                    !Browser.IsVisible)
                {
                    return;
                }

                ApplyModuleBrowserNavigation(_moduleBrowserNavigationSequence, _moduleBrowserExpectedUrl);
            });
        }

        /// <summary>
        /// Returns whether a failed local navigation can succeed once the module listener binds.
        /// </summary>
        /// <param name="status">Error status reported by WebView2 for the failed navigation.</param>
        private static bool ShouldRetryModuleBrowserNavigation(
            Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus status) =>
            status is Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ServerUnreachable
                or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Timeout
                or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionAborted
                or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionReset
                or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Disconnected
                or Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.CannotConnect;

        /// <summary>
        /// Retries one failed local navigation without overlapping requests or surviving a page switch.
        /// </summary>
        /// <param name="sequence">Navigation sequence number of the failed request.</param>
        /// <param name="url">URL that failed to load.</param>
        private void ScheduleModuleBrowserRetry(int sequence, string url)
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
                    string.Equals(_moduleBrowserExpectedUrl, url, StringComparison.OrdinalIgnoreCase) &&
                    Browser.IsVisible)
                {
                    ApplyModuleBrowserNavigation(sequence, url, force: true);
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
#endif

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

            // A re-created handler starts empty: restore the page the browser was meant to show.
            var targetUrl = _moduleBrowserExpectedUrl ?? _pendingModuleBrowserUrl ?? _moduleBrowserUrl;
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

            // Browser.Source was assigned while the core was initializing;
            // WebView2 will complete that one navigation without a second Navigate call.
            _pendingModuleBrowserUrl = null;
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
            if (string.IsNullOrEmpty(e.Url))
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
        private void ApplySidebarButtonIconFromPalette(Button button, string paletteResourceKey)
        {
            var path = ResolveSidebarIconFile(button);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var tint = IconTintHelper.ResolvePaletteColor(paletteResourceKey);
            button.ImageSource = PackagedIconTintCache.Get(path, tint);
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
            if (string.Equals(button.ClassId, "PAGE", StringComparison.Ordinal) &&
                button.BindingContext is ModuleConfig module)
            {
                if (!string.IsNullOrEmpty(module.SidebarIconFullPath) && File.Exists(module.SidebarIconFullPath))
                {
                    return module.SidebarIconFullPath;
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
