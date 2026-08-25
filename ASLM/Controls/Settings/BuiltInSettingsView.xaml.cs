// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Hosts the stable XAML trees used by every built-in settings category.
    /// </summary>
    public partial class BuiltInSettingsView : ContentView
    {
        private const double ThemeColorSingleColumnThreshold = 880;
        private IReadOnlyList<ThemeColorItemViewModel> _themeColors = [];
        private bool? _usesSingleThemeColorColumn;

        /// <summary>
        /// Identifies the left theme-color column items.
        /// </summary>
        public static readonly BindableProperty ThemeColorsLeftProperty = BindableProperty.Create(
            nameof(ThemeColorsLeft),
            typeof(IEnumerable<ThemeColorItemViewModel>),
            typeof(BuiltInSettingsView),
            Array.Empty<ThemeColorItemViewModel>());

        /// <summary>
        /// Identifies the right theme-color column items.
        /// </summary>
        public static readonly BindableProperty ThemeColorsRightProperty = BindableProperty.Create(
            nameof(ThemeColorsRight),
            typeof(IEnumerable<ThemeColorItemViewModel>),
            typeof(BuiltInSettingsView),
            Array.Empty<ThemeColorItemViewModel>());

        /// <summary>
        /// Creates the built-in settings host and localizes its static content.
        /// </summary>
        public BuiltInSettingsView()
        {
            InitializeComponent();
            ApplyLocalization();
        }

        public Entry UserNameInput => UsernameEntry;
        public Entry ModulePortInput => ModulePortEntry;
        public Label ModulePortError => PortErrorLabel;
        public VerticalStackLayout PortsHost => PortsSection;
        public VerticalStackLayout UserProfile => UserProfileSection;
        public SettingsToggle ApiServerInput => ApiServerToggle;
        public SettingsToggle ConsoleSidebarInput => ConsoleSidebarToggle;
        public SettingsToggle ConsoleIndividualInput => ConsoleIndividualToggle;
        public SettingsToggle ConsoleCompletedInput => ConsoleCompletedToggle;
        public SettingsToggle LegalAutoAcceptInput => LegalAutoAcceptToggle;
        public Button AslmAccountAction => AslmAccountButton;
        public Label AslmAccountState => AslmAccountStatus;
        public Label AslmAccountTypeBadge => AslmAccountTypeBadgeLabel;
        public ImageButton AslmAccountLink => AslmAccountLinkButton;
        public Button GitHubAccountAction => GitHubAccountButton;
        public Label GitHubAccountState => GitHubAccountStatus;
        public ImageButton GitHubAccountLink => GitHubAccountLinkButton;
        public Button OllamaAccountAction => OllamaAccountButton;
        public Label OllamaAccountState => OllamaAccountStatus;
        public ImageButton OllamaAccountLink => OllamaAccountLinkButton;
        public SettingsToggle CheckUpdatesInput => CheckUpdatesToggle;
        public SettingsToggle AutoUpdatesInput => AutoUpdatesToggle;
        public Picker AppChannelInput => AppChannelPicker;
        public Picker ModuleChannelInput => ModuleChannelPicker;
        public Label AslmInstalledVersion => AslmInstalledVersionLabel;
        public Label AslmAvailableVersion => AslmAvailableVersionLabel;
        public HorizontalStackLayout AslmUpdateActionContainer => AslmUpdateActionHost;
        public Grid PrepareAppUpdateContainer => PrepareAppUpdateHost;
        public Button PrepareAppUpdateAction => PrepareAppUpdateButton;
        public HorizontalStackLayout PrepareAppUpdateProgressContent => PrepareAppUpdateProgress;
        public ActivityIndicator PrepareAppUpdateProgressSpinner => PrepareAppUpdateSpinner;
        public Label PrepareAppUpdateProgressValue => PrepareAppUpdateProgressPercent;
        public Button RestartAppUpdateAction => RestartAppUpdateButton;
        public Label OllamaInstalledVersion => OllamaInstalledVersionLabel;
        public Label OllamaAvailableVersion => OllamaAvailableVersionLabel;
        public Grid OllamaUpdateContainer => OllamaUpdateHost;
        public Button OllamaUpdateAction => OllamaUpdateButton;
        public HorizontalStackLayout OllamaUpdateProgressContent => OllamaUpdateProgress;
        public ActivityIndicator OllamaUpdateProgressSpinner => OllamaUpdateSpinner;
        public Label OllamaUpdateProgressValue => OllamaUpdateProgressPercent;
        public Picker LanguageInput => LanguagePicker;
        public Picker AppearanceInput => AppearancePicker;
        public VerticalStackLayout CustomThemesHost => CustomThemeSection;
        public SettingsSectionView ThemeEditorHost => ThemeEditorSection;
        public Picker CustomThemeInput => CustomThemePicker;
        public Picker BaseAppearanceInput => BaseAppearancePicker;
        public Button CreateThemeAction => CreateThemeButton;
        public Button ImportThemeAction => ImportThemeButton;
        public Button ExportThemeAction => ExportThemeButton;
        public Button RenameThemeAction => RenameThemeButton;
        public Button DeleteThemeAction => DeleteThemeButton;

        public IEnumerable<ThemeColorItemViewModel> ThemeColorsLeft
        {
            get => (IEnumerable<ThemeColorItemViewModel>)GetValue(ThemeColorsLeftProperty);
            set => SetValue(ThemeColorsLeftProperty, value);
        }

        public IEnumerable<ThemeColorItemViewModel> ThemeColorsRight
        {
            get => (IEnumerable<ThemeColorItemViewModel>)GetValue(ThemeColorsRightProperty);
            set => SetValue(ThemeColorsRightProperty, value);
        }

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

        /// <summary>
        /// Applies current localized strings to the declarative built-in settings rows.
        /// </summary>
        public void ApplyLocalization()
        {
            // Core settings.
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

        /// <summary>
        /// Updates the dynamic title of the selected theme's editor category.
        /// </summary>
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
            var useSingleColumn = availableWidth <= 0 || availableWidth < ThemeColorSingleColumnThreshold;
            if (!force && _usesSingleThemeColorColumn == useSingleColumn)
            {
                return;
            }

            _usesSingleThemeColorColumn = useSingleColumn;
            ThemeColorsGrid.ColumnDefinitions[0].Width = GridLength.Star;
            ThemeColorsGrid.ColumnDefinitions[1].Width = useSingleColumn ? 0 : 1;
            ThemeColorsGrid.ColumnDefinitions[2].Width = useSingleColumn ? 0 : GridLength.Star;
            ThemeColorsDivider.IsVisible = !useSingleColumn;
            ThemeColorsRightLayout.IsVisible = !useSingleColumn;
            ThemeColorsLeftLayout.Margin = useSingleColumn
                ? new Thickness(0)
                : new Thickness(0, 0, 12, 0);

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
