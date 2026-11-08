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
    /// Drives the shared download catalog dialog overlay: the internal modules page and the module-provided
    /// download categories with their filters, items, variants and information blocks.
    /// </summary>
    public partial class DownloadsView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        // Dialog size limits

        /// <summary>
        /// Share of the view width that the dialog takes before the size limits are applied.
        /// </summary>
        private const double DialogWidthFactor = 0.88;

        /// <summary>
        /// Share of the view height that the dialog takes before the size limits are applied.
        /// </summary>
        private const double DialogHeightFactor = 0.84;

        /// <summary>
        /// Smallest dialog width; a smaller view is still given this width.
        /// </summary>
        private const double MinDialogWidth = 1080;

        /// <summary>
        /// Smallest dialog height; a smaller view is still given this height.
        /// </summary>
        private const double MinDialogHeight = 620;

        /// <summary>
        /// Largest dialog width.
        /// </summary>
        private const double MaxDialogWidth = 1520;

        /// <summary>
        /// Largest dialog height.
        /// </summary>
        private const double MaxDialogHeight = 920;


        // Services and cancellation

        /// <summary>
        /// Catalog that loads the download categories from the modules that provide them.
        /// </summary>
        private readonly DownloadCatalog _catalog;

        /// <summary>
        /// Installer that downloads and removes variants and reports which operations are pending.
        /// </summary>
        private readonly DownloadInstaller _installer;

        /// <summary>
        /// Localization service the view is hooked to, so it is localized again when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Module information overlay shown on top of the modules page.
        /// </summary>
        private readonly ModuleInfo _moduleInfo;

        /// <summary>
        /// Cancellation source of the running full catalog load; replacing it cancels the previous load.
        /// </summary>
        private CancellationTokenSource? _catalogLoadCts;

        /// <summary>
        /// Cancellation source of the running query of a single category (search or filter change).
        /// </summary>
        private CancellationTokenSource? _catalogRefreshCts;

        /// <summary>
        /// Cancellation source of the running refresh of the selected item's detail.
        /// </summary>
        private CancellationTokenSource? _detailRefreshCts;

        /// <summary>
        /// Cancellation source of the pending debounce delay that postpones a search query.
        /// </summary>
        private CancellationTokenSource? _searchDebounceCts;


        // View state

        /// <summary>
        /// Set by the first Loaded event, so the initial dialog sizing runs only once.
        /// </summary>
        private bool _hasLoaded;

        /// <summary>
        /// Whether the internal modules page is shown instead of a catalog category.
        /// </summary>
        private bool _isInternalModulesSelected = true;

        /// <summary>
        /// Whether the catalog is loading and the busy indicator is shown.
        /// </summary>
        private bool _isBusy;

        /// <summary>
        /// Resource keys of variants whose installation or removal was started from this view and has not finished yet.
        /// </summary>
        private readonly HashSet<string> _pendingResourceKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Text of the search box for the active category.
        /// </summary>
        private string _searchText = string.Empty;

        /// <summary>
        /// Search text, filters and selection remembered for each category, keyed by the category group key.
        /// </summary>
        private readonly Dictionary<string, DownloadCategoryState> _categoryStates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// State of the active category; an empty state while no category is active.
        /// </summary>
        private DownloadCategoryState _activeCategoryState = new();

        /// <summary>
        /// Set while the stored search text of a category is written back to the search box,
        /// so that this change is not treated as a new user query.
        /// </summary>
        private bool _restoringCategory;

        /// <summary>
        /// Keys of the filters selected in the active category.
        /// </summary>
        private HashSet<string> SelectedFilterKeys => _activeCategoryState.FilterKeys;

        /// <summary>
        /// Signature of the detail that is currently applied, used to skip refreshes that bring no change.
        /// </summary>
        private string _lastDetailSignature = string.Empty;

        /// <summary>
        /// Items of the active category before they are turned into list entries.
        /// </summary>
        private List<DownloadCatalogItem> _categoryItems = [];

        /// <summary>
        /// Active catalog category, or null when there is none.
        /// </summary>
        private DownloadCategoryViewModel? _activeCategory;

        /// <summary>
        /// Selected list item, or null when nothing is selected.
        /// </summary>
        private DownloadListItemViewModel? _selectedItem;

        /// <summary>
        /// Loaded detail of the selected item, or null while it is not loaded.
        /// </summary>
        private DownloadCatalogItemDetail? _selectedItemDetail;

        /// <summary>
        /// Variant selected for the selected item, or null when there is none.
        /// </summary>
        private DownloadVariantViewModel? _selectedVariant;

        /// <summary>
        /// Whether the variant popup is open.
        /// </summary>
        private bool _isVariantSelectorOpen;

        /// <summary>
        /// Information block that is shown for the selected item, or null when there is none.
        /// </summary>
        private DownloadInfoBlockViewModel? _selectedInfoBlock;

        /// <summary>
        /// Web content of the selected information block, or null when it has none.
        /// </summary>
        private WebViewSource? _selectedInfoBlockSource;

        /// <summary>
        /// Measured height of the information block web content; starts at 1 until a valid height is reported.
        /// </summary>
        private double _selectedInfoBlockWebHeight = 1;

        /// <summary>
        /// Whether the dialog is open; results that arrive after it was closed are dropped.
        /// </summary>
        private bool _isDownloadsOpen;

        /// <summary>
        /// Whether the theme change handlers are attached.
        /// </summary>
        private bool _themeHandlersAttached;

#if WINDOWS
        /// <summary>
        /// Native preview host that renders the information block web content on Windows.
        /// </summary>
        private DownloadInfoPreviewHost? _infoBlockPreview;
#elif LINUX
        /// <summary>
        /// Document preview that renders the information block web content on Linux.
        /// </summary>
        private ASLM.Services.Controls.LinuxDocumentPreview? _infoBlockPreview;

        /// <summary>
        /// Native browser handle of the Linux document preview.
        /// </summary>
        private object? _linuxPreviewBrowser;
