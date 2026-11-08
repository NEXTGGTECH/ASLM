// Copyright NEXTGGTECH. Apache License 2.0.

using Debug = System.Diagnostics.Debug;
using ASLM.Controls.Settings;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Settings overlay of the shell: a category sidebar and a content surface that shows either a built-in
    /// category (ASLM, accounts, updates, personalization) or the declarative settings of one module.
    /// All edits are held as drafts in a <see cref="SettingsEditSession"/> next to the accepted baselines, so the
    /// view can detect unsaved changes, discard them, or validate and persist them in one save pass.
    /// The class is split across several partial files by topic.
    /// </summary>
    public partial class SettingsView : ContentView, ILocalizable
    {
        // Constants and layout limits

        /// <summary>
        /// Share of the host width that the settings dialog occupies before the size limits are applied.
        /// </summary>
        private const double DialogWidthFactor = 0.8;

        /// <summary>
        /// Share of the host height that the settings dialog occupies before the size limits are applied.
        /// </summary>
        private const double DialogHeightFactor = 0.8;

        /// <summary>
        /// Smallest dialog width in device-independent units.
        /// </summary>
        private const double MinDialogWidth = 960;

        /// <summary>
        /// Smallest dialog height in device-independent units.
        /// </summary>
        private const double MinDialogHeight = 540;

        /// <summary>
        /// Largest dialog width in device-independent units.
        /// </summary>
        private const double MaxDialogWidth = 1280;

        /// <summary>
        /// Largest dialog height in device-independent units.
        /// </summary>
        private const double MaxDialogHeight = 720;

        /// <summary>
        /// Page opened by the GitHub account link when no account is connected or its profile address is unknown.
        /// </summary>
        private const string GitHubHomeUrl = "https://github.com/";

        /// <summary>
        /// Account settings page of Ollama that the Ollama account link opens.
        /// </summary>
        private const string OllamaSettingsUrl = "https://ollama.com/settings";

        /// <summary>
        /// Delay between status checks while waiting for the browser-based Ollama sign-in to finish.
        /// </summary>
        private static readonly TimeSpan OllamaSignInPollInterval = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Total time after which the Ollama sign-in status polling gives up.
        /// </summary>
        private static readonly TimeSpan OllamaSignInPollDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Resource key of the style for passive footer buttons.
        /// </summary>
        private const string FooterButtonStyleKey = "SettingsFooterButtonStyle";

        /// <summary>
        /// Resource key of the style for the emphasized (primary) footer button.
        /// </summary>
        private const string FooterPrimaryButtonStyleKey = "SettingsFooterPrimaryButtonStyle";

        /// <summary>
        /// Resource key of the style for destructive footer buttons such as discarding changes.
        /// </summary>
        private const string FooterDangerButtonStyleKey = "SettingsFooterDangerButtonStyle";


        // Services and edit session

        /// <summary>
        /// Persisted application data that drafts are loaded from and saved to.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Module discovery, runtime setting access, validation and persistence used by the page.
        /// </summary>
        private readonly SettingsService _settingsService;

        /// <summary>
        /// Localization service that the view hooks into to re-localize itself and to apply a changed culture.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Source of the Ollama account metadata and the sign-in and sign-out actions.
        /// </summary>
        private readonly OllamaSettingsStore _ollamaSettings;

        /// <summary>
        /// Store that connects and disconnects the GitHub account and exposes its state.
        /// </summary>
        private readonly GitHubAccountStore _githubAccountStore;

        /// <summary>
        /// Source of the latest GitHub API rate-limit snapshot shown on the GitHub account card.
        /// </summary>
        private readonly GitHubRateLimitStore _githubRateLimitStore;

        /// <summary>
        /// GitHub client used to refresh the rate-limit usage after a successful connect.
        /// </summary>
        private readonly GitHubUpdateClient _githubUpdateClient;

        /// <summary>
        /// Update manager that runs update operations and publishes their live state.
        /// </summary>
        private readonly UpdateManager _updateManager;

        /// <summary>
        /// Scheduler that holds the available update candidates and reports changes to them.
        /// </summary>
        private readonly UpdateScheduler _updateScheduler;

        /// <summary>
        /// Engine discovery used to read the installed Ollama engine state.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// ASLM Mirror Server whose enabled state is edited through the API server toggle.
        /// </summary>
        private readonly AslmMirrorServer _mirrorServer;

        /// <summary>
        /// Notification center that receives the toast shown after settings were saved.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Theme service that previews and applies appearance and custom palettes.
        /// </summary>
        private readonly ThemeService _themeService;

        /// <summary>
        /// Registry of user-defined custom themes.
        /// </summary>
        private readonly CustomThemesStore _customThemesStore;

        /// <summary>
        /// SUNRISE service that tells whether the active ASLM account is local or cloud-based.
        /// </summary>
        private readonly SunriseService _sunriseService;

        /// <summary>
        /// Application and module drafts together with their accepted baselines.
        /// </summary>
        private readonly SettingsEditSession _editSession = new();


        // Module settings and category state

        /// <summary>
        /// View models of the module settings pages built so far, keyed by module runtime key.
        /// </summary>
        private readonly Dictionary<string, ModuleSettingsPageViewModel> _moduleSettingsPresentations =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Module settings views built so far, keyed by module runtime key, reused when a module is revisited.
        /// </summary>
        private readonly Dictionary<string, ModuleSettingsView> _moduleSettingsViews =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Runtime keys of cached module pages whose draft changed externally and which must refresh their rows
        /// the next time they are populated.
        /// </summary>
        private readonly HashSet<string> _moduleSettingsPresentationsNeedingRefresh =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Bindable model of the category sidebar.
        /// </summary>
        private readonly SettingsCategorySidebarViewModel _categoryPresentation;

        /// <summary>
        /// Per-module gates that keep the runtime setting getters of one module sequential.
        /// </summary>
        private readonly Dictionary<string, SemaphoreSlim> _moduleRuntimeLoadLocks =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Lifetime of the module runtime reads; replaced and cancelled whenever the loaded values become obsolete.
        /// </summary>
        private CancellationTokenSource _moduleRuntimeLoadCancellation = new();

        /// <summary>
        /// Lifetime of the incremental creation of module setting rows; replaced and cancelled on navigation or close.
        /// </summary>
        private CancellationTokenSource _moduleSurfaceBuildCancellation = new();

        /// <summary>
        /// Modules that currently contribute a settings category.
        /// </summary>
        private List<ModuleConfig> _loadedModules = [];

        /// <summary>
        /// Categories shown in the sidebar: the built-in ones followed by one per module.
        /// </summary>
        private List<SettingsCategory> _categories = [];

        /// <summary>
        /// Category whose content is currently displayed, or null when nothing is shown.
        /// </summary>
        private SettingsCategory? _activeCategory;

        /// <summary>
        /// Built-in category that was requested from outside and is opened as soon as loading and saving allow it.
        /// </summary>
        private SettingsCategoryKind? _requestedCategory;


        // View and operation state

        /// <summary>
        /// Ollama account and engine state shown on the accounts page, loaded from the Ollama settings store.
        /// </summary>
        private OllamaPersistentSettings _ollamaDraft = new();

        /// <summary>
        /// Latest GitHub account state shown on the accounts page.
        /// </summary>
        private GitHubAccountState _githubDraft = new();

        /// <summary>
        /// Whether the first load completed, which enables refreshing when the shell revisits the page.
        /// </summary>
        private bool _hasLoaded;

        /// <summary>
        /// Whether a settings load is in progress.
        /// </summary>
        private bool _isLoading;

        /// <summary>
        /// Whether a category switch is in progress, which blocks re-entrant selection.
        /// </summary>
        private bool _isSwitchingCategory;

        /// <summary>
        /// Whether a save is in progress, which locks the footer and the category selection.
        /// </summary>
        private bool _isSaving;

        /// <summary>
        /// Whether an Ollama sign-in or sign-out is running.
        /// </summary>
        private bool _isOllamaAccountActionRunning;

        /// <summary>
        /// Whether the live Ollama account metadata is being refreshed in the background.
        /// </summary>
        private bool _isOllamaMetadataRefreshRunning;

        /// <summary>
        /// Whether a GitHub connect or disconnect is running.
        /// </summary>
        private bool _isGitHubAccountActionRunning;

        /// <summary>
        /// Whether switching the ASLM account between local and cloud mode is running.
        /// </summary>
        private bool _isAslmAccountActionRunning;

        /// <summary>
        /// Whether the view listens to update scheduler and update operation events.
        /// </summary>
        private bool _isUpdateSchedulerSubscribed;

        /// <summary>
        /// Set to 1 while a delayed footer button update is pending, so rapid changes are coalesced.
        /// </summary>
        private int _actionButtonUpdateQueued;

        /// <summary>
        /// Counter that is advanced to invalidate module runtime reads that started before the change.
        /// </summary>
        private int _moduleRuntimeLoadGeneration;

        /// <summary>
        /// Module catalog revision that <see cref="_loadedModules"/> was discovered at,
        /// or -1 before the first discovery.
        /// </summary>
        private long _loadedModuleCatalogRevision = -1;


        // Account controls

        /// <summary>
        /// Name of the running Ollama account action, <c>signin</c> or <c>signout</c>, or empty when none runs.
        /// </summary>
        private string _ollamaAccountAction = string.Empty;

        /// <summary>
        /// Button that signs in to or out of Ollama.
        /// </summary>
        private Button? _ollamaAccountButton;

        /// <summary>
        /// Label that describes the Ollama account state.
        /// </summary>
        private Label? _ollamaAccountStatusLabel;

        /// <summary>
        /// Button that connects or disconnects the GitHub account.
        /// </summary>
        private Button? _githubAccountButton;

        /// <summary>
        /// Label that describes the GitHub account state and API usage.
        /// </summary>
        private Label? _githubAccountStatusLabel;

        /// <summary>
        /// Button that switches the ASLM account between local and cloud mode.
        /// </summary>
        private Button? _aslmAccountButton;

        /// <summary>
        /// Label that describes the ASLM account state.
        /// </summary>
        private Label? _aslmAccountStatusLabel;


        // Update controls

        /// <summary>
        /// Switch that enables automatic update checks.
        /// </summary>
        private SettingsToggle? _checkUpdatesToggle;

        /// <summary>
        /// Switch that enables automatic installation of updates.
        /// </summary>
        private SettingsToggle? _autoUpdatesToggle;

        /// <summary>
        /// Picker for the ASLM update channel.
        /// </summary>
        private Picker? _appUpdateChannelPicker;

        /// <summary>
        /// Picker for the default update channel of modules.
        /// </summary>
        private Picker? _moduleUpdateChannelPicker;

        /// <summary>
        /// Label with the installed ASLM version.
        /// </summary>
        private Label? _aslmInstalledVersionLabel;

        /// <summary>
        /// Label with the available ASLM version; hidden when no newer version is known.
        /// </summary>
        private Label? _aslmAvailableVersionLabel;

        /// <summary>
        /// Row that holds the ASLM update buttons and is visible only while an ASLM update action is possible.
        /// </summary>
        private HorizontalStackLayout? _aslmUpdateActionHost;

        /// <summary>
        /// Container that overlays the ASLM download button and its progress indicator.
        /// </summary>
        private Grid? _prepareAppUpdateHost;

        /// <summary>
        /// Button that downloads the ASLM update.
        /// </summary>
        private Button? _prepareAppUpdateButton;

        /// <summary>
        /// Progress content (spinner, stage and percentage) shown instead of the ASLM download button while it runs.
        /// </summary>
        private HorizontalStackLayout? _prepareAppUpdateProgress;

        /// <summary>
        /// Spinner of the ASLM update progress.
        /// </summary>
        private ActivityIndicator? _prepareAppUpdateSpinner;

        /// <summary>
        /// Label with the current stage of the ASLM update operation.
        /// </summary>
        private Label? _prepareAppUpdateProgressText;

        /// <summary>
        /// Label with the download percentage of the ASLM update.
        /// </summary>
        private Label? _prepareAppUpdateProgressPercent;

        /// <summary>
        /// Button that restarts ASLM so the prepared update gets installed.
        /// </summary>
        private Button? _restartAppUpdateButton;

        /// <summary>
        /// ASLM update offered by the scheduler that has not been downloaded yet.
        /// </summary>
        private UpdateCandidate? _pendingAppUpdateCandidate;

        /// <summary>
        /// Container that overlays the Ollama update button and its progress indicator.
        /// </summary>
        private Grid? _ollamaUpdateHost;

        /// <summary>
        /// Button that downloads and installs the Ollama engine update.
        /// </summary>
        private Button? _ollamaUpdateButton;

        /// <summary>
        /// Progress content (spinner, stage and percentage) shown instead of the Ollama update button while it runs.
        /// </summary>
        private HorizontalStackLayout? _ollamaUpdateProgress;

        /// <summary>
        /// Spinner of the Ollama update progress.
        /// </summary>
        private ActivityIndicator? _ollamaUpdateSpinner;

        /// <summary>
        /// Label with the current stage of the Ollama update operation.
        /// </summary>
        private Label? _ollamaUpdateProgressText;

        /// <summary>
        /// Label with the download percentage of the Ollama update.
        /// </summary>
        private Label? _ollamaUpdateProgressPercent;

        /// <summary>
        /// Label with the installed Ollama engine version.
        /// </summary>
        private Label? _ollamaInstalledVersionLabel;

        /// <summary>
        /// Label with the available Ollama engine version; hidden when no newer version is known.
        /// </summary>
        private Label? _ollamaAvailableVersionLabel;

        /// <summary>
        /// Ollama engine update offered by the scheduler that has not been installed yet.
        /// </summary>
        private UpdateCandidate? _pendingOllamaUpdateCandidate;


        // ASLM toggles

        /// <summary>
        /// Switch that enables the ASLM Mirror Server.
        /// </summary>
        private SettingsToggle? _apiServerToggle;

        /// <summary>
        /// Switch that shows the Consoles page in the navigation sidebar.
        /// </summary>
        private SettingsToggle? _consoleSidebarToggle;

        /// <summary>
        /// Switch that keeps the consoles of completed processes visible.
        /// </summary>
        private SettingsToggle? _consoleCompletedToggle;

        /// <summary>
        /// Switch that shows a separate console for each module process.
        /// </summary>
        private SettingsToggle? _consoleIndividualToggle;

        /// <summary>
        /// Switch that accepts updated legal policies automatically.
        /// </summary>
        private SettingsToggle? _legalAutoAcceptToggle;

        /// <summary>
        /// Switch that reopens the last visited page when ASLM starts.
        /// </summary>
        private SettingsToggle? _restoreLastPageToggle;

        /// <summary>
        /// Switch that disables the Home page.
        /// </summary>
        private SettingsToggle? _disableHomePageToggle;


        // Background operations

        /// <summary>
        /// Cancellation source of the running live Ollama metadata refresh, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _ollamaMetadataRefreshCts;

        /// <summary>
        /// Cancellation source of the running Ollama sign-in status poll, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _ollamaStatusPollingCts;

        /// <summary>
        /// Cancellation source of the running ASLM account switch, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _aslmAccountActionCts;

        /// <summary>
        /// Nesting depth of programmatic control updates; above zero the control change handlers ignore their events.
        /// </summary>
        private int _builtInControlStateApplicationDepth;


        // Personalization state

        /// <summary>
        /// Detached copy of the custom theme open in the color editor, or null when no theme is being edited.
        /// </summary>
        private CustomTheme? _editingThemeDraft;

        /// <summary>
        /// Picker for the appearance mode.
        /// </summary>
        private Picker? _appearancePicker;

        /// <summary>
        /// Picker for the interface language.
        /// </summary>
        private Picker? _languagePicker;

        /// <summary>
        /// Section with the custom theme picker and theme actions, visible only in the Custom appearance mode.
        /// </summary>
        private VerticalStackLayout? _customThemeSection;

        /// <summary>
        /// Picker that selects the custom theme to edit.
        /// </summary>
        private Picker? _customThemePicker;

        /// <summary>
        /// Section with the color editor of the selected custom theme.
        /// </summary>
        private SettingsSectionView? _themeEditorSection;

        /// <summary>
        /// Set while the custom theme picker is filled programmatically so its selection event is ignored.
        /// </summary>
        private bool _suppressCustomThemePickerEvents;

        /// <summary>
        /// Whether the language, appearance and custom theme controls were filled from the personalization draft.
        /// </summary>
        private bool _personalizationControlsInitialized;


        // Control accessors

        /// <summary>
        /// Gets whether built-in controls are being updated from draft state.
        /// </summary>
        private bool IsApplyingBuiltInControlState => _builtInControlStateApplicationDepth > 0;

        /// <summary>
        /// Gets the stable display-name input declared by the built-in settings view.
        /// </summary>
        private Entry UsernameEntry => BuiltInSettingsContainer.UserNameInput;

        /// <summary>
        /// Gets the stable module-port input declared by the built-in settings view.
        /// </summary>
        private Entry ModulePortEntry => BuiltInSettingsContainer.ModulePortInput;

        /// <summary>
        /// Gets the stable module-port validation label.
        /// </summary>
        private Label PortErrorLabel => BuiltInSettingsContainer.ModulePortError;

        /// <summary>
        /// Gets the stable port settings section.
        /// </summary>
        private VerticalStackLayout PortsSection => BuiltInSettingsContainer.PortsHost;

        /// <summary>
        /// Gets the stable profile settings section.
        /// </summary>
        private VerticalStackLayout UserProfileSection => BuiltInSettingsContainer.UserProfile;


        // Draft accessors

        /// <summary>
        /// Gets or replaces the accepted ASLM baseline stored by the session.
        /// </summary>
        private AslmBaseline _aslmBaseline
        {
            get => _editSession.Application.AslmBaseline;
            set => _editSession.Application.AslmBaseline = value;
        }

        /// <summary>
        /// Gets or replaces the accepted console baseline stored by the session.
        /// </summary>
        private ConsoleBaseline _consoleBaseline
        {
            get => _editSession.Application.ConsoleBaseline;
            set => _editSession.Application.ConsoleBaseline = value;
        }

        /// <summary>
        /// Gets or replaces the accepted update baseline stored by the session.
        /// </summary>
        private UpdateBaseline _updateBaseline
        {
            get => _editSession.Application.UpdateBaseline;
            set => _editSession.Application.UpdateBaseline = value;
        }

        /// <summary>
        /// Gets or replaces the console draft stored by the session.
        /// </summary>
        private ConsoleBaseline _consoleDraft
        {
            get => _editSession.Application.Console;
            set => _editSession.Application.Console = value;
        }

        /// <summary>
        /// Gets or replaces the update draft stored by the session.
        /// </summary>
        private UpdateBaseline _updateDraft
        {
            get => _editSession.Application.Update;
            set => _editSession.Application.Update = value;
        }

        /// <summary>
        /// Gets or replaces the user-name draft stored by the session.
        /// </summary>
        private string _userNameDraft
        {
            get => _editSession.Application.UserName;
            set => _editSession.Application.UserName = value;
        }

        /// <summary>
        /// Gets or replaces the port draft stored by the session.
        /// </summary>
        private string _portStartDraft
        {
            get => _editSession.Application.PortStart;
            set => _editSession.Application.PortStart = value;
        }

        /// <summary>
        /// Gets or replaces the API-server draft stored by the session.
        /// </summary>
        private bool _apiServerEnabledDraft
        {
            get => _editSession.Application.ApiServerEnabled;
            set => _editSession.Application.ApiServerEnabled = value;
        }

        /// <summary>
        /// Gets or replaces the legal draft stored by the session.
        /// </summary>
        private bool _legalAutoAcceptDraft
        {
            get => _editSession.Application.LegalAutoAcceptUpdates;
            set => _editSession.Application.LegalAutoAcceptUpdates = value;
        }

        /// <summary>
        /// Gets or replaces the accepted legal baseline stored by the session.
        /// </summary>
        private bool _legalAutoAcceptBaseline
        {
            get => _editSession.Application.LegalAutoAcceptBaseline;
            set => _editSession.Application.LegalAutoAcceptBaseline = value;
        }

        /// <summary>
        /// Gets or replaces the last-page restoration draft stored by the session.
        /// </summary>
        private bool _restoreLastPageDraft
        {
            get => _editSession.Application.RestoreLastPage;
            set => _editSession.Application.RestoreLastPage = value;
        }

        /// <summary>
        /// Gets or replaces the home-page visibility draft stored by the session.
        /// </summary>
        private bool _disableHomePageDraft
        {
            get => _editSession.Application.DisableHomePage;
            set => _editSession.Application.DisableHomePage = value;
        }

        /// <summary>
        /// Gets or replaces the personalization draft stored by the session.
        /// </summary>
        private AppPersonalizationConfig _personalizationDraft
        {
            get => _editSession.Application.Personalization;
            set => _editSession.Application.Personalization = value;
        }

        /// <summary>
        /// Gets or replaces the accepted personalization baseline stored by the session.
        /// </summary>
        private AppPersonalizationConfig _personalizationBaseline
        {
            get => _editSession.Application.PersonalizationBaseline;
            set => _editSession.Application.PersonalizationBaseline = value;
        }


        // Events

        /// <summary>
        /// Raised when the user asks to close the settings overlay.
        /// </summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Raised after saved changes to the navigation-related settings (last page restoration, home page).
        /// </summary>
        public event EventHandler? NavigationSettingsSaved;


        // Initialization

        /// <summary>
        /// Creates the settings view, connects the built-in controls and hooks the loading and layout handlers.
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
            // Keep the injected services.
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

            // Create the sidebar model before the XAML is loaded and bind it to the selector.
            _categoryPresentation = new SettingsCategorySidebarViewModel(OnCategorySelectorRequested);
            InitializeComponent();
            CategorySelector.BindingContext = _categoryPresentation;
            InitializeBuiltInControlReferences();
            LocalizableAttach.Hook(this, _localization, this);
            ApplyScrollViewChrome(CategoryScroll);
            SettingsScroll.Scrolled += OnSettingsScrollScrolled;

            // Text edits update the drafts at once and schedule a footer refresh.
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

            // Size the dialog on layout changes and load or release the page with the visual tree.
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
            _disableHomePageToggle = BuiltInSettingsContainer.DisableHomePageInput;

            // Account cards.
            _aslmAccountButton = BuiltInSettingsContainer.AslmAccountAction;
            _aslmAccountStatusLabel = BuiltInSettingsContainer.AslmAccountState;
            _githubAccountButton = BuiltInSettingsContainer.GitHubAccountAction;
            _githubAccountStatusLabel = BuiltInSettingsContainer.GitHubAccountState;
            _ollamaAccountButton = BuiltInSettingsContainer.OllamaAccountAction;
            _ollamaAccountStatusLabel = BuiltInSettingsContainer.OllamaAccountState;

            // Update cards.
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
            _prepareAppUpdateProgressText = BuiltInSettingsContainer.PrepareAppUpdateProgressStatus;
            _prepareAppUpdateProgressPercent = BuiltInSettingsContainer.PrepareAppUpdateProgressValue;
            _restartAppUpdateButton = BuiltInSettingsContainer.RestartAppUpdateAction;
            _ollamaUpdateHost = BuiltInSettingsContainer.OllamaUpdateContainer;
            _ollamaUpdateButton = BuiltInSettingsContainer.OllamaUpdateAction;
            _ollamaUpdateProgress = BuiltInSettingsContainer.OllamaUpdateProgressContent;
            _ollamaUpdateSpinner = BuiltInSettingsContainer.OllamaUpdateProgressSpinner;
            _ollamaUpdateProgressText = BuiltInSettingsContainer.OllamaUpdateProgressStatus;
            _ollamaUpdateProgressPercent = BuiltInSettingsContainer.OllamaUpdateProgressValue;
            _ollamaInstalledVersionLabel = BuiltInSettingsContainer.OllamaInstalledVersion;
            _ollamaAvailableVersionLabel = BuiltInSettingsContainer.OllamaAvailableVersion;

            // Personalization controls.
            _languagePicker = BuiltInSettingsContainer.LanguageInput;
            _appearancePicker = BuiltInSettingsContainer.AppearanceInput;
            _customThemeSection = BuiltInSettingsContainer.CustomThemesHost;
            _themeEditorSection = BuiltInSettingsContainer.ThemeEditorHost;
            _customThemePicker = BuiltInSettingsContainer.CustomThemeInput;

            // Populate invariant choices once and wire all interaction handlers once.
            _appUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };
            _moduleUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };

            // Toggle, picker and selection changes feed the drafts and the footer state.
            _apiServerToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleSidebarToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleIndividualToggle.Toggled += OnAslmBuiltInToggleChanged;
            _consoleCompletedToggle.Toggled += OnAslmBuiltInToggleChanged;
            _legalAutoAcceptToggle.Toggled += OnAslmBuiltInToggleChanged;
            _restoreLastPageToggle.Toggled += OnAslmBuiltInToggleChanged;
            _disableHomePageToggle.Toggled += OnAslmBuiltInToggleChanged;
            _checkUpdatesToggle.Toggled += OnUpdateControlChanged;
            _autoUpdatesToggle.Toggled += OnUpdateControlChanged;
            _appUpdateChannelPicker.SelectedIndexChanged += OnUpdateControlChanged;
            _moduleUpdateChannelPicker.SelectedIndexChanged += OnUpdateControlChanged;
            _languagePicker.SelectedIndexChanged += OnLanguagePickerChanged;
            _appearancePicker.SelectedIndexChanged += OnAppearancePickerChanged;
            _customThemePicker.SelectedIndexChanged += OnCustomThemePickerSelectionChanged;
            BuiltInSettingsContainer.BaseAppearanceInput.SelectedIndexChanged += OnBaseAppearancePickerChanged;

            // Account, update and theme actions.
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


        // Control change handlers

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
        /// Reloads the settings page when the shell revisits it; does nothing before the first load or during a load.
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


        // Category requests

        /// <summary>
        /// Opens a built-in category, including before the first asynchronous load completes.
        /// </summary>
        internal void SelectCategory(SettingsCategoryKind kind)
        {
            _requestedCategory = kind;
            ApplyRequestedCategory();
        }

        /// <summary>
        /// Opens the requested category once loading, saving and category switching are idle;
        /// the request stays pending when it cannot be applied yet.
        /// </summary>
        private void ApplyRequestedCategory()
        {
            if (_isLoading || _isSaving || _isSwitchingCategory || !_requestedCategory.HasValue) return;
            var category = _categories.FirstOrDefault(c => c.Kind == _requestedCategory.Value);
            if (category == null) return;
            TrySelectCategory(category);
            _requestedCategory = null;
        }


        // Overlay events

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
            // Pending edits need the user's decision; declining to discard them keeps the dialog open.
            if (!await ConfirmDiscardChangesIfNeededAsync())
            {
                return;
            }

            // Stop all background work started by the pages before the overlay goes away.
            StopOllamaStatusPolling();
            StopOllamaMetadataRefresh();
            StopAslmAccountAction();
            CancelModuleRuntimeLoads();
            CancelModuleSurfaceBuilds();
            _ollamaSettings.StopManagedRuntime();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }


        // Loading

        /// <summary>
        /// Loads shared settings, discovers modules, and restores the active category.
        /// </summary>
        private async Task LoadSettingsAsync()
        {
            // Return control to the shell so the settings overlay can be attached and painted
            // before any page work starts.
            await Task.Yield();

            // Remember the open category so it can be reopened after the reload.
            var previousCategoryId = _activeCategory?.Id;

            // Reset every draft source to the persisted state; the Ollama read leaves the UI thread
            // because it resolves the engine executable on disk.
            LoadAslmDraftsFromAppData();
            LoadPersonalizationDraftsFromAppData();
            await Task.Run(LoadOllamaDraftsFromService);
            await ReloadModuleDraftsAsync();

            // Rebuild the categories and pick the one to show: an explicit request wins,
            // then the previously open category, then the first one.
            _categories = SettingsPresentationBuilder.BuildCategories(_loadedModules).ToList();

            var targetCategory = (_requestedCategory.HasValue
                ? _categories.FirstOrDefault(c => c.Kind == _requestedCategory.Value) : null)
                ?? ResolveCategory(previousCategoryId) ?? _categories.FirstOrDefault();

            // Nothing to show: display the empty state, which also hides the footer actions.
            if (targetCategory == null)
            {
                _activeCategory = null;
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_NoSettingsAvailable));
                UpdateActionButtons();
                return;
            }

            BuildCategorySelectors();
            ActivateCategory(targetCategory);
            _requestedCategory = null;
        }

        /// <summary>
        /// Initializes the settings page once after the control is first shown.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            // Subscriptions are attached on every show because OnUnloaded detaches them.
            AttachAccountLinkThemeHandlers();
            AttachUpdateSchedulerHandler();

            // The content is loaded only once; later shows are refreshed through RefreshAsync.
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
        /// Detaches the theme and update subscriptions and stops all background work when the settings view
        /// leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            DetachAccountLinkThemeHandlers();
            DetachUpdateSchedulerHandler();
            StopOllamaStatusPolling();
            StopOllamaMetadataRefresh();
            StopAslmAccountAction();
            CancelModuleRuntimeLoads();
            CancelModuleSurfaceBuilds();
            _ollamaSettings.StopManagedRuntime();
        }


        // Localization

        /// <summary>
        /// Re-applies the localized texts of the dialog chrome, the built-in pages and the category sidebar,
        /// then re-renders the active category.
        /// </summary>
        public void ApplyLocalization()
        {
            // Static chrome texts.
            SettingsSidebarTitleLabel.Text = L.Get(LocalizationKeys.Settings_Title);
            ToolTipProperties.SetText(CloseSettingsButton, L.Get(LocalizationKeys.Settings_CloseTooltip));
            BuiltInSettingsContainer.ApplyLocalization();

            // Localization must not replace an active operation's label with the default download text.
            ResetUpdateActionControls();

            DefaultButton.Text = L.Get(LocalizationKeys.Settings_LoadDefault);
            DiscardButton.Text = L.Get(LocalizationKeys.Settings_DiscardChanges);
            UpdateActionButtons();

            // Pickers and cached module pages hold localized texts, so they are rebuilt from scratch.
            _personalizationControlsInitialized = false;
            ClearModuleSettingsSurfaceCache();

            if (_categories.Count > 0)
            {
                BuildCategorySelectors();
            }

            if (_activeCategory != null)
            {
                ActivateCategory(_activeCategory, loadModuleRuntimeValues: false);
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

        /// <summary>
        /// Canonical appearance mode ids in the order the appearance picker lists them.
        /// </summary>
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

            return "System";
        }


        // Dialog sizing

        /// <summary>
        /// Keeps the dialog within the requested min/max bounds while scaling to the host size.
        /// </summary>
        private void OnViewSizeChanged(object? sender, EventArgs e)
        {
            UpdateDialogSize();
        }

        /// <summary>
        /// Applies the responsive dialog size as a share of the available overlay area, within the size limits.
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
        /// Copies the persisted personalization settings into the editable page draft and its baseline.
        /// </summary>
        private void LoadPersonalizationDraftsFromAppData()
        {
            // The session clones the config, so edits never reach the persisted object.
            var stored = _appData.Data.Personalization;
            _editSession.Application.LoadPersonalization(new AppPersonalizationConfig
            {
                Appearance = AppPersonalizationConfig.NormalizeAppearance(stored.Appearance),
                Language = AppPersonalizationConfig.NormalizeLanguage(stored.Language),
                CustomThemeId = stored.CustomThemeId
            });

            // The pickers are refilled from the new draft the next time the category is rendered.
            _personalizationControlsInitialized = false;
        }

        /// <summary>
        /// Copies the persisted shared settings into the editable page draft and its baseline,
        /// then shows them in the built-in controls.
        /// </summary>
        private void LoadAslmDraftsFromAppData()
        {
            // Load the drafts and accept them as baseline, so nothing counts as changed afterwards.
            var snapshot = SettingsService.BuildAslmDraftSnapshot(_appData, _mirrorServer.IsEnabled);
            _editSession.Application.LoadAslm(snapshot, _appData.Data.Legal.AutoAcceptUpdates);

            // Show the loaded values and clear stale feedback from an earlier edit.
            ApplyAslmDraftsToControls();
            ResetUpdateActionControls();
            PortErrorLabel.IsVisible = false;
        }

        /// <summary>
        /// Loads the Ollama settings into the page draft, falling back to defaults when reading them fails.
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
        /// Discovers module manifests and creates drafts without invoking runtime setting getters.
        /// </summary>
        private async Task ReloadModuleDraftsAsync()
        {
            // Obsolete runtime reads and half-built module pages must not outlive the reload.
            RestartModuleRuntimeLoads();
            RestartModuleSurfaceBuilds();

            // Skip discovery while the module catalog is unchanged since the last pass.
            var catalogRevision = _settingsService.ModuleCatalogRevision;
            if (_loadedModuleCatalogRevision == catalogRevision)
            {
                return;
            }

            // Discover on a worker, then replace the module drafts and drop the cached module pages.
            var cancellationToken = _moduleRuntimeLoadCancellation.Token;
            var discovered = await Task.Run(
                    () => _settingsService.DiscoverModulesAsync(),
                    cancellationToken)
                .WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _loadedModules = discovered
                .Where(SettingsService.IsModuleEligibleForSettings)
                .ToList();
            _editSession.ReplaceModules(_loadedModules);

            // Keep the captured revision so a catalog mutation during discovery forces another pass next time.
            _loadedModuleCatalogRevision = catalogRevision;
            ClearModuleSettingsSurfaceCache();
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
            // Ignore selections while saving or switching, and a repeated selection of the open category.
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
                // Store the edits of the category being left in the drafts before it is replaced.
                _isSwitchingCategory = true;
                SyncDraftValuesFromControls();

                // The requested object may come from an older category list, so look it up again by id.
                var resolvedCategory = ResolveCategory(category.Id);
                if (resolvedCategory == null)
                {
                    return;
                }

                // Unsaved personalization edits are only previewed on screen, so restore the saved theme when leaving.
                var leavingPersonalization =
                    _activeCategory?.Kind == SettingsCategoryKind.Personalization &&
                    resolvedCategory.Kind != SettingsCategoryKind.Personalization;

                if (leavingPersonalization && HasUnsavedPersonalizationChanges())
                {
                    _themeService.ApplyFromSettings();
                }

                // Reads and half-built pages of the previous module are abandoned before the new page renders.
                RestartModuleRuntimeLoads();
                RestartModuleSurfaceBuilds();
                ActivateCategory(resolvedCategory);
            }
            finally
            {
                _isSwitchingCategory = false;
            }
        }

        /// <summary>
        /// Activates the selected category and optionally starts its lazy runtime read.
        /// </summary>
        private void ActivateCategory(SettingsCategory category, bool loadModuleRuntimeValues = true)
        {
            _activeCategory = category;
            ActiveCategoryTitleLabel.Text = GetLocalizedCategoryTitle(category);

            // Render the page for the category kind; module values are read lazily after the page is shown.
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
                    if (loadModuleRuntimeValues)
                    {
                        _ = RefreshActiveModuleRuntimeValuesAsync(category);
                    }

                    break;
                case SettingsCategoryKind.Personalization:
                    RenderPersonalizationCategory();
                    break;
            }

            // Reflect the new category in the sidebar and the footer, and show it from the top.
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
                // A module page also selects its first section in the section navigation.
                if (_activeCategory?.Kind == SettingsCategoryKind.Module &&
                    _activeCategory.Module != null &&
                    _moduleSettingsPresentations.TryGetValue(
                        SettingsService.GetModuleRuntimeKey(_activeCategory.Module),
                        out var presentation))
                {
                    presentation.ActivateFirstVisibleSection();
                }

                await Task.Yield();
                await SettingsScroll.ScrollToAsync(0, 0, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to reset settings scroll position: {ex.Message}");
            }
        }


        // Module runtime loading

        /// <summary>
        /// Loads live runtime values only for the currently visible module settings page.
        /// </summary>
        private async Task RefreshActiveModuleRuntimeValuesAsync(SettingsCategory category)
        {
            if (category.Kind != SettingsCategoryKind.Module || category.Module == null)
            {
                return;
            }

            // Capture the module, its draft and the current generation now; the read finishes later and must
            // detect that the dialog moved on in the meantime.
            var module = category.Module;
            var moduleDraft = _editSession.GetModule(module);
            var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
            var loadGeneration = _moduleRuntimeLoadGeneration;
            var moduleLoadLock = GetModuleRuntimeLoadLock(runtimeKey);
            var cancellationToken = _moduleRuntimeLoadCancellation.Token;

            // Wait for an earlier read of the same module, because its getters must run one at a time.
            try
            {
                await moduleLoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                // A refresh may replace every module draft while an older page read is waiting.
                if (loadGeneration != _moduleRuntimeLoadGeneration)
                {
                    return;
                }

                // Only settings that are displayed need a live value.
                var settings = module.Settings?.Where(SettingsService.ShouldDisplaySetting).ToList() ?? [];
                if (settings.Count == 0)
                {
                    return;
                }

                // Keep getters ordered on a worker while publishing each completed value independently.
                await Task.Run(async () =>
                {
                    foreach (var setting in settings)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var loadedSetting = await _settingsService.LoadSettingValueAsync(
                            module,
                            setting,
                            cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        // Publish on the UI thread, but only while this module's page is still the visible one.
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (loadGeneration != _moduleRuntimeLoadGeneration)
                            {
                                return;
                            }

                            var stillActive =
                                _activeCategory?.Kind == SettingsCategoryKind.Module &&
                                _activeCategory.Module != null &&
                                string.Equals(
                                    _activeCategory.Module.SourcePath,
                                    module.SourcePath,
                                    StringComparison.OrdinalIgnoreCase);
                            if (!stillActive)
                            {
                                return;
                            }

                            // A delayed getter may update untouched rows but must never replace an edit in progress.
                            var settingDraft = moduleDraft.GetSetting(setting.Key);
                            if (settingDraft.HasChanges)
                            {
                                return;
                            }

                            // Store the loaded value in the draft and refresh only its row.
                            _settingsService.ApplyLoadedSettingsToDraft(moduleDraft, [loadedSetting]);
                            if (_moduleSettingsPresentations.TryGetValue(runtimeKey, out var presentation))
                            {
                                presentation.RefreshSettingFromDraft(
                                    setting.Key,
                                    SettingsService.HasDependentSettings(module, setting));
                            }
                        }).ConfigureAwait(false);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Closing or reopening settings intentionally abandons the obsolete runtime read.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh runtime settings for module '{module.Name}': {ex.Message}");
            }
            finally
            {
                moduleLoadLock.Release();
            }
        }

        /// <summary>
        /// Returns the per-module gate that keeps its runtime getters sequential
        /// while allowing different modules to load concurrently.
        /// </summary>
        private SemaphoreSlim GetModuleRuntimeLoadLock(string runtimeKey)
        {
            if (_moduleRuntimeLoadLocks.TryGetValue(runtimeKey, out var moduleLoadLock))
            {
                return moduleLoadLock;
            }

            moduleLoadLock = new SemaphoreSlim(1, 1);
            _moduleRuntimeLoadLocks[runtimeKey] = moduleLoadLock;
            return moduleLoadLock;
        }

        /// <summary>
        /// Cancels obsolete reads and creates a fresh lifetime for the next settings discovery pass.
        /// </summary>
        private void RestartModuleRuntimeLoads()
        {
            var previousCancellation = _moduleRuntimeLoadCancellation;
            _moduleRuntimeLoadCancellation = new CancellationTokenSource();
            _moduleRuntimeLoadGeneration++;

            QueueModuleRuntimeLoadCancellation(previousCancellation);
        }

        /// <summary>
        /// Cancels active module getters so closing the settings overlay never waits for them.
        /// </summary>
        private void CancelModuleRuntimeLoads()
        {
            _moduleRuntimeLoadGeneration++;
            QueueModuleRuntimeLoadCancellation(_moduleRuntimeLoadCancellation);
        }

        /// <summary>
        /// Runs process-terminating cancellation callbacks away from the UI thread.
        /// </summary>
        private static void QueueModuleRuntimeLoadCancellation(CancellationTokenSource cancellation)
        {
            _ = Task.Run(() =>
            {
                if (!cancellation.IsCancellationRequested)
                {
                    cancellation.Cancel();
                }
            });
        }

        /// <summary>
        /// Starts a fresh incremental-render lifetime and drops incomplete cached module pages.
        /// </summary>
        private void RestartModuleSurfaceBuilds()
        {
            var previousCancellation = _moduleSurfaceBuildCancellation;
            _moduleSurfaceBuildCancellation = new CancellationTokenSource();
            previousCancellation.Cancel();
            DiscardIncompleteModuleSettingsSurfaces();
        }

        /// <summary>
        /// Stops incremental row creation when the settings overlay is no longer visible.
        /// </summary>
        private void CancelModuleSurfaceBuilds()
        {
            if (!_moduleSurfaceBuildCancellation.IsCancellationRequested)
            {
                _moduleSurfaceBuildCancellation.Cancel();
            }

            DiscardIncompleteModuleSettingsSurfaces();
        }

        /// <summary>
        /// Removes partially materialized pages so a later open restarts them from a clean tree.
        /// </summary>
        private void DiscardIncompleteModuleSettingsSurfaces()
        {
            var incompleteKeys = _moduleSettingsPresentations
                .Where(static pair => !pair.Value.IsFullyLoaded)
                .Select(static pair => pair.Key)
                .ToList();
            foreach (var key in incompleteKeys)
            {
                if (_moduleSettingsViews.TryGetValue(key, out var incompleteView) &&
                    ReferenceEquals(ModuleSettingsContainer.Content, incompleteView))
                {
                    ModuleSettingsContainer.Content = null;
                }

                _moduleSettingsViews.Remove(key);
                _moduleSettingsPresentations.Remove(key);
                _moduleSettingsPresentationsNeedingRefresh.Remove(key);
            }
        }


        // Category lookup and selector state

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


        // Footer actions

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
            // Without a dispatcher there is nothing to delay on, so update immediately.
            if (Dispatcher == null)
            {
                UpdateActionButtons();
                return;
            }

            // Only the first change in a burst schedules the update; later ones are covered by it.
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
            // Decide what the user may do now; the footer reflects unsaved edits of every category,
            // not only the open one.
            var canInteract = !_isSaving && !_isAslmAccountActionRunning && _activeCategory != null;
            var hasChanges = canInteract && HasAnyUnsavedChanges();
            var canReset = _activeCategory != null;

            // Visibility, availability and texts of the load-default, discard and save buttons.
            DefaultButton.IsVisible = canReset;
            DefaultButton.IsEnabled = canInteract && canReset;
            DiscardButton.IsVisible = canReset && hasChanges;
            DiscardButton.IsEnabled = canInteract && hasChanges;
            SaveButton.IsEnabled = canInteract;
            SaveButton.Text = _isSaving ? L.Get(LocalizationKeys.Settings_Saving) : L.Get(LocalizationKeys.Settings_Save);
            ApplyActionButtonState(DefaultButton, false);
            ApplyActionButtonState(DiscardButton, isPrimary: false, isDanger: true);

            // Without a category every action is hidden.
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

            // Save buttons appear only with changes; save-and-restart additionally needs a change
            // that supports a restart.
            var canShowRestart = hasChanges && HasPendingRestartChanges();

            SaveAndRestartButton.IsEnabled = canInteract && canShowRestart;
            SaveAndRestartButton.Text = L.Get(LocalizationKeys.Settings_SaveAndRestart);

            SaveButton.IsVisible = hasChanges;
            SaveAndRestartButton.IsVisible = canShowRestart;

            // Emphasize exactly one button: save-and-restart when offered, otherwise plain save.
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

            // Reassigning the same Style instance is often a no-op; clearing first forces DynamicResource
            // setters to rebind after Application.Resources palette updates (theme preview, custom colors).
            if (ReferenceEquals(button.Style, style))
            {
                button.Style = null;
            }

            button.Style = style;
        }
    }
}
