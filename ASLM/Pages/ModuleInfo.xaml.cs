// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages;

/// <summary>
/// Public module metadata and installation controls, hosted above the download catalog.
/// </summary>
public partial class ModuleInfo : ContentView, ILocalizable
{
    // Fields and state

    private readonly ModuleInstaller _installer;
    private readonly UpdateManager _updates;
    private readonly EngineInstaller _engines;
    private readonly ModuleTrustService _trust;
    private readonly ModuleConsoleStore _consoleStore;
    /// <summary>
    /// Entry sections shared by Downloads, module cards and update notifications.
    /// </summary>
    public enum Section { Info, Configuration, Status }
    private Section _selectedSection;
    private ModuleTrustLevel _trustLevel;
    private CancellationTokenSource? _optionsCts;
    private bool _synchronizingPickers;
    private string? _loadedOptionsKey;
    private string? _configurationErrorKey;
    private string? _configurationErrorDetail;
    private ModuleMaintenanceSnapshot? _activity;
    private ModuleConfig? _module;
    private List<ModuleConfig> _catalog = [];
    private List<EngineConfig> _engineCatalog = [];
    private readonly Dictionary<string, ModuleOperation> _downloads = new(StringComparer.OrdinalIgnoreCase);
    private int _openVersion;
    private bool _isLoading;
    private bool _registered;
    private bool _isOpen;
    private int _stateRefreshVersion;
    private CancellationTokenSource? _presentationCts;
    private string _displayVersion = string.Empty;
    private ImageSource? _iconSource;

    // View data

