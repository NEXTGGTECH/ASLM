// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls;

namespace ASLM.Pages
{
    /// <summary>
    /// Displays module cards and keeps the grid responsive inside the shell.
    /// </summary>
    public partial class ModulesView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        private const double MinCardWidth = 440;
        private bool _confirmingRemoval;
        private bool _confirmingReinstall;
        private ModuleViewModel? _menuModule;
        private Rect _menuAnchor;

        private readonly UpdateManager _updateManager;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly ModuleLaunchCoordinator _launchCoordinator;
        private readonly AppLocalizationService _localization;
        private readonly AppDataStore _appData;
        private AppShellPage? _shell;
        private ModuleConsoleStore? _consoleStore;
        private int _gridSpan = 1;
        private ImageSource _moreIconSource = null!;
        private readonly HashSet<string> _removingSourcePaths = new(StringComparer.OrdinalIgnoreCase);


        // View data

        /// <summary>
        /// Stores the module cards shown in the dashboard.
        /// </summary>
        public ObservableCollection<ModuleViewModel> Modules { get; } = new();

        /// <summary>
        /// Shows the logs action only while the consoles page is enabled in saved settings.
        /// </summary>
        public bool ShowLogsAction => _appData.Data.Consoles.SidebarVisible;


        // Layout state

        /// <summary>
        /// Gets the theme-tinted more-menu glyph for module cards.
        /// </summary>
        public ImageSource MoreIconSource
        {
            get => _moreIconSource;
            private set
            {
                if (_moreIconSource == value)
                {
                    return;
                }

                _moreIconSource = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the number of columns used by the responsive grid.
        /// </summary>
        public int GridSpan
        {
            get => _gridSpan;
            set
            {
                if (_gridSpan == value)
                {
                    return;
                }

                _gridSpan = value;
                OnPropertyChanged();
            }
        }


        // Initialization

        /// <summary>
        /// Creates the module management view and hooks the resize handler.
        /// </summary>
        public ModulesView(
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            ModuleLaunchCoordinator launchCoordinator,
            AppLocalizationService localization,
            AppDataStore appData)
        {
            _updateManager = updateManager;
            _moduleTrustService = moduleTrustService;
            _launchCoordinator = launchCoordinator;
            _localization = localization;
            _appData = appData;
            InitializeComponent();
            BindingContext = this;
            DashboardView.HandlerChanged += OnDashboardViewHandlerChanged;
            DashboardView.Scrolled += OnDashboardScrolled;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            LocalizableAttach.Hook(this, _localization, this);
            RefreshMoreIconChrome();
        }

        /// <inheritdoc />
        public void ApplyLocalization()
        {
            PageTitleLabel.Text = L.Get(LocalizationKeys.Modules_Title);
            EmptyModulesLabel.Text = L.Get(LocalizationKeys.Modules_Empty);
            foreach (var module in Modules)
            {
                module.RefreshLocalizedLabels();
            }
        }


        // Notifications

        /// <inheritdoc />
        public new event PropertyChangedEventHandler? PropertyChanged;


        // Property change

        /// <summary>
        /// Raises the view-level property changed event.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // Data binding

        /// <summary>
        /// Initializes the dashboard with the current module list and services.
        /// </summary>
        internal void Initialize(
            AppShellPage shell,
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            _shell = shell;
            if (_consoleStore != null) _consoleStore.StateChanged -= OnMaintenanceChanged;
            _consoleStore = runner.ConsoleStore;
            if (IsLoaded) _consoleStore.StateChanged += OnMaintenanceChanged;
            PopulateModules(modules, installer, runner);
        }


        // Refresh

        /// <summary>
        /// Rebuilds the dashboard from the latest module list.
        /// </summary>
        internal void RefreshModules(
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            PopulateModules(modules, installer, runner);
        }

        /// <summary>
        /// Scrolls the requested module card into view.
        /// </summary>
        internal void FocusModule(string sourcePath)
        {
            var module = Modules.FirstOrDefault(candidate =>
                string.Equals(candidate.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));

            if (module == null || Dispatcher == null)
            {
                return;
            }

            Dispatcher.Dispatch(() =>
                DashboardView.ScrollTo(module, position: ScrollToPosition.MakeVisible, animate: true));
        }


        // Module population

        /// <summary>
        /// Recreates the module card view models from the current config list.
        /// </summary>
        private void PopulateModules(
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            // A rebuilt or recycled card must not leave actions bound to its previous view model.
            CloseAllMenus();
            Modules.Clear();

            foreach (var module in modules)
            {
                var viewModel = new ModuleViewModel(
                    module,
                    installer,
                    runner,
                    _launchCoordinator,
                    _updateManager,
                    _moduleTrustService,
                    OnModuleStateChanged,
                    OnMenuToggleRequested,
                    OpenConfigureUpdates,
                    UpdateModule,
                    CheckModuleUpdates,
                    OnRemoveRequested,
                    OnReinstallRequested,
                    OpenModuleInfo,
                    OpenModuleLogs);
                viewModel.SetRemoving(_removingSourcePaths.Contains(module.SourcePath));
                Modules.Add(viewModel);
            }
        }

        // State callback

        /// <summary>
        /// Forwards module state changes back to the shell.
        /// </summary>
        private void OnModuleStateChanged()
        {
            _shell?.OnModuleStateChanged();
        }


        // Menu flow

        /// <summary>
        /// Captures the tapped button's bounds in dashboard coordinates before opening the shared menu.
        /// </summary>
        private void OnModuleMenuTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not View { BindingContext: ModuleViewModel module } anchor ||
                e.GetPosition(DashboardRoot) is not { } dashboardPoint ||
                e.GetPosition(anchor) is not { } localPoint)
            {
                return;
            }

            // Native gesture coordinates include collection scrolling and recycled item offsets on both platforms.
            _menuAnchor = new Rect(dashboardPoint.X - localPoint.X, dashboardPoint.Y - localPoint.Y,
                anchor.Width, anchor.Height);
            module.ToggleMenuCommand.Execute(null);
        }

