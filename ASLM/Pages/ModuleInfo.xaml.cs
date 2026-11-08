// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Public module metadata and installation controls, hosted above the download catalog.
    /// This part covers the three sections (information, configuration, status), the update preferences and the
    /// installation and update actions; the repository documentation lives in the Documents part.
    /// </summary>
    public partial class ModuleInfo : ContentView, ILocalizable
    {
        // Fields and state

        /// <summary>
        /// Module installer that discovers the catalog, reads the registry and reports installation and removal state.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Update manager that loads repository options, checks for updates and runs catalog installations.
        /// </summary>
        private readonly UpdateManager _updates;

        /// <summary>
        /// Engine installer whose discovered engines supply names and versions for the dependency rows.
        /// </summary>
        private readonly EngineInstaller _engines;

        /// <summary>
        /// Trust service that resolves the trust level (official, community reviewed or unreviewed) of the shown module.
        /// </summary>
        private readonly ModuleTrustService _trust;

        /// <summary>
        /// Console store that provides the maintenance log and the activity snapshot of the shown module.
        /// </summary>
        private readonly ModuleConsoleStore _consoleStore;

        /// <summary>
        /// Entry sections shared by Downloads, module cards and update notifications.
        /// </summary>
        public enum Section
        {
            /// <summary>
            /// Public module metadata and repository documents.
            /// </summary>
            Info,

            /// <summary>
            /// Update channel, release and branch preferences.
            /// </summary>
            Configuration,

            /// <summary>
            /// Runtime state and the maintenance console.
            /// </summary>
            Status
        }

        /// <summary>
        /// Section that is currently visible.
        /// </summary>
        private Section _selectedSection;

        /// <summary>
        /// Trust level of the shown module, resolved again whenever the content is rebuilt.
        /// </summary>
        private ModuleTrustLevel _trustLevel;

        /// <summary>
        /// Cancellation source of the running repository option lookup, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _optionsCts;

        /// <summary>
        /// Set while code replaces picker lists or selections, so the picker change handlers do not save preferences.
        /// </summary>
        private bool _synchronizingPickers;

        /// <summary>
        /// Key (source path and update mode) of the options that were loaded last; prevents loading them twice.
        /// </summary>
        private string? _loadedOptionsKey;

        /// <summary>
        /// Localization key of the last option lookup failure, or null when the last lookup succeeded.
        /// </summary>
        private string? _configurationErrorKey;

        /// <summary>
        /// Error text that is inserted into the message of <c>_configurationErrorKey</c>.
        /// </summary>
        private string? _configurationErrorDetail;

        /// <summary>
        /// Latest maintenance snapshot (activity, running state and console session) of the shown module.
        /// </summary>
        private ModuleMaintenanceSnapshot? _activity;

        /// <summary>
        /// Module that is shown in the overlay, or null before the first opening.
        /// </summary>
        private ModuleConfig? _module;

        /// <summary>
        /// Discovered modules, used to show names of module dependencies.
        /// </summary>
        private List<ModuleConfig> _catalog = [];

        /// <summary>
        /// Discovered engines, used to show names and versions of engine dependencies.
        /// </summary>
        private List<EngineConfig> _engineCatalog = [];

        /// <summary>
        /// Installations, update checks and updates started by this view, keyed by the source path of their module.
        /// </summary>
        private readonly Dictionary<string, ModuleOperation> _downloads = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Counter that is increased by every opening; asynchronous results of an earlier opening compare it and are dropped.
        /// </summary>
        private int _openVersion;

        /// <summary>
        /// Set while the catalog snapshot of the current opening is being read.
        /// </summary>
        private bool _isLoading;

        /// <summary>
        /// Whether the shown module is installed (present in the module registry).
        /// </summary>
        private bool _registered;

        /// <summary>
        /// Whether the overlay is open; closing it turns pending asynchronous results into no-ops.
        /// </summary>
        private bool _isOpen;

        /// <summary>
        /// Counter that is increased when the module state is reloaded or preferences are saved,
        /// so an older reload in flight does not overwrite the newer state.
        /// </summary>
        private int _stateRefreshVersion;

        /// <summary>
        /// Cancellation source of the running icon and version lookup, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _presentationCts;

        /// <summary>
        /// Version shown in the overview: the manifest version, replaced by the catalog version once it is known.
        /// </summary>
        private string _displayVersion = string.Empty;

        /// <summary>
        /// Module icon: the local icon file, replaced by catalog artwork when that is loaded.
        /// </summary>
        private ImageSource? _iconSource;


        // Events

        /// <summary>
        /// Raised when the overlay asks its host to close it.
        /// </summary>
        public event EventHandler? CloseRequested;


        // Overview data

        /// <summary>
        /// Public metadata rows shown on the information tab.
        /// </summary>
        public ObservableCollection<InfoField> Fields { get; } = [];

        /// <summary>
        /// Name of the shown module, or an empty string when no module is open.
        /// </summary>
        public string ModuleName => _module?.Name ?? string.Empty;

        /// <summary>
        /// Description of the shown module, or an empty string when there is none.
        /// </summary>
        public string Description => _module?.Description ?? string.Empty;

        /// <summary>
        /// Localized "Version: x" caption, or an empty string when no version is known.
        /// </summary>
        public string VersionText => string.IsNullOrWhiteSpace(_displayVersion) ? string.Empty
            : $"{L.Get(LocalizationKeys.ModuleInfo_Version)}: {_displayVersion}";

        /// <summary>
        /// Localized "Author: x" caption, or an empty string when the manifest names no author.
        /// </summary>
        public string AuthorText => string.IsNullOrWhiteSpace(_module?.Author) ? string.Empty
            : $"{L.Get(LocalizationKeys.ModuleInfo_Author)}: {_module.Author}";

        /// <summary>
        /// Whether a version caption is shown.
        /// </summary>
        public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);

        /// <summary>
        /// Whether the manifest marks the module as beta.
        /// </summary>
        public bool IsBeta => _module?.IsBeta == true;

        /// <summary>
        /// Whether the manifest marks the module as experimental.
        /// </summary>
        public bool IsExperimental => _module?.IsExperimental == true;

        /// <summary>
        /// Whether at least one development status badge is shown.
        /// </summary>
        public bool HasDevelopmentStatus => IsBeta || IsExperimental;

        /// <summary>
        /// Localized caption of the beta badge.
        /// </summary>
        public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);

        /// <summary>
        /// Localized caption of the experimental badge.
        /// </summary>
        public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);

        /// <summary>
        /// Whether an author caption is shown.
        /// </summary>
        public bool HasAuthor => !string.IsNullOrWhiteSpace(AuthorText);

        /// <summary>
        /// Whether a module description is shown.
        /// </summary>
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        /// <summary>
        /// Icon of the shown module, or null when it has none.
        /// </summary>
        public ImageSource? IconSource => _iconSource;

        /// <summary>
        /// Whether an icon is shown.
        /// </summary>
        public bool HasIcon => IconSource != null;

        /// <summary>
        /// Whether the overview block has anything to show; the block is hidden otherwise.
        /// </summary>
        public bool HasOverview => HasIcon || HasDescription || HasVersion || HasAuthor || HasDevelopmentStatus;


        // Section and trust state

        /// <summary>
        /// Whether the information tab is selected.
        /// </summary>
        public bool IsInfoSelected => _selectedSection == Section.Info;

        /// <summary>
        /// Whether the configuration tab is selected.
        /// </summary>
        public bool IsConfigurationSelected => _selectedSection == Section.Configuration;

        /// <summary>
        /// Whether the status tab is selected.
        /// </summary>
        public bool IsStatusSelected => _selectedSection == Section.Status;

        /// <summary>
        /// Whether the verified badge is shown, which is the case for official modules.
        /// </summary>
        public bool ShowVerifiedBadge => _module != null && _trustLevel == ModuleTrustLevel.Official;

        /// <summary>
        /// Whether the "not verified" warning is shown, which is the case for unreviewed modules.
        /// </summary>
        public bool ShowUnverifiedWarning => _module != null && _trustLevel == ModuleTrustLevel.Unreviewed;

        /// <summary>
        /// Localized text of the "not verified" warning.
        /// </summary>
        public string NotVerifiedText => L.Get(LocalizationKeys.Modules_NotVerified);

        /// <summary>
        /// Public GitHub URL of the module repository, or null when the module cannot be downloaded.
        /// </summary>
        public string? SourceUrl => _module == null ? null : GetSourceUrl(_module);

        /// <summary>
        /// Whether the module has a repository link.
        /// </summary>
        public bool HasSourceLink => SourceUrl != null;


        // Configuration state

        /// <summary>
        /// Whether install, update and cancel actions are offered; a caller that defers installation (the setup flow) turns them off.
        /// </summary>
        public bool AllowInstallationActions { get; private set; } = true;

        /// <summary>
        /// Update modes the channel picker offers; a fixed initial list until the repository options are loaded.
        /// </summary>
        public IReadOnlyList<string> ChannelOptions { get; private set; } = ModuleDownloadOptions.InitialChannels;

        /// <summary>
        /// Releases the release picker offers, starting with the moving "latest" entry.
        /// </summary>
        public IReadOnlyList<UpdateCandidate> ReleaseOptions { get; private set; } = [];

        /// <summary>
        /// Branches the branch picker offers.
        /// </summary>
        public IReadOnlyList<string> BranchOptions { get; private set; } = [];

        /// <summary>
        /// Whether the module follows a repository branch instead of releases.
        /// </summary>
        public bool IsBranchMode => _module?.Update.Mode == "branch";

        /// <summary>
        /// Whether the module follows releases, so the release picker is shown instead of the branch picker.
        /// </summary>
        public bool IsReleaseMode => !IsBranchMode;

        /// <summary>
        /// Whether the repository options are being loaded.
        /// </summary>
        public bool IsLoadingOptions { get; private set; }

        /// <summary>
        /// Whether update preferences can be edited: the overlay is open and loaded, no operation or removal runs,
        /// and the module has a repository to configure.
        /// </summary>
        public bool CanConfigure => _isOpen && !_isLoading && !IsBusy && _activity?.Activity == null && HasSourceLink &&
            _module != null && _installer.GetRemovalState(_module.Id) == null;

        /// <summary>
        /// Whether the release or branch target can be changed; this also waits for the repository options to load.
        /// </summary>
        public bool CanSelectTarget => CanConfigure && !IsLoadingOptions;

        /// <summary>
        /// Message shown in the configuration tab: the last option lookup error, a note that the module has no
        /// source, or an empty string.
        /// </summary>
        public string ConfigurationMessage => _configurationErrorKey != null
            ? L.Get(_configurationErrorKey, _configurationErrorDetail ?? string.Empty)
            : !HasSourceLink && _module != null ? L.Get(LocalizationKeys.ModuleInfo_NoSource) : string.Empty;

        /// <summary>
        /// Whether a configuration message is shown.
        /// </summary>
        public bool HasConfigurationMessage => !string.IsNullOrEmpty(ConfigurationMessage);


        // Status and installation state

        /// <summary>
        /// Localized state of the module: loading, the installer's current state label,
        /// or the state derived from registration and maintenance activity.
        /// </summary>
        public string ModuleStateText => _isLoading ? L.Get(LocalizationKeys.ModuleInfo_Loading)
            : (_module == null ? null : _installer.GetInstallationState(_module.Id)?.Label)
                ?? L.Get(ResolveStateKey(_registered, _activity?.IsRunning == true, _activity?.Activity));

        /// <summary>
        /// Maintenance console text of the shown module; it is read only while the status tab is selected.
        /// </summary>
        public string LogText { get; private set; } = string.Empty;

        /// <summary>
        /// Whether the maintenance console has any text.
        /// </summary>
        public bool HasLog => !string.IsNullOrWhiteSpace(LogText);

        /// <summary>
        /// Key of the console session that <see cref="LogText"/> belongs to; the console view resets its scroll position when it changes.
        /// </summary>
        public string LogSessionKey { get; private set; } = string.Empty;

        /// <summary>
        /// Operation that this view started for the shown module, or null when there is none.
        /// </summary>
        private ModuleOperation? CurrentDownload => _module != null && _downloads.TryGetValue(_module.SourcePath, out var download)
            ? download : null;

        /// <summary>
        /// Whether the shown module is occupied by a check, update, catalog installation or installation,
        /// started by this view or by another part of the application.
        /// </summary>
        public bool IsBusy => CurrentDownload != null || (_module != null && (_updates.IsModuleUpdatePending(_module.SourcePath) ||
            _updates.IsCatalogInstallPending(_module.Id) || _installer.IsInstalling(_module.Id)));

        /// <summary>
        /// Whether an installation or update of the shown module is in progress. A local request may still be checking
        /// metadata, even if it will apply a discovered update.
        /// </summary>
        public bool IsCurrentInstallation => CurrentDownload?.RequestedActivity == ModuleActivity.Installing ||
            (CurrentDownload?.RequestedActivity == ModuleActivity.Updating && _activity?.Activity == ModuleActivity.Updating);

        /// <summary>
        /// Whether the cancel button is available: an update started here is applying,
        /// or a queued catalog installation of this module (not a dependency installation) can be canceled.
        /// </summary>
        public bool CanCancelInstallation => AllowInstallationActions && ((CurrentDownload?.RequestedActivity == ModuleActivity.Updating && IsCurrentInstallation) ||
            (_module != null && _updates.IsCatalogInstallPending(_module.Id) &&
            _installer.GetInstallationState(_module.Id) is not { IsDependency: true }));

        /// <summary>
        /// Whether the install button is shown for a module that is not installed and not occupied.
        /// </summary>
        public bool ShowInstallButton => AllowInstallationActions && !_registered && !IsBusy && _activity?.Activity != ModuleActivity.Installing;

        /// <summary>
        /// Whether an update check of the shown module is running.
        /// </summary>
        public bool IsCheckingUpdates => _module != null && _updates.IsCheckingModuleUpdate(_module.SourcePath);

        /// <summary>
        /// Whether an installed module can be checked for updates or updated.
        /// </summary>
        public bool CanCheckOrUpdate => AllowInstallationActions && _registered && CanConfigure && !IsLoadingOptions;

        /// <summary>
        /// Whether the update button is shown: the module is installed and a pending update candidate exists.
        /// </summary>
        public bool ShowUpdateButton => AllowInstallationActions && _registered && !IsBusy && _activity?.Activity != ModuleActivity.Updating && _module != null &&
            _updates.TryRestorePendingUpdateCandidate(_module, out _);

        /// <summary>
        /// Whether the module can be installed now: it is idle, supported on this platform, downloadable,
        /// and either not installed or installed without a completed first run.
        /// </summary>
        public bool CanInstall => AllowInstallationActions && !_isLoading && !IsBusy && _activity?.Activity == null && _module is { IsSupportedOnCurrentPlatform: true } &&
                                   (!_registered || !_module.Status.FirstRunCompleted) &&
                                   UpdateManager.CanDownloadModule(_module);

        /// <summary>
        /// Progress of the current operation from 0 to 1, or 0 when there is none.
        /// </summary>
        public double DownloadFraction => CurrentDownload?.Fraction ?? 0;

        /// <summary>
        /// Transfer caption (percent and sizes) of the current operation, or an empty string.
        /// </summary>
        public string TransferText => CurrentDownload?.TransferText ?? string.Empty;

        /// <summary>
        /// Whether a transfer caption is shown.
        /// </summary>
        public bool HasTransferText => !string.IsNullOrWhiteSpace(TransferText);


        // Initialization

        /// <summary>
        /// Creates the module information view and attaches its localization and visual lifecycle.
        /// </summary>
        public ModuleInfo(ModuleInstaller installer, UpdateManager updates, EngineInstaller engines,
            ModuleTrustService trust, AppLocalizationService localization, ModuleConsoleStore consoleStore)
        {
            // Attach services before creating bindings and native controls.
            _installer = installer;
            _updates = updates;
            _engines = engines;
            _trust = trust;
            _consoleStore = consoleStore;
            InitializeComponent();
            BindingContext = this;
            LocalizableAttach.Hook(this, localization, this);

            // Keep global subscriptions scoped to the attached view and native picker lifecycle.
            SizeChanged += OnSizeChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            // Re-apply the stored picker selections whenever a picker finishes loading or gets a new native handler.
            foreach (var picker in new[] { ChannelPicker, ReleasePicker, BranchPicker })
            {
                picker.Loaded += (_, _) => SyncConfigurationPickers();
                picker.HandlerChanged += (_, _) => Dispatcher.Dispatch(SyncConfigurationPickers);
            }
        }

        /// <summary>
        /// Attaches shared operation and theme events while the overlay is in the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            // Remove before adding so a repeated native Loaded event cannot duplicate refresh work.
            _updates.ModuleUpdateStateChanged -= OnConsoleStateChanged;
            _updates.ModuleUpdateStateChanged += OnConsoleStateChanged;
            _consoleStore.StateChanged -= OnConsoleStateChanged;
            _consoleStore.StateChanged += OnConsoleStateChanged;
            _installer.ModulesChanged -= OnModulesChanged;
            _installer.ModulesChanged += OnModulesChanged;

            // Theme changes retint the source link icon.
            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
                app.RequestedThemeChanged += OnRequestedThemeChanged;
            }

            // Operations may have progressed while the view was detached.
            RefreshSourceIcon();
            RefreshActivity();
        }

        /// <summary>
        /// Detaches global events and cancels presentation requests without interrupting module maintenance.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            _updates.ModuleUpdateStateChanged -= OnConsoleStateChanged;
            _consoleStore.StateChanged -= OnConsoleStateChanged;
            _installer.ModulesChanged -= OnModulesChanged;
            ThemeService.PaletteApplied -= OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
            }

            // View-owned lookups cannot update detached selectors; service-owned jobs continue normally.
            CancelOptionsLoad();
            _presentationCts?.Cancel();
            CancelDocumentRequests();
        }

        /// <summary>
        /// Recalculates overlay bounds after the application window changes size.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e) => UpdateDialogSize();


        // Opening and localization

        /// <summary>
        /// Loads current metadata and optionally navigates directly to a requested section.
        /// Maintenance completion reloads skip a redundant check so the operation log remains visible.
        /// </summary>
        /// <param name="module">Module to show; it is replaced by the freshly discovered manifest once that is read.</param>
        /// <param name="section">Section to select, or null to keep the current one (new modules start on Info).</param>
        /// <param name="checkForUpdates">Whether an installed module is checked for updates after its options are loaded.</param>
        /// <param name="allowInstallationActions">Whether install, update and cancel actions are offered.</param>
        public async Task OpenAsync(ModuleConfig module, Section? section = null, bool checkForUpdates = true,
            bool allowInstallationActions = true)
        {
            // Setup can edit the same persisted preferences while reserving installation for its final step.
            AllowInstallationActions = allowInstallationActions;
            OnPropertyChanged(nameof(AllowInstallationActions));

            // Invalidate older lookups before publishing the immediate local selection.
            var version = ++_openVersion;
            _isLoading = true;
            ResetDocuments();
            ++_stateRefreshVersion;
            CancelOptionsLoad();
            _loadedOptionsKey = null;
            _configurationErrorKey = null;

            // The current selection is local data; only the list of available alternatives needs GitHub.
            ChannelOptions = ModuleDownloadOptions.InitialChannels;

            // Another module starts on the information tab; the same module keeps the tab the user was on.
            _isOpen = true;
            if (!string.Equals(_module?.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase))
                SelectSection(Section.Info);

            // Show the module passed by the caller at once, before the catalog snapshot is read.
            _module = module;
            _module.Normalize();
            _displayVersion = module.Version;
            _iconSource = File.Exists(module.IconFullPath) ? ImageSource.FromFile(module.IconFullPath) : null;
            _registered = false;
            if (section.HasValue) SelectSection(section.Value);
            ResetConfigurationOptions();
            RefreshContent();
            UpdateDialogSize();

            try
            {
                // Read the current catalog away from the UI thread, then accept only this open request.
                var snapshot = await Task.Run(async () => (
                    Modules: await _installer.DiscoverModulesAsync(),
                    Engines: _engines.DiscoverEngines(),
                    Installed: _installer.Registry.ReadIds()));
                if (version != _openVersion) return;

                // Replace the caller's module with the freshly discovered one and read its registration.
                _catalog = snapshot.Modules;
                _engineCatalog = snapshot.Engines;
                _module = _catalog.FirstOrDefault(candidate => string.Equals(candidate.Id, module.Id,
                    StringComparison.OrdinalIgnoreCase)) ?? module;
                _registered = snapshot.Installed.Contains(module.Id);
                _updates.ApplyCatalogDefaults(_module, _registered);
            }
            catch (Exception ex)
            {
                // A superseded opening reports nothing; otherwise the failure goes to the module console.
                if (version != _openVersion) return;
                AppendActivityError(module, ex);
            }
            finally
            {
                // Only the newest opening finishes loading and starts the secondary lookups (icon and version,
                // documents, repository options); an older one must not touch the view.
                if (version == _openVersion)
                {
                    _isLoading = false;
                    ResetConfigurationOptions();
                    RefreshContent();
                    _ = RefreshPresentationAsync();
                    if (IsInfoSelected) _ = LoadDocumentsAsync();
                    _ = LoadConfigurationOptionsAsync(checkForUpdates: _registered && checkForUpdates);
                }
            }
        }

        /// <summary>
        /// Refreshes localized labels and option captions without changing the saved selection.
        /// </summary>
        public void ApplyLocalization()
        {
            // Captions of the buttons, tabs and configuration labels.
            ToolTipProperties.SetText(CloseButton, L.Get(LocalizationKeys.ModulesDownloads_Close));
            InstallButton.Text = L.Get(LocalizationKeys.Common_Install_Action);
            UpdateButton.Text = L.Get(LocalizationKeys.ModuleUpdate_InstallUpdate);
            CancelButton.Text = L.Get(LocalizationKeys.Common_Cancel);
            InfoTabButton.Text = L.Get(LocalizationKeys.ModuleInfo_InfoTab);
            StatusTabButton.Text = L.Get(LocalizationKeys.ModuleInfo_StatusTab);
            ConfigurationTabButton.Text = L.Get(LocalizationKeys.ModuleInfo_ConfigurationTab);
            StateTitleLabel.Text = L.Get(LocalizationKeys.ModuleInfo_StateTitle);
            ChannelLabel.Text = L.Get(LocalizationKeys.ModuleInfo_Channel);
            ReleaseLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_ReleaseVersion);
            BranchLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_RepositoryBranch);
            DocumentRetryButton.Text = L.Get(LocalizationKeys.SetupWizard_Retry);
            RaiseDocumentProperties();

            // Tooltip and accessibility text of the repository link button.
            var linkText = L.Get(LocalizationKeys.Downloads_OpenLink);
            ToolTipProperties.SetText(SourceLinkButton, linkText);
            SemanticProperties.SetDescription(SourceLinkButton, linkText);

            // Rebuild the release list from its concrete releases (the "latest" entry is recreated), then refresh the bindings.
            ReleaseOptions = BuildReleaseOptions(ReleaseOptions.Where(option => !option.IsVirtualLatest).ToList(),
                _module?.Update.SelectedReleaseTag);
            RefreshConfiguration();
            RefreshContent();
        }


        // Content and activity

        /// <summary>
        /// Rebuilds public metadata and footer bindings from the current module snapshot.
        /// </summary>
        private void RefreshContent()
        {
            // Rebuild public information from the current manifest, never from runtime command text.
            Fields.Clear();
            if (_module != null)
            {
                _trustLevel = _trust.Resolve(_module);
                foreach (var field in CreateFields(_module, _catalog, _engineCatalog))
                    Fields.Add(field);
            }

            // Notify the bindings of every property that depends on the module or the operation state.
            foreach (var property in new[] { nameof(ModuleName), nameof(Description), nameof(HasDescription),
                         nameof(IconSource), nameof(HasIcon), nameof(HasOverview), nameof(IsBusy), nameof(IsCurrentInstallation),
                         nameof(VersionText), nameof(AuthorText), nameof(HasVersion), nameof(HasAuthor),
                         nameof(IsBeta), nameof(IsExperimental), nameof(HasDevelopmentStatus), nameof(BetaLabel), nameof(ExperimentalLabel),
                         nameof(CanInstall), nameof(ShowInstallButton), nameof(IsCheckingUpdates),
                         nameof(CanCheckOrUpdate), nameof(ShowUpdateButton),
                         nameof(ShowVerifiedBadge), nameof(ShowUnverifiedWarning), nameof(NotVerifiedText),
                         nameof(SourceUrl), nameof(HasSourceLink),
                         nameof(DownloadFraction), nameof(TransferText), nameof(HasTransferText) })
                OnPropertyChanged(property);

            RefreshActivity();
            RefreshConfiguration();
        }

        /// <summary>
        /// Maps runtime activity to the shared module status labels. A maintenance activity wins; without one an
        /// unregistered module is not installed, and a registered one is either running or installed.
        /// </summary>
        internal static string ResolveStateKey(bool registered, bool running, ModuleActivity? activity) => activity switch
        {
            ModuleActivity.Installing => LocalizationKeys.SetupWizard_Installing,
            ModuleActivity.Updating => LocalizationKeys.Modules_Updating,
            ModuleActivity.Restarting => LocalizationKeys.Modules_Restarting,
            ModuleActivity.Stopping => LocalizationKeys.Home_Module_Stopping,
            _ when !registered => LocalizationKeys.ModuleInfo_NotInstalled,
            _ when running => LocalizationKeys.Home_Metric_Running,
            _ => LocalizationKeys.ModuleInfo_Installed
        };

        /// <summary>
        /// Refreshes the activity state on the main thread when the shared console or update state changes while the overlay is open.
        /// </summary>
        private void OnConsoleStateChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isOpen) RefreshActivity();
        });

        /// <summary>
        /// Updates maintenance state and reads console output only for the visible Status section.
        /// </summary>
        private void RefreshActivity()
        {
            // Keep checking, transferring, and module runtime state separate in the footer and status card.
            _activity = _module == null ? null : _consoleStore.GetMaintenanceSnapshot(_module.SourcePath);

            // Everything that depends on the activity snapshot or on an operation state is re-read by the bindings.
            OnPropertyChanged(nameof(ModuleStateText));
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(ShowInstallButton));
            OnPropertyChanged(nameof(CanCancelInstallation));
            OnPropertyChanged(nameof(IsCurrentInstallation));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanSelectTarget));
            OnPropertyChanged(nameof(IsCheckingUpdates));
            OnPropertyChanged(nameof(CanCheckOrUpdate));
            OnPropertyChanged(nameof(ShowUpdateButton));

            // Console text can be large, so it is copied only while the Status section shows it.
            if (!IsStatusSelected) return;
            var hadLog = HasLog;
            LogSessionKey = _activity?.SessionKey ?? string.Empty;
            LogText = _activity?.Text ?? string.Empty;
            OnPropertyChanged(nameof(LogSessionKey));
            OnPropertyChanged(nameof(LogText));
            OnPropertyChanged(nameof(HasLog));

            // The first output changes the console size, so its layout must be measured again.
            if (!hadLog && HasLog) RefreshLogLayout();
        }

        /// <summary>
        /// Reloads external manifest changes without replacing an active local operation.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!_isOpen || _module == null) return;
            var selected = _module;

            // Progress notifications must not replace a selection being checked or installed in this view.
            if (CurrentDownload != null) { RefreshContent(); return; }

            // Only the newest reload may apply its result: the version changes whenever another reload starts
            // or preferences are saved, and the shown module must still be the one that was reloaded.
            var version = ++_stateRefreshVersion;
            try
            {
                var fresh = await _installer.LoadModuleConfig(selected.SourcePath);
                var ids = await Task.Run(_installer.Registry.ReadIds);
                if (version != _stateRefreshVersion || !ReferenceEquals(_module, selected)) return;

                // Apply the reloaded manifest and registration, then rebuild the options and the repository data.
                _module = fresh ?? selected;
                _registered = ids.Contains(selected.Id);
                _updates.ApplyCatalogDefaults(_module, _registered);
                CancelOptionsLoad();
                _loadedOptionsKey = null;
                ResetConfigurationOptions();
                RefreshContent();
                _ = RefreshPresentationAsync();
                _ = LoadConfigurationOptionsAsync();
            }
            catch (Exception ex)
            {
                // A failed background reload keeps the current state; it is only traced.
                System.Diagnostics.Debug.WriteLine(ex);
            }
        });


        // Section navigation

        /// <summary>
        /// Selects public module information without starting maintenance.
        /// </summary>
        private void OnInfoTabClicked(object? sender, EventArgs e) => SelectSection(Section.Info);

        /// <summary>
        /// Selects download preferences and restores native picker selections.
        /// </summary>
        private void OnConfigurationTabClicked(object? sender, EventArgs e) => SelectSection(Section.Configuration);

        /// <summary>
        /// Selects runtime status and the existing maintenance console.
        /// </summary>
        private void OnStatusTabClicked(object? sender, EventArgs e) => SelectSection(Section.Status);

        /// <summary>
        /// Switches the visible section and lazily restores its content.
        /// </summary>
        private void SelectSection(Section selected)
        {
            _selectedSection = selected;
            OnPropertyChanged(nameof(IsInfoSelected));
            OnPropertyChanged(nameof(IsConfigurationSelected));
            OnPropertyChanged(nameof(IsStatusSelected));
            RefreshActivity();

            // Each section loads its data on first use: picker options, console layout, repository documents.
            if (IsConfigurationSelected)
            {
                Dispatcher.Dispatch(SyncConfigurationPickers);
                _ = LoadConfigurationOptionsAsync();
            }

            if (IsStatusSelected && HasLog) RefreshLogLayout();
            if (IsInfoSelected && !_isLoading && !_documentsLoaded && _documentListCts == null)
                _ = LoadDocumentsAsync();
        }

        /// <summary>
        /// Remeasures console content when the first maintenance output becomes visible.
        /// </summary>
        private void RefreshLogLayout() => Dispatcher.Dispatch(() =>
        {
            ActivityLogPanel.InvalidateMeasure();
            ActivityLogConsole.InvalidateMeasure();
        });


        // Download configuration

        /// <summary>
        /// Displays saved release and branch targets immediately while remote choices are loading.
        /// </summary>
        private void ResetConfigurationOptions()
        {
            ReleaseOptions = BuildReleaseOptions([], _module?.Update.SelectedReleaseTag);
            BranchOptions = _module == null ? [] : [_module.Update.Branch];
            RefreshConfiguration();
        }

        /// <summary>
        /// Keeps a pinned target distinct from the moving latest alias and its display metadata.
        /// </summary>
        /// <param name="releases">Concrete releases loaded from the repository; the first one is the target of "latest".</param>
        /// <param name="selectedTag">Saved release tag, which is added when the repository list no longer contains it.</param>
        /// <returns>The "latest" entry followed by the distinct concrete releases.</returns>
        internal static IReadOnlyList<UpdateCandidate> BuildReleaseOptions(
            IReadOnlyList<UpdateCandidate> releases, string? selectedTag)
        {
            // The first entry is the moving "latest" alias, labeled with the release it currently points to.
            var options = new List<UpdateCandidate>
            {
                new() { IsVirtualLatest = true, DisplayName = UpdateManager.FormatLatestRelease(
                    releases.FirstOrDefault(option => !option.IsVirtualLatest)) }
            };

            // Then the concrete releases, one per tag.
            options.AddRange(releases.Where(option => !option.IsVirtualLatest && !string.IsNullOrWhiteSpace(option.ReleaseTag))
                .DistinctBy(option => option.ReleaseTag, StringComparer.OrdinalIgnoreCase));

            // A pinned tag that is missing from the list stays selectable, so the saved choice is not lost.
            if (!string.IsNullOrWhiteSpace(selectedTag) &&
                !string.Equals(selectedTag, ModuleUpdateConfig.LatestReleaseTag, StringComparison.OrdinalIgnoreCase) &&
                !options.Any(option => string.Equals(option.ReleaseTag, selectedTag, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new() { DisplayName = selectedTag, ReleaseTag = selectedTag });
            }

            return options;
        }

        /// <summary>
        /// Cancels only the repository option lookup and clears its transient loading state.
        /// </summary>
        private void CancelOptionsLoad()
        {
            _optionsCts?.Cancel();
            _optionsCts = null;
            IsLoadingOptions = false;
        }

        /// <summary>
        /// Loads available channels and checks changed preferences only after resolving their effective target.
        /// </summary>
        /// <param name="checkForUpdates">Whether to check for updates after the options are loaded;
        /// a change of the saved selection forces the check.</param>
        private async Task LoadConfigurationOptionsAsync(bool checkForUpdates = false)
        {
            if (!CanConfigure || !_isOpen || _module == null)
            {
                return;
            }

            // Bind the request to both its module and the current user selection.
            var module = _module;
            var revision = _stateRefreshVersion;
            var mode = module.Update.Mode;
            var key = $"{module.SourcePath}|{mode}";
            if (_loadedOptionsKey == key || IsLoadingOptions)
            {
                return;
            }

            // Mark the lookup as running; its token lets a newer selection or closing the view cancel it.
            using var cts = new CancellationTokenSource();
            _optionsCts = cts;
            IsLoadingOptions = true;
            _configurationErrorKey = null;
            RefreshConfiguration();
            try
            {
                var options = await _updates.GetModuleDownloadOptionsAsync(module, cts.Token, isManualRequest: true);

                // Drop the response when the lookup was canceled or another module is shown now.
                if (cts.IsCancellationRequested || !ReferenceEquals(_module, module))
                {
                    return;
                }

                // A confirmed missing channel may change the selection; a failed request never does.
                ChannelOptions = options.Channels;
                if (options.ApplyTo(module))
                {
                    _updates.SaveModuleUpdatePreferences(module);
                    checkForUpdates = true;
                    RefreshContent();
                }

                // Keep an explicit branch visible even when it has since disappeared from the repository.
                BranchOptions = options.Mode == "branch"
                    ? options.Branches.Append(module.Update.Branch).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
                ReleaseOptions = options.Mode == "branch" ? [] : BuildReleaseOptions(options.Releases, module.Update.SelectedReleaseTag);
                _loadedOptionsKey = $"{module.SourcePath}|{module.Update.Mode}";
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer selection or a closed view has replaced this lookup.
            }
            catch (Exception ex)
            {
                // A canceled or superseded lookup reports no error; otherwise the message is shown in the tab.
                if (cts.IsCancellationRequested || !ReferenceEquals(_module, module))
                {
                    return;
                }

                _configurationErrorKey = LocalizationKeys.ModuleUpdate_LoadOptionsFailedFormat;
                _configurationErrorDetail = ex.Message;
            }
            finally
            {
                // Release the loading state only if no newer lookup has replaced this one.
                if (ReferenceEquals(_optionsCts, cts))
                {
                    _optionsCts = null;
                    IsLoadingOptions = false;
                    RefreshConfiguration();
                    Dispatcher.Dispatch(SyncConfigurationPickers);
                }
            }

            // A canceled lookup or a response for a previous selection must never start a new check.
            if (checkForUpdates && !cts.IsCancellationRequested && _configurationErrorKey == null &&
                revision == _stateRefreshVersion && ReferenceEquals(_module, module) &&
                _loadedOptionsKey == $"{module.SourcePath}|{module.Update.Mode}" && CanCheckOrUpdate)
            {
                await RunUpdateAsync(apply: false);
            }
        }

        /// <summary>
        /// Refreshes option lists under a guard so native selection resets never save preferences.
        /// </summary>
        private void RefreshConfiguration()
        {
            // List replacement may reset a native Picker to -1; this must not become a preference edit.
            _synchronizingPickers = true;
            try
            {
                foreach (var property in new[] { nameof(ChannelOptions), nameof(ReleaseOptions), nameof(BranchOptions), nameof(IsBranchMode),
                             nameof(IsReleaseMode), nameof(CanConfigure), nameof(CanSelectTarget), nameof(IsLoadingOptions),
                             nameof(ConfigurationMessage), nameof(HasConfigurationMessage), nameof(CanCheckOrUpdate) })
                {
                    OnPropertyChanged(property);
                }

                SyncConfigurationPickers();
            }
            finally
            {
                _synchronizingPickers = false;
            }
        }

        /// <summary>
        /// Restores native picker selections while suppressing programmatic change events.
        /// </summary>
        private void SyncConfigurationPickers()
        {
            var wasSynchronizing = _synchronizingPickers;
            _synchronizingPickers = true;
            try
            {
                // Select the saved mode, release (the "latest" entry for an empty or latest tag) and branch.
                ChannelPicker.SelectedIndex = ChannelOptions.ToList().FindIndex(mode => mode == _module?.Update.Mode);
                var tag = _module?.Update.SelectedReleaseTag;
                var latest = string.IsNullOrWhiteSpace(tag) ||
                             string.Equals(tag, ModuleUpdateConfig.LatestReleaseTag, StringComparison.OrdinalIgnoreCase);
                ReleasePicker.SelectedIndex = ReleaseOptions.ToList().FindIndex(option => latest ? option.IsVirtualLatest
                    : string.Equals(option.ReleaseTag, tag, StringComparison.OrdinalIgnoreCase));
                BranchPicker.SelectedIndex = BranchOptions.ToList().FindIndex(branch =>
                    string.Equals(branch, _module?.Update.Branch, StringComparison.OrdinalIgnoreCase));
#if WINDOWS
                // WinUI can hydrate the list after MAUI's selection pass when switching release/branch columns.
                foreach (var picker in new[] { ChannelPicker, ReleasePicker, BranchPicker })
                {
                    try
                    {
                        if (picker.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ComboBox combo &&
                            picker.SelectedIndex < combo.Items.Count)
                        {
                            combo.SelectedIndex = picker.SelectedIndex;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The deferred/Loaded pass retries when the native item collection finishes updating.
                        System.Diagnostics.Debug.WriteLine(ex);
                    }
                }
#endif
            }
            finally
            {
                // Restore the previous value, because RefreshConfiguration calls this method while it holds the guard.
                _synchronizingPickers = wasSynchronizing;
            }
        }

        /// <summary>
        /// Saves the channel, resolves unavailable-source fallbacks, then checks without installing anything.
        /// </summary>
        private async void OnChannelChanged(object? sender, EventArgs e)
        {
            // Ignore programmatic selections and pickers that cannot be edited now, and unchanged values.
            if (_synchronizingPickers || !CanConfigure || ChannelPicker.SelectedItem is not string mode ||
                _module?.Update.Mode == mode)
            {
                return;
            }

            // Cancel old option lookups before persisting the user's new source mode.
            CancelOptionsLoad();
            _loadedOptionsKey = null;
            var saved = SaveConfiguration(update =>
            {
                update.Mode = mode;
                update.Channel = mode == "pre-release" ? "pre-release" : "release";
            });
            ResetConfigurationOptions();

            // Load the options of the new mode; with them the effective target is known and the check can run.
            if (saved)
            {
                await LoadConfigurationOptionsAsync(checkForUpdates: true);
            }
        }

        /// <summary>
        /// Checks the newly selected release so the update action never refers to the previous target.
        /// </summary>
        private async void OnReleaseChanged(object? sender, EventArgs e)
        {
            // Ignore programmatic selections and pickers that cannot be edited now.
            if (_synchronizingPickers || !CanSelectTarget || ReleasePicker.SelectedItem is not UpdateCandidate option)
            {
                return;
            }

            // The virtual "latest" entry is stored as the moving alias, any other entry as its concrete tag.
            var tag = option.IsVirtualLatest ? ModuleUpdateConfig.LatestReleaseTag : option.ReleaseTag;
            if (string.Equals(_module?.Update.SelectedReleaseTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Refresh the candidate only after the explicit tag or moving alias has been saved.
            if (SaveConfiguration(update => update.SelectedReleaseTag = tag))
            {
                await RunUpdateAsync(apply: false);
            }
        }

        /// <summary>
        /// Checks the saved branch without changing the selected section or starting a download.
        /// </summary>
        private async void OnBranchChanged(object? sender, EventArgs e)
        {
            // Ignore programmatic selections, pickers that cannot be edited now, and unchanged values.
            if (_synchronizingPickers || !CanSelectTarget || BranchPicker.SelectedItem is not string branch ||
                _module?.Update.Branch == branch)
            {
                return;
            }

            // Checking this branch must not apply it or navigate away from Configuration.
            if (SaveConfiguration(update => update.Branch = branch))
            {
                await RunUpdateAsync(apply: false);
            }
        }

        /// <summary>
        /// Persists an explicit source edit and restores the previous selection if saving fails.
        /// </summary>
        /// <param name="change">Edit applied to the update configuration of the shown module.</param>
        /// <returns>True when the preferences were saved; false when there is no module or saving failed.</returns>
        private bool SaveConfiguration(Action<ModuleUpdateConfig> change)
        {
            if (_module == null) return false;

            // An older in-flight manifest reload must not replace this selection.
            ++_stateRefreshVersion;
            var update = _module.Update;

            // Saving preferences must not leave a half-applied selection in the UI after an IO failure.
            var previous = (update.Mode, update.Channel, update.Branch, update.SelectedReleaseTag, update.PendingUpdate, update.UseDefaultChannel);
            try
            {
                // Apply the edit, drop the stale pending update candidate and persist the preferences.
                change(update);
                if (update.Mode != "branch") update.SelectedReleaseTag ??= ModuleUpdateConfig.LatestReleaseTag;
                update.PendingUpdate = null;
                _updates.SaveModuleUpdatePreferences(_module);
                _configurationErrorKey = null;
                _ = RefreshPresentationAsync();
                return true;
            }
            catch (Exception ex)
            {
                // Roll back to the values from before the edit and report the failure in the module console.
                (update.Mode, update.Channel, update.Branch, update.SelectedReleaseTag, update.PendingUpdate, update.UseDefaultChannel) = previous;
                AppendActivityError(_module, ex);
                return false;
            }
            finally { RefreshContent(); }
        }


        // Repository presentation

        /// <summary>
        /// Records an operation failure in the module console without adding text to the status card.
        /// </summary>
        private void AppendActivityError(ModuleConfig module, Exception error) =>
            _consoleStore.CreateMaintenanceLog(module, null, reset: false)
                .Report(L.Get(LocalizationKeys.ModuleInfo_Failed, error.Message));

        /// <summary>
        /// Loads version and artwork independently and discards responses for a previous module.
        /// </summary>
        private async Task RefreshPresentationAsync()
        {
            // A newer request replaces the one in flight; its results are rejected through the cancellation token.
            _presentationCts?.Cancel();
            if (_module == null || !_isOpen) return;
            var module = _module;
            using var cts = new CancellationTokenSource();
            _presentationCts = cts;

            // Check request identity before accepting asynchronous presentation data.
            bool IsCurrent() => !cts.IsCancellationRequested && _isOpen && ReferenceEquals(_module, module);

            // Load artwork independently so a slow version query cannot delay a cached icon.
            async Task LoadIconAsync()
            {
                var artwork = await _updates.GetModuleCatalogArtworkAsync(module, cts.Token);
                if (!IsCurrent() || artwork == null) return;
                _iconSource = artwork.Icon is { } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;
                OnPropertyChanged(nameof(IconSource));
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(HasOverview));
            }

            // Refresh the displayed version without replacing an installed module's metadata.
            async Task LoadVersionAsync()
            {
                var version = await _updates.GetModuleCatalogVersionAsync(module, _registered, cts.Token);
                if (!IsCurrent() || _displayVersion == version) return;
                _displayVersion = version;
                OnPropertyChanged(nameof(VersionText));
                OnPropertyChanged(nameof(HasVersion));
                OnPropertyChanged(nameof(HasOverview));
            }

            // Run both lookups in parallel; failures only leave the previous icon and version in place.
            try
            {
                await Task.WhenAll(LoadIconAsync(), LoadVersionAsync());
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer request or a closed view canceled this one, so there is nothing to report.
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            finally
            {
                if (ReferenceEquals(_presentationCts, cts)) _presentationCts = null;
            }
        }

        /// <summary>
        /// Returns a supported repository link without exposing a local manifest path.
        /// </summary>
        internal static string? GetSourceUrl(ModuleConfig module) => UpdateManager.CanDownloadModule(module)
            ? $"https://github.com/{module.Source.Repo.Trim().Trim('/')}" : null;

        /// <summary>
        /// Refreshes the source-link glyph after a custom palette changes.
        /// </summary>
        private void OnPaletteApplied() => MainThread.BeginInvokeOnMainThread(RefreshSourceIcon);

        /// <summary>
        /// Reapplies the link tint after a system theme change.
        /// </summary>
        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => OnPaletteApplied();

        /// <summary>
        /// Resolves the link glyph from the shared theme-tinted icon cache.
        /// </summary>
        private void RefreshSourceIcon() => SourceLinkButton.Source =
            PackagedIconTintCache.Get("icon_link.png", IconTintHelper.ResolvePaletteColor("LabelPrimary"));


        // Public metadata

        /// <summary>
        /// Builds the information rows from public metadata only; command lines, settings, local paths and
        /// bridge internals stay private.
        /// </summary>
        /// <param name="module">Module whose manifest supplies the values.</param>
        /// <param name="catalog">Discovered modules, used to show the names of module dependencies.</param>
        /// <param name="engines">Discovered engines, used to show the names and versions of engine dependencies.</param>
        /// <returns>The rows that have a value, in display order.</returns>
        internal static IReadOnlyList<InfoField> CreateFields(ModuleConfig module,
            IReadOnlyList<ModuleConfig> catalog, IReadOnlyList<EngineConfig> engines)
        {
            // Format public values explicitly; internal commands, paths and setting values remain hidden.
            var fields = new List<InfoField>();

            // Add only available public values to the information card.
            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    fields.Add(new(key, value));
                }
            }

            // Use the same engine precedence as module discovery.
            EngineConfig? FindEngine(string id) => engines.FirstOrDefault(engine =>
                string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase)) ?? module.Engines.FirstOrDefault(engine =>
                string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));

            // Fall back to the engine identifier when no display name is available.
            string EngineName(string id) => FindEngine(id) is { Name.Length: > 0 } engine ? engine.Name : id;

            // Identity and update source; the target shown depends on whether the module follows a branch or releases.
            Add(LocalizationKeys.ModuleInfo_Id, module.Id);
            Add(LocalizationKeys.ModuleInfo_Type, module.Type);
            Add(LocalizationKeys.ModuleInfo_Categories, string.Join(", ", module.Category));
            if (module.HasDeclaredUpdateConfig)
            {
                Add(LocalizationKeys.ModuleInfo_Channel, module.Update.Mode);
                if (module.Update.Mode == "branch")
                    Add(LocalizationKeys.ModuleUpdate_RepositoryBranch, module.Update.Branch);
                else
                    Add(LocalizationKeys.ModuleUpdate_ReleaseVersion, module.Update.SelectedReleaseTag);
            }

            // Supported platforms: one line per operating system with its distinct architectures.
            Add(LocalizationKeys.ModuleInfo_Platforms, string.Join("\n", module.EffectiveSupportedPlatforms
                .GroupBy(platform => platform.Os, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key} {string.Join("/", group.Select(platform => platform.Arch).Distinct(StringComparer.OrdinalIgnoreCase))}")));

            // Required engines with their version when known and, when the module provides the engine itself, its description.
            var requiredEngines = module.Dependencies.Engines.Select(dep =>
            {
                var engine = FindEngine(dep.Id);
                var value = EngineName(dep.Id);
                if (!string.IsNullOrWhiteSpace(engine?.Version)) value += $" v.: {engine.Version}";
                var description = module.Engines.FirstOrDefault(provided =>
                    string.Equals(provided.Id, dep.Id, StringComparison.OrdinalIgnoreCase))?.Description;
                return new InfoValue(value, description);
            }).ToList();
            if (requiredEngines.Count > 0)
                fields.Add(new(LocalizationKeys.ModuleInfo_Engines, string.Join("\n", requiredEngines.Select(engine => engine.Text)))
                { Items = requiredEngines });

            // Libraries each engine must provide; the row title contains the engine name, so it is not localized.
            foreach (var dep in module.Dependencies.Engines.Where(dep => dep.Libraries.Count > 0))
                fields.Add(new($"{EngineName(dep.Id)} packages", string.Join(", ", dep.Libraries)) { LiteralTitle = true });

            // Module and model dependencies; modules are shown by name when they are in the catalog.
            Add(LocalizationKeys.ModuleInfo_Modules, string.Join("\n", module.Dependencies.Modules.Select(dep =>
                catalog.FirstOrDefault(item => string.Equals(item.Id, dep.Id, StringComparison.OrdinalIgnoreCase)) is { } item
                    ? $"{item.Name} ({item.Id})" : dep.Id)));
            Add(LocalizationKeys.ModuleInfo_Models, string.Join(", ", module.Dependencies.Models));
            return fields;
        }


        // Installation and update actions

        /// <summary>
        /// Queues installation and tracks only this module's progress and completion.
        /// </summary>
        private async void OnInstallClicked(object? sender, EventArgs e)
        {
            if (!CanInstall || _module == null) return;

            // Reserve local progress before submitting the job to the shared installation queue.
            var selected = _module;
            using var cts = new CancellationTokenSource();
            var download = new ModuleOperation(cts, ModuleActivity.Installing);
            _downloads.Add(selected.SourcePath, download);
            var resultKey = LocalizationKeys.ModuleInfo_Installed;
            var resultDetail = string.Empty;

            // Switch to the Status section, where the progress and the console output are shown.
            CancelOptionsLoad();
            SelectSection(Section.Status);
            RefreshContent();

            var log = new Progress<string>(message => System.Diagnostics.Debug.WriteLine(message));

            // Progress is stored on the operation and published to the bindings only while it is still the current one.
            var progress = new Progress<DownloadProgress>(value =>
            {
                if (!_downloads.TryGetValue(selected.SourcePath, out var pending) || !ReferenceEquals(pending, download)) return;
                download.Fraction = Math.Clamp(value.Fraction, 0, 1);
                download.TransferText = value.TotalBytes > 0
                    ? $"{download.Fraction:P0} · {DownloadsView.FormatDownloadSize(value.DownloadedBytes)} / {DownloadsView.FormatDownloadSize(value.TotalBytes)}"
                    : DownloadsView.FormatDownloadSize(value.DownloadedBytes);
                if (!ReferenceEquals(CurrentDownload, download)) return;
                OnPropertyChanged(nameof(DownloadFraction));
                OnPropertyChanged(nameof(TransferText));
                OnPropertyChanged(nameof(HasTransferText));
            });

            // Install through the shared queue and record how it ended; the result is written to the console below.
            try
            {
                var launch = await _updates.InstallCatalogModuleAsync(selected, log, progress, cts.Token);
                if (launch.Status is not (ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning or ModuleLaunchStatus.NoRunCommands))
                {
                    // Installation remains recorded even if the subsequent launch fails.
                    resultKey = LocalizationKeys.ModuleInfo_Failed;
                    resultDetail = launch.Message ?? launch.Status.ToString();
                }
            }
            catch (OperationCanceledException)
            {
                resultKey = LocalizationKeys.ModuleInfo_Canceled;
            }
            catch (Exception ex)
            {
                resultKey = LocalizationKeys.ModuleInfo_Failed;
                resultDetail = ex.Message;
            }
            finally
            {
                // Log the result, release the local operation and reload the view if it still shows this module.
                _consoleStore.CreateMaintenanceLog(selected, null, reset: false).Report(
                    L.Get(resultKey, resultDetail));
                _downloads.Remove(selected.SourcePath);

                // Finishing one queued job must not reset another module's selection or progress.
                if (_isOpen && _module is { } current &&
                    string.Equals(current.SourcePath, selected.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    await OpenAsync(current, checkForUpdates: false);
                }
            }
        }

        /// <summary>
        /// Cancels the selected update or the root catalog installation without canceling other modules.
        /// </summary>
        private void OnCancelClicked(object? sender, EventArgs e)
        {
            if (!CanCancelInstallation) return;

            // An update started here is canceled through its own token; a queued catalog installation through the service.
            if (CurrentDownload is { RequestedActivity: ModuleActivity.Updating } operation) operation.Cancellation.Cancel();
            else if (CanCancelInstallation && _module != null) _updates.CancelCatalogInstall(_module.Id);
        }

        /// <summary>
        /// Rechecks and installs the currently selected source instead of using a stale candidate.
        /// </summary>
        private async void OnUpdateClicked(object? sender, EventArgs e) => await RunUpdateAsync(apply: true);

        /// <summary>
        /// Runs a check or explicit update, navigating to Status only for the latter.
        /// </summary>
        /// <param name="apply">True to install the update; false to only check for one.</param>
        private async Task RunUpdateAsync(bool apply)
        {
            if (!AllowInstallationActions || !_registered || !CanConfigure || _module == null)
            {
                return;
            }

            // Reserve this view's request independently of the service's shared check/update state.
            var selected = _module;
            CancelOptionsLoad();
            using var cts = new CancellationTokenSource();
            var operation = new ModuleOperation(cts, apply ? ModuleActivity.Updating : null);
            _downloads.Add(selected.SourcePath, operation);
            if (apply)
            {
                SelectSection(Section.Status);
            }

            RefreshContent();
            try
            {
                // Progress belongs to the operation, not the currently visible module or tab.
                var progress = new Progress<DownloadProgress>(value =>
                {
                    operation.Fraction = Math.Clamp(value.Fraction, 0, 1);
                    operation.TransferText = value.TotalBytes > 0
                        ? $"{operation.Fraction:P0} · {DownloadsView.FormatDownloadSize(value.DownloadedBytes)} / {DownloadsView.FormatDownloadSize(value.TotalBytes)}"
                        : DownloadsView.FormatDownloadSize(value.DownloadedBytes);
                    if (!ReferenceEquals(CurrentDownload, operation)) return;
                    OnPropertyChanged(nameof(DownloadFraction));
                    OnPropertyChanged(nameof(TransferText));
                    OnPropertyChanged(nameof(HasTransferText));
                });

                // Either install the update or only check whether one is available.
                var checkedModule = apply
                    ? await _updates.UpdateModuleAsync(selected, progress, cts.Token)
                    : await _updates.CheckModuleUpdatesAsync(selected, cts.Token);

                // A check changes preferences and the pending candidate, not the installed metadata.
                // Updating these in place avoids resetting selectors and briefly displaying Loading.
                if (!apply && checkedModule != null && ReferenceEquals(_module, selected))
                {
                    selected.Update = checkedModule.Update;
                }
            }
            finally
            {
                // Release the operation; the view is updated only if it still shows the same module.
                _downloads.Remove(selected.SourcePath);
                if (_isOpen && _module is { } current &&
                    string.Equals(current.SourcePath, selected.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    if (apply)
                    {
                        // An installed update changes the manifest, so the module is opened again.
                        await OpenAsync(current, checkForUpdates: false);
                    }
                    else
                    {
                        // After a check reload the options only when the update mode no longer matches the loaded ones.
                        RefreshContent();
                        if (_loadedOptionsKey != $"{current.SourcePath}|{current.Update.Mode}")
                        {
                            ChannelOptions = ModuleDownloadOptions.InitialChannels;
                            ResetConfigurationOptions();
                            _ = LoadConfigurationOptionsAsync();
                        }
                    }
                }
            }
        }


        // Operation tracking

        /// <summary>
        /// Tracks an owned request; checks have no requested maintenance activity or transfer controls.
        /// </summary>
        private sealed class ModuleOperation(CancellationTokenSource cancellation, ModuleActivity? requestedActivity)
        {
            /// <summary>
            /// Maintenance activity the request started, or null for a plain update check.
            /// </summary>
            public ModuleActivity? RequestedActivity { get; } = requestedActivity;

            /// <summary>
            /// Cancellation source that cancels the request.
            /// </summary>
            public CancellationTokenSource Cancellation { get; } = cancellation;

            /// <summary>
            /// Transfer progress from 0 to 1.
            /// </summary>
            public double Fraction { get; set; }

            /// <summary>
            /// Caption of the transfer: percent and transferred size.
            /// </summary>
            public string TransferText { get; set; } = string.Empty;
        }


        // Closing and layout

        /// <summary>
        /// Closes the overlay through the same path as an outside click.
        /// </summary>
        private void OnCloseClicked(object? sender, EventArgs e) => RequestClose();

        /// <summary>
        /// Consumes taps inside the content so they do not close the overlay.
        /// </summary>
        private void OnDialogTapped(object? sender, EventArgs e)
        {
            // Intentionally empty: handling the tap here stops it from reaching the overlay backdrop.
        }

        /// <summary>
        /// Closes the view and cancels presentation lookups without canceling installation.
        /// </summary>
        public void RequestClose()
        {
            // Mark the view closed first, so results of lookups that are still running are discarded.
            _isOpen = false;
            CancelOptionsLoad();
            _presentationCts?.Cancel();
            CancelDocumentRequests();
            DocumentPreview.SetDocument(null);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Opens the selected module's public repository.
        /// </summary>
        private async void OnSourceLinkClicked(object? sender, EventArgs e)
        {
            if (SourceUrl is { } url) await OpenLinkAsync(url);
        }

        /// <summary>
        /// Opens an external link and records failures in the existing module console.
        /// </summary>
        private async Task OpenLinkAsync(string url)
        {
            var module = _module;
            try { await Launcher.Default.OpenAsync(new Uri(url)); }
            catch (Exception ex)
            {
                // Show the failure in the console of the module and bring the Status section to the front.
                if (module != null) AppendActivityError(module, ex);
                if (_isOpen && ReferenceEquals(module, _module)) SelectSection(Section.Status);
            }
        }

        /// <summary>
        /// Fits the module overlay within the current application window.
        /// </summary>
        private void UpdateDialogSize()
        {
            // Nothing to fit before the first layout pass; afterwards the dialog takes about 78% of the width and
            // 82% of the height within fixed limits, never more than the view minus a 24-unit margin.
            if (Width <= 0 || Height <= 0) return;
            DialogBorder.WidthRequest = Math.Min(Math.Max(0, Width - 24), Math.Clamp(Math.Floor(Width * 0.78), 860, 1200));
            DialogBorder.HeightRequest = Math.Min(Math.Max(0, Height - 24), Math.Clamp(Math.Floor(Height * 0.82), 560, 820));
        }


        // Metadata row types

        /// <summary>
        /// One public metadata row with its localized title and optional engine descriptions.
        /// </summary>
        public sealed record InfoField(string Key, string Value)
        {
            /// <summary>
            /// Whether <see cref="InfoField.Key"/> is shown as it is instead of being looked up as a localization key.
            /// </summary>
            public bool LiteralTitle { get; init; }

            /// <summary>
            /// Title of the row: the key itself for a literal title, otherwise its localized text.
            /// </summary>
            public string Title => LiteralTitle ? Key : L.Get(Key);

            /// <summary>
            /// Values of the row; a single value built from <see cref="InfoField.Value"/> unless the creator supplies its own.
            /// </summary>
            public IReadOnlyList<InfoValue> Items { get; init; } = [new(Value)];
        }

        /// <summary>
        /// A metadata value and optional hover description.
        /// </summary>
        public sealed record InfoValue(string Text, string? Description = null);
    }
}
