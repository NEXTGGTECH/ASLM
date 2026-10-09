// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Hosts the built-in ASLM module downloads page.
    /// </summary>
    public partial class ModulesDownloadsView : ContentView, ILocalizable
    {
        private ModuleInstaller? _installer;
        private ModuleTrustService? _trust;
        private UpdateManager? _updates;
        private List<ModuleCard> _catalog = [];
        private int _refreshVersion;
        private CancellationTokenSource? _refreshCts;

        public ObservableCollection<ModuleCard> Modules { get; } = [];
        public event EventHandler<ModuleConfig>? ModuleSelected;

        /// <summary>
        /// Creates the built-in modules download surface defined by XAML.
        /// </summary>
        public ModulesDownloadsView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (_installer != null)
                {
                    _installer.ModulesChanged -= OnModulesChanged;
                    _installer.ModulesChanged += OnModulesChanged;
                    _ = RefreshAsync();
                }
            };
            Unloaded += (_, _) =>
            {
                if (_installer != null) _installer.ModulesChanged -= OnModulesChanged;
                _refreshCts?.Cancel();
            };
        }

        internal void Initialize(ModuleInstaller installer, ModuleTrustService trust, AppLocalizationService localization,
            UpdateManager updates)
        {
            _installer = installer;
            _trust = trust;
            _updates = updates;
            LocalizableAttach.Hook(this, localization, this);
        }

        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.Modules_Title);
            SearchEntry.Placeholder = L.Get(LocalizationKeys.ModulesDownloads_Search);
            EmptyLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
            ApplySearch();
        }

        internal async Task RefreshAsync()
        {
            if (_installer == null) return;
            var version = ++_refreshVersion;
            _refreshCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _refreshCts = cts;
            try
            {
                var modules = await Task.Run(_installer.DiscoverAvailableModulesAsync);
                if (version != _refreshVersion || cts.IsCancellationRequested) return;
                foreach (var module in modules) _updates!.ApplyCatalogDefaults(module, installed: false);
                _catalog = modules.Select(module => new ModuleCard(module, _trust!.Resolve(module), _installer)).ToList();
                EmptyLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
                ApplySearch();
                // Each card publishes its icon and version independently, as soon as either is ready.
                await Task.WhenAll(_catalog.Select(card => card.RefreshPresentationAsync(_updates!, cts.Token)));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            catch (Exception)
            {
                if (version != _refreshVersion) return;
                _catalog = [];
                Modules.Clear();
                EmptyLabel.Text = L.Get(LocalizationKeys.ModulesDownloads_LoadFailed);
                EmptyLabel.IsVisible = true;
            }
            finally
            {
                if (ReferenceEquals(_refreshCts, cts)) _refreshCts = null;
            }
        }

        private void OnModulesChanged(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

        private void ApplySearch()
        {
            var query = SearchEntry.Text?.Trim() ?? string.Empty;
            Modules.Clear();
            foreach (var card in _catalog.Where(card =>
                         card.Module.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         card.Module.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                         card.Module.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                card.RefreshLocalization();
                Modules.Add(card);
            }
            EmptyLabel.IsVisible = Modules.Count == 0;
        }

        private void OnModuleTapped(object? sender, TappedEventArgs e)
        {
            if (sender is BindableObject { BindingContext: ModuleCard card })
                ModuleSelected?.Invoke(this, card.Module);
        }

        public sealed class ModuleCard(ModuleConfig module, ModuleTrustLevel trust, ModuleInstaller installer) : BindableObject
        {
            public ModuleConfig Module { get; } = module;
            private readonly ModuleTrustLevel Trust = trust;
            private string _version = module.Version;
            public ImageSource? IconSource { get; private set; } = File.Exists(module.IconFullPath)
                ? ImageSource.FromFile(module.IconFullPath) : null;
            public string Name => Module.Name;
            public string VersionString => string.IsNullOrWhiteSpace(_version) ? string.Empty
                : _version.StartsWith('v') ? _version : $"v{_version}";
            public bool HasIcon => IconSource != null;
            public bool IsBeta => Module.IsBeta;
            public bool IsExperimental => Module.IsExperimental;
            public bool HasDevelopmentStatus => IsBeta || IsExperimental;
            public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);
            public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);
            public bool ShowVerifiedBadge => Trust == ModuleTrustLevel.Official;
            public bool ShowUnverifiedWarning => Trust == ModuleTrustLevel.Unreviewed;
            public string NotVerifiedLabel => L.Get(LocalizationKeys.Modules_NotVerified);
            public bool ShowMaintenanceStatus => installer.GetInstallationState(Module.Id) != null;
            public bool IsRemoving => installer.GetInstallationState(Module.Id)?.Phase == DownloadOperationState.Removing;
            public string MaintenanceLabel => installer.GetInstallationState(Module.Id)?.LabelWithRequester ?? string.Empty;
            internal void RefreshLocalization()
            {
                OnPropertyChanged(nameof(NotVerifiedLabel));
                OnPropertyChanged(nameof(BetaLabel));
                OnPropertyChanged(nameof(ExperimentalLabel));
                OnPropertyChanged(nameof(MaintenanceLabel));
            }

            internal Task RefreshPresentationAsync(UpdateManager updates, CancellationToken ct)
            {
                async Task LoadIconAsync()
                {
                    var artwork = await updates.GetModuleCatalogArtworkAsync(Module, ct);
                    if (ct.IsCancellationRequested || artwork == null) return;
                    IconSource = artwork.Icon is { } bytes ? ImageSource.FromStream(() => new MemoryStream(bytes)) : null;
                    OnPropertyChanged(nameof(IconSource));
                    OnPropertyChanged(nameof(HasIcon));
                }
                async Task LoadVersionAsync()
                {
                    var version = await updates.GetModuleCatalogVersionAsync(Module, false, ct);
                    if (ct.IsCancellationRequested || _version == version) return;
                    _version = version;
                    OnPropertyChanged(nameof(VersionString));
                }
                return Task.WhenAll(LoadIconAsync(), LoadVersionAsync());
            }
        }
    }
}