    public event EventHandler? CloseRequested;
    public ObservableCollection<InfoField> Fields { get; } = [];
    public string ModuleName => _module?.Name ?? string.Empty;
    public string Description => _module?.Description ?? string.Empty;
    public string VersionText => string.IsNullOrWhiteSpace(_displayVersion) ? string.Empty
        : $"{L.Get(LocalizationKeys.ModuleInfo_Version)}: {_displayVersion}";
    public string AuthorText => string.IsNullOrWhiteSpace(_module?.Author) ? string.Empty
        : $"{L.Get(LocalizationKeys.ModuleInfo_Author)}: {_module.Author}";
    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);
    public bool IsBeta => _module?.IsBeta == true;
    public bool IsExperimental => _module?.IsExperimental == true;
    public bool HasDevelopmentStatus => IsBeta || IsExperimental;
    public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);
    public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);
    public bool HasAuthor => !string.IsNullOrWhiteSpace(AuthorText);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public ImageSource? IconSource => _iconSource;
    public bool HasIcon => IconSource != null;
    public bool HasOverview => HasIcon || HasDescription || HasVersion || HasAuthor || HasDevelopmentStatus;
    public bool IsInfoSelected => _selectedSection == Section.Info;
    public bool IsConfigurationSelected => _selectedSection == Section.Configuration;
    public bool IsStatusSelected => _selectedSection == Section.Status;
    public bool ShowVerifiedBadge => _module != null && _trustLevel == ModuleTrustLevel.Official;
    public bool ShowUnverifiedWarning => _module != null && _trustLevel == ModuleTrustLevel.Unreviewed;
    public string NotVerifiedText => L.Get(LocalizationKeys.Modules_NotVerified);
    public string? SourceUrl => _module == null ? null : GetSourceUrl(_module);
    public bool HasSourceLink => SourceUrl != null;
    public IReadOnlyList<string> ChannelOptions { get; private set; } = ModuleDownloadOptions.InitialChannels;
    public IReadOnlyList<UpdateCandidate> ReleaseOptions { get; private set; } = [];
    public IReadOnlyList<string> BranchOptions { get; private set; } = [];
    public bool IsBranchMode => _module?.Update.Mode == "branch";
    public bool IsReleaseMode => !IsBranchMode;
    public bool IsLoadingOptions { get; private set; }
    public bool CanConfigure => _isOpen && !_isLoading && !IsBusy && _activity?.Activity == null && HasSourceLink &&
        _module != null && _installer.GetRemovalState(_module.Id) == null;
    public bool CanSelectTarget => CanConfigure && !IsLoadingOptions;
    public string ConfigurationMessage => _configurationErrorKey != null
        ? L.Get(_configurationErrorKey, _configurationErrorDetail ?? string.Empty)
        : !HasSourceLink && _module != null ? L.Get(LocalizationKeys.ModuleInfo_NoSource) : string.Empty;
    public bool HasConfigurationMessage => !string.IsNullOrEmpty(ConfigurationMessage);
    public string ModuleStateText => _isLoading ? L.Get(LocalizationKeys.ModuleInfo_Loading)
        : (_module == null ? null : _installer.GetInstallationState(_module.Id)?.Label)
            ?? L.Get(ResolveStateKey(_registered, _activity?.IsRunning == true, _activity?.Activity));
    public string LogText { get; private set; } = string.Empty;
    public bool HasLog => !string.IsNullOrWhiteSpace(LogText);
    public string LogSessionKey { get; private set; } = string.Empty;
    private ModuleOperation? CurrentDownload => _module != null && _downloads.TryGetValue(_module.SourcePath, out var download)
        ? download : null;
    public bool IsBusy => CurrentDownload != null || (_module != null && (_updates.IsModuleUpdatePending(_module.SourcePath) ||
        _updates.IsCatalogInstallPending(_module.Id) || _installer.IsInstalling(_module.Id)));
    // A local request may still be checking metadata, even if it will apply a discovered update.
    public bool IsCurrentInstallation => CurrentDownload?.RequestedActivity == ModuleActivity.Installing ||
        (CurrentDownload?.RequestedActivity == ModuleActivity.Updating && _activity?.Activity == ModuleActivity.Updating);
    public bool CanCancelInstallation => (CurrentDownload?.RequestedActivity == ModuleActivity.Updating && IsCurrentInstallation) ||
        (_module != null && _updates.IsCatalogInstallPending(_module.Id) &&
        _installer.GetInstallationState(_module.Id) is not { IsDependency: true });
    public bool ShowInstallButton => !_registered && !IsBusy && _activity?.Activity != ModuleActivity.Installing;
    public bool IsCheckingUpdates => _module != null && _updates.IsCheckingModuleUpdate(_module.SourcePath);
    public bool CanCheckOrUpdate => _registered && CanConfigure && !IsLoadingOptions;
    public bool ShowUpdateButton => _registered && !IsBusy && _activity?.Activity != ModuleActivity.Updating && _module != null &&
        _updates.TryRestorePendingUpdateCandidate(_module, out _);
    public bool CanInstall => !_isLoading && !IsBusy && _activity?.Activity == null && _module is { IsSupportedOnCurrentPlatform: true } &&
                               (!_registered || !_module.Status.FirstRunCompleted) &&
                               UpdateManager.CanDownloadModule(_module);
    public double DownloadFraction => CurrentDownload?.Fraction ?? 0;
    public string TransferText => CurrentDownload?.TransferText ?? string.Empty;
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
    public async Task OpenAsync(ModuleConfig module, Section? section = null, bool checkForUpdates = true)
    {
        // Invalidate older lookups before publishing the immediate local selection.
        var version = ++_openVersion;
        ++_stateRefreshVersion;
        CancelOptionsLoad();
        _loadedOptionsKey = null;
        _configurationErrorKey = null;
        // The current selection is local data; only the list of available alternatives needs GitHub.
        ChannelOptions = ModuleDownloadOptions.InitialChannels;
        _isOpen = true;
        if (!string.Equals(_module?.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase))
            SelectSection(Section.Info);
        _module = module;
        _module.Normalize();
        _displayVersion = module.Version;
        _iconSource = File.Exists(module.IconFullPath) ? ImageSource.FromFile(module.IconFullPath) : null;
        _isLoading = true;
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
            _catalog = snapshot.Modules;
            _engineCatalog = snapshot.Engines;
            _module = _catalog.FirstOrDefault(candidate => string.Equals(candidate.Id, module.Id,
                StringComparison.OrdinalIgnoreCase)) ?? module;
            _registered = snapshot.Installed.Contains(module.Id);
            _updates.ApplyCatalogDefaults(_module, _registered);
        }
        catch (Exception ex)
        {
            if (version != _openVersion) return;
            AppendActivityError(module, ex);
        }
        finally
        {
            if (version == _openVersion)
            {
                _isLoading = false;
                ResetConfigurationOptions();
                RefreshContent();
                _ = RefreshPresentationAsync();
                _ = LoadConfigurationOptionsAsync(checkForUpdates: _registered && checkForUpdates);
            }
        }
    }

    /// <summary>
    /// Refreshes localized labels and option captions without changing the saved selection.
    /// </summary>
    public void ApplyLocalization()
    {
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
        var linkText = L.Get(LocalizationKeys.Downloads_OpenLink);
        ToolTipProperties.SetText(SourceLinkButton, linkText);
        SemanticProperties.SetDescription(SourceLinkButton, linkText);
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
    /// Maps runtime activity to the shared module status labels.
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
    /// Dispatches shared console and update-state changes to the visible view.
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
        if (!IsStatusSelected) return;
        var hadLog = HasLog;
        LogSessionKey = _activity?.SessionKey ?? string.Empty;
        LogText = _activity?.Text ?? string.Empty;
        OnPropertyChanged(nameof(LogSessionKey));
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
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
        var version = ++_stateRefreshVersion;
        try
        {
            var fresh = await _installer.LoadModuleConfig(selected.SourcePath);
            var ids = await Task.Run(_installer.Registry.ReadIds);
            if (version != _stateRefreshVersion || !ReferenceEquals(_module, selected)) return;
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
        if (IsConfigurationSelected)
        {
            Dispatcher.Dispatch(SyncConfigurationPickers);
            _ = LoadConfigurationOptionsAsync();
        }
        if (IsStatusSelected && HasLog) RefreshLogLayout();
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
    internal static IReadOnlyList<UpdateCandidate> BuildReleaseOptions(
        IReadOnlyList<UpdateCandidate> releases, string? selectedTag)
    {
        var options = new List<UpdateCandidate>
        {
            new() { IsVirtualLatest = true, DisplayName = UpdateManager.FormatLatestRelease(
                releases.FirstOrDefault(option => !option.IsVirtualLatest)) }
        };
        options.AddRange(releases.Where(option => !option.IsVirtualLatest && !string.IsNullOrWhiteSpace(option.ReleaseTag))
            .DistinctBy(option => option.ReleaseTag, StringComparer.OrdinalIgnoreCase));
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
        using var cts = new CancellationTokenSource();
        _optionsCts = cts;
        IsLoadingOptions = true;
        _configurationErrorKey = null;
        RefreshConfiguration();
        try
        {
            var options = await _updates.GetModuleDownloadOptionsAsync(module, cts.Token, isManualRequest: true);
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
            if (cts.IsCancellationRequested || !ReferenceEquals(_module, module))
            {
                return;
            }
            _configurationErrorKey = LocalizationKeys.ModuleUpdate_LoadOptionsFailedFormat;
            _configurationErrorDetail = ex.Message;
        }
        finally
        {
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
            _synchronizingPickers = wasSynchronizing;
        }
    }

    /// <summary>
    /// Saves the channel, resolves unavailable-source fallbacks, then checks without installing anything.
    /// </summary>
    private async void OnChannelChanged(object? sender, EventArgs e)
    {
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
        if (_synchronizingPickers || !CanSelectTarget || ReleasePicker.SelectedItem is not UpdateCandidate option)
        {
            return;
        }

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
    private bool SaveConfiguration(Action<ModuleUpdateConfig> change)
    {
        if (_module == null) return false;
        ++_stateRefreshVersion; // An older in-flight manifest reload must not replace this selection.
        var update = _module.Update;
        // Saving preferences must not leave a half-applied selection in the UI after an IO failure.
        var previous = (update.Mode, update.Channel, update.Branch, update.SelectedReleaseTag, update.PendingUpdate, update.UseDefaultChannel);
        try
        {
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
        try
        {
            await Task.WhenAll(LoadIconAsync(), LoadVersionAsync());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
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
    /// Only public metadata; command lines, settings, local paths and bridge internals stay private.
    /// </summary>
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
        Add(LocalizationKeys.ModuleInfo_Platforms, string.Join("\n", module.EffectiveSupportedPlatforms
            .GroupBy(platform => platform.Os, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key} {string.Join("/", group.Select(platform => platform.Arch).Distinct(StringComparer.OrdinalIgnoreCase))}")));
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
        foreach (var dep in module.Dependencies.Engines.Where(dep => dep.Libraries.Count > 0))
            fields.Add(new($"{EngineName(dep.Id)} packages", string.Join(", ", dep.Libraries)) { LiteralTitle = true });
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
        CancelOptionsLoad();
        SelectSection(Section.Status);
        RefreshContent();
        var log = new Progress<string>(message => System.Diagnostics.Debug.WriteLine(message));
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
    private async Task RunUpdateAsync(bool apply)
    {
        if (!_registered || !CanConfigure || _module == null)
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
            _downloads.Remove(selected.SourcePath);
            if (_isOpen && _module is { } current &&
                string.Equals(current.SourcePath, selected.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                if (apply)
                {
                    await OpenAsync(current, checkForUpdates: false);
                }
                else
                {
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

    /// <summary>
    /// Tracks an owned request; checks have no requested maintenance activity or transfer controls.
    /// </summary>
    private sealed class ModuleOperation(CancellationTokenSource cancellation, ModuleActivity? requestedActivity)
    {
        public ModuleActivity? RequestedActivity { get; } = requestedActivity;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public double Fraction { get; set; }
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
    private void OnDialogTapped(object? sender, EventArgs e) { }
    /// <summary>
    /// Closes the view and cancels presentation lookups without canceling installation.
    /// </summary>
    public void RequestClose()
    {
        _isOpen = false;
        CancelOptionsLoad();
        _presentationCts?.Cancel();
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
            if (module != null) AppendActivityError(module, ex);
            if (_isOpen && ReferenceEquals(module, _module)) SelectSection(Section.Status);
        }
    }

    /// <summary>
    /// Fits the module overlay within the current application window.
    /// </summary>
    private void UpdateDialogSize()
    {
        if (Width <= 0 || Height <= 0) return;
        DialogBorder.WidthRequest = Math.Min(Math.Max(0, Width - 24), Math.Clamp(Math.Floor(Width * 0.78), 860, 1200));
        DialogBorder.HeightRequest = Math.Min(Math.Max(0, Height - 24), Math.Clamp(Math.Floor(Height * 0.82), 560, 820));
    }

    /// <summary>
    /// One public metadata row with its localized title and optional engine descriptions.
    /// </summary>
    public sealed record InfoField(string Key, string Value)
    {
        public bool LiteralTitle { get; init; }
        public string Title => LiteralTitle ? Key : L.Get(Key);
        public IReadOnlyList<InfoValue> Items { get; init; } = [new(Value)];
    }

    /// <summary>
    /// A metadata value and optional hover description.
    /// </summary>
    public sealed record InfoValue(string Text, string? Description = null);
}
