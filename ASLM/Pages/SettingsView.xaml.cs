// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using Debug = System.Diagnostics.Debug;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls.Shapes;

namespace ASLM.Pages
{
    /// <summary>
    /// Displays shared application settings and dynamic module settings inside the shell.
    /// </summary>
    public partial class SettingsView : ContentView, ILocalizable
    {
        private const string PasswordIconHidden = "icon_password_off.png";
        private const string PasswordIconVisible = "icon_password_on.png";
        private const double DialogWidthFactor = 0.8;
        private const double DialogHeightFactor = 0.8;
        private const double MinDialogWidth = 960;
        private const double MinDialogHeight = 540;
        private const double MaxDialogWidth = 1280;
        private const double MaxDialogHeight = 720;
        private static readonly TimeSpan OllamaSignInPollInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan OllamaSignInPollDuration = TimeSpan.FromMinutes(5);

        /// <summary>Keeps picker fields from stretching across the whole settings pane.</summary>
        private const double PersonalizationPickerMaxWidth = 256;

        private const string FooterButtonStyleKey = "SettingsFooterButtonStyle";
        private const string FooterPrimaryButtonStyleKey = "SettingsFooterPrimaryButtonStyle";
        private const string FooterDangerButtonStyleKey = "SettingsFooterDangerButtonStyle";
        private const string SelectorHeaderLabelStyleKey = "SettingsSelectorHeaderLabelStyle";
        private const string SelectorButtonBorderStyleKey = "SettingsSelectorButtonBorderStyle";
        private const string SelectorButtonLabelStyleKey = "SettingsSelectorButtonLabelStyle";
        private const string TransparentBorderStyleKey = "SettingsTransparentBorderStyle";
        private const string FieldBorderStyleKey = "SettingsFieldBorderStyle";
        private const string TextEntryStyleKey = "SettingsTextEntryStyle";
        private const string PickerStyleKey = "SettingsPickerStyle";
        private const string SubGroupHeaderLabelStyleKey = "SettingsSubGroupHeaderLabelStyle";
        private const string CardTitleLabelStyleKey = "SettingsCardTitleLabelStyle";
        private const string CardDescriptionLabelStyleKey = "SettingsCardDescriptionLabelStyle";
        private const string SecondaryLabelStyleKey = "SettingsSecondaryLabelStyle";
        private const string InlineActionButtonStyleKey = "SettingsInlineActionButtonStyle";
        private const string PasswordToggleImageStyleKey = "SettingsPasswordToggleImageStyle";

        private const double TitleDescriptionSpacing = 8;

        private readonly AppDataStore _appData;
        private readonly SettingsService _settingsService;
        private readonly AppLocalizationService _localization;
        private readonly OllamaSettingsStore _ollamaSettings;
        private readonly GitHubAccountStore _githubAccountStore;
        private readonly GitHubUpdateClient _githubUpdateClient;
        private readonly UpdateManager _updateManager;
        private readonly EngineInstaller _engineInstaller;
        private readonly AslmMirrorServer _mirrorServer;
        private readonly NotificationCenter _notifications;
        private readonly ThemeService _themeService;
        private readonly CustomThemesStore _customThemesStore;
        private readonly SunriseService _sunriseService;
        private readonly List<SettingControlMapping> _settingMappings = [];
        private readonly Dictionary<string, SettingBaseline> _settingBaselines = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _categoryButtons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _runtimeLoadedModuleIds = new(StringComparer.OrdinalIgnoreCase);
        private List<ModuleConfig> _loadedModules = [];
        private List<SettingsCategory> _categories = [];
        private SettingsCategory? _activeCategory;
        private AslmBaseline _aslmBaseline = new(string.Empty, string.Empty, true);
        private ConsoleBaseline _consoleBaseline = new(true, true, true);
        private UpdateBaseline _updateBaseline = new(true, false, "release", "release", "release");
        private ConsoleBaseline _consoleDraft = new(true, true, true);
        private UpdateBaseline _updateDraft = new(true, false, "release", "release", "release");
        private OllamaPersistentSettings _ollamaDraft = new();
        private GitHubAccountState _githubDraft = new();
        private string _userNameDraft = string.Empty;
        private string _portStartDraft = string.Empty;
        private bool _apiServerEnabledDraft = true;
        private bool _hasLoaded;
        private bool _isRefreshingVisibility;
        private bool _isSwitchingCategory;
        private bool _isSaving;
        private bool _isOllamaAccountActionRunning;
        private bool _isOllamaMetadataRefreshRunning;
        private bool _isGitHubAccountActionRunning;
        private bool _isAslmAccountActionRunning;
        private int _actionButtonUpdateQueued;
        private string _ollamaAccountAction = string.Empty;
        private Button? _ollamaAccountButton;
        private Label? _ollamaAccountStatusLabel;
        private Button? _githubAccountButton;
        private Label? _githubAccountStatusLabel;
        private Button? _aslmAccountButton;
        private Label? _aslmAccountStatusLabel;
        private CompactToggle? _checkUpdatesToggle;
        private CompactToggle? _autoUpdatesToggle;
        private Picker? _appUpdateChannelPicker;
        private Picker? _moduleUpdateModePicker;
        private Picker? _moduleUpdateChannelPicker;
        private Label? _updateStatusLabel;
        private Button? _prepareAppUpdateButton;
        private Button? _restartAppUpdateButton;
        private UpdateCandidate? _pendingAppUpdateCandidate;
        private Button? _ollamaUpdateButton;
        private Button? _ollamaCheckUpdateButton;
        private Label? _ollamaUpdateStatusLabel;
        private Label? _ollamaVersionDescriptionLabel;
        private UpdateCandidate? _pendingOllamaUpdateCandidate;
        private CompactToggle? _apiServerToggle;
        private CompactToggle? _consoleSidebarToggle;
        private CompactToggle? _consoleCompletedToggle;
        private CompactToggle? _consoleIndividualToggle;
        private CompactToggle? _legalAutoAcceptToggle;
        private bool _legalAutoAcceptDraft = true;
        private bool _legalAutoAcceptBaseline = true;
        private CancellationTokenSource? _ollamaMetadataRefreshCts;
        private CancellationTokenSource? _ollamaStatusPollingCts;
        private CancellationTokenSource? _aslmAccountActionCts;
        private bool _settingsReconcileTimerStarted;
        private bool _settingsReconcileTimerStopRequested;

        private AppPersonalizationConfig _personalizationDraft = new();
        private AppPersonalizationConfig _personalizationBaseline = new();
        private CustomTheme? _editingThemeDraft;
        private Picker? _appearancePicker;
        private Picker? _languagePicker;
        private VerticalStackLayout? _customThemeSection;
        private Picker? _customThemePicker;
        private VerticalStackLayout? _themeEditorSection;
        private bool _suppressCustomThemePickerEvents;

        /// <summary>
        /// Raised when the user asks to close the settings overlay.
        /// </summary>
        public event EventHandler? CloseRequested;

        /// <summary>
        /// Stores how one rendered control maps back to its module setting and current draft readers.
        /// </summary>
        private record SettingControlMapping(
            ModuleConfig Module,
            ModuleSetting Setting,
            Func<object?> ReadValue,
            Func<bool>? ReadCustomValue,
            string InitialDisplayValue,
            bool InitialUseCustomValue);

        /// <summary>
        /// Represents a lightweight settings toggle with a fixed visual and hitbox.
        /// </summary>
        private sealed class CompactToggle
        {
            /// <summary>Gets the on-state track color from the active application palette.</summary>
            private static Color ToggleOnColor => Application.Current?.Resources.TryGetValue("ActionBlue", out var v) == true && v is Color c ? c : Color.FromArgb("#0A84FF");
            /// <summary>Gets the off-state track color from the active application palette.</summary>
            private static Color ToggleOffColor => Application.Current?.Resources.TryGetValue("BackgroundTertiary", out var v) == true && v is Color c ? c : Color.FromArgb("#3A3A3C");
            /// <summary>Gets the thumb fill color from the active application palette.</summary>
            private static Color ToggleThumbColor => Application.Current?.Resources.TryGetValue("LabelPrimary", out var v) == true && v is Color c ? c : Colors.White;
            private const double TrackWidth = 36;
            private const double TrackHeight = 20;
            private const double ThumbSize = 16;
            private const double ThumbInset = 2;

            private class ThumbDrawable : IDrawable
            {
                /// <summary>
                /// Draws the toggle thumb as a filled circle on the graphics canvas.
                /// </summary>
                public void Draw(ICanvas canvas, RectF dirtyRect)
                {
                    canvas.Antialias = true;
                    canvas.FillColor = ToggleThumbColor;
                    canvas.FillCircle((float)ThumbSize / 2, (float)ThumbSize / 2, (float)ThumbSize / 2);
                }
            }

            private readonly AbsoluteLayout _layout;
            private readonly Border _track;
            private readonly GraphicsView _thumb;
            private bool _suppressTap;
            private double _dragStartOffset;

            /// <summary>
            /// Initializes a new compact toggle instance with the requested initial state.
            /// </summary>
            public CompactToggle(bool isToggled = false)
            {
                _track = new Border
                {
                    StrokeThickness = 0,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = 0,
                    WidthRequest = TrackWidth,
                    HeightRequest = TrackHeight,
                    MinimumWidthRequest = TrackWidth,
                    MinimumHeightRequest = TrackHeight,
                    VerticalOptions = LayoutOptions.Center
                };

                _thumb = new GraphicsView
                {
                    Drawable = new ThumbDrawable(),
                    WidthRequest = ThumbSize,
                    HeightRequest = ThumbSize,
                    MinimumWidthRequest = ThumbSize,
                    MinimumHeightRequest = ThumbSize,
                    VerticalOptions = LayoutOptions.Center
                };

                _layout = new AbsoluteLayout
                {
                    WidthRequest = TrackWidth,
                    HeightRequest = TrackHeight,
                    MinimumWidthRequest = TrackWidth,
                    MinimumHeightRequest = TrackHeight,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Center
                };

                AbsoluteLayout.SetLayoutBounds(_track, new Rect(0, 0, TrackWidth, TrackHeight));
                AbsoluteLayout.SetLayoutFlags(_track, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);
                AbsoluteLayout.SetLayoutBounds(_thumb, new Rect(ThumbInset, ThumbTopOffset, ThumbSize, ThumbSize));
                AbsoluteLayout.SetLayoutFlags(_thumb, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.None);

                _layout.Children.Add(_track);
                _layout.Children.Add(_thumb);
                _thumb.ZIndex = 1;
                View = _layout;

                var tapGesture = new TapGestureRecognizer();
                tapGesture.Tapped += (_, _) =>
                {
                    if (_suppressTap)
                    {
                        _suppressTap = false;
                        return;
                    }

                    IsToggled = !IsToggled;
                };
                _layout.GestureRecognizers.Add(tapGesture);

                var panGesture = new PanGestureRecognizer();
                panGesture.PanUpdated += OnPanUpdated;
                _layout.GestureRecognizers.Add(panGesture);

                _isToggled = isToggled;
                UpdateVisualState();
            }

            /// <summary>
            /// Gets the visual root used in the settings page layout.
            /// </summary>
            public View View { get; }

            /// <summary>
            /// Occurs when the toggle value changes.
            /// </summary>
            public event EventHandler<ToggledEventArgs>? Toggled;

            /// <summary>
            /// Gets or sets the current toggle state and updates the visual presentation.
            /// </summary>
            public bool IsToggled
            {
                get => _isToggled;
                set
                {
                    if (_isToggled == value)
                    {
                        return;
                    }

                    _isToggled = value;
                    UpdateVisualState();
                    Toggled?.Invoke(this, new ToggledEventArgs(value));
                }
            }

            private bool _isToggled;

            /// <summary>
            /// Updates internal and visual state without raising <see cref="Toggled"/>.
            /// </summary>
            public void SetStateWithoutToggleEvent(bool isToggled)
            {
                _isToggled = isToggled;
                UpdateVisualState();
            }

            /// <summary>
            /// Applies the correct track and thumb layout for the current toggle state.
            /// </summary>
            private void UpdateVisualState()
            {
                _track.Background = new SolidColorBrush(_isToggled ? ToggleOnColor : ToggleOffColor);
                SetThumbOffset(_isToggled ? MaxThumbOffset : 0);
            }

            /// <summary>
            /// Handles horizontal dragging for the compact toggle thumb.
            /// </summary>
            private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
            {
                switch (e.StatusType)
                {
                    case GestureStatus.Started:
                        _dragStartOffset = CurrentThumbOffset;
                        break;
                    case GestureStatus.Running:
                        _suppressTap = true;
                        MainThread.BeginInvokeOnMainThread(() => SetThumbOffset(ClampOffset(_dragStartOffset + e.TotalX)));
                        break;
                    case GestureStatus.Canceled:
                    case GestureStatus.Completed:
                        var nextState = CurrentThumbOffset >= MaxThumbOffset / 2;
                        MainThread.BeginInvokeOnMainThread(() => {
                            SetThumbOffset(nextState ? MaxThumbOffset : 0);
                            IsToggled = nextState;
                        });
                        break;
                }
            }

            /// <summary>
            /// Moves the thumb to the requested horizontal offset inside the track.
            /// </summary>
            private void SetThumbOffset(double offset)
            {
                AbsoluteLayout.SetLayoutBounds(_thumb, new Rect(ThumbInset + offset, ThumbTopOffset, ThumbSize, ThumbSize));
            }

            /// <summary>
            /// Restricts thumb movement to the bounds of the compact track.
            /// </summary>
            private static double ClampOffset(double offset)
            {
                if (offset < 0)
                {
                    return 0;
                }

                return offset > MaxThumbOffset ? MaxThumbOffset : offset;
            }

            /// <summary>
            /// Gets the maximum translation distance available to the thumb inside the track.
            /// </summary>
            private static double MaxThumbOffset => TrackWidth - ThumbSize - (ThumbInset * 2);

            /// <summary>
            /// Gets the top offset that vertically centers the thumb inside the track.
            /// </summary>
            private static double ThumbTopOffset => (TrackHeight - ThumbSize) / 2;

            /// <summary>
            /// Gets the current horizontal thumb offset relative to the left inset.
            /// </summary>
            private double CurrentThumbOffset => AbsoluteLayout.GetLayoutBounds(_thumb).X - ThumbInset;
        }


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
            GitHubUpdateClient githubUpdateClient,
            UpdateManager updateManager,
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
            _githubUpdateClient = githubUpdateClient;
            _updateManager = updateManager;
            _engineInstaller = engineInstaller;
            _mirrorServer = mirrorServer;
            _notifications = notifications;
            _themeService = themeService;
            _customThemesStore = customThemesStore;
            _sunriseService = sunriseService;
            InitializeComponent();
            LocalizableAttach.Hook(this, _localization, this);
            ApplyFlatEntryStyle(UsernameEntry);
            ApplyFlatEntryStyle(ModulePortEntry);
            ApplyScrollViewChrome(CategoryScroll, isSidebar: true);
            ApplyScrollViewChrome(SettingsScroll, isSidebar: false);
            UsernameEntry.TextChanged += (_, _) => QueueActionButtonUpdate();
            ModulePortEntry.TextChanged += (_, _) => QueueActionButtonUpdate();
            SizeChanged += OnViewSizeChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }


        // Refresh

        /// <summary>
        /// Reloads the settings page when the shell revisits it.
        /// </summary>
        public async Task RefreshAsync()
        {
            if (!_hasLoaded)
            {
                return;
            }

            try
            {
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh settings view: {ex.Message}");
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

            _categories = _settingsService.CreateOrderedCategories(_loadedModules);

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
        }

        /// <summary>
        /// Initializes the settings page once after the control is first shown.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            if (_hasLoaded)
            {
                StartSettingsReconcileTimer();
                return;
            }

            _hasLoaded = true;
            UpdateDialogSize();

            try
            {
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings view: {ex.Message}");
            }
            finally
            {
                StartSettingsReconcileTimer();
            }
        }

        /// <summary>
        /// Stops background Ollama status polling when the settings view leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            StopSettingsReconcileTimer();
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

            DisplayNameTitleLabel.Text = L.Get(LocalizationKeys.Settings_DisplayName);
            DisplayNameDescriptionLabel.Text = L.Get(LocalizationKeys.Settings_DisplayNameDescription);
            PortsGroupHeader.Text = L.Get(LocalizationKeys.Settings_Ports);
            ModulePortTitleLabel.Text = L.Get(LocalizationKeys.Settings_ModulePortTitle);
            ModulePortDescriptionLabel.Text = L.Get(LocalizationKeys.Settings_ModulePortDescription);

            DefaultButton.Text = L.Get(LocalizationKeys.Settings_LoadDefault);
            DiscardButton.Text = L.Get(LocalizationKeys.Settings_DiscardChanges);
            UpdateActionButtons();

            if (_categories.Count > 0)
            {
                BuildCategorySelectors();
            }

            if (_activeCategory != null)
            {
                ActivateCategory(_activeCategory);
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
            _personalizationDraft = new AppPersonalizationConfig
            {
                Appearance = AppPersonalizationConfig.NormalizeAppearance(stored.Appearance),
                Language = AppPersonalizationConfig.NormalizeLanguage(stored.Language),
                CustomThemeId = stored.CustomThemeId
            };
            _personalizationBaseline = new AppPersonalizationConfig
            {
                Appearance = _personalizationDraft.Appearance,
                Language = _personalizationDraft.Language,
                CustomThemeId = _personalizationDraft.CustomThemeId
            };
        }

        /// <summary>
        /// Copies the persisted shared settings into the editable page draft.
        /// </summary>
        private void LoadAslmDraftsFromAppData()
        {
            var snapshot = SettingsService.BuildAslmDraftSnapshot(_appData, _mirrorServer.IsEnabled);
            _userNameDraft = snapshot.UserName;
            _portStartDraft = snapshot.PortStart;
            _apiServerEnabledDraft = snapshot.ApiServerEnabled;
            _aslmBaseline = new AslmBaseline(_userNameDraft, _portStartDraft, _apiServerEnabledDraft);
            _consoleBaseline = snapshot.ConsoleBaseline;
            _consoleDraft = _consoleBaseline;
            _updateBaseline = snapshot.UpdateBaseline;
            _updateDraft = _updateBaseline;
            _legalAutoAcceptDraft = _appData.Data.Legal.AutoAcceptUpdates;
            _legalAutoAcceptBaseline = _legalAutoAcceptDraft;

            ApplyAslmDraftsToControls();
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
        /// Reloads module settings and refreshes the baseline snapshots used by save detection.
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
                _settingBaselines.Clear();
            }

            if (reloadRuntimeValues)
            {
                _runtimeLoadedModuleIds.Clear();
                _settingBaselines.Clear();
            }

