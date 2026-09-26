// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages;

/// <summary>Public module metadata and installation controls, hosted above the download catalog.</summary>
public partial class ModuleInfo : ContentView, ILocalizable
{
    private readonly ModuleInstaller _installer;
    private readonly UpdateManager _updates;
    private readonly EngineInstaller _engines;
    private readonly ModuleTrustService _trust;
    private readonly ModuleConsoleStore _consoleStore;
    private enum Section { Info, Configuration, Status }
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
    private CancellationTokenSource? _downloadCts;
    private int _openVersion;
    private bool _isLoading;
    private bool _registered;
    private string? _activeDownloadPath;
    private bool _isOpen;
    private int _stateRefreshVersion;
    private CancellationTokenSource? _presentationCts;
    private string _displayVersion = string.Empty;
    private ImageSource? _iconSource;

    public event EventHandler? CloseRequested;
    public ObservableCollection<InfoField> Fields { get; } = [];
    public string ModuleName => _module?.Name ?? string.Empty;
    public string Description => _module?.Description ?? string.Empty;
    public string VersionText => string.IsNullOrWhiteSpace(_displayVersion) ? string.Empty
        : $"{L.Get(LocalizationKeys.ModuleInfo_Version)}: {_displayVersion}";
    public string AuthorText => string.IsNullOrWhiteSpace(_module?.Author) ? string.Empty
        : $"{L.Get(LocalizationKeys.ModuleInfo_Author)}: {_module.Author}";
    public bool HasVersion => !string.IsNullOrWhiteSpace(VersionText);
    public bool HasAuthor => !string.IsNullOrWhiteSpace(AuthorText);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public ImageSource? IconSource => _iconSource;
    public bool HasIcon => IconSource != null;
    public bool HasOverview => HasIcon || HasDescription || HasVersion || HasAuthor;
    public bool IsInfoSelected => _selectedSection == Section.Info;
    public bool IsConfigurationSelected => _selectedSection == Section.Configuration;
    public bool IsStatusSelected => _selectedSection == Section.Status;
    public bool ShowVerifiedBadge => _module != null && _trustLevel == ModuleTrustLevel.Official;
    public bool ShowUnverifiedWarning => _module != null && _trustLevel == ModuleTrustLevel.Unreviewed;
    public string NotVerifiedText => L.Get(LocalizationKeys.Modules_NotVerified);
    public string? SourceUrl => _module == null ? null : GetSourceUrl(_module);
    public bool HasSourceLink => SourceUrl != null;
    public IReadOnlyList<string> ChannelOptions { get; } = ["release", "pre-release", "branch"];
    public IReadOnlyList<UpdateCandidate> ReleaseOptions { get; private set; } = [];
    public IReadOnlyList<string> BranchOptions { get; private set; } = [];
    public bool IsBranchMode => _module?.Update.Mode == "branch";
    public bool IsReleaseMode => !IsBranchMode;
    public bool IsLoadingOptions { get; private set; }
    public bool CanConfigure => !_isLoading && !IsBusy && _activity?.Activity == null && HasSourceLink;
    public bool CanSelectTarget => CanConfigure && !IsLoadingOptions;
    public string ConfigurationMessage => _configurationErrorKey != null
        ? L.Get(_configurationErrorKey, _configurationErrorDetail ?? string.Empty)
        : !HasSourceLink && _module != null ? L.Get(LocalizationKeys.ModuleInfo_NoSource) : string.Empty;
    public bool HasConfigurationMessage => !string.IsNullOrEmpty(ConfigurationMessage);
    public string ModuleStateText => _isLoading ? L.Get(LocalizationKeys.ModuleInfo_Loading)
        : L.Get(ResolveStateKey(_registered, _activity?.IsRunning == true, _activity?.Activity));
    public string LogText { get; private set; } = string.Empty;
    public bool HasLog => !string.IsNullOrWhiteSpace(LogText);
    public string LogSessionKey { get; private set; } = string.Empty;
    public bool IsBusy => _downloadCts != null;
    public bool IsCurrentInstallation => IsBusy && string.Equals(_activeDownloadPath, _module?.SourcePath, StringComparison.OrdinalIgnoreCase);
    public bool ShowInstallButton => !IsCurrentInstallation && _activity?.Activity != ModuleActivity.Installing;
    public bool CanInstall => !_isLoading && !IsBusy && _activity?.Activity == null && _module is { IsSupportedOnCurrentPlatform: true } &&
                               (!_registered || !_module.Status.FirstRunCompleted) &&
                               UpdateManager.CanDownloadModule(_module);
    public double DownloadFraction { get; private set; }
    public string TransferText { get; private set; } = string.Empty;
    public bool HasTransferText => !string.IsNullOrWhiteSpace(TransferText);

