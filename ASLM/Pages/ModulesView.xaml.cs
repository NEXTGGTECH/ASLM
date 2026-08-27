// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
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

        private readonly UpdateManager _updateManager;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly ModuleLaunchCoordinator _launchCoordinator;
        private readonly AppLocalizationService _localization;
        private AppShellPage? _shell;
        private int _gridSpan = 1;
        private ImageSource _moreIconSource = null!;


        // View data

        /// <summary>
        /// Stores the module cards shown in the dashboard.
        /// </summary>
        public ObservableCollection<ModuleViewModel> Modules { get; } = new();


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
            AppLocalizationService localization)
        {
            _updateManager = updateManager;
            _moduleTrustService = moduleTrustService;
            _launchCoordinator = launchCoordinator;
            _localization = localization;
            InitializeComponent();
            BindingContext = this;
            DashboardView.HandlerChanged += OnDashboardViewHandlerChanged;
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
            Modules.Clear();

            foreach (var module in modules)
            {
                Modules.Add(new ModuleViewModel(
                    module,
                    installer,
                    runner,
                    _launchCoordinator,
                    _updateManager,
                    _moduleTrustService,
                    OnModuleStateChanged,
                    OnMenuToggleRequested,
                    OpenConfigureUpdates,
                    OpenUpdateDialog));
            }
        }

        /// <summary>
        /// Builds a card view model for overlay flows when the modules dashboard has not created one yet.
        /// </summary>
        internal static ModuleViewModel CreateViewModelForDeferredUpdateOverlay(
            ModuleConfig config,
            ModuleInstaller installer,
            ModuleRunner runner,
            ModuleLaunchCoordinator launchCoordinator,
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            Action onStateChanged) =>
            new ModuleViewModel(
                config,
                installer,
                runner,
                launchCoordinator,
                updateManager,
                moduleTrustService,
                onStateChanged,
                static _ => { },
                static _ => { },
                static _ => { });


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
        /// Opens or closes the mini action menu for the selected module card.
        /// </summary>
        private void OnMenuToggleRequested(ModuleViewModel module)
        {
            foreach (var candidate in Modules)
            {
                var shouldStayOpen = ReferenceEquals(candidate, module) && !candidate.IsMenuOpen;
                candidate.SetMenuOpen(shouldStayOpen);
            }
        }

        /// <summary>
        /// Opens the module update configuration overlay for the selected card.
        /// </summary>
        private void OpenConfigureUpdates(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleUpdateOverlay(module, ModuleUpdateMode.Configure);
        }

        /// <summary>
        /// Opens the module update details overlay for the selected card.
        /// </summary>
        private void OpenUpdateDialog(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleUpdateOverlay(module, ModuleUpdateMode.Update);
        }

        /// <summary>
        /// Closes every module mini menu in the dashboard.
        /// </summary>
        private void CloseAllMenus()
        {
            foreach (var module in Modules)
            {
                module.SetMenuOpen(false);
            }
        }


        // Layout events

        /// <summary>
        /// Recalculates the grid layout when the view size changes.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e)
        {
            RecalculateGridSpan();
        }

        /// <summary>
        /// Reapplies native list behavior after the platform view enters the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
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
            ThemeService.PaletteApplied -= OnPaletteAppliedForMoreIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }
        }

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
        private static readonly IReadOnlyList<string> SharedSourceModeOptions = ["release", "pre-release", "branch"];
        private const string LatestReleaseSelection = ModuleUpdateConfig.LatestReleaseTag;

        private readonly ModuleConfig _config;
        private readonly ModuleInstaller _installer;
        private readonly ModuleRunner _runner;
        private readonly ModuleLaunchCoordinator _launchCoordinator;
        private readonly UpdateManager _updateManager;
        private readonly Action _onStateChanged;
        private readonly Action<ModuleViewModel> _onMenuToggleRequested;
        private readonly Action<ModuleViewModel> _onConfigureUpdatesRequested;
        private readonly Action<ModuleViewModel> _onUpdateDialogRequested;
        private readonly ObservableCollection<string> _branchOptions = [];
        private readonly ObservableCollection<UpdateCandidate> _releaseOptions = [];
        private readonly Command _launchCommand;
        private readonly Command _stopCommand;
        private readonly Command _restartCommand;
        private readonly Command _checkUpdateCommand;
        private readonly Command _updateCommand;
        private readonly Command _closeMenuCommand;

        private string _selectedSourceMode = "release";
        private string _selectedBranch = "main";
        private UpdateStatusPhase _updateStatusPhase = UpdateStatusPhase.ReadyToCheck;
        private string? _updateStatusFormatArg;
        private string _updateStatus = string.Empty;
        private UpdateCandidate? _updateCandidate;
        private UpdateCandidate? _selectedReleaseOption;
        private bool _hasUpdate;
        private bool _isCheckingUpdate;
        private bool _isUpdating;
        private bool _isRestarting;
        private bool _isStarting;
        private bool _isMenuOpen;
        private bool _hasLoadedBranchOptions;
        private bool _hasLoadedReleaseOptions;
        private bool _isRefreshingBranchOptions;
        private bool _isRefreshingReleaseOptions;
        private string _loadedReleaseMode = string.Empty;
        private readonly StringBuilder _updateLogBuffer = new();
        private string _updateActivityStatus = string.Empty;
        private string _updateDownloadDetail = string.Empty;
        private double _updateOverallProgress;
        private double _updateFileProgress;
        private bool _hasUpdateProgress;
        private long _lastDownloadedBytes;
        private DateTime _lastSpeedUpdate = DateTime.UtcNow;
        private double _lastSpeed;


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
            Action<ModuleViewModel> onUpdateDialogRequested)
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
            _onUpdateDialogRequested = onUpdateDialogRequested;

            // Normalize once so every card starts from the persisted update preferences.
            _config.Normalize();
            _selectedSourceMode = _config.Update.Mode;
            _selectedBranch = _config.Update.Branch;
            _branchOptions.Add(_selectedBranch);
            
            _launchCommand = new Command(OnLaunch, CanLaunch);
            _stopCommand = new Command(OnStop, CanStop);
            _restartCommand = new Command(OnRestart, CanRestart);
            ToggleMenuCommand = new Command(ExecuteToggleMenuCommand);
            _closeMenuCommand = new Command(ExecuteCloseMenuCommand);
            OpenConfigureUpdatesCommand = new Command(ExecuteOpenConfigureUpdatesCommand);
            OpenUpdateDialogCommand = new Command(ExecuteOpenUpdateDialogCommand);
            _checkUpdateCommand = new Command(ExecuteCheckUpdateCommand, CanCheckOrUpdate);
            _updateCommand = new Command(ExecuteApplyUpdateCommand, CanApplyUpdate);

            LaunchCommand = _launchCommand;
            StopCommand = _stopCommand;
            RestartCommand = _restartCommand;
            CheckUpdateCommand = _checkUpdateCommand;
            UpdateCommand = _updateCommand;
            CloseMenuCommand = _closeMenuCommand;
            TryHydratePendingUpdateFromConfig();
            if (_updateStatusPhase == UpdateStatusPhase.ReadyToCheck && string.IsNullOrEmpty(_updateStatus))
            {
                SetUpdatePhase(UpdateStatusPhase.ReadyToCheck);
            }

            RefreshLocalizedLabels();
        }


        // Update status

        /// <summary>
        /// Describes the update badge phases shown on the module card.
        /// </summary>
        private enum UpdateStatusPhase
        {
            ReadyToCheck,
            Checking,
            UpToDate,
            Available,
            CheckFailed,
            Updating,
            Updated,
            Failed
        }

        /// <summary>
        /// Updates the cached update phase and refreshes the status text shown on the card.
        /// </summary>
        private void SetUpdatePhase(UpdateStatusPhase phase, string? formatArg = null)
        {
            _updateStatusPhase = phase;
            _updateStatusFormatArg = formatArg;
            var text = FormatUpdateStatus(phase, formatArg);
            UpdateStatus = text;
        }

        /// <summary>
        /// Formats the localized update status text for the requested phase.
        /// </summary>
        private static string FormatUpdateStatus(UpdateStatusPhase phase, string? formatArg) =>
            phase switch
            {
                UpdateStatusPhase.ReadyToCheck => L.Get(LocalizationKeys.ModuleUpdate_Status_ReadyToCheck),
                UpdateStatusPhase.Checking => L.Get(LocalizationKeys.ModuleUpdate_Status_Checking),
                UpdateStatusPhase.UpToDate => L.Get(LocalizationKeys.ModuleUpdate_Status_UpToDate),
                UpdateStatusPhase.Available => L.Get(LocalizationKeys.ModuleUpdate_Status_UpdateAvailableFormat, formatArg ?? string.Empty),
                UpdateStatusPhase.CheckFailed => L.Get(LocalizationKeys.ModuleUpdate_Status_CheckFailedFormat, formatArg ?? string.Empty),
                UpdateStatusPhase.Updating => L.Get(LocalizationKeys.ModuleUpdate_Status_Updating),
                UpdateStatusPhase.Updated => L.Get(LocalizationKeys.ModuleUpdate_Status_Updated),
                UpdateStatusPhase.Failed => L.Get(LocalizationKeys.ModuleUpdate_Status_UpdateFailed),
                _ => L.Get(LocalizationKeys.ModuleUpdate_Status_ReadyToCheck)
            };

        /// <summary>
        /// Reapplies localization to the current update status phase.
        /// </summary>
        private void RefreshUpdateStatusLocalization() =>
            SetUpdatePhase(_updateStatusPhase, _updateStatusFormatArg);


        // Localized card labels

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
            RefreshUpdateStatusLocalization();

            OnPropertyChanged(nameof(NotVerifiedLabel));
            OnPropertyChanged(nameof(UpdateLabel));
            OnPropertyChanged(nameof(StopLabel));
            OnPropertyChanged(nameof(RestartLabel));
            OnPropertyChanged(nameof(RestartingLabel));
            OnPropertyChanged(nameof(StartingLabel));
            OnPropertyChanged(nameof(UpdatingLabel));
            OnPropertyChanged(nameof(LaunchLabel));
            OnPropertyChanged(nameof(CheckUpdatesLabel));
            OnPropertyChanged(nameof(ConfigureUpdatesLabel));
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


        // Update options

        /// <summary>
        /// Gets selectable module update source modes.
        /// </summary>
        public IReadOnlyList<string> SourceModeOptions => SharedSourceModeOptions;

        /// <summary>
        /// Gets repository branches loaded from GitHub.
        /// </summary>
        public ObservableCollection<string> BranchOptions => _branchOptions;

        /// <summary>
        /// Gets selectable release versions loaded from GitHub.
        /// </summary>
        public ObservableCollection<UpdateCandidate> ReleaseOptions => _releaseOptions;

        /// <summary>
        /// Gets or sets the selected module update mode.
        /// </summary>
        public string SelectedSourceMode
        {
            get => _selectedSourceMode;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? "release" : value;
                if (string.Equals(_selectedSourceMode, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedSourceMode = normalized;
                _config.Update.Mode = _selectedSourceMode;
                _config.Update.Channel = string.Equals(_selectedSourceMode, "pre-release", StringComparison.OrdinalIgnoreCase)
                    ? "pre-release"
                    : "release";
                _updateCandidate = null;
                HasUpdate = false;
                PersistPendingUpdateSnapshot();
                SetUpdatePhase(UpdateStatusPhase.ReadyToCheck);
                SetUpdateActivityStatus(UpdateStatus);
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpdateCandidate));
                OnPropertyChanged(nameof(AvailableUpdateLabel));
                OnPropertyChanged(nameof(IsBranchMode));
                OnPropertyChanged(nameof(IsReleaseMode));
                OnPropertyChanged(nameof(SelectedTargetLabel));
                OnPropertyChanged(nameof(CanInstallSelectedUpdate));
                OnPropertyChanged(nameof(ShowInstallAction));
            }
        }

        /// <summary>
        /// Gets or sets the selected branch.
        /// </summary>
        public string SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                SetBranchSelection(value, ignoreRefreshGuard: false);
            }
        }

        /// <summary>
        /// Gets or sets the selected release target.
        /// </summary>
        public UpdateCandidate? SelectedReleaseOption
        {
            get => _selectedReleaseOption;
            set
            {
                if (_isRefreshingReleaseOptions)
                {
                    return;
                }

                var selectedKey = ResolveReleaseSelectionKey(value);
                if (string.Equals(
                    ResolveReleaseSelectionKey(_selectedReleaseOption),
                    selectedKey,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _selectedReleaseOption = value;
                _config.Update.SelectedReleaseTag = selectedKey;
                PersistPendingUpdateSnapshot();

                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedTargetLabel));
                OnPropertyChanged(nameof(CanInstallSelectedUpdate));
                OnPropertyChanged(nameof(ShowInstallAction));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether branch update controls should be visible.
        /// </summary>
        public bool IsBranchMode => string.Equals(SelectedSourceMode, "branch", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets whether release channel controls should be visible.
        /// </summary>
        public bool IsReleaseMode => !IsBranchMode;


        // Update state

        /// <summary>
        /// Gets update status text shown on the module card.
        /// </summary>
        public string UpdateStatus
        {
            get => _updateStatus;
            private set
            {
                if (_updateStatus == value)
                {
                    return;
                }

                _updateStatus = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets the current update candidate loaded for the card.
        /// </summary>
        public UpdateCandidate? UpdateCandidate => _updateCandidate;

        /// <summary>
        /// Gets whether an update candidate is available.
        /// </summary>
        public bool HasUpdate
        {
            get => _hasUpdate;
            private set
            {
                if (_hasUpdate == value)
                {
                    return;
                }

                _hasUpdate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(AvailableUpdateLabel));
                OnPropertyChanged(nameof(ShowInstallAction));
                OnPropertyChanged(nameof(ShowCardUpdateAction));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets a short label for the currently available update.
        /// </summary>
        public string AvailableUpdateLabel => _updateCandidate == null
            ? "No update detected"
            : _updateCandidate.RemoteVersion;

        /// <summary>
        /// Gets the currently selected install target for the dialog header.
        /// </summary>
        public string SelectedTargetLabel => IsBranchMode
            ? SelectedBranch
            : FormatSelectedReleaseTarget(_selectedReleaseOption)
                ?? "No version selected";

        /// <summary>
        /// Gets whether an update operation can be started.
        /// </summary>
        public bool CanUpdate => CanInstallSelectedUpdate;

        /// <summary>
        /// Gets whether the current selection can be installed.
        /// </summary>
        public bool CanInstallSelectedUpdate => IsBranchMode
            ? !IsBusy &&
              !string.IsNullOrWhiteSpace(SelectedBranch) &&
              _updateCandidate != null
            : ResolveSelectedReleaseInstallCandidate() != null && !IsBusy;

        /// <summary>
        /// Gets whether update checking is in progress.
        /// </summary>
        public bool IsCheckingUpdate
        {
            get => _isCheckingUpdate;
            private set
            {
                if (_isCheckingUpdate == value)
                {
                    return;
                }

                _isCheckingUpdate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(CanCheckUpdates));
                OnPropertyChanged(nameof(HasUpdateProgress));
                OnPropertyChanged(nameof(ShowInstallAction));
                OnPropertyChanged(nameof(ShowHeaderBusyIndicator));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether update installation is in progress.
        /// </summary>
        public bool IsUpdating
        {
            get => _isUpdating;
            private set
            {
                if (_isUpdating == value)
                {
                    return;
                }

                _isUpdating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(CanCheckUpdates));
                OnPropertyChanged(nameof(HasUpdateProgress));
                OnPropertyChanged(nameof(ShowInstallAction));
                OnPropertyChanged(nameof(ShowUpdatingStatus));
                OnPropertyChanged(nameof(CanShowLaunchAction));
                OnPropertyChanged(nameof(ShowRunningActions));
                OnPropertyChanged(nameof(ShowCardUpdateAction));
                OnPropertyChanged(nameof(ShowHeaderBusyIndicator));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether any update-related work is currently running.
        /// </summary>
        public bool IsBusy => IsCheckingUpdate || IsUpdating;

        /// <summary>
        /// Gets whether the update check action should be enabled.
        /// </summary>
        public bool CanCheckUpdates => !IsBusy;

        /// <summary>
        /// Gets whether the install action should stay visible for the current target selection.
        /// </summary>
        public bool ShowInstallAction => CanInstallSelectedUpdate && !IsUpdating;

        /// <summary>
        /// Gets the activity status cached for the module update dialog.
        /// </summary>
        public string UpdateActivityStatus => string.IsNullOrWhiteSpace(_updateActivityStatus)
            ? UpdateStatus
            : _updateActivityStatus;

        /// <summary>
        /// Gets whether the update dialog should show progress bars.
        /// </summary>
        public bool HasUpdateProgress => _hasUpdateProgress || IsBusy;

        /// <summary>
        /// Gets the cached overall update progress fraction.
        /// </summary>
        public double UpdateOverallProgress => _updateOverallProgress;

        /// <summary>
        /// Gets the cached file download progress fraction.
        /// </summary>
        public double UpdateFileProgress => _updateFileProgress;

        /// <summary>
        /// Gets the cached transfer detail line.
        /// </summary>
        public string UpdateDownloadDetail => _updateDownloadDetail;

        /// <summary>
        /// Gets whether the cached transfer detail line should be shown.
        /// </summary>
        public bool HasUpdateDownloadDetail => !string.IsNullOrWhiteSpace(UpdateDownloadDetail);

        /// <summary>
        /// Gets the cached update console text.
        /// </summary>
        public string UpdateLogText => _updateLogBuffer.ToString();

        /// <summary>
        /// Gets whether the update console already contains lines.
        /// </summary>
        public bool HasUpdateLog => _updateLogBuffer.Length > 0;

        /// <summary>
        /// Gets whether the header should show the lightweight check spinner.
        /// </summary>
        public bool ShowHeaderBusyIndicator => IsCheckingUpdate;

        /// <summary>
        /// Gets whether the compact update action should stay visible in the card header.
        /// </summary>
        public bool ShowCardUpdateAction => HasUpdate && !IsUpdating;

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
        public bool CanShowLaunchAction => IsStopped && !IsUpdating && !IsStarting;

        /// <summary>
        /// Gets whether the running action buttons should stay visible.
        /// </summary>
        public bool ShowRunningActions => IsRunning && !IsRestarting && !IsStarting && !IsUpdating;

        /// <summary>
        /// Gets whether the updating status pill should be visible.
        /// </summary>
        public bool ShowUpdatingStatus => IsUpdating;

        /// <summary>
        /// Gets whether the starting status pill should be visible.
        /// </summary>
        public bool ShowStartingStatus => IsStarting;


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
        /// Gets the command that opens the update details dialog.
        /// </summary>
        public ICommand OpenUpdateDialogCommand { get; }

        /// <summary>
        /// Gets the command that applies an available module update.
        /// </summary>
        public ICommand UpdateCommand { get; }


        // Menu helpers

        /// <summary>
        /// Opens or closes the card mini menu through the parent dashboard.
        /// </summary>
        private void ExecuteToggleMenuCommand()
        {
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
            SetMenuOpen(false);
            _onConfigureUpdatesRequested(this);
        }

        /// <summary>
        /// Opens the update details dialog when an update exists.
        /// </summary>
        private void ExecuteOpenUpdateDialogCommand()
        {
            if (!HasUpdate)
            {
                return;
            }

            SetMenuOpen(false);
            _onUpdateDialogRequested(this);
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
            return !IsCheckingUpdate && !IsUpdating;
        }

        /// <summary>
        /// Returns whether the apply-update command may run.
        /// </summary>
        private bool CanApplyUpdate()
        {
            return CanInstallSelectedUpdate && CanCheckOrUpdate();
        }

        /// <summary>
        /// Returns whether the launch command may run.
        /// </summary>
        private bool CanLaunch()
        {
            return IsStopped && !IsStarting && !IsUpdating;
        }

        /// <summary>
        /// Returns whether the stop command may run.
        /// </summary>
        private bool CanStop()
        {
            return IsRunning && !IsRestarting && !IsStarting && !IsUpdating;
        }

        /// <summary>
        /// Returns whether the restart command may run.
        /// </summary>
        private bool CanRestart()
        {
            return IsRunning && !IsRestarting && !IsStarting && !IsUpdating;
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
            _updateCommand.ChangeCanExecute();
        }

        /// <summary>
        /// Persists the module update preferences after one setting changes.
        /// </summary>
        private void PersistUpdatePreferences()
        {
            // Normalize once so the in-memory fields mirror the persisted representation exactly.
            _config.Update.Normalize();
            _selectedSourceMode = _config.Update.Mode;
            _selectedBranch = _config.Update.Branch;
            _config.Update.SelectedReleaseTag = string.IsNullOrWhiteSpace(_config.Update.SelectedReleaseTag)
                ? null
                : _config.Update.SelectedReleaseTag;
            _updateManager.SaveModuleUpdatePreferences(_config);
        }

        /// <summary>
        /// Writes <see cref="ModuleUpdateConfig.PendingUpdate"/> so the card badge survives dashboard rebuilds and app restarts.
        /// </summary>
        private void PersistPendingUpdateSnapshot()
        {
            try
            {
                if (_hasUpdate && _updateCandidate != null)
                {
                    _config.Update.PendingUpdate = ModulePendingUpdate.FromCandidate(_config, _updateCandidate);
                }
                else
                {
                    _config.Update.PendingUpdate = null;
                }

                PersistUpdatePreferences();
            }
            catch
            {
                // Preference persistence must never break the module card UI.
            }
        }

        /// <summary>
        /// Restores update badge state from the manifest after startup or when a stale snapshot is on disk.
        /// </summary>
        private void TryHydratePendingUpdateFromConfig()
        {
            if (_hasUpdate || _updateCandidate != null)
            {
                return;
            }

            if (!_updateManager.TryRestorePendingUpdateCandidate(_config, out var candidate))
            {
                ClearStalePendingUpdateOnDisk();
                return;
            }

            _updateCandidate = candidate;
            HasUpdate = true;
            SetUpdatePhase(UpdateStatusPhase.Available, candidate.RemoteVersion);
            SetUpdateActivityStatus(UpdateStatus);
            OnPropertyChanged(nameof(UpdateCandidate));
            OnPropertyChanged(nameof(AvailableUpdateLabel));
            ApplyCheckResultSelection();
        }

        /// <summary>
        /// Removes an invalid <see cref="ModuleUpdateConfig.PendingUpdate"/> block from disk when it no longer applies.
        /// </summary>
        private void ClearStalePendingUpdateOnDisk()
        {
            if (_config.Update.PendingUpdate == null)
            {
                return;
            }

            _config.Update.PendingUpdate = null;
            try
            {
                PersistUpdatePreferences();
            }
            catch
            {
            }
        }

        /// <summary>
        /// Synchronizes the latest UI selection into the backing update config before check or install operations.
        /// </summary>
        private void SynchronizeSelectionForUpdateOperation()
        {
            _config.Update.Mode = _selectedSourceMode;
            _config.Update.Channel = string.Equals(_selectedSourceMode, "pre-release", StringComparison.OrdinalIgnoreCase)
                ? "pre-release"
                : "release";

            if (string.Equals(_selectedSourceMode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                _config.Update.Branch = string.IsNullOrWhiteSpace(_selectedBranch)
                    ? _config.Update.Branch
                    : _selectedBranch.Trim();
            }
            else if (_selectedReleaseOption != null)
            {
                _config.Update.SelectedReleaseTag = ResolveReleaseSelectionKey(_selectedReleaseOption);
            }

            // Normalize validates and cleans the config, but may reset Branch to "main" if it was empty.
            // We preserve the user's selected branch to avoid losing their choice during subsequent operations.
            var branchBeforeNormalize = _config.Update.Branch;
            _config.Update.Normalize();
            
            // Restore the user's selected branch if Normalize() changed it due to empty/whitespace validation
            if (string.Equals(_selectedSourceMode, "branch", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_selectedBranch) &&
                !string.Equals(branchBeforeNormalize, _config.Update.Branch, StringComparison.OrdinalIgnoreCase))
            {
                _config.Update.Branch = _selectedBranch.Trim();
            }
            
            _selectedSourceMode = _config.Update.Mode;
            _selectedBranch = _config.Update.Branch;
        }

        /// <summary>
        /// Applies the branch selection from a dialog picker without the list-refresh guard so the
        /// backing config is guaranteed to carry the user's choice before a check or install runs.
        /// </summary>
        internal void ApplyBranchSelection(string branch)
        {
            SetBranchSelection(branch, ignoreRefreshGuard: true);
        }

        /// <summary>
        /// Stores the selected repository branch and invalidates stale update candidates.
        /// </summary>
        private void SetBranchSelection(string branch, bool ignoreRefreshGuard)
        {
            if (!ignoreRefreshGuard && _isRefreshingBranchOptions)
            {
                return;
            }

            var normalized = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
            if (string.Equals(_selectedBranch, normalized, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_config.Update.Branch, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedBranch = normalized;
            _config.Update.Branch = normalized;
            _updateCandidate = null;
            HasUpdate = false;
            PersistPendingUpdateSnapshot();
            SetUpdatePhase(UpdateStatusPhase.ReadyToCheck);
            SetUpdateActivityStatus(UpdateStatus);
            OnPropertyChanged(nameof(SelectedBranch));
            OnPropertyChanged(nameof(UpdateCandidate));
            OnPropertyChanged(nameof(AvailableUpdateLabel));
            OnPropertyChanged(nameof(SelectedTargetLabel));
            OnPropertyChanged(nameof(CanInstallSelectedUpdate));
            OnPropertyChanged(nameof(ShowInstallAction));
            RefreshCommandStates();
        }

        /// <summary>
        /// Bridges the check-update command into the asynchronous workflow.
        /// </summary>
        private async void ExecuteCheckUpdateCommand()
        {
            SetMenuOpen(false);
            await RefreshUpdateStateAsync(forceOptionLoad: false);
        }

        /// <summary>
        /// Bridges the apply-update command into the asynchronous workflow.
        /// </summary>
        private async void ExecuteApplyUpdateCommand()
        {
            await ApplyUpdateAsync();
        }

        /// <summary>
        /// Refreshes the update candidate state for this module.
        /// </summary>
        internal async Task RefreshUpdateStateAsync(bool forceOptionLoad)
        {
            if (IsCheckingUpdate)
            {
                return;
            }

            IsCheckingUpdate = true;
            SetUpdatePhase(UpdateStatusPhase.Checking);
            SetUpdateActivityStatus(UpdateStatus);

            try
            {
                await EnsureSelectionOptionsLoadedAsync(forceOptionLoad);
                SynchronizeSelectionForUpdateOperation();

                _updateCandidate = await Task.Run(() => _updateManager.CheckModuleUpdateAsync(
                    _config,
                    isManualRequest: true));
                HasUpdate = _updateCandidate != null;
                OnPropertyChanged(nameof(UpdateCandidate));
                OnPropertyChanged(nameof(AvailableUpdateLabel));
                ApplyCheckResultSelection();

                if (_updateCandidate == null)
                {
                    SetUpdatePhase(UpdateStatusPhase.UpToDate);
                }
                else
                {
                    SetUpdatePhase(UpdateStatusPhase.Available, _updateCandidate.RemoteVersion);
                }

                SetUpdateActivityStatus(UpdateStatus);
            }
            catch (Exception ex)
            {
                _updateCandidate = null;
                HasUpdate = false;
                OnPropertyChanged(nameof(UpdateCandidate));
                OnPropertyChanged(nameof(AvailableUpdateLabel));
                SetUpdatePhase(UpdateStatusPhase.CheckFailed, ex.Message);
                SetUpdateActivityStatus(UpdateStatus);
            }
            finally
            {
                PersistPendingUpdateSnapshot();
                IsCheckingUpdate = false;
            }
        }

        /// <summary>
        /// Ensures the dialog pickers have the data required by the currently selected update mode.
        /// </summary>
        internal async Task EnsureSelectionOptionsLoadedAsync(bool forceRefresh)
        {
            if (IsBranchMode)
            {
                if (forceRefresh || !_hasLoadedBranchOptions || BranchOptions.Count == 0)
                {
                    await LoadBranchesAsync();
                }

                return;
            }

            if (forceRefresh ||
                !_hasLoadedReleaseOptions ||
                ReleaseOptions.Count == 0 ||
                !string.Equals(_loadedReleaseMode, _selectedSourceMode, StringComparison.OrdinalIgnoreCase))
            {
                await LoadReleaseOptionsAsync();
            }
        }

        /// <summary>
        /// Loads branch names for the configured GitHub repository.
        /// </summary>
        private async Task LoadBranchesAsync()
        {
            if (_isRefreshingBranchOptions)
            {
                return;
            }

            _isRefreshingBranchOptions = true;

            try
            {
                var branches = await Task.Run(() => _updateManager.GetModuleBranchesAsync(
                    _config,
                    isManualRequest: true));
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var selectedBranch = string.IsNullOrWhiteSpace(_selectedBranch)
                        ? _config.Update.Branch
                        : _selectedBranch;

                    selectedBranch = string.IsNullOrWhiteSpace(selectedBranch)
                        ? "main"
                        : selectedBranch.Trim();

                    BranchOptions.Clear();
                    BranchOptions.Add(selectedBranch);

                    foreach (var branch in branches
                                 .Where(branch => !string.IsNullOrWhiteSpace(branch.Name))
                                 .Select(branch => branch.Name.Trim())
                                 .Where(branch => !string.Equals(branch, selectedBranch, StringComparison.OrdinalIgnoreCase))
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(branch => branch, StringComparer.OrdinalIgnoreCase))
                    {
                        BranchOptions.Add(branch);
                    }

                    _selectedBranch = selectedBranch;
                    _config.Update.Branch = selectedBranch;
                    _hasLoadedBranchOptions = true;
                    OnPropertyChanged(nameof(SelectedBranch));
                    OnPropertyChanged(nameof(SelectedTargetLabel));
                    OnPropertyChanged(nameof(BranchOptions));
                    PersistPendingUpdateSnapshot();
                });
            }
            catch
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var selectedBranch = string.IsNullOrWhiteSpace(_selectedBranch)
                        ? _config.Update.Branch
                        : _selectedBranch;

                    selectedBranch = string.IsNullOrWhiteSpace(selectedBranch)
                        ? "main"
                        : selectedBranch.Trim();

                    if (!BranchOptions.Any(branch =>
                            string.Equals(branch, selectedBranch, StringComparison.OrdinalIgnoreCase)))
                    {
                        BranchOptions.Add(selectedBranch);
                    }

                    _selectedBranch = selectedBranch;
                    _config.Update.Branch = selectedBranch;
                    // A failed load should stay retryable the next time the dialog opens.
                    _hasLoadedBranchOptions = false;
                    OnPropertyChanged(nameof(SelectedBranch));
                    OnPropertyChanged(nameof(SelectedTargetLabel));
                    OnPropertyChanged(nameof(BranchOptions));
                    PersistPendingUpdateSnapshot();
                });
            }
            finally
            {
                _isRefreshingBranchOptions = false;
            }
        }

        /// <summary>
        /// Loads release versions for the current release stream without forcing an update check.
        /// </summary>
        private async Task LoadReleaseOptionsAsync()
        {
            if (_isRefreshingReleaseOptions)
            {
                return;
            }

            _isRefreshingReleaseOptions = true;

            try
            {
                var releases = await Task.Run(() => _updateManager.GetModuleReleaseCandidatesAsync(
                    _config,
                    isManualRequest: true));
                var releaseOptions = BuildReleaseOptions(releases);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var preferredKey = ResolvePreferredReleaseSelectionKey(releaseOptions);
                    ReleaseOptions.Clear();

                    foreach (var release in releaseOptions)
                    {
                        ReleaseOptions.Add(release);
                    }

                    _selectedReleaseOption = ReleaseOptions.FirstOrDefault(candidate =>
                        string.Equals(
                            ResolveReleaseSelectionKey(candidate),
                            preferredKey,
                            StringComparison.OrdinalIgnoreCase));

                    _hasLoadedReleaseOptions = true;
                    _loadedReleaseMode = _selectedSourceMode;
                    if (_selectedReleaseOption != null)
                    {
                        _config.Update.SelectedReleaseTag = ResolveReleaseSelectionKey(_selectedReleaseOption);
                    }

                    OnPropertyChanged(nameof(ReleaseOptions));
                    OnPropertyChanged(nameof(SelectedReleaseOption));
                    OnPropertyChanged(nameof(SelectedTargetLabel));
                    OnPropertyChanged(nameof(CanInstallSelectedUpdate));
                    OnPropertyChanged(nameof(ShowInstallAction));
                    RefreshCommandStates();
                    PersistPendingUpdateSnapshot();
                });
            }
            finally
            {
                _isRefreshingReleaseOptions = false;
            }
        }

        /// <summary>
        /// Builds picker options and prepends the virtual latest target when releases are available.
        /// </summary>
        private static List<UpdateCandidate> BuildReleaseOptions(IReadOnlyList<UpdateCandidate> releases)
        {
            if (releases.Count == 0)
            {
                return [];
            }

            var options = new List<UpdateCandidate>
            {
                BuildLatestReleaseOption(releases[0])
            };
            options.AddRange(releases);
            return options;
        }

        /// <summary>
        /// Creates the virtual latest option while keeping the concrete release tag for installation.
        /// </summary>
        private static UpdateCandidate BuildLatestReleaseOption(UpdateCandidate latest)
        {
            return new UpdateCandidate
            {
                TargetKind = latest.TargetKind,
                TargetId = latest.TargetId,
                Name = latest.Name,
                DisplayName = LatestReleaseSelection,
                CurrentVersion = latest.CurrentVersion,
                RemoteVersion = latest.RemoteVersion,
                Channel = latest.Channel,
                Mode = latest.Mode,
                DownloadUrl = latest.DownloadUrl,
                ReferenceName = latest.ReferenceName,
                ReleaseTag = latest.ReleaseTag,
                CommitSha = latest.CommitSha,
                IsVirtualLatest = true,
                IsPrerelease = latest.IsPrerelease,
                PublishedAt = latest.PublishedAt,
                Module = latest.Module
            };
        }

        /// <summary>
        /// Picks the release selection key that should stay selected after a version list refresh.
        /// </summary>
        private string ResolvePreferredReleaseSelectionKey(IEnumerable<UpdateCandidate> releases)
        {
            if (!string.IsNullOrWhiteSpace(_config.Update.SelectedReleaseTag) &&
                releases.Any(candidate =>
                    string.Equals(
                        ResolveReleaseSelectionKey(candidate),
                        _config.Update.SelectedReleaseTag,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return _config.Update.SelectedReleaseTag;
            }

            return LatestReleaseSelection;
        }

        /// <summary>
        /// Refreshes release action state after an update check.
        /// </summary>
        private void ApplyCheckResultSelection()
        {
            OnPropertyChanged(nameof(CanInstallSelectedUpdate));
            OnPropertyChanged(nameof(ShowInstallAction));
            RefreshCommandStates();
        }

        /// <summary>
        /// Applies the latest available update candidate.
        /// </summary>
        internal async Task<bool> ApplyUpdateAsync(
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null)
        {
            if (IsUpdating)
            {
                return false;
            }

            var installCandidate = await ResolveSelectedInstallCandidateAsync();
            if (installCandidate == null)
            {
                return false;
            }

            IsUpdating = true;
            SetUpdatePhase(UpdateStatusPhase.Updating);
            SetUpdateActivityStatus(UpdateStatus);

            IProgress<string> debugLog = new Progress<string>(message =>
                Debug.WriteLine($"[ModuleUpdate:{Name}] {message}"));

            var success = false;
            var enteredApply = false;
            try
            {
                enteredApply = true;
                var logSink = log;
                var progressSink = progress;

                // Forward messages into both the debug output and the optional dialog log.
                var combinedLog = new Progress<string>(message =>
                {
                    debugLog.Report(message);
                    AppendUpdateLog(message);
                    AdvanceProgressFromLog(message);
                    SetUpdateActivityStatus(message);
                    logSink?.Report(message);
                });

                var combinedProgress = new Progress<DownloadProgress>(download =>
                {
                    UpdateCachedDownloadProgress(download);
                    progressSink?.Report(download);
                });

                success = await Task.Run(() => _updateManager.ApplyModuleUpdateAsync(
                    installCandidate,
                    combinedLog,
                    combinedProgress,
                    isManualRequest: true,
                    ct: CancellationToken.None));

                if (success)
                {
                    await ReloadInstalledConfigAsync();
                }

                SetUpdatePhase(success ? UpdateStatusPhase.Updated : UpdateStatusPhase.Failed);
                SetUpdateActivityStatus(UpdateStatus);
                SetOverallProgress(success ? 1.0 : Math.Max(_updateOverallProgress, 0.15));
                HasUpdate = false;
                _updateCandidate = null;
                OnPropertyChanged(nameof(UpdateCandidate));
                OnPropertyChanged(nameof(AvailableUpdateLabel));
                await EnsureSelectionOptionsLoadedAsync(forceRefresh: false);
                await RefreshUpdateStateAsync(forceOptionLoad: false);
            }
            finally
            {
                if (enteredApply)
                {
                    IsUpdating = false;
                    OnPropertyChanged(nameof(ShowUpdatingStatus));
                    OnPropertyChanged(nameof(CanShowLaunchAction));
                }
            }

            if (enteredApply)
            {
                _onStateChanged?.Invoke();
            }

            return success;
        }


        // Launch flow

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

            await Task.Run(() => _runner.StopModuleAsync(_config.SourcePath));

            _config.Status.Enabled = false;
            await Task.Run(() => _installer.SaveConfigAsync(_config));
            NotifyStateChanged();
            _onStateChanged?.Invoke();
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

            await ReloadEditableConfigAsync();

            IsRestarting = true;

            try
            {
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

        /// <summary>
        /// Reloads the installed manifest after a module update changed files on disk.
        /// </summary>
        private async Task ReloadInstalledConfigAsync()
        {
            var freshConfig = await _installer.LoadModuleConfig(_config.SourcePath);
            if (freshConfig == null)
            {
                return;
            }

            ApplyReloadedConfig(freshConfig);
        }

        /// <summary>
        /// Copies the refreshed manifest data back into the live card view model.
        /// </summary>
        private void ApplyReloadedConfig(ModuleConfig freshConfig)
        {
            // The card keeps the same backing object reference, so we copy new manifest data into it.
            _config.FileVersion = freshConfig.FileVersion;
            _config.Id = freshConfig.Id;
            _config.Name = freshConfig.Name;
            _config.Description = freshConfig.Description;
            _config.Version = freshConfig.Version;
            _config.Author = freshConfig.Author;
            _config.Type = freshConfig.Type;
            _config.Category = freshConfig.Category;
            _config.SupportedPlatforms = freshConfig.SupportedPlatforms;
            _config.Engines = freshConfig.Engines;
            _config.Source = freshConfig.Source;
            _config.Dependencies = freshConfig.Dependencies;
            _config.Commands = freshConfig.Commands;
            _config.HasPage = freshConfig.HasPage;
            _config.Icon = freshConfig.Icon;
            _config.SidebarIcon = freshConfig.SidebarIcon;
            _config.Settings = freshConfig.Settings;
            _config.SettingCategories = freshConfig.SettingCategories;
            _config.DownloadsBridge = freshConfig.DownloadsBridge;
            _config.ModuleInterop = freshConfig.ModuleInterop;
            _config.Update = freshConfig.Update;
            _config.Status = freshConfig.Status;
            _config.SourcePath = freshConfig.SourcePath;

            _config.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);

            _config.Update.Normalize();
            _selectedSourceMode = _config.Update.Mode;
            _selectedBranch = _config.Update.Branch;
            _selectedReleaseOption = null;
            _hasLoadedReleaseOptions = false;
            _loadedReleaseMode = string.Empty;

            // Rebuild branches so the picker keeps the persisted value after a release-based update.
            BranchOptions.Clear();
            BranchOptions.Add(_selectedBranch);
            _hasLoadedBranchOptions = false;

            NotifyModuleMetadataChanged();
            NotifyStateChanged();
            TryHydratePendingUpdateFromConfig();
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
            OnPropertyChanged(nameof(ShowUpdatingStatus));
            RefreshCommandStates();
        }

        /// <summary>
        /// Raises property changes for module metadata that may change after an update.
        /// </summary>
        private void NotifyModuleMetadataChanged()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(VersionString));
            OnPropertyChanged(nameof(IconFullPath));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(SourceModeOptions));
            OnPropertyChanged(nameof(ReleaseOptions));
            OnPropertyChanged(nameof(BranchOptions));
            OnPropertyChanged(nameof(SelectedSourceMode));
            OnPropertyChanged(nameof(SelectedReleaseOption));
            OnPropertyChanged(nameof(SelectedBranch));
            OnPropertyChanged(nameof(IsBranchMode));
            OnPropertyChanged(nameof(IsReleaseMode));
            OnPropertyChanged(nameof(SelectedTargetLabel));
            OnPropertyChanged(nameof(CanInstallSelectedUpdate));
            OnPropertyChanged(nameof(ShowInstallAction));
            OnPropertyChanged(nameof(ShowCardUpdateAction));
            OnPropertyChanged(nameof(ShowHeaderBusyIndicator));
        }

        /// <summary>
        /// Returns the update candidate represented by the current UI selection.
        /// </summary>
        private async Task<UpdateCandidate?> ResolveSelectedInstallCandidateAsync()
        {
            SynchronizeSelectionForUpdateOperation();

            if (IsBranchMode)
            {
                return await Task.Run(() => _updateManager.ResolveModuleInstallCandidateAsync(
                    _config,
                    isManualRequest: true));
            }

            return ResolveSelectedReleaseInstallCandidate();
        }

        /// <summary>
        /// Returns the release candidate represented by the current picker selection.
        /// </summary>
        private UpdateCandidate? ResolveSelectedReleaseInstallCandidate()
        {
            if (_selectedReleaseOption == null || string.IsNullOrWhiteSpace(_selectedReleaseOption.ReleaseTag))
            {
                return null;
            }

            if (!UpdateManager.HasRecordedRemoteSourceInstall(_config))
            {
                return _selectedReleaseOption;
            }

            var installedRef = !string.IsNullOrWhiteSpace(_config.Update.InstalledReleaseTag)
                ? _config.Update.InstalledReleaseTag
                : (_config.Status.InstalledVersion ?? _config.Version);

            return ReleaseTagOrdering.AreEquivalentVersionReferences(installedRef, _selectedReleaseOption.ReleaseTag)
                ? null
                : _selectedReleaseOption;
        }

        /// <summary>
        /// Returns the persisted selection key for a release picker option.
        /// </summary>
        private static string? ResolveReleaseSelectionKey(UpdateCandidate? candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            return IsLatestReleaseOption(candidate)
                ? LatestReleaseSelection
                : candidate.ReleaseTag;
        }

        /// <summary>
        /// Returns whether the release picker option represents the moving latest target.
        /// </summary>
        private static bool IsLatestReleaseOption(UpdateCandidate? candidate)
        {
            return candidate?.IsVirtualLatest == true;
        }

        /// <summary>
        /// Formats the selected release target without losing the concrete tag behind latest.
        /// </summary>
        private static string? FormatSelectedReleaseTarget(UpdateCandidate? candidate)
        {
            if (candidate == null)
            {
                return null;
            }

            if (candidate.IsVirtualLatest)
            {
                return string.IsNullOrWhiteSpace(candidate.RemoteVersion)
                    ? LatestReleaseSelection
                    : $"{LatestReleaseSelection} ({candidate.RemoteVersion})";
            }

            return !string.IsNullOrWhiteSpace(candidate.DisplayName)
                ? candidate.DisplayName
                : candidate.RemoteVersion;
        }

        /// <summary>
        /// Resets the cached update session before a new dialog action begins.
        /// </summary>
        internal void ResetUpdateSession(bool clearLog)
        {
            if (clearLog)
            {
                _updateLogBuffer.Clear();
                OnPropertyChanged(nameof(UpdateLogText));
                OnPropertyChanged(nameof(HasUpdateLog));
            }

            _updateDownloadDetail = string.Empty;
            _updateOverallProgress = 0;
            _updateFileProgress = 0;
            _hasUpdateProgress = false;
            _lastDownloadedBytes = 0;
            _lastSpeedUpdate = DateTime.UtcNow;
            _lastSpeed = 0;
            _updateActivityStatus = UpdateStatus;

            OnPropertyChanged(nameof(UpdateActivityStatus));
            OnPropertyChanged(nameof(UpdateDownloadDetail));
            OnPropertyChanged(nameof(HasUpdateDownloadDetail));
            OnPropertyChanged(nameof(UpdateOverallProgress));
            OnPropertyChanged(nameof(UpdateFileProgress));
            OnPropertyChanged(nameof(HasUpdateProgress));
        }

        /// <summary>
        /// Clears finished update output before reopening the dialog while preserving active sessions.
        /// </summary>
        internal void ResetCompletedUpdateSession()
        {
            if (IsBusy)
            {
                return;
            }

            // Do not wipe in-flight install UI if flags were lost but status still reflects an active update.
            if (_updateStatusPhase == UpdateStatusPhase.Updating)
            {
                return;
            }

            if (_updateStatusPhase is UpdateStatusPhase.Updated or UpdateStatusPhase.Failed)
            {
                SetUpdatePhase(UpdateStatusPhase.ReadyToCheck);
            }

            ResetUpdateSession(clearLog: true);
            SetUpdateActivityStatus(UpdateStatus);
        }

        /// <summary>
        /// Appends one line to the cached module update console.
        /// </summary>
        internal void AppendUpdateLog(string message)
        {
            _updateLogBuffer.AppendLine(message);
            OnPropertyChanged(nameof(UpdateLogText));
            OnPropertyChanged(nameof(HasUpdateLog));
        }

        /// <summary>
        /// Updates the cached activity status shown in the update dialog.
        /// </summary>
        internal void SetUpdateActivityStatus(string value)
        {
            if (string.Equals(_updateActivityStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            _updateActivityStatus = value;
            OnPropertyChanged(nameof(UpdateActivityStatus));
        }

        /// <summary>
        /// Advances the coarse overall progress based on high-level installer log lines.
        /// </summary>
        private void AdvanceProgressFromLog(string message)
        {
            if (message.Contains("Stopping", StringComparison.OrdinalIgnoreCase))
            {
                SetOverallProgress(0.18);
            }
            else if (message.Contains("Preserved", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("Restored", StringComparison.OrdinalIgnoreCase))
            {
                SetOverallProgress(Math.Max(_updateOverallProgress, 0.62));
            }
            else if (message.Contains("Running first-run setup", StringComparison.OrdinalIgnoreCase))
            {
                SetOverallProgress(Math.Max(_updateOverallProgress, 0.84));
            }
            else if (message.Contains("updated", StringComparison.OrdinalIgnoreCase))
            {
                SetOverallProgress(1.0);
            }
        }

        /// <summary>
        /// Updates the cached transfer progress shown in the module update dialog.
        /// </summary>
        private void UpdateCachedDownloadProgress(DownloadProgress progress)
        {
            if (progress.TotalBytes <= 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSpeedUpdate).TotalSeconds;
            if (elapsed >= 0.5)
            {
                _lastSpeed = (progress.DownloadedBytes - _lastDownloadedBytes) / elapsed;
                _lastDownloadedBytes = progress.DownloadedBytes;
                _lastSpeedUpdate = now;
            }

            var detail = $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}";
            if (_lastSpeed > 0)
            {
                detail += $" - {FormatBytes((long)_lastSpeed)}/s";
            }

            SetOverallProgress(Math.Max(_updateOverallProgress, 0.12 + (progress.Fraction * 0.38)));
            SetFileProgress(progress.Fraction);
            SetDownloadDetail(detail);
        }

        /// <summary>
        /// Updates the cached overall progress fraction.
        /// </summary>
        private void SetOverallProgress(double value)
        {
            var clamped = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(_updateOverallProgress - clamped) < 0.0001)
            {
                return;
            }

            _updateOverallProgress = clamped;
            _hasUpdateProgress = true;
            OnPropertyChanged(nameof(UpdateOverallProgress));
            OnPropertyChanged(nameof(HasUpdateProgress));
        }

        /// <summary>
        /// Updates the cached file progress fraction.
        /// </summary>
        private void SetFileProgress(double value)
        {
            var clamped = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(_updateFileProgress - clamped) < 0.0001)
            {
                return;
            }

            _updateFileProgress = clamped;
            _hasUpdateProgress = true;
            OnPropertyChanged(nameof(UpdateFileProgress));
            OnPropertyChanged(nameof(HasUpdateProgress));
        }

        /// <summary>
        /// Updates the cached download detail line.
        /// </summary>
        private void SetDownloadDetail(string value)
        {
            if (string.Equals(_updateDownloadDetail, value, StringComparison.Ordinal))
            {
                return;
            }

            _updateDownloadDetail = value;
            OnPropertyChanged(nameof(UpdateDownloadDetail));
            OnPropertyChanged(nameof(HasUpdateDownloadDetail));
        }

        /// <summary>
        /// Formats byte counts into readable size labels.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824)
            {
                return $"{bytes / 1_073_741_824.0:F1} GB";
            }

            if (bytes >= 1_048_576)
            {
                return $"{bytes / 1_048_576.0:F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }

            return $"{bytes} B";
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
