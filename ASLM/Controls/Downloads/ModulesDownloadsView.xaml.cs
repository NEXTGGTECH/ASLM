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
        // Fields

        /// <summary>
        /// Installer that discovers available modules and reports installation changes, or null before
        /// <see cref="Initialize"/>.
        /// </summary>
        private ModuleInstaller? _installer;

        /// <summary>
        /// Trust service that resolves the trust state shown on each card, or null before <see cref="Initialize"/>.
        /// </summary>
        private ModuleTrustService? _trust;

        /// <summary>
        /// Update manager that supplies catalog defaults and card version data, or null before
        /// <see cref="Initialize"/>.
        /// </summary>
        private UpdateManager? _updates;

        /// <summary>
        /// All available module cards of the last refresh, before the search filter is applied.
        /// </summary>
        private List<ModuleCatalogCardViewModel> _catalog = [];

        /// <summary>
        /// Sequence number of the latest refresh; results of older refreshes are discarded.
        /// </summary>
        private int _refreshVersion;

        /// <summary>
        /// Cancellation source of the refresh that is currently running, or null when idle.
        /// </summary>
        private CancellationTokenSource? _refreshCts;


        // Properties and events

        /// <summary>
        /// Cards shown in the list, which is the catalog narrowed by the current search text.
        /// </summary>
        public ObservableCollection<ModuleCatalogCardViewModel> Modules { get; } = [];

        /// <summary>
        /// Raised when the user selects a module card to open its details.
        /// </summary>
        public event EventHandler<ModuleConfig>? ModuleSelected;


        // Initialization

        /// <summary>
        /// Creates the built-in modules download surface defined by XAML.
        /// </summary>
        public ModulesDownloadsView()
        {
            InitializeComponent();

            // The catalog follows installation changes only while the view is on screen.
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
        /// <param name="installer">Source of the available modules and of installation change notifications.</param>
        /// <param name="trust">Resolves the trust information shown on each card.</param>
        /// <param name="localization">Localization service that refreshes this view on language changes.</param>
        /// <param name="updates">Provides catalog defaults and version data for the cards.</param>
        internal void Initialize(ModuleInstaller installer, ModuleTrustService trust, AppLocalizationService localization,
            UpdateManager updates)
        {
            _installer = installer;
            _trust = trust;
            _updates = updates;
            LocalizableAttach.Hook(this, localization, this);
        }


        // Localization

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


        // Catalog loading

        /// <summary>
        /// Rebuilds the available catalog and publishes each card's presentation independently.
        /// </summary>
        internal async Task RefreshAsync()
        {
            if (_installer == null) return;

            // A newer refresh supersedes the running one: cancel it and let its results be discarded.
            var version = ++_refreshVersion;
            _refreshCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _refreshCts = cts;
            try
            {
                // Discover off the UI thread, then build one card per available module.
                var modules = await Task.Run(_installer.DiscoverAvailableModulesAsync);
                if (version != _refreshVersion || cts.IsCancellationRequested) return;
                foreach (var module in modules) _updates!.ApplyCatalogDefaults(module, installed: false);
                _catalog = modules.Select(module => new ModuleCatalogCardViewModel(module, _trust!.Resolve(module), _installer)).ToList();
                EmptyLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
                ApplySearch();

                // Each card publishes its icon and version independently, as soon as either is ready.
                await Task.WhenAll(_catalog.Select(card => card.RefreshPresentationAsync(_updates!, cts.Token)));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // A newer refresh or unloading cancelled this one on purpose, so there is nothing to report.
            }
            catch (Exception)
            {
                // A superseded refresh must not clobber the list; otherwise show an empty catalog with the
                // failure text.
                if (version != _refreshVersion) return;
                _catalog = [];
                Modules.Clear();
                EmptyLabel.Text = L.Get(LocalizationKeys.ModulesDownloads_LoadFailed);
                EmptyLabel.IsVisible = true;
            }
            finally
            {
                // Only the latest refresh owns the shared cancellation source.
                if (ReferenceEquals(_refreshCts, cts)) _refreshCts = null;
            }
        }

        /// <summary>
        /// Refreshes catalog membership after installation or removal state changes.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());


        // Search and selection

        /// <summary>
        /// Applies the search text without repeating repository lookups.
        /// </summary>
        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplySearch();

        /// <summary>
        /// Filters existing cards by module identity, title or description.
        /// </summary>
        private void ApplySearch()
        {
            // Rebuild the visible list from the cached catalog; an empty query matches every card.
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
