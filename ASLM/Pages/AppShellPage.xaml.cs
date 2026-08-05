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
    /// </summary>
    public partial class AppShellPage : ContentPage, INotifyPropertyChanged, ILocalizable
    {
        private const double PanelExpandedWidth = 240;
        private const double PanelCollapsedWidth = 48;
        private const double SidebarIconLogicalSize = 20;

        private const string IconMenu = "icon_menu.png";
        private const string IconHome = "icon_home.png";
        private const string IconConsole = "icon_console.png";
        private const string IconModules = "icon_modules.png";
        private const string IconApi = "icon_api.png";
        private const string IconNotifications = "icon_notifications.png";
        private const string IconDownload = "icon_download.png";
        private const string IconSettings = "icon_settings.png";
        private const string IconPage = "icon_page.png";

        private static Color TransparentBackground => Colors.Transparent;

        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleRunner _moduleRunner;
        private readonly AppDataStore _appData;
        private readonly PortRegistry _ports;
        private readonly NotificationCenter _notifications;
        private readonly UpdateManager _updateManager;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly AslmMirrorServer _mirrorServer;
        private readonly ModuleStartThrottle _moduleStartThrottle;
        private readonly ModuleLaunchCoordinator _moduleLaunchCoordinator;
        private readonly SettingsService _settingsService;
        private readonly LegalAcceptanceService _legalAcceptance;
        private readonly AppLocalizationService _localization;
        private readonly IServiceProvider _services;

        private List<ModuleConfig> _allModules = [];
        private ModuleConfig? _activeModule;
        private bool _panelExpanded;
        private bool _hasLoaded;

        private View? _homeView;
        private View? _consolesView;
        private View? _modulesView;
        private View? _aslmApiView;
        private View? _notificationsView;
        private View? _downloadsView;
        private View? _settingsView;
        private View? _moduleUpdateView;

        private Button? _activeNavButton;
        private Button[] _navButtons = [];
        private CancellationTokenSource? _sidebarButtonLayoutCts;
        private readonly SemaphoreSlim _moduleRefreshLock = new(1, 1);
        private bool _shellEventsHooked;
        private int _moduleRefreshQueued;


        // Initialization

        /// <summary>
        /// Creates the application shell and restores the saved sidebar state.
        /// </summary>
        public AppShellPage(
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            AppDataStore appData,
            PortRegistry ports,
            NotificationCenter notifications,
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            AslmMirrorServer mirrorServer,
            ModuleStartThrottle moduleStartThrottle,
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
            _moduleTrustService = moduleTrustService;
            _mirrorServer = mirrorServer;
            _moduleStartThrottle = moduleStartThrottle;
            _moduleLaunchCoordinator = moduleLaunchCoordinator;
            _settingsService = settingsService;
            _legalAcceptance = legalAcceptance;
            _localization = localization;
            _services = services;

            InitializeComponent();
            LocalizableAttach.Hook(this, _localization, this);
            BindingContext = this;
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;

            // Restore the sidebar width before the first render so the shell opens in the saved state.
            _panelExpanded = Preferences.Default.Get("SidebarExpanded", false);
            SidePanel.WidthRequest = _panelExpanded ? PanelExpandedWidth : PanelCollapsedWidth;

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

            ApplySidebarButtonLayout();
            ScheduleSidebarButtonLayoutRefresh();

            ApplyAslmApiNavigationState();
            ApplyConsoleNavigationState();

            _localization.CultureChanged += OnLocalizationCultureChanged;
        }


        // Property change

        /// <inheritdoc />
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
        /// Loads module state once and opens the default shell view.
        /// </summary>
        private async void OnPageLoaded(object? sender, EventArgs e)
        {
            HookShellEvents();

            if (_hasLoaded)
            {
                return;
            }

            _hasLoaded = true;
            LegalAcceptanceOverlay.PresentIfRequired(OverlayContainer, _legalAcceptance, _services);
            ScheduleSidebarButtonLayoutRefresh();
            await RefreshModulesAsync();
            NavigateTo(HomeButton);
            ApplyAslmApiNavigationState();
            ApplyConsoleNavigationState();
            ScheduleEnsureModuleBrowserLeftToRight();
            _ = StartEnabledModulesAsync();
        }

        /// <summary>
        /// Unhooks shell-level notification events when the page leaves the visual tree.
        /// </summary>
        private void OnPageUnloaded(object? sender, EventArgs e)
        {
            _localization.CultureChanged -= OnLocalizationCultureChanged;
            UnhookShellEvents();
#if WINDOWS
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
        /// </summary>
        private async Task RefreshModulesAsync()
        {
            Interlocked.Exchange(ref _moduleRefreshQueued, 1);
            if (!await _moduleRefreshLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                do
                {
                    Interlocked.Exchange(ref _moduleRefreshQueued, 0);
                    var modules = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync());
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
        private void ApplyModules(List<ModuleConfig> modules)
        {
            var activeModuleSourcePath = _activeModule?.SourcePath;
            _allModules = modules;

            if (!string.IsNullOrWhiteSpace(activeModuleSourcePath))
            {
                _activeModule = _allModules.FirstOrDefault(module =>
                    string.Equals(module.SourcePath, activeModuleSourcePath, StringComparison.OrdinalIgnoreCase));
            }

            BuildPageButtons();

            if (_activeModule is { HasPage: true, Status.Enabled: false })
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
        /// Refreshes module-backed UI after a module manifest is saved.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e)
        {
            _ = RefreshModulesAsync();
        }

        /// <summary>
        /// Refreshes module page buttons after a module changes state.
        /// </summary>
        internal void OnModuleStateChanged()
        {
            _ = RefreshModulesAsync();
        }

        /// <summary>
        /// Reloads the embedded module browser when the shared port map changes.
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
        /// Refreshes ASLM API sidebar visibility when the server setting changes.
        /// </summary>
        private void OnMirrorServerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(ApplyAslmApiNavigationState);
        }

        /// <summary>
        /// Shows the ASLM API navigation item only when the API server is enabled.
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
        /// Shows the consoles navigation item according to the saved user preference.
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
        /// Expands or collapses the sidebar and updates every visible button.
        /// </summary>
        private void OnCollapseClicked(object? sender, EventArgs e)
        {
            _panelExpanded = !_panelExpanded;
            Preferences.Default.Set("SidebarExpanded", _panelExpanded);

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
        /// Routes button clicks to the shared navigation handler.
        /// </summary>
        private void OnNavClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
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
        private void OpenSettingsOverlay()
        {
            _settingsView ??= _services.GetRequiredService<SettingsView>();
            if (_settingsView is SettingsView settingsView)
            {
                settingsView.CloseRequested -= OnSettingsCloseRequested;
                settingsView.CloseRequested += OnSettingsCloseRequested;
                _ = settingsView.RefreshAsync();
            }

            OverlayContainer.Content = _settingsView;
            OverlayContainer.IsVisible = true;
        }

        /// <summary>
        /// Opens the shared notifications overlay and refreshes it before showing.
        /// </summary>
        private void OpenNotificationsOverlay()
        {
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
        /// Opens the shared module update overlay for the selected module.
        /// </summary>
        internal void OpenModuleUpdateOverlay(ModuleViewModel module, ModuleUpdateMode mode)
        {
            _ = OpenModuleUpdateOverlayAsync(module, mode);
        }

        /// <summary>
        /// Loads and shows the module update overlay before binding it to the selected module.
        /// </summary>
        private async Task OpenModuleUpdateOverlayAsync(ModuleViewModel module, ModuleUpdateMode mode)
        {
            _moduleUpdateView ??= _services.GetRequiredService<ModuleUpdateView>();
            if (_moduleUpdateView is not ModuleUpdateView moduleUpdateView)
            {
                return;
            }

            moduleUpdateView.CloseRequested -= OnModuleUpdateCloseRequested;
            moduleUpdateView.CloseRequested += OnModuleUpdateCloseRequested;
            OverlayContainer.Content = _moduleUpdateView;
            OverlayContainer.IsVisible = true;

            await moduleUpdateView.OpenAsync(module, mode);
        }

        /// <summary>
        /// Hides the overlay container when the settings view requests close.
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
        /// Hides the overlay container when the notifications view requests close.
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
        /// Hides the overlay container when the module update view requests close.
        /// </summary>
        private void OnModuleUpdateCloseRequested(object? sender, EventArgs e)
        {
            OverlayContainer.IsVisible = false;
        }


        // Toast notifications

        /// <summary>
        /// Shows an in-app toast when a new notification is published.
        /// </summary>
        private void OnNotificationPublished(object? sender, AppNotification notification)
        {
            MainThread.BeginInvokeOnMainThread(() => ShowToast(notification));
        }

        /// <summary>
        /// Adds one toast card to the bottom-right stack for a short fixed lifetime.
        /// </summary>
        private void ShowToast(AppNotification notification)
        {
            var toast = CreateToast(notification);
            ToastPanel.Children.Insert(0, toast);

            while (ToastPanel.Children.Count > 4)
            {
                ToastPanel.Children.RemoveAt(ToastPanel.Children.Count - 1);
            }

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
        private Border CreateToast(AppNotification notification)
        {
            Border toastHost = null!;

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
        /// Routes update notification actions from toasts and the notifications popover.
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
        /// Opens module update configuration or runs the ASLM self-update pipeline for one notification.
        /// </summary>
        private async Task ProcessUpdateNotificationNowAsync(AppNotification notification)
        {
            try
            {
                if (OverlayContainer.IsVisible && ReferenceEquals(OverlayContainer.Content, _notificationsView))
                {
                    OverlayContainer.IsVisible = false;
                    OverlayContainer.Content = null;
                }

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
        /// Opens the module update overlay in configure mode for the module referenced by the notification.
        /// </summary>
        private async Task OpenModuleUpdateFromNotificationAsync(string moduleId)
        {
            var modules = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync());
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

            ModuleViewModel? viewModel = null;
            if (_modulesView is ModulesView modulesView)
            {
                viewModel = modulesView.Modules.FirstOrDefault(module =>
                    string.Equals(module.SourcePath, config.SourcePath, StringComparison.OrdinalIgnoreCase));
            }

            viewModel ??= ModulesView.CreateViewModelForDeferredUpdateOverlay(
                config,
                _moduleInstaller,
                _moduleRunner,
                _moduleLaunchCoordinator,
                _updateManager,
                _moduleTrustService,
                OnModuleStateChanged);

            OpenModuleUpdateOverlay(viewModel, ModuleUpdateMode.Configure);
        }

        /// <summary>
        /// Applies an available engine update referenced by one notification.
        /// </summary>
        private async Task RunEngineUpdateFromNotificationAsync(string engineId)
        {
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

            var candidate = await Task.Run(() =>
                _updateManager.CheckEngineUpdateAsync(
                    engine,
                    publishUpdateNotification: false,
                    isManualRequest: true));

            if (candidate == null)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_EngineUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_EngineUpdateNotAvailable),
                    L.Get(LocalizationKeys.Settings_OllamaUpdate_UpToDate),
                    engineId);
                return;
            }

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
            }
        }

        /// <summary>
        /// Prepares a pending ASLM build when needed and restarts through the launcher.
        /// </summary>
        private async Task RunAppSelfUpdateFromNotificationAsync()
        {
            var candidate = await Task.Run(() =>
                _updateManager.CheckAppUpdateAsync(CancellationToken.None, publishUpdateNotification: false));

            if (candidate == null && !_updateManager.HasPendingAppUpdate)
            {
                _notifications.PublishSystemToast(
                    L.Get(LocalizationKeys.Notifications_AslmUpdateTitle),
                    L.Get(LocalizationKeys.Notifications_AslmUpdateNotAvailable),
                    L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate),
                    "aslm");
                return;
            }

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
            }

            await _settingsService.StopAllModulesAsync();
            await Task.Run(SettingsService.StartLauncherForSelfUpdate);
            Application.Current?.Quit();
        }

        /// <summary>
        /// Removes a toast after the requested delay unless it has already been dismissed.
        /// </summary>
        private async Task RemoveToastAfterDelayAsync(Border toast, TimeSpan delay)
        {
            await Task.Delay(delay);
            await MainThread.InvokeOnMainThreadAsync(() => RemoveToast(toast));
        }

        /// <summary>
        /// Animates and removes one toast from the stack.
        /// </summary>
        private void RemoveToast(Border toast)
        {
            if (!ToastPanel.Children.Contains(toast))
            {
                return;
            }

            _ = toast.FadeToAsync(0, 120, Easing.CubicIn).ContinueWith(_ =>
                MainThread.BeginInvokeOnMainThread(() => ToastPanel.Children.Remove(toast)));
        }


        // Overlay geometry

        /// <summary>
        /// Calculates one child element's bounds in the shell coordinate space.
        /// </summary>
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
        /// </summary>
        private void NavigateTo(Button navButton)
        {
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

            ApplyShellNavActiveStyle(navButton);
            _activeNavButton = navButton;

            ContentArea.Content = GetViewForButton(navButton);
        }

        /// <summary>
        /// Opens the home dashboard.
        /// </summary>
        internal void OpenHome()
        {
            NavigateTo(HomeButton);
        }

        /// <summary>
        /// Opens the consoles page and optionally focuses one module.
        /// </summary>
        internal void OpenConsoles(string? moduleSourcePath = null)
        {
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
        /// Returns the cached shell view for the selected navigation button.
        /// </summary>
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

            if (button == DownloadsButton)
            {
                if (_homeView == null)
                {
                    var homeView = _services.GetRequiredService<HomeView>();
                    homeView.Initialize(this);
                    _homeView = homeView;
                }

                return ContentArea.Content ?? _homeView;
            }

            if (button == NotificationsButton)
            {
                if (_homeView == null)
                {
                    var homeView = _services.GetRequiredService<HomeView>();
                    homeView.Initialize(this);
                    _homeView = homeView;
                }

                return ContentArea.Content ?? _homeView;
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

            var unknown = new Label { Text = "Unknown page" };
            unknown.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");
            return unknown;
        }

        // Button labels

        /// <inheritdoc />
        public void ApplyLocalization()
        {
            Title = L.Get(LocalizationKeys.AppShell_Title);
            ApplySidebarButtonLayout();
        }

        /// <summary>
        /// Returns the display label for one static shell button.
        /// </summary>
        private string GetButtonLabel(Button button)
        {
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
                ImageSource sidebarIcon;
                if (!string.IsNullOrEmpty(module.SidebarIconFullPath) && File.Exists(module.SidebarIconFullPath))
                {
                    sidebarIcon = ImageSource.FromFile(module.SidebarIconFullPath);
                }
                else
                {
                    sidebarIcon = IconPage;
                }

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
        private async Task RefreshSidebarButtonLayoutAsync(CancellationToken ct)
        {
            try
            {
                foreach (var delay in new[] { 16, 80, 160, 320 })
                {
                    await Task.Delay(delay, ct);
                    Dispatcher.Dispatch(ApplySidebarButtonLayout);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Applies text/icon spacing and native alignment to every sidebar button, including the collapse toggle.
        /// </summary>
        private void ApplySidebarButtonLayout()
        {
            var spacing = _panelExpanded ? 14 : 0;

            CollapseButton.Text = string.Empty;
            CollapseButton.ContentLayout = new Button.ButtonContentLayout(
                Button.ButtonContentLayout.ImagePosition.Left,
                0);
            CollapseButton.HorizontalOptions = LayoutOptions.Fill;
            UpdateButtonAlignment(CollapseButton);
            ApplySidebarButtonIconFromPalette(CollapseButton, "LabelPrimary");

            foreach (var button in _navButtons)
            {
                button.Text = _panelExpanded ? GetButtonLabel(button) : string.Empty;
                button.ContentLayout = new Button.ButtonContentLayout(
                    Button.ButtonContentLayout.ImagePosition.Left,
                    spacing);
                button.HorizontalOptions = LayoutOptions.Fill;
                UpdateButtonAlignment(button);
            }

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
        private void ActivateModulePage(ModuleConfig module)
        {
            var resolved = ResolveModuleConfig(module);
            if (resolved == null)
            {
                return;
            }

            _activeModule = resolved;
            var url = _ports.GetModuleUrl(resolved);

            ContentArea.Content = null;
            EnsureModuleBrowserLeftToRight();
            NavigateModuleBrowser(url);
            Browser.IsVisible = true;
            ScheduleEnsureModuleBrowserLeftToRight();

            // Clear active styling from shell buttons before highlighting the module page button.
            foreach (var button in _navButtons)
            {
                ApplyShellNavInactiveStyle(button);
            }

            _activeNavButton = null;

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
        }


        // Module startup

        /// <summary>
        /// Starts enabled modules that expose run commands when the shell opens.
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

            var startTasks = enabledModules.Select(async module =>
            {
                await _moduleStartThrottle.WaitAsync();
                try
                {
                    var logProgress = new Progress<string>(message => System.Diagnostics.Debug.WriteLine($"[ModuleStart:{module.Name}] {message}"));

                    await Task.Run(() => _moduleRunner.ExecuteRunAsync(module, logProgress, CancellationToken.None));
                }
                finally
                {
                    _moduleStartThrottle.Release();
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

        private string? _moduleBrowserUrl;
        private string? _moduleBrowserExpectedUrl;
        private int _moduleBrowserNavigationSequence;
#if WINDOWS
        private string? _pendingModuleBrowserUrl;
        private Microsoft.UI.Xaml.Controls.WebView2? _moduleWebView2;
        private bool _moduleWebViewNavigationHooked;
#endif

        /// <summary>
        /// Returns the latest discovered module matching <paramref name="module"/>.
        /// </summary>
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
        /// </summary>
        private void ClearModuleBrowserNavigationTarget()
        {
            Interlocked.Increment(ref _moduleBrowserNavigationSequence);
            _moduleBrowserExpectedUrl = null;
            _moduleBrowserUrl = null;
#if WINDOWS
            _pendingModuleBrowserUrl = null;
#endif
            Browser.Source = null;
        }

        /// <summary>
        /// Compares two local module base URLs by scheme, host, and port.
        /// </summary>
        private static bool ModuleBrowserUrlsMatch(string? actual, string? expected)
        {
            if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

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
        /// Navigates the shared module browser to one local module URL.
        /// </summary>
        private void NavigateModuleBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var sequence = Interlocked.Increment(ref _moduleBrowserNavigationSequence);
            _moduleBrowserExpectedUrl = url;
            MainThread.BeginInvokeOnMainThread(() => ApplyModuleBrowserNavigation(sequence, url));
        }

        /// <summary>
        /// Applies one module-browser navigation on the UI thread.
        /// </summary>
        private void ApplyModuleBrowserNavigation(int sequence, string url)
        {
            if (sequence != _moduleBrowserNavigationSequence ||
                !string.Equals(_moduleBrowserExpectedUrl, url, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _moduleBrowserUrl = url;
            Browser.Source = new UrlWebViewSource { Url = url };

#if WINDOWS
            _pendingModuleBrowserUrl = url;

            if (_moduleWebView2?.CoreWebView2 is not Microsoft.Web.WebView2.Core.CoreWebView2 core)
            {
                return;
            }

            _pendingModuleBrowserUrl = null;
            WireModuleWebViewNavigation(core);

            if (ModuleBrowserUrlsMatch(core.Source, url))
            {
                return;
            }

            core.Stop();
            core.Navigate(url);
#endif
        }

#if WINDOWS

        /// <summary>
        /// Subscribes to WebView2 navigation completion so stale async loads can be corrected.
        /// </summary>
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
        /// </summary>
        private void OnModuleBrowserNavigationCompleted(
            object? sender,
            Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!e.IsSuccess || !Browser.IsVisible)
            {
                return;
            }

            var expected = _moduleBrowserExpectedUrl;
            var actual = _moduleWebView2?.CoreWebView2?.Source;
            if (string.IsNullOrWhiteSpace(expected) || ModuleBrowserUrlsMatch(actual, expected))
            {
                return;
            }

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

#endif

        /// <summary>
        /// Wires the native WebView2 once the MAUI handler attaches so module pages can receive drag-and-drop.
        /// </summary>
        private void Browser_HandlerChanged(object? sender, EventArgs e)
        {
#if WINDOWS
            ReleaseModuleWebViewDropTarget();

            if (Browser.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.WebView2 native)
            {
                return;
            }

            _moduleWebView2 = native;
            _moduleWebView2.CoreWebView2Initialized += OnModuleWebViewCoreInitialized;
            ApplyModuleWebViewDropTarget(_moduleWebView2);
            EnsureModuleBrowserLeftToRight();

            var targetUrl = _moduleBrowserExpectedUrl ?? _pendingModuleBrowserUrl ?? _moduleBrowserUrl;
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                ApplyModuleBrowserNavigation(_moduleBrowserNavigationSequence, targetUrl);
            }
#endif
        }

#if WINDOWS
        /// <summary>
        /// Enables drag-and-drop on the module WebView2 after the core is initialized.
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

            var targetUrl = _moduleBrowserExpectedUrl ?? _pendingModuleBrowserUrl ?? _moduleBrowserUrl;
            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                ApplyModuleBrowserNavigation(_moduleBrowserNavigationSequence, targetUrl);
            }
        }

        /// <summary>
        /// Unhooks module WebView2 drag-and-drop handlers when the browser handler is released.
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

            Uri uri;
            try
            {
                uri = new Uri(e.Url);
            }
            catch
            {
                return;
            }

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
        /// Aligns WinUI button content to the left to match the sidebar layout.
        /// </summary>
        private void UpdateButtonAlignment(Button button)
        {
#if WINDOWS
            if (button.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Button nativeButton)
            {
                nativeButton.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
                ConstrainSidebarIconSize(nativeButton);
            }
#endif
        }

#if WINDOWS
        /// <summary>
        /// Pins the WinUI image inside a sidebar button to the logical icon size (avoids stretch on first wide layout).
        /// </summary>
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
        /// Applies inactive sidebar styling to one navigation button.
        /// </summary>
        private void ApplyShellNavInactiveStyle(Button button)
        {
            button.SetDynamicResource(Button.TextColorProperty, "LabelSecondary");
            button.BackgroundColor = Colors.Transparent;
            ApplySidebarButtonIconFromPalette(button, "LabelPrimary");
        }

        /// <summary>
        /// Applies active sidebar styling to one navigation button.
        /// </summary>
        private void ApplyShellNavActiveStyle(Button button)
        {
            button.SetDynamicResource(Button.TextColorProperty, "LabelPrimary");
            button.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundTertiary");
            ApplySidebarButtonIconFromPalette(button, GetActiveNavIconPaletteKey(button));
        }

        /// <summary>
        /// Replaces the sidebar button image with a Skia-tinted PNG so colors track the palette without WinUI behavior crashes.
        /// </summary>
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
        private string? ResolveSidebarIconFile(Button button)
        {
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
        /// </summary>
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