            foreach (var module in _loadedModules)
            {
                await _settingsService.LoadModuleDraftAsync(module, reloadRuntimeValues, _settingBaselines);
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
            _categoryButtons.Clear();
            CategoryPanel.Children.Clear();

            // ASLM Group
            CategoryPanel.Children.Add(CreateSelectorHeader(L.Get(LocalizationKeys.Settings_Category_ASLM)));
            foreach (var category in _categories.Where(c => SettingsService.GetGroupForCategory(c) == SettingsCategoryGroup.Aslm))
            {
                var button = CreateSelectorButton(GetLocalizedCategoryTitle(category));
                button.BindingContext = category;
                var tapGesture = new TapGestureRecognizer();
                tapGesture.Tapped += OnCategorySelectorClicked;
                button.GestureRecognizers.Add(tapGesture);
                CategoryPanel.Children.Add(button);
                _categoryButtons[category.Id] = button;
            }

            // Modules Group
            var modCategories = _categories.Where(c => SettingsService.GetGroupForCategory(c) == SettingsCategoryGroup.Modules).ToList();
            if (modCategories.Count > 0)
            {
                CategoryPanel.Children.Add(new BoxView { HeightRequest = 12, Color = Colors.Transparent });
                CategoryPanel.Children.Add(CreateSelectorHeader(L.Get(LocalizationKeys.Settings_Header_Modules)));
                foreach (var category in modCategories)
                {
                    var button = CreateSelectorButton(GetLocalizedCategoryTitle(category));
                    button.BindingContext = category;
                    var tapGesture = new TapGestureRecognizer();
                    tapGesture.Tapped += OnCategorySelectorClicked;
                    button.GestureRecognizers.Add(tapGesture);
                    CategoryPanel.Children.Add(button);
                    _categoryButtons[category.Id] = button;
                }
            }

            UpdateSelectorButtonStates();
        }

        /// <summary>
        /// Creates a category group header label for the sidebar.
        /// </summary>
        private static Label CreateSelectorHeader(string text) =>
            new()
            {
                Text = text,
                Style = GetStyleResource(SelectorHeaderLabelStyleKey)
            };

        /// <summary>
        /// Creates one sidebar selector button for a specific settings category.
        /// </summary>
        private static Border CreateSelectorButton(string text)
        {
            var border = new Border
            {
                Style = GetStyleResource(SelectorButtonBorderStyleKey)
            };

            var label = new Label
            {
                Text = text,
                Style = GetStyleResource(SelectorButtonLabelStyleKey)
            };

            border.Content = label;

            return border;
        }

        /// <summary>
        /// Handles clicks on the per-category selector buttons.
        /// </summary>
        private async void OnCategorySelectorClicked(object? sender, EventArgs e)
        {
            if (sender is Border { BindingContext: SettingsCategory category })
            {
                await TrySelectCategoryAsync(category);
            }
        }

        /// <summary>
        /// Switches to the requested category after preserving or discarding pending edits.
        /// </summary>
        private async Task TrySelectCategoryAsync(SettingsCategory category)
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

                if (leavingPersonalization)
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
        /// Activates the selected category and rebuilds the visible settings content.
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

                    if (stillActive && HasUnsavedChanges())
                    {
                        return;
                    }

