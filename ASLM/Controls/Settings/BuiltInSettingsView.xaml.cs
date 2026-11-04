// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Hosts the stable XAML trees used by every built-in settings category.
    /// </summary>
    public partial class BuiltInSettingsView : ContentView
    {
        // Fields and constants

        /// <summary>
        /// Editor width in device-independent units below which the theme colors use a single column.
        /// </summary>
        private const double ThemeColorSingleColumnThreshold = 880;

        /// <summary>
        /// All theme palette rows currently shown, split between the left and right column by the layout.
        /// </summary>
        private IReadOnlyList<ThemeColorItemViewModel> _themeColors = [];

        /// <summary>
        /// Whether the theme colors are currently laid out in one column, or null before the first layout.
        /// </summary>
        private bool? _usesSingleThemeColorColumn;


        // Bindable properties

        /// <summary>
        /// Identifies the bindable property that holds the theme-color rows of the left column.
        /// </summary>
        public static readonly BindableProperty ThemeColorsLeftProperty = BindableProperty.Create(
            nameof(ThemeColorsLeft),
            typeof(IEnumerable<ThemeColorItemViewModel>),
            typeof(BuiltInSettingsView),
            Array.Empty<ThemeColorItemViewModel>());

        /// <summary>
        /// Identifies the bindable property that holds the theme-color rows of the right column.
        /// </summary>
        public static readonly BindableProperty ThemeColorsRightProperty = BindableProperty.Create(
            nameof(ThemeColorsRight),
            typeof(IEnumerable<ThemeColorItemViewModel>),
            typeof(BuiltInSettingsView),
            Array.Empty<ThemeColorItemViewModel>());


        // Initialization

        /// <summary>
        /// Creates the built-in settings host and localizes its static content.
        /// </summary>
        public BuiltInSettingsView()
        {
            InitializeComponent();

            // Cloud account controls are only offered when the SUNRISE integration is enabled.
            AslmAccountCard.IsVisible = SunriseService.IsEnabled;
            UserProfileSection.IsVisible = SunriseService.IsEnabled;
            ApplyLocalization();
        }


        // Profile and core settings controls

        /// <summary>
        /// Text box for the local display name.
        /// </summary>
        public Entry UserNameInput => UsernameEntry;

        /// <summary>
        /// Text box for the first port of the range reserved for modules.
        /// </summary>
        public Entry ModulePortInput => ModulePortEntry;

        /// <summary>
        /// Label that shows the validation error of the module port entry.
        /// </summary>
        public Label ModulePortError => PortErrorLabel;

        /// <summary>
        /// Container of the ports section, which the presenter shows or hides as a whole.
        /// </summary>
        public VerticalStackLayout PortsHost => PortsSection;

        /// <summary>
        /// Container of the user profile section, which is only visible when SUNRISE accounts are enabled.
        /// </summary>
        public VerticalStackLayout UserProfile => UserProfileSection;

        /// <summary>
        /// Switch that enables the ASLM Mirror Server.
        /// </summary>
        public SettingsToggle ApiServerInput => ApiServerToggle;

        /// <summary>
        /// Switch that shows the Consoles page in the navigation sidebar.
        /// </summary>
        public SettingsToggle ConsoleSidebarInput => ConsoleSidebarToggle;

        /// <summary>
        /// Switch that shows a separate console for each module process.
        /// </summary>
        public SettingsToggle ConsoleIndividualInput => ConsoleIndividualToggle;

        /// <summary>
        /// Switch that keeps the consoles of completed processes visible.
        /// </summary>
        public SettingsToggle ConsoleCompletedInput => ConsoleCompletedToggle;

        /// <summary>
        /// Switch that accepts updated legal policies automatically.
        /// </summary>
        public SettingsToggle LegalAutoAcceptInput => LegalAutoAcceptToggle;

        /// <summary>
        /// Switch that reopens the last visited page when ASLM starts.
        /// </summary>
        public SettingsToggle RestoreLastPageInput => RestoreLastPageToggle;

        /// <summary>
        /// Switch that disables the Home page.
        /// </summary>
        public SettingsToggle DisableHomePageInput => DisableHomePageToggle;


        // Account controls

        /// <summary>
        /// Button that connects or disconnects the ASLM cloud account.
        /// </summary>
        public Button AslmAccountAction => AslmAccountButton;

        /// <summary>
        /// Label that describes the state of the ASLM cloud account.
        /// </summary>
        public Label AslmAccountState => AslmAccountStatus;

        /// <summary>
        /// Badge label that shows the type of the connected ASLM account.
        /// </summary>
        public Label AslmAccountTypeBadge => AslmAccountTypeBadgeLabel;

        /// <summary>
        /// Link button that opens the ASLM account page; hidden until a link is available.
        /// </summary>
        public ImageButton AslmAccountLink => AslmAccountLinkButton;

        /// <summary>
        /// Button that connects or disconnects the GitHub account.
        /// </summary>
        public Button GitHubAccountAction => GitHubAccountButton;

        /// <summary>
        /// Label that describes the state of the GitHub account.
        /// </summary>
        public Label GitHubAccountState => GitHubAccountStatus;

        /// <summary>
        /// Link button that opens the GitHub account page.
        /// </summary>
        public ImageButton GitHubAccountLink => GitHubAccountLinkButton;

        /// <summary>
        /// Button that connects or disconnects the Ollama account.
        /// </summary>
        public Button OllamaAccountAction => OllamaAccountButton;

        /// <summary>
        /// Label that describes the state of the Ollama account.
        /// </summary>
        public Label OllamaAccountState => OllamaAccountStatus;

        /// <summary>
        /// Link button that opens the Ollama account page.
        /// </summary>
        public ImageButton OllamaAccountLink => OllamaAccountLinkButton;


        // Update controls

        /// <summary>
        /// Switch that enables automatic update checks.
        /// </summary>
        public SettingsToggle CheckUpdatesInput => CheckUpdatesToggle;

        /// <summary>
        /// Switch that enables automatic installation of updates.
        /// </summary>
        public SettingsToggle AutoUpdatesInput => AutoUpdatesToggle;

        /// <summary>
        /// Picker for the ASLM update channel.
        /// </summary>
        public Picker AppChannelInput => AppChannelPicker;

        /// <summary>
        /// Picker for the default update channel of modules.
        /// </summary>
        public Picker ModuleChannelInput => ModuleChannelPicker;

        /// <summary>
        /// Label that shows the installed ASLM version.
        /// </summary>
        public Label AslmInstalledVersion => AslmInstalledVersionLabel;

        /// <summary>
        /// Label that shows the newest available ASLM version; hidden when there is none.
        /// </summary>
        public Label AslmAvailableVersion => AslmAvailableVersionLabel;

        /// <summary>
        /// Row that holds the ASLM update buttons and is visible only while an update action is possible.
        /// </summary>
        public HorizontalStackLayout AslmUpdateActionContainer => AslmUpdateActionHost;

        /// <summary>
        /// Container that overlays the download button and its progress indicator for the ASLM update.
        /// </summary>
        public Grid PrepareAppUpdateContainer => PrepareAppUpdateHost;

        /// <summary>
        /// Button that downloads the ASLM update.
        /// </summary>
        public Button PrepareAppUpdateAction => PrepareAppUpdateButton;

        /// <summary>
        /// Progress content (spinner, stage and percentage) shown on top of the ASLM download button.
        /// </summary>
        public HorizontalStackLayout PrepareAppUpdateProgressContent => PrepareAppUpdateProgress;

        /// <summary>
        /// Spinner of the ASLM update download progress.
        /// </summary>
        public ActivityIndicator PrepareAppUpdateProgressSpinner => PrepareAppUpdateSpinner;

        /// <summary>
        /// Exposes the live self-update stage label to the settings presenter.
        /// </summary>
        public Label PrepareAppUpdateProgressStatus => PrepareAppUpdateProgressText;

        /// <summary>
        /// Label that shows the percentage of the ASLM update download.
        /// </summary>
        public Label PrepareAppUpdateProgressValue => PrepareAppUpdateProgressPercent;

        /// <summary>
        /// Button that installs the downloaded ASLM update and restarts the application.
        /// </summary>
        public Button RestartAppUpdateAction => RestartAppUpdateButton;

        /// <summary>
        /// Label that shows the installed Ollama engine version.
        /// </summary>
        public Label OllamaInstalledVersion => OllamaInstalledVersionLabel;

        /// <summary>
        /// Label that shows the newest available Ollama engine version; hidden when there is none.
        /// </summary>
        public Label OllamaAvailableVersion => OllamaAvailableVersionLabel;

        /// <summary>
        /// Container that overlays the update button and its progress indicator for the Ollama engine.
        /// </summary>
        public Grid OllamaUpdateContainer => OllamaUpdateHost;

        /// <summary>
        /// Button that downloads and installs the Ollama engine update.
        /// </summary>
        public Button OllamaUpdateAction => OllamaUpdateButton;

        /// <summary>
        /// Progress content (spinner, stage and percentage) shown on top of the Ollama update button.
        /// </summary>
        public HorizontalStackLayout OllamaUpdateProgressContent => OllamaUpdateProgress;

        /// <summary>
        /// Spinner of the Ollama update progress.
        /// </summary>
        public ActivityIndicator OllamaUpdateProgressSpinner => OllamaUpdateSpinner;

        /// <summary>
        /// Exposes the live engine-update stage label to the settings presenter.
        /// </summary>
        public Label OllamaUpdateProgressStatus => OllamaUpdateProgressText;

        /// <summary>
        /// Label that shows the percentage of the Ollama update progress.
        /// </summary>
        public Label OllamaUpdateProgressValue => OllamaUpdateProgressPercent;


        // Personalization controls

        /// <summary>
        /// Picker for the interface language.
        /// </summary>
        public Picker LanguageInput => LanguagePicker;

        /// <summary>
        /// Picker for the built-in theme mode.
        /// </summary>
        public Picker AppearanceInput => AppearancePicker;

        /// <summary>
        /// Container of the custom theme management rows, which the presenter shows or hides as a whole.
        /// </summary>
        public VerticalStackLayout CustomThemesHost => CustomThemeSection;

        /// <summary>
        /// Section of the theme editor; its title shows the name of the selected theme.
        /// </summary>
        public SettingsSectionView ThemeEditorHost => ThemeEditorSection;

        /// <summary>
        /// Picker for the active custom theme.
        /// </summary>
        public Picker CustomThemeInput => CustomThemePicker;

        /// <summary>
        /// Picker for the built-in theme that the edited custom theme is based on.
        /// </summary>
        public Picker BaseAppearanceInput => BaseAppearancePicker;

        /// <summary>
        /// Button that creates a new custom theme.
        /// </summary>
        public Button CreateThemeAction => CreateThemeButton;

        /// <summary>
        /// Button that imports a custom theme from a file.
        /// </summary>
        public Button ImportThemeAction => ImportThemeButton;

        /// <summary>
        /// Button that exports the selected custom theme to a file.
        /// </summary>
        public Button ExportThemeAction => ExportThemeButton;

        /// <summary>
        /// Button that renames the selected custom theme.
        /// </summary>
        public Button RenameThemeAction => RenameThemeButton;

        /// <summary>
        /// Button that deletes the selected custom theme.
        /// </summary>
        public Button DeleteThemeAction => DeleteThemeButton;


        // Theme color layout

        /// <summary>
        /// Theme-color rows shown in the left column.
        /// </summary>
        public IEnumerable<ThemeColorItemViewModel> ThemeColorsLeft
        {
            get => (IEnumerable<ThemeColorItemViewModel>)GetValue(ThemeColorsLeftProperty);
            set => SetValue(ThemeColorsLeftProperty, value);
        }

        /// <summary>
        /// Theme-color rows shown in the right column; empty while the editor uses a single column.
        /// </summary>
        public IEnumerable<ThemeColorItemViewModel> ThemeColorsRight
        {
            get => (IEnumerable<ThemeColorItemViewModel>)GetValue(ThemeColorsRightProperty);
            set => SetValue(ThemeColorsRightProperty, value);
        }


        // Category switching

        /// <summary>
        /// Shows one built-in category while retaining the other XAML trees for reuse.
        /// </summary>
        public void ShowCategory(SettingsCategoryKind kind)
        {
            CoreContainer.IsVisible = kind == SettingsCategoryKind.Aslm;
            AccountsContainer.IsVisible = kind == SettingsCategoryKind.Accounts;
            UpdatesContainer.IsVisible = kind == SettingsCategoryKind.Updates;
            PersonalizationContainer.IsVisible = kind == SettingsCategoryKind.Personalization;
        }

        /// <summary>
        /// Hides every built-in category without destroying its controls.
        /// </summary>
        public void HideCategories()
        {
            CoreContainer.IsVisible = false;
            AccountsContainer.IsVisible = false;
            UpdatesContainer.IsVisible = false;
            PersonalizationContainer.IsVisible = false;
        }


        // Localization

        /// <summary>
        /// Applies current localized strings to the declarative built-in settings rows.
        /// </summary>
        public void ApplyLocalization()
        {
            // Core settings.
            RestoreLastPageRow.Title = L.Get(LocalizationKeys.Settings_RestoreLastPage_Title);
            RestoreLastPageRow.Description = string.Empty;
            DisableHomePageRow.Title = L.Get(LocalizationKeys.Settings_DisableHomePage_Title);
            DisableHomePageRow.Description = string.Empty;
            PortsCategory.Title = L.Get(LocalizationKeys.Settings_Ports);
            ModulePortTitle.Text = L.Get(LocalizationKeys.Settings_ModulePortTitle);
            ModulePortInfoButton.Description = string.Empty;
            ApiCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_API);
            ApiServerRow.Title = L.Get(LocalizationKeys.Settings_ApiServer_Title);
            ApiServerRow.Description = string.Empty;
            ConsolesCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_Consoles);
            ConsoleSidebarRow.Title = L.Get(LocalizationKeys.Settings_ConsolesPage_Title);
            ConsoleSidebarRow.Description = string.Empty;
            ConsoleIndividualRow.Title = L.Get(LocalizationKeys.Settings_IndividualConsoles_Title);
            ConsoleIndividualRow.Description = string.Empty;
            ConsoleCompletedRow.Title = L.Get(LocalizationKeys.Settings_CompletedConsoles_Title);
            ConsoleCompletedRow.Description = string.Empty;
            LegalCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_Legal);
            LegalAutoAcceptRow.Title = L.Get(LocalizationKeys.Settings_Legal_AutoAcceptUpdates_Title);
            LegalAutoAcceptRow.Description = string.Empty;

            // Account settings.
            AslmAccountTitle.Text = L.Get(LocalizationKeys.Settings_AslmAccount_Title);
            DisplayNameTitle.Text = L.Get(LocalizationKeys.Settings_DisplayName);
            GitHubAccountTitle.Text = L.Get(LocalizationKeys.Settings_GitHub_TokenTitle);
            OllamaAccountTitle.Text = L.Get(LocalizationKeys.Settings_OllamaAccount_Title);
            ToolTipProperties.SetText(AslmAccountLinkButton, L.Get(LocalizationKeys.Common_Open));
            ToolTipProperties.SetText(GitHubAccountLinkButton, L.Get(LocalizationKeys.Common_Open));
            ToolTipProperties.SetText(OllamaAccountLinkButton, L.Get(LocalizationKeys.Common_Open));

            // Update settings.
            CheckUpdatesRow.Title = L.Get(LocalizationKeys.Settings_CheckUpdates_Title);
            CheckUpdatesRow.Description = string.Empty;
            AutoUpdatesRow.Title = L.Get(LocalizationKeys.Settings_AutoInstall_Title);
            AutoUpdatesRow.Description = string.Empty;
            AppChannelRow.Title = L.Get(LocalizationKeys.Settings_AppChannel_Title);
            AppChannelRow.Description = string.Empty;
            ModuleChannelRow.Title = L.Get(LocalizationKeys.Settings_ModuleChannel_Title);
            ModuleChannelRow.Description = string.Empty;
            PrepareAppUpdateButton.Text = L.Get(LocalizationKeys.Settings_DownloadUpdate);
            PrepareAppUpdateProgressText.Text = L.Get(LocalizationKeys.Settings_Downloading);
            RestartAppUpdateButton.Text = L.Get(LocalizationKeys.Settings_InstallAndRestart);
            OllamaUpdateButton.Text = L.Get(LocalizationKeys.Settings_DownloadUpdate);
            OllamaUpdateProgressText.Text = L.Get(LocalizationKeys.Settings_Downloading);

            // Personalization settings.
            LanguageRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Language);
            LanguageRow.Description = string.Empty;
            AppearanceRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Mode);
            AppearanceRow.Description = string.Empty;
            ThemeManagementRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Themes);
            ThemeManagementRow.Description = string.Empty;
            ActiveThemeRow.Title = L.Get(LocalizationKeys.Settings_Personalization_ActiveTheme);
            ActiveThemeRow.Description = string.Empty;
            CreateThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_New);
            ImportThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Import);
            ExportThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Export);
            RenameThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Rename);
            DeleteThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Delete);
            BaseAppearanceRow.Title = L.Get(LocalizationKeys.Settings_ThemeEditor_Base);
            BaseAppearanceRow.Description = string.Empty;
        }


        // Theme editor

        /// <summary>
        /// Updates the dynamic title of the selected theme's editor category.
        /// </summary>
        /// <param name="title">Section title, normally the name of the theme being edited.</param>
        public void SetThemeEditorTitle(string title)
        {
            ThemeEditorSection.Title = title;
        }

        /// <summary>
        /// Replaces the theme palette rows and distributes them for the current editor width.
        /// </summary>
        public void SetThemeColors(IEnumerable<ThemeColorItemViewModel> colors)
        {
            _themeColors = colors.ToList();
            ApplyThemeColorLayout(ResolveThemeColorEditorWidth(), force: true);
        }

        /// <summary>
        /// Reflows palette rows when the editor crosses its responsive width threshold.
        /// </summary>
        private void OnThemeColorsGridSizeChanged(object? sender, EventArgs e)
        {
            ApplyThemeColorLayout(ResolveThemeColorEditorWidth());
        }

        /// <summary>
        /// Returns the most reliable currently measured width for the color editor.
        /// </summary>
        private double ResolveThemeColorEditorWidth() =>
            ThemeColorsGrid.Width > 0 ? ThemeColorsGrid.Width : Width;

        /// <summary>
        /// Switches between one and two palette columns without recreating row view models.
        /// </summary>
        private void ApplyThemeColorLayout(double availableWidth, bool force = false)
        {
            // Narrow or not yet measured editors use one column; skip the work when nothing changes.
            var useSingleColumn = availableWidth <= 0 || availableWidth < ThemeColorSingleColumnThreshold;
            if (!force && _usesSingleThemeColorColumn == useSingleColumn)
            {
                return;
            }

            // Collapse the divider and right column in single-column mode; otherwise restore the two-column grid.
            _usesSingleThemeColorColumn = useSingleColumn;
            ThemeColorsGrid.ColumnDefinitions[0].Width = GridLength.Star;
            ThemeColorsGrid.ColumnDefinitions[1].Width = useSingleColumn ? 0 : 1;
            ThemeColorsGrid.ColumnDefinitions[2].Width = useSingleColumn ? 0 : GridLength.Star;
            ThemeColorsDivider.IsVisible = !useSingleColumn;
            ThemeColorsRightLayout.IsVisible = !useSingleColumn;
            ThemeColorsLeftLayout.Margin = useSingleColumn
                ? new Thickness(0)
                : new Thickness(0, 0, 12, 0);

            // Single column keeps every row on the left; two columns split the rows at the midpoint.
            if (useSingleColumn)
            {
                ThemeColorsLeft = _themeColors;
                ThemeColorsRight = Array.Empty<ThemeColorItemViewModel>();
                return;
            }

            var midpoint = (_themeColors.Count + 1) / 2;
            ThemeColorsLeft = _themeColors.Take(midpoint).ToList();
            ThemeColorsRight = _themeColors.Skip(midpoint).ToList();
        }
    }
}