        /// <summary>
        /// Opens or closes the mini action menu for the selected module card.
        /// </summary>
        private void OnMenuToggleRequested(ModuleViewModel module)
        {
            var shouldOpen = !module.IsMenuOpen;
            CloseAllMenus();
            if (!shouldOpen)
            {
                return;
            }

            // Read saved settings on every opening so a cached dashboard never exposes disabled consoles.
            OnPropertyChanged(nameof(ShowLogsAction));

            // One dashboard-level menu can extend beyond a card without affecting the card's size.
            _menuModule = module;
            module.PropertyChanged += OnMenuModulePropertyChanged;
            ModuleMenuPopup.BindingContext = module;
            module.SetMenuOpen(true);
            ModuleMenuLayer.IsVisible = true;
            UpdateModuleMenuPosition();
            Dispatcher.Dispatch(UpdateModuleMenuPosition);
        }

        /// <summary>
        /// Closes the shared layer when a command or lifecycle transition closes its owning card menu.
        /// </summary>
        private void OnMenuModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModuleViewModel.IsMenuOpen) && _menuModule?.IsMenuOpen == false)
            {
                CloseAllMenus();
            }
        }

        /// <summary>
        /// Dismisses card actions when the user taps the surrounding dashboard.
        /// </summary>
        private void OnMenuBackdropTapped(object? sender, TappedEventArgs e) => CloseAllMenus();

        /// <summary>
        /// Measures visible actions and fits the popup to the dashboard, scrolling only when necessary.
        /// </summary>
        private void UpdateModuleMenuPosition()
        {
            if (_menuModule == null || DashboardRoot.Width <= 0 || DashboardRoot.Height <= 0)
            {
                return;
            }

            // Measure content without a height limit so adding actions never silently hides the last one.
            const double inset = 10; // Border padding and stroke on both sides.
            var viewport = new Size(DashboardRoot.Width, DashboardRoot.Height);
            var content = ModuleMenuItems.Measure(double.PositiveInfinity, double.PositiveInfinity);
            var needsScroll = content.Height + inset > viewport.Height - 8;
            var desired = new Size(content.Width + inset + (needsScroll ? 12 : 0), content.Height + inset);
            var bounds = CalculateModuleMenuBounds(_menuAnchor, viewport, desired);
            AbsoluteLayout.SetLayoutBounds(ModuleMenuPopup, bounds);
        }

        /// <summary>
        /// Aligns a menu to its button and keeps every edge inside the available dashboard viewport.
        /// </summary>
        internal static Rect CalculateModuleMenuBounds(Rect anchor, Size viewport, Size desired)
        {
            const double edge = 4;
            const double gap = 4;
            var width = Math.Min(desired.Width, Math.Max(0, viewport.Width - edge * 2));
            var height = Math.Min(desired.Height, Math.Max(0, viewport.Height - edge * 2));
            var maxX = Math.Max(edge, viewport.Width - width - edge);
            var maxY = Math.Max(edge, viewport.Height - height - edge);
            var below = anchor.Bottom + gap;
            var above = anchor.Top - gap - height;

            // Prefer below the button, then above it; tiny windows constrain the menu's scroll viewport.
            var y = below > maxY && above >= edge ? above : below;
            return new Rect(Math.Clamp(anchor.Right - width, edge, maxX),
                Math.Clamp(y, edge, maxY), width, height);
        }

        /// <summary>
        /// Opens the module update configuration overlay for the selected card.
        /// </summary>
        private void OpenConfigureUpdates(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleInfoOverlay(module.Config, ModuleInfo.Section.Configuration);
        }

        /// <summary>
        /// Checks and applies the selected update without opening an overlay or changing navigation.
        /// </summary>
        private async void UpdateModule(ModuleViewModel module)
        {
            if (module.IsBusy || module.IsRemoving)
            {
                return;
            }

            CloseAllMenus();
            await _updateManager.UpdateModuleAsync(module.Config);
            // Refresh persisted version and pending-update badges after success, failure or an empty check.
            OnModuleStateChanged();
        }

        /// <summary>
        /// Checks the module source without installing a previously found or newly discovered update.
        /// </summary>
        private async void CheckModuleUpdates(ModuleViewModel module)
        {
            if (module.IsBusy || module.IsRemoving)
            {
                return;
            }

            // Checking and updating are separate menu actions with separate service entry points.
            CloseAllMenus();
            await _updateManager.CheckModuleUpdatesAsync(module.Config);
            OnModuleStateChanged();
        }

        /// <summary>
        /// Opens public module metadata without starting any maintenance operation.
        /// </summary>
        private void OpenModuleInfo(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleInfoOverlay(module.Config, ModuleInfo.Section.Info);
        }

        /// <summary>
        /// Opens a running module's merged console when console navigation is enabled.
        /// </summary>
        private void OpenModuleLogs(ModuleViewModel module)
        {
            // Recheck both conditions in case the module stopped after the menu was opened.
            CloseAllMenus();
            if (ShowLogsAction && module.IsRunning)
            {
                _shell?.OpenConsoles(module.SourcePath);
            }
        }

        /// <summary>
        /// Closes every module mini menu in the dashboard.
        /// </summary>
        private void CloseAllMenus()
        {
            // Detach before resetting the view model to avoid recursive close notifications.
            if (_menuModule != null)
            {
                _menuModule.PropertyChanged -= OnMenuModulePropertyChanged;
                _menuModule = null;
            }
            ModuleMenuLayer.IsVisible = false;
            ModuleMenuPopup.BindingContext = null;
            foreach (var module in Modules)
            {
                module.SetMenuOpen(false);
            }
        }

        /// <summary>
        /// Confirms uninstall in the dashboard overlay and keeps the card busy throughout removal.
        /// </summary>
        private async void OnRemoveRequested(ModuleViewModel module)
        {
            if (_confirmingRemoval || _confirmingReinstall || !module.RemoveCommand.CanExecute(null)) return;
            CloseAllMenus();
            _confirmingRemoval = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.For(this).ConfirmAsync(this,
                    DialogText.Localized(LocalizationKeys.Modules_RemoveConfirmTitle, module.Name),
                    DialogText.Localized(LocalizationKeys.Modules_RemoveConfirmFormat, module.Name),
                    DialogText.Localized(LocalizationKeys.Modules_Remove), danger: true);
            }
            finally { _confirmingRemoval = false; }
            if (!confirmed) return;
            if (!module.RemoveCommand.CanExecute(null)) return;

            SetRemovingModule(module.SourcePath, true);
            string? retainedFiles = null;
            Exception? failure = null;
            try
            {
                retainedFiles = await module.UninstallAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // A manifest refresh may have replaced the card while the uninstall was running.
                module.SetRemoving(false);
                SetRemovingModule(module.SourcePath, false);
            }

            if (!IsLoaded) return;
            if (failure != null)
                await DialogService.For(this).AlertAsync(this, DialogText.Localized(LocalizationKeys.Modules_RemoveFailed), failure.Message);
            else if (retainedFiles != null)
                await DialogService.For(this).AlertAsync(this, DialogText.Localized(LocalizationKeys.Modules_RemoveCleanupTitle),
                    DialogText.Localized(LocalizationKeys.Modules_RemoveCleanupFormat, retainedFiles));
        }

        /// <summary>
        /// Confirms a clean reinstall with a detached copy of the latest saved update preferences.
        /// </summary>
        private async void OnReinstallRequested(ModuleViewModel module)
        {
            if (_confirmingRemoval || _confirmingReinstall || !module.ReinstallCommand.CanExecute(null)) return;
            CloseAllMenus();
            _confirmingReinstall = true;
            try
            {
                ModuleUpdateConfig selection;
                // Dispose the form as soon as it closes; the queued job owns its own preferences snapshot.
                try
                {
                    using var form = new ModuleReinstallForm(await module.LoadReinstallConfigAsync(), _updateManager);
                    if (!IsLoaded) return;
                    var dialog = DialogService.For(this).ShowAsync(this, form.Request);
                    _ = form.LoadOptionsAsync();
                    var result = await dialog;
                    if (result.IsCancelled) return;
                    selection = form.BuildSelection(result.Values);
                }
                finally { _confirmingReinstall = false; }

                if (!module.ReinstallCommand.CanExecute(null)) return;
                var launch = await module.ReinstallAsync(selection);
                if (IsLoaded && launch.Status is not (ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning or ModuleLaunchStatus.NoRunCommands))
                    await DialogService.For(this).AlertAsync(this,
                        module.Name,
                        DialogText.Localized(LocalizationKeys.ModuleInfo_Failed, launch.Message ?? launch.Status.ToString()));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (IsLoaded) await DialogService.For(this).AlertAsync(this,
                    DialogText.Localized(LocalizationKeys.Modules_ReinstallFailed), ex.Message);
            }
        }

        /// <summary>
        /// Keeps removal state consistent if manifest discovery replaces a visible card.
        /// </summary>
        private void SetRemovingModule(string sourcePath, bool removing)
        {
            if (removing) _removingSourcePaths.Add(sourcePath);
            else _removingSourcePaths.Remove(sourcePath);
            foreach (var module in Modules)
                module.SetRemoving(_removingSourcePaths.Contains(module.SourcePath));
        }


        // Layout events

        /// <summary>
        /// Recalculates the grid layout when the view size changes.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e)
        {
            CloseAllMenus();
            RecalculateGridSpan();
        }

        /// <summary>
        /// Dismisses actions before collection scrolling moves or recycles their anchor card.
        /// </summary>
        private void OnDashboardScrolled(object? sender, ItemsViewScrolledEventArgs e) => CloseAllMenus();

        /// <summary>
        /// Reapplies native list behavior after the platform view enters the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            _updateManager.ModuleUpdateStateChanged -= OnMaintenanceChanged;
            _updateManager.ModuleUpdateStateChanged += OnMaintenanceChanged;
            if (_consoleStore != null)
            {
                _consoleStore.StateChanged -= OnMaintenanceChanged;
                _consoleStore.StateChanged += OnMaintenanceChanged;
                foreach (var module in Modules) module.RefreshMaintenanceState();
            }
            ThemeService.PaletteApplied -= OnPaletteAppliedForMoreIcon;
            ThemeService.PaletteApplied += OnPaletteAppliedForMoreIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            RefreshMoreIconChrome();
            DisableDashboardItemTransitions();
        }

        /// <summary>
        /// Unsubscribes from theme events when the view leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            CloseAllMenus();
            _updateManager.ModuleUpdateStateChanged -= OnMaintenanceChanged;
            if (_consoleStore != null) _consoleStore.StateChanged -= OnMaintenanceChanged;
            ThemeService.PaletteApplied -= OnPaletteAppliedForMoreIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }
        }

        /// <summary>
        /// Keeps existing cards in sync with updates started outside the dashboard.
        /// </summary>
        private void OnMaintenanceChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsLoaded) return;
            foreach (var module in Modules) module.RefreshMaintenanceState();
        });

        /// <summary>
        /// Refreshes the more-menu icon after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForMoreIcon()
        {
            MainThread.BeginInvokeOnMainThread(RefreshMoreIconChrome);
        }

        /// <summary>
        /// Refreshes the more-menu icon when the application theme changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshMoreIconChrome);
        }

        /// <summary>
        /// Recomputes the tinted more-menu icon for the current palette.
        /// </summary>
        private void RefreshMoreIconChrome()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            MoreIconSource = PackagedIconTintCache.Get("icon_more.png", iconTint);
        }

        /// <summary>
        /// Removes native list entrance transitions as soon as the dashboard list is materialized.
        /// </summary>
        private void OnDashboardViewHandlerChanged(object? sender, EventArgs e)
        {
            DisableDashboardItemTransitions();
        }


        // Background input

        /// <summary>
        /// Closes any open module menu when the user taps outside the popup actions.
        /// </summary>
        private void OnBackgroundTapped(object? sender, TappedEventArgs e)
        {
            CloseAllMenus();
        }


        // Layout calculation

        /// <summary>
        /// Computes the number of columns that fit into the available width.
        /// </summary>
        private void RecalculateGridSpan()
        {
            var availableWidth = Width - 60;
            if (availableWidth > 0)
            {
                GridSpan = Math.Max(1, (int)(availableWidth / MinCardWidth));
            }
        }


        // Native list behavior

        /// <summary>
        /// Disables platform item transitions for the module dashboard.
        /// </summary>
        private void DisableDashboardItemTransitions()
        {
#if WINDOWS
            if (DashboardView.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject platformView)
            {
                return;
            }

            DisableWinUiItemTransitions(platformView);
#endif
        }