                    _settingsService.UpdateSettingBaselines(module, loaded, _settingBaselines);
                    foreach (var item in loaded)
                    {
                        if (!item.Setting.IsAutomaticallyManaged || item.Setting.UseCustomValue)
                        {
                            item.Setting.Value = item.Value;
                        }
                    }

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
            await _settingsService.LoadModuleDraftAsync(module, reloadRuntimeValues: true, _settingBaselines);
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
            foreach (var pair in _categoryButtons)
            {
                ApplySelectorButtonState(pair.Value, _activeCategory != null && pair.Key.Equals(_activeCategory.Id, StringComparison.OrdinalIgnoreCase));
                pair.Value.IsEnabled = !_isSaving;
            }
        }

        /// <summary>
        /// Reapplies category selector colors and footer action button styles after the palette is rewritten
        /// (preview or apply). Selector rows use explicit <see cref="Color"/> values; footer buttons reuse keyed
        /// <see cref="Style"/> instances, so <see cref="ApplyActionButtonState"/> must run again for
        /// <see cref="DynamicResource"/> bindings to pick up updated resource entries.
        /// </summary>
        private void RefreshCategorySelectorChromeFromResources()
        {
            UpdateSelectorButtonStates();
            if (_hasLoaded)
            {
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Applies the selected visual state to one selector button.
        /// </summary>
        private static void ApplySelectorButtonState(Border button, bool isActive)
        {
            if (button.Content is Label label)
            {
                label.TextColor = isActive
                    ? GetColorResource("LabelPrimary", Colors.White)
                    : GetColorResource("LabelSecondary", Color.FromArgb("#99EBEBF5"));
                label.FontAttributes = FontAttributes.None;
            }

            button.BackgroundColor = isActive
                ? GetColorResource("BackgroundTertiary", Color.FromArgb("#FF2C2C2E"))
                : Colors.Transparent;
            button.Opacity = isActive ? 1.0 : 0.92;
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
        /// Starts a periodic pass that copies visible control values into drafts and refreshes footer buttons.
        /// Mitigates WinUI timing where compact toggles and the save bar can disagree until a later layout pass.
        /// </summary>
        private void StartSettingsReconcileTimer()
        {
            if (Dispatcher == null || _settingsReconcileTimerStarted)
            {
                return;
            }

            _settingsReconcileTimerStopRequested = false;
            _settingsReconcileTimerStarted = true;
            Dispatcher.StartTimer(TimeSpan.FromSeconds(2), OnSettingsReconcileTimerTick);
        }

        /// <summary>
        /// Stops the periodic settings/UI reconcile timer started with <see cref="StartSettingsReconcileTimer"/>.
        /// </summary>
        private void StopSettingsReconcileTimer()
        {
            _settingsReconcileTimerStopRequested = true;
        }

        /// <summary>
        /// Periodically reconciles visible controls with drafts while the settings overlay is open.
        /// </summary>
        private bool OnSettingsReconcileTimerTick()
        {
            if (_settingsReconcileTimerStopRequested || !IsLoaded)
            {
                _settingsReconcileTimerStarted = false;
                return false;
            }

            if (!_hasLoaded || _activeCategory == null || _isSaving)
            {
                return true;
            }

            ReconcileSettingsVisibleControlsWithDrafts();
            return true;
        }

        /// <summary>
        /// Pulls visible values into in-memory drafts (including API/Consoles on the ASLM page) and updates save/discard visibility.
        /// </summary>
        private void ReconcileSettingsVisibleControlsWithDrafts()
        {
            if (!_hasLoaded || _activeCategory == null || _isSaving)
            {
                return;
            }

            if (_activeCategory.Kind == SettingsCategoryKind.Aslm)
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
            }

            SyncDraftValuesFromControls();
            UpdateActionButtons();
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
            HasUnsavedAslmSettingsChanges() ||
            GetModulesWithUnsavedChanges().Any(CanRestartModule);

        /// <summary>
        /// Returns modules with unsaved settings, including the currently edited module controls.
        /// </summary>
        private List<ModuleConfig> GetModulesWithUnsavedChanges()
        {
            var result = new List<ModuleConfig>();
            foreach (var module in _loadedModules)
            {
                var isActiveModule =
                    _activeCategory?.Kind == SettingsCategoryKind.Module &&
                    _activeCategory.Module != null &&
                    string.Equals(_activeCategory.Module.SourcePath, module.SourcePath, StringComparison.OrdinalIgnoreCase);

                var hasChanges = isActiveModule && _settingMappings.Count > 0
                    ? HasUnsavedModuleChanges()
                    : _settingsService.ModuleHasChangesComparedToBaseline(module, _settingBaselines);

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
            button.BorderWidth = 0;
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


        // Rendering

        /// <summary>
        /// Shows the combined ASLM settings category while hiding module-specific content.
        /// </summary>
        private void RenderAslmCategory()
        {
            ApplyAslmDraftsToControls();
            _settingMappings.Clear();
            ResetRenderedControlReferences();
            PrepareCategorySurface(showAslmContainer: true, showModuleContainer: true, showEmptyState: false);
            AccountModeSection.IsVisible = false;
            AccountModeSection.Children.Clear();
            UserProfileSection.IsVisible = false;
            PortsSection.IsVisible = true;

            var section = CreateModuleSectionBorder();
            var content = new VerticalStackLayout { Spacing = 8 };

            content.Children.Add(CreateSubGroupHeader(L.Get(LocalizationKeys.Settings_SubGroup_API)));
            AddAslmApiSettings(content);

            content.Children.Add(CreateSubGroupHeader(L.Get(LocalizationKeys.Settings_SubGroup_Consoles)));
            AddConsoleSettings(content);

            content.Children.Add(CreateSubGroupHeader(L.Get(LocalizationKeys.Settings_SubGroup_Legal)));
            AddLegalSettings(content);

            section.Content = content;
            ModuleSettingsContainer.Children.Add(section);
        }

        /// <summary>
        /// Shows the combined accounts category with ASLM, GitHub, and Ollama sections.
        /// </summary>
        private void RenderAccountsCategory()
        {
            ApplyAslmDraftsToControls();
            _settingMappings.Clear();
            ResetRenderedControlReferences();
            PrepareCategorySurface(showAslmContainer: true, showModuleContainer: true, showEmptyState: false);
            AccountModeSection.Children.Clear();
            AccountModeSection.Children.Add(CreateAslmAccountCard());
            AccountModeSection.IsVisible = true;
            UserProfileSection.IsVisible = !_sunriseService.IsCloudAccount;
            PortsSection.IsVisible = false;

            var section = CreateModuleSectionBorder();
            var content = new VerticalStackLayout { Spacing = 8 };

            content.Children.Add(CreateGitHubAccountCard());
            content.Children.Add(CreateOllamaAccountCard());

            section.Content = content;
            ModuleSettingsContainer.Children.Add(section);

            _githubDraft = _githubAccountStore.GetState();
            UpdateAslmAccountActionControls();
            UpdateGitHubAccountActionControls();
            UpdateOllamaAccountActionControls();
            StartOllamaMetadataRefresh();
        }

        /// <summary>
        /// Shows the dedicated updates category.
        /// </summary>
        private void RenderUpdatesCategory()
        {
            _settingMappings.Clear();
            ResetRenderedControlReferences();
            PrepareCategorySurface(showAslmContainer: false, showModuleContainer: true, showEmptyState: false);

            var section = CreateModuleSectionBorder();
            var content = new VerticalStackLayout { Spacing = 8 };
            AddUpdateSettings(content);

            section.Content = content;
            ModuleSettingsContainer.Children.Add(section);
        }

        /// <summary>
        /// Adds the local ASLM API setting rows to the combined ASLM category.
        /// </summary>
        private void AddAslmApiSettings(Layout content)
        {
            _apiServerToggle = CreateCompactToggle(_apiServerEnabledDraft);
            _apiServerToggle.Toggled += (_, _) =>
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
                QueueActionButtonUpdate();
            };
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_ApiServer_Title),
                L.Get(LocalizationKeys.Settings_ApiServer_Description),
                _apiServerToggle.View));
        }

        /// <summary>
        /// Adds the console page setting rows to the combined ASLM category.
        /// </summary>
        private void AddConsoleSettings(Layout content)
        {
            _consoleSidebarToggle = CreateCompactToggle(_consoleDraft.SidebarVisible);
            _consoleSidebarToggle.Toggled += (_, _) =>
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
                QueueActionButtonUpdate();
            };
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_ConsolesPage_Title),
                L.Get(LocalizationKeys.Settings_ConsolesPage_Description),
                _consoleSidebarToggle.View));

            _consoleIndividualToggle = CreateCompactToggle(_consoleDraft.ShowIndividualModuleConsoles);
            _consoleIndividualToggle.Toggled += (_, _) =>
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
                QueueActionButtonUpdate();
            };
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_IndividualConsoles_Title),
                L.Get(LocalizationKeys.Settings_IndividualConsoles_Description),
                _consoleIndividualToggle.View));

            _consoleCompletedToggle = CreateCompactToggle(_consoleDraft.ShowCompletedProcesses);
            _consoleCompletedToggle.Toggled += (_, _) =>
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
                QueueActionButtonUpdate();
            };
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_CompletedConsoles_Title),
                L.Get(LocalizationKeys.Settings_CompletedConsoles_Description),
                _consoleCompletedToggle.View));
        }

        /// <summary>
        /// Adds legal document acceptance setting rows to the combined ASLM category.
        /// </summary>
        private void AddLegalSettings(Layout content)
        {
            _legalAutoAcceptToggle = CreateCompactToggle(_legalAutoAcceptDraft);
            _legalAutoAcceptToggle.Toggled += (_, _) =>
            {
                RefreshAslmApiAndConsoleDraftsFromToggles();
                QueueActionButtonUpdate();
            };
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_Legal_AutoAcceptUpdates_Title),
                L.Get(LocalizationKeys.Settings_Legal_AutoAcceptUpdates_Description),
                _legalAutoAcceptToggle.View));
        }

        /// <summary>
        /// Adds update setting rows to the combined ASLM category.
        /// </summary>
        private void AddUpdateSettings(Layout content)
        {
            _checkUpdatesToggle = CreateCompactToggle(_updateDraft.CheckEnabled);
            _checkUpdatesToggle.Toggled += (_, _) => QueueActionButtonUpdate();
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_CheckUpdates_Title),
                L.Get(LocalizationKeys.Settings_CheckUpdates_Description),
                _checkUpdatesToggle.View));

            _autoUpdatesToggle = CreateCompactToggle(_updateDraft.AutoUpdateEnabled);
            _autoUpdatesToggle.Toggled += (_, _) => QueueActionButtonUpdate();
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_AutoInstall_Title),
                L.Get(LocalizationKeys.Settings_AutoInstall_Description),
                _autoUpdatesToggle.View));

            _appUpdateChannelPicker = CreatePicker(null, 13);
            _appUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };
            _appUpdateChannelPicker.SelectedItem = _updateDraft.AppChannel;
            _appUpdateChannelPicker.SelectedIndexChanged += (_, _) => QueueActionButtonUpdate();
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_AppChannel_Title),
                L.Get(LocalizationKeys.Settings_AppChannel_Description),
                CreateUpdatePickerContainer(_appUpdateChannelPicker)));

            _moduleUpdateModePicker = CreatePicker(null, 13);
            _moduleUpdateModePicker.ItemsSource = new List<string> { "release", "branch" };
            _moduleUpdateModePicker.SelectedItem = _updateDraft.ModuleDefaultMode;
            _moduleUpdateModePicker.SelectedIndexChanged += (_, _) => QueueActionButtonUpdate();
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_ModuleUpdateMode_Title),
                L.Get(LocalizationKeys.Settings_ModuleUpdateMode_Description),
                CreateUpdatePickerContainer(_moduleUpdateModePicker)));

            _moduleUpdateChannelPicker = CreatePicker(null, 13);
            _moduleUpdateChannelPicker.ItemsSource = new List<string> { "release", "pre-release" };
            _moduleUpdateChannelPicker.SelectedItem = _updateDraft.ModuleDefaultChannel;
            _moduleUpdateChannelPicker.SelectedIndexChanged += (_, _) => QueueActionButtonUpdate();
            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_ModuleChannel_Title),
                L.Get(LocalizationKeys.Settings_ModuleChannel_Description),
                CreateUpdatePickerContainer(_moduleUpdateChannelPicker)));

            content.Children.Add(CreateManualUpdateCard());
            content.Children.Add(CreateOllamaUpdateCard());
            _ = RefreshOllamaVersionDisplayAsync();
        }

        /// <summary>
        /// Creates the Ollama engine update card shown in the Updates category.
        /// </summary>
        private Border CreateOllamaUpdateCard()
        {
            var card = CreateSettingCardBorder();
            var content = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };
            var engine = ResolveOllamaEngineConfig();
            var currentVersion = engine?.Status.Installed == true
                ? ResolveOllamaDisplayVersion(engine)
                : "-";

            content.Children.Add(CreateCardTitle(L.Get(LocalizationKeys.Settings_OllamaUpdate_Title)));
            _ollamaVersionDescriptionLabel = CreateCardDescription(
                L.Get(LocalizationKeys.Settings_OllamaUpdate_Description, currentVersion));
            content.Children.Add(_ollamaVersionDescriptionLabel);

            var actions = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            _ollamaCheckUpdateButton = CreateInlineActionButton(
                L.Get(LocalizationKeys.Settings_CheckOllamaUpdates),
                OnCheckOllamaUpdatesClicked);
            _ollamaCheckUpdateButton.IsEnabled = engine?.Status.Installed == true;
            _ollamaUpdateButton = CreateInlineActionButton(
                L.Get(LocalizationKeys.Settings_OllamaUpdate_Button),
                OnUpdateOllamaClicked);
            _ollamaUpdateButton.IsVisible = false;
            _ollamaUpdateButton.IsEnabled = engine?.Status.Installed == true;
            actions.Children.Add(_ollamaCheckUpdateButton);
            actions.Children.Add(_ollamaUpdateButton);

            _ollamaUpdateStatusLabel = CreateSecondaryLabel(BuildInitialOllamaUpdateStatusText());

            content.Children.Add(actions);
            content.Children.Add(_ollamaUpdateStatusLabel);
            card.Content = content;
            return card;
        }

        /// <summary>
        /// Returns the managed Ollama engine manifest when it is present on disk.
        /// </summary>
        private EngineConfig? ResolveOllamaEngineConfig()
        {
            return _engineInstaller.DiscoverEngines()
                .FirstOrDefault(engine =>
                    string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the best available installed version label for the Ollama engine card.
        /// </summary>
        private static string ResolveOllamaDisplayVersion(EngineConfig engine)
        {
            if (!string.IsNullOrWhiteSpace(engine.Status.InstalledReleaseTag) &&
                !IsPlaceholderEngineReleaseTag(engine.Status.InstalledReleaseTag))
            {
                return engine.Status.InstalledReleaseTag.Trim();
            }

            if (!string.IsNullOrWhiteSpace(engine.Status.InstalledVersion) &&
                !IsPlaceholderEngineReleaseTag(engine.Status.InstalledVersion))
            {
                return engine.Status.InstalledVersion.Trim();
            }

            return "—";
        }

        /// <summary>
        /// Returns whether an engine version value is a manifest placeholder rather than a release tag.
        /// </summary>
        private static bool IsPlaceholderEngineReleaseTag(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds the Ollama update status label shown before a check runs in this session.
        /// </summary>
        private string BuildInitialOllamaUpdateStatusText() =>
            L.Get(LocalizationKeys.Settings_UpdateStatus_None);

        /// <summary>
        /// Resolves and refreshes only the installed Ollama version label in the Updates category.
        /// </summary>
        private async Task RefreshOllamaVersionDisplayAsync()
        {
            var engine = ResolveOllamaEngineConfig();
            if (engine?.Status.Installed != true)
            {
                return;
            }

            try
            {
                await _updateManager.TrySyncEngineInstalledReleaseTagFromRuntimeAsync(engine, isManualRequest: false);
                _engineInstaller.InvalidateCache();
                engine = ResolveOllamaEngineConfig();
                if (engine == null)
                {
                    return;
                }

                var version = ResolveOllamaDisplayVersion(engine);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (_ollamaVersionDescriptionLabel != null)
                    {
                        _ollamaVersionDescriptionLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Description,
                            version);
                    }
                });
            }
            catch
            {
                // Version sync is best-effort UI enrichment only.
            }
        }

        /// <summary>
        /// Creates one update setting card with a title, description, and trailing control.
        /// </summary>
        private static Border CreateUpdateCard(string title, string description, View control)
        {
            var card = CreateSettingCardBorder();
            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 8,
                MinimumHeightRequest = 32
            };

            var text = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };
            text.Children.Add(CreateCardTitle(title));
            text.Children.Add(CreateCardDescription(description));

            grid.Children.Add(text);
            Grid.SetColumn(text, 0);

            control.HorizontalOptions = LayoutOptions.End;
            control.VerticalOptions = LayoutOptions.Center;
            grid.Children.Add(control);
            Grid.SetColumn(control, 1);

            card.Content = grid;
            return card;
        }

        /// <summary>
        /// Creates the manual update action card.
        /// </summary>
        private Border CreateManualUpdateCard()
        {
            var card = CreateSettingCardBorder();
            var content = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };

            content.Children.Add(CreateCardTitle(L.Get(LocalizationKeys.Settings_ManualCheck_Title)));
            var installedReleaseSummary = SettingsService.BuildAslmInstalledReleaseSummary(_appData);
            if (!string.IsNullOrWhiteSpace(installedReleaseSummary))
            {
                content.Children.Add(CreateCardDescription(installedReleaseSummary));
            }

            _updateStatusLabel = CreateSecondaryLabel(BuildInitialManualUpdateStatusText());

            var actions = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            var checkButton = CreateInlineActionButton(
                L.Get(LocalizationKeys.Settings_CheckAslmUpdates),
                OnCheckAslmUpdatesClicked);
            _prepareAppUpdateButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_PrepareAslmUpdate), OnPrepareAppUpdateClicked);
            _prepareAppUpdateButton.IsVisible = false;
            _restartAppUpdateButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_RestartNow), OnRestartNowClicked);
            _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
            actions.Children.Add(checkButton);
            actions.Children.Add(_prepareAppUpdateButton);
            actions.Children.Add(_restartAppUpdateButton);

            content.Children.Add(actions);
            content.Children.Add(_updateStatusLabel);
            card.Content = content;
            return card;
        }

        /// <summary>
        /// Builds the manual-check status label shown before a check runs in this session.
        /// </summary>
        private string BuildInitialManualUpdateStatusText()
        {
            if (_updateManager.HasPendingAppUpdate)
            {
                var pending = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                return string.IsNullOrWhiteSpace(pending)
                    ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                    : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, pending);
            }

            return L.Get(LocalizationKeys.Settings_UpdateStatus_None);
        }

        /// <summary>
        /// Builds the ASLM manual-check summary after <see cref="UpdateManager.CheckAppUpdateAsync"/> completes.
        /// </summary>
        private string BuildAslmManualUpdateCheckStatusMessage()
        {
            var appTag = _pendingAppUpdateCandidate != null
                ? (_pendingAppUpdateCandidate.ReleaseTag ?? _pendingAppUpdateCandidate.RemoteVersion).Trim()
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(appTag))
            {
                if (_updateManager.HasPendingAppUpdate)
                {
                    var pending = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                    if (!string.IsNullOrWhiteSpace(pending) &&
                        string.Equals(pending, appTag, StringComparison.OrdinalIgnoreCase))
                    {
                        return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmTagPrepared, appTag);
                    }

                    if (!string.IsNullOrWhiteSpace(pending))
                    {
                        return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmAvailableStaged, appTag, pending);
                    }
                }

                return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmAvailable, appTag);
            }

            if (_updateManager.HasPendingAppUpdate)
            {
                var pendingOnly = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                return string.IsNullOrWhiteSpace(pendingOnly)
                    ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                    : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, pendingOnly);
            }

            return L.Get(LocalizationKeys.Settings_UpdateStatus_UpToDate);
        }

        /// <summary>
        /// Runs a manual ASLM update check and exposes self-update preparation when available.
        /// </summary>
        private async void OnCheckAslmUpdatesClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            try
            {
                _pendingAppUpdateCandidate = null;
                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = false;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
                }

                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Checking);
                }

                _pendingAppUpdateCandidate = await Task.Run(() =>
                    _updateManager.CheckAppUpdateAsync(isManualRequest: true));

                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = _pendingAppUpdateCandidate != null &&
                                                        !_updateManager.HasPendingAppUpdate;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
                }

                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = BuildAslmManualUpdateCheckStatusMessage();
                }
            }
            catch (Exception ex)
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Runs a manual Ollama engine update check and exposes the update action when available.
        /// </summary>
        private async void OnCheckOllamaUpdatesClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            try
            {
                _pendingOllamaUpdateCandidate = null;
                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = false;
                }

                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Checking);
                }

                var engine = ResolveOllamaEngineConfig();
                if (engine?.Status.Installed != true)
                {
                    if (_ollamaUpdateStatusLabel != null)
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, "Ollama is not installed.");
                    }

                    return;
                }

                _pendingOllamaUpdateCandidate = await Task.Run(() =>
                    _updateManager.CheckEngineUpdateAsync(engine, isManualRequest: true));

                await RefreshOllamaVersionDisplayAsync();
                engine = ResolveOllamaEngineConfig();

                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = _pendingOllamaUpdateCandidate != null;
                }

                if (_ollamaUpdateStatusLabel != null)
                {
                    if (_pendingOllamaUpdateCandidate != null)
                    {
                        var ollamaTag = (_pendingOllamaUpdateCandidate.ReleaseTag ??
                                         _pendingOllamaUpdateCandidate.RemoteVersion).Trim();
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Available,
                            ollamaTag);
                    }
                    else if (engine?.Status.Installed == true)
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_UpToDate);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Downloads and applies an available Ollama engine update.
        /// </summary>
        private async void OnUpdateOllamaClicked(object? sender, EventArgs e)
        {
            if (_pendingOllamaUpdateCandidate == null)
            {
                return;
            }

            if (sender is Button updateButton)
            {
                updateButton.IsEnabled = false;
            }

            try
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_Updating);
                }

                var candidate = _pendingOllamaUpdateCandidate;
                var log = new Progress<string>(message =>
                {
                    if (_ollamaUpdateStatusLabel != null)
                    {
                        MainThread.BeginInvokeOnMainThread(() => _ollamaUpdateStatusLabel.Text = message);
                    }
                });

                var success = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(
                    candidate,
                    log,
                    isManualRequest: true));

                if (_ollamaUpdateStatusLabel != null)
                {
                    if (success)
                    {
                        var installedTag = (candidate.ReleaseTag ?? candidate.RemoteVersion).Trim();
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Success,
                            installedTag);
                        if (_ollamaVersionDescriptionLabel != null)
                        {
                            _ollamaVersionDescriptionLabel.Text = L.Get(
                                LocalizationKeys.Settings_OllamaUpdate_Description,
                                installedTag);
                        }

                        _pendingOllamaUpdateCandidate = null;
                    }
                    else
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Failed,
                            "The update could not be applied.");
                    }
                }

                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = !success && _pendingOllamaUpdateCandidate != null;
                }
            }
            catch (Exception ex)
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(
                        LocalizationKeys.Settings_OllamaUpdate_Failed,
                        ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Downloads an available ASLM build and writes the pending update manifest for the patcher.
        /// </summary>
        private async void OnPrepareAppUpdateClicked(object? sender, EventArgs e)
        {
            if (_pendingAppUpdateCandidate == null)
            {
                return;
            }

            if (sender is Button prepareButton)
            {
                prepareButton.IsEnabled = false;
            }

            try
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Downloading);
                }

                var log = new Progress<string>(message =>
                {
                    if (_updateStatusLabel != null)
                    {
                        MainThread.BeginInvokeOnMainThread(() => _updateStatusLabel.Text = message);
                    }
                });

                var success = await Task.Run(() => _updateManager.PrepareAppUpdateAsync(
                    _pendingAppUpdateCandidate,
                    log,
                    isManualRequest: true));
                if (_updateStatusLabel != null)
                {
                    if (success)
                    {
                        var preparedTag = _pendingAppUpdateCandidate?.ReleaseTag ?? _pendingAppUpdateCandidate?.RemoteVersion;
                        _updateStatusLabel.Text = string.IsNullOrWhiteSpace(preparedTag)
                            ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                            : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, preparedTag.Trim());
                    }
                    else
                    {
                        _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CouldNotPrepare);
                    }
                }

                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = !success;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = success || _updateManager.HasPendingAppUpdate;
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Restarts through the launcher so the prepared ASLM update can be applied by the patcher.
        /// </summary>
        private async void OnRestartNowClicked(object? sender, EventArgs e)
        {
            if (sender is Button restartButton)
            {
                restartButton.IsEnabled = false;
            }

            try
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Restarting);
                }

                await RestartApplicationThroughLauncherAsync();
            }
            catch (Exception ex)
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_RestartFailed, ex.Message);
                }

                if (sender is Button failedButton)
                {
                    failedButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Builds the ASLM local/cloud account card shown at the top of the Accounts category.
        /// </summary>
        private Border CreateAslmAccountCard()
        {
            var card = CreateSettingCardBorder();
            var row = new Grid
            {
                ColumnSpacing = 16,
                VerticalOptions = LayoutOptions.Center,
                MinimumHeightRequest = 44
            };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var text = new VerticalStackLayout
            {
                Spacing = TitleDescriptionSpacing,
                VerticalOptions = LayoutOptions.Center
            };

            var title = CreateCardTitle(L.Get(LocalizationKeys.Settings_AslmAccount_Title));
            title.VerticalOptions = LayoutOptions.Center;
            text.Children.Add(title);

            _aslmAccountStatusLabel = CreateSecondaryLabel(BuildAslmAccountStatusText());
            _aslmAccountStatusLabel.LineBreakMode = LineBreakMode.WordWrap;
            _aslmAccountStatusLabel.MaxLines = 2;
            text.Children.Add(_aslmAccountStatusLabel);

            row.Children.Add(text);
            Grid.SetColumn(text, 0);

            _aslmAccountButton = CreateInlineActionButton(
                _sunriseService.IsCloudAccount
                    ? L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocal)
                    : L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToCloud),
                OnAslmAccountButtonClicked);
            row.Children.Add(_aslmAccountButton);
            Grid.SetColumn(_aslmAccountButton, 1);

            card.Content = row;
            return card;
        }

        /// <summary>
        /// Builds the GitHub account card shown in the Accounts category.
        /// </summary>
        private Border CreateGitHubAccountCard()
        {
            var card = CreateSettingCardBorder();
            var row = new Grid
            {
                ColumnSpacing = 16,
                VerticalOptions = LayoutOptions.Center,
                MinimumHeightRequest = 44
            };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var text = new VerticalStackLayout
            {
                Spacing = TitleDescriptionSpacing,
                VerticalOptions = LayoutOptions.Center
            };

            var title = CreateCardTitle(L.Get(LocalizationKeys.Settings_GitHub_TokenTitle));
            title.VerticalOptions = LayoutOptions.Center;
            text.Children.Add(title);

            _githubAccountStatusLabel = CreateSecondaryLabel(BuildGitHubAccountStatusText());
            _githubAccountStatusLabel.LineBreakMode = LineBreakMode.TailTruncation;
            _githubAccountStatusLabel.MaxLines = 2;
            text.Children.Add(_githubAccountStatusLabel);

            row.Children.Add(text);
            Grid.SetColumn(text, 0);

            _githubAccountButton = CreateInlineActionButton(
                IsGitHubConnected() ? L.Get(LocalizationKeys.Settings_GitHub_Disconnect) : L.Get(LocalizationKeys.Settings_GitHub_Connect),
                OnGitHubAccountButtonClicked);
            row.Children.Add(_githubAccountButton);
            Grid.SetColumn(_githubAccountButton, 1);

            card.Content = row;
            return card;
        }

        /// <summary>
        /// Builds the Ollama account card shown in the Accounts category.
        /// </summary>
        private Border CreateOllamaAccountCard()
        {
            var card = CreateSettingCardBorder();
            var row = new Grid
            {
                ColumnSpacing = 16,
                VerticalOptions = LayoutOptions.Center,
                MinimumHeightRequest = 44
            };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var text = new VerticalStackLayout
            {
                Spacing = TitleDescriptionSpacing,
                VerticalOptions = LayoutOptions.Center
            };

            var title = CreateCardTitle(L.Get(LocalizationKeys.Settings_OllamaAccount_Title));
            title.VerticalOptions = LayoutOptions.Center;
            text.Children.Add(title);

            _ollamaAccountStatusLabel = CreateSecondaryLabel(L.Get(LocalizationKeys.Settings_Ollama_NotSignedIn));
            _ollamaAccountStatusLabel.LineBreakMode = LineBreakMode.TailTruncation;
            _ollamaAccountStatusLabel.MaxLines = 1;
            text.Children.Add(_ollamaAccountStatusLabel);

            row.Children.Add(text);
            Grid.SetColumn(text, 0);

            _ollamaAccountButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_Ollama_SignIn), OnOllamaAccountButtonClicked);
            row.Children.Add(_ollamaAccountButton);
            Grid.SetColumn(_ollamaAccountButton, 1);

            card.Content = row;
            return card;
        }

        /// <summary>
        /// Rebuilds the currently visible module settings page from the in-memory draft state.
        /// </summary>
        private void RenderModuleCategory(ModuleConfig module)
        {
            _settingMappings.Clear();
            ResetRenderedControlReferences();
            PrepareCategorySurface(showAslmContainer: false, showModuleContainer: true, showEmptyState: false);

            var settings = module.Settings?.Where(SettingsService.ShouldDisplaySetting).ToList() ?? [];
            var loadedSettings = settings
                .Select(setting => new LoadedSetting(setting, _settingsService.GetCurrentSettingValue(module, setting)))
                .ToList();

            var valuesByKey = loadedSettings.ToDictionary(item => item.Setting.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var visibleSettings = loadedSettings
                .Where(item => SettingsService.ShouldRenderSetting(item.Setting, settings, valuesByKey))
                .ToList();

            if (visibleSettings.Count == 0)
            {
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_ModuleNoSettings));
                return;
            }

            EmptyCategoryState.IsVisible = false;

            // ASLM-managed settings keep their existing layout and ignore category/dependency metadata.
            var automaticSettings = visibleSettings
                .Where(item => !SettingsService.IsSettingsMetadataEligible(item.Setting))
                .ToList();
            AddModuleSettingsSection(module, automaticSettings, null, null);

            var declaredCategoryIds = module.SettingCategories
                .Select(category => category.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var category in module.SettingCategories)
            {
                var categorySettings = visibleSettings
                    .Where(item =>
                        SettingsService.IsSettingsMetadataEligible(item.Setting) &&
                        string.Equals(item.Setting.Category, category.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                AddModuleSettingsSection(
                    module,
                    categorySettings,
                    category.Name,
                    category.Description);
            }

            var uncategorizedSettings = visibleSettings
                .Where(item =>
                    SettingsService.IsSettingsMetadataEligible(item.Setting) &&
                    (string.IsNullOrWhiteSpace(item.Setting.Category) ||
                     !declaredCategoryIds.Contains(item.Setting.Category)))
                .ToList();
            AddModuleSettingsSection(
                module,
                uncategorizedSettings,
                module.SettingCategories.Count > 0
                    ? L.Get(LocalizationKeys.Settings_ModuleOtherSettings)
                    : null,
                null);
        }

        /// <summary>
        /// Adds one non-empty module settings group with optional category metadata.
        /// </summary>
        private void AddModuleSettingsSection(
            ModuleConfig module,
            IReadOnlyList<LoadedSetting> items,
            string? title,
            string? description)
        {
            if (items.Count == 0)
            {
                return;
            }

            var section = CreateModuleSectionBorder();
            var content = new VerticalStackLayout { Spacing = 8 };
            if (!string.IsNullOrWhiteSpace(title))
            {
                content.Children.Add(CreateCardTitle(title));
                if (!string.IsNullOrWhiteSpace(description))
                {
                    content.Children.Add(CreateCardDescription(description));
                }
            }

            foreach (var item in items)
            {
                if (!item.Setting.IsAutomaticallyManaged || item.Setting.UseCustomValue)
                {
                    item.Setting.Value = item.Value;
                }

                var (card, mapping) = CreateSettingCard(module, item.Setting, item.Value);
                content.Children.Add(card);
                if (mapping != null)
                {
                    _settingMappings.Add(mapping);
                }
            }

            section.Content = content;
            ModuleSettingsContainer.Children.Add(section);
        }

        /// <summary>
        /// Shows the empty-state card and hides other content containers.
        /// </summary>
        private void ShowEmptyCategory(string message)
        {
            ResetRenderedControlReferences();
            PrepareCategorySurface(showAslmContainer: false, showModuleContainer: false, showEmptyState: true);
            EmptyCategoryLabel.Text = message;
        }

        /// <summary>
        /// Pushes the current ASLM draft values back into the always-created XAML controls.
        /// </summary>
        private void ApplyAslmDraftsToControls()
        {
            UsernameEntry.Text = _userNameDraft;
            ModulePortEntry.Text = _portStartDraft;
        }

        /// <summary>
        /// Pushes API and console draft values into the dynamically created compact toggles when present.
        /// </summary>
        private void ApplyAslmBuiltInDraftsToToggles()
        {
            if (_apiServerToggle != null)
            {
                _apiServerToggle.SetStateWithoutToggleEvent(_apiServerEnabledDraft);
            }

            if (_consoleSidebarToggle != null)
            {
                _consoleSidebarToggle.SetStateWithoutToggleEvent(_consoleDraft.SidebarVisible);
            }

            if (_consoleIndividualToggle != null)
            {
                _consoleIndividualToggle.SetStateWithoutToggleEvent(_consoleDraft.ShowIndividualModuleConsoles);
            }

            if (_consoleCompletedToggle != null)
            {
                _consoleCompletedToggle.SetStateWithoutToggleEvent(_consoleDraft.ShowCompletedProcesses);
            }

            if (_legalAutoAcceptToggle != null)
            {
                _legalAutoAcceptToggle.SetStateWithoutToggleEvent(_legalAutoAcceptDraft);
            }
        }

        /// <summary>
        /// Copies API, console, and legal compact toggles into the shared drafts after user interaction.
        /// </summary>
        private void RefreshAslmApiAndConsoleDraftsFromToggles()
        {
            if (_apiServerToggle != null)
            {
                _apiServerEnabledDraft = _apiServerToggle.IsToggled;
            }

            if (_consoleSidebarToggle != null &&
                _consoleCompletedToggle != null &&
                _consoleIndividualToggle != null)
            {
                _consoleDraft = new ConsoleBaseline(
                    _consoleSidebarToggle.IsToggled,
                    _consoleCompletedToggle.IsToggled,
                    _consoleIndividualToggle.IsToggled);
            }

            if (_legalAutoAcceptToggle != null)
            {
                _legalAutoAcceptDraft = _legalAutoAcceptToggle.IsToggled;
            }
        }


        // Draft synchronization

        /// <summary>
        /// Synchronizes the visible category controls back into the shared in-memory draft state.
        /// </summary>
        private void SyncDraftValuesFromControls()
        {
            if (_activeCategory == null)
            {
                return;
            }

            if (_activeCategory.Kind == SettingsCategoryKind.Module)
            {
                SyncModuleDraftValuesFromControls();
                return;
            }

            if (_activeCategory.Kind == SettingsCategoryKind.Personalization)
            {
                // Personalization drafts are updated in-place by picker/toggle event handlers.
                return;
            }

            SyncAslmDraftValuesFromControls();
            SyncBuiltInDraftValuesFromControls();
        }

        /// <summary>
        /// Copies the visible ASLM input controls into the current draft values.
        /// </summary>
        private void SyncAslmDraftValuesFromControls()
        {
            if (UserProfileSection.IsVisible)
            {
                _userNameDraft = UsernameEntry.Text?.Trim() ?? string.Empty;
            }

            if (PortsSection.IsVisible)
            {
                _portStartDraft = ModulePortEntry.Text?.Trim() ?? string.Empty;
            }
        }

        /// <summary>
        /// Copies visible built-in ASLM controls into the cross-category draft values.
        /// </summary>
        private void SyncBuiltInDraftValuesFromControls()
        {
            // API and console drafts are driven by RefreshAslmApiAndConsoleDraftsFromToggles on user input
            // and by load/default flows so SyncDraftValuesFromControls cannot overwrite them from WinUI timing glitches.
            _updateDraft = GetCurrentUpdateDraft();
        }

        /// <summary>
        /// Captures the current control values into the in-memory module settings draft.
        /// </summary>
        private void SyncModuleDraftValuesFromControls()
        {
            foreach (var mapping in _settingMappings)
            {
                var useCustom = mapping.ReadCustomValue?.Invoke() ?? mapping.Setting.UseCustomValue;
                mapping.Setting.UseCustomValue = useCustom;

                if (mapping.Setting.IsAutomaticallyManaged && !useCustom)
                {
                    continue;
                }

                mapping.Setting.Value = mapping.Setting.NormalizeUserValue(mapping.ReadValue());
            }
        }

        /// <summary>
        /// Rebuilds the visible settings list after one of the controlling toggles changes.
        /// </summary>
        private async Task RefreshDynamicVisibilityAsync()
        {
            if (_isRefreshingVisibility || _activeCategory?.Kind != SettingsCategoryKind.Module || _activeCategory.Module == null)
            {
                return;
            }

            try
            {
                _isRefreshingVisibility = true;
                SyncModuleDraftValuesFromControls();
                RenderModuleCategory(_activeCategory.Module);
                await Task.CompletedTask;
            }
            finally
            {
                _isRefreshingVisibility = false;
            }
        }

        /// <summary>
        /// Prompts the user to discard unsaved changes before leaving the current category.
        /// </summary>
        private async Task<bool> ConfirmDiscardChangesIfNeededAsync()
        {
            SyncDraftValuesFromControls();

            if (_activeCategory == null || !HasAnyUnsavedChanges())
            {
                return true;
            }

            var discardChanges = await ShowAlertAsync(
                L.Get(LocalizationKeys.Settings_DiscardDialog_Title),
                L.Get(LocalizationKeys.Settings_DiscardDialog_Message),
                L.Get(LocalizationKeys.Common_Discard),
                L.Get(LocalizationKeys.Common_Stay));

            if (!discardChanges)
            {
                return false;
            }

            LoadAslmDraftsFromAppData();
            LoadPersonalizationDraftsFromAppData();
            LoadOllamaDraftsFromService();
            await LoadModuleDraftsAsync(reloadModules: false, reloadRuntimeValues: true);
            _categories = _settingsService.CreateOrderedCategories(_loadedModules);

            _themeService.ApplyFromSettings();

            return true;
        }

        /// <summary>
        /// Determines whether the currently visible category has unsaved changes.
        /// </summary>
        private bool HasUnsavedChanges()
        {
            if (_activeCategory == null)
            {
                return false;
            }

            return _activeCategory.Kind switch
            {
                SettingsCategoryKind.Aslm => HasUnsavedAslmSettingsChanges(),
                SettingsCategoryKind.Accounts => HasUnsavedAccountChanges(),
                SettingsCategoryKind.Updates => HasUnsavedUpdateChanges(),
                SettingsCategoryKind.Module => HasUnsavedModuleChanges(),
                SettingsCategoryKind.Personalization => HasUnsavedPersonalizationChanges(),
                _ => false
            };
        }

        /// <summary>
        /// Determines whether any loaded settings category has pending unsaved edits.
        /// </summary>
        private bool HasAnyUnsavedChanges() =>
            HasUnsavedAccountChanges() ||
            HasUnsavedAslmSettingsChanges() ||
            HasUnsavedPersonalizationChanges() ||
            _loadedModules.Any(m => _settingsService.ModuleHasChangesComparedToBaseline(m, _settingBaselines));

        /// <summary>
        /// Determines whether the personalization draft differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedPersonalizationChanges() =>
            !string.Equals(_personalizationDraft.Appearance, _personalizationBaseline.Appearance, StringComparison.Ordinal) ||
            !string.Equals(_personalizationDraft.Language, _personalizationBaseline.Language, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_personalizationDraft.CustomThemeId, _personalizationBaseline.CustomThemeId, StringComparison.Ordinal) ||
            HasUnsavedThemeColorChanges();

        /// <summary>
        /// Determines whether the in-memory theme editor differs from the saved custom theme.
        /// </summary>
        private bool HasUnsavedThemeColorChanges()
        {
            if (_editingThemeDraft == null)
            {
                return false;
            }

            var saved = _customThemesStore.FindById(_editingThemeDraft.Id);
            if (saved == null)
            {
                // Theme exists only in the editor draft, not yet persisted.
                return true;
            }

            if (!string.Equals(_editingThemeDraft.Name, saved.Name, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(_editingThemeDraft.BaseAppearance, saved.BaseAppearance, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var (key, value) in _editingThemeDraft.Colors)
            {
                if (!saved.Colors.TryGetValue(key, out var savedValue) ||
                    !string.Equals(value, savedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return saved.Colors.Count != _editingThemeDraft.Colors.Count;
        }

        /// <summary>
        /// Determines whether the account display name differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedAccountChanges()
        {
            var userName = UserProfileSection.IsVisible
                ? UsernameEntry.Text?.Trim() ?? string.Empty
                : _userNameDraft;
            return SettingsService.HasUnsavedAccountChanges(userName, _aslmBaseline);
        }

        /// <summary>
        /// Determines whether any combined ASLM setting differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedAslmSettingsChanges() =>
            SettingsService.HasUnsavedAslmSettingsChanges(
                GetCurrentPortStartDraft(),
                _apiServerEnabledDraft,
                _consoleDraft,
                GetCurrentUpdateDraft(),
                _legalAutoAcceptDraft,
                _aslmBaseline,
                _consoleBaseline,
                _updateBaseline,
                _legalAutoAcceptBaseline);

        /// <summary>
        /// Reads the module start port draft from visible controls when the ports section is shown.
        /// </summary>
        private string GetCurrentPortStartDraft() =>
            PortsSection.IsVisible
                ? ModulePortEntry.Text?.Trim() ?? string.Empty
                : _portStartDraft;

        /// <summary>
        /// Determines whether the port draft differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedPortChanges()
        {
            return SettingsService.HasUnsavedPortChanges(
                GetCurrentPortStartDraft(),
                _aslmBaseline);
        }

        /// <summary>
        /// Determines whether the ASLM API setting differs from the last saved baseline.
        /// </summary>
        private bool HasUnsavedAslmApiChanges() =>
            SettingsService.HasUnsavedApiServerChanges(_apiServerEnabledDraft, _aslmBaseline);

        /// <summary>
        /// Determines whether console preferences differ from the last saved baseline.
        /// </summary>
        private bool HasUnsavedConsoleChanges() =>
            SettingsService.HasUnsavedConsoleChanges(_consoleDraft, _consoleBaseline);

        /// <summary>
        /// Determines whether the visible module editors differ from the last saved baseline.
        /// </summary>
        private bool HasUnsavedModuleChanges()
        {
            foreach (var mapping in _settingMappings)
            {
                var useCustom = mapping.ReadCustomValue?.Invoke() ?? mapping.Setting.UseCustomValue;
                var currentValue = mapping.Setting.IsAutomaticallyManaged && !useCustom
                    ? _settingsService.GetResolvedSettingValue(mapping.Module, mapping.Setting)
                    : mapping.Setting.NormalizeUserValue(mapping.ReadValue());
                var displayValue = mapping.Setting.FormatValueForDisplay(currentValue);

                if (mapping.InitialUseCustomValue != useCustom ||
                    !string.Equals(mapping.InitialDisplayValue, displayValue, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether update controls differ from the last saved baseline.
        /// </summary>
        private bool HasUnsavedUpdateChanges()
        {
            var updateDraft = GetCurrentUpdateDraft();
            return SettingsService.HasUnsavedUpdateChanges(updateDraft, _updateBaseline);
        }

        /// <summary>
        /// Gets the latest update draft, reading visible controls when present.
        /// </summary>
        private UpdateBaseline GetCurrentUpdateDraft() =>
            _checkUpdatesToggle != null &&
            _autoUpdatesToggle != null &&
            _appUpdateChannelPicker != null &&
            _moduleUpdateModePicker != null &&
            _moduleUpdateChannelPicker != null
                ? new UpdateBaseline(
                    _checkUpdatesToggle.IsToggled,
                    _autoUpdatesToggle.IsToggled,
                    _appUpdateChannelPicker.SelectedItem?.ToString() ?? "release",
                    _moduleUpdateModePicker.SelectedItem?.ToString() ?? "release",
                    _moduleUpdateChannelPicker.SelectedItem?.ToString() ?? "release")
                : _updateDraft;

        /// <summary>
        /// Pushes default update preferences into the visible update controls.
        /// </summary>
        private void ApplyUpdateDefaultsToControls()
        {
            _updateDraft = SettingsService.BuildDefaultUpdateBaseline();

            if (_checkUpdatesToggle != null)
            {
                _checkUpdatesToggle.IsToggled = _updateDraft.CheckEnabled;
            }

            if (_autoUpdatesToggle != null)
            {
                _autoUpdatesToggle.IsToggled = _updateDraft.AutoUpdateEnabled;
            }

            if (_appUpdateChannelPicker != null)
            {
                _appUpdateChannelPicker.SelectedItem = _updateDraft.AppChannel;
            }

            if (_moduleUpdateModePicker != null)
            {
                _moduleUpdateModePicker.SelectedItem = _updateDraft.ModuleDefaultMode;
            }

            if (_moduleUpdateChannelPicker != null)
            {
                _moduleUpdateChannelPicker.SelectedItem = _updateDraft.ModuleDefaultChannel;
            }
        }

        /// <summary>
        /// Shows a confirmation dialog on the current shell page.
        /// </summary>
        private static Task<bool> ShowAlertAsync(string title, string message, string accept, string cancel) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayAlertAsync(title, message, accept, cancel)
                : Task.FromResult(false);

        /// <summary>
        /// Finds one of the keyed XAML styles used by dynamically rendered settings controls.
        /// </summary>
        private static Style? GetStyleResource(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style
                ? style
                : null;

        /// <summary>
        /// Finds one of the shared color resources with a defensive fallback.
        /// </summary>
        private static Color GetColorResource(string key, Color fallback) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
                ? color
                : fallback;

        /// <summary>
        /// Shows a simple informational dialog on the current shell page.
        /// </summary>
        private Task ShowSuccessAsync(string message)
        {
            _notifications.PublishSystemToast(
                L.Get(LocalizationKeys.Settings_SavedTitle),
                message,
                L.Get(LocalizationKeys.Common_Saved),
                "settings-save");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Shows an error dialog on the current shell page.
        /// </summary>
        private static Task ShowErrorAsync(string message) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayAlertAsync(
                    L.Get(LocalizationKeys.Settings_ValidationError),
                    message,
                    L.Get(LocalizationKeys.Common_OK))
                : Task.CompletedTask;


        // Saving

        /// <summary>
        /// Restores the currently visible category to its default values.
        /// </summary>
        private async void OnDefaultClicked(object? sender, EventArgs e)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            SyncDraftValuesFromControls();

            var appliedAslmBuiltInDefaults = false;
            (string PortStart, bool ApiServerEnabled, ConsoleBaseline ConsoleDefaults, bool LegalAutoAcceptUpdates)? aslmDefaultBuiltIns = null;

            switch (_activeCategory.Kind)
            {
                case SettingsCategoryKind.Aslm:
                    aslmDefaultBuiltIns = SettingsService.BuildDefaultAslmDrafts();
                    _portStartDraft = aslmDefaultBuiltIns.Value.PortStart;
                    _apiServerEnabledDraft = aslmDefaultBuiltIns.Value.ApiServerEnabled;
                    _consoleDraft = aslmDefaultBuiltIns.Value.ConsoleDefaults;
                    _legalAutoAcceptDraft = aslmDefaultBuiltIns.Value.LegalAutoAcceptUpdates;
                    appliedAslmBuiltInDefaults = true;
                    PortErrorLabel.IsVisible = false;
                    RenderAslmCategory();
                    break;
                case SettingsCategoryKind.Accounts:
                    if (!_sunriseService.IsCloudAccount)
                    {
                        _userNameDraft = Environment.UserName;
                    }
                    RenderAccountsCategory();
                    break;
                case SettingsCategoryKind.Updates:
                    ApplyUpdateDefaultsToControls();
                    RenderUpdatesCategory();
                    break;
                case SettingsCategoryKind.Module:
                    SettingsService.ResetModuleToDefaults(_activeCategory.Module!);
                    RenderModuleCategory(_activeCategory.Module!);
                    break;
                case SettingsCategoryKind.Personalization:
                    _personalizationDraft = new AppPersonalizationConfig();
                    _editingThemeDraft = null;
                    RenderPersonalizationCategory();
                    break;
            }

            SyncDraftValuesFromControls();

            // Rebuilt compact toggles can briefly report a stale value on some hosts right after attach.
            // Re-assert defaults on drafts and controls so save detection matches the intended default state.
            if (appliedAslmBuiltInDefaults && aslmDefaultBuiltIns is { } builtIns)
            {
                _portStartDraft = builtIns.PortStart;
                _apiServerEnabledDraft = builtIns.ApiServerEnabled;
                _consoleDraft = builtIns.ConsoleDefaults;
                _legalAutoAcceptDraft = builtIns.LegalAutoAcceptUpdates;
                ApplyAslmDraftsToControls();
                ApplyAslmBuiltInDraftsToToggles();
            }

            await SettingsScroll.ScrollToAsync(0, 0, false);
            ReconcileSettingsVisibleControlsWithDrafts();
            Dispatcher?.Dispatch(ReconcileSettingsVisibleControlsWithDrafts);
            if (Dispatcher != null)
            {
                Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(500), ReconcileSettingsVisibleControlsWithDrafts);
            }
        }

        /// <summary>
        /// Reverts all pending settings drafts back to the last persisted values.
        /// </summary>
        private async void OnDiscardChangesClicked(object? sender, EventArgs e)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            await DiscardUnsavedChangesAsync();
            await SettingsScroll.ScrollToAsync(0, 0, false);
        }

        /// <summary>
        /// Reloads saved values and rebuilds the active category after an explicit discard.
        /// </summary>
        private async Task DiscardUnsavedChangesAsync()
        {
            var activeCategoryId = _activeCategory?.Id;

            LoadAslmDraftsFromAppData();
            LoadPersonalizationDraftsFromAppData();
            LoadOllamaDraftsFromService();
            await LoadModuleDraftsAsync(reloadModules: false, reloadRuntimeValues: true);

            _categories = _settingsService.CreateOrderedCategories(_loadedModules);
            BuildCategorySelectors();

            var targetCategory = ResolveCategory(activeCategoryId) ?? _categories.FirstOrDefault();
            if (targetCategory == null)
            {
                _activeCategory = null;
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_NoSettingsAvailable));
                UpdateActionButtons();
                return;
            }

            ActivateCategory(targetCategory);
        }

        /// <summary>
        /// Saves the current settings without restarting anything.
        /// </summary>
        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            await SaveAsync(restartAfterSave: false);
        }

        /// <summary>
        /// Saves the current settings and restarts the active target when supported.
        /// </summary>
        private async void OnSaveAndRestartClicked(object? sender, EventArgs e)
        {
            await SaveAsync(restartAfterSave: true);
        }

        /// <summary>
        /// Validates, persists, and optionally restarts the active category target.
        /// </summary>
        private async Task SaveAsync(bool restartAfterSave)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            try
            {
                _isSaving = true;
                SyncDraftValuesFromControls();
                UpdateSelectorButtonStates();
                UpdateActionButtons();

                if (!SettingsService.TryValidateDisplayName(_userNameDraft, out var validatedUserName, out var displayNameErrorMessage))
                {
                    await ShowErrorAsync(displayNameErrorMessage);
                    return;
                }
                _userNameDraft = validatedUserName;

                var portResult = SettingsService.TryParsePortStart(_portStartDraft);
                if (!portResult.Success)
                {
                    if (_activeCategory.Kind == SettingsCategoryKind.Aslm)
                    {
                        ShowPortError(portResult.ErrorMessage);
                    }
                    else
                    {
                        await ShowErrorAsync(portResult.ErrorMessage);
                    }

                    return;
                }

                _updateDraft = GetCurrentUpdateDraft();
                if (!SettingsService.TryValidateAndBuildUpdateSettings(_updateDraft, out var nextSettings, out var updateErrorMessage))
                {
                    await ShowErrorAsync(updateErrorMessage);
                    return;
                }

                foreach (var module in _loadedModules)
                {
                    if (!_settingsService.TryValidateModuleSettings(module, out var moduleErrorMessage))
                    {
                        await ShowErrorAsync(moduleErrorMessage);
                        return;
                    }
                }

                var hadPersonalizationChanges = HasUnsavedPersonalizationChanges();
                if (hadPersonalizationChanges)
                {
                    await SavePersonalizationAsync(applyImmediately: !restartAfterSave);
                }

                var hadAppRestartChanges = HasUnsavedAslmSettingsChanges();
                var hadAslmChanges = HasUnsavedAccountChanges() || hadAppRestartChanges;
                var modulesWithChanges = GetModulesWithUnsavedChanges();

                SettingsService.ApplyDraftsToAppData(
                    _appData,
                    _userNameDraft,
                    portResult.ModulesStart,
                    _consoleDraft,
                    nextSettings,
                    _legalAutoAcceptDraft);
                await _appData.SaveAsync();

                if (_apiServerEnabledDraft != _aslmBaseline.ApiServerEnabled)
                {
                    await _mirrorServer.SetEnabledAsync(_apiServerEnabledDraft);
                }

                _aslmBaseline = new AslmBaseline(
                    _userNameDraft,
                    _portStartDraft,
                    _mirrorServer.IsEnabled);
                _apiServerEnabledDraft = _mirrorServer.IsEnabled;
                _consoleBaseline = _consoleDraft;
                _legalAutoAcceptBaseline = _legalAutoAcceptDraft;
                _updateBaseline = SettingsService.BuildAslmDraftSnapshot(_appData, _mirrorServer.IsEnabled).UpdateBaseline;
                _updateDraft = _updateBaseline;
                PortErrorLabel.IsVisible = false;

                var touchedModules = new HashSet<ModuleConfig>();
                var deferredSettings = new List<string>();
                foreach (var module in modulesWithChanges)
                {
                    var moduleSaveResult = await _settingsService.SaveActiveModuleAsync(module, _settingBaselines);
                    touchedModules.UnionWith(moduleSaveResult.TouchedModules);
                    deferredSettings.AddRange(moduleSaveResult.DeferredSettings);
                }

                foreach (var module in touchedModules)
                {
                    await ReloadModuleRuntimeValuesAsync(module);
                }

                var activeCategoryId = _activeCategory.Id;
                _categories = _settingsService.CreateOrderedCategories(_loadedModules);
                BuildCategorySelectors();
                var resolvedCategory = ResolveCategory(activeCategoryId);
                if (resolvedCategory != null)
                {
                    ActivateCategory(resolvedCategory);
                }

                var hadAnyPersistedSettingsChanges = hadAslmChanges || hadPersonalizationChanges;
                var successMessage = BuildLocalizedSaveMessage(
                    hadAnyPersistedSettingsChanges,
                    touchedModules.Count > 0,
                    deferredSettings);
                if (restartAfterSave)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await ShowSuccessAsync(successMessage);
                    });
                }
                else
                {
                    await ShowSuccessAsync(successMessage);
                }

                if (restartAfterSave && hadPersonalizationChanges)
                {
                    try
                    {
                        await RestartApplicationThroughLauncherAsync();
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_UpdateStatus_RestartFailed, ex.Message));
                    }

                    return;
                }

                if (restartAfterSave && await RestartChangedTargetsAsync(hadAppRestartChanges, touchedModules))
                {
                    return;
                }
            }
            finally
            {
                _isSaving = false;
                UpdateSelectorButtonStates();
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Restarts the changed app-level target or changed module targets when supported.
        /// </summary>
        private async Task<bool> RestartChangedTargetsAsync(bool restartApp, IEnumerable<ModuleConfig> changedModules)
        {
            if (restartApp)
            {
                await RestartApplicationAsync();
                return true;
            }

            foreach (var module in changedModules.Where(CanRestartModule))
            {
                await _settingsService.RestartModuleAsync(module);
            }

            return false;
        }

        /// <summary>
        /// Stops modules, starts the launcher with process wait, and exits so ASLM relaunches cleanly.
        /// </summary>
        private async Task RestartApplicationThroughLauncherAsync()
        {
            await _settingsService.StopAllModulesAsync();
            await Task.Run(SettingsService.StartLauncherForApplicationRestart);
            Application.Current?.Quit();
        }

        /// <summary>
        /// Restarts the application startup chain so app-level changes take effect.
        /// </summary>
        private async Task RestartApplicationAsync()
        {
            await _settingsService.StopAllModulesAsync();

            if (Application.Current is not App application || application.Windows.Count == 0)
            {
                return;
            }

            var newPage = application.CreateStartupPage();
            var window = application.Windows[0];
            window.Page = newPage;
        }

        /// <summary>
        /// Switches the ASLM profile between local mode and browser-authorized SUNRISE mode.
        /// </summary>
        private async void OnAslmAccountButtonClicked(object? sender, EventArgs e)
        {
            if (_isAslmAccountActionRunning)
            {
                return;
            }

            if (_sunriseService.IsCloudAccount)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalTitle),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalMessage),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalConfirm),
                    L.Get(LocalizationKeys.Common_Cancel));
                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                _isAslmAccountActionRunning = true;
                var actionCts = new CancellationTokenSource();
                _aslmAccountActionCts = actionCts;
                UpdateAslmAccountActionControls();
                UpdateActionButtons();

                if (_sunriseService.IsCloudAccount)
                {
                    await _sunriseService.SelectLocalAccountAsync(actionCts.Token);
                }
                else
                {
                    var result = await _sunriseService.AuthenticateApplicationAsync(actionCts.Token);
                    if (!result.Success)
                    {
                        if (actionCts.IsCancellationRequested)
                        {
                            return;
                        }

                        var error = string.IsNullOrWhiteSpace(result.Error)
                            ? L.Get(LocalizationKeys.SetupWizard_CloudAccountRequired)
                            : result.Error;
                        await ShowErrorAsync(L.Get(
                            LocalizationKeys.Settings_AslmAccount_AuthenticationFailed,
                            error));
                        return;
                    }
                }

                _userNameDraft = _appData.Data.User.Name;
                _aslmBaseline = _aslmBaseline with { UserName = _userNameDraft };
                UsernameEntry.Text = _userNameDraft;
                if (_activeCategory?.Kind == SettingsCategoryKind.Accounts)
                {
                    RenderAccountsCategory();
                    UpdateActionButtons();
                }
            }
            catch (OperationCanceledException)
            {
                // Closing the settings overlay cancels an outstanding browser sign-in.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ASLM account switch failed: {ex}");
                await ShowErrorAsync(L.Get(
                    LocalizationKeys.Settings_AslmAccount_AuthenticationFailed,
                    ex.Message));
            }
            finally
            {
                _aslmAccountActionCts?.Dispose();
                _aslmAccountActionCts = null;
                _isAslmAccountActionRunning = false;
                UpdateAslmAccountActionControls();
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Cancels an in-flight ASLM browser authorization when the settings view closes.
        /// </summary>
        private void StopAslmAccountAction()
        {
            _aslmAccountActionCts?.Cancel();
        }

        /// <summary>
        /// Builds the status line for the active local or cloud ASLM account mode.
        /// </summary>
        private string BuildAslmAccountStatusText()
        {
            if (_isAslmAccountActionRunning)
            {
                return L.Get(LocalizationKeys.Settings_AslmAccount_Connecting);
            }

            if (!_sunriseService.IsCloudAccount)
            {
                return L.Get(LocalizationKeys.Settings_AslmAccount_LocalStatus);
            }

            return L.Get(
                LocalizationKeys.Settings_AslmAccount_CloudStatus,
                GetCloudAccountDisplayName(_sunriseService.UserData.Account));
        }

        /// <summary>
        /// Refreshes the ASLM account card after authorization or a mode switch.
        /// </summary>
        private void UpdateAslmAccountActionControls()
        {
            if (_aslmAccountStatusLabel != null)
            {
                _aslmAccountStatusLabel.Text = BuildAslmAccountStatusText();
            }

            if (_aslmAccountButton != null)
            {
                _aslmAccountButton.Text = _isAslmAccountActionRunning
                    ? L.Get(LocalizationKeys.Settings_AslmAccount_Connecting)
                    : _sunriseService.IsCloudAccount
                        ? L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocal)
                        : L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToCloud);
                _aslmAccountButton.IsEnabled = !_isAslmAccountActionRunning;
            }
        }

        /// <summary>
        /// Resolves the best user-facing name exposed by a SUNRISE account.
        /// </summary>
        private static string GetCloudAccountDisplayName(SunriseUserAccount account)
        {
            if (!string.IsNullOrWhiteSpace(account.Aslm?.Username))
            {
                return account.Aslm.Username;
            }

            if (!string.IsNullOrWhiteSpace(account.Username))
            {
                return account.Username;
            }

            return account.Email;
        }

        /// <summary>
        /// Handles the GitHub account connect or disconnect button click.
        /// </summary>
        private async void OnGitHubAccountButtonClicked(object? sender, EventArgs e)
        {
            await ExecuteGitHubAccountActionAsync(connect: !IsGitHubConnected());
        }

        /// <summary>
        /// Runs one GitHub account action and refreshes the account card state.
        /// </summary>
        private async Task ExecuteGitHubAccountActionAsync(bool connect)
        {
            if (_isGitHubAccountActionRunning)
            {
                return;
            }

            try
            {
                _isGitHubAccountActionRunning = true;
                UpdateGitHubAccountActionControls();

                if (connect)
                {
                    try
                    {
                        await Launcher.OpenAsync(GitHubAccountStore.BuildTokenCreationUrl());
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, ex.Message));
                        return;
                    }

                    var token = await PromptForGitHubTokenAsync();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        return;
                    }

                    var result = await _githubAccountStore.ConnectAsync(token);
                    _githubDraft = result.State;

                    if (!result.Success)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, result.Message));
                        return;
                    }

                    try
                    {
                        await _githubUpdateClient.RefreshRateLimitAsync(GitHubRequestSources.Manual);
                    }
                    catch
                    {
                        // Rate-limit refresh is best-effort after a successful connect.
                    }
                }
                else
                {
                    var result = await _githubAccountStore.DisconnectAsync();
                    _githubDraft = result.State;
                }
            }
            finally
            {
                _isGitHubAccountActionRunning = false;
                UpdateGitHubAccountActionControls();
            }
        }

        /// <summary>
        /// Returns whether the cached GitHub account state is connected.
        /// </summary>
        private bool IsGitHubConnected() => _githubDraft.IsConnected;

        /// <summary>
        /// Builds the GitHub account status line for the settings card.
        /// </summary>
        private string BuildGitHubAccountStatusText()
        {
            if (_isGitHubAccountActionRunning)
            {
                return L.Get(LocalizationKeys.Settings_GitHub_Connecting);
            }

            if (_githubDraft.IsConnected && !string.IsNullOrWhiteSpace(_githubDraft.UserName))
            {
                return L.Get(LocalizationKeys.Settings_GitHub_ConnectedAs, _githubDraft.UserName);
            }

            if (!string.IsNullOrWhiteSpace(_githubDraft.ErrorMessage))
            {
                return L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, _githubDraft.ErrorMessage);
            }

            return L.Get(LocalizationKeys.Settings_GitHub_NotConnectedHint);
        }

        /// <summary>
        /// Refreshes the GitHub account card labels and action button state.
        /// </summary>
        private void UpdateGitHubAccountActionControls()
        {
            if (_githubAccountStatusLabel != null)
            {
                _githubAccountStatusLabel.Text = BuildGitHubAccountStatusText();
            }

            if (_githubAccountButton != null)
            {
                var isConnected = IsGitHubConnected();
                _githubAccountButton.Text = _isGitHubAccountActionRunning
                    ? L.Get(LocalizationKeys.Settings_GitHub_Connecting)
                    : isConnected
                        ? L.Get(LocalizationKeys.Settings_GitHub_Disconnect)
                        : L.Get(LocalizationKeys.Settings_GitHub_Connect);
                _githubAccountButton.IsEnabled = !_isGitHubAccountActionRunning;
                ApplyOllamaAccountButtonState(_githubAccountButton, isConnected);
            }
        }

        /// <summary>
        /// Prompts the user to paste a GitHub personal access token after browser sign-in.
        /// </summary>
        private static Task<string?> PromptForGitHubTokenAsync() =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayPromptAsync(
                    L.Get(LocalizationKeys.Settings_GitHub_ConnectPrompt_Title),
                    L.Get(LocalizationKeys.Settings_GitHub_ConnectPrompt_Message),
                    L.Get(LocalizationKeys.Settings_GitHub_Connect),
                    L.Get(LocalizationKeys.Common_Cancel),
                    L.Get(LocalizationKeys.Settings_GitHub_TokenPlaceholder),
                    maxLength: 256,
                    keyboard: Keyboard.Text)
                : Task.FromResult<string?>(null);

        /// <summary>
        /// Handles the single Ollama account action button click.
        /// </summary>
        private async void OnOllamaAccountButtonClicked(object? sender, EventArgs e)
        {
            await ExecuteOllamaAccountActionAsync(signIn: !IsOllamaSignedIn());
        }

        /// <summary>
        /// Runs one Ollama account action and refreshes the compact account button state.
        /// </summary>
        private async Task ExecuteOllamaAccountActionAsync(bool signIn)
        {
            if (_isOllamaAccountActionRunning)
            {
                return;
            }

            StopOllamaStatusPolling();

            if (!signIn)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Title),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Message),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Confirm),
                    L.Get(LocalizationKeys.Common_Cancel));

                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                _isOllamaAccountActionRunning = true;
                _ollamaAccountAction = signIn ? "signin" : "signout";
                UpdateOllamaAccountActionControls();

                var result = signIn
                    ? await _ollamaSettings.SignInAsync()
                    : await _ollamaSettings.SignOutAsync();

                await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: signIn);
                UpdateOllamaAccountActionControls();

                if (!result.Success)
                {
                    await ShowErrorAsync(result.Message);
                    return;
                }

                if (signIn && result.IsPendingVerification && !IsOllamaSignedIn())
                {
                    StartOllamaStatusPolling();
                }
            }
            finally
            {
                _isOllamaAccountActionRunning = false;
                _ollamaAccountAction = string.Empty;
                UpdateOllamaAccountActionControls();
            }
        }

        /// <summary>
        /// Refreshes the non-editable Ollama metadata without overwriting unsaved field edits.
        /// </summary>
        private async Task RefreshOllamaRuntimeMetadataAsync(bool queryLiveStatus, CancellationToken ct = default)
        {
            try
            {
                var refreshed = queryLiveStatus
                    ? await Task.Run(() => _ollamaSettings.RefreshSettingsAsync(ct), ct)
                    : await Task.Run(() => _ollamaSettings.LoadSettings(), ct);
                ApplyOllamaRuntimeMetadata(refreshed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh Ollama settings: {ex.Message}");
                ApplyOllamaRuntimeMetadata(new OllamaPersistentSettings());
            }
        }

        /// <summary>
        /// Copies the latest Ollama metadata into the visible UI draft.
        /// </summary>
        private void ApplyOllamaRuntimeMetadata(OllamaPersistentSettings refreshed)
        {
            _ollamaDraft.IsCliAvailable = refreshed.IsCliAvailable;
            _ollamaDraft.IsSignedIn = refreshed.IsSignedIn;
            _ollamaDraft.UserName = refreshed.UserName;
        }

        /// <summary>
        /// Updates the current account status labels and action buttons when the Ollama card is visible.
        /// </summary>
        private void UpdateOllamaAccountActionControls()
        {
            if (_ollamaAccountStatusLabel != null)
            {
                _ollamaAccountStatusLabel.Text = BuildOllamaAccountStatusText();
            }

            if (_ollamaAccountButton != null)
            {
                var isSignedIn = IsOllamaSignedIn();
                _ollamaAccountButton.Text =
                    _isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signin", StringComparison.Ordinal)
                        ? L.Get(LocalizationKeys.Settings_Ollama_SigningIn) :
                    _isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signout", StringComparison.Ordinal)
                        ? L.Get(LocalizationKeys.Settings_Ollama_SigningOut) :
                    isSignedIn ? L.Get(LocalizationKeys.Settings_Ollama_SignOut) : L.Get(LocalizationKeys.Settings_Ollama_SignIn);
                _ollamaAccountButton.IsEnabled = _ollamaDraft.IsCliAvailable &&
                    !_isOllamaAccountActionRunning &&
                    !_isOllamaMetadataRefreshRunning;
                ApplyOllamaAccountButtonState(_ollamaAccountButton, isSignedIn);
            }
        }

        /// <summary>
        /// Starts a background refresh for the live Ollama account state when the account page is visible.
        /// </summary>
        private void StartOllamaMetadataRefresh()
        {
            if (_activeCategory?.Kind != SettingsCategoryKind.Accounts)
            {
                return;
            }

            StopOllamaMetadataRefresh();

            if (!_ollamaDraft.IsCliAvailable)
            {
                UpdateOllamaAccountActionControls();
                return;
            }

            var refreshCts = new CancellationTokenSource();
            _ollamaMetadataRefreshCts = refreshCts;
            _isOllamaMetadataRefreshRunning = true;
            UpdateOllamaAccountActionControls();

            _ = RefreshOllamaMetadataAsync(refreshCts);
        }

        /// <summary>
        /// Stops the in-flight live Ollama metadata refresh, if any.
        /// </summary>
        private void StopOllamaMetadataRefresh()
        {
            var refreshCts = _ollamaMetadataRefreshCts;
            _ollamaMetadataRefreshCts = null;
            _isOllamaMetadataRefreshRunning = false;
            refreshCts?.Cancel();
            refreshCts?.Dispose();
            UpdateOllamaAccountActionControls();
        }

        /// <summary>
        /// Refreshes the live Ollama metadata without blocking the initial settings page render.
        /// </summary>
        private async Task RefreshOllamaMetadataAsync(CancellationTokenSource refreshCts)
        {
            try
            {
                await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: true, refreshCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!ReferenceEquals(_ollamaMetadataRefreshCts, refreshCts))
                    {
                        return;
                    }

                    refreshCts.Dispose();
                    _ollamaMetadataRefreshCts = null;
                    _isOllamaMetadataRefreshRunning = false;
                    UpdateOllamaAccountActionControls();
                });
            }
        }

        /// <summary>
        /// Clears the current references to dynamically created Ollama controls before the page is rebuilt.
        /// </summary>
        private void ResetOllamaControlReferences()
        {
            _ollamaAccountButton = null;
            _ollamaAccountStatusLabel = null;
        }

        private void ResetGitHubControlReferences()
        {
            _githubAccountButton = null;
            _githubAccountStatusLabel = null;
        }

        private void ResetAslmAccountControlReferences()
        {
            _aslmAccountButton = null;
            _aslmAccountStatusLabel = null;
        }

        /// <summary>
        /// Clears all dynamic control references before rebuilding one category UI tree.
        /// </summary>
        private void ResetRenderedControlReferences()
        {
            ResetOllamaControlReferences();
            ResetGitHubControlReferences();
            ResetAslmAccountControlReferences();
            ResetUpdateControlReferences();
            ResetAslmApiControlReferences();
        }

        /// <summary>
        /// Applies baseline visibility and cleanup for one category render pass.
        /// </summary>
        private void PrepareCategorySurface(bool showAslmContainer, bool showModuleContainer, bool showEmptyState)
        {
            AslmSettingsContainer.IsVisible = showAslmContainer;
            ModuleSettingsContainer.IsVisible = showModuleContainer;
            EmptyCategoryState.IsVisible = showEmptyState;
            ModuleSettingsContainer.Children.Clear();
        }

        /// <summary>
        /// Clears references to dynamically created update controls before the page is rebuilt.
        /// </summary>
        private void ResetUpdateControlReferences()
        {
            _checkUpdatesToggle = null;
            _autoUpdatesToggle = null;
            _appUpdateChannelPicker = null;
            _moduleUpdateModePicker = null;
            _moduleUpdateChannelPicker = null;
            _updateStatusLabel = null;
            _prepareAppUpdateButton = null;
            _restartAppUpdateButton = null;
            _pendingAppUpdateCandidate = null;
            _ollamaUpdateButton = null;
            _ollamaCheckUpdateButton = null;
            _ollamaUpdateStatusLabel = null;
            _ollamaVersionDescriptionLabel = null;
            _pendingOllamaUpdateCandidate = null;
        }

        /// <summary>
        /// Clears the dynamically created built-in ASLM setting controls before the page is rebuilt.
        /// </summary>
        private void ResetAslmApiControlReferences()
        {
            _apiServerToggle = null;
            _consoleSidebarToggle = null;
            _consoleCompletedToggle = null;
            _consoleIndividualToggle = null;
        }

        /// <summary>
        /// Determines whether the current Ollama account state should be treated as signed in.
        /// </summary>
        private bool IsOllamaSignedIn() => _ollamaDraft.IsSignedIn;

        /// <summary>
        /// Starts a short-lived background poll that waits for the browser sign-in flow to complete.
        /// </summary>
        private void StartOllamaStatusPolling()
        {
            StopOllamaStatusPolling();

            var pollingCts = new CancellationTokenSource();
            _ollamaStatusPollingCts = pollingCts;
            UpdateOllamaAccountActionControls();

            _ = PollOllamaStatusAsync(pollingCts);
        }

        /// <summary>
        /// Cancels the active background sign-in status poll, if any.
        /// </summary>
        private void StopOllamaStatusPolling()
        {
            var pollingCts = _ollamaStatusPollingCts;
            _ollamaStatusPollingCts = null;
            pollingCts?.Cancel();
            pollingCts?.Dispose();
            UpdateOllamaAccountActionControls();
        }

        /// <summary>
        /// Polls the local Ollama API until sign-in completes or the timeout window expires.
        /// </summary>
        private async Task PollOllamaStatusAsync(CancellationTokenSource pollingCts)
        {
            var ct = pollingCts.Token;
            var deadline = DateTime.UtcNow + OllamaSignInPollDuration;

            try
            {
                while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: true, ct);

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        UpdateOllamaAccountActionControls();
                    });

                    if (IsOllamaSignedIn())
                    {
                        return;
                    }

                    await Task.Delay(OllamaSignInPollInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!ReferenceEquals(_ollamaStatusPollingCts, pollingCts))
                    {
                        return;
                    }

                    pollingCts.Dispose();
                    _ollamaStatusPollingCts = null;
                    UpdateOllamaAccountActionControls();
                });
            }
        }

        /// <summary>
        /// Returns the compact status line shown under the Ollama account title.
        /// </summary>
        private string BuildOllamaAccountStatusText()
        {
            if (!_ollamaDraft.IsCliAvailable)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_NotInstalled);
            }

            if (_isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signin", StringComparison.Ordinal))
            {
                return L.Get(LocalizationKeys.Settings_Ollama_WaitingSignIn);
            }

            if (_isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signout", StringComparison.Ordinal))
            {
                return L.Get(LocalizationKeys.Settings_Ollama_SigningOutStatus);
            }

            if (_isOllamaMetadataRefreshRunning)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_CheckingAccount);
            }

            if (_ollamaStatusPollingCts != null && !_ollamaDraft.IsSignedIn)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_WaitingSignIn);
            }

            if (_ollamaDraft.IsSignedIn)
            {
                return string.IsNullOrWhiteSpace(_ollamaDraft.UserName)
                    ? L.Get(LocalizationKeys.Settings_Ollama_SignedIn)
                    : L.Get(LocalizationKeys.Settings_Ollama_SignedInAs, _ollamaDraft.UserName);
            }

            return L.Get(LocalizationKeys.Settings_Ollama_NotSignedIn);
        }

        /// <summary>
        /// Displays the current port validation error.
        /// </summary>
        private void ShowPortError(string message)
        {
            PortErrorLabel.Text = message;
            PortErrorLabel.IsVisible = true;
        }


        // Editor creation

        /// <summary>
        /// Builds one setting card and its control mapping when the setting is editable.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateSettingCard(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var card = CreateSettingCardBorder();
            var description = SettingsService.BuildSettingDescription(setting);

            if (_settingsService.IsAutoDetectedAslmEngine(setting))
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var text = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };
                text.Children.Add(CreateCardTitle(setting.Name));
                if (!string.IsNullOrWhiteSpace(description))
                {
                    text.Children.Add(CreateCardDescription(description));
                }

                row.Children.Add(text);
                Grid.SetColumn(text, 0);

                var badge = CreateStatusBadge(
                    value is bool installed && installed
                        ? L.Get(LocalizationKeys.Settings_Engine_Installed)
                        : L.Get(LocalizationKeys.Settings_Engine_NotInstalled),
                    value is bool ready && ready);
                row.Children.Add(badge);
                Grid.SetColumn(badge, 1);

                card.Content = row;
                return (card, null);
            }

            if (setting.NormalizedType is "bool" or "engine")
            {
                var (toggleView, mapping) = CreateBooleanEditor(module, setting, value);
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

                var text = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };
                text.Children.Add(CreateCardTitle(setting.Name));
                if (!string.IsNullOrWhiteSpace(description))
                {
                    text.Children.Add(CreateCardDescription(description));
                }

                row.Children.Add(text);
                Grid.SetColumn(text, 0);
                row.Children.Add(toggleView);
                Grid.SetColumn(toggleView, 1);

                card.Content = row;
                return (card, mapping);
            }

            var content = new VerticalStackLayout { Spacing = TitleDescriptionSpacing };
            content.Children.Add(CreateCardTitle(setting.Name));
            if (!string.IsNullOrWhiteSpace(description))
            {
                content.Children.Add(CreateCardDescription(description));
            }

            var (editor, mappingResult) = CreateEditor(module, setting, value);
            content.Children.Add(editor);
            card.Content = content;
            return (card, mappingResult);
        }

        /// <summary>
        /// Chooses the appropriate editor for the setting type and metadata.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            if (setting.IsAutomaticallyManaged)
            {
                return CreateManagedEditor(module, setting, value);
            }

            if (SettingsService.IsActiveEngineSelector(setting))
            {
                return CreateActiveEngineEditor(module, setting, value);
            }

            if (setting.AllowedValues is { Count: > 0 })
            {
                return CreatePickerEditor(module, setting, value);
            }

            return setting.NormalizedType switch
            {
                "int" or "integer" or "long" or "float" or "double" or "number" => CreateNumericEditor(module, setting, value),
                "password" => CreatePasswordEditor(module, setting, value),
                _ => CreateTextEditor(module, setting, value)
            };
        }

        /// <summary>
        /// Creates a toggle editor for boolean-style settings and hooks visibility refresh when needed.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateBooleanEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var toggle = CreateCompactToggle(value is bool boolValue
                ? boolValue
                : bool.TryParse(setting.FormatValueForDisplay(value), out var parsedBool) && parsedBool);
            toggle.View.HorizontalOptions = LayoutOptions.End;

            if (SettingsService.HasDependentSettings(module, setting))
            {
                toggle.Toggled += (_, _) =>
                {
                    setting.Value = toggle.IsToggled;
                    _ = RefreshDynamicVisibilityAsync();
                };
            }

            toggle.Toggled += (_, _) => QueueActionButtonUpdate();

            return (toggle.View, new SettingControlMapping(
                module,
                setting,
                () => toggle.IsToggled,
                null,
                baseline.DisplayValue,
                baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates the dropdown selector used by the active LLM engine setting.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateActiveEngineEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var allowedValues = setting.AllowedValues ?? [];
            var picker = CreatePicker(null, 13);
            var pickerContainer = CreatePickerContainer(picker);

            var selectedValue = setting.FormatValueForDisplay(value);
            if (string.IsNullOrWhiteSpace(selectedValue))
            {
                selectedValue = allowedValues.FirstOrDefault() ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(selectedValue) &&
                allowedValues.All(option => !string.Equals(option, selectedValue, StringComparison.OrdinalIgnoreCase)))
            {
                allowedValues = [selectedValue, .. allowedValues];
            }

            picker.ItemsSource = allowedValues;

            var selectedIndex = allowedValues.FindIndex(option =>
                string.Equals(option, selectedValue, StringComparison.OrdinalIgnoreCase));
            picker.SelectedIndex = selectedIndex >= 0 ? selectedIndex : (allowedValues.Count > 0 ? 0 : -1);
            picker.SelectedIndexChanged += (_, _) => QueueActionButtonUpdate();

            return (pickerContainer, new SettingControlMapping(
                module,
                setting,
                () => picker.SelectedIndex >= 0 && picker.SelectedIndex < allowedValues.Count
                    ? allowedValues[picker.SelectedIndex]
                    : null,
                null,
                baseline.DisplayValue,
                baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates a picker editor for settings that declare a list of allowed values.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreatePickerEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var picker = CreatePicker(null, 14);
            var pickerContainer = CreatePickerContainer(picker);
            var allowedValues = setting.AllowedValues!.ToList();
            picker.ItemsSource = allowedValues;

            var currentValue = setting.FormatValueForDisplay(value);
            if (string.IsNullOrWhiteSpace(currentValue))
            {
                currentValue = setting.FormatValueForDisplay(setting.Default);
            }

            var selectedIndex = allowedValues.FindIndex(option =>
                string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex < 0 && !string.IsNullOrWhiteSpace(currentValue))
            {
                allowedValues.Insert(0, currentValue);
                selectedIndex = 0;
            }

            picker.SelectedIndex = selectedIndex;
            picker.SelectedIndexChanged += (_, _) => QueueActionButtonUpdate();

            return (pickerContainer, new SettingControlMapping(
                module,
                setting,
                () => picker.SelectedIndex >= 0 && picker.SelectedIndex < allowedValues.Count
                    ? allowedValues[picker.SelectedIndex]
                    : null,
                null,
                baseline.DisplayValue,
                baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates a numeric text entry for number-like settings.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateNumericEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var (field, entry) = CreateTextField(setting.FormatValueForDisplay(value));
            entry.Keyboard = Keyboard.Numeric;
            entry.TextChanged += (_, _) => QueueActionButtonUpdate();

            return (field, new SettingControlMapping(module, setting, () => entry.Text, null, baseline.DisplayValue, baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates a plain text entry for free-form string settings.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateTextEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var (field, entry) = CreateTextField(setting.FormatValueForDisplay(value));
            entry.TextChanged += (_, _) => QueueActionButtonUpdate();
            return (field, new SettingControlMapping(module, setting, () => entry.Text, null, baseline.DisplayValue, baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates a password entry and its mapping while preserving the shared baseline snapshot.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreatePasswordEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var baseline = _settingsService.GetSettingBaseline(module, setting, value, _settingBaselines);
            var (field, entry) = CreatePasswordField(setting.FormatValueForDisplay(value));
            entry.TextChanged += (_, _) => QueueActionButtonUpdate();
            return (field, new SettingControlMapping(module, setting, () => entry.Text, null, baseline.DisplayValue, baseline.UseCustomValue));
        }

        /// <summary>
        /// Creates the editor used for ASLM-managed settings that can optionally switch to custom values.
        /// </summary>
        private (View Control, SettingControlMapping? Mapping) CreateManagedEditor(ModuleConfig module, ModuleSetting setting, object? value)
        {
            var autoValue = _settingsService.GetResolvedSettingValue(module, setting);
            var initialDisplayValue = setting.UseCustomValue
                ? setting.FormatValueForDisplay(value)
                : setting.FormatValueForDisplay(autoValue);

            var isPasswordSetting = string.Equals(setting.NormalizedType, "password", StringComparison.OrdinalIgnoreCase);
            var entryView = isPasswordSetting
                ? CreatePasswordField(initialDisplayValue)
                : CreateTextField(initialDisplayValue);
            var baseline = _settingsService.GetSettingBaseline(module, setting, initialDisplayValue, _settingBaselines);
            var entry = entryView.Entry;
            var lastCustomValue = setting.FormatValueForDisplay(setting.Value ?? value);

            var customToggle = CreateInlineToggle();
            customToggle.IsToggled = setting.UseCustomValue;

            entry.TextChanged += (_, args) =>
            {
                if (customToggle.IsToggled)
                {
                    lastCustomValue = args.NewTextValue ?? string.Empty;
                }

                QueueActionButtonUpdate();
            };

            customToggle.Toggled += (_, args) =>
            {
                entry.Text = args.Value
                    ? (string.IsNullOrWhiteSpace(lastCustomValue) ? setting.FormatValueForDisplay(setting.Value ?? value) : lastCustomValue)
                    : setting.FormatValueForDisplay(_settingsService.GetResolvedSettingValue(module, setting));

                ApplyTextEntryState(entry, !args.Value);
                QueueActionButtonUpdate();
            };

            ApplyTextEntryState(entry, !setting.UseCustomValue);

            var container = new VerticalStackLayout { Spacing = 10 };
            container.Children.Add(CreateInlineToggleRow(L.Get(LocalizationKeys.Settings_UseCustomValue), customToggle));
            container.Children.Add(entryView.Control);

            return (container, new SettingControlMapping(
                module,
                setting,
                () => entry.Text,
                () => customToggle.IsToggled,
                baseline.DisplayValue,
                baseline.UseCustomValue));
        }


        // Personalization rendering

        /// <summary>
        /// Renders the personalization category: appearance picker, custom theme list, and color editor.
        /// </summary>
        private void RenderPersonalizationCategory()
        {
            _settingMappings.Clear();
            ResetRenderedControlReferences();
            ResetPersonalizationControlReferences();
            PrepareCategorySurface(showAslmContainer: false, showModuleContainer: true, showEmptyState: false);

            var section = CreateModuleSectionBorder();
            var content = new VerticalStackLayout { Spacing = 8 };

            _languagePicker = CreatePicker(string.Empty, 13);
            foreach (var language in AppLocalizationService.SupportedLanguages)
            {
                _languagePicker.Items.Add(GetLanguageDisplayName(language.Id));
            }

            var selectedLanguage = AppLocalizationService.SupportedLanguages
                .FirstOrDefault(language =>
                    string.Equals(language.Id, _personalizationDraft.Language, StringComparison.OrdinalIgnoreCase))
                ?? AppLocalizationService.SupportedLanguages[0];
            _personalizationDraft.Language = selectedLanguage.Id;
            _languagePicker.SelectedItem = GetLanguageDisplayName(selectedLanguage.Id);
            _languagePicker.SelectedIndexChanged += OnLanguagePickerChanged;
            ApplyCompactPickerStyle(_languagePicker);

            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_Personalization_Language),
                L.Get(LocalizationKeys.Settings_Personalization_LanguageDescription),
                CreatePickerContainer(_languagePicker, PersonalizationPickerMaxWidth)));

            _appearancePicker = CreatePicker(string.Empty, 13);
            foreach (var appearance in AppearanceOptions)
            {
                _appearancePicker.Items.Add(GetAppearanceDisplayName(appearance));
            }

            _appearancePicker.SelectedItem = GetAppearanceDisplayName(_personalizationDraft.Appearance);
            _appearancePicker.SelectedIndexChanged += OnAppearancePickerChanged;
            ApplyCompactPickerStyle(_appearancePicker);

            content.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_Personalization_Mode),
                L.Get(LocalizationKeys.Settings_Personalization_ModeDescription),
                CreatePickerContainer(_appearancePicker, PersonalizationPickerMaxWidth)));

            _customThemeSection = new VerticalStackLayout { Spacing = 8 };
            _customThemeSection.IsVisible = string.Equals(_personalizationDraft.Appearance, "Custom", StringComparison.Ordinal);
            content.Children.Add(_customThemeSection);
            RebuildCustomThemeSection();

            _themeEditorSection = new VerticalStackLayout { Spacing = 8 };
            _themeEditorSection.IsVisible = false;
            content.Children.Add(_themeEditorSection);
            if (string.Equals(_personalizationDraft.Appearance, "Custom", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(_personalizationDraft.CustomThemeId))
            {
                var selectedTheme = _customThemesStore.FindById(_personalizationDraft.CustomThemeId);
                if (selectedTheme != null)
                {
                    ApplyCustomThemeSelection(selectedTheme.Id);
                }
            }

            section.Content = content;
            ModuleSettingsContainer.Children.Add(section);

            _themeService.ApplyPersonalization(_personalizationDraft, _editingThemeDraft);
            RefreshCategorySelectorChromeFromResources();
        }

        /// <summary>
        /// Clears the personalization-specific control references before a rebuild.
        /// </summary>
        private void ResetPersonalizationControlReferences()
        {
            _appearancePicker = null;
            _languagePicker = null;
            _customThemeSection = null;
            _customThemePicker = null;
            _themeEditorSection = null;
        }

        /// <summary>
        /// Rebuilds the custom theme picker when the Custom appearance mode is active.
        /// </summary>
        private void RebuildCustomThemeSection()
        {
            if (_customThemeSection == null)
            {
                return;
            }

            _customThemeSection.Children.Clear();

            var themes = _customThemesStore.Root.Themes;

            var manageActions = new HorizontalStackLayout
            {
                Spacing = 8,
                HorizontalOptions = LayoutOptions.End
            };

            var createButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_Personalization_New), OnCreateThemeClicked);
            createButton.FontSize = 13;
            createButton.HeightRequest = 32;
            createButton.MinimumHeightRequest = 32;
            createButton.Padding = new Thickness(12, 0);

            var importButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_Personalization_Import), OnImportThemeClicked);
            importButton.FontSize = 13;
            importButton.HeightRequest = 32;
            importButton.MinimumHeightRequest = 32;
            importButton.Padding = new Thickness(12, 0);

            var exportButton = CreateInlineActionButton(L.Get(LocalizationKeys.Settings_Personalization_Export), OnExportThemeClicked);
            exportButton.FontSize = 13;
            exportButton.HeightRequest = 32;
            exportButton.MinimumHeightRequest = 32;
            exportButton.Padding = new Thickness(12, 0);
            exportButton.IsEnabled = themes.Count > 0;

            manageActions.Children.Add(createButton);
            manageActions.Children.Add(importButton);
            manageActions.Children.Add(exportButton);

            _customThemeSection.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_Personalization_Themes),
                L.Get(LocalizationKeys.Settings_Personalization_ThemesDescription),
                manageActions));

            _customThemePicker = new Picker
            {
                HorizontalOptions = LayoutOptions.Fill,
                Title = string.Empty,
                ItemDisplayBinding = new Binding(nameof(CustomTheme.Name)),
                IsEnabled = themes.Count > 0
            };
            _customThemePicker.Style = GetStyleResource(PickerStyleKey);
            _customThemePicker.FontSize = 13;
            ApplyCompactPickerStyle(_customThemePicker);
            _customThemePicker.ItemsSource = themes;
            _customThemePicker.SelectedIndexChanged += OnCustomThemePickerSelectionChanged;

            var deleteButton = new Button
            {
                Text = L.Get(LocalizationKeys.Settings_Personalization_Delete),
                Style = GetStyleResource(FooterDangerButtonStyleKey),
                FontSize = 13,
                HeightRequest = 32,
                MinimumHeightRequest = 32,
                Padding = new Thickness(12, 0),
                CornerRadius = 6,
                IsEnabled = themes.Count > 0
            };
            deleteButton.SetDynamicResource(Button.TextColorProperty, "White");
            deleteButton.Clicked += OnDeleteCurrentCustomThemeClicked;

            var pickerShell = CreatePickerContainer(_customThemePicker, PersonalizationPickerMaxWidth);
            var selectThemeControls = new Grid
            {
                ColumnSpacing = 8,
                HorizontalOptions = LayoutOptions.Fill,
                MinimumWidthRequest = 0,
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                }
            };
            pickerShell.HorizontalOptions = LayoutOptions.Fill;
            selectThemeControls.Children.Add(pickerShell);
            Grid.SetColumn(pickerShell, 0);
            selectThemeControls.Children.Add(deleteButton);
            Grid.SetColumn(deleteButton, 1);

            var selectDescription = themes.Count == 0
                ? L.Get(LocalizationKeys.Settings_Personalization_SelectThemeEmpty)
                : L.Get(LocalizationKeys.Settings_Personalization_SelectThemeActive);

            _customThemeSection.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_Personalization_ActiveTheme),
                selectDescription,
                selectThemeControls));

            _suppressCustomThemePickerEvents = true;
            if (themes.Count > 0)
            {
                // Match the picker to the persisted draft only. Do not assign a default theme id here:
                // that would mutate the draft (e.g. Light/Dark mode with null CustomThemeId but themes on disk)
                // and falsely trip HasUnsavedPersonalizationChanges on first open.
                var selected = themes.FirstOrDefault(t =>
                    string.Equals(t.Id, _personalizationDraft.CustomThemeId, StringComparison.OrdinalIgnoreCase));
                _customThemePicker.SelectedItem = selected;
            }
            else
            {
                _customThemePicker.SelectedItem = null;
            }

            _suppressCustomThemePickerEvents = false;
        }

        /// <summary>
        /// Loads the selected custom theme into the editor when the theme picker changes.
        /// </summary>
        private void OnCustomThemePickerSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressCustomThemePickerEvents || _customThemePicker == null)
            {
                return;
            }

            if (_customThemePicker.SelectedItem is not CustomTheme t)
            {
                return;
            }

            ApplyCustomThemeSelection(t.Id);
        }

        /// <summary>
        /// Loads the selected custom theme into the editor and updates the preview.
        /// </summary>
        private void ApplyCustomThemeSelection(string? themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId) || _themeEditorSection == null)
            {
                return;
            }

            _personalizationDraft.CustomThemeId = themeId;
            var theme = _customThemesStore.FindById(themeId);
            if (theme == null)
            {
                return;
            }

            _editingThemeDraft = CloneCustomTheme(theme);

            BuildThemeColorEditor(_themeEditorSection);
            _themeEditorSection.IsVisible = true;
            _themeService.PreviewCustomTheme(_editingThemeDraft);
            RefreshCategorySelectorChromeFromResources();
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Deletes the theme currently selected in the custom theme picker.
        /// </summary>
        private async void OnDeleteCurrentCustomThemeClicked(object? sender, EventArgs e)
        {
            if (_customThemePicker?.SelectedItem is not CustomTheme t)
            {
                return;
            }

            await OnDeleteThemeClickedAsync(t.Id);
        }

        /// <summary>
        /// Builds the full color-key editor rows inside the provided container.
        /// </summary>
        private void BuildThemeColorEditor(VerticalStackLayout container)
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            container.Children.Clear();

            container.Children.Add(CreateSubGroupHeader(
                L.Get(LocalizationKeys.Settings_ThemeEditor_ColorsFormat, _editingThemeDraft.Name)));

            var basePicker = CreatePicker(string.Empty, 13);
            var baseDarkLabel = GetAppearanceDisplayName("Dark");
            var baseLightLabel = GetAppearanceDisplayName("Light");
            basePicker.Items.Add(baseDarkLabel);
            basePicker.Items.Add(baseLightLabel);
            basePicker.SelectedItem = string.Equals(_editingThemeDraft.BaseAppearance, "light", StringComparison.OrdinalIgnoreCase)
                ? baseLightLabel
                : baseDarkLabel;
            basePicker.SelectedIndexChanged += (_, _) =>
            {
                if (_editingThemeDraft == null)
                {
                    return;
                }

                _editingThemeDraft.BaseAppearance = string.Equals(
                    basePicker.SelectedItem as string,
                    baseLightLabel,
                    StringComparison.Ordinal)
                    ? "light"
                    : "dark";
                _themeService.PreviewCustomTheme(_editingThemeDraft);
                RefreshCategorySelectorChromeFromResources();
                QueueActionButtonUpdate();
            };
            ApplyCompactPickerStyle(basePicker);
            container.Children.Add(CreateUpdateCard(
                L.Get(LocalizationKeys.Settings_ThemeEditor_Base),
                L.Get(LocalizationKeys.Settings_ThemeEditor_BaseDescription),
                CreatePickerContainer(basePicker, PersonalizationPickerMaxWidth)));

            var keys = ThemePaletteResolver.AllKeys.ToList();
            var mid = (keys.Count + 1) / 2;
            var columns = new Grid { ColumnSpacing = 0 };
            columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            columns.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Absolute)));
            columns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

            var colLeft = new VerticalStackLayout
            {
                Spacing = 4,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var colRight = new VerticalStackLayout
            {
                Spacing = 4,
                HorizontalOptions = LayoutOptions.Fill,
                Margin = new Thickness(10, 0, 0, 0)
            };
            for (var i = 0; i < keys.Count; i++)
            {
                var row = CreateCompactColorEditorRow(keys[i]);
                if (i < mid)
                {
                    colLeft.Children.Add(row);
                }
                else
                {
                    colRight.Children.Add(row);
                }
            }

            var columnDivider = new BoxView
            {
                WidthRequest = 1,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                Opacity = 0.55,
                Margin = new Thickness(0, 2, 0, 2)
            };
            columnDivider.SetDynamicResource(BoxView.ColorProperty, "Separator");

            columns.Children.Add(colLeft);
            Grid.SetColumn(colLeft, 0);
            columns.Children.Add(columnDivider);
            Grid.SetColumn(columnDivider, 1);
            columns.Children.Add(colRight);
            Grid.SetColumn(colRight, 2);

            var colorsCard = CreateSettingCardBorder();
            colorsCard.Content = columns;
            container.Children.Add(colorsCard);
        }

        /// <summary>
        /// Builds one compact palette-key row with swatch, hex label, and picker action.
        /// </summary>
        private View CreateCompactColorEditorRow(string key)
        {
            _editingThemeDraft!.Colors.TryGetValue(key, out var existingValue);

            var swatchFill = new BoxView
            {
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                CornerRadius = 4
            };
            var swatchFrame = new Border
            {
                WidthRequest = 24,
                HeightRequest = 24,
                BackgroundColor = Colors.Transparent,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(1),
                Content = swatchFill,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center
            };
            UpdateColorSwatch(swatchFrame, existingValue);

            var hexLabel = new Label
            {
                FontSize = 12,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Start,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
                Text = string.IsNullOrWhiteSpace(existingValue) ? "—" : existingValue,
                MinimumWidthRequest = 0
            };
            hexLabel.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");

            var pickButton = new Button
            {
                Text = L.Get(LocalizationKeys.Settings_ThemeEditor_Pick),
                FontSize = 12,
                HeightRequest = 24,
                MinimumHeightRequest = 24,
                Padding = new Thickness(4, 0),
                CornerRadius = 5,
                MinimumWidthRequest = 56,
                HorizontalOptions = LayoutOptions.End
            };
            pickButton.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundTertiary");
            pickButton.SetDynamicResource(Button.TextColorProperty, "LabelPrimary");

            var clearButton = new Button
            {
                Text = L.Get(LocalizationKeys.Settings_ThemeEditor_Clear),
                FontSize = 12,
                HeightRequest = 24,
                MinimumHeightRequest = 24,
                Padding = new Thickness(4, 0),
                CornerRadius = 5,
                MinimumWidthRequest = 56,
                HorizontalOptions = LayoutOptions.End
            };
            clearButton.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundTertiary");
            clearButton.SetDynamicResource(Button.TextColorProperty, "LabelSecondary");

            var capturedKey = key;
            pickButton.Clicked += async (_, _) =>
            {
                await OpenThemeColorPickerForKeyAsync(capturedKey, swatchFrame, hexLabel);
            };

            var swatchTap = new TapGestureRecognizer();
            swatchTap.Tapped += async (_, _) =>
            {
                await OpenThemeColorPickerForKeyAsync(capturedKey, swatchFrame, hexLabel);
            };
            swatchFrame.GestureRecognizers.Add(swatchTap);

            clearButton.Clicked += (_, _) =>
            {
                if (_editingThemeDraft == null)
                {
                    return;
                }

                _editingThemeDraft.Colors.Remove(capturedKey);
                hexLabel.Text = "—";
                UpdateColorSwatch(swatchFrame, null);
                _themeService.PreviewCustomTheme(_editingThemeDraft);
                RefreshCategorySelectorChromeFromResources();
                QueueActionButtonUpdate();
            };

            var row = new Grid
            {
                ColumnSpacing = 8,
                HeightRequest = 28,
                MinimumHeightRequest = 28,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                MinimumWidthRequest = 0
            };
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(28, GridUnitType.Absolute)));
            row.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var keyLabel = new Label
            {
                Text = key,
                FontSize = 13,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Start,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
                MinimumWidthRequest = 0
            };
            keyLabel.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");

            row.Children.Add(keyLabel);
            Grid.SetColumn(keyLabel, 0);
            row.Children.Add(swatchFrame);
            Grid.SetColumn(swatchFrame, 1);
            row.Children.Add(hexLabel);
            Grid.SetColumn(hexLabel, 2);
            row.Children.Add(pickButton);
            Grid.SetColumn(pickButton, 3);
            row.Children.Add(clearButton);
            Grid.SetColumn(clearButton, 4);

            return row;
        }

        /// <summary>
        /// Updates a themed color swatch from a hex string (inherit uses neutral fill + separator stroke).
        /// </summary>
        private static void UpdateColorSwatch(Border swatchFrame, string? hex)
        {
            if (swatchFrame.Content is not BoxView fill)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(hex) && ThemePaletteResolver.TryParseHex(hex, out var color))
            {
                fill.BackgroundColor = color;
                swatchFrame.Stroke = ThemePaletteResolver.SwatchContrastStroke(color);
                swatchFrame.Opacity = 1;
            }
            else
            {
                fill.BackgroundColor = GetColorResource("BackgroundTertiary", Color.FromArgb("#FF2C2C2E"));
                swatchFrame.Stroke = GetColorResource("Separator", Color.FromArgb("#FF48484A"));
                swatchFrame.Opacity = 1;
            }
        }

        /// <summary>
        /// Opens the platform color picker for one theme palette key and updates the draft.
        /// </summary>
        private async Task OpenThemeColorPickerForKeyAsync(string key, Border swatchFrame, Label hexLabel)
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            _editingThemeDraft.Colors.TryGetValue(key, out var existingHex);
            var initial = ResolveThemeEditorColor(key, existingHex);
            Color? picked = await ThemeColorPickerView.PickAsync(initial);
            if (picked is not { } chosen)
            {
                return;
            }

            var hex = ThemePaletteResolver.ToHex(chosen);
            _editingThemeDraft.Colors[key] = hex;
            hexLabel.Text = hex;
            UpdateColorSwatch(swatchFrame, hex);
            _themeService.PreviewCustomTheme(_editingThemeDraft);
            RefreshCategorySelectorChromeFromResources();
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Resolves the initial color shown in the theme editor for a palette key.
        /// </summary>
        private static Color ResolveThemeEditorColor(string key, string? existingHex)
        {
            if (!string.IsNullOrWhiteSpace(existingHex) && ThemePaletteResolver.TryParseHex(existingHex, out var parsed))
            {
                return parsed;
            }

            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            {
                return c;
            }

            return Colors.Gray;
        }

        /// <summary>
        /// Updates appearance draft state and live theme preview when the appearance picker changes.
        /// </summary>
        private void OnAppearancePickerChanged(object? sender, EventArgs e)
        {
            if (_appearancePicker == null)
            {
                return;
            }

            var selectedDisplay = _appearancePicker.SelectedItem as string ?? GetAppearanceDisplayName("Dark");
            var selected = ResolveAppearanceFromDisplayName(selectedDisplay);
            _personalizationDraft.Appearance = AppPersonalizationConfig.NormalizeAppearance(selected);

            var isCustom = string.Equals(selected, "Custom", StringComparison.OrdinalIgnoreCase);
            if (_customThemeSection != null)
            {
                _customThemeSection.IsVisible = isCustom;
                if (isCustom)
                {
                    RebuildCustomThemeSection();
                    if (_customThemesStore.Root.Themes.Count > 0)
                    {
                        ApplyCustomThemeSelection(_personalizationDraft.CustomThemeId);
                    }
                    else if (_themeEditorSection != null)
                    {
                        _themeEditorSection.IsVisible = false;
                        _editingThemeDraft = null;
                    }
                }
            }

            if (_themeEditorSection != null && !isCustom)
            {
                _themeEditorSection.IsVisible = false;
            }

            QueueActionButtonUpdate();

            if (!string.Equals(selected, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                _editingThemeDraft = null;
            }

            _themeService.ApplyPersonalization(_personalizationDraft, _editingThemeDraft);
            RefreshCategorySelectorChromeFromResources();
        }

        /// <summary>
        /// Updates the language draft when the language picker selection changes.
        /// </summary>
        private void OnLanguagePickerChanged(object? sender, EventArgs e)
        {
            if (_languagePicker == null)
            {
                return;
            }

            var selectedDisplayName = _languagePicker.SelectedItem as string;
            var language = AppLocalizationService.SupportedLanguages
                .FirstOrDefault(option =>
                    string.Equals(GetLanguageDisplayName(option.Id), selectedDisplayName, StringComparison.Ordinal))
                ?? AppLocalizationService.SupportedLanguages[0];
            _personalizationDraft.Language = language.Id;
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Prompts for a new custom theme name, creates it, and selects it in the editor.
        /// </summary>
        private async void OnCreateThemeClicked(object? sender, EventArgs e)
        {
            var name = await PromptAsync(
                L.Get(LocalizationKeys.Settings_ThemeNew_PromptTitle),
                L.Get(LocalizationKeys.Settings_ThemeNew_PromptMessage),
                L.Get(LocalizationKeys.Settings_ThemeNew_DefaultName));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null)
            {
                return;
            }

            var inheritDark = L.Get(LocalizationKeys.Settings_Personalization_InheritDark);
            var inheritLight = L.Get(LocalizationKeys.Settings_Personalization_InheritLight);
            var inheritChoice = await host.DisplayActionSheetAsync(
                L.Get(LocalizationKeys.Settings_Personalization_InheritTitle),
                L.Get(LocalizationKeys.Common_Cancel),
                null,
                inheritDark,
                inheritLight);

            if (string.IsNullOrEmpty(inheritChoice) ||
                string.Equals(inheritChoice, L.Get(LocalizationKeys.Common_Cancel), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var baseAppearance = string.Equals(inheritChoice, inheritLight, StringComparison.OrdinalIgnoreCase)
                ? "light"
                : "dark";

            var newTheme = _customThemesStore.CreateTheme(name.Trim(), baseAppearance);
            ThemePaletteResolver.PrefillCustomThemeFromBuiltIn(newTheme);
            await _customThemesStore.SaveAsync();

            _personalizationDraft.CustomThemeId = newTheme.Id;
            RebuildCustomThemeSection();
            ApplyCustomThemeSelection(newTheme.Id);
        }

        /// <summary>
        /// Imports a custom theme from a user-selected JSON file.
        /// </summary>
        private async void OnImportThemeClicked(object? sender, EventArgs e)
        {
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null)
            {
                return;
            }

            try
            {
                var pick = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = L.Get(LocalizationKeys.Settings_Personalization_ImportPickerTitle),
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.WinUI] = new[] { ".json" },
                        [DevicePlatform.MacCatalyst] = new[] { "public.json" },
                        [DevicePlatform.macOS] = new[] { "json" },
                        [DevicePlatform.iOS] = new[] { "public.json" },
                        [DevicePlatform.Android] = new[] { "application/json" }
                    })
                });

                if (pick == null)
                {
                    return;
                }

                await using var stream = await pick.OpenReadAsync();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                if (json.Length > 524_288)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_TooLarge));
                    return;
                }

                var imported = _customThemesStore.DeserializeImportedTheme(json);
                if (imported == null)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_Invalid));
                    return;
                }

                var added = _customThemesStore.ImportThemeCopy(imported);
                await _customThemesStore.SaveAsync();
                _personalizationDraft.CustomThemeId = added.Id;
                RebuildCustomThemeSection();
                ApplyCustomThemeSelection(added.Id);
                QueueActionButtonUpdate();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_Failed, ex.Message));
            }
        }

        /// <summary>
        /// Exports the selected custom theme to a JSON file on disk.
        /// </summary>
        private async void OnExportThemeClicked(object? sender, EventArgs e)
        {
            if (_customThemePicker?.SelectedItem is not CustomTheme t)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeExport_Select));
                return;
            }

            try
            {
                var json = _customThemesStore.SerializeThemeForExport(t);
#if WINDOWS
                var path = await PickExportThemeFilePathAsync(SanitizeThemeFileName(t.Name));
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                await File.WriteAllTextAsync(path, json);
#elif MACCATALYST
                await MacThemeFileExporter.ExportAsync(SanitizeThemeFileName(t.Name), json);
#endif
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeExport_Failed, ex.Message));
            }
        }

        /// <summary>
        /// Normalizes a theme name into a safe file name for export.
        /// </summary>
        private static string SanitizeThemeFileName(string name)
        {
            var baseName = string.IsNullOrWhiteSpace(name) ? "ASLM_theme" : name.Trim();
            var invalid = global::System.IO.Path.GetInvalidFileNameChars();
            var buffer = new char[baseName.Length];
            for (var i = 0; i < baseName.Length; i++)
            {
                var c = baseName[i];
                buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
            }

            var s = new string(buffer).Trim();
            return string.IsNullOrEmpty(s) ? "ASLM_theme" : s;
        }

        /// <summary>
        /// Confirms deletion, removes a custom theme, and refreshes personalization UI state.
        /// </summary>
        private async Task OnDeleteThemeClickedAsync(string themeId)
        {
            var confirmed = await ShowAlertAsync(
                L.Get(LocalizationKeys.Settings_ThemeDelete_Title),
                L.Get(LocalizationKeys.Settings_ThemeDelete_Message),
                L.Get(LocalizationKeys.Settings_ThemeDelete_Accept),
                L.Get(LocalizationKeys.Common_Cancel));

            if (!confirmed)
            {
                return;
            }

            _customThemesStore.DeleteTheme(themeId);
            await _customThemesStore.SaveAsync();

            if (string.Equals(_personalizationDraft.CustomThemeId, themeId, StringComparison.Ordinal))
            {
                _personalizationDraft.CustomThemeId = _customThemesStore.Root.Themes.FirstOrDefault()?.Id;
                _editingThemeDraft = null;

                if (_themeEditorSection != null)
                {
                    _themeEditorSection.IsVisible = false;
                }
            }

            RebuildCustomThemeSection();

            if (!string.IsNullOrWhiteSpace(_personalizationDraft.CustomThemeId) &&
                _customThemesStore.FindById(_personalizationDraft.CustomThemeId) != null)
            {
                ApplyCustomThemeSelection(_personalizationDraft.CustomThemeId);
            }
            else if (_themeEditorSection != null)
            {
                _themeEditorSection.IsVisible = false;
            }

            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Persists the personalization draft to app data and optionally applies the new theme immediately.
        /// </summary>
        private async Task SavePersonalizationAsync(bool applyImmediately = true)
        {
            var languageChanged = !string.Equals(
                _personalizationBaseline.Language,
                _personalizationDraft.Language,
                StringComparison.OrdinalIgnoreCase);

            // Persist in-editor custom theme color changes before updating app data.
            if (_editingThemeDraft != null)
            {
                var existingTheme = _customThemesStore.FindById(_editingThemeDraft.Id);
                if (existingTheme != null)
                {
                    existingTheme.Name = _editingThemeDraft.Name;
                    existingTheme.BaseAppearance = _editingThemeDraft.BaseAppearance;
                    existingTheme.Colors = new Dictionary<string, string>(
                        _editingThemeDraft.Colors,
                        StringComparer.OrdinalIgnoreCase);
                    await _customThemesStore.SaveAsync();
                }
            }

            // Write appearance, language, and custom theme selection to app data.
            _appData.Data.Personalization.Appearance = _personalizationDraft.Appearance;
            _appData.Data.Personalization.Language = _personalizationDraft.Language;
            _appData.Data.Personalization.CustomThemeId = _personalizationDraft.CustomThemeId;
            _appData.Data.Personalization.Normalize();

            // Refresh personalization baselines used by unsaved-change detection.
            _personalizationBaseline = new AppPersonalizationConfig
            {
                Appearance = _personalizationDraft.Appearance,
                Language = _personalizationDraft.Language,
                CustomThemeId = _personalizationDraft.CustomThemeId
            };

            if (!applyImmediately)
            {
                return;
            }

            // Apply the saved theme and palette without waiting for restart.
            _themeService.ApplyFromSettings();

            if (languageChanged)
            {
                _localization.ApplyCulture();
            }
        }

        /// <summary>
        /// Shows a simple text-input prompt dialog and returns the user's entry.
        /// </summary>
        private static Task<string?> PromptAsync(string title, string message, string defaultValue) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayPromptAsync(title, message, initialValue: defaultValue)
                : Task.FromResult<string?>(null);

        /// <summary>
        /// Creates a deep copy of a custom theme for editing without modifying the stored version.
        /// </summary>
        private static CustomTheme CloneCustomTheme(CustomTheme source) =>
            new()
            {
                Id = source.Id,
                Name = source.Name,
                BaseAppearance = source.BaseAppearance,
                Colors = new Dictionary<string, string>(source.Colors, StringComparer.OrdinalIgnoreCase)
            };


        // Styling helpers

        /// <summary>
        /// Creates the outer container used for one module settings group.
        /// </summary>
        private static Border CreateModuleSectionBorder()
        {
            var border = new Border
            {
                Style = GetStyleResource(TransparentBorderStyleKey)
            };

            return border;
        }

        /// <summary>
        /// Creates the card container used for one individual setting.
        /// </summary>
        private static Border CreateSettingCardBorder()
        {
            var border = new Border
            {
                Style = GetStyleResource(TransparentBorderStyleKey)
            };

            return border;
        }

        /// <summary>
        /// Creates a compact sub-group header label used to group related settings
        /// within one category (e.g. "Ports", "API", "Consoles", "Updates").
        /// </summary>
        private static Label CreateSubGroupHeader(string text)
        {
            var label = new Label
            {
                Text = text,
                Style = GetStyleResource(SubGroupHeaderLabelStyleKey)
            };
            return label;
        }

        /// <summary>
        /// Creates the primary label for a setting name.
        /// </summary>
        private static Label CreateCardTitle(string text)
        {
            var label = new Label
            {
                Text = text,
                Style = GetStyleResource(CardTitleLabelStyleKey)
            };
            return label;
        }

        /// <summary>
        /// Creates the secondary label used for setting descriptions.
        /// </summary>
        private static Label CreateCardDescription(string text)
        {
            var label = new Label
            {
                Text = text,
                Style = GetStyleResource(CardDescriptionLabelStyleKey)
            };
            return label;
        }

        /// <summary>
        /// Creates the compact secondary label used for inline helper rows.
        /// </summary>
        private static Label CreateSecondaryLabel(string text)
        {
            var label = new Label
            {
                Text = text,
                Style = GetStyleResource(SecondaryLabelStyleKey)
            };
            return label;
        }

        /// <summary>
        /// Creates a compact toggle used by inline helper rows.
        /// </summary>
        private static CompactToggle CreateInlineToggle() =>
            CreateCompactToggle(false);

        /// <summary>
        /// Creates a compact helper row that keeps the custom-value toggle close to its label.
        /// </summary>
        private static Grid CreateInlineToggleRow(string text, CompactToggle toggle)
        {
            var label = CreateSecondaryLabel(text);
            label.VerticalOptions = LayoutOptions.Center;
            label.LineBreakMode = LineBreakMode.NoWrap;

            var row = new Grid
            {
                ColumnSpacing = 8,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                MinimumHeightRequest = 22,
                Margin = new Thickness(0, 4, 0, 0)
            };
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            row.Children.Add(label);
            Grid.SetColumn(label, 0);

            toggle.View.HorizontalOptions = LayoutOptions.End;
            row.Children.Add(toggle.View);
            Grid.SetColumn(toggle.View, 1);

            return row;
        }

        /// <summary>
        /// Creates a compact secondary action button used inside the Ollama account card.
        /// </summary>
        private static Button CreateInlineActionButton(string text, EventHandler clicked)
        {
            var button = new Button
            {
                Text = text,
                Style = GetStyleResource(InlineActionButtonStyleKey)
            };

            button.Clicked += clicked;
            return button;
        }

        /// <summary>
        /// Applies the Ollama account button color for the current sign-in state.
        /// </summary>
        private static void ApplyOllamaAccountButtonState(Button button, bool isSignedIn)
        {
            button.BackgroundColor = isSignedIn
                ? GetColorResource("ActionRed", Color.FromArgb("#FF453A"))
                : GetColorResource("ActionBlue", Color.FromArgb("#0A84FF"));
            button.TextColor = GetColorResource("White", Colors.White);
            button.BorderWidth = 0;
            button.Opacity = button.IsEnabled ? 1.0 : 0.55;
        }

        /// <summary>
        /// Creates a compact picker that follows the shared settings page typography and colors.
        /// </summary>
        private static Picker CreatePicker(string? title, double fontSize)
        {
            var picker = new Picker
            {
                Title = title,
                Style = GetStyleResource(PickerStyleKey),
                HorizontalOptions = LayoutOptions.Fill
            };

            // Apply after Style so callers' fontSize wins over SettingsPickerStyle defaults.
            picker.FontSize = fontSize;
            ApplyCompactPickerStyle(picker);
            return picker;
        }

        /// <summary>
        /// Creates a subtle field container for pickers so they match the weight of text inputs.
        /// </summary>
        private static Border CreatePickerContainer(Picker picker, double? maximumWidth = null)
        {
            var border = new Border
            {
                Style = GetStyleResource(FieldBorderStyleKey),
                Content = picker
            };

            if (maximumWidth is { } cap)
            {
                border.MaximumWidthRequest = cap;
                border.HorizontalOptions = LayoutOptions.End;
            }

            return border;
        }

        /// <summary>
        /// Creates the compact fixed-width picker shell used by the Updates category.
        /// </summary>
        private static Border CreateUpdatePickerContainer(Picker picker)
        {
            var border = CreatePickerContainer(picker);
            border.WidthRequest = 132;
            border.MinimumWidthRequest = 132;
            return border;
        }

        /// <summary>
        /// Creates a bordered field shell shared by text inputs and password editors.
        /// </summary>
        private static Border CreateFieldContainer(View content, Thickness? padding = null)
        {
            var border = new Border
            {
                Style = GetStyleResource(FieldBorderStyleKey),
                Content = content
            };

            if (padding != null)
            {
                border.Padding = padding.Value;
            }

            return border;
        }

        /// <summary>
        /// Creates a compact toggle with a fixed custom visual used instead of the native switch.
        /// </summary>
        private static CompactToggle CreateCompactToggle(bool isToggled) =>
            new(isToggled);

        /// <summary>
        /// Normalizes the native picker chrome so it does not render an extra WinUI border inside the card.
        /// </summary>
        private static void ApplyCompactPickerStyle(Picker picker)
        {
            void ApplyPlatformStyle()
            {
#if WINDOWS
                if (picker.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ComboBox comboBox)
                {
                    var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    comboBox.Background = transparentBrush;
                    comboBox.BorderBrush = transparentBrush;
                    comboBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    comboBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    comboBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    comboBox.UseSystemFocusVisuals = false;
                }
#endif
            }

            picker.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }

        /// <summary>
        /// Applies a quieter WinUI scrollbar treatment so the overlay keeps its minimalist look.
        /// </summary>
        private static void ApplyScrollViewChrome(ScrollView scrollView, bool isSidebar)
        {
            void ApplyPlatformStyle()
            {
#if WINDOWS
                if (scrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer viewer)
                {
                    var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    viewer.Background = transparentBrush;
                    viewer.BorderBrush = transparentBrush;
                    viewer.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    viewer.Padding = new Microsoft.UI.Xaml.Thickness(0);

                    if (!isSidebar)
                    {
                        viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                        viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                    }

                    void Restyle()
                    {
                        StyleScrollBars(viewer, isSidebar);
                    }

                    viewer.Loaded -= OnViewerLoaded;
                    viewer.Loaded += OnViewerLoaded;
                    viewer.SizeChanged -= OnViewerSizeChanged;
                    viewer.SizeChanged += OnViewerSizeChanged;
                    viewer.DispatcherQueue.TryEnqueue(Restyle);

                    void OnViewerLoaded(object? sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Restyle();
                    void OnViewerSizeChanged(object? sender, Microsoft.UI.Xaml.SizeChangedEventArgs e) => Restyle();
                }
#endif
            }

            scrollView.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }

#if WINDOWS
        /// <summary>
        /// Restyles WinUI scrollbars to sit tighter to the edge with lower visual weight.
        /// </summary>
        private static void StyleScrollBars(Microsoft.UI.Xaml.Controls.ScrollViewer viewer, bool isSidebar)
        {
            var thumbBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(isSidebar ? (byte)54 : (byte)92, 255, 255, 255));
            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var hiddenOpacity = isSidebar ? (double?)null : 0;

            foreach (var scrollBar in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(viewer))
            {
                if (!isSidebar)
                {
                    scrollBar.Opacity = 0;
                    scrollBar.Width = 0;
                    scrollBar.MinWidth = 0;
                    scrollBar.Height = 0;
                    scrollBar.MinHeight = 0;
                }

                if (scrollBar.Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical)
                {
                    scrollBar.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right;
                    scrollBar.Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4);
                    scrollBar.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    scrollBar.Background = transparentBrush;
                    scrollBar.Foreground = thumbBrush;
                    scrollBar.Opacity = hiddenOpacity ?? 0.18;
                }
                else
                {
                    scrollBar.Height = isSidebar ? 3 : 0;
                    scrollBar.MinHeight = isSidebar ? 3 : 0;
                    scrollBar.Background = transparentBrush;
                    scrollBar.Opacity = hiddenOpacity ?? 0.2;
                }

                foreach (var repeatButton in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>(scrollBar))
                {
                    repeatButton.Background = transparentBrush;
                    repeatButton.BorderBrush = transparentBrush;
                    repeatButton.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    repeatButton.Opacity = 0;
                }

                foreach (var border in FindDescendants<Microsoft.UI.Xaml.Controls.Border>(scrollBar))
                {
                    border.Background = transparentBrush;
                    border.BorderBrush = transparentBrush;
                    border.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                }

                foreach (var thumb in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.Thumb>(scrollBar))
                {
                    thumb.Background = thumbBrush;
                    thumb.BorderBrush = transparentBrush;
                    thumb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    thumb.MinWidth = isSidebar ? 6 : 6;
                    thumb.Width = isSidebar ? 6 : 6;
                    thumb.MinHeight = 18;
                    thumb.Opacity = isSidebar ? 0.38 : 0.55;
                }
            }
        }

        /// <summary>
        /// Enumerates descendants of the requested WinUI type.
        /// </summary>
        private static IEnumerable<T> FindDescendants<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
        {
            var queue = new Queue<Microsoft.UI.Xaml.DependencyObject>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);

                for (var index = 0; index < childCount; index++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, index);
                    if (child is T typed)
                    {
                        yield return typed;
                    }

                    queue.Enqueue(child);
                }
            }
        }