    public ModuleInfo(ModuleInstaller installer, UpdateManager updates, EngineInstaller engines,
        ModuleTrustService trust, AppLocalizationService localization, ModuleConsoleStore consoleStore)
    {
        _installer = installer;
        _updates = updates;
        _engines = engines;
        _trust = trust;
        _consoleStore = consoleStore;
        InitializeComponent();
        BindingContext = this;
        LocalizableAttach.Hook(this, localization, this);
        SizeChanged += (_, _) => UpdateDialogSize();
        Loaded += (_, _) =>
        {
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
            RefreshSourceIcon();
            RefreshActivity();
        };
        Unloaded += (_, _) =>
        {
            _consoleStore.StateChanged -= OnConsoleStateChanged;
            _installer.ModulesChanged -= OnModulesChanged;
            ThemeService.PaletteApplied -= OnPaletteApplied;
            if (Application.Current is { } app)
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
            CancelOptionsLoad();
            _presentationCts?.Cancel();
        };
        foreach (var picker in new[] { ChannelPicker, ReleasePicker, BranchPicker })
        {
            picker.Loaded += (_, _) => SyncConfigurationPickers();
            picker.HandlerChanged += (_, _) => Dispatcher.Dispatch(SyncConfigurationPickers);
        }
    }

    public async Task OpenAsync(ModuleConfig module)
    {
        var version = ++_openVersion;
        ++_stateRefreshVersion;
        CancelOptionsLoad();
        _loadedOptionsKey = null;
        _configurationErrorKey = null;
        _isOpen = true;
        if (!string.Equals(_module?.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase))
            SelectSection(Section.Info);
        _module = module;
        _displayVersion = module.Version;
        _iconSource = File.Exists(module.IconFullPath) ? ImageSource.FromFile(module.IconFullPath) : null;
        _isLoading = true;
        _registered = false;
        ResetConfigurationOptions();
        if (!IsBusy) TransferText = string.Empty;
        RefreshContent();
        UpdateDialogSize();
        try
        {
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
                if (IsConfigurationSelected) _ = LoadConfigurationOptionsAsync();
            }
        }
    }

    public void ApplyLocalization()
    {
        ToolTipProperties.SetText(CloseButton, L.Get(LocalizationKeys.ModulesDownloads_Close));
        InstallButton.Text = L.Get(LocalizationKeys.Common_Install_Action);
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

    private void RefreshContent()
    {
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
                     nameof(CanInstall), nameof(ShowInstallButton),
                     nameof(ShowVerifiedBadge), nameof(ShowUnverifiedWarning), nameof(NotVerifiedText),
                     nameof(SourceUrl), nameof(HasSourceLink),
                     nameof(DownloadFraction), nameof(TransferText), nameof(HasTransferText) })
            OnPropertyChanged(property);
        RefreshActivity();
        RefreshConfiguration();
    }

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

    private void OnConsoleStateChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (_isOpen) RefreshActivity();
    });

    private void RefreshActivity()
    {
        _activity = _module == null ? null : _consoleStore.GetMaintenanceSnapshot(_module.SourcePath);
        OnPropertyChanged(nameof(ModuleStateText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(CanConfigure));
        OnPropertyChanged(nameof(CanSelectTarget));
        if (!IsStatusSelected) return;
        var hadLog = HasLog;
        LogSessionKey = _activity?.SessionKey ?? string.Empty;
        LogText = _activity?.Text ?? string.Empty;
        OnPropertyChanged(nameof(LogSessionKey));
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(HasLog));
        if (!hadLog && HasLog) RefreshLogLayout();
    }

    private void OnModulesChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(async () =>
    {
        if (!_isOpen || _module == null) return;
        var selected = _module;
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
            if (IsConfigurationSelected) _ = LoadConfigurationOptionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    });

    private void OnInfoTabClicked(object? sender, EventArgs e) => SelectSection(Section.Info);
    private void OnConfigurationTabClicked(object? sender, EventArgs e) => SelectSection(Section.Configuration);
    private void OnStatusTabClicked(object? sender, EventArgs e) => SelectSection(Section.Status);

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

    private void RefreshLogLayout() => Dispatcher.Dispatch(() =>
    {
        ActivityLogPanel.InvalidateMeasure();
        ActivityLogConsole.InvalidateMeasure();
    });

    private void ResetConfigurationOptions()
    {
        ReleaseOptions = BuildReleaseOptions([], _module?.Update.SelectedReleaseTag);
        BranchOptions = _module == null ? [] : [_module.Update.Branch];
        RefreshConfiguration();
    }

    // Keep an explicitly saved tag even when offline or absent from the current release stream.
    // Installation resolves it through UpdateManager, never silently substitutes another version.
    internal static IReadOnlyList<UpdateCandidate> BuildReleaseOptions(
        IReadOnlyList<UpdateCandidate> releases, string? selectedTag)
    {
        var options = new List<UpdateCandidate>
        {
            new() { IsVirtualLatest = true, DisplayName = ModuleUpdateConfig.LatestReleaseTag }
        };
        options.AddRange(releases.Where(option => !option.IsVirtualLatest && !string.IsNullOrWhiteSpace(option.ReleaseTag))
            .DistinctBy(option => option.ReleaseTag, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(selectedTag) &&
            !string.Equals(selectedTag, ModuleUpdateConfig.LatestReleaseTag, StringComparison.OrdinalIgnoreCase) &&
            !options.Any(option => string.Equals(option.ReleaseTag, selectedTag, StringComparison.OrdinalIgnoreCase)))
            options.Add(new() { DisplayName = selectedTag, ReleaseTag = selectedTag });
        return options;
    }

    private void CancelOptionsLoad()
    {
        _optionsCts?.Cancel();
        _optionsCts = null;
        IsLoadingOptions = false;
    }

    private async Task LoadConfigurationOptionsAsync()
    {
        if (!CanConfigure || !_isOpen || _module == null) return;
        var module = _module;
        var mode = module.Update.Mode;
        var key = $"{module.SourcePath}|{mode}";
        if (_loadedOptionsKey == key || IsLoadingOptions) return;
        using var cts = new CancellationTokenSource();
        _optionsCts = cts;
        IsLoadingOptions = true;
        _configurationErrorKey = null;
        RefreshConfiguration();
        try
        {
            if (mode == "branch")
            {
                var branches = await _updates.GetModuleBranchesAsync(module, cts.Token, isManualRequest: true);
                if (cts.IsCancellationRequested || !ReferenceEquals(_module, module)) return;
                BranchOptions = new[] { module.Update.Branch }.Concat(branches.Select(branch => branch.Name))
                    .Where(branch => !string.IsNullOrWhiteSpace(branch))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            else
            {
                var releases = await _updates.GetModuleReleaseCandidatesAsync(module, cts.Token, isManualRequest: true);
                if (cts.IsCancellationRequested || !ReferenceEquals(_module, module)) return;
                ReleaseOptions = BuildReleaseOptions(releases, module.Update.SelectedReleaseTag);
            }
            _loadedOptionsKey = key;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_module, module)) return;
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
    }

    private void RefreshConfiguration()
    {
        // List replacement may reset a native Picker to -1; this must not become a preference edit.
        _synchronizingPickers = true;
        try
        {
            foreach (var property in new[] { nameof(ReleaseOptions), nameof(BranchOptions), nameof(IsBranchMode),
                         nameof(IsReleaseMode), nameof(CanConfigure), nameof(CanSelectTarget), nameof(IsLoadingOptions),
                         nameof(ConfigurationMessage), nameof(HasConfigurationMessage) })
                OnPropertyChanged(property);
            SyncConfigurationPickers();
        }
        finally { _synchronizingPickers = false; }
    }

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
                        combo.SelectedIndex = picker.SelectedIndex;
                }
                catch (Exception ex)
                {
                    // The deferred/Loaded pass retries when the native item collection finishes updating.
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
#endif
        }
        finally { _synchronizingPickers = wasSynchronizing; }
    }

    private async void OnChannelChanged(object? sender, EventArgs e)
    {
        if (_synchronizingPickers || !CanConfigure || ChannelPicker.SelectedItem is not string mode ||
            _module?.Update.Mode == mode) return;
        CancelOptionsLoad();
        _loadedOptionsKey = null;
        var saved = SaveConfiguration(update =>
        {
            update.Mode = mode;
            update.Channel = mode == "pre-release" ? "pre-release" : "release";
        });
        ResetConfigurationOptions();
        if (saved) await LoadConfigurationOptionsAsync();
    }

    private void OnReleaseChanged(object? sender, EventArgs e)
    {
        if (_synchronizingPickers || !CanSelectTarget || ReleasePicker.SelectedItem is not UpdateCandidate option) return;
        var tag = option.IsVirtualLatest ? ModuleUpdateConfig.LatestReleaseTag : option.ReleaseTag;
        if (string.Equals(_module?.Update.SelectedReleaseTag, tag, StringComparison.OrdinalIgnoreCase)) return;
        SaveConfiguration(update => update.SelectedReleaseTag = tag);
    }

    private void OnBranchChanged(object? sender, EventArgs e)
    {
        if (_synchronizingPickers || !CanSelectTarget || BranchPicker.SelectedItem is not string branch ||
            _module?.Update.Branch == branch) return;
        SaveConfiguration(update => update.Branch = branch);
    }

    private bool SaveConfiguration(Action<ModuleUpdateConfig> change)
    {
        if (_module == null) return false;
        ++_stateRefreshVersion; // An older in-flight manifest reload must not replace this selection.
        var update = _module.Update;
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

    private void AppendActivityError(ModuleConfig module, Exception error) =>
        _consoleStore.CreateMaintenanceLog(module, null, reset: false)
            .Report(L.Get(LocalizationKeys.ModuleInfo_Failed, error.Message));

    private async Task RefreshPresentationAsync()
    {
        _presentationCts?.Cancel();
        if (_module == null || !_isOpen) return;
        var module = _module;
        using var cts = new CancellationTokenSource();
        _presentationCts = cts;
        bool IsCurrent() => !cts.IsCancellationRequested && _isOpen && ReferenceEquals(_module, module);
        async Task LoadIconAsync()
        {
            var artwork = await _updates.GetModuleCatalogArtworkAsync(module, cts.Token);
            if (!IsCurrent() || artwork == null) return;
            _iconSource = artwork.Icon is { } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;
            OnPropertyChanged(nameof(IconSource));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(HasOverview));
        }
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

    internal static string? GetSourceUrl(ModuleConfig module) => UpdateManager.CanDownloadModule(module)
        ? $"https://github.com/{module.Source.Repo.Trim().Trim('/')}" : null;

    private void OnPaletteApplied() => MainThread.BeginInvokeOnMainThread(RefreshSourceIcon);
    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e) => OnPaletteApplied();
    private void RefreshSourceIcon() => SourceLinkButton.Source =
        PackagedIconTintCache.Get("icon_link.png", IconTintHelper.ResolvePaletteColor("LabelPrimary"));

    /// <summary>Only public metadata; command lines, settings, local paths and bridge internals stay private.</summary>
    internal static IReadOnlyList<InfoField> CreateFields(ModuleConfig module,
        IReadOnlyList<ModuleConfig> catalog, IReadOnlyList<EngineConfig> engines)
    {
        var fields = new List<InfoField>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(key, value));
        }
        EngineConfig? FindEngine(string id) => engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase)) ?? module.Engines.FirstOrDefault(engine =>
            string.Equals(engine.Id, id, StringComparison.OrdinalIgnoreCase));
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

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        if (!CanInstall || _module == null) return;
        var selected = _module;
        using var cts = new CancellationTokenSource();
        _downloadCts = cts;
        _activeDownloadPath = selected.SourcePath;
        var resultKey = LocalizationKeys.ModuleInfo_Installed;
        var resultDetail = string.Empty;
        TransferText = string.Empty;
        DownloadFraction = 0;
        CancelOptionsLoad();
        SelectSection(Section.Status);
        RefreshContent();
        var log = new Progress<string>(message => System.Diagnostics.Debug.WriteLine(message));
        var progress = new Progress<DownloadProgress>(value =>
        {
            if (!ReferenceEquals(_downloadCts, cts)) return;
            DownloadFraction = Math.Clamp(value.Fraction, 0, 1);
            TransferText = value.TotalBytes > 0
                ? $"{DownloadFraction:P0} · {DownloadsView.FormatDownloadSize(value.DownloadedBytes)} / {DownloadsView.FormatDownloadSize(value.TotalBytes)}"
                : DownloadsView.FormatDownloadSize(value.DownloadedBytes);
            OnPropertyChanged(nameof(DownloadFraction));
            OnPropertyChanged(nameof(TransferText));
            OnPropertyChanged(nameof(HasTransferText));
        });
        try
        {
            await Task.Run(() => _updates.InstallCatalogModuleAsync(selected, log, progress, cts.Token));
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
            _downloadCts = null;
            TransferText = string.Empty;
            // The user may close this dialog or inspect another module while the download runs.
            var current = _module;
            if (current != null)
            {
                var wasOpen = _isOpen;
                var refreshVersion = _openVersion + 1;
                await OpenAsync(current);
                if (refreshVersion == _openVersion)
                {
                    if (!wasOpen) _isOpen = false;
                }
            }
            RefreshContent();
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e) => _downloadCts?.Cancel();
    private void OnCloseClicked(object? sender, EventArgs e) => RequestClose();
    private void OnDialogTapped(object? sender, EventArgs e) { }
    public void RequestClose()
    {
        _isOpen = false;
        CancelOptionsLoad();
        _presentationCts?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnSourceLinkClicked(object? sender, EventArgs e)
    {
        if (SourceUrl is { } url) await OpenLinkAsync(url);
    }

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

    private void UpdateDialogSize()
    {
        if (Width <= 0 || Height <= 0) return;
        DialogBorder.WidthRequest = Math.Min(Math.Max(0, Width - 24), Math.Clamp(Math.Floor(Width * 0.78), 860, 1200));
        DialogBorder.HeightRequest = Math.Min(Math.Max(0, Height - 24), Math.Clamp(Math.Floor(Height * 0.82), 560, 820));
    }

    public sealed record InfoField(string Key, string Value)
    {
        public bool LiteralTitle { get; init; }
        public string Title => LiteralTitle ? Key : L.Get(Key);
        public IReadOnlyList<InfoValue> Items { get; init; } = [new(Value)];
    }

    public sealed record InfoValue(string Text, string? Description = null);
}
