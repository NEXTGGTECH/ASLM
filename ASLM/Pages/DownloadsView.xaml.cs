// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ASLM.Controls.Downloads;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Drives the shared download catalog dialog overlay.
    /// </summary>
    public partial class DownloadsView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        private const double DialogWidthFactor = 0.88;
        private const double DialogHeightFactor = 0.84;
        private const double MinDialogWidth = 1080;
        private const double MinDialogHeight = 620;
        private const double MaxDialogWidth = 1520;
        private const double MaxDialogHeight = 920;

        private readonly DownloadCatalog _catalog;
        private readonly DownloadInstaller _installer;
        private readonly AppLocalizationService _localization;
        private CancellationTokenSource? _catalogLoadCts;
        private CancellationTokenSource? _catalogRefreshCts;
        private CancellationTokenSource? _detailRefreshCts;
        private CancellationTokenSource? _searchDebounceCts;

        private bool _hasLoaded;
        private bool _isInternalModulesSelected = true;
        private bool _isBusy;
        private bool _isInstalling;
        private string _searchText = string.Empty;
        private readonly Dictionary<string, DownloadCategoryState> _categoryStates = new(StringComparer.OrdinalIgnoreCase);
        private DownloadCategoryState _activeCategoryState = new();
        private bool _restoringCategory;
        private HashSet<string> SelectedFilterKeys => _activeCategoryState.FilterKeys;
        private string _lastDetailSignature = string.Empty;
        private List<DownloadCatalogItem> _categoryItems = [];
        private DownloadCategoryViewModel? _activeCategory;
        private DownloadListItemViewModel? _selectedItem;
        private DownloadCatalogItemDetail? _selectedItemDetail;
        private DownloadVariantViewModel? _selectedVariant;
        private bool _isVariantSelectorOpen;
        private DownloadInfoBlockViewModel? _selectedInfoBlock;
        private WebViewSource? _selectedInfoBlockSource;
        private double _selectedInfoBlockWebHeight = 1;
        private bool _isDownloadsOpen;
        private bool _themeHandlersAttached;
#if WINDOWS
        private DownloadInfoPreviewHost? _infoBlockPreview;
