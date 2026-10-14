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
        private List<ModuleCatalogCardViewModel> _catalog = [];
        private int _refreshVersion;
        private CancellationTokenSource? _refreshCts;

        public ObservableCollection<ModuleCatalogCardViewModel> Modules { get; } = [];
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

        /// <summary>
        /// Attaches the shared catalog, presentation and localization services.
        /// </summary>
        internal void Initialize(ModuleInstaller installer, ModuleTrustService trust, AppLocalizationService localization,
            UpdateManager updates)
        {
            _installer = installer;
            _trust = trust;
            _updates = updates;
            LocalizableAttach.Hook(this, localization, this);
        }

        /// <summary>
        /// Refreshes the catalog labels and translated card badges.
        /// </summary>
        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.Modules_Title);
            SearchEntry.Placeholder = L.Get(LocalizationKeys.ModulesDownloads_Search);
            EmptyLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
            ApplySearch();
        }

        /// <summary>
        /// Rebuilds the available catalog and publishes each card's presentation independently.
        /// </summary>
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
                _catalog = modules.Select(module => new ModuleCatalogCardViewModel(module, _trust!.Resolve(module), _installer)).ToList();
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

        /// <summary>
        /// Refreshes catalog membership after installation or removal state changes.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());

        /// <summary>
        /// Applies the search text without repeating repository lookups.
        /// </summary>
        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

        /// <summary>
        /// Filters existing cards by module identity, title or description.
        /// </summary>
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

        /// <summary>
        /// Forwards the shared card's navigation action to the catalog host.
        /// </summary>
        private void OnModuleSelected(object? sender, ModuleConfig module) =>
            ModuleSelected?.Invoke(this, module);
    }
}
