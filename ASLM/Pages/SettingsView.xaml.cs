// Copyright NEXTGGTECH. Apache License 2.0.

using Debug = System.Diagnostics.Debug;
using ASLM.Controls.Settings;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Coordinates declarative application and module settings views inside the shell.
    /// </summary>
    public partial class SettingsView : ContentView, ILocalizable
    {
        private const double DialogWidthFactor = 0.8;
        private const double DialogHeightFactor = 0.8;
        private const double MinDialogWidth = 960;
        private const double MinDialogHeight = 540;
        private const double MaxDialogWidth = 1280;
        private const double MaxDialogHeight = 720;
        private const string GitHubHomeUrl = "https://github.com/";
        private const string OllamaSettingsUrl = "https://ollama.com/settings";
        private static readonly TimeSpan OllamaSignInPollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan OllamaSignInPollDuration = TimeSpan.FromMinutes(5);

        private const string FooterButtonStyleKey = "SettingsFooterButtonStyle";
        private const string FooterPrimaryButtonStyleKey = "SettingsFooterPrimaryButtonStyle";
        private const string FooterDangerButtonStyleKey = "SettingsFooterDangerButtonStyle";
        private readonly AppDataStore _appData;
        private readonly SettingsService _settingsService;
        private readonly AppLocalizationService _localization;
        private readonly OllamaSettingsStore _ollamaSettings;
        private readonly GitHubAccountStore _githubAccountStore;
        private readonly GitHubRateLimitStore _githubRateLimitStore;
        private readonly GitHubUpdateClient _githubUpdateClient;
        private readonly UpdateManager _updateManager;
        private readonly UpdateScheduler _updateScheduler;
        private readonly EngineInstaller _engineInstaller;
        private readonly AslmMirrorServer _mirrorServer;
        private readonly NotificationCenter _notifications;
        private readonly ThemeService _themeService;
        private readonly CustomThemesStore _customThemesStore;
        private readonly SunriseService _sunriseService;
        private readonly SettingsEditSession _editSession = new();
        private readonly Dictionary<string, ModuleSettingsPageViewModel> _moduleSettingsPresentations =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModuleSettingsView> _moduleSettingsViews =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _moduleSettingsPresentationsNeedingRefresh =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly SettingsCategorySidebarViewModel _categoryPresentation;
        private readonly HashSet<string> _runtimeLoadedModuleIds = new(StringComparer.OrdinalIgnoreCase);
        private List<ModuleConfig> _loadedModules = [];
        private List<SettingsCategory> _categories = [];
        private SettingsCategory? _activeCategory;
        private OllamaPersistentSettings _ollamaDraft = new();
        private GitHubAccountState _githubDraft = new();
        private bool _hasLoaded;
        private bool _isLoading;
        private bool _isSwitchingCategory;
        private bool _isSaving;
        private bool _isOllamaAccountActionRunning;
        private bool _isOllamaMetadataRefreshRunning;
        private bool _isGitHubAccountActionRunning;
        private bool _isAslmAccountActionRunning;
        private bool _isUpdateSchedulerSubscribed;
        private int _actionButtonUpdateQueued;
        private int _moduleSettingsWarmupGeneration;
        private string _ollamaAccountAction = string.Empty;
        private Button? _ollamaAccountButton;
        private Label? _ollamaAccountStatusLabel;
        private Button? _githubAccountButton;
        private Label? _githubAccountStatusLabel;
        private Button? _aslmAccountButton;
        private Label? _aslmAccountStatusLabel;
        private SettingsToggle? _checkUpdatesToggle;
        private SettingsToggle? _autoUpdatesToggle;
        private Picker? _appUpdateChannelPicker;
        private Picker? _moduleUpdateChannelPicker;
        private Label? _aslmInstalledVersionLabel;
        private Label? _aslmAvailableVersionLabel;
        private HorizontalStackLayout? _aslmUpdateActionHost;
        private Grid? _prepareAppUpdateHost;
        private Button? _prepareAppUpdateButton;
        private HorizontalStackLayout? _prepareAppUpdateProgress;
        private ActivityIndicator? _prepareAppUpdateSpinner;
        private Label? _prepareAppUpdateProgressPercent;
        private Button? _restartAppUpdateButton;
        private UpdateCandidate? _pendingAppUpdateCandidate;
        private Grid? _ollamaUpdateHost;
        private Button? _ollamaUpdateButton;
        private HorizontalStackLayout? _ollamaUpdateProgress;
        private ActivityIndicator? _ollamaUpdateSpinner;
        private Label? _ollamaUpdateProgressPercent;
        private Label? _ollamaInstalledVersionLabel;
        private Label? _ollamaAvailableVersionLabel;
        private UpdateCandidate? _pendingOllamaUpdateCandidate;
        private SettingsToggle? _apiServerToggle;
        private SettingsToggle? _consoleSidebarToggle;
        private SettingsToggle? _consoleCompletedToggle;
        private SettingsToggle? _consoleIndividualToggle;
        private SettingsToggle? _legalAutoAcceptToggle;
        private SettingsToggle? _restoreLastPageToggle;
        private CancellationTokenSource? _ollamaMetadataRefreshCts;
        private CancellationTokenSource? _ollamaStatusPollingCts;
        private CancellationTokenSource? _aslmAccountActionCts;
        private int _builtInControlStateApplicationDepth;

        private CustomTheme? _editingThemeDraft;
        private Picker? _appearancePicker;
        private Picker? _languagePicker;
        private VerticalStackLayout? _customThemeSection;
        private Picker? _customThemePicker;
        private SettingsSectionView? _themeEditorSection;
        private bool _suppressCustomThemePickerEvents;
        private bool _personalizationControlsInitialized;

        /// <summary>Gets whether built-in controls are being updated from draft state.</summary>
        private bool IsApplyingBuiltInControlState => _builtInControlStateApplicationDepth > 0;

        /// <summary>Gets the stable display-name input declared by the built-in settings view.</summary>
        private Entry UsernameEntry => BuiltInSettingsContainer.UserNameInput;

        /// <summary>Gets the stable module-port input declared by the built-in settings view.</summary>
        private Entry ModulePortEntry => BuiltInSettingsContainer.ModulePortInput;

        /// <summary>Gets the stable module-port validation label.</summary>
        private Label PortErrorLabel => BuiltInSettingsContainer.ModulePortError;

        /// <summary>Gets the stable port settings section.</summary>
        private VerticalStackLayout PortsSection => BuiltInSettingsContainer.PortsHost;

        /// <summary>Gets the stable profile settings section.</summary>
        private VerticalStackLayout UserProfileSection => BuiltInSettingsContainer.UserProfile;

        /// <summary>Gets or replaces the editable ASLM baseline stored by the session.</summary>
        private AslmBaseline _aslmBaseline
        {
            get => _editSession.Application.AslmBaseline;
            set => _editSession.Application.AslmBaseline = value;
        }

        /// <summary>Gets or replaces the editable console baseline stored by the session.</summary>
        private ConsoleBaseline _consoleBaseline
        {
            get => _editSession.Application.ConsoleBaseline;
            set => _editSession.Application.ConsoleBaseline = value;
        }

        /// <summary>Gets or replaces the editable update baseline stored by the session.</summary>
        private UpdateBaseline _updateBaseline
        {
            get => _editSession.Application.UpdateBaseline;
            set => _editSession.Application.UpdateBaseline = value;
        }

        /// <summary>Gets or replaces the console draft stored by the session.</summary>
        private ConsoleBaseline _consoleDraft
        {
            get => _editSession.Application.Console;
            set => _editSession.Application.Console = value;
        }

        /// <summary>Gets or replaces the update draft stored by the session.</summary>
        private UpdateBaseline _updateDraft
        {
            get => _editSession.Application.Update;
            set => _editSession.Application.Update = value;
        }

        /// <summary>Gets or replaces the user-name draft stored by the session.</summary>
        private string _userNameDraft
        {
            get => _editSession.Application.UserName;
            set => _editSession.Application.UserName = value;
        }

        /// <summary>Gets or replaces the port draft stored by the session.</summary>
        private string _portStartDraft
        {
            get => _editSession.Application.PortStart;
            set => _editSession.Application.PortStart = value;
        }

        /// <summary>Gets or replaces the API-server draft stored by the session.</summary>
        private bool _apiServerEnabledDraft
        {
            get => _editSession.Application.ApiServerEnabled;
            set => _editSession.Application.ApiServerEnabled = value;
        }

        /// <summary>Gets or replaces the legal draft stored by the session.</summary>
        private bool _legalAutoAcceptDraft
        {
            get => _editSession.Application.LegalAutoAcceptUpdates;
            set => _editSession.Application.LegalAutoAcceptUpdates = value;
        }

        /// <summary>Gets or replaces the legal baseline stored by the session.</summary>
        private bool _legalAutoAcceptBaseline
        {
            get => _editSession.Application.LegalAutoAcceptBaseline;
            set => _editSession.Application.LegalAutoAcceptBaseline = value;
        }

        /// <summary>Gets or replaces the last-page restoration draft stored by the session.</summary>
        private bool _restoreLastPageDraft
        {
            get => _editSession.Application.RestoreLastPage;
            set => _editSession.Application.RestoreLastPage = value;
        }

        /// <summary>Gets or replaces the personalization draft stored by the session.</summary>
        private AppPersonalizationConfig _personalizationDraft
        {
            get => _editSession.Application.Personalization;
            set => _editSession.Application.Personalization = value;
        }

        /// <summary>Gets or replaces the personalization baseline stored by the session.</summary>
        private AppPersonalizationConfig _personalizationBaseline
        {
            get => _editSession.Application.PersonalizationBaseline;
            set => _editSession.Application.PersonalizationBaseline = value;
        }

        /// <summary>
        /// Raised when the user asks to close the settings overlay.
        /// </summary>
        public event EventHandler? CloseRequested;

        // Initialization

        /// <summary>
        /// Creates the settings view and hooks the first-load handler.
        /// </summary>
        public SettingsView(
            AppDataStore appData,
            SettingsService settingsService,
            AppLocalizationService localization,
            OllamaSettingsStore ollamaSettings,
            GitHubAccountStore githubAccountStore,
            GitHubRateLimitStore githubRateLimitStore,
            GitHubUpdateClient githubUpdateClient,
            UpdateManager updateManager,
            UpdateScheduler updateScheduler,
            EngineInstaller engineInstaller,
            AslmMirrorServer mirrorServer,
            NotificationCenter notifications,
            ThemeService themeService,
            CustomThemesStore customThemesStore,
            SunriseService sunriseService)
        {
            _appData = appData;
            _settingsService = settingsService;
            _localization = localization;
            _ollamaSettings = ollamaSettings;
            _githubAccountStore = githubAccountStore;
            _githubRateLimitStore = githubRateLimitStore;
            _githubUpdateClient = githubUpdateClient;
            _updateManager = updateManager;
            _updateScheduler = updateScheduler;
            _engineInstaller = engineInstaller;
            _mirrorServer = mirrorServer;
            _notifications = notifications;
            _themeService = themeService;
            _customThemesStore = customThemesStore;
            _sunriseService = sunriseService;
            _categoryPresentation = new SettingsCategorySidebarViewModel(OnCategorySelectorRequested);
            InitializeComponent();
            CategorySelector.BindingContext = _categoryPresentation;
            InitializeBuiltInControlReferences();
            LocalizableAttach.Hook(this, _localization, this);
            ApplyScrollViewChrome(CategoryScroll, isSidebar: true);
            ApplyScrollViewChrome(SettingsScroll, isSidebar: false);
            UsernameEntry.TextChanged += (_, args) =>
            {
                _userNameDraft = args.NewTextValue?.Trim() ?? string.Empty;
                QueueActionButtonUpdate();
            };
            ModulePortEntry.TextChanged += (_, args) =>
            {
                _portStartDraft = args.NewTextValue?.Trim() ?? string.Empty;
                QueueActionButtonUpdate();
            };
            SizeChanged += OnViewSizeChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Connects page behavior to the reusable controls declared in the built-in XAML view.
        /// </summary>
        private void InitializeBuiltInControlReferences()
        {
            // Keep existing action logic independent from the concrete XAML host.
            _apiServerToggle = BuiltInSettingsContainer.ApiServerInput;
            _consoleSidebarToggle = BuiltInSettingsContainer.ConsoleSidebarInput;
            _consoleIndividualToggle = BuiltInSettingsContainer.ConsoleIndividualInput;
            _consoleCompletedToggle = BuiltInSettingsContainer.ConsoleCompletedInput;
            _legalAutoAcceptToggle = BuiltInSettingsContainer.LegalAutoAcceptInput;
            _restoreLastPageToggle = BuiltInSettingsContainer.RestoreLastPageInput;
            _aslmAccountButton = BuiltInSettingsContainer.AslmAccountAction;
            _aslmAccountStatusLabel = BuiltInSettingsContainer.AslmAccountState;
            _githubAccountButton = BuiltInSettingsContainer.GitHubAccountAction;
            _githubAccountStatusLabel = BuiltInSettingsContainer.GitHubAccountState;
            _ollamaAccountButton = BuiltInSettingsContainer.OllamaAccountAction;
            _ollamaAccountStatusLabel = BuiltInSettingsContainer.OllamaAccountState;
            _checkUpdatesToggle = BuiltInSettingsContainer.CheckUpdatesInput;
            _autoUpdatesToggle = BuiltInSettingsContainer.AutoUpdatesInput;
            _appUpdateChannelPicker = BuiltInSettingsContainer.AppChannelInput;
            _moduleUpdateChannelPicker = BuiltInSettingsContainer.ModuleChannelInput;
            _aslmInstalledVersionLabel = BuiltInSettingsContainer.AslmInstalledVersion;
            _aslmAvailableVersionLabel = BuiltInSettingsContainer.AslmAvailableVersion;
            _aslmUpdateActionHost = BuiltInSettingsContainer.AslmUpdateActionContainer;
            _prepareAppUpdateHost = BuiltInSettingsContainer.PrepareAppUpdateContainer;
            _prepareAppUpdateButton = BuiltInSettingsContainer.PrepareAppUpdateAction;
            _prepareAppUpdateProgress = BuiltInSettingsContainer.PrepareAppUpdateProgressContent;
            _prepareAppUpdateSpinner = BuiltInSettingsContainer.PrepareAppUpdateProgressSpinner;
            _prepareAppUpdateProgressPercent = BuiltInSettingsContainer.PrepareAppUpdateProgressValue;
            _restartAppUpdateButton = BuiltInSettingsContainer.RestartAppUpdateAction;
            _ollamaUpdateHost = BuiltInSettingsContainer.OllamaUpdateContainer;
            _ollamaUpdateButton = BuiltInSettingsContainer.OllamaUpdateAction;
            _ollamaUpdateProgress = BuiltInSettingsContainer.OllamaUpdateProgressContent;
            _ollamaUpdateSpinner = BuiltInSettingsContainer.OllamaUpdateProgressSpinner;
            _ollamaUpdateProgressPercent = BuiltInSettingsContainer.OllamaUpdateProgressValue;
            _ollamaInstalledVersionLabel = BuiltInSettingsContainer.OllamaInstalledVersion;
            _ollamaAvailableVersionLabel = BuiltInSettingsContainer.OllamaAvailableVersion;
            _languagePicker = BuiltInSettingsContainer.LanguageInput;
            _appearancePicker = BuiltInSettingsContainer.AppearanceInput;
            _customThemeSection = BuiltInSettingsContainer.CustomThemesHost;
            _themeEditorSection = BuiltInSettingsContainer.ThemeEditorHost;
            _customThemePicker = BuiltInSettingsContainer.CustomThemeInput;

            // Populate invariant choices once and wire all interaction handlers once.
            _appUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };
            _moduleUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };
            _apiServerToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleSidebarToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleIndividualToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleCompletedToggle.Toggled += OnAslmBuiltInToggleChanged;
            _legalAutoAcceptToggle.Toggled += OnAslmBuiltInToggleChanged;
            _restoreLastPageToggle.Toggled += OnAslmBuiltInToggleChanged;
            _checkUpdatesToggle.Toggled += OnUpdateControlChanged;
            _autoUpdatesToggle.Toggled += OnUpdateControlChanged;
            _appUpdateChannelPicker.SelectedIndexChanged += OnUpdateControlChanged;
            _moduleUpdateChannelPicker.SelectedIndexChanged += OnUpdateControlChanged;
            _languagePicker.SelectedIndexChanged += OnLanguagePickerChanged;
            _appearancePicker.SelectedIndexChanged += OnAppearancePickerChanged;
            _customThemePicker.SelectedIndexChanged += OnCustomThemePickerSelectionChanged;
            BuiltInSettingsContainer.BaseAppearanceInput.SelectedIndexChanged += OnBaseAppearancePickerChanged;
            _aslmAccountButton.Clicked += OnAslmAccountButtonClicked;
            BuiltInSettingsContainer.AslmAccountLink.Clicked += OnAslmAccountLinkClicked;
            _githubAccountButton.Clicked += OnGitHubAccountButtonClicked;
            BuiltInSettingsContainer.GitHubAccountLink.Clicked += OnGitHubAccountLinkClicked;
            _ollamaAccountButton.Clicked += OnOllamaAccountButtonClicked;
            BuiltInSettingsContainer.OllamaAccountLink.Clicked += OnOllamaAccountLinkClicked;
            _prepareAppUpdateButton.Clicked += OnPrepareAppUpdateClicked;
            _restartAppUpdateButton.Clicked += OnRestartNowClicked;
            _ollamaUpdateButton.Clicked += OnUpdateOllamaClicked;
            BuiltInSettingsContainer.CreateThemeAction.Clicked += OnCreateThemeClicked;
            BuiltInSettingsContainer.ImportThemeAction.Clicked += OnImportThemeClicked;
            BuiltInSettingsContainer.ExportThemeAction.Clicked += OnExportThemeClicked;
            BuiltInSettingsContainer.RenameThemeAction.Clicked += OnRenameCurrentCustomThemeClicked;
            BuiltInSettingsContainer.DeleteThemeAction.Clicked += OnDeleteCurrentCustomThemeClicked;
        }

        /// <summary>
        /// Synchronizes core toggles to drafts after a user change.
        /// </summary>
        private void OnAslmBuiltInToggleChanged(object? sender, ToggledEventArgs e)
        {
            if (IsApplyingBuiltInControlState)
            {
                return;
            }

            RefreshAslmApiAndConsoleDraftsFromToggles();
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Marks update settings dirty after a picker or toggle change.
        /// </summary>
        private void OnUpdateControlChanged(object? sender, EventArgs e)
        {
            if (!IsApplyingBuiltInControlState)
            {
                QueueActionButtonUpdate();
            }
        }


        // Refresh

        /// <summary>
        /// Reloads the settings page when the shell revisits it.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (!_hasLoaded || _isLoading)
            {
                return;
            }

            try
            {
                _isLoading = true;
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh settings view: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }


        // Overlay Events

        /// <summary>
        /// Closes the settings overlay when the user taps outside the dialog.
        /// </summary>
        private void OnBackgroundTapped(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Absorbs taps on the dialog surface so they do not reach the background handler.
        /// </summary>
        private void OnBorderTapped(object? sender, EventArgs e)
        {
            // Intentionally empty: prevents the tap from closing the overlay via the background handler.
        }

        /// <summary>
        /// Handles the close button and requests overlay dismissal.
        /// </summary>
        private void OnCloseClicked(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Stops background work, confirms discard when needed, and raises <see cref="CloseRequested"/>.
        /// </summary>
        private async void RequestClose()
        {
            if (!await ConfirmDiscardChangesIfNeededAsync())
            {
                return;
            }

            StopOllamaStatusPolling();
            StopOllamaMetadataRefresh();
            StopAslmAccountAction();
            _ollamaSettings.StopManagedRuntime();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }


        // Loading

        /// <summary>
        /// Loads shared settings, discovers modules, and restores the active category.
        /// </summary>
        private async Task LoadSettingsAsync()
        {
            var previousCategoryId = _activeCategory?.Id;

            LoadAslmDraftsFromAppData();
            LoadPersonalizationDraftsFromAppData();
            await Task.Run(LoadOllamaDraftsFromService);
            await LoadModuleDraftsAsync(reloadModules: true, reloadRuntimeValues: false);

            _categories = SettingsPresentationBuilder.BuildCategories(_loadedModules).ToList();

            var targetCategory = ResolveCategory(previousCategoryId) ?? _categories.FirstOrDefault();
            if (targetCategory == null)
            {
                _activeCategory = null;
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_NoSettingsAvailable));
                UpdateActionButtons();
                return;
            }

            BuildCategorySelectors();
            ActivateCategory(targetCategory);
            _ = WarmModuleSettingsSurfacesAsync();
        }

        /// <summary>
        /// Initializes the settings page once after the control is first shown.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            AttachAccountLinkThemeHandlers();
            AttachUpdateSchedulerHandler();

            if (_hasLoaded || _isLoading)
            {
                return;
            }

            UpdateDialogSize();

            try
            {
                _isLoading = true;
                await LoadSettingsAsync();
                _hasLoaded = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings view: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }

        /// <summary>
        /// Stops background Ollama status polling when the settings view leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            DetachAccountLinkThemeHandlers();
            DetachUpdateSchedulerHandler();
            StopOllamaStatusPolling();
            StopOllamaMetadataRefresh();
            StopAslmAccountAction();
            _ollamaSettings.StopManagedRuntime();
        }

        /// <inheritdoc />
        public void ApplyLocalization()
        {
            SettingsSidebarTitleLabel.Text = L.Get(LocalizationKeys.Settings_Title);
            ToolTipProperties.SetText(CloseSettingsButton, L.Get(LocalizationKeys.Settings_CloseTooltip));
            BuiltInSettingsContainer.ApplyLocalization();
            RefreshAslmVersionInformation();
            RefreshOllamaVersionInformation();

            DefaultButton.Text = L.Get(LocalizationKeys.Settings_LoadDefault);
            DiscardButton.Text = L.Get(LocalizationKeys.Settings_DiscardChanges);
            UpdateActionButtons();
            _personalizationControlsInitialized = false;
            ClearModuleSettingsSurfaceCache();

            if (_categories.Count > 0)
            {
                BuildCategorySelectors();
            }

            if (_activeCategory != null)
            {
                ActivateCategory(_activeCategory);
            }

            if (_hasLoaded)
            {
                _ = WarmModuleSettingsSurfacesAsync();
            }
        }

        /// <summary>
        /// Resolves the localized sidebar title for a settings category.
        /// </summary>
        private static string GetLocalizedCategoryTitle(SettingsCategory category) =>
            category.Kind switch
            {
                SettingsCategoryKind.Aslm => L.Get(LocalizationKeys.Settings_Category_ASLM),
                SettingsCategoryKind.Accounts => L.Get(LocalizationKeys.Settings_Category_Accounts),
                SettingsCategoryKind.Updates => L.Get(LocalizationKeys.Settings_Category_Updates),
                SettingsCategoryKind.Personalization => L.Get(LocalizationKeys.Settings_Category_Personalization),
                SettingsCategoryKind.Module => category.Module?.Name ?? category.Title,
                _ => category.Title
            };

        /// <summary>
        /// Builds the localized save result shown after application and module persistence.
        /// </summary>
        private static string BuildLocalizedSaveMessage(
            bool hadAnyPersistedSettingsChanges,
            bool touchedModules,
            IReadOnlyList<string> deferredSettings)
        {
            if (!hadAnyPersistedSettingsChanges && !touchedModules)
            {
                return L.Get(LocalizationKeys.Settings_SaveMessage_None);
            }

            if (deferredSettings.Count > 0)
            {
                var preview = string.Join("\n", deferredSettings.Take(5));
                return L.Get(LocalizationKeys.Settings_SaveMessage_Deferred, preview);
            }

            return L.Get(LocalizationKeys.Settings_SaveMessage_Saved);
        }

        private static readonly string[] AppearanceOptions = ["Dark", "Light", "System", "Custom"];

        /// <summary>
        /// Returns the picker display label for a supported language id.
        /// </summary>
        private static string GetLanguageDisplayName(string languageId) =>
            AppLocalizationService.GetPickerDisplayName(languageId);

        /// <summary>
        /// Returns the localized picker label for an appearance mode id.
        /// </summary>
        private static string GetAppearanceDisplayName(string appearance) =>
            AppPersonalizationConfig.NormalizeAppearance(appearance) switch
            {
                "Light" => L.Get(LocalizationKeys.Settings_Personalization_Appearance_Light),
                "System" => L.Get(LocalizationKeys.Settings_Personalization_Appearance_System),
                "Custom" => L.Get(LocalizationKeys.Settings_Personalization_Appearance_Custom),
                _ => L.Get(LocalizationKeys.Settings_Personalization_Appearance_Dark)
            };

        /// <summary>
        /// Maps a localized appearance picker label back to its canonical id.
        /// </summary>
        private static string ResolveAppearanceFromDisplayName(string? displayName)
        {
            foreach (var appearance in AppearanceOptions)
            {
                if (string.Equals(GetAppearanceDisplayName(appearance), displayName, StringComparison.Ordinal))
                {
                    return appearance;
                }
            }

            return "Dark";
        }

        /// <summary>
        /// Keeps the dialog within the requested min/max bounds while scaling to the host size.
        /// </summary>
        private void OnViewSizeChanged(object? sender, EventArgs e)
        {
            UpdateDialogSize();
        }

        /// <summary>
        /// Applies the responsive dialog size using 80 percent of the available overlay area.
        /// </summary>
        private void UpdateDialogSize()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            SettingsDialog.WidthRequest = ClampDialogSize(Math.Floor(Width * DialogWidthFactor), MinDialogWidth, MaxDialogWidth);
            SettingsDialog.HeightRequest = ClampDialogSize(Math.Floor(Height * DialogHeightFactor), MinDialogHeight, MaxDialogHeight);
        }

        /// <summary>
        /// Restricts one calculated dialog dimension to its supported bounds.
        /// </summary>
        private static double ClampDialogSize(double value, double min, double max) =>
            Math.Max(min, Math.Min(max, value));


        // Loading helpers

        /// <summary>
        /// Copies the persisted personalization settings into the editable page draft.
        /// </summary>
        private void LoadPersonalizationDraftsFromAppData()
        {
            var stored = _appData.Data.Personalization;
            _editSession.Application.LoadPersonalization(new AppPersonalizationConfig
            {
                Appearance = AppPersonalizationConfig.NormalizeAppearance(stored.Appearance),
                Language = AppPersonalizationConfig.NormalizeLanguage(stored.Language),
                CustomThemeId = stored.CustomThemeId
            });
            _personalizationControlsInitialized = false;
        }

        /// <summary>
        /// Copies the persisted shared settings into the editable page draft.
        /// </summary>
        private void LoadAslmDraftsFromAppData()
        {
            var snapshot = SettingsService.BuildAslmDraftSnapshot(_appData, _mirrorServer.IsEnabled);
            _editSession.Application.LoadAslm(snapshot, _appData.Data.Legal.AutoAcceptUpdates);

            ApplyAslmDraftsToControls();
            ResetUpdateActionControls();
            PortErrorLabel.IsVisible = false;
        }

        /// <summary>
        /// Copies the persisted Ollama settings into the editable page draft.
        /// </summary>
        private void LoadOllamaDraftsFromService()
        {
            try
            {
                _ollamaDraft = _ollamaSettings.LoadSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load Ollama settings: {ex.Message}");
                _ollamaDraft = new OllamaPersistentSettings();
            }
        }

        /// <summary>
        /// Reloads module settings and accepts the resulting edit-session baseline.
        /// </summary>
        private async Task LoadModuleDraftsAsync(bool reloadModules, bool reloadRuntimeValues)
        {
            if (reloadModules || _loadedModules.Count == 0)
            {
                var discovered = await _settingsService.DiscoverModulesAsync();
                _loadedModules = discovered
                    .Where(SettingsService.IsModuleEligibleForSettings)
                    .ToList();
                _runtimeLoadedModuleIds.Clear();
                _editSession.ReplaceModules(_loadedModules);
                ClearModuleSettingsSurfaceCache();
            }

            if (reloadRuntimeValues)
            {
                _runtimeLoadedModuleIds.Clear();
            }

            foreach (var module in _loadedModules)
            {
                await _settingsService.LoadModuleDraftAsync(
                    _editSession.GetModule(module),
                    reloadRuntimeValues);
                if (reloadRuntimeValues)
                {
                    _runtimeLoadedModuleIds.Add(SettingsService.GetModuleRuntimeKey(module));
                }
            }
        }


        // Categories

        /// <summary>
        /// Rebuilds the unified category selector sidebar.
        /// </summary>
        private void BuildCategorySelectors()
        {
            _categoryPresentation.Load(
                _categories,
                GetLocalizedCategoryTitle,
                L.Get(LocalizationKeys.Settings_Category_ASLM),
                L.Get(LocalizationKeys.Settings_Header_Modules),
                _activeCategory?.Id);
            CategorySelector.IsEnabled = !_isSaving;
        }

        /// <summary>
        /// Handles one category selection requested by the bindable sidebar model.
        /// </summary>
        private void OnCategorySelectorRequested(SettingsCategory category)
        {
            TrySelectCategory(category);
        }

        /// <summary>
        /// Switches to the requested category while preserving its detached pending edits.
        /// </summary>
        private void TrySelectCategory(SettingsCategory category)
        {
            if (_isSaving || _isSwitchingCategory)
            {
                return;
            }

            if (_activeCategory != null &&
                _activeCategory.Id.Equals(category.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                _isSwitchingCategory = true;
                SyncDraftValuesFromControls();

                var resolvedCategory = ResolveCategory(category.Id);
                if (resolvedCategory == null)
                {
                    return;
                }

                var leavingPersonalization =
                    _activeCategory?.Kind == SettingsCategoryKind.Personalization &&
                    resolvedCategory.Kind != SettingsCategoryKind.Personalization;

                if (leavingPersonalization && HasUnsavedPersonalizationChanges())
                {
                    _themeService.ApplyFromSettings();
                }

                ActivateCategory(resolvedCategory);
            }
            finally
            {
                _isSwitchingCategory = false;
            }
        }

        /// <summary>
        /// Activates the selected category and displays its stable settings content.
        /// </summary>
        private void ActivateCategory(SettingsCategory category)
        {
            _activeCategory = category;
            ActiveCategoryTitleLabel.Text = GetLocalizedCategoryTitle(category);

            switch (category.Kind)
            {
                case SettingsCategoryKind.Aslm:
                    RenderAslmCategory();
                    break;
                case SettingsCategoryKind.Accounts:
                    RenderAccountsCategory();
                    break;
                case SettingsCategoryKind.Updates:
                    RenderUpdatesCategory();
                    break;
                case SettingsCategoryKind.Module:
                    RenderModuleCategory(category.Module!);
                    _ = RefreshActiveModuleRuntimeValuesAsync(category);
                    break;
                case SettingsCategoryKind.Personalization:
                    RenderPersonalizationCategory();
                    break;
            }

            UpdateSelectorButtonStates();
            UpdateActionButtons();
            ResetSettingsScrollPosition();
        }

        /// <summary>
        /// Returns the settings viewport to the beginning without blocking category rendering.
        /// </summary>
        private async void ResetSettingsScrollPosition()
        {
            try
            {
                await Task.Yield();
                await SettingsScroll.ScrollToAsync(0, 0, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reset settings scroll position: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads live runtime values only for the currently visible module settings page.
        /// </summary>
        private async Task RefreshActiveModuleRuntimeValuesAsync(SettingsCategory category)
        {
            if (category.Kind != SettingsCategoryKind.Module || category.Module == null)
            {
                return;
            }

            var module = category.Module;
            var moduleDraft = _editSession.GetModule(module);
            var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
            if (_runtimeLoadedModuleIds.Contains(runtimeKey))
            {
                return;
            }

            try
            {
                var settings = module.Settings?.Where(SettingsService.ShouldDisplaySetting).ToList() ?? [];
                if (settings.Count == 0)
                {
                    _runtimeLoadedModuleIds.Add(runtimeKey);
                    return;
                }

                var loaded = await Task.WhenAll(settings.Select(setting => _settingsService.LoadSettingValueAsync(module, setting)));

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var stillActive =
                        _activeCategory?.Kind == SettingsCategoryKind.Module &&
                        _activeCategory.Module != null &&
                        string.Equals(_activeCategory.Module.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase);

                    // A delayed runtime getter must never overwrite edits captured after the request started.
                    if (moduleDraft.HasChanges || stillActive && HasUnsavedChanges())
                    {
                        return;
                    }

                    _settingsService.ApplyLoadedSettingsToDraft(moduleDraft, loaded);
                    MarkModuleSettingsPresentationForRefresh(module);

                    _runtimeLoadedModuleIds.Add(runtimeKey);

                    if (stillActive)
                    {
                        RenderModuleCategory(module);
                        UpdateActionButtons();
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh runtime settings for module '{module.Name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Forces live runtime values for one module after settings are saved.
        /// </summary>
        private async Task ReloadModuleRuntimeValuesAsync(ModuleConfig module)
        {
            var key = SettingsService.GetModuleRuntimeKey(module);
            _runtimeLoadedModuleIds.Remove(key);
            await _settingsService.LoadModuleDraftAsync(
                _editSession.GetModule(module),
                reloadRuntimeValues: true);
            MarkModuleSettingsPresentationForRefresh(module);
            _runtimeLoadedModuleIds.Add(key);
        }

        /// <summary>
        /// Returns the category that matches the stored category identifier, if it still exists.
        /// </summary>
        private SettingsCategory? ResolveCategory(string? categoryId) =>
            string.IsNullOrWhiteSpace(categoryId)
                ? null
                : _categories.FirstOrDefault(category => category.Id.Equals(categoryId, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Applies active and inactive styling to the selector buttons.
        /// </summary>
        private void UpdateSelectorButtonStates()
        {
            _categoryPresentation.SetActive(_activeCategory?.Id);
            CategorySelector.IsEnabled = !_isSaving;
        }

        /// <summary>
        /// Reapplies footer action styles after the active palette resources are rewritten.
        /// </summary>
        private void RefreshFooterChromeFromResources()
        {
            if (_hasLoaded)
            {
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Coalesces rapid editor changes before recomputing save/reset button state.
        /// </summary>
        private void QueueActionButtonUpdate()
        {
            if (Dispatcher == null)
            {
                UpdateActionButtons();
                return;
            }

            if (Interlocked.Exchange(ref _actionButtonUpdateQueued, 1) == 1)
            {
                return;
            }

            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
            {
                Interlocked.Exchange(ref _actionButtonUpdateQueued, 0);
                UpdateActionButtons();
            });
        }

        /// <summary>
        /// Updates the footer action buttons to match the currently visible category.
        /// </summary>
        private void UpdateActionButtons()
        {
            var canInteract = !_isSaving && !_isAslmAccountActionRunning && _activeCategory != null;
            var hasChanges = canInteract && HasAnyUnsavedChanges();
            var canReset = _activeCategory != null;
            DefaultButton.IsVisible = canReset;
            DefaultButton.IsEnabled = canInteract && canReset;
            DiscardButton.IsVisible = canReset && hasChanges;
            DiscardButton.IsEnabled = canInteract && hasChanges;
            SaveButton.IsEnabled = canInteract;
            SaveButton.Text = _isSaving ? L.Get(LocalizationKeys.Settings_Saving) : L.Get(LocalizationKeys.Settings_Save);
            ApplyActionButtonState(DefaultButton, false);
            ApplyActionButtonState(DiscardButton, isPrimary: false, isDanger: true);

            if (_activeCategory == null)
            {
                DefaultButton.IsVisible = false;
                DiscardButton.IsVisible = false;
                DiscardButton.IsEnabled = false;
                SaveAndRestartButton.IsEnabled = false;
                SaveAndRestartButton.IsVisible = false;
                SaveButton.IsVisible = false;
                ApplyActionButtonState(SaveButton, false);
                SaveAndRestartButton.Text = L.Get(LocalizationKeys.Settings_SaveAndRestart);
                return;
            }

            var canShowRestart = hasChanges && HasPendingRestartChanges();

            SaveAndRestartButton.IsEnabled = canInteract && canShowRestart;
            SaveAndRestartButton.Text = L.Get(LocalizationKeys.Settings_SaveAndRestart);

            SaveButton.IsVisible = hasChanges;
            SaveAndRestartButton.IsVisible = canShowRestart;

            var highlightRestart = canShowRestart;
            var highlightSave = hasChanges && !highlightRestart;

            ApplyActionButtonState(SaveButton, highlightSave);
            ApplyActionButtonState(SaveAndRestartButton, highlightRestart);
        }

        /// <summary>
        /// Checks whether any pending edit has a restart path, regardless of the visible category.
        /// </summary>
        private bool HasPendingRestartChanges() =>
            HasUnsavedPersonalizationChanges() ||
            HasUnsavedAslmRestartSettingsChanges() ||
            GetModulesWithUnsavedChanges().Any(CanRestartModule);

        /// <summary>
        /// Returns modules with unsaved settings, including the currently edited module controls.
        /// </summary>
        private List<ModuleConfig> GetModulesWithUnsavedChanges()
        {
            var result = new List<ModuleConfig>();
            foreach (var module in _loadedModules)
            {
                var hasChanges = SettingsService.ModuleHasChangesComparedToBaseline(
                    _editSession.GetModule(module));

                if (hasChanges)
                {
                    result.Add(module);
                }
            }

            return result;
        }

        /// <summary>
        /// Determines whether one changed module can be restarted from settings.
        /// </summary>
        private static bool CanRestartModule(ModuleConfig module) =>
            module.Status.Enabled && module.Commands.Run.Count > 0;

        /// <summary>
        /// Applies the passive or emphasized visual state to one footer action button.
        /// </summary>
        private static void ApplyActionButtonState(Button button, bool isPrimary, bool isDanger = false)
        {
            var key = isDanger
                ? FooterDangerButtonStyleKey
                : isPrimary
                    ? FooterPrimaryButtonStyleKey
                    : FooterButtonStyleKey;
            var style = GetStyleResource(key);
            if (style == null)
            {
                return;
            }

            // Reassigning the same Style instance is often a no-op; clearing first forces DynamicResource setters to rebind
            // after Application.Resources palette updates (theme preview, custom colors).
            if (ReferenceEquals(button.Style, style))
            {
                button.Style = null;
            }

            button.Style = style;
        }
    }
}