#endif

        private Entry SearchEntry => BridgeContent.SearchInput;
        private Label ItemListEmptyTitleLabel => BridgeContent.ItemListEmptyTitle;
        private Label DetailEmptyTitleLabel => BridgeContent.DetailEmptyTitle;
        private Label VariantSectionLabel => BridgeContent.VariantTitle;
        private Button InstallButton => BridgeContent.InstallAction;
        private ImageButton OpenButton => BridgeContent.OpenAction;
        private Button RemoveButton => BridgeContent.RemoveAction;


        // Initialization

        /// <summary>
        /// Builds the download overlay and wires its core events.
        /// </summary>
        public DownloadsView(DownloadCatalog catalog, DownloadInstaller installer, AppLocalizationService localization)
        {
            _catalog = catalog;
            _installer = installer;
            _localization = localization;

            InitializeComponent();
            InstallCommand = new Command(async () => await InstallSelectedVariantAsync());
            OpenVariantCommand = new Command(async () => await OpenSelectedVariantAsync());
            BindingContext = this;

            BridgeContent.SearchTextChanged += OnSearchTextChanged;
            BridgeContent.InfoBlockWebViewHandlerChanged += OnInfoBlockWebViewHandlerChanged;
            BridgeContent.InfoBlockWebViewNavigating += OnInfoBlockWebViewNavigating;
            BridgeContent.InfoBlockWebViewNavigated += OnInfoBlockWebViewNavigated;
            BridgeContent.InfoBlockBrowser.Loaded += OnInfoBlockWebViewLoaded;
            BridgeContent.InfoBlockBrowser.Unloaded += OnInfoBlockWebViewUnloaded;
            BridgeContent.InstallRequested += OnInstallClicked;
            BridgeContent.OpenRequested += OnOpenClicked;
            BridgeContent.RemoveRequested += OnDeleteClicked;
            BridgeContent.VariantSelectorToggleRequested += OnVariantSelectorToggleRequested;

            LocalizableAttach.Hook(this, _localization, this);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (_, _) => UpdateDialogSize();
        }


        // Localization

        /// <summary>
        /// Applies localized strings to the download overlay controls.
        /// </summary>
        /// <inheritdoc />
        public void ApplyLocalization()
        {
            DownloadsTitleLabel.Text = L.Get(LocalizationKeys.Downloads_Title);
            InternalModulesLabel.Text = L.Get(LocalizationKeys.AppShell_Nav_Modules);
            ModulesSectionLabel.Text = L.Get(LocalizationKeys.Settings_Header_Modules);
            SearchEntry.Placeholder = L.Get(LocalizationKeys.Downloads_SearchPlaceholder);
            ItemListEmptyTitleLabel.Text = L.Get(LocalizationKeys.Downloads_NoItems);
            OnPropertyChanged(nameof(ActiveCategoryTitle));
            DetailEmptyTitleLabel.Text = L.Get(LocalizationKeys.Downloads_SelectItem);
            InstallButton.Text = L.Get(LocalizationKeys.Common_Download);
            RemoveButton.Text = L.Get(LocalizationKeys.Common_Remove);
            VariantSectionLabel.Text = L.Get(LocalizationKeys.Downloads_SelectVariant);
            BridgeContent.ItemDetailsTitle.Text = L.Get(LocalizationKeys.Downloads_DetailsLabel);
            BridgeContent.ItemFeaturesTitle.Text = L.Get(LocalizationKeys.Downloads_FeaturesLabel);
            SemanticProperties.SetDescription(OpenButton, L.Get(LocalizationKeys.Downloads_OpenLink));

            RefreshLocalizedBindableText();
        }

        public event EventHandler? CloseRequested;
        public new event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<DownloadCategoryViewModel> Categories { get; } = new();
        public ObservableCollection<DownloadFilterViewModel> Filters { get; } = new();
        public ObservableCollection<DownloadListItemViewModel> CurrentItems { get; } = new();
        public ObservableCollection<DownloadVariantViewModel> Variants { get; } = new();
        public ObservableCollection<DownloadInfoBlockViewModel> InfoBlocks { get; } = new();

        public ICommand InstallCommand { get; }
        public ICommand OpenVariantCommand { get; }

        public bool IsInternalModulesSelected
        {
            get => _isInternalModulesSelected;
            private set
            {
                if (_isInternalModulesSelected == value) return;
                _isInternalModulesSelected = value;
                UpdateInfoBlockPreviewSource();
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsModuleCategorySelected));
            }
        }

        public bool IsModuleCategorySelected => !IsInternalModulesSelected && _activeCategory != null;
        public bool HasModuleCategories => Categories.Count > 0;

        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (_isBusy == value) return;
                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public bool IsInstalling
        {
            get => _isInstalling;
            private set
            {
                if (_isInstalling == value) return;
                _isInstalling = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowInstallButton));
                OnPropertyChanged(nameof(ShowDeleteButton));
            }
        }

        public string ActiveCategoryTitle => _activeCategory?.Title ?? L.Get(LocalizationKeys.Downloads_CatalogColumnTitle);
        public string ActiveCategoryDescription => _activeCategory?.Description ?? string.Empty;
        public bool HasActiveCategoryDescription => !string.IsNullOrWhiteSpace(ActiveCategoryDescription);
        public bool HasFilters => Filters.Count > 0;
        public bool HasCurrentItems => CurrentItems.Count > 0;
        public bool IsItemListEmptyVisible => Categories.Count > 0 && !HasCurrentItems && !IsBusy;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (string.Equals(_searchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = value ?? string.Empty;
                if (!_restoringCategory)
                {
                    _activeCategoryState.SearchText = _searchText;
                    _activeCategoryState.NeedsRefresh = true;
                    _activeCategoryState.QueryVersion++;
                }
                OnPropertyChanged();
            }
        }

        public string DetailHeaderTitle => HasSelectedItem
            ? SelectedItemTitle
            : L.Get(LocalizationKeys.Downloads_SelectItem);
        public bool HasSelectedItem => _selectedItem != null;
        public bool IsDetailEmptyVisible => !HasSelectedItem && !IsBusy;

        public string SelectedItemTitle => _selectedItemDetail?.Title ?? _selectedItem?.Title ?? string.Empty;
        public IReadOnlyList<DownloadFieldViewModel> SelectedItemDetails { get; private set; } = [];
        public bool HasSelectedItemDetails => SelectedItemDetails.Count > 0;
        public string SelectedItemSummary => _selectedItemDetail?.Summary ?? _selectedItem?.Summary ?? string.Empty;
        public bool HasSelectedItemSummary => !string.IsNullOrWhiteSpace(SelectedItemSummary);
        public IReadOnlyList<DownloadFieldViewModel> SelectedItemTags { get; private set; } = [];
        public bool HasSelectedItemTags => SelectedItemTags.Count > 0;
        public bool HasVariants => Variants.Count > 0;
        public bool HasSelectedVariant => _selectedVariant != null;
        public IReadOnlyList<DownloadVariantViewModel> SelectedVariantCard =>
            _selectedVariant == null ? [] : [_selectedVariant];
        public bool IsVariantSelectorOpen
        {
            get => _isVariantSelectorOpen;
            private set
            {
                if (_isVariantSelectorOpen == value) return;
                _isVariantSelectorOpen = value;
                OnPropertyChanged();
            }
        }
        public string SelectedVariantTitle => _selectedVariant?.Title ?? string.Empty;
        public string SelectedVariantSummary => _selectedVariant?.Summary ?? string.Empty;
        public bool HasSelectedVariantSummary => !string.IsNullOrWhiteSpace(SelectedVariantSummary);
        public bool ShowInstallButton => _selectedVariant != null && !_selectedVariant.Variant.Installed && !IsInstalling;
        public bool ShowDeleteButton => _selectedVariant?.Variant.Installed == true && !IsInstalling;
        public bool ShowOpenVariantButton => !string.IsNullOrWhiteSpace(GetSelectedVariantHomepageUrl());
        public bool HasInfoBlocks => InfoBlocks.Count > 0;
        public bool HasMultipleInfoBlocks => InfoBlocks.Count > 1;
        public bool HasSelectedInfoBlock => _selectedInfoBlock != null;
        public string SelectedInfoBlockTitle => _selectedInfoBlock?.Title ?? L.Get(LocalizationKeys.Downloads_Details);
        public string SelectedInfoBlockText => _selectedInfoBlock?.RenderedContent ?? string.Empty;
        public bool HasSelectedInfoBlockText => !string.IsNullOrWhiteSpace(SelectedInfoBlockText);
        public bool HasSelectedInfoBlockWebContent => SelectedInfoBlockSource != null;
        public WebViewSource? SelectedInfoBlockSource
        {
            get => _selectedInfoBlockSource;
            private set
            {
                if (ReferenceEquals(_selectedInfoBlockSource, value)) return;
                _selectedInfoBlockSource = value;
                OnPropertyChanged();
            }
        }

        public double SelectedInfoBlockWebHeight
        {
            get => _selectedInfoBlockWebHeight;
            private set
            {
                if (!DownloadInfoPreview.IsValidHeight(value)) return;
                var clamped = Math.Ceiling(value);
                if (Math.Abs(_selectedInfoBlockWebHeight - clamped) < 0.5)
                {
                    return;
                }

                _selectedInfoBlockWebHeight = clamped;
                OnPropertyChanged();
            }
        }

        // Property notifications

        /// <summary>
        /// Raises the local property change event.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // Overlay opening

        /// <summary>
        /// Opens the dialog and refreshes the available downloads automatically.
        /// </summary>
        public Task OpenAsync()
        {
            _isDownloadsOpen = true;
            RefreshThemeIcons();
            UpdateInfoBlockPreviewSource();
            UpdateDialogSize();
            return LoadCatalogAsync(
                preferCached: true,
                forceRefresh: true,
                preserveSelection: true,
                showBusyIndicator: true,
                silentRefresh: false);
        }


        // Initial layout setup

        /// <summary>
        /// Initializes the dialog size once.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            AttachThemeHandlers();
            if (_hasLoaded) return;
            _hasLoaded = true;
            UpdateDialogSize();
        }

        private void OnUnloaded(object? sender, EventArgs e)
        {
            DetachThemeHandlers();
        }

        private void AttachThemeHandlers()
        {
            if (_themeHandlersAttached)
            {
                RefreshThemeIcons();
                return;
            }

            _themeHandlersAttached = true;
            ThemeService.PaletteApplied += OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged += OnRequestedThemeChanged;
            }

            RefreshThemeIcons();
        }

        private void DetachThemeHandlers()
        {
            if (!_themeHandlersAttached) return;

            _themeHandlersAttached = false;
            ThemeService.PaletteApplied -= OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
            }
        }

        private void OnPaletteApplied()
        {
            MainThread.BeginInvokeOnMainThread(RefreshThemeIcons);
        }

        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshThemeIcons);
        }

        private void RefreshThemeIcons()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            OpenButton.Source = PackagedIconTintCache.Get("icon_link.png", iconTint);
            foreach (var field in SelectedItemDetails.Concat(SelectedItemTags)
                .Concat(CurrentItems.SelectMany(item => item.CatalogTags)))
                field.RefreshIcon();
        }


        // Catalog refresh

        /// <summary>
        /// Refreshes the current catalog query and preserves selection when possible.
        /// </summary>
        private async Task LoadCatalogAsync(
            bool preferCached,
            bool forceRefresh,
            bool preserveSelection,
            bool showBusyIndicator,
            bool silentRefresh,
            string? categoryGroupKey = null)
        {
            // A category query must not cancel discovery of the other modules.
            var isCatalogLoad = categoryGroupKey == null;
            if (isCatalogLoad)
                _catalogRefreshCts?.Cancel();
            else
                _activeCategoryState.QueryVersion++;
            var catalogCts = isCatalogLoad
                ? ReplaceCancellationTokenSource(ref _catalogLoadCts)
                : ReplaceCancellationTokenSource(ref _catalogRefreshCts);
            var ct = catalogCts.Token;

            if (showBusyIndicator)
            {
                IsBusy = true;
            }

            try
            {
                // Copy each category's state before background work; no query is shared across pages.
                var queries = _categoryStates.ToDictionary(
                    pair => pair.Key,
                    pair => new DownloadCatalogQuery(pair.Value.SearchText, pair.Value.FilterKeys.ToArray()),
                    StringComparer.OrdinalIgnoreCase);
                var queryVersions = _categoryStates.ToDictionary(
                    pair => pair.Key, pair => pair.Value.QueryVersion, StringComparer.OrdinalIgnoreCase);
                var appliedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                bool IsCurrentQuery(string key) => !_categoryStates.TryGetValue(key, out var state) ||
                    state.QueryVersion == queryVersions.GetValueOrDefault(key);

                void ApplyResult(IReadOnlyList<DownloadCatalogCategory> categories, HashSet<string>? retainedKeys = null)
                {
                    if (ct.IsCancellationRequested || !_isDownloadsOpen) return;
                    var currentCategories = categories.Where(category => IsCurrentQuery(category.GroupKey)).ToList();
                    foreach (var category in currentCategories)
                    {
                        var state = GetCategoryState(category);
                        state.NeedsRefresh = false;
                        // A shared category's other providers may still contribute filters.
                        if (retainedKeys != null)
                            state.FilterKeys.RemoveWhere(key => !category.Filters.Any(filter =>
                                string.Equals(filter.Key, key, StringComparison.OrdinalIgnoreCase)));
                    }

                    var firstActiveUpdate = currentCategories.Any(category =>
                        string.Equals(category.GroupKey, _activeCategory?.Category.GroupKey, StringComparison.OrdinalIgnoreCase) &&
                        !appliedCategories.Contains(category.GroupKey));
                    var previousVariantKey = preserveSelection ? _selectedVariant?.Variant.ResourceKey : null;
                    var activeChanged = ApplyCategories(currentCategories, retainedKeys, preserveSelection);
                    appliedCategories.UnionWith(currentCategories.Select(category => category.GroupKey));

                    if ((activeChanged || firstActiveUpdate) && _selectedItem != null && !IsInternalModulesSelected)
                        _ = LoadSelectedItemDetailAsync(preferCached, forceRefresh, previousVariantKey, silentRefresh);
                }

                var snapshot = await Task.Run(
                    () => _catalog.LoadCatalogAsync(
                        categoryQueries: queries,
                        categoryGroupKey: categoryGroupKey,
                        preferCached: preferCached,
                        forceRefresh: forceRefresh,
                        ct: ct,
                        categoryLoaded: isCatalogLoad
                            ? category => MainThread.InvokeOnMainThreadAsync(() => ApplyResult([category]))
                            : null),
                    ct);
                if (ct.IsCancellationRequested) return;

                // Remove vanished categories only after discovery finishes, preserving newer user queries.
                var retainedKeys = snapshot.Categories.Select(category => category.GroupKey)
                    .Concat(Categories.Where(category => !IsCurrentQuery(category.Category.GroupKey) ||
                        (!isCatalogLoad && !string.Equals(category.Category.GroupKey, categoryGroupKey, StringComparison.OrdinalIgnoreCase)))
                        .Select(category => category.Category.GroupKey))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                ApplyResult(snapshot.Categories, retainedKeys);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested &&
                    !silentRefresh)
                {
                    Debug.WriteLine($"Failed to refresh download catalog: {ex}");
                }
            }
            finally
            {
                if (isCatalogLoad && ReferenceEquals(_catalogLoadCts, catalogCts))
                {
                    IsBusy = false;
                    RaiseLayoutProperties();
                }
            }
        }


        // Catalog snapshot

        /// <summary>
        /// Updates only changed categories, keeping sidebar instances and the current page intact.
        /// </summary>
        private bool ApplyCategories(IReadOnlyList<DownloadCatalogCategory> categories, HashSet<string>? retainedKeys, bool preserveSelection)
        {
            SaveCategorySelection();
            var activeChanged = false;
            foreach (var category in categories)
            {
                var existing = Categories.FirstOrDefault(viewModel =>
                    string.Equals(viewModel.Category.GroupKey, category.GroupKey, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    Categories.Add(new DownloadCategoryViewModel(category, SelectCategoryAsync));
                else if (existing.Update(category) && ReferenceEquals(existing, _activeCategory))
                    activeChanged = true;
            }

            if (retainedKeys != null)
                for (var index = Categories.Count - 1; index >= 0; index--)
                    if (!retainedKeys.Contains(Categories[index].Category.GroupKey))
                        Categories.RemoveAt(index);

            var ordered = Categories.OrderBy(category => category.Category.SortOrder)
                .ThenBy(category => category.Title, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < ordered.Count; index++)
                if (!ReferenceEquals(Categories[index], ordered[index]))
                    Categories.Move(Categories.IndexOf(ordered[index]), index);

            var targetCategory = _activeCategory != null && Categories.Contains(_activeCategory)
                ? _activeCategory : Categories.FirstOrDefault();
            activeChanged |= !ReferenceEquals(targetCategory, _activeCategory);
            if (activeChanged)
                ActivateCategory(targetCategory, preserveSelection ? _selectedItem?.Item.ResourceKey : null);

            OnPropertyChanged(nameof(HasModuleCategories));
            OnPropertyChanged(nameof(IsModuleCategorySelected));
            RaiseLayoutProperties();
            return activeChanged;
        }

        /// <summary>
        /// Swaps the active category and rebuilds the list panel.
        /// </summary>
        private void ActivateCategory(DownloadCategoryViewModel? category, string? selectedItemKey)
        {
            if (!string.Equals(_activeCategory?.Category.GroupKey, category?.Category.GroupKey, StringComparison.OrdinalIgnoreCase))
                ClearDetail();
            _activeCategory = category;
            _activeCategoryState = category == null ? new() : GetCategoryState(category.Category);
            _restoringCategory = true;
            try { SearchText = _activeCategoryState.SearchText; }
            finally { _restoringCategory = false; }
            foreach (var viewModel in Categories)
            {
                viewModel.IsSelected = !IsInternalModulesSelected && ReferenceEquals(viewModel, category);
            }

            _categoryItems = category?.Category.Items.ToList() ?? [];
            BuildAvailableFilters();
            ApplyCurrentItemFilters(selectedItemKey ?? _activeCategoryState.SelectedItemKey);
            if (_activeCategoryState.Detail is { } detail &&
                !ReferenceEquals(_selectedItemDetail, detail) &&
                string.Equals(detail.ResourceKey, _selectedItem?.Item.ResourceKey, StringComparison.OrdinalIgnoreCase))
            {
                ApplyDetail(detail, _activeCategoryState.SelectedVariantKey);
                _lastDetailSignature = ComputeDetailSignature(detail);
            }
            RaiseCategoryProperties();
        }

        private DownloadCategoryState GetCategoryState(DownloadCatalogCategory category)
        {
            if (!_categoryStates.TryGetValue(category.GroupKey, out var state))
            {
                state = new DownloadCategoryState();
                _categoryStates.Add(category.GroupKey, state);
            }
            if (state.QueryVersion == 0)
                state.FilterKeys.UnionWith(category.Filters.Where(filter => filter.Selected).Select(filter => filter.Key));
            return state;
        }

        private void SaveCategorySelection()
        {
            _activeCategoryState.SelectedItemKey = _selectedItem?.Item.ResourceKey;
            _activeCategoryState.SelectedVariantKey = _selectedVariant?.Variant.ResourceKey;
            _activeCategoryState.Detail = _selectedItemDetail;
        }

        /// <summary>
        /// Rehydrates filter state from the active category payload.
        /// </summary>
        private void BuildAvailableFilters()
        {
            var availableFilters = _activeCategory?.Category.Filters
                .Where(static filter => !string.IsNullOrWhiteSpace(filter.Key))
                .OrderBy(filter => filter.SortOrder)
                .ThenBy(filter => filter.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];

            Filters.Clear();
            foreach (var filter in availableFilters)
            {
                Filters.Add(new DownloadFilterViewModel(filter.Key, filter.Title, filter.Kind, ToggleFilter)
                {
                    IsSelected = SelectedFilterKeys.Contains(filter.Key)
                });
            }

            OnPropertyChanged(nameof(HasFilters));
        }

        /// <summary>
        /// Rebuilds the visible item list for the current category.
        /// </summary>
        private void ApplyCurrentItemFilters(string? selectedItemKey)
        {
            var items = _categoryItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CurrentItems.Clear();
            foreach (var item in items)
            {
                CurrentItems.Add(new DownloadListItemViewModel(item, SelectItemAsync));
            }

            var targetItem = CurrentItems.FirstOrDefault(item => string.Equals(item.Item.ResourceKey, selectedItemKey, StringComparison.OrdinalIgnoreCase))
                ?? CurrentItems.FirstOrDefault();

            SetSelectedItem(targetItem);
            RaiseCategoryProperties();
        }


        // Filter selection

        /// <summary>
        /// Keeps sort filters single-select and capability filters multi-select.
        /// </summary>
        private async Task ToggleFilter(DownloadFilterViewModel filter)
        {
            if (string.Equals(filter.Kind, "sort", StringComparison.OrdinalIgnoreCase))
            {
                SelectedFilterKeys.RemoveWhere(key =>
                    Filters.Any(viewModel =>
                        string.Equals(viewModel.Kind, "sort", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(viewModel.Key, key, StringComparison.OrdinalIgnoreCase)));

                SelectedFilterKeys.Add(filter.Key);
            }
            else if (SelectedFilterKeys.Contains(filter.Key))
            {
                SelectedFilterKeys.Remove(filter.Key);
            }
            else
            {
                SelectedFilterKeys.Add(filter.Key);
            }

            UpdateFilterSelectionStates();
            _searchDebounceCts?.Cancel();
            _activeCategoryState.NeedsRefresh = true;
            _activeCategoryState.QueryVersion++;
            await RefreshCurrentQueryAsync();
        }

        /// <summary>
        /// Mirrors the backing filter set into the view models.
        /// </summary>
        private void UpdateFilterSelectionStates()
        {
            foreach (var viewModel in Filters)
            {
                viewModel.IsSelected = SelectedFilterKeys.Contains(viewModel.Key);
            }
        }

        // Localization

        /// <summary>
        /// Reapplies ASLM-owned labels without translating module-supplied content.
        /// </summary>
        private void RefreshLocalizedBindableText()
        {
            OnPropertyChanged(nameof(DetailHeaderTitle));
            OnPropertyChanged(nameof(ActiveCategoryTitle));

            foreach (var variant in Variants)
            {
                variant.RefreshLocalizedText();
            }

            foreach (var infoBlock in InfoBlocks)
            {
                infoBlock.RefreshLocalizedText();
            }

            OnPropertyChanged(nameof(SelectedInfoBlockTitle));
            OnPropertyChanged(nameof(SelectedInfoBlockText));
            OnPropertyChanged(nameof(HasSelectedInfoBlockText));
        }


        // Search refresh

        /// <summary>
        /// Re-runs the active provider query without showing a busy overlay.
        /// </summary>
        private async Task RefreshCurrentQueryAsync()
        {
            if (_activeCategory == null || IsInternalModulesSelected || !_isDownloadsOpen) return;
            await LoadCatalogAsync(
                preferCached: false,
                forceRefresh: true,
                preserveSelection: true,
                showBusyIndicator: false,
                silentRefresh: true,
                categoryGroupKey: _activeCategory.Category.GroupKey);
        }

        /// <summary>
        /// Delays provider-side search refresh until the user pauses typing.
        /// </summary>
        private void ScheduleRemoteSearchRefresh()
        {
            var debounceCts = ReplaceCancellationTokenSource(ref _searchDebounceCts);
            var ct = debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(350, ct);
                    ct.ThrowIfCancellationRequested();

                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        await RefreshCurrentQueryAsync();
                    });
                }
                catch (OperationCanceledException)
                {
                }
            }, ct);
        }


        // Item selection

        /// <summary>
        /// Activates the category and refreshes its selected item details.
        /// </summary>
        private async Task SelectCategoryAsync(DownloadCategoryViewModel category)
        {
            if (ReferenceEquals(_activeCategory, category) && !IsInternalModulesSelected) return;

            _searchDebounceCts?.Cancel();
            _catalogRefreshCts?.Cancel();
            _detailRefreshCts?.Cancel();
            SaveCategorySelection();
            IsInternalModulesSelected = false;
            ActivateCategory(category, selectedItemKey: null);

            if (_activeCategoryState.NeedsRefresh)
            {
                await RefreshCurrentQueryAsync();
                return;
            }
            if (_selectedItemDetail != null) return;

            if (_selectedItem != null)
            {
                var item = _selectedItem;
                await LoadSelectedItemDetailAsync(preferCached: true, forceRefresh: false, selectedVariantKey: null, silentRefresh: false);
                if (_isDownloadsOpen && !IsInternalModulesSelected && ReferenceEquals(_activeCategory, category) && ReferenceEquals(_selectedItem, item))
                    _ = LoadSelectedItemDetailAsync(preferCached: true, forceRefresh: true, selectedVariantKey: _selectedVariant?.Variant.ResourceKey, silentRefresh: true);
            }
            else
            {
                ClearDetail();
            }
        }

        /// <summary>
        /// Loads cached details first, then refreshes them in the background.
        /// </summary>
        private async Task SelectItemAsync(DownloadListItemViewModel item)
        {
            if (ReferenceEquals(_selectedItem, item) && _selectedItemDetail != null) return;

            SetSelectedItem(item);
            await LoadSelectedItemDetailAsync(preferCached: true, forceRefresh: false, selectedVariantKey: null, silentRefresh: false);
            if (_isDownloadsOpen && !IsInternalModulesSelected && ReferenceEquals(_selectedItem, item))
                _ = LoadSelectedItemDetailAsync(preferCached: true, forceRefresh: true, selectedVariantKey: _selectedVariant?.Variant.ResourceKey, silentRefresh: true);
        }

        /// <summary>
        /// Refreshes the selected item detail while guarding against stale responses.
        /// </summary>
        private async Task LoadSelectedItemDetailAsync(bool preferCached, bool forceRefresh, string? selectedVariantKey, bool silentRefresh)
        {
            if (_selectedItem == null)
            {
                ClearDetail();
                return;
            }

            var item = _selectedItem.Item;
            var itemResourceKey = item.ResourceKey;
            var detailCts = ReplaceCancellationTokenSource(ref _detailRefreshCts);
            var ct = detailCts.Token;

            try
            {
                // Ignore late responses when the user has already moved to another item
                var detail = await Task.Run(
                    () => _catalog.GetItemDetailAsync(item, preferCached, forceRefresh, ct),
                    ct);
                if (ct.IsCancellationRequested || _selectedItem?.Item.ResourceKey != itemResourceKey) return;

                var detailSignature = ComputeDetailSignature(detail);
                if (silentRefresh && string.Equals(_lastDetailSignature, detailSignature, StringComparison.Ordinal))
                {
                    return;
                }

                ApplyDetail(detail, selectedVariantKey);
                _lastDetailSignature = detailSignature;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested && !silentRefresh)
                {
                    Debug.WriteLine($"Failed to load download details for '{item.Title}': {ex}");
                }
            }
        }

        /// <summary>
        /// Rebuilds variants and info blocks for the selected item.
        /// </summary>
        private void ApplyDetail(DownloadCatalogItemDetail detail, string? selectedVariantKey)
        {
            _selectedItemDetail = detail;
            var previousBlockKey = _selectedInfoBlock?.Block.Id;

            Variants.Clear();
            foreach (var variant in detail.Variants)
            {
                Variants.Add(new DownloadVariantViewModel(variant, SelectVariant));
            }

            InfoBlocks.Clear();
            foreach (var block in detail.Blocks)
            {
                InfoBlocks.Add(new DownloadInfoBlockViewModel(block, detail.HomepageUrl, SelectInfoBlock));
            }

            var targetVariant = Variants.FirstOrDefault(variant => string.Equals(variant.Variant.ResourceKey, selectedVariantKey, StringComparison.OrdinalIgnoreCase))
                ?? Variants.FirstOrDefault(variant => string.Equals(variant.Variant.ResourceKey, detail.DefaultVariantResourceKey, StringComparison.OrdinalIgnoreCase))
                ?? Variants.FirstOrDefault();

            SetSelectedVariant(targetVariant);
            var targetBlock = InfoBlocks.FirstOrDefault(block => string.Equals(block.Block.Id, previousBlockKey, StringComparison.OrdinalIgnoreCase))
                ?? InfoBlocks.FirstOrDefault();
            SetSelectedInfoBlock(targetBlock);
            RaiseDetailProperties();
        }

        /// <summary>
        /// Resets all detail-side state when no item is selected.
        /// </summary>
        private void ClearDetail()
        {
            _selectedItemDetail = null;
            _lastDetailSignature = string.Empty;
            Variants.Clear();
            InfoBlocks.Clear();
            SetSelectedVariant(null);
            SetSelectedInfoBlock(null);
            RaiseDetailProperties();
        }

        /// <summary>
        /// Updates list selection and preserves current detail when the resource did not change.
        /// </summary>
        private void SetSelectedItem(DownloadListItemViewModel? item)
        {
            var preserveExistingDetail = item != null &&
                _selectedItemDetail != null &&
                string.Equals(_selectedItem?.Item.ResourceKey, item.Item.ResourceKey, StringComparison.OrdinalIgnoreCase);

            _selectedItem = item;
            foreach (var viewModel in CurrentItems)
            {
                viewModel.IsSelected = ReferenceEquals(viewModel, item);
            }

            if (item == null)
            {
                ClearDetail();
                return;
            }

            if (preserveExistingDetail)
            {
                RaiseDetailProperties();
                return;
            }

            _selectedItemDetail = null;
            Variants.Clear();
            InfoBlocks.Clear();
            SetSelectedVariant(null);
            SetSelectedInfoBlock(null);
            RaiseDetailProperties();
        }


        // Variant selection

        /// <summary>
        /// Routes selector clicks into the shared variant setter.
        /// </summary>
        private void SelectVariant(DownloadVariantViewModel? variant)
        {
            SetSelectedVariant(variant);
        }

        /// <summary>
        /// Opens or closes the custom variant menu.
        /// </summary>
        private void ToggleVariantSelector()
        {
            if (Variants.Count > 0)
                IsVariantSelectorOpen = !IsVariantSelectorOpen;
        }

        private void OnVariantSelectorToggleRequested(object? sender, EventArgs e) => ToggleVariantSelector();

        /// <summary>
        /// Updates variant selection and dependent property state.
        /// </summary>
        private void SetSelectedVariant(DownloadVariantViewModel? variant)
        {
            IsVariantSelectorOpen = false;
            _selectedVariant = variant;
            foreach (var viewModel in Variants)
            {
                viewModel.IsSelected = ReferenceEquals(viewModel, variant);
            }

            RaiseVariantProperties();
        }


        // Info block selection

        /// <summary>
        /// Routes tab clicks into the shared info block setter.
        /// </summary>
        private Task SelectInfoBlock(DownloadInfoBlockViewModel? block)
        {
            SetSelectedInfoBlock(block);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rebuilds the preview source and resets its measured height.
        /// </summary>
        private void SetSelectedInfoBlock(DownloadInfoBlockViewModel? block)
        {
            _selectedInfoBlock = block;
            foreach (var viewModel in InfoBlocks)
            {
                viewModel.IsSelected = ReferenceEquals(viewModel, block);
            }

            SelectedInfoBlockWebHeight = 1;
            SelectedInfoBlockSource = BuildInfoBlockSource(block);
            UpdateInfoBlockPreviewSource();
            RaiseInfoBlockProperties();
        }


        // Install actions

        /// <summary>
        /// Runs the install flow and then refreshes the catalog state.
        /// </summary>
        private async Task InstallSelectedVariantAsync()
        {
            if (_selectedItem == null || _selectedVariant == null || IsInstalling) return;

            IsInstalling = true;

            try
            {
                var item = _selectedItem.Item;
                var variant = _selectedVariant.Variant;
                var result = await Task.Run(() => _installer.InstallAsync(item, variant));
                if (result.Success)
                {
                    await LoadCatalogAsync(preferCached: true, forceRefresh: false, preserveSelection: true, showBusyIndicator: false, silentRefresh: false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to install download: {ex}");
            }
            finally
            {
                IsInstalling = false;
            }
        }

        /// <summary>
        /// Runs the uninstall flow and then refreshes the catalog state.
        /// </summary>
        private async Task DeleteSelectedVariantAsync()
        {
            if (_selectedItem == null || _selectedVariant == null || IsInstalling) return;

            IsInstalling = true;

            try
            {
                var item = _selectedItem.Item;
                var variant = _selectedVariant.Variant;
                var result = await Task.Run(() => _installer.UninstallAsync(item, variant));
                if (result.Success)
                {
                    await LoadCatalogAsync(preferCached: true, forceRefresh: false, preserveSelection: true, showBusyIndicator: false, silentRefresh: false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove download: {ex}");
            }
            finally
            {
                IsInstalling = false;
            }
        }

        /// <summary>
        /// Launches the selected variant or item page in the system browser.
        /// </summary>
        private async Task OpenSelectedVariantAsync()
        {
            var homepageUrl = GetSelectedVariantHomepageUrl();
            if (string.IsNullOrWhiteSpace(homepageUrl))
            {
                return;
            }

            if (!Uri.TryCreate(homepageUrl, UriKind.Absolute, out var homepageUri))
            {
                return;
            }

            try
            {
                await Launcher.Default.OpenAsync(homepageUri);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open download link: {ex}");
            }
        }


        // Detail formatting

        /// <summary>
        /// Prefers the selected variant page and falls back to item-level pages.
        /// </summary>
        private string GetSelectedVariantHomepageUrl()
        {
            if (!string.IsNullOrWhiteSpace(_selectedVariant?.Variant.HomepageUrl))
                return _selectedVariant.Variant.HomepageUrl;

            if (!string.IsNullOrWhiteSpace(_selectedItemDetail?.HomepageUrl))
                return _selectedItemDetail.HomepageUrl;

            return _selectedItem?.Item.HomepageUrl ?? string.Empty;
        }

        /// <summary>
        /// Resolves local or remote HTML content into a WebView source.
        /// </summary>
        private static WebViewSource? BuildInfoBlockSource(DownloadInfoBlockViewModel? block)
        {
            if (block == null || !block.HasContentUrl)
            {
                return null;
            }

            var contentUrl = block.ContentUrl;
            if (Uri.TryCreate(contentUrl, UriKind.Absolute, out var absoluteUri))
            {
                return new UrlWebViewSource { Url = absoluteUri.AbsoluteUri };
            }

            if (Path.IsPathRooted(contentUrl) && File.Exists(contentUrl))
            {
                return new UrlWebViewSource { Url = new Uri(contentUrl).AbsoluteUri };
            }

            if (Uri.TryCreate(block.SourceUrl, UriKind.Absolute, out var baseUri) &&
                Uri.TryCreate(baseUri, contentUrl, out var combinedUri))
            {
                return new UrlWebViewSource { Url = combinedUri.AbsoluteUri };
            }

            return null;
        }

        // Overlay tap handling

        /// <summary>
        /// Closes the dialog when the backdrop is tapped.
        /// </summary>
        private void OnBackgroundTapped(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Prevents backdrop close when the dialog surface is tapped.
        /// </summary>
        private void OnDialogTapped(object? sender, EventArgs e)
        {
            // Swallow taps inside the dialog so they do not propagate to the tinted backdrop.
        }


        // Dialog event handlers

        /// <summary>
        /// Forwards the click into the async install flow.
        /// </summary>
        private async void OnInstallClicked(object? sender, EventArgs e)
        {
            await InstallSelectedVariantAsync();
        }

        /// <summary>
        /// Forwards the click into the async uninstall flow.
        /// </summary>
        private async void OnDeleteClicked(object? sender, EventArgs e)
        {
            await DeleteSelectedVariantAsync();
        }

        /// <summary>
        /// Forwards the click into the browser launch flow.
        /// </summary>
        private async void OnOpenClicked(object? sender, EventArgs e)
        {
            await OpenSelectedVariantAsync();
        }

        /// <summary>
        /// Updates the local query text and debounces the remote refresh.
        /// </summary>
        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_restoringCategory) return;
            SearchText = e.NewTextValue ?? string.Empty;
            _catalogRefreshCts?.Cancel();
            ScheduleRemoteSearchRefresh();
        }

        /// <summary>
        /// Shows the built-in ASLM modules download page.
        /// </summary>
        private void OnModulesTapped(object? sender, TappedEventArgs e)
        {
            SaveCategorySelection();
            _searchDebounceCts?.Cancel();
            _catalogRefreshCts?.Cancel();
            IsInternalModulesSelected = true;
            _detailRefreshCts?.Cancel();
            foreach (var category in Categories)
            {
                category.IsSelected = false;
            }
        }

        /// <summary>
        /// Closes the downloads overlay from its shared close button.
        /// </summary>
        private void OnCloseClicked(object? sender, EventArgs e)
        {
            RequestClose();
        }


        // WebView preview

        private void OnInfoBlockWebViewHandlerChanged(object? sender, EventArgs e)
        {
#if WINDOWS
            var native = (sender as WebView)?.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.WebView2;
            if (ReferenceEquals(native, _infoBlockPreview?.PlatformView)) return;
            _infoBlockPreview?.Dispose();
            _infoBlockPreview = native == null ? null : new DownloadInfoPreviewHost(
                native, Dispatcher, height => SelectedInfoBlockWebHeight = height);
            UpdateInfoBlockPreviewSource();
#endif
        }

        private void UpdateInfoBlockPreviewSource()
        {
#if WINDOWS
            _infoBlockPreview?.SetSource(_isDownloadsOpen && !IsInternalModulesSelected
                ? (SelectedInfoBlockSource as UrlWebViewSource)?.Url : null);
#else
            BridgeContent.InfoBlockBrowser.Source = SelectedInfoBlockSource;
#endif
        }

        private void OnInfoBlockWebViewLoaded(object? sender, EventArgs e)
        {
            OnInfoBlockWebViewHandlerChanged(sender, e);
            UpdateInfoBlockPreviewSource();
        }

        private void OnInfoBlockWebViewUnloaded(object? sender, EventArgs e)
        {
#if WINDOWS
            _infoBlockPreview?.Dispose();
            _infoBlockPreview = null;
#endif
        }

        /// <summary>Keeps external links out of the embedded preview.</summary>
        private async void OnInfoBlockWebViewNavigating(object? sender, WebNavigatingEventArgs e)
        {
            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || IsSelectedInfoBlockUrl(e.Url))
                return;

            if (uri.Scheme == "http" || uri.Scheme == "https")
            {
                e.Cancel = true;
                try { await Launcher.Default.OpenAsync(uri); }
                catch { }
            }
        }

        private void OnInfoBlockWebViewNavigated(object? sender, WebNavigatedEventArgs e)
        {
            if (e.Result != WebNavigationResult.Success || !IsSelectedInfoBlockUrl(e.Url)) return;
            OnInfoBlockWebViewHandlerChanged(sender, EventArgs.Empty);
#if MACCATALYST
            // Preserve the existing one-shot fallback using WebKit's native content size.
            if (sender is WebView view && view.Handler?.PlatformView is WebKit.WKWebView native)
                SelectedInfoBlockWebHeight = native.ScrollView.ContentSize.Height;
#endif
        }

        private bool IsSelectedInfoBlockUrl(string? url) =>
            SelectedInfoBlockSource is UrlWebViewSource selected &&
            Uri.TryCreate(selected.Url, UriKind.Absolute, out var expected) &&
            Uri.TryCreate(url, UriKind.Absolute, out var actual) && expected.Equals(actual);

        /// <summary>
        /// Cancels all active refresh work before closing the dialog.
        /// </summary>
        private void RequestClose()
        {
            SaveCategorySelection();
            _isDownloadsOpen = false;
#if WINDOWS
            _infoBlockPreview?.Pause();
#endif
            _catalogLoadCts?.Cancel();
            _catalogRefreshCts?.Cancel();
            _detailRefreshCts?.Cancel();
            _searchDebounceCts?.Cancel();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }


        // Snapshot signatures

        /// <summary>
        /// Detects category changes without rebuilding unrelated pages as providers finish.
        /// </summary>
        private static string ComputeCategorySignature(DownloadCatalogCategory category)
        {
            var builder = new StringBuilder();
            AppendSignatureSegment(builder, category.GroupKey);
            AppendSignatureSegment(builder, category.Title);
            AppendSignatureSegment(builder, category.Description);
            builder.Append(category.SortOrder).Append(';');
            builder.Append(category.Filters.Count).Append(';');

            foreach (var filter in category.Filters)
            {
                AppendSignatureSegment(builder, filter.Key);
                AppendSignatureSegment(builder, filter.Title);
                AppendSignatureSegment(builder, filter.Kind);
                builder.Append(filter.Selected ? '1' : '0').Append(';');
                builder.Append(filter.SortOrder).Append(';');
            }

            builder.Append(category.Items.Count).Append(';');
            foreach (var item in category.Items)
            {
                AppendSignatureSegment(builder, item.ResourceKey);
                AppendSignatureSegment(builder, item.CategoryId);
                AppendSignatureSegment(builder, item.Title);
                AppendSignatureSegment(builder, item.Summary);
                AppendSignatureSegment(builder, item.Provider);
                AppendSignatureSegment(builder, item.Version);
                AppendSignatureSegment(builder, item.HomepageUrl);
                AppendSignatureSegment(builder, item.DefaultVariantResourceKey);
                AppendMetadataSignature(builder, item.Details);
                AppendMetadataSignature(builder, item.Tags);
                builder.Append(item.Installed ? '1' : '0').Append(';');
                AppendSignatureSegment(builder, item.InstalledVersion);
                builder.Append(item.VariantCount).Append(';');
                builder.Append(item.SortOrder).Append(';');
                builder.Append(item.Sources.Count).Append(';');
                foreach (var source in item.Sources)
                {
                    AppendSignatureSegment(builder, source.ModuleId);
                    AppendSignatureSegment(builder, source.ModuleName);
                    AppendSignatureSegment(builder, source.ModuleSourcePath);
                    AppendSignatureSegment(builder, source.CategoryId);
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Computes a compact signature for detail-level change detection.
        /// </summary>
        private static string ComputeDetailSignature(DownloadCatalogItemDetail detail)
        {
            var builder = new StringBuilder();
            AppendSignatureSegment(builder, detail.ResourceKey);
            AppendMetadataSignature(builder, detail.Details);
            AppendMetadataSignature(builder, detail.Tags);
            AppendSignatureSegment(builder, detail.DefaultVariantResourceKey);
            builder.Append(detail.Variants.Count).Append(';');

            foreach (var variant in detail.Variants)
            {
                AppendSignatureSegment(builder, variant.ResourceKey);
                AppendSignatureSegment(builder, variant.Title);
                AppendSignatureSegment(builder, variant.Summary);
                AppendSignatureSegment(builder, variant.HomepageUrl);
                builder.Append(variant.Size).Append(';');
                builder.Append(variant.HasSize ? '1' : '0').Append(';');
                builder.Append(variant.SortOrder).Append(';');
                builder.Append(variant.Installed ? '1' : '0').Append(';');
            }

            builder.Append(detail.Blocks.Count).Append(';');
            foreach (var block in detail.Blocks)
            {
                AppendSignatureSegment(builder, block.Id);
                AppendSignatureSegment(builder, block.Format);
                AppendSignatureSegment(builder, block.Content);
                AppendSignatureSegment(builder, block.ContentUrl);
                AppendSignatureSegment(builder, block.SourceUrl);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Includes every rendered metadata property in refresh change detection.
        /// </summary>
        private static void AppendMetadataSignature(StringBuilder builder, IReadOnlyList<ModuleDownloadField> values)
        {
            builder.Append(values.Count).Append(';');
            foreach (var value in values)
            {
                AppendSignatureSegment(builder, value.Text);
                AppendSignatureSegment(builder, value.BackgroundColor);
                AppendSignatureSegment(builder, value.TextColor);
                AppendSignatureSegment(builder, value.Image?.Key);
                builder.Append(value.ShowInCatalog ? '1' : '0').Append(';');
            }
        }

        /// <summary>Appends an unambiguous length-prefixed signature segment.</summary>
        private static void AppendSignatureSegment(StringBuilder builder, string? value)
        {
            if (value == null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length).Append(':').Append(value).Append(';');
        }

        /// <summary>
        /// Formats download sizes in decimal byte units, matching the provider's MB/GB labels.
        /// </summary>
        private static string FormatDownloadSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
            decimal value = bytes;
            var unit = 0;
            while (decimal.Round(value, 2) >= 1000 && unit < units.Length - 1)
            {
                value /= 1000;
                unit++;
            }

            return $"{value.ToString("0.##", CultureInfo.CurrentCulture)} {units[unit]}";
        }


        // Dialog sizing

        /// <summary>
        /// Resizes the dialog within its supported min and max bounds.
        /// </summary>
        private void UpdateDialogSize()
        {
            if (Width <= 0 || Height <= 0) return;

            DownloadDialog.WidthRequest = ClampDialogSize(Math.Floor(Width * DialogWidthFactor), MinDialogWidth, MaxDialogWidth);
            DownloadDialog.HeightRequest = ClampDialogSize(Math.Floor(Height * DialogHeightFactor), MinDialogHeight, MaxDialogHeight);
        }

        /// <summary>
        /// Keeps one dialog dimension within the configured bounds.
        /// </summary>
        private static double ClampDialogSize(double value, double min, double max) => Math.Max(min, Math.Min(max, value));


        // Cancellation tokens

        /// <summary>
        /// Cancels and disposes the previous token source before creating a new one.
        /// </summary>
        private static CancellationTokenSource ReplaceCancellationTokenSource(ref CancellationTokenSource? current)
        {
            current?.Cancel();
            current?.Dispose();
            current = new CancellationTokenSource();
            return current;
        }


        // Property refresh helpers

        /// <summary>
        /// Raises layout-level state used by the list and detail panes.
        /// </summary>
        private void RaiseLayoutProperties()
        {
            OnPropertyChanged(nameof(HasCurrentItems));
            OnPropertyChanged(nameof(IsItemListEmptyVisible));
            OnPropertyChanged(nameof(HasSelectedItem));
            OnPropertyChanged(nameof(IsDetailEmptyVisible));
        }

        /// <summary>
        /// Raises category-level properties.
        /// </summary>
        private void RaiseCategoryProperties()
        {
            OnPropertyChanged(nameof(ActiveCategoryTitle));
            OnPropertyChanged(nameof(ActiveCategoryDescription));
            OnPropertyChanged(nameof(HasActiveCategoryDescription));
            OnPropertyChanged(nameof(IsModuleCategorySelected));
            RaiseLayoutProperties();
        }

        /// <summary>
        /// Raises detail-level properties.
        /// </summary>
        private void RaiseDetailProperties()
        {
            SelectedItemDetails = CreateFields(_selectedItemDetail?.Details ?? _selectedItem?.Item.Details ?? []);
            SelectedItemTags = CreateFields(_selectedItemDetail?.Tags ?? _selectedItem?.Item.Tags ?? []);
            OnPropertyChanged(nameof(DetailHeaderTitle));
            OnPropertyChanged(nameof(HasSelectedItem));
            OnPropertyChanged(nameof(IsDetailEmptyVisible));
            OnPropertyChanged(nameof(SelectedItemTitle));
            OnPropertyChanged(nameof(SelectedItemDetails));
            OnPropertyChanged(nameof(HasSelectedItemDetails));
            OnPropertyChanged(nameof(SelectedItemSummary));
            OnPropertyChanged(nameof(HasSelectedItemSummary));
            OnPropertyChanged(nameof(SelectedItemTags));
            OnPropertyChanged(nameof(HasSelectedItemTags));
            OnPropertyChanged(nameof(HasVariants));
            OnPropertyChanged(nameof(HasInfoBlocks));
            RaiseInfoBlockProperties();
            RaiseVariantProperties();
        }

        /// <summary>
        /// Raises variant-level properties.
        /// </summary>
        private void RaiseVariantProperties()
        {
            OnPropertyChanged(nameof(HasSelectedVariant));
            OnPropertyChanged(nameof(SelectedVariantCard));
            OnPropertyChanged(nameof(SelectedVariantTitle));
            OnPropertyChanged(nameof(SelectedVariantSummary));
            OnPropertyChanged(nameof(HasSelectedVariantSummary));
            OnPropertyChanged(nameof(ShowInstallButton));
            OnPropertyChanged(nameof(ShowDeleteButton));
            OnPropertyChanged(nameof(ShowOpenVariantButton));
        }

        /// <summary>
        /// Raises info block properties.
        /// </summary>
        private void RaiseInfoBlockProperties()
        {
            OnPropertyChanged(nameof(HasInfoBlocks));
            OnPropertyChanged(nameof(HasMultipleInfoBlocks));
            OnPropertyChanged(nameof(HasSelectedInfoBlock));
            OnPropertyChanged(nameof(SelectedInfoBlockTitle));
            OnPropertyChanged(nameof(SelectedInfoBlockText));
            OnPropertyChanged(nameof(HasSelectedInfoBlockText));
            OnPropertyChanged(nameof(HasSelectedInfoBlockWebContent));
            OnPropertyChanged(nameof(SelectedInfoBlockWebHeight));
        }


        /// <summary>
        /// Keeps a category's query and last selection for the lifetime of the overlay, without disk storage.
        /// </summary>
        private sealed class DownloadCategoryState
        {
            public string SearchText { get; set; } = string.Empty;
            public HashSet<string> FilterKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
            public bool NeedsRefresh { get; set; } = true;
            public int QueryVersion { get; set; }
            public string? SelectedItemKey { get; set; }
            public string? SelectedVariantKey { get; set; }
            public DownloadCatalogItemDetail? Detail { get; set; }
        }

        /// <summary>
        /// Represents one selectable category in the sidebar.
        /// </summary>
        public sealed class DownloadCategoryViewModel : INotifyPropertyChanged
        {
            private readonly Func<DownloadCategoryViewModel, Task> _selectAction;
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one category to its select action.
            /// </summary>
            public DownloadCategoryViewModel(DownloadCatalogCategory category, Func<DownloadCategoryViewModel, Task> selectAction)
            {
                Category = category;
                _selectAction = selectAction;
                SelectCommand = new Command(async () => await _selectAction(this));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public DownloadCatalogCategory Category { get; private set; }
            public string Title => Category.Title;
            public string Description => Category.Description;
            public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
            public ICommand SelectCommand { get; }

            internal bool Update(DownloadCatalogCategory category)
            {
                if (string.Equals(ComputeCategorySignature(Category), ComputeCategorySignature(category), StringComparison.Ordinal))
                    return false;

                Category = category;
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(HasDescription));
                return true;
            }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }



            // Property notifications

            /// <summary>
            /// Raises the local property change event.
            /// </summary>
            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }


        /// <summary>
        /// Represents one selectable provider filter.
        /// </summary>
        public sealed class DownloadFilterViewModel : INotifyPropertyChanged
        {
            private readonly Func<DownloadFilterViewModel, Task> _toggleAction;
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one filter to its toggle action.
            /// </summary>
            public DownloadFilterViewModel(string key, string title, string kind, Func<DownloadFilterViewModel, Task> toggleAction)
            {
                Key = key;
                Title = title;
                Kind = kind;
                _toggleAction = toggleAction;
                ToggleCommand = new Command(async () => await _toggleAction(this));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Key { get; }
            public string Title { get; }
            public string Kind { get; }
            public ICommand ToggleCommand { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }




            /// <summary>
            /// Raises the local property change event.
            /// </summary>
            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }


        /// <summary>
        /// Represents one grouped item card in the center list.
        /// </summary>
        public sealed class DownloadListItemViewModel : INotifyPropertyChanged
        {
            private readonly Func<DownloadListItemViewModel, Task> _selectAction;
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one grouped item to its select action.
            /// </summary>
            public DownloadListItemViewModel(DownloadCatalogItem item, Func<DownloadListItemViewModel, Task> selectAction)
            {
                Item = item;
                _selectAction = selectAction;
                CatalogTags = CreateFields(item.Tags.Where(tag => tag.ShowInCatalog && tag.Image != null));
                SelectCommand = new Command(async () => await _selectAction(this));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public DownloadCatalogItem Item { get; }
            public string Title => Item.Title;
            public string Summary => Item.Summary;
            public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
            public IReadOnlyList<DownloadFieldViewModel> CatalogTags { get; }
            public bool HasCatalogTags => CatalogTags.Count > 0;
            public ICommand SelectCommand { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }

            public string MetadataLine
            {
                get
                {
                    var segments = new List<string>();
                    if (!string.IsNullOrWhiteSpace(Item.Provider)) segments.Add(Item.Provider);
                    if (!string.IsNullOrWhiteSpace(Item.Version)) segments.Add(Item.Version);
                    segments.AddRange(Item.Details
                        .Where(value => value.ShowInCatalog && !string.IsNullOrWhiteSpace(value.Text))
                        .Select(value => value.Text));
                    return string.Join(" | ", segments);
                }
            }

            public bool HasMetadata => !string.IsNullOrWhiteSpace(MetadataLine);



            /// <summary>
            /// Raises the local property change event.
            /// </summary>
            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }


        /// <summary>
        /// Represents one installable variant in the selector list.
        /// </summary>
        public sealed class DownloadVariantViewModel : INotifyPropertyChanged
        {
            private readonly Action<DownloadVariantViewModel?> _selectAction;
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one variant to its select action.
            /// </summary>
            public DownloadVariantViewModel(DownloadCatalogVariant variant, Action<DownloadVariantViewModel?> selectAction)
            {
                Variant = variant;
                _selectAction = selectAction;
                SelectCommand = new Command(() => _selectAction(this));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public DownloadCatalogVariant Variant { get; }
            public string Title => Variant.Title;
            public string Summary => Variant.Summary;
            public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
            public string SizeLabel => Variant.HasSize ? FormatDownloadSize(Variant.Size) : "-";
            public bool IsDownloaded => Variant.Installed;
            public string DownloadedLabel => L.Get(LocalizationKeys.Downloads_Downloaded);
            public ICommand SelectCommand { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }

            public void RefreshLocalizedText()
            {
                OnPropertyChanged(nameof(DownloadedLabel));
                OnPropertyChanged(nameof(SizeLabel));
            }

            // Property notifications

            /// <summary>
            /// Raises the local property change event.
            /// </summary>
            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static IReadOnlyList<DownloadFieldViewModel> CreateFields(IEnumerable<ModuleDownloadField> fields) =>
            fields.Select(field => new DownloadFieldViewModel(field)).ToArray();

        public sealed class DownloadFieldViewModel : INotifyPropertyChanged
        {
            private readonly DownloadCatalogIcon? _image;

            public DownloadFieldViewModel(ModuleDownloadField value)
            {
                Text = value.Text;
                // These are module-supplied values resolved by the bridge, not ASLM style defaults.
                if (value.BackgroundColor != null) Background = new SolidColorBrush(Color.FromArgb(value.BackgroundColor));
                if (value.TextColor != null) TextColor = Color.FromArgb(value.TextColor);
                _image = value.Image;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string Text { get; }
            public bool HasText => Text.Length > 0;
            public Brush? Background { get; }
            public Color? TextColor { get; }
            public ImageSource? Icon => _image == null ? null : PackagedIconTintCache.Get(
                _image.Data, TextColor ?? IconTintHelper.ResolvePaletteColor("LabelPrimary"));
            public bool HasBackground => Background != null;
            public bool HasTextColor => TextColor != null;
            public bool HasIcon => _image != null;

            public void RefreshIcon()
            {
                if (HasIcon && !HasTextColor)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }


        /// <summary>
        /// Represents one details tab in the right panel.
        /// </summary>
        public sealed class DownloadInfoBlockViewModel : INotifyPropertyChanged
        {
            private readonly Func<DownloadInfoBlockViewModel, Task> _selectAction;
            private readonly string? _baseUrl;
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one info block to its select action and resolves preview text.
            /// </summary>
            public DownloadInfoBlockViewModel(DownloadCatalogInfoBlock block, string? baseUrl, Func<DownloadInfoBlockViewModel, Task> selectAction)
            {
                Block = block;
                _selectAction = selectAction;
                _baseUrl = baseUrl;
                RenderedContent = ResolveRenderedContent(block, baseUrl);
                SelectCommand = new Command(async () => await _selectAction(this));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public DownloadCatalogInfoBlock Block { get; }
            public string Title => string.IsNullOrWhiteSpace(Block.Title) ? L.Get(LocalizationKeys.Downloads_Details) : Block.Title;
            public string RenderedContent { get; private set; }
            public string ContentUrl => Block.ContentUrl;
            public string SourceUrl => Block.SourceUrl;
            public bool HasTextContent => !string.IsNullOrWhiteSpace(RenderedContent);
            public bool HasContentUrl => !string.IsNullOrWhiteSpace(ContentUrl);
            public ICommand SelectCommand { get; }

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }



            public void RefreshLocalizedText()
            {
                RenderedContent = ResolveRenderedContent(Block, _baseUrl);
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(RenderedContent));
                OnPropertyChanged(nameof(HasTextContent));
            }

            // Content resolution

            /// <summary>
            /// Skips text rendering when the block points at dedicated HTML content.
            /// </summary>
            private static string ResolveRenderedContent(DownloadCatalogInfoBlock block, string? baseUrl)
            {
                if (!string.IsNullOrWhiteSpace(block.ContentUrl))
                {
                    var format = (block.Format ?? string.Empty).Trim().ToLowerInvariant();
                    if (format is "html-file" or "html-url" or "web")
                    {
                        return string.Empty;
                    }
                }

                return InfoBlockTextFormatter.Render(block, baseUrl);
            }


            // Property notifications

            /// <summary>
            /// Raises the local property change event.
            /// </summary>
            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }


        /// <summary>
        /// Converts text-based blocks into a readable plain-text fallback.
        /// </summary>
        private static class InfoBlockTextFormatter
        {
            private static readonly Regex ScriptRegex = new(
                "<script\\b[^<]*(?:(?!</script>)<[^<]*)*</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

            private static readonly Regex FencedCodeRegex = new(
                "```(?<lang>[a-zA-Z0-9_+-]*)\\s*\\n(?<code>.*?)```",
                RegexOptions.Singleline | RegexOptions.Compiled);

            private static readonly Regex HeadingRegex = new(
                "^(#{1,6})\\s+(.*)$",
                RegexOptions.Compiled);

            private static readonly Regex UnorderedListRegex = new(
                "^\\s*[-*+]\\s+(.*)$",
                RegexOptions.Compiled);

            private static readonly Regex OrderedListRegex = new(
                "^\\s*\\d+\\.\\s+(.*)$",
                RegexOptions.Compiled);

            private static readonly Regex BlockquoteRegex = new(
                "^\\s*>\\s?(.*)$",
                RegexOptions.Compiled);

            private static readonly Regex HorizontalRuleRegex = new(
                "^\\s*([-*_]){3,}\\s*$",
                RegexOptions.Compiled);


            // Text rendering

            /// <summary>
            /// Dispatches block rendering based on the declared format.
            /// </summary>
            public static string Render(DownloadCatalogInfoBlock block, string? baseUrl)
            {
                if (string.IsNullOrWhiteSpace(block.Content))
                {
                    return string.Empty;
                }

                var format = (block.Format ?? "text").Trim().ToLowerInvariant();

                try
                {
                    return format switch
                    {
                        "html" => ConvertHtmlToText(block.Content, baseUrl),
                        "markdown" or "md" => ConvertMarkdownToText(block.Content, baseUrl),
                        _ => NormalizePlainText(block.Content)
                    };
                }
                catch
                {
                    return NormalizePlainText(block.Content);
                }
            }

            /// <summary>
            /// Normalizes line endings and trims extra outer whitespace.
            /// </summary>
            private static string NormalizePlainText(string text)
            {
                return (text ?? string.Empty)
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\r", "\n", StringComparison.Ordinal)
                    .Trim();
            }

            /// <summary>
            /// Reduces markdown into a readable plain-text representation.
            /// </summary>
            private static string ConvertMarkdownToText(string markdown, string? baseUrl)
            {
                var normalized = NormalizePlainText(markdown);
                var builder = new StringBuilder();
                var paragraphLines = new List<string>();
                var codeLines = new List<string>();
                var inCodeBlock = false;

                foreach (var rawLine in normalized.Split('\n'))
                {
                    var line = rawLine.TrimEnd();

                    // Track fenced code blocks as raw text sections.
                    if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        FlushParagraph(builder, paragraphLines);

                        if (inCodeBlock)
                        {
                            foreach (var codeLine in codeLines)
                            {
                                AppendLine(builder, codeLine);
                            }

                            codeLines.Clear();
                            AppendBlankLine(builder);
                            inCodeBlock = false;
                        }
                        else
                        {
                            inCodeBlock = true;
                        }

                        continue;
                    }

                    if (inCodeBlock)
                    {
                        codeLines.Add(line);
                        continue;
                    }

                    // Treat blank lines as paragraph boundaries.
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendBlankLine(builder);
                        continue;
                    }

                    // Preserve common markdown block markers in a simplified text form.
                    var headingMatch = HeadingRegex.Match(line);
                    if (headingMatch.Success)
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendLine(builder, headingMatch.Groups[2].Value.Trim().ToUpperInvariant());
                        AppendBlankLine(builder);
                        continue;
                    }

                    if (HorizontalRuleRegex.IsMatch(line))
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendLine(builder, "--------------------------------");
                        AppendBlankLine(builder);
                        continue;
                    }

                    var quoteMatch = BlockquoteRegex.Match(line);
                    if (quoteMatch.Success)
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendLine(builder, $"> {ApplyInlineMarkdownToText(quoteMatch.Groups[1].Value.Trim(), baseUrl)}");
                        continue;
                    }

                    var unorderedMatch = UnorderedListRegex.Match(line);
                    if (unorderedMatch.Success)
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendLine(builder, $"- {ApplyInlineMarkdownToText(unorderedMatch.Groups[1].Value.Trim(), baseUrl)}");
                        continue;
                    }

                    var orderedMatch = OrderedListRegex.Match(line);
                    if (orderedMatch.Success)
                    {
                        FlushParagraph(builder, paragraphLines);
                        AppendLine(builder, $"1. {ApplyInlineMarkdownToText(orderedMatch.Groups[1].Value.Trim(), baseUrl)}");
                        continue;
                    }

                    paragraphLines.Add(line.Trim());
                }

                FlushParagraph(builder, paragraphLines);

                if (codeLines.Count > 0)
                {
                    foreach (var codeLine in codeLines)
                    {
                        AppendLine(builder, codeLine);
                    }
                }

                return builder.ToString().Trim();
            }

            /// <summary>
            /// Reduces lightweight HTML into a readable plain-text representation.
            /// </summary>
            private static string ConvertHtmlToText(string html, string? baseUrl)
            {
                var sanitized = ScriptRegex.Replace(html ?? string.Empty, string.Empty);
                sanitized = Regex.Replace(sanitized, "(?i)<br\\s*/?>", "\n");
                sanitized = Regex.Replace(sanitized, "(?i)</p\\s*>", "\n\n");
                sanitized = Regex.Replace(sanitized, "(?i)</div\\s*>", "\n");
                sanitized = Regex.Replace(sanitized, "(?i)</li\\s*>", "\n");
                sanitized = Regex.Replace(sanitized, "(?i)<li\\b[^>]*>", "- ");
                sanitized = Regex.Replace(sanitized, "(?i)</h[1-6]\\s*>", "\n\n");
                sanitized = Regex.Replace(sanitized, "(?i)<h[1-6]\\b[^>]*>", string.Empty);
                sanitized = Regex.Replace(sanitized, "(?i)<pre\\b[^>]*>", "\n");
                sanitized = Regex.Replace(sanitized, "(?i)</pre\\s*>", "\n");
                sanitized = Regex.Replace(sanitized, "(?i)<code\\b[^>]*>", "`");
                sanitized = Regex.Replace(sanitized, "(?i)</code\\s*>", "`");
                sanitized = Regex.Replace(sanitized, "(?i)<img\\b[^>]*alt\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>",
                    match => L.Get(LocalizationKeys.Downloads_ImagePlaceholderFormat, match.Groups[1].Value));
                sanitized = Regex.Replace(sanitized, "(?i)<img\\b[^>]*src\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>", match =>
                {
                    var url = AbsolutizeUrl(match.Groups[1].Value, baseUrl);
                    return string.IsNullOrWhiteSpace(url)
                        ? L.Get(LocalizationKeys.Downloads_ImagePlaceholder)
                        : L.Get(LocalizationKeys.Downloads_ImagePlaceholderFormat, url);
                });

                sanitized = Regex.Replace(sanitized, "(?i)<a\\b[^>]*href\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>(.*?)</a>", match =>
                {
                    var href = AbsolutizeUrl(WebUtility.HtmlDecode(match.Groups[1].Value), baseUrl);
                    var label = ConvertHtmlToText(match.Groups[2].Value, baseUrl);
                    return string.IsNullOrWhiteSpace(href) ? label : $"{label} ({href})";
                }, RegexOptions.Singleline);

                sanitized = Regex.Replace(sanitized, "<[^>]+>", string.Empty);
                sanitized = WebUtility.HtmlDecode(sanitized);
                sanitized = Regex.Replace(sanitized, "[ \t]+\n", "\n");
                sanitized = Regex.Replace(sanitized, "\n{3,}", "\n\n");
                return sanitized.Trim();
            }


            // Paragraph helpers

            /// <summary>
            /// Flushes the current paragraph into the output builder.
            /// </summary>
            private static void FlushParagraph(StringBuilder builder, List<string> paragraphLines)
            {
                if (paragraphLines.Count == 0)
                {
                    return;
                }

                var paragraph = string.Join(" ", paragraphLines).Trim();
                if (!string.IsNullOrWhiteSpace(paragraph))
                {
                    AppendLine(builder, ApplyInlineMarkdownToText(paragraph, null));
                    AppendBlankLine(builder);
                }

                paragraphLines.Clear();
            }

            /// <summary>
            /// Appends one line to the output builder.
            /// </summary>
            private static void AppendLine(StringBuilder builder, string value)
            {
                builder.AppendLine(value);
            }

            /// <summary>
            /// Appends one blank line when the builder is not already separated.
            /// </summary>
            private static void AppendBlankLine(StringBuilder builder)
            {
                if (builder.Length > 0 && CountTrailingLineFeeds(builder) < 2)
                {
                    builder.AppendLine();
                }
            }

            /// <summary>
            /// Counts trailing line feeds at the end of the builder.
            /// </summary>
            private static int CountTrailingLineFeeds(StringBuilder builder)
            {
                var count = 0;
                for (var index = builder.Length - 1; index >= 0 && count < 2; index--)
                {
                    var current = builder[index];
                    if (current == '\n')
                    {
                        count++;
                        continue;
                    }

                    if (current != '\r')
                    {
                        break;
                    }
                }

                return count;
            }


            // Inline markdown helpers

            /// <summary>
            /// Reduces inline markdown and links into readable text.
            /// </summary>
            private static string ApplyInlineMarkdownToText(string value, string? baseUrl)
            {
                var text = value ?? string.Empty;

                text = Regex.Replace(
                    text,
                    "!\\[(.*?)\\]\\((.*?)\\)",
                    match =>
                    {
                        var alt = match.Groups[1].Value.Trim();
                        var url = AbsolutizeUrl(match.Groups[2].Value.Trim(), baseUrl);
                        var imageLabel = L.Get(LocalizationKeys.Downloads_ImagePlaceholderFormat, alt);
                        return string.IsNullOrWhiteSpace(url) ? imageLabel : $"{imageLabel} {url}";
                    });

                text = Regex.Replace(
                    text,
                    "\\[(.*?)\\]\\((.*?)\\)",
                    match =>
                    {
                        var label = match.Groups[1].Value.Trim();
                        var url = AbsolutizeUrl(match.Groups[2].Value.Trim(), baseUrl);
                        return string.IsNullOrWhiteSpace(url) ? label : $"{label} ({url})";
                    });

                text = Regex.Replace(text, "\\*\\*(.+?)\\*\\*", "$1");
                text = Regex.Replace(text, "__(.+?)__", "$1");
                text = Regex.Replace(text, "(?<!\\*)\\*(?!\\s)(.+?)(?<!\\s)\\*(?!\\*)", "$1");
                text = Regex.Replace(text, "(?<!_)_(?!\\s)(.+?)(?<!\\s)_(?!_)", "$1");
                text = Regex.Replace(text, "`([^`]+)`", "$1");
                return WebUtility.HtmlDecode(text).Trim();
            }


            // URL helpers

            /// <summary>
            /// Resolves a relative URL against the block base URL.
            /// </summary>
            private static string AbsolutizeUrl(string? candidate, string? baseUrl)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    return string.Empty;
                }

                if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
                {
                    return absolute.ToString();
                }

                if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
                    Uri.TryCreate(baseUri, candidate, out var combined))
                {
                    return combined.ToString();
                }

                return candidate ?? string.Empty;
            }
        }
    }
}
