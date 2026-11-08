// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Presents a catalog module consistently in Downloads and the setup wizard.
    /// </summary>
    public sealed class ModuleCatalogCardViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Installer queried for the install or removal state that the card shows as a maintenance status.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Trust level of the module, which decides between the verified badge and the unverified warning.
        /// </summary>
        private readonly ModuleTrustLevel _trust;

        /// <summary>
        /// Version text behind <see cref="VersionString"/>; starts as the manifest version and later becomes the
        /// repository version.
        /// </summary>
        private string _version;

        /// <summary>
        /// Backing field of <see cref="IsSelected"/>.
        /// </summary>
        private bool _isSelected;

        /// <summary>
        /// Counter that is incremented on every presentation refresh, so results of older refreshes can be discarded.
        /// </summary>
        private int _presentationVersion;


        // Bindable state

        /// <summary>
        /// Raised when a bound property changes so the card refreshes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Catalog module that the card presents.
        /// </summary>
        public ModuleConfig Module { get; }

        /// <summary>
        /// Module artwork: the local icon file, or the repository artwork once loaded; null when there is none.
        /// </summary>
        public ImageSource? IconSource { get; private set; }

        /// <summary>
        /// Module name shown as the card title.
        /// </summary>
        public string Name => Module.Name;

        /// <summary>
        /// Displayed version with a leading "v", or an empty string when the version is unknown.
        /// </summary>
        public string VersionString => string.IsNullOrWhiteSpace(_version) ? string.Empty
            : _version.StartsWith('v') ? _version : $"v{_version}";

        /// <summary>
        /// Whether <see cref="IconSource"/> has an image, so the card shows it instead of a placeholder.
        /// </summary>
        public bool HasIcon => IconSource != null;

        /// <summary>
        /// Whether the module is marked as a beta release.
        /// </summary>
        public bool IsBeta => Module.IsBeta;

        /// <summary>
        /// Whether the module is marked as experimental.
        /// </summary>
        public bool IsExperimental => Module.IsExperimental;

        /// <summary>
        /// Whether the card shows a beta or experimental badge.
        /// </summary>
        public bool HasDevelopmentStatus => IsBeta || IsExperimental;

        /// <summary>
        /// Localized text of the beta badge.
        /// </summary>
        public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);

        /// <summary>
        /// Localized text of the experimental badge.
        /// </summary>
        public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);

        /// <summary>
        /// Whether the card shows the verified badge, which only official modules earn.
        /// </summary>
        public bool ShowVerifiedBadge => _trust == ModuleTrustLevel.Official;

        /// <summary>
        /// Whether the card warns that the module has neither been verified nor community-reviewed.
        /// </summary>
        public bool ShowUnverifiedWarning => _trust == ModuleTrustLevel.Unreviewed;

        /// <summary>
        /// Localized text of the unverified-module warning.
        /// </summary>
        public string NotVerifiedLabel => L.Get(LocalizationKeys.Modules_NotVerified);

        /// <summary>
        /// Whether the installer currently runs an operation for this module, so the card shows its status.
        /// </summary>
        public bool ShowMaintenanceStatus => _installer.GetInstallationState(Module.Id) != null;

        /// <summary>
        /// Whether the running operation for this module is a removal.
        /// </summary>
        public bool IsRemoving => _installer.GetInstallationState(Module.Id)?.Phase == DownloadOperationState.Removing;

        /// <summary>
        /// Localized description of the running operation, including who requested it; empty when none runs.
        /// </summary>
        public string MaintenanceLabel => _installer.GetInstallationState(Module.Id)?.LabelWithRequester ?? string.Empty;

        /// <summary>
        /// Whether the card shows a selection checkbox, as in the setup wizard.
        /// </summary>
        public bool ShowSelection { get; }

        /// <summary>
        /// Whether the user may change the selection: the module must support this platform and not be required.
        /// </summary>
        public bool CanSelect => ShowSelection && Module.IsSupportedOnCurrentPlatform && !ModuleRegistry.IsRequired(Module.Id);

        /// <summary>
        /// Whether the card shows that the module is unavailable on the current platform (selection mode only).
        /// </summary>
        public bool ShowUnsupported => ShowSelection && !Module.IsSupportedOnCurrentPlatform;

        /// <summary>
        /// Localized text that names the current platform as unsupported by the module.
        /// </summary>
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


        // Initialization

        /// <summary>
        /// Uses local artwork immediately and selects only required modules in the setup wizard.
        /// </summary>
        /// <param name="module">Catalog module to present.</param>
        /// <param name="trust">Trust level that decides the verification badge or warning.</param>
        /// <param name="installer">Installer that reports running operations for the module.</param>
        /// <param name="showSelection">Whether the card offers a selection checkbox.</param>
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


        // Presentation refresh

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
            // Drop the result when the refresh was cancelled or superseded while the artwork was loading.
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
            // Same staleness guard as for the artwork; an unchanged version raises no notification.
            var version = await updates.GetModuleCatalogVersionAsync(Module, false, ct);
            if (ct.IsCancellationRequested || revision != _presentationVersion || _version == version) return;
            _version = version;
            OnPropertyChanged(nameof(VersionString));
        }


        // Change notification

        /// <summary>
        /// Notifies bindings without requiring a native UI dispatcher to construct a catalog item.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
