// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Internal;

/// <summary>
/// Presents a catalog module consistently in Downloads and the setup wizard.
/// </summary>
public sealed class ModuleCatalogCardViewModel : INotifyPropertyChanged
{
    private readonly ModuleInstaller _installer;
    private readonly ModuleTrustLevel _trust;
    private string _version;
    private bool _isSelected;
    private int _presentationVersion;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ModuleConfig Module { get; }
    public ImageSource? IconSource { get; private set; }
    public string Name => Module.Name;
    public string VersionString => string.IsNullOrWhiteSpace(_version) ? string.Empty
        : _version.StartsWith('v') ? _version : $"v{_version}";
    public bool HasIcon => IconSource != null;
    public bool IsBeta => Module.IsBeta;
    public bool IsExperimental => Module.IsExperimental;
    public bool HasDevelopmentStatus => IsBeta || IsExperimental;
    public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);
    public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);
    public bool ShowVerifiedBadge => _trust == ModuleTrustLevel.Official;
    public bool ShowUnverifiedWarning => _trust == ModuleTrustLevel.Unreviewed;
    public string NotVerifiedLabel => L.Get(LocalizationKeys.Modules_NotVerified);
    public bool ShowMaintenanceStatus => _installer.GetInstallationState(Module.Id) != null;
    public bool IsRemoving => _installer.GetInstallationState(Module.Id)?.Phase == DownloadOperationState.Removing;
    public string MaintenanceLabel => _installer.GetInstallationState(Module.Id)?.LabelWithRequester ?? string.Empty;
    public bool ShowSelection { get; }
    public bool CanSelect => ShowSelection && Module.IsSupportedOnCurrentPlatform && !ModuleRegistry.IsRequired(Module.Id);
    public bool ShowUnsupported => ShowSelection && !Module.IsSupportedOnCurrentPlatform;
    public string UnsupportedLabel => L.Get(LocalizationKeys.SetupWizard_ModuleUnsupported, PlatformInfo.PlatformKey);

    /// <summary>
    /// Keeps mandatory choices locked even when a native checkbox attempts to change the binding.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !CanSelect) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Uses local artwork immediately and selects only required modules in the setup wizard.
    /// </summary>
    public ModuleCatalogCardViewModel(ModuleConfig module, ModuleTrustLevel trust, ModuleInstaller installer,
        bool showSelection = false)
    {
        Module = module;
        _trust = trust;
        _installer = installer;
        _version = module.Version;
        ShowSelection = showSelection;
        _isSelected = showSelection && ModuleRegistry.IsRequired(module.Id);
        IconSource = File.Exists(module.IconFullPath) ? ImageSource.FromFile(module.IconFullPath) : null;
    }

    /// <summary>
    /// Refreshes translated badges and statuses without rebuilding the card or losing its selection.
    /// </summary>
    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(NotVerifiedLabel));
        OnPropertyChanged(nameof(BetaLabel));
        OnPropertyChanged(nameof(ExperimentalLabel));
        OnPropertyChanged(nameof(MaintenanceLabel));
        OnPropertyChanged(nameof(UnsupportedLabel));
    }

    /// <summary>
    /// Publishes cached artwork and repository version independently as each becomes available.
    /// </summary>
    internal Task RefreshPresentationAsync(UpdateManager updates, CancellationToken ct)
    {
        // A later configuration edit owns the card; discard older metadata responses.
        var revision = ++_presentationVersion;
        return Task.WhenAll(LoadIconAsync(updates, revision, ct), LoadVersionAsync(updates, revision, ct));
    }

    /// <summary>
    /// Loads repository artwork through the shared local cache without waiting for release metadata.
    /// </summary>
    private async Task LoadIconAsync(UpdateManager updates, int revision, CancellationToken ct)
    {
        var artwork = await updates.GetModuleCatalogArtworkAsync(Module, ct);
        if (ct.IsCancellationRequested || revision != _presentationVersion || artwork == null) return;
        IconSource = artwork.Icon is { } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;
        OnPropertyChanged(nameof(IconSource));
        OnPropertyChanged(nameof(HasIcon));
    }

    /// <summary>
    /// Resolves the displayed version for the saved channel and target.
    /// </summary>
    private async Task LoadVersionAsync(UpdateManager updates, int revision, CancellationToken ct)
    {
        var version = await updates.GetModuleCatalogVersionAsync(Module, false, ct);
        if (ct.IsCancellationRequested || revision != _presentationVersion || _version == version) return;
        _version = version;
        OnPropertyChanged(nameof(VersionString));
    }

    /// <summary>
    /// Notifies bindings without requiring a native UI dispatcher to construct a catalog item.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
