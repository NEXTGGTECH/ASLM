// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Net;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Controls.Downloads
{
    /// <summary>
    /// Hosts catalog and explicit GitHub repository downloads of ASLM modules.
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

        /// <summary>
        /// Handles explicit repository searches independently of local catalog filtering.
        /// </summary>
        private ModuleRepositorySearch? _repositorySearch;

        /// <summary>
        /// Identifies the search result represented by the current repository card.
        /// </summary>
        private ModuleConfig? _repositoryResult;

        /// <summary>
        /// Keeps the found card and its artwork while switching between download sources.
        /// </summary>
        private ModuleCatalogCardViewModel? _repositoryCard;

        /// <summary>
        /// Cancels artwork work when the searched repository or visible section changes.
        /// </summary>
        private CancellationTokenSource? _repositoryArtworkCts;

        /// <summary>
        /// Stores whether the explicit GitHub repository source is selected.
        /// </summary>
        private bool _isGitHubSelected;

        /// <summary>
        /// Preserves the catalog filter independently of repository input.
        /// </summary>
        private string _catalogQuery = string.Empty;

        /// <summary>
        /// Prevents restored input from being treated as a user edit during source switching.
        /// </summary>
        private bool _changingSection;


        // Properties and events

        /// <summary>
        /// Cards shown in the list, which is the catalog narrowed by the current search text.
        /// </summary>
        public ObservableCollection<ModuleCatalogCardViewModel> Modules { get; } = [];

        /// <summary>
        /// Gets whether the built-in module catalog is selected.
        /// </summary>
        public bool IsCatalogSelected => !_isGitHubSelected;

        /// <summary>
        /// Gets whether an exact GitHub repository can be searched.
        /// </summary>
        public bool IsGitHubSelected => _isGitHubSelected;

        /// <summary>
        /// Gets whether the explicit search button may submit the current repository text.
        /// </summary>
        public bool CanSearchRepository => _repositorySearch?.CanSearch == true;

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
                CancelRepositorySearch();
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
            _repositorySearch = new ModuleRepositorySearch(updates.FindRepositoryModuleAsync);
            _repositorySearch.Changed += OnRepositorySearchChanged;
            LocalizableAttach.Hook(this, localization, this);
        }


        // Localization

        /// <summary>
        /// Refreshes the catalog labels and translated card badges.
        /// </summary>
        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.Modules_Title);
            CatalogTabButton.Text = L.Get(LocalizationKeys.ModulesDownloads_Catalog);
            GitHubTabButton.Text = L.Get(LocalizationKeys.ModulesDownloads_GitHub);
            RepositorySearchButton.Text = L.Get(LocalizationKeys.ModulesDownloads_SearchRepository);
            SearchEntry.Placeholder = L.Get(IsGitHubSelected
                ? LocalizationKeys.ModulesDownloads_RepositoryPlaceholder : LocalizationKeys.ModulesDownloads_Search);
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

            // Installation changes refresh a found module locally; they never repeat a repository search.
            if (IsGitHubSelected)
            {
                if (_repositorySearch?.Result is { } result)
                {
                    var local = await _installer.DiscoverModulesAsync();
                    var current = local.FirstOrDefault(module => string.Equals(module.Id, result.Id,
                        StringComparison.OrdinalIgnoreCase) && GitHubRepository.Equals(module.Source.Repo, result.Source.Repo));
                    if (current != null)
                        _repositoryCard = new(current, _trust!.Resolve(current), _installer);
                }
                ApplySearch();
                return;
            }

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
        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_changingSection) return;
            if (IsGitHubSelected && _repositorySearch != null)
            {
                _repositoryArtworkCts?.Cancel();
                _repositorySearch.Query = e.NewTextValue ?? string.Empty;
            }
            else _catalogQuery = e.NewTextValue ?? string.Empty;
            ApplySearch();
        }

        /// <summary>
        /// Shows the catalog filter without submitting the repository text.
        /// </summary>
        private void OnCatalogTabClicked(object? sender, EventArgs e) => SelectSource(gitHub: false);

        /// <summary>
        /// Shows the last repository query and result without starting a lookup.
        /// </summary>
        private void OnGitHubTabClicked(object? sender, EventArgs e) => SelectSource(gitHub: true);

        /// <summary>
        /// Restores each source's independent input and stops work belonging to the previous section.
        /// </summary>
        private void SelectSource(bool gitHub)
        {
            if (_isGitHubSelected == gitHub) return;
            CancelRepositorySearch();
            _refreshCts?.Cancel();
            _isGitHubSelected = gitHub;
            _changingSection = true;
            try { SearchEntry.Text = gitHub ? _repositorySearch?.Query : _catalogQuery; }
            finally { _changingSection = false; }
            OnPropertyChanged(nameof(IsCatalogSelected));
            OnPropertyChanged(nameof(IsGitHubSelected));
            ApplyLocalization();
            if (!gitHub) _ = RefreshAsync();
        }

        /// <summary>
        /// Publishes search progress and results without triggering another request.
        /// </summary>
        private void OnRepositorySearchChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(CanSearchRepository));
            if (IsGitHubSelected) ApplySearch();
        }

        /// <summary>
        /// Starts the only repository lookup entry point and loads artwork after publishing the result.
        /// </summary>
        private async void OnRepositorySearchClicked(object? sender, EventArgs e)
        {
            if (!IsGitHubSelected || _repositorySearch == null) return;
            var result = await _repositorySearch.SearchAsync();
            if (!IsGitHubSelected || _repositoryCard == null || result == null ||
                !ReferenceEquals(result, _repositorySearch.Result)) return;

            // Artwork reuses the manifest from discovery and does not request release or branch catalogs.
            _repositoryArtworkCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _repositoryArtworkCts = cts;
            try { await _repositoryCard.RefreshArtworkAsync(_updates!, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The query or selected source changed while the optional artwork was loading.
            }
            finally
            {
                if (ReferenceEquals(_repositoryArtworkCts, cts)) _repositoryArtworkCts = null;
            }
        }

        /// <summary>
        /// Stops searches when the downloads surface closes or switches to another category.
        /// </summary>
        internal void CancelRepositorySearch()
        {
            _repositorySearch?.Cancel();
            _repositoryArtworkCts?.Cancel();
        }

        /// <summary>
        /// Filters existing cards by module identity, title or description.
        /// </summary>
        private void ApplySearch()
        {
            // Rebuild the visible list from the cached catalog; an empty query matches every card.
            var query = SearchEntry.Text?.Trim() ?? string.Empty;
            Modules.Clear();
            if (IsGitHubSelected)
            {
                if (_repositorySearch?.Result is { } result)
                {
                    if (!ReferenceEquals(_repositoryResult, result))
                    {
                        _repositoryResult = result;
                        _repositoryCard = new(result, _trust!.Resolve(result), _installer!);
                    }
                    _repositoryCard!.RefreshLocalization();
                    Modules.Add(_repositoryCard);
                }
                EmptyLabel.Text = _repositorySearch?.IsSearching == true
                    ? L.Get(LocalizationKeys.ModulesDownloads_Searching)
                    : _repositorySearch?.Error is { } error ? FormatRepositoryError(error)
                    : L.Get(LocalizationKeys.ModulesDownloads_RepositoryHint);
                EmptyLabel.IsVisible = Modules.Count == 0;
                return;
            }

            EmptyLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
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
        /// Explains account, quota and transport failures without confusing them with invalid manifests.
        /// </summary>
        private static string FormatRepositoryError(Exception error) => error switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                L.Get(LocalizationKeys.ModulesDownloads_AuthenticationFailed),
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                L.Get(LocalizationKeys.ModulesDownloads_AccessDenied),
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                L.Get(LocalizationKeys.ModulesDownloads_RateLimited),
            ArgumentException or InvalidDataException or InvalidOperationException => error.Message,
            _ => L.Get(LocalizationKeys.ModulesDownloads_SearchFailed, error.Message)
        };

        /// <summary>
        /// Forwards the shared card's navigation action to the catalog host.
        /// </summary>
        private void OnModuleSelected(object? sender, ModuleConfig module) =>
            ModuleSelected?.Invoke(this, module);
    }
}