#endif


        /// <summary>
        /// Creates a password editor that keeps the entry styling consistent with regular text fields.
        /// </summary>
        private static (View Control, Entry Entry) CreatePasswordField(string? text)
        {
            var entry = CreateTextEntry(text, isPassword: true, clearButtonVisibility: ClearButtonVisibility.Never);
            entry.Margin = new Thickness(12, 0, 36, 0);
            var toggleIcon = new Image
            {
                Source = PasswordIconHidden,
                Style = GetStyleResource(PasswordToggleImageStyleKey)
            };

            var toggleButton = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                MinimumWidthRequest = 32,
                MinimumHeightRequest = 32,
                Padding = 0,
                Margin = new Thickness(0, 0, 2, 0),
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Colors.Transparent,
                StrokeThickness = 0,
                Content = toggleIcon
            };

            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (_, _) =>
            {
                entry.IsPassword = !entry.IsPassword;
                UpdatePasswordToggleIcon(toggleIcon, entry.IsPassword);
            };
            toggleButton.GestureRecognizers.Add(tapGesture);

            var grid = new Grid { MinimumHeightRequest = 36 };
            grid.Children.Add(entry);
            grid.Children.Add(toggleButton);
            toggleButton.ZIndex = 1;
            return (CreateFieldContainer(grid, new Thickness(0)), entry);
        }

        /// <summary>
        /// Updates the password visibility icon to reflect the current hidden or visible state.
        /// </summary>
        private static void UpdatePasswordToggleIcon(Image toggleIcon, bool isPasswordHidden)
        {
            toggleIcon.Source = isPasswordHidden
                ? PasswordIconHidden
                : PasswordIconVisible;
        }

        /// <summary>
        /// Creates a standard text entry configured for the requested text behavior.
        /// </summary>
        private static (View Control, Entry Entry) CreateTextField(string? text, bool isPassword = false, ClearButtonVisibility clearButtonVisibility = ClearButtonVisibility.WhileEditing)
        {
            var entry = CreateTextEntry(text, isPassword, clearButtonVisibility);
            return (CreateFieldContainer(entry), entry);
        }

        /// <summary>
        /// Creates a standard text entry configured for the requested text behavior.
        /// </summary>
        private static Entry CreateTextEntry(string? text, bool isPassword = false, ClearButtonVisibility clearButtonVisibility = ClearButtonVisibility.WhileEditing)
        {
            var entry = new Entry
            {
                Text = text,
                ClearButtonVisibility = clearButtonVisibility,
                IsPassword = isPassword,
                Style = GetStyleResource(TextEntryStyleKey)
            };

            ApplyFlatEntryStyle(entry);
            return entry;
        }

        /// <summary>
        /// Removes native WinUI chrome so entries match the shared field borders.
        /// </summary>
        private static void ApplyFlatEntryStyle(Entry entry)
        {
            void ApplyPlatformStyle()
            {
#if WINDOWS
                var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

                switch (entry.Handler?.PlatformView)
                {
                    case Microsoft.UI.Xaml.Controls.TextBox textBox:
                        textBox.Background = transparentBrush;
                        textBox.BorderBrush = transparentBrush;
                        textBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                        textBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                        textBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        textBox.UseSystemFocusVisuals = false;
                        break;
                    case Microsoft.UI.Xaml.Controls.PasswordBox passwordBox:
                        passwordBox.Background = transparentBrush;
                        passwordBox.BorderBrush = transparentBrush;
                        passwordBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                        passwordBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                        passwordBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                        passwordBox.UseSystemFocusVisuals = false;
                        break;
                }
#endif
            }

            entry.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }

        /// <summary>
        /// Applies the read-only visual treatment used by ASLM-managed entries.
        /// </summary>
        private static void ApplyTextEntryState(Entry entry, bool isReadOnly)
        {
            entry.IsReadOnly = isReadOnly;
            entry.Opacity = isReadOnly ? 0.72 : 1.0;
        }


        // Misc helpers

        /// <summary>
        /// Creates the read-only installed-state badge shown for auto-detected ASLM engines.
        /// </summary>
        private Border CreateStatusBadge(string text, bool isPositive)
        {
            var label = new Label
            {
                Text = text,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center
            };

            label.TextColor = isPositive
                ? GetColorResource("LinkColor", Color.FromArgb("#D8E8FF"))
                : GetColorResource("LabelSecondary", Color.FromArgb("#99EBEBF5"));

            return new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 0 },
                Padding = 0,
                BackgroundColor = Colors.Transparent,
                HorizontalOptions = LayoutOptions.End,
                VerticalOptions = LayoutOptions.Center,
                Content = label
            };
        }

#if WINDOWS
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        /// <summary>
        /// Shows the Windows save picker for exporting a custom theme JSON file.
        /// </summary>
        private static async Task<string?> PickExportThemeFilePathAsync(string suggestedFileName)
        {
            var native = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView;
            nint hwnd = 0;
            if (native is Microsoft.UI.Xaml.Window win)
            {
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
            }
            else if (native != null)
            {
                try
                {
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                }
                catch
                {
                    hwnd = GetForegroundWindow();
                }
            }
            else
            {
                hwnd = GetForegroundWindow();
            }

            if (hwnd == 0)
            {
                return null;
            }

            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "ASLM_theme" : suggestedFileName
            };

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("ASLM theme (.json)", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
#endif

    }
}