#endif


        // Shortcuts to the child controls

        /// <summary>
        /// Search box of the category page.
        /// </summary>
        private Entry SearchEntry => BridgeContent.SearchInput;

        /// <summary>
        /// Placeholder shown when the category has no items.
        /// </summary>
        private Label ItemListEmptyTitleLabel => BridgeContent.ItemListEmptyTitle;

        /// <summary>
        /// Placeholder shown when no item is selected.
        /// </summary>
        private Label DetailEmptyTitleLabel => BridgeContent.DetailEmptyTitle;

        /// <summary>
        /// Heading of the variant section.
        /// </summary>
        private Label VariantSectionLabel => BridgeContent.VariantTitle;

        /// <summary>
        /// Button that downloads the selected variant.
        /// </summary>
        private Button InstallButton => BridgeContent.InstallAction;

        /// <summary>
        /// Link button that opens the homepage of the selected variant.
        /// </summary>
        private ImageButton OpenButton => BridgeContent.OpenAction;

        /// <summary>
        /// Button that removes the installed variant.
        /// </summary>
        private Button RemoveButton => BridgeContent.RemoveAction;


        // Initialization

        /// <summary>
        /// Builds the download overlay and wires its core events.
        /// </summary>
        public DownloadsView(DownloadCatalog catalog, DownloadInstaller installer, AppLocalizationService localization,
            ModuleInstaller moduleInstaller, ModuleTrustService moduleTrustService, ModuleInfo moduleInfo, UpdateManager updates)
        {
            _catalog = catalog;
            _installer = installer;
            _localization = localization;
            _moduleInfo = moduleInfo;

            // Build the XAML tree and give the modules page its services.
            InitializeComponent();
            ModulesContent.Initialize(moduleInstaller, moduleTrustService, localization, updates);

            // The module information overlay opens on top of the modules page and refreshes the page when it closes.
            ModuleInfoHost.Content = moduleInfo;
            moduleInfo.CloseRequested += async (_, _) =>
            {
                ModuleInfoHost.IsVisible = false;
                await ModulesContent.RefreshAsync();
            };
            ModulesContent.ModuleSelected += async (_, module) =>
            {
                ModuleInfoHost.IsVisible = true;
                await moduleInfo.OpenAsync(module);
            };
            InstallCommand = new Command(async () => await InstallSelectedVariantAsync());
            OpenVariantCommand = new Command(async () => await OpenSelectedVariantAsync());
            BindingContext = this;

            // Forward the events of the category page: search, information web view, actions and the variant popup.
            BridgeContent.SearchTextChanged += OnSearchTextChanged;
            BridgeContent.InfoBlockWebViewHandlerChanged += OnInfoBlockWebViewHandlerChanged;
            BridgeContent.InfoBlockWebViewNavigating += OnInfoBlockWebViewNavigating;
            BridgeContent.InfoBlockWebViewNavigated += OnInfoBlockWebViewNavigated;
            BridgeContent.InfoBlockBrowser.Loaded += OnInfoBlockWebViewLoaded;
            BridgeContent.InfoBlockBrowser.Unloaded += OnInfoBlockWebViewUnloaded;
            BridgeContent.InstallRequested += OnInstallClicked;
            BridgeContent.OpenRequested += OnOpenClicked;
            BridgeContent.RemoveRequested += OnDeleteClicked;

            // The cancel button stops the pending download of the selected variant.
            BridgeContent.CancelRequested += (_, _) =>
            {
                if (_selectedItem != null && _selectedVariant != null)
                    _installer.Cancel(DownloadInstaller.GetResourceKey(_selectedItem.Item, _selectedVariant.Variant));
            };
            BridgeContent.VariantSelectorToggleRequested += OnVariantSelectorToggleRequested;

            // Localize now and whenever the language changes, then follow the visual lifecycle and size of the view.
            LocalizableAttach.Hook(this, _localization, this);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (_, _) => UpdateDialogSize();
        }


        // Localization

        /// <summary>
        /// Applies localized strings to the download overlay controls, then refreshes the bound texts.
        /// </summary>
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
            BridgeContent.CancelAction.Text = L.Get(LocalizationKeys.Common_Cancel);
            VariantSectionLabel.Text = L.Get(LocalizationKeys.Downloads_SelectVariant);
            BridgeContent.ItemDetailsTitle.Text = L.Get(LocalizationKeys.Downloads_DetailsLabel);
            BridgeContent.ItemFeaturesTitle.Text = L.Get(LocalizationKeys.Downloads_FeaturesLabel);
            SemanticProperties.SetDescription(OpenButton, L.Get(LocalizationKeys.Downloads_OpenLink));

            RefreshLocalizedBindableText();
        }


        // Events

        /// <summary>
        /// Raised when the user asks to close the download dialog.
        /// </summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Raised when a bound property of the view changes. It hides the inherited event so that
        /// <c>OnPropertyChanged</c> of this class notifies the bindings.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;


        // Collections and commands

        /// <summary>
        /// Download categories listed in the sidebar below the internal modules entry.
        /// </summary>
        public ObservableCollection<DownloadCategoryViewModel> Categories { get; } = new();

        /// <summary>
        /// Filter and sort chips of the active category.
        /// </summary>
        public ObservableCollection<DownloadFilterViewModel> Filters { get; } = new();

        /// <summary>
        /// Items of the active category in display order.
        /// </summary>
        public ObservableCollection<DownloadListItemViewModel> CurrentItems { get; } = new();

        /// <summary>
        /// Variants of the selected item.
        /// </summary>
        public ObservableCollection<DownloadVariantViewModel> Variants { get; } = new();

        /// <summary>
        /// Information blocks of the selected item's detail.
        /// </summary>
        public ObservableCollection<DownloadInfoBlockViewModel> InfoBlocks { get; } = new();

        /// <summary>
        /// Command that downloads the selected variant.
        /// </summary>
        public ICommand InstallCommand { get; }

        /// <summary>
        /// Command that opens the homepage of the selected variant.
        /// </summary>
        public ICommand OpenVariantCommand { get; }


        // Category state

        /// <summary>
        /// Whether the internal modules page is shown instead of a catalog category.
        /// </summary>
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

        /// <summary>
        /// Whether a catalog category page is shown.
        /// </summary>
        public bool IsModuleCategorySelected => !IsInternalModulesSelected && _activeCategory != null;

        /// <summary>
        /// Whether the catalog has at least one category.
        /// </summary>
        public bool HasModuleCategories => Categories.Count > 0;

        /// <summary>
        /// Whether the catalog is loading; it hides the empty-state placeholders.
        /// </summary>
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

        /// <summary>
        /// Whether the selected variant is being installed, either requested here or pending in the installer.
        /// </summary>
        public bool IsInstalling => _selectedItem != null && _selectedVariant != null &&
            (_pendingResourceKeys.Contains(DownloadInstaller.GetResourceKey(_selectedItem.Item, _selectedVariant.Variant)) ||
             _installer.IsPending(_selectedItem.Item, _selectedVariant.Variant));

        /// <summary>
        /// Title of the item list column: the active category title, or a generic catalog title.
        /// </summary>
        public string ActiveCategoryTitle => _activeCategory?.Title ?? L.Get(LocalizationKeys.Downloads_CatalogColumnTitle);

        /// <summary>
        /// Description of the active category, or an empty string.
        /// </summary>
        public string ActiveCategoryDescription => _activeCategory?.Description ?? string.Empty;

        /// <summary>
        /// Whether the active category has a description to show.
        /// </summary>
        public bool HasActiveCategoryDescription => !string.IsNullOrWhiteSpace(ActiveCategoryDescription);

        /// <summary>
        /// Whether the active category offers filters.
        /// </summary>
        public bool HasFilters => Filters.Count > 0;

        /// <summary>
        /// Whether the active category has items to list.
        /// </summary>
        public bool HasCurrentItems => CurrentItems.Count > 0;

        /// <summary>
        /// Whether the "no items" placeholder is shown: categories exist, the list is empty and nothing is loading.
        /// </summary>
        public bool IsItemListEmptyVisible => Categories.Count > 0 && !HasCurrentItems && !IsBusy;

        /// <summary>
        /// Text of the search box. A change by the user is stored in the state of the active category
        /// and marks that category for a new query; restoring a category does not.
        /// </summary>
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (string.Equals(_searchText, value, StringComparison.Ordinal))
                {
                    return;
                }

                // Restoring the stored text of a category must not count as a new query.
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


        // Selected item state

        /// <summary>
        /// Heading of the detail column: the selected item title, or the "select an item" hint.
        /// </summary>
        public string DetailHeaderTitle => HasSelectedItem
            ? SelectedItemTitle
            : L.Get(LocalizationKeys.Downloads_SelectItem);

        /// <summary>
        /// Whether an item is selected.
        /// </summary>
        public bool HasSelectedItem => _selectedItem != null;

        /// <summary>
        /// Whether the empty detail placeholder is shown: no item is selected and nothing is loading.
        /// </summary>
        public bool IsDetailEmptyVisible => !HasSelectedItem && !IsBusy;

        /// <summary>
        /// Title of the selected item; the loaded detail takes precedence over the list entry.
        /// </summary>
        public string SelectedItemTitle => _selectedItemDetail?.Title ?? _selectedItem?.Title ?? string.Empty;

        /// <summary>
        /// Detail fields of the selected item.
        /// </summary>
        public IReadOnlyList<DownloadFieldViewModel> SelectedItemDetails { get; private set; } = [];

        /// <summary>
        /// Whether the selected item has detail fields.
        /// </summary>
        public bool HasSelectedItemDetails => SelectedItemDetails.Count > 0;

        /// <summary>
        /// Summary of the selected item; the loaded detail takes precedence over the list entry.
        /// </summary>
        public string SelectedItemSummary => _selectedItemDetail?.Summary ?? _selectedItem?.Summary ?? string.Empty;

        /// <summary>
        /// Whether the selected item has a summary to show.
        /// </summary>
        public bool HasSelectedItemSummary => !string.IsNullOrWhiteSpace(SelectedItemSummary);

        /// <summary>
        /// Tag fields of the selected item.
        /// </summary>
        public IReadOnlyList<DownloadFieldViewModel> SelectedItemTags { get; private set; } = [];

        /// <summary>
        /// Whether the selected item has tags.
        /// </summary>
        public bool HasSelectedItemTags => SelectedItemTags.Count > 0;


        // Selected variant state

        /// <summary>
        /// Whether the selected item has variants.
        /// </summary>
        public bool HasVariants => Variants.Count > 0;

        /// <summary>
        /// Whether a variant is selected.
        /// </summary>
        public bool HasSelectedVariant => _selectedVariant != null;

        /// <summary>
        /// The selected variant as a single-element list, which the selector card binds to.
        /// </summary>
        public IReadOnlyList<DownloadVariantViewModel> SelectedVariantCard =>
            _selectedVariant == null ? [] : [_selectedVariant];

        /// <summary>
        /// Whether the variant popup is open.
        /// </summary>
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

        /// <summary>
        /// Title of the selected variant, or an empty string.
        /// </summary>
        public string SelectedVariantTitle => _selectedVariant?.Title ?? string.Empty;

        /// <summary>
        /// Summary of the selected variant, or an empty string.
        /// </summary>
        public string SelectedVariantSummary => _selectedVariant?.Summary ?? string.Empty;

        /// <summary>
        /// Whether the selected variant has a summary to show.
        /// </summary>
        public bool HasSelectedVariantSummary => !string.IsNullOrWhiteSpace(SelectedVariantSummary);

        /// <summary>
        /// Whether the download button is shown: a variant is selected, not installed and not being installed.
        /// </summary>
        public bool ShowInstallButton => _selectedVariant != null && !_selectedVariant.Variant.Installed && !IsInstalling;

        /// <summary>
        /// Whether the remove button is shown: the selected variant is installed and not being installed.
        /// </summary>
        public bool ShowDeleteButton => _selectedVariant?.Variant.Installed == true && !IsInstalling;

        /// <summary>
        /// Whether the cancel button is shown: the installer has a pending operation for the selected variant.
        /// </summary>
        public bool ShowCancelButton => _selectedItem != null && _selectedVariant != null &&
            _installer.IsPending(_selectedItem.Item, _selectedVariant.Variant);

        /// <summary>
        /// Whether the selected variant has a homepage that the link button can open.
        /// </summary>
        public bool ShowOpenVariantButton => !string.IsNullOrWhiteSpace(GetSelectedVariantHomepageUrl());


        // Information block state

        /// <summary>
        /// Whether the selected item has information blocks.
        /// </summary>
        public bool HasInfoBlocks => InfoBlocks.Count > 0;

        /// <summary>
        /// Whether there are several information blocks, which makes the block selector visible.
        /// </summary>
        public bool HasMultipleInfoBlocks => InfoBlocks.Count > 1;

        /// <summary>
        /// Whether an information block is selected.
        /// </summary>
        public bool HasSelectedInfoBlock => _selectedInfoBlock != null;

        /// <summary>
        /// Title of the selected information block, or the generic details title.
        /// </summary>
        public string SelectedInfoBlockTitle => _selectedInfoBlock?.Title ?? L.Get(LocalizationKeys.Downloads_Details);

        /// <summary>
        /// Rendered plain-text content of the selected information block, or an empty string.
        /// </summary>
        public string SelectedInfoBlockText => _selectedInfoBlock?.RenderedContent ?? string.Empty;

        /// <summary>
        /// Whether the selected information block has text to show.
        /// </summary>
        public bool HasSelectedInfoBlockText => !string.IsNullOrWhiteSpace(SelectedInfoBlockText);

        /// <summary>
        /// Whether the selected information block is shown as web content.
        /// </summary>
        public bool HasSelectedInfoBlockWebContent => SelectedInfoBlockSource != null;

        /// <summary>
        /// Web content of the selected information block, or null when it has none.
        /// </summary>
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

        /// <summary>
        /// Height of the information block web content. Invalid values are ignored, the value is rounded up,
        /// and changes of less than half a unit are ignored.
        /// </summary>
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
        /// Raises the local property change event; the property name defaults to the calling member.
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
            // Mark the dialog open first, so catalog results are accepted, and prepare the visuals.
            _isDownloadsOpen = true;
            RefreshThemeIcons();
            UpdateInfoBlockPreviewSource();
            UpdateDialogSize();
            ModuleInfoHost.IsVisible = false;

            // Refresh the modules page and the catalog at the same time; the catalog loads with a forced upstream refresh.
            return Task.WhenAll(ModulesContent.RefreshAsync(), LoadCatalogAsync(
                preferCached: true,
                forceRefresh: true,
                preserveSelection: true,
                showBusyIndicator: true,
                silentRefresh: false));
        }


        // Lifecycle and theme icons

        /// <summary>
        /// Subscribes to installer and theme changes when the view enters the visual tree
        /// and initializes the dialog size on the first load.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            // Unsubscribe before subscribing, so a repeated Loaded event cannot register the handler twice.
            _installer.OperationsChanged -= OnDownloadOperationsChanged;
            _installer.OperationsChanged += OnDownloadOperationsChanged;
            RaiseVariantProperties();
            AttachThemeHandlers();

            // The dialog size is set once here; later size changes are handled by the SizeChanged event.
            if (_hasLoaded) return;
            _hasLoaded = true;
            UpdateDialogSize();
        }

        /// <summary>
        /// Releases the installer and theme subscriptions when the view leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            _installer.OperationsChanged -= OnDownloadOperationsChanged;
            DetachThemeHandlers();
        }

        /// <summary>
        /// Refreshes the install, remove and cancel state on the main thread when the installer's operations change.
        /// </summary>
        private void OnDownloadOperationsChanged(object? sender, EventArgs e) =>
            MainThread.BeginInvokeOnMainThread(RaiseVariantProperties);

        /// <summary>
        /// Subscribes to palette and system theme changes once and tints the icons for the current theme.
        /// </summary>
        private void AttachThemeHandlers()
        {
            // Already subscribed: only the icons need the current tint.
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

        /// <summary>
        /// Unsubscribes from palette and system theme changes if they were subscribed.
        /// </summary>
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

        /// <summary>
        /// Retints the icons on the main thread after a custom palette was applied.
        /// </summary>
        private void OnPaletteApplied()
        {
            MainThread.BeginInvokeOnMainThread(RefreshThemeIcons);
        }

        /// <summary>
        /// Retints the icons on the main thread after the system theme changed.
        /// </summary>
        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshThemeIcons);
        }

        /// <summary>
        /// Applies the primary label color to the link icon and to the icons of the item fields and tags.
        /// </summary>
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
        /// <param name="preferCached">Allows the modules to answer from their local cache.</param>
        /// <param name="forceRefresh">Asks the modules to refresh their upstream data first.</param>
        /// <param name="preserveSelection">Keeps the selected item and variant when they still exist.</param>
        /// <param name="showBusyIndicator">Shows the busy indicator while loading.</param>
        /// <param name="silentRefresh">Marks a background refresh whose failures are not traced and whose unchanged detail is not reapplied.</param>
        /// <param name="categoryGroupKey">Group key of the single category to query, or null to load the whole catalog.</param>
        private async Task LoadCatalogAsync(
            bool preferCached,
            bool forceRefresh,
            bool preserveSelection,
            bool showBusyIndicator,
            bool silentRefresh,
            string? categoryGroupKey = null)
        {
            // A category query must not cancel discovery of the other modules. A full load replaces any running
            // category query, and a category query invalidates older results of its own category through the
            // query version. Replacing a token source cancels the load that used the previous one.
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
                // The copied versions let a result be dropped when the user changed that query in the meantime.
                var queries = _categoryStates.ToDictionary(
                    pair => pair.Key,
                    pair => new DownloadCatalogQuery(pair.Value.SearchText, pair.Value.FilterKeys.ToArray()),
                    StringComparer.OrdinalIgnoreCase);
                var queryVersions = _categoryStates.ToDictionary(
                    pair => pair.Key, pair => pair.Value.QueryVersion, StringComparer.OrdinalIgnoreCase);
                var appliedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // A result is current while its category query has not changed since the snapshot above.
                bool IsCurrentQuery(string key) => !_categoryStates.TryGetValue(key, out var state) ||
                    state.QueryVersion == queryVersions.GetValueOrDefault(key);

                // Applies the categories whose query is still current to the sidebar and list, and reloads the detail
                // of the selected item when the active category changed or delivered its first result.
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

                // Load off the UI thread. A full load reports each category as soon as it is discovered, so the sidebar
                // fills progressively; a category query delivers everything with the final snapshot.
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
                // A newer load or a closed dialog canceled this one; its results are obsolete.
            }
            catch (Exception ex)
            {
                // Failures are only traced, and not for canceled or silent background refreshes.
                if (!ct.IsCancellationRequested &&
                    !silentRefresh)
                {
                    Debug.WriteLine($"Failed to refresh download catalog: {ex}");
                }
            }
            finally
            {
                // Only the latest full load ends the busy state; a category query leaves it as it is.
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
        /// <param name="categories">Categories to add or update.</param>
        /// <param name="retainedKeys">Group keys that stay in the sidebar; other categories are removed. Null removes nothing.</param>
        /// <param name="preserveSelection">Keeps the selected item when the active category is replaced.</param>
        /// <returns>True when the active category changed or its content was updated.</returns>
        private bool ApplyCategories(IReadOnlyList<DownloadCatalogCategory> categories, HashSet<string>? retainedKeys, bool preserveSelection)
        {
            // Remember the selection of the active category before its content is replaced.
            SaveCategorySelection();
            var activeChanged = false;

            // Add new categories and update existing ones in place, so their sidebar view models survive.
            foreach (var category in categories)
            {
                var existing = Categories.FirstOrDefault(viewModel =>
                    string.Equals(viewModel.Category.GroupKey, category.GroupKey, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    Categories.Add(new DownloadCategoryViewModel(category, SelectCategoryAsync));
                else if (existing.Update(category) && ReferenceEquals(existing, _activeCategory))
                    activeChanged = true;
            }

            // Drop categories that no longer exist, but only when the caller delivered a complete result.
            if (retainedKeys != null)
                for (var index = Categories.Count - 1; index >= 0; index--)
                    if (!retainedKeys.Contains(Categories[index].Category.GroupKey))
                        Categories.RemoveAt(index);

            // Order the sidebar by the category sort order and title, moving entries instead of recreating them.
            var ordered = Categories.OrderBy(category => category.Category.SortOrder)
                .ThenBy(category => category.Title, StringComparer.OrdinalIgnoreCase).ToList();
            for (var index = 0; index < ordered.Count; index++)
                if (!ReferenceEquals(Categories[index], ordered[index]))
                    Categories.Move(Categories.IndexOf(ordered[index]), index);

            // Keep the active category, or fall back to the first one when it disappeared, and rebuild the page if it changed.
            var targetCategory = _activeCategory != null && Categories.Contains(_activeCategory)
                ? _activeCategory : Categories.FirstOrDefault();
            activeChanged |= !ReferenceEquals(targetCategory, _activeCategory);
            if (activeChanged)
                ActivateCategory(targetCategory, preserveSelection ? _selectedItem?.Item.ResourceKey : null);

            // Publish the new sidebar state to the bindings.
            OnPropertyChanged(nameof(HasModuleCategories));
            OnPropertyChanged(nameof(IsModuleCategorySelected));
            RaiseLayoutProperties();
            return activeChanged;
        }

        /// <summary>
        /// Swaps the active category and rebuilds the list panel.
        /// </summary>
        /// <param name="category">Category to activate, or null when none is left.</param>
        /// <param name="selectedItemKey">Resource key of the item to select; null restores the item remembered for the category.</param>
        private void ActivateCategory(DownloadCategoryViewModel? category, string? selectedItemKey)
        {
            // Another category shows other items, so the old detail is cleared first.
            if (!string.Equals(_activeCategory?.Category.GroupKey, category?.Category.GroupKey, StringComparison.OrdinalIgnoreCase))
                ClearDetail();
            _activeCategory = category;
            _activeCategoryState = category == null ? new() : GetCategoryState(category.Category);

            // Write the remembered search text back into the box without counting it as a new query.
            _restoringCategory = true;
            try { SearchText = _activeCategoryState.SearchText; }
            finally { _restoringCategory = false; }
            foreach (var viewModel in Categories)
            {
                viewModel.IsSelected = !IsInternalModulesSelected && ReferenceEquals(viewModel, category);
            }

            // Rebuild filters and item list of the category and select the requested or remembered item.
            _categoryItems = category?.Category.Items.ToList() ?? [];
            BuildAvailableFilters();
            ApplyCurrentItemFilters(selectedItemKey ?? _activeCategoryState.SelectedItemKey);

            // Show the remembered detail again if it belongs to the item that ended up selected.
            if (_activeCategoryState.Detail is { } detail &&
                !ReferenceEquals(_selectedItemDetail, detail) &&
                string.Equals(detail.ResourceKey, _selectedItem?.Item.ResourceKey, StringComparison.OrdinalIgnoreCase))
            {
                ApplyDetail(detail, _activeCategoryState.SelectedVariantKey);
                _lastDetailSignature = ComputeDetailSignature(detail);
            }

            RaiseCategoryProperties();
        }

        /// <summary>
        /// Returns the remembered state of a category and creates it on first use. As long as the category has
        /// not been queried or changed, its default-selected filters are taken from the provider.
        /// </summary>
        private DownloadCategoryState GetCategoryState(DownloadCatalogCategory category)
        {
            if (!_categoryStates.TryGetValue(category.GroupKey, out var state))
            {
                state = new DownloadCategoryState();
                _categoryStates.Add(category.GroupKey, state);
            }

            // QueryVersion stays 0 until the first query or user change of search or filters.
            if (state.QueryVersion == 0)
                state.FilterKeys.UnionWith(category.Filters.Where(filter => filter.Selected).Select(filter => filter.Key));
            return state;
        }

        /// <summary>
        /// Stores the selected item, variant and detail in the state of the active category.
        /// </summary>
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
        /// Rebuilds the visible item list for the current category and selects the requested item.
        /// </summary>
        /// <param name="selectedItemKey">Resource key of the item to select, or null to select the first item.</param>
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

            // Select the requested item, or the first one when it is not in the list.
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
            // Selecting a sort filter replaces the previously selected sort filter; other filters toggle independently.
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

            // The filters changed the query: cancel a pending search delay, invalidate older results and query again.
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


        // Localized bindable text

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
            // Only a catalog category has a provider query; the internal modules page has none.
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
            // Every keystroke replaces the token, which cancels the previous delay; only the last one gets to query.
            var debounceCts = ReplaceCancellationTokenSource(ref _searchDebounceCts);
            var ct = debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait until the user pauses typing, then run the query on the UI thread unless it was canceled meanwhile.
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
                    // Another keystroke, a filter change or closing the dialog superseded this delayed query.
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

            // Stop work that belongs to the category being left, keep its selection and switch to the new category.
            _searchDebounceCts?.Cancel();
            _catalogRefreshCts?.Cancel();
            _detailRefreshCts?.Cancel();
            SaveCategorySelection();
            IsInternalModulesSelected = false;
            ActivateCategory(category, selectedItemKey: null);

            // A category whose query changed while it was inactive is queried again, which also reloads the detail.
            if (_activeCategoryState.NeedsRefresh)
            {
                await RefreshCurrentQueryAsync();
                return;
            }

            // The remembered detail was restored by ActivateCategory, so there is nothing to load.
            if (_selectedItemDetail != null) return;

            // Load the detail of the selected item from cache first, then refresh it silently if nothing changed meanwhile.
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
        /// <param name="preferCached">Allows the module to answer from its local cache.</param>
        /// <param name="forceRefresh">Asks the module to refresh its upstream data first.</param>
        /// <param name="selectedVariantKey">Resource key of the variant to select afterwards, or null for the default variant.</param>
        /// <param name="silentRefresh">Marks a background refresh: errors are not traced and an unchanged detail is not reapplied.</param>
        private async Task LoadSelectedItemDetailAsync(bool preferCached, bool forceRefresh, string? selectedVariantKey, bool silentRefresh)
        {
            if (_selectedItem == null)
            {
                ClearDetail();
                return;
            }

            // Starting a new load cancels the previous one, so only the latest request can apply its result.
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

                // A background refresh that returned the same content must not rebuild the variants and blocks,
                // which would reset the selection and the preview.
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
                // A newer load or another selection canceled this request, so the result is obsolete.
            }
            catch (Exception ex)
            {
                // Failures are traced only for the current, non-silent request.
                if (!ct.IsCancellationRequested && !silentRefresh)
                {
                    Debug.WriteLine($"Failed to load download details for '{item.Title}': {ex}");
                }
            }
        }

        /// <summary>
        /// Rebuilds variants and info blocks for the selected item.
        /// </summary>
        /// <param name="detail">Loaded detail of the selected item.</param>
        /// <param name="selectedVariantKey">Resource key of the variant to select, or null for the default variant.</param>
        private void ApplyDetail(DownloadCatalogItemDetail detail, string? selectedVariantKey)
        {
            // Remember the selected information block, so the same block stays selected after the rebuild.
            _selectedItemDetail = detail;
            var previousBlockKey = _selectedInfoBlock?.Block.Id;

            Variants.Clear();
            foreach (var variant in detail.Variants)
            {
                Variants.Add(new DownloadVariantViewModel(variant, SelectVariant, _installer));
            }

            InfoBlocks.Clear();
            foreach (var block in detail.Blocks)
            {
                InfoBlocks.Add(new DownloadInfoBlockViewModel(block, detail.HomepageUrl, SelectInfoBlock));
            }

            // Select the requested variant, otherwise the default of the detail, otherwise the first one.
            var targetVariant = Variants.FirstOrDefault(variant => string.Equals(variant.Variant.ResourceKey, selectedVariantKey, StringComparison.OrdinalIgnoreCase))
                ?? Variants.FirstOrDefault(variant => string.Equals(variant.Variant.ResourceKey, detail.DefaultVariantResourceKey, StringComparison.OrdinalIgnoreCase))
                ?? Variants.FirstOrDefault();

            SetSelectedVariant(targetVariant);

            // Keep the previously selected information block when it still exists.
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
        /// <param name="item">Item to select, or null to clear the selection.</param>
        private void SetSelectedItem(DownloadListItemViewModel? item)
        {
            // The loaded detail stays valid when the same resource is selected again.
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

            // A different resource: drop the previous detail until the new one is loaded.
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

        /// <summary>
        /// Opens or closes the variant popup when the category page requests it.
        /// </summary>
        private void OnVariantSelectorToggleRequested(object? sender, EventArgs e) => ToggleVariantSelector();

        /// <summary>
        /// Updates variant selection and dependent property state.
        /// </summary>
        private void SetSelectedVariant(DownloadVariantViewModel? variant)
        {
            // Choosing a variant also closes the popup.
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

            // A different block has a different content height, so the measured height starts over.
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
            if (_selectedItem == null || _selectedVariant == null || IsInstalling || _selectedVariant.Variant.Installed) return;

            // Mark the variant as pending first, so the buttons switch to the busy state before the installer reports.
            var item = _selectedItem.Item;
            var variant = _selectedVariant.Variant;
            var resourceKey = DownloadInstaller.GetResourceKey(item, variant);
            _pendingResourceKeys.Add(resourceKey);

            try
            {
                // Start the installation, show its state at once and reload the catalog after it succeeded.
                var installation = _installer.InstallAsync(item, variant);
                RaiseVariantProperties();
                var result = await installation;
                if (result.Success)
                {
                    await LoadCatalogAsync(preferCached: true, forceRefresh: false, preserveSelection: true, showBusyIndicator: false, silentRefresh: false);
                }
            }
            catch (OperationCanceledException)
            {
                // The installation was canceled; there is nothing to reload.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to install download: {ex}");
            }
            finally
            {
                // The installer's own state takes over once the pending marker is removed.
                _pendingResourceKeys.Remove(resourceKey);
                RaiseVariantProperties();
            }
        }

        /// <summary>
        /// Runs the uninstall flow and then refreshes the catalog state.
        /// </summary>
        private async Task DeleteSelectedVariantAsync()
        {
            if (_selectedItem == null || _selectedVariant == null || IsInstalling) return;

            // Mark the variant as pending first, so the buttons switch to the busy state before the installer reports.
            var item = _selectedItem.Item;
            var variant = _selectedVariant.Variant;
            var resourceKey = DownloadInstaller.GetResourceKey(item, variant);
            _pendingResourceKeys.Add(resourceKey);

            try
            {
                // Start the removal, show its state at once and reload the catalog after it succeeded.
                var removal = _installer.UninstallAsync(item, variant);
                RaiseVariantProperties();
                var result = await removal;
                if (result.Success)
                {
                    await LoadCatalogAsync(preferCached: true, forceRefresh: false, preserveSelection: true, showBusyIndicator: false, silentRefresh: false);
                }
            }
            catch (OperationCanceledException)
            {
                // The removal was canceled; there is nothing to reload.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove download: {ex}");
            }
            finally
            {
                // The installer's own state takes over once the pending marker is removed.
                _pendingResourceKeys.Remove(resourceKey);
                RaiseVariantProperties();
            }
        }

        /// <summary>
        /// Launches the selected variant or item page in the system browser.
        /// </summary>
        private async Task OpenSelectedVariantAsync()
        {
            // Only an absolute, well-formed URL is passed to the system browser.
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
                // A browser that cannot be launched is only traced.
                Debug.WriteLine($"Failed to open download link: {ex}");
            }
        }


        // Detail formatting

        /// <summary>
        /// Prefers the selected variant page and falls back to item-level pages.
        /// </summary>
        /// <returns>The first non-empty homepage URL of the variant, the item detail or the list item; otherwise an empty string.</returns>
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
        /// <param name="block">Information block whose content URL is resolved, or null.</param>
        /// <returns>A URL source, or null when the block has no usable content location.</returns>
        private static WebViewSource? BuildInfoBlockSource(DownloadInfoBlockViewModel? block)
        {
            if (block == null || !block.HasContentUrl)
            {
                return null;
            }

            // An absolute URL is used as it is.
            var contentUrl = block.ContentUrl;
            if (Uri.TryCreate(contentUrl, UriKind.Absolute, out var absoluteUri))
            {
                return new UrlWebViewSource { Url = absoluteUri.AbsoluteUri };
            }

            // An existing local file path becomes a file URL.
            if (Path.IsPathRooted(contentUrl) && File.Exists(contentUrl))
            {
                return new UrlWebViewSource { Url = new Uri(contentUrl).AbsoluteUri };
            }

            // A relative reference is resolved against the URL the block was loaded from.
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
            // Text that is written back while a category is restored is not a new query.
            if (_restoringCategory) return;

            // Store the text, drop a category query that is still running (the typed text makes it outdated)
            // and start the debounce delay for the next one.
            SearchText = e.NewTextValue ?? string.Empty;
            _catalogRefreshCts?.Cancel();
            ScheduleRemoteSearchRefresh();
        }

        /// <summary>
        /// Shows the built-in ASLM modules download page.
        /// </summary>
        private void OnModulesTapped(object? sender, TappedEventArgs e)
        {
            // Leave the active category: keep its selection, stop its pending work and deselect it in the sidebar.
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

        /// <summary>
        /// Creates or replaces the native preview host when the platform handler of the information web view changes.
        /// </summary>
        private void OnInfoBlockWebViewHandlerChanged(object? sender, EventArgs e)
        {
#if WINDOWS
            var native = (sender as WebView)?.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.WebView2;
            if (ReferenceEquals(native, _infoBlockPreview?.PlatformView)) return;
            _infoBlockPreview?.Dispose();
            _infoBlockPreview = native == null ? null : new DownloadInfoPreviewHost(
                native, Dispatcher, height => SelectedInfoBlockWebHeight = height);
            UpdateInfoBlockPreviewSource();
#elif LINUX
            var view = sender as WebView;
            var native = view?.Handler?.PlatformView;
            if (ReferenceEquals(native, _linuxPreviewBrowser)) return;
            _infoBlockPreview?.Dispose();
            _linuxPreviewBrowser = native;
            _infoBlockPreview = view == null ? null
                : ASLM.Services.Controls.LinuxDocumentPreview.Create(view, height => SelectedInfoBlockWebHeight = height);
#endif
        }

        /// <summary>
        /// Points the preview at the selected information block. On Windows the preview shows content only while the
        /// dialog is open and a catalog category is selected; other platforms assign the source to the web view directly.
        /// </summary>
        private void UpdateInfoBlockPreviewSource()
        {
#if WINDOWS
            _infoBlockPreview?.SetSource(_isDownloadsOpen && !IsInternalModulesSelected
                ? (SelectedInfoBlockSource as UrlWebViewSource)?.Url : null);
#else
            BridgeContent.InfoBlockBrowser.Source = SelectedInfoBlockSource;
#endif
        }

        /// <summary>
        /// Attaches the preview host and shows the selected source when the web view is loaded.
        /// </summary>
        private void OnInfoBlockWebViewLoaded(object? sender, EventArgs e)
        {
            OnInfoBlockWebViewHandlerChanged(sender, e);
            UpdateInfoBlockPreviewSource();
        }

        /// <summary>
        /// Disposes the native preview host when the web view is unloaded (Windows and Linux only).
        /// </summary>
        private void OnInfoBlockWebViewUnloaded(object? sender, EventArgs e)
        {
#if WINDOWS || LINUX
            _infoBlockPreview?.Dispose();
            _infoBlockPreview = null;
#endif
#if LINUX
            _linuxPreviewBrowser = null;
#endif
        }

        /// <summary>
        /// Keeps external links out of the embedded preview: navigation to any http or https address other than the
        /// selected block is canceled and the address is opened in the system browser instead.
        /// </summary>
        private async void OnInfoBlockWebViewNavigating(object? sender, WebNavigatingEventArgs e)
        {
            // The selected block itself (and anything that is not an absolute URL) loads in the preview.
            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || IsSelectedInfoBlockUrl(e.Url))
                return;

            if (uri.Scheme == "http" || uri.Scheme == "https")
            {
                e.Cancel = true;
                try { await Launcher.Default.OpenAsync(uri); }
                catch
                {
                    // Best effort: the navigation is already canceled, and a failing browser launch has no fallback.
                }
            }
        }

        /// <summary>
        /// Reattaches the preview host after the selected block finished loading successfully and, on Mac Catalyst,
        /// takes the content height from the native web view.
        /// </summary>
        private void OnInfoBlockWebViewNavigated(object? sender, WebNavigatedEventArgs e)
        {
            // Only a successful load of the selected block content is handled.
            if (e.Result != WebNavigationResult.Success || !IsSelectedInfoBlockUrl(e.Url)) return;
            OnInfoBlockWebViewHandlerChanged(sender, EventArgs.Empty);
#if MACCATALYST
            // Preserve the existing one-shot fallback using WebKit's native content size.
            if (sender is WebView view && view.Handler?.PlatformView is WebKit.WKWebView native)
                SelectedInfoBlockWebHeight = native.ScrollView.ContentSize.Height;
#endif
        }

        /// <summary>
        /// Returns whether a URL is the address of the selected information block content.
        /// </summary>
        private bool IsSelectedInfoBlockUrl(string? url) =>
            SelectedInfoBlockSource is UrlWebViewSource selected &&
            Uri.TryCreate(selected.Url, UriKind.Absolute, out var expected) &&
            Uri.TryCreate(url, UriKind.Absolute, out var actual) && expected.Equals(actual);

        /// <summary>
        /// Cancels all active refresh work before closing the dialog. While the module information overlay is
        /// shown, only that overlay is closed.
        /// </summary>
        private void RequestClose()
        {
            if (ModuleInfoHost.IsVisible)
            {
                _moduleInfo.RequestClose();
                return;
            }

            // Closing the dialog: keep the selection for the next opening, reject late results and stop all work.
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
            // Category header: identity, texts and sort order.
            var builder = new StringBuilder();
            AppendSignatureSegment(builder, category.GroupKey);
            AppendSignatureSegment(builder, category.Title);
            AppendSignatureSegment(builder, category.Description);
            builder.Append(category.SortOrder).Append(';');
            builder.Append(category.Filters.Count).Append(';');

            // Every filter with its selection and order.
            foreach (var filter in category.Filters)
            {
                AppendSignatureSegment(builder, filter.Key);
                AppendSignatureSegment(builder, filter.Title);
                AppendSignatureSegment(builder, filter.Kind);
                builder.Append(filter.Selected ? '1' : '0').Append(';');
                builder.Append(filter.SortOrder).Append(';');
            }

            // Every item with all fields that the list shows, including installation state and the sources that provide it.
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
            // Item identity, metadata and the default variant.
            var builder = new StringBuilder();
            AppendSignatureSegment(builder, detail.ResourceKey);
            AppendMetadataSignature(builder, detail.Details);
            AppendMetadataSignature(builder, detail.Tags);
            AppendSignatureSegment(builder, detail.DefaultVariantResourceKey);
            builder.Append(detail.Variants.Count).Append(';');

            // Every variant with its size and installation state.
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

            // Every information block with its content or content location.
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

        /// <summary>
        /// Appends an unambiguous length-prefixed signature segment; a null value is written as a separate marker.
        /// </summary>
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
        internal static string FormatDownloadSize(long bytes)
        {
            // Step up a unit while the value, rounded to two decimals, would still show 1000 or more.
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
            // Nothing to size before the first layout pass.
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
        /// Raises detail-level properties after rebuilding the detail and tag fields of the selected item
        /// from the loaded detail, or from the list entry while the detail is not loaded.
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
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(HasSelectedVariant));
            OnPropertyChanged(nameof(SelectedVariantCard));
            OnPropertyChanged(nameof(SelectedVariantTitle));
            OnPropertyChanged(nameof(SelectedVariantSummary));
            OnPropertyChanged(nameof(HasSelectedVariantSummary));
            OnPropertyChanged(nameof(ShowInstallButton));
            OnPropertyChanged(nameof(ShowDeleteButton));
            OnPropertyChanged(nameof(ShowCancelButton));
            foreach (var variant in Variants) variant.RefreshLocalizedText();
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


        // Category and filter types

        /// <summary>
        /// Keeps a category's query and last selection for the lifetime of the overlay, without disk storage.
        /// </summary>
        private sealed class DownloadCategoryState
        {
            /// <summary>
            /// Search text of the category.
            /// </summary>
            public string SearchText { get; set; } = string.Empty;

            /// <summary>
            /// Keys of the filters selected in the category.
            /// </summary>
            public HashSet<string> FilterKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Whether the category must be queried again, because its query changed or it has not been loaded yet.
            /// </summary>
            public bool NeedsRefresh { get; set; } = true;

            /// <summary>
            /// Counter that is increased by every query change and every category query; a result is dropped when the counter moved after the request started.
            /// </summary>
            public int QueryVersion { get; set; }

            /// <summary>
            /// Resource key of the item that was selected when the category was left.
            /// </summary>
            public string? SelectedItemKey { get; set; }

            /// <summary>
            /// Resource key of the variant that was selected when the category was left.
            /// </summary>
            public string? SelectedVariantKey { get; set; }

            /// <summary>
            /// Loaded detail of the selected item, restored without a new request when the category is shown again.
            /// </summary>
            public DownloadCatalogItemDetail? Detail { get; set; }
        }

        /// <summary>
        /// Represents one selectable category in the sidebar.
        /// </summary>
        public sealed class DownloadCategoryViewModel : INotifyPropertyChanged
        {
            // Fields

            /// <summary>
            /// Action that makes this category the active one.
            /// </summary>
            private readonly Func<DownloadCategoryViewModel, Task> _selectAction;

            /// <summary>
            /// Whether the category is the active one in the sidebar.
            /// </summary>
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


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Category data; replaced by <see cref="Update"/> when a refresh brings changes.
            /// </summary>
            public DownloadCatalogCategory Category { get; private set; }

            /// <summary>
            /// Title shown in the sidebar.
            /// </summary>
            public string Title => Category.Title;

            /// <summary>
            /// Description of the category.
            /// </summary>
            public string Description => Category.Description;

            /// <summary>
            /// Whether the category has a description.
            /// </summary>
            public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

            /// <summary>
            /// Command that activates the category.
            /// </summary>
            public ICommand SelectCommand { get; }

            /// <summary>
            /// Replaces the category data when its signature shows a real change, so unchanged refreshes keep the page as it is.
            /// </summary>
            /// <param name="category">Freshly loaded category.</param>
            /// <returns>True when the data changed; false when the new data is identical.</returns>
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

            /// <summary>
            /// Whether the category is the active one in the sidebar.
            /// </summary>
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
            // Fields

            /// <summary>
            /// Action that toggles this filter in the active category.
            /// </summary>
            private readonly Func<DownloadFilterViewModel, Task> _toggleAction;

            /// <summary>
            /// Whether the filter is selected.
            /// </summary>
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


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Key that identifies the filter in the provider query.
            /// </summary>
            public string Key { get; }

            /// <summary>
            /// Caption of the filter chip.
            /// </summary>
            public string Title { get; }

            /// <summary>
            /// Filter kind; "sort" filters are single-select, all other kinds can be combined.
            /// </summary>
            public string Kind { get; }

            /// <summary>
            /// Command that toggles the filter.
            /// </summary>
            public ICommand ToggleCommand { get; }

            /// <summary>
            /// Whether the filter is selected.
            /// </summary>
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


        // Item, variant and detail types

        /// <summary>
        /// Represents one grouped item card in the center list.
        /// </summary>
        public sealed class DownloadListItemViewModel : INotifyPropertyChanged
        {
            // Fields

            /// <summary>
            /// Action that selects this item and loads its detail.
            /// </summary>
            private readonly Func<DownloadListItemViewModel, Task> _selectAction;

            /// <summary>
            /// Whether the item is the selected one in the list.
            /// </summary>
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one grouped item to its select action and prepares the tags that are shown on the list card.
            /// </summary>
            public DownloadListItemViewModel(DownloadCatalogItem item, Func<DownloadListItemViewModel, Task> selectAction)
            {
                Item = item;
                _selectAction = selectAction;
                CatalogTags = CreateFields(item.Tags.Where(tag => tag.ShowInCatalog && tag.Image != null));
                SelectCommand = new Command(async () => await _selectAction(this));
            }


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Catalog item that the card represents.
            /// </summary>
            public DownloadCatalogItem Item { get; }

            /// <summary>
            /// Title of the item.
            /// </summary>
            public string Title => Item.Title;

            /// <summary>
            /// Short description of the item.
            /// </summary>
            public string Summary => Item.Summary;

            /// <summary>
            /// Whether the item has a description.
            /// </summary>
            public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

            /// <summary>
            /// Tags with an icon that are shown on the card; tags without an icon or not meant for the catalog are left out.
            /// </summary>
            public IReadOnlyList<DownloadFieldViewModel> CatalogTags { get; }

            /// <summary>
            /// Whether the card shows tags.
            /// </summary>
            public bool HasCatalogTags => CatalogTags.Count > 0;

            /// <summary>
            /// Command that selects the item.
            /// </summary>
            public ICommand SelectCommand { get; }

            /// <summary>
            /// Whether the item is the selected one in the list.
            /// </summary>
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
            /// One-line summary of provider, version and the details that are meant for the catalog, separated by pipes.
            /// </summary>
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

            /// <summary>
            /// Whether the metadata line has any text.
            /// </summary>
            public bool HasMetadata => !string.IsNullOrWhiteSpace(MetadataLine);


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
        /// Represents one installable variant in the selector list.
        /// </summary>
        public sealed class DownloadVariantViewModel : INotifyPropertyChanged
        {
            // Fields

            /// <summary>
            /// Action that makes this variant the selected one.
            /// </summary>
            private readonly Action<DownloadVariantViewModel?> _selectAction;

            /// <summary>
            /// Installer queried for the running operation of this variant.
            /// </summary>
            private readonly DownloadInstaller _installer;

            /// <summary>
            /// Whether the variant is the selected one.
            /// </summary>
            private bool _isSelected;


            // Initialization

            /// <summary>
            /// Binds one variant to its select action.
            /// </summary>
            public DownloadVariantViewModel(DownloadCatalogVariant variant, Action<DownloadVariantViewModel?> selectAction, DownloadInstaller installer)
            {
                Variant = variant;
                _selectAction = selectAction;
                _installer = installer;
                SelectCommand = new Command(() => _selectAction(this));
            }


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Catalog variant that the entry represents.
            /// </summary>
            public DownloadCatalogVariant Variant { get; }

            /// <summary>
            /// Title of the variant.
            /// </summary>
            public string Title => Variant.Title;

            /// <summary>
            /// Short description of the variant.
            /// </summary>
            public string Summary => Variant.Summary;

            /// <summary>
            /// Whether the variant has a description.
            /// </summary>
            public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

            /// <summary>
            /// Formatted download size, or a dash when the size is unknown.
            /// </summary>
            public string SizeLabel => Variant.HasSize ? FormatDownloadSize(Variant.Size) : "-";

            /// <summary>
            /// Whether the variant is installed and the installer has no running operation for it.
            /// </summary>
            public bool IsDownloaded => Variant.Installed && _installer.GetState(Variant.ResourceKey) == null;

            /// <summary>
            /// Whether a status text is shown: the variant is installed or has a queued, running or removing operation.
            /// </summary>
            public bool HasStatus => Variant.Installed || _installer.GetState(Variant.ResourceKey) != null;

            /// <summary>
            /// Localized status: the state of the running operation, otherwise "downloaded" for an installed variant.
            /// </summary>
            public string StatusText => _installer.GetState(Variant.ResourceKey) switch
            {
                DownloadOperationState.Queued => L.Get(LocalizationKeys.Downloads_Queued),
                DownloadOperationState.Running => L.Get(LocalizationKeys.Settings_Downloading),
                DownloadOperationState.Removing => L.Get(LocalizationKeys.Modules_Removing),
                _ => Variant.Installed ? DownloadedLabel : string.Empty
            };

            /// <summary>
            /// Localized "downloaded" label.
            /// </summary>
            public string DownloadedLabel => L.Get(LocalizationKeys.Downloads_Downloaded);

            /// <summary>
            /// Command that selects the variant.
            /// </summary>
            public ICommand SelectCommand { get; }

            /// <summary>
            /// Whether the variant is the selected one.
            /// </summary>
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
            /// Raises the notifications for every text and state that depends on the language or on the installer.
            /// </summary>
            public void RefreshLocalizedText()
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(IsDownloaded));
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

        /// <summary>
        /// Wraps a list of module-supplied fields (details or tags) into view models.
        /// </summary>
        private static IReadOnlyList<DownloadFieldViewModel> CreateFields(IEnumerable<ModuleDownloadField> fields) =>
            fields.Select(field => new DownloadFieldViewModel(field)).ToArray();

        /// <summary>
        /// Represents one detail or tag value with the optional colors and icon that the module supplied.
        /// </summary>
        public sealed class DownloadFieldViewModel : INotifyPropertyChanged
        {
            // Fields

            /// <summary>
            /// Icon supplied by the module, or null when the field has none.
            /// </summary>
            private readonly DownloadCatalogIcon? _image;


            // Initialization

            /// <summary>
            /// Creates the view model from a module-supplied field and converts its color strings.
            /// </summary>
            public DownloadFieldViewModel(ModuleDownloadField value)
            {
                Text = value.Text;

                // These are module-supplied values resolved by the bridge, not ASLM style defaults.
                if (value.BackgroundColor != null) Background = new SolidColorBrush(Color.FromArgb(value.BackgroundColor));
                if (value.TextColor != null) TextColor = Color.FromArgb(value.TextColor);
                _image = value.Image;
            }


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Text of the field.
            /// </summary>
            public string Text { get; }

            /// <summary>
            /// Whether the field has text.
            /// </summary>
            public bool HasText => Text.Length > 0;

            /// <summary>
            /// Background brush supplied by the module, or null for the default look.
            /// </summary>
            public Brush? Background { get; }

            /// <summary>
            /// Text color supplied by the module, or null for the default color.
            /// </summary>
            public Color? TextColor { get; }

            /// <summary>
            /// Icon tinted with the module's text color, or with the primary label color when the module supplied none.
            /// </summary>
            public ImageSource? Icon => _image == null ? null : PackagedIconTintCache.Get(
                _image.Data, TextColor ?? IconTintHelper.ResolvePaletteColor("LabelPrimary"));

            /// <summary>
            /// Whether the module supplied a background.
            /// </summary>
            public bool HasBackground => Background != null;

            /// <summary>
            /// Whether the module supplied a text color.
            /// </summary>
            public bool HasTextColor => TextColor != null;

            /// <summary>
            /// Whether the field has an icon.
            /// </summary>
            public bool HasIcon => _image != null;

            /// <summary>
            /// Reevaluates the icon after a theme change; an icon with a module-supplied text color keeps its tint.
            /// </summary>
            public void RefreshIcon()
            {
                if (HasIcon && !HasTextColor)
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }


        // Information block types

        /// <summary>
        /// Represents one details tab in the right panel.
        /// </summary>
        public sealed class DownloadInfoBlockViewModel : INotifyPropertyChanged
        {
            // Fields

            /// <summary>
            /// Action that selects this block and shows its content.
            /// </summary>
            private readonly Func<DownloadInfoBlockViewModel, Task> _selectAction;

            /// <summary>
            /// Homepage URL of the item, used to resolve relative links in the rendered text; may be null.
            /// </summary>
            private readonly string? _baseUrl;

            /// <summary>
            /// Whether the block is the selected tab.
            /// </summary>
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


            // Bindable state

            /// <summary>
            /// Raised when a bound property changes.
            /// </summary>
            public event PropertyChangedEventHandler? PropertyChanged;

            /// <summary>
            /// Catalog information block that the tab represents.
            /// </summary>
            public DownloadCatalogInfoBlock Block { get; }

            /// <summary>
            /// Title of the tab; the generic details title when the block has none.
            /// </summary>
            public string Title => string.IsNullOrWhiteSpace(Block.Title) ? L.Get(LocalizationKeys.Downloads_Details) : Block.Title;

            /// <summary>
            /// Plain-text rendering of the block content; empty when the block is shown as web content or has no content.
            /// </summary>
            public string RenderedContent { get; private set; }

            /// <summary>
            /// Location of the web content of the block, or an empty string.
            /// </summary>
            public string ContentUrl => Block.ContentUrl;

            /// <summary>
            /// Address the block was loaded from; relative content locations are resolved against it.
            /// </summary>
            public string SourceUrl => Block.SourceUrl;

            /// <summary>
            /// Whether the block has rendered text.
            /// </summary>
            public bool HasTextContent => !string.IsNullOrWhiteSpace(RenderedContent);

            /// <summary>
            /// Whether the block points at web content.
            /// </summary>
            public bool HasContentUrl => !string.IsNullOrWhiteSpace(ContentUrl);

            /// <summary>
            /// Command that selects the block.
            /// </summary>
            public ICommand SelectCommand { get; }

            /// <summary>
            /// Whether the block is the selected tab.
            /// </summary>
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
            /// Renders the block text again and raises the notifications of the texts that depend on the language.
            /// </summary>
            public void RefreshLocalizedText()
            {
                RenderedContent = ResolveRenderedContent(Block, _baseUrl);
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(RenderedContent));
                OnPropertyChanged(nameof(HasTextContent));
            }


            // Content resolution

            /// <summary>
            /// Returns the plain-text rendering of a block, or an empty string when the block points at dedicated
            /// web content (HTML file, HTML URL or web format) that is shown in the web view instead.
            /// </summary>
            private static string ResolveRenderedContent(DownloadCatalogInfoBlock block, string? baseUrl)
            {
                // Blocks that point at web content are displayed in the web view, so they get no text rendering.
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


        // Information block text rendering

        /// <summary>
        /// Converts text-based blocks into a readable plain-text fallback.
        /// </summary>
        private static class InfoBlockTextFormatter
        {
            // Patterns

            /// <summary>
            /// Matches a complete HTML script element including its content.
            /// </summary>
            private static readonly Regex ScriptRegex = new(
                "<script\\b[^<]*(?:(?!</script>)<[^<]*)*</script>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

            /// <summary>
            /// Matches a fenced markdown code block with an optional language name.
            /// </summary>
            private static readonly Regex FencedCodeRegex = new(
                "```(?<lang>[a-zA-Z0-9_+-]*)\\s*\\n(?<code>.*?)```",
                RegexOptions.Singleline | RegexOptions.Compiled);

            /// <summary>
            /// Matches a markdown heading line and captures the hashes and the heading text.
            /// </summary>
            private static readonly Regex HeadingRegex = new(
                "^(#{1,6})\\s+(.*)$",
                RegexOptions.Compiled);

            /// <summary>
            /// Matches a markdown bullet list line and captures the item text.
            /// </summary>
            private static readonly Regex UnorderedListRegex = new(
                "^\\s*[-*+]\\s+(.*)$",
                RegexOptions.Compiled);

            /// <summary>
            /// Matches a markdown numbered list line and captures the item text.
            /// </summary>
            private static readonly Regex OrderedListRegex = new(
                "^\\s*\\d+\\.\\s+(.*)$",
                RegexOptions.Compiled);

            /// <summary>
            /// Matches a markdown block quote line and captures the quoted text.
            /// </summary>
            private static readonly Regex BlockquoteRegex = new(
                "^\\s*>\\s?(.*)$",
                RegexOptions.Compiled);

            /// <summary>
            /// Matches a markdown horizontal rule line of three or more dashes, asterisks or underscores.
            /// </summary>
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

                // Unknown formats are plain text; a failing converter falls back to plain text as well.
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
                    // The converters are best effort; the unconverted text is still readable.
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

                // Walk the text line by line; consecutive plain lines are collected into one paragraph.
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

                    // Lines inside a code block are kept as they are until the closing fence.
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

                    // Anything else is paragraph text.
                    paragraphLines.Add(line.Trim());
                }

                // Output what is still pending, including a code block that was never closed.
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
                // Remove scripts, then turn block-level tags into line breaks and list or code markers.
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

                // Images become a text placeholder (with the alt text or the resolved address).
                sanitized = Regex.Replace(sanitized, "(?i)<img\\b[^>]*alt\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>",
                    match => L.Get(LocalizationKeys.Downloads_ImagePlaceholderFormat, match.Groups[1].Value));
                sanitized = Regex.Replace(sanitized, "(?i)<img\\b[^>]*src\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>", match =>
                {
                    var url = AbsolutizeUrl(match.Groups[1].Value, baseUrl);
                    return string.IsNullOrWhiteSpace(url)
                        ? L.Get(LocalizationKeys.Downloads_ImagePlaceholder)
                        : L.Get(LocalizationKeys.Downloads_ImagePlaceholderFormat, url);
                });

                // Links keep their text and show the resolved address after it.
                sanitized = Regex.Replace(sanitized, "(?i)<a\\b[^>]*href\\s*=\\s*['\"]([^'\"]*)['\"][^>]*>(.*?)</a>", match =>
                {
                    var href = AbsolutizeUrl(WebUtility.HtmlDecode(match.Groups[1].Value), baseUrl);
                    var label = ConvertHtmlToText(match.Groups[2].Value, baseUrl);
                    return string.IsNullOrWhiteSpace(href) ? label : $"{label} ({href})";
                }, RegexOptions.Singleline);

                // Strip the remaining tags, decode entities and collapse the surplus whitespace.
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

                // Images become a placeholder, links keep their label and show the resolved address.
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

                // Remove bold, italic and inline code markers and keep the text between them.
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

                // Absolute addresses stay as they are; relative ones are combined with the base URL when there is one.
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