#if WINDOWS
        /// <summary>
        /// Clears WinUI item transitions from the native collection surface and its visual children.
        /// </summary>
        private static void DisableWinUiItemTransitions(Microsoft.UI.Xaml.DependencyObject element)
        {
            if (element is Microsoft.UI.Xaml.Controls.ListViewBase listView)
            {
                listView.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            }

            var childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < childrenCount; index++)
            {
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, index) is Microsoft.UI.Xaml.DependencyObject child)
                {
                    DisableWinUiItemTransitions(child);
                }
            }
        }
#endif
    }

    /// <summary>
    /// Wraps one module config and exposes UI state and commands for its card.
    /// </summary>
    public class ModuleViewModel : INotifyPropertyChanged
    {
        private readonly ModuleConfig _config;
        private readonly ModuleInstaller _installer;
        private readonly ModuleRunner _runner;
        private readonly ModuleLaunchCoordinator _launchCoordinator;
        private readonly UpdateManager _updateManager;
        private readonly Action _onStateChanged;
        private readonly Action<ModuleViewModel> _onMenuToggleRequested;
        private readonly Action<ModuleViewModel> _onConfigureUpdatesRequested;
        private readonly Action<ModuleViewModel> _onUpdateRequested;
        private readonly Action<ModuleViewModel> _onCheckUpdatesRequested;
        private readonly Action<ModuleViewModel> _onRemoveRequested;
        private readonly Action<ModuleViewModel>? _onReinstallRequested;
        private readonly Action<ModuleViewModel>? _onLogsRequested;
        private readonly Command _launchCommand;
        private readonly Command _stopCommand;
        private readonly Command _restartCommand;
        private readonly Command _checkUpdateCommand;
        private readonly Command _closeMenuCommand;
        private readonly Command _removeCommand;
        private readonly Command _reinstallCommand;
        private bool _isRemoving;
        private bool _isRestarting;
        private bool _isStarting;
        private bool _isMenuOpen;
        private ModuleActivity? _lastMaintenanceActivity;
        private bool _lastUpdatePending;
        private bool _lastCheckingUpdates;


        // Notifications

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;


        private readonly ModuleTrustLevel _trustLevel;


        // Initialization

        /// <summary>
        /// Creates the card view model for one module.
        /// </summary>
        public ModuleViewModel(
            ModuleConfig config,
            ModuleInstaller installer,
            ModuleRunner runner,
            ModuleLaunchCoordinator launchCoordinator,
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            Action onStateChanged,
            Action<ModuleViewModel> onMenuToggleRequested,
            Action<ModuleViewModel> onConfigureUpdatesRequested,
            Action<ModuleViewModel> onUpdateRequested,
            Action<ModuleViewModel> onCheckUpdatesRequested,
            Action<ModuleViewModel> onRemoveRequested,
            Action<ModuleViewModel>? onReinstallRequested = null,
            Action<ModuleViewModel>? onInfoRequested = null,
            Action<ModuleViewModel>? onLogsRequested = null)
        {
            _config = config;
            _installer = installer;
            _runner = runner;
            _launchCoordinator = launchCoordinator;
            _updateManager = updateManager;
            _trustLevel = moduleTrustService.Resolve(config);
            _onStateChanged = onStateChanged;
            _onMenuToggleRequested = onMenuToggleRequested;
            _onConfigureUpdatesRequested = onConfigureUpdatesRequested;
            _onUpdateRequested = onUpdateRequested;
            _onCheckUpdatesRequested = onCheckUpdatesRequested;
            _onRemoveRequested = onRemoveRequested;
            _onReinstallRequested = onReinstallRequested;
            _onLogsRequested = onLogsRequested;

            // Normalize once so every card starts from the persisted update preferences.
            _config.Normalize();
            
            _launchCommand = new Command(OnLaunch, CanLaunch);
            _stopCommand = new Command(OnStop, CanStop);
            _restartCommand = new Command(OnRestart, CanRestart);
            ToggleMenuCommand = new Command(ExecuteToggleMenuCommand);
            _closeMenuCommand = new Command(ExecuteCloseMenuCommand);
            OpenConfigureUpdatesCommand = new Command(ExecuteOpenConfigureUpdatesCommand);
            OpenInfoCommand = new Command(() => { SetMenuOpen(false); onInfoRequested?.Invoke(this); });
            ViewLogsCommand = new Command(ExecuteViewLogsCommand);
            UpdateCommand = new Command(ExecuteUpdateCommand);
            _checkUpdateCommand = new Command(ExecuteCheckUpdateCommand, CanCheckOrUpdate);
            _removeCommand = new Command(OnRemove, () => CanUninstall && !IsRemoving && !IsBusy && !IsStarting && !IsRestarting);
            _reinstallCommand = new Command(OnReinstall, () => !IsRemoving && !IsBusy && !IsStarting && !IsRestarting &&
                string.IsNullOrEmpty(_installer.GetPendingConsumers(_config.Id)));

            LaunchCommand = _launchCommand;
            StopCommand = _stopCommand;
            RestartCommand = _restartCommand;
            CheckUpdateCommand = _checkUpdateCommand;
            CloseMenuCommand = _closeMenuCommand;
            RefreshLocalizedLabels();
        }


        // Localized card labels

        public ICommand RemoveCommand => _removeCommand;
        internal ModuleConfig Config => _config;
        public ICommand OpenInfoCommand { get; }
        public string InfoLabel => L.Get(LocalizationKeys.ModuleInfo_InfoTab);
        public bool CanUninstall => !ModuleRegistry.IsRequired(_config.Id) && string.IsNullOrEmpty(_installer.GetPendingConsumers(_config.Id));
        public string RemoveLabel => L.Get(LocalizationKeys.Modules_Remove);
        public ICommand ReinstallCommand => _reinstallCommand;
        public string ReinstallLabel => L.Get(LocalizationKeys.Modules_Reinstall);
        public string RemovingLabel => L.Get(_installer.GetRemovalState(_config.Id) == DownloadOperationState.Queued
            ? LocalizationKeys.Downloads_Queued : LocalizationKeys.Modules_Removing);
        public bool IsRemoving => _isRemoving || _installer.GetRemovalState(_config.Id) != null ||
            _installer.GetInstallationState(_config.Id)?.Phase == DownloadOperationState.Removing;
        public bool IsNotRemoving => !IsRemoving;

        /// <summary>
        /// Updates the removal reservation and all card actions affected by it.
        /// </summary>
        internal void SetRemoving(bool removing)
        {
            if (_isRemoving == removing) return;
            _isRemoving = removing;
            if (removing) SetMenuOpen(false);
            OnPropertyChanged(nameof(IsRemoving));
            OnPropertyChanged(nameof(IsNotRemoving));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            OnPropertyChanged(nameof(ShowCardUpdateAction));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            OnPropertyChanged(nameof(ShowStartingStatus));
            RefreshCommandStates();
        }

        /// <summary>
        /// Queues removal through the shared module lifecycle.
        /// </summary>
        internal Task<string?> UninstallAsync() => _updateManager.UninstallModuleAsync(_config);

        /// <summary>
        /// Reads persisted preferences rather than potentially stale selections cached by the card.
        /// </summary>
        internal async Task<ModuleConfig> LoadReinstallConfigAsync() =>
            await _installer.LoadModuleConfig(_config.SourcePath)
                ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, Name));

        /// <summary>
        /// Runs reinstallation through the shared queues and keeps failures visible in the module console.
        /// </summary>
        internal async Task<ModuleLaunchResult> ReinstallAsync(ModuleUpdateConfig selection)
        {
            var log = _runner.ConsoleStore.CreateMaintenanceLog(_config, null, reset: true);
            try
            {
                return await _updateManager.ReinstallModuleAsync(_config, selection, log);
            }
            catch (Exception ex)
            {
                log.Report(ex is OperationCanceledException ? L.Get(LocalizationKeys.ModuleInfo_Canceled)
                    : L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                throw;
            }
        }

        /// <summary>
        /// Gets the localized not-verified badge text.
        /// </summary>
        public string NotVerifiedLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized update action label.
        /// </summary>
        public string UpdateLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized stop action label.
        /// </summary>
        public string StopLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized restart action label.
        /// </summary>
        public string RestartLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized restarting status label.
        /// </summary>
        public string RestartingLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized starting status label.
        /// </summary>
        public string StartingLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized updating status label.
        /// </summary>
        public string UpdatingLabel { get; private set; } = string.Empty;

        public bool IsInstalling => _installer.IsInstalling(_config.Id);
        public string MaintenanceLabel => _installer.GetInstallationState(_config.Id)?.Label ?? UpdatingLabel;

        /// <summary>
        /// Gets the localized launch action label.
        /// </summary>
        public string LaunchLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized check-updates menu label.
        /// </summary>
        public string CheckUpdatesLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized configure-updates menu label.
        /// </summary>
        public string ConfigureUpdatesLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized action label for opening the module's console output.
        /// </summary>
        public string ViewLogsLabel => L.Get(LocalizationKeys.Modules_ViewLogs);

        /// <summary>
        /// Refreshes localized strings shown on the module card template.
        /// </summary>
        internal void RefreshLocalizedLabels()
        {
            NotVerifiedLabel = L.Get(LocalizationKeys.Modules_NotVerified);
            UpdateLabel = L.Get(LocalizationKeys.Modules_Update);
            StopLabel = L.Get(LocalizationKeys.Modules_Stop);
            RestartLabel = L.Get(LocalizationKeys.Modules_Restart);
            RestartingLabel = L.Get(LocalizationKeys.Modules_Restarting);
            StartingLabel = L.Get(LocalizationKeys.Modules_Starting);
            UpdatingLabel = L.Get(LocalizationKeys.Modules_Updating);
            LaunchLabel = L.Get(LocalizationKeys.Modules_Launch);
            CheckUpdatesLabel = L.Get(LocalizationKeys.Modules_CheckUpdates);
            ConfigureUpdatesLabel = L.Get(LocalizationKeys.Modules_ConfigureUpdates);

            OnPropertyChanged(nameof(NotVerifiedLabel));
            OnPropertyChanged(nameof(BetaLabel));
            OnPropertyChanged(nameof(ExperimentalLabel));
            OnPropertyChanged(nameof(UpdateLabel));
            OnPropertyChanged(nameof(StopLabel));
            OnPropertyChanged(nameof(RestartLabel));
            OnPropertyChanged(nameof(RestartingLabel));
            OnPropertyChanged(nameof(StartingLabel));
            OnPropertyChanged(nameof(UpdatingLabel));
            OnPropertyChanged(nameof(MaintenanceLabel));
            OnPropertyChanged(nameof(LaunchLabel));
            OnPropertyChanged(nameof(CheckUpdatesLabel));
            OnPropertyChanged(nameof(ConfigureUpdatesLabel));
            OnPropertyChanged(nameof(RemoveLabel));
            OnPropertyChanged(nameof(ReinstallLabel));
            OnPropertyChanged(nameof(InfoLabel));
            OnPropertyChanged(nameof(ViewLogsLabel));
            OnPropertyChanged(nameof(RemovingLabel));
        }


        // Card properties

        /// <summary>
        /// Gets the module name shown on the card.
        /// </summary>
        public string Name => _config.Name;

        /// <summary>
        /// Gets the resolved trust level for the module card.
        /// </summary>
        public ModuleTrustLevel TrustLevel => _trustLevel;

        /// <summary>
        /// Gets whether the official verified badge should be shown.
        /// </summary>
        public bool ShowVerifiedBadge => _trustLevel == ModuleTrustLevel.Official;

        /// <summary>
        /// Gets whether the unverified warning badge should be shown.
        /// </summary>
        public bool ShowUnverifiedWarning => _trustLevel == ModuleTrustLevel.Unreviewed;

        /// <summary>
        /// Gets the short module description shown on the card.
        /// </summary>
        public string Description => _config.Description;

        /// <summary>
        /// Gets the stable source path of the module manifest.
        /// </summary>
        public string SourcePath => _config.SourcePath;

        /// <summary>
        /// Gets the formatted version label shown on the card.
        /// </summary>
        public string VersionString => $"v{_config.Version}";

        public bool IsBeta => _config.IsBeta;
        public bool IsExperimental => _config.IsExperimental;
        public bool HasDevelopmentStatus => IsBeta || IsExperimental;
        public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);
        public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);

        /// <summary>
        /// Gets the resolved module icon path.
        /// </summary>
        public string? IconFullPath => _config.IconFullPath;

        /// <summary>
        /// Gets whether the module has an icon to render.
        /// </summary>
        public bool HasIcon => !string.IsNullOrEmpty(_config.IconFullPath);

        /// <summary>
        /// Gets whether the module is currently enabled.
        /// </summary>
        public bool IsRunning => _config.Status.Enabled;

        /// <summary>
        /// Gets whether the module is currently stopped.
        /// </summary>
        public bool IsStopped => !_config.Status.Enabled;

        /// <summary>
        /// Gets or sets whether the module is currently restarting.
        /// </summary>
        public bool IsRestarting
        {
            get => _isRestarting;
            set
            {
                if (_isRestarting == value)
                {
                    return;
                }

                _isRestarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotRestarting));
                OnPropertyChanged(nameof(ShowRunningActions));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether the restart overlay should stay hidden.
        /// </summary>
        public bool IsNotRestarting => !IsRestarting;


        // Maintenance state shared with ModuleInfo and automatic updates

        /// <summary>
        /// Uses the persisted candidate so rebuilding a card does not lose its update badge.
        /// </summary>
        public bool HasUpdate => _updateManager.TryRestorePendingUpdateCandidate(_config, out _);

        /// <summary>
        /// Reads the lightweight activity state without formatting console output.
        /// </summary>
        public bool IsUpdating => _runner.ConsoleStore.GetActivity(SourcePath) == ModuleActivity.Updating;

        /// <summary>
        /// Shows the header spinner for metadata checks, including checks initiated in ModuleInfo.
        /// </summary>
        public bool IsCheckingUpdates => _updateManager.IsCheckingModuleUpdate(SourcePath);

        /// <summary>
        /// Blocks conflicting card actions during an installation or update.
        /// </summary>
        public bool IsBusy => IsUpdating || IsInstalling || _updateManager.IsModuleUpdatePending(SourcePath);

        /// <summary>
        /// Refreshes only state bindings when background maintenance changes.
        /// </summary>
        internal void RefreshMaintenanceState()
        {
            // Console output is frequent; only an actual phase change needs to redraw card controls.
            var activity = _runner.ConsoleStore.GetActivity(SourcePath);
            var updatePending = _updateManager.IsModuleUpdatePending(SourcePath);
            var checking = IsCheckingUpdates;
            if (_lastMaintenanceActivity == activity && _lastUpdatePending == updatePending &&
                _lastCheckingUpdates == checking)
            {
                return;
            }
            _lastMaintenanceActivity = activity;
            _lastUpdatePending = updatePending;
            _lastCheckingUpdates = checking;
            OnPropertyChanged(nameof(IsCheckingUpdates));
            OnPropertyChanged(nameof(IsUpdating));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowCardUpdateAction));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            RefreshCommandStates();
        }

        /// <summary>
        /// Gets whether the compact update action should stay visible in the card header.
        /// </summary>
        public bool ShowCardUpdateAction => HasUpdate && !IsBusy && !IsRemoving;

        /// <summary>
        /// Gets or sets whether the module is currently starting.
        /// </summary>
        public bool IsStarting
        {
            get => _isStarting;
            private set
            {
                if (_isStarting == value)
                {
                    return;
                }

                _isStarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanShowLaunchAction));
                OnPropertyChanged(nameof(ShowStartingStatus));
                OnPropertyChanged(nameof(ShowRunningActions));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether the launch button should stay visible.
        /// </summary>
        public bool CanShowLaunchAction => IsStopped && !IsUpdating && !IsStarting && !IsRemoving && !IsInstalling;

        /// <summary>
        /// Gets whether the running action buttons should stay visible.
        /// </summary>
        public bool ShowRunningActions => IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsRemoving && !IsInstalling;

        /// <summary>
        /// Uses one status pill for installation and updating.
        /// </summary>
        public bool ShowMaintenanceStatus => (IsUpdating || IsInstalling) && !IsRemoving;

        /// <summary>
        /// Gets whether the starting status pill should be visible.
        /// </summary>
        public bool ShowStartingStatus => IsStarting && !IsRemoving && !IsInstalling;


        // Card menu

        /// <summary>
        /// Gets whether the mini actions menu is open for this card.
        /// </summary>
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            private set
            {
                if (_isMenuOpen == value)
                {
                    return;
                }

                _isMenuOpen = value;
                OnPropertyChanged();
            }
        }


        // Commands

        /// <summary>
        /// Gets the command that launches the module.
        /// </summary>
        public ICommand LaunchCommand { get; }

        /// <summary>
        /// Gets the command that stops the module.
        /// </summary>
        public ICommand StopCommand { get; }

        /// <summary>
        /// Gets the command that restarts the module.
        /// </summary>
        public ICommand RestartCommand { get; }

        /// <summary>
        /// Gets the command that toggles the mini actions menu.
        /// </summary>
        public ICommand ToggleMenuCommand { get; }

        /// <summary>
        /// Gets the command that closes the mini actions menu.
        /// </summary>
        public ICommand CloseMenuCommand { get; }

        /// <summary>
        /// Gets the command that checks for module updates.
        /// </summary>
        public ICommand CheckUpdateCommand { get; }

        /// <summary>
        /// Gets the command that opens update configuration for the module.
        /// </summary>
        public ICommand OpenConfigureUpdatesCommand { get; }

        /// <summary>
        /// Gets the command that opens the merged console output for this module.
        /// </summary>
        public ICommand ViewLogsCommand { get; }

        /// <summary>
        /// Gets the command that starts an available update without opening a view.
        /// </summary>
        public ICommand UpdateCommand { get; }

        // Menu helpers

        /// <summary>
        /// Opens or closes the card mini menu through the parent dashboard.
        /// </summary>
        private void ExecuteToggleMenuCommand()
        {
            if (IsRemoving || IsInstalling) return;
            _onMenuToggleRequested(this);
        }

        /// <summary>
        /// Closes the mini menu without running an action.
        /// </summary>
        private void ExecuteCloseMenuCommand()
        {
            SetMenuOpen(false);
        }

        /// <summary>
        /// Closes the menu and opens the update configuration dialog.
        /// </summary>
        private void ExecuteOpenConfigureUpdatesCommand()
        {
            if (IsRemoving || IsInstalling) return;
            SetMenuOpen(false);
            _onConfigureUpdatesRequested(this);
        }

        /// <summary>
        /// Closes the card menu before navigating to the module's console.
        /// </summary>
        private void ExecuteViewLogsCommand()
        {
            // Console navigation is read-only and does not participate in module lifecycle operations.
            SetMenuOpen(false);
            _onLogsRequested?.Invoke(this);
        }

        /// <summary>
        /// Starts the selected update when the card has an available candidate.
        /// </summary>
        private void ExecuteUpdateCommand()
        {
            if (!HasUpdate || !CanCheckOrUpdate())
            {
                return;
            }

            SetMenuOpen(false);
            _onUpdateRequested(this);
        }

        /// <summary>
        /// Sets the visible state of the mini menu.
        /// </summary>
        internal void SetMenuOpen(bool isOpen)
        {
            IsMenuOpen = isOpen;
        }


        // Update command helpers

        /// <summary>
        /// Returns whether update-related commands may run.
        /// </summary>
        private bool CanCheckOrUpdate()
        {
            return !IsRemoving && !IsBusy;
        }

        /// <summary>
        /// Returns whether the launch command may run.
        /// </summary>
        private bool CanLaunch()
        {
            return !IsRemoving && IsStopped && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Returns whether the stop command may run.
        /// </summary>
        private bool CanStop()
        {
            return !IsRemoving && IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Returns whether the restart command may run.
        /// </summary>
        private bool CanRestart()
        {
            return !IsRemoving && IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Refreshes command enabled states after one flag changes.
        /// </summary>
        private void RefreshCommandStates()
        {
            _launchCommand.ChangeCanExecute();
            _stopCommand.ChangeCanExecute();
            _restartCommand.ChangeCanExecute();
            _checkUpdateCommand.ChangeCanExecute();
            _removeCommand.ChangeCanExecute();
            _reinstallCommand.ChangeCanExecute();
        }

        /// <summary>
        /// Checks for updates without applying a candidate or opening ModuleInfo.
        /// </summary>
        private void ExecuteCheckUpdateCommand()
        {
            if (!CanCheckOrUpdate())
            {
                return;
            }

            SetMenuOpen(false);
            _onCheckUpdatesRequested(this);
        }


        // Launch flow

        /// <summary>
        /// Opens the uninstall confirmation for a removable, idle module.
        /// </summary>
        private void OnRemove()
        {
            if (!_removeCommand.CanExecute(null)) return;
            SetMenuOpen(false);
            _onRemoveRequested(this);
        }

        /// <summary>
        /// Opens reinstall confirmation without applying the mandatory-module uninstall restriction.
        /// </summary>
        private void OnReinstall()
        {
            if (!_reinstallCommand.CanExecute(null)) return;
            SetMenuOpen(false);
            _onReinstallRequested?.Invoke(this);
        }

        /// <summary>
        /// Loads the latest config, completes first-run setup if needed, and starts the module.
        /// </summary>
        private async void OnLaunch()
        {
            if (!CanLaunch())
            {
                return;
            }

            IsStarting = true;

            try
            {
                await ReloadEditableConfigAsync();

                var launchLog = new Progress<string>(message => Debug.WriteLine($"[Launch] {message}"));
                var result = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    _config.SourcePath,
                    launchLog,
                    CancellationToken.None);

                if (result.EffectiveConfig != null &&
                    string.Equals(result.EffectiveConfig.SourcePath, _config.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _config.Settings = result.EffectiveConfig.Settings;
                    _config.Commands = result.EffectiveConfig.Commands;
                    _config.Status.FirstRunCompleted = result.EffectiveConfig.Status.FirstRunCompleted;
                    _config.Status.Enabled = result.EffectiveConfig.Status.Enabled;
                }

                if (result.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning)
                {
                    NotifyStateChanged();
                    _onStateChanged?.Invoke();
                }
                else
                {
                    Debug.WriteLine($"Launch failed ({result.Status}): {result.Message}");
                }
            }
            catch (Exception ex)
            {
                // UI event handlers must contain unexpected failures so WinUI can keep processing input.
                Debug.WriteLine($"Launch failed with an unexpected error: {ex}");
            }
            finally
            {
                IsStarting = false;
            }
        }


        // Stop flow

        /// <summary>
        /// Stops the module and persists the disabled state.
        /// </summary>
        private async void OnStop()
        {
            if (!CanStop())
            {
                return;
            }

            try
            {
                using var operation = await ModuleInstaller.BeginContentOperationAsync(CancellationToken.None, _config);
                await Task.Run(() => _runner.StopModuleAsync(_config.SourcePath));
                _config.Status.Enabled = false;
                await Task.Run(() => _installer.SaveConfigAsync(_config));
                NotifyStateChanged();
                _onStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                _runner.ConsoleStore.AppendOverviewLine(_config, ex.Message);
                Debug.WriteLine($"Stop failed: {ex}");
            }
        }


        // Restart flow

        /// <summary>
        /// Stops and starts the module again while showing the restart state.
        /// </summary>
        private async void OnRestart()
        {
            if (!CanRestart())
            {
                return;
            }

            IsRestarting = true;
            using var activity = _runner.ConsoleStore.BeginActivity(_config.SourcePath, ModuleActivity.Restarting);

            try
            {
                using var operation = await ModuleInstaller.BeginContentOperationAsync(CancellationToken.None, _config);
                await ReloadEditableConfigAsync();
                await Task.Run(() => _runner.StopModuleAsync(_config.SourcePath));
                await Task.Delay(1000);

                var restartLog = new Progress<string>(message => Debug.WriteLine($"[Restart] {message}"));
                var restartResult = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    _config.SourcePath,
                    restartLog,
                    CancellationToken.None);

                if (restartResult.EffectiveConfig != null &&
                    string.Equals(restartResult.EffectiveConfig.SourcePath, _config.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _config.Status.Enabled = restartResult.EffectiveConfig.Status.Enabled;
                }

                if (restartResult.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning)
                {
                    NotifyStateChanged();
                    _onStateChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _runner.ConsoleStore.AppendOverviewLine(_config, ex.Message);
                Debug.WriteLine($"Restart failed: {ex}");
            }
            finally
            {
                IsRestarting = false;
            }
        }


        // Config refresh

        /// <summary>
        /// Reloads module settings and commands from disk before launch or restart.
        /// </summary>
        private async Task ReloadEditableConfigAsync()
        {
            // Launch and restart should respect the latest settings saved in the overlay,
            // so the card refreshes the mutable sections from disk just before execution.
            var freshConfig = await _installer.LoadModuleConfig(_config.SourcePath);
            if (freshConfig == null)
            {
                return;
            }

            _config.Settings = freshConfig.Settings;
            _config.Commands = freshConfig.Commands;
        }

        // UI state refresh

        /// <summary>
        /// Raises property changes for the running state flags.
        /// </summary>
        private void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            RefreshCommandStates();
        }

        // Property change

        /// <summary>
        /// Raises the view-model property changed event.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
