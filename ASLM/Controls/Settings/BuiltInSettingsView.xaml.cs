// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Hosts the stable XAML trees used by every built-in settings category.
    /// </summary>
    public partial class BuiltInSettingsView : ContentView
    {
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
        public Button GitHubAccountAction => GitHubAccountButton;
        public Label GitHubAccountState => GitHubAccountStatus;
        public Button OllamaAccountAction => OllamaAccountButton;
        public Label OllamaAccountState => OllamaAccountStatus;
        public SettingsToggle CheckUpdatesInput => CheckUpdatesToggle;
        public SettingsToggle AutoUpdatesInput => AutoUpdatesToggle;
        public Picker AppChannelInput => AppChannelPicker;
        public Picker ModuleModeInput => ModuleModePicker;
        public Picker ModuleChannelInput => ModuleChannelPicker;
        public Label InstalledReleaseSummary => ManualInstalledSummary;
        public Button CheckAslmUpdateAction => CheckAslmUpdateButton;
        public Button PrepareAppUpdateAction => PrepareAppUpdateButton;
        public Button RestartAppUpdateAction => RestartAppUpdateButton;
        public Label AppUpdateState => UpdateStatusLabel;
        public Label OllamaVersionDescriptionLabel => OllamaVersionDescription;
        public Button CheckOllamaUpdateAction => CheckOllamaUpdateButton;
        public Button OllamaUpdateAction => OllamaUpdateButton;
        public Label OllamaUpdateState => OllamaUpdateStatusLabel;
        public Picker LanguageInput => LanguagePicker;
        public Picker AppearanceInput => AppearancePicker;
        public VerticalStackLayout CustomThemesHost => CustomThemeSection;
        public VerticalStackLayout ThemeEditorHost => ThemeEditorSection;
        public Picker CustomThemeInput => CustomThemePicker;
        public Picker BaseAppearanceInput => BaseAppearancePicker;
        public Button CreateThemeAction => CreateThemeButton;
        public Button ImportThemeAction => ImportThemeButton;
        public Button ExportThemeAction => ExportThemeButton;
        public Button DeleteThemeAction => DeleteThemeButton;
        public Label ThemeColorsTitle => ThemeColorsHeader;

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
            ModulePortDescription.Text = L.Get(LocalizationKeys.Settings_ModulePortDescription);
            ApiCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_API);
            ApiServerRow.Title = L.Get(LocalizationKeys.Settings_ApiServer_Title);
            ApiServerRow.Description = L.Get(LocalizationKeys.Settings_ApiServer_Description);
            ConsolesCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_Consoles);
            ConsoleSidebarRow.Title = L.Get(LocalizationKeys.Settings_ConsolesPage_Title);
            ConsoleSidebarRow.Description = L.Get(LocalizationKeys.Settings_ConsolesPage_Description);
            ConsoleIndividualRow.Title = L.Get(LocalizationKeys.Settings_IndividualConsoles_Title);
            ConsoleIndividualRow.Description = L.Get(LocalizationKeys.Settings_IndividualConsoles_Description);
            ConsoleCompletedRow.Title = L.Get(LocalizationKeys.Settings_CompletedConsoles_Title);
            ConsoleCompletedRow.Description = L.Get(LocalizationKeys.Settings_CompletedConsoles_Description);
            LegalCategory.Title = L.Get(LocalizationKeys.Settings_SubGroup_Legal);
            LegalAutoAcceptRow.Title = L.Get(LocalizationKeys.Settings_Legal_AutoAcceptUpdates_Title);
            LegalAutoAcceptRow.Description = L.Get(LocalizationKeys.Settings_Legal_AutoAcceptUpdates_Description);

            // Account settings.
            AslmAccountTitle.Text = L.Get(LocalizationKeys.Settings_AslmAccount_Title);
            DisplayNameTitle.Text = L.Get(LocalizationKeys.Settings_DisplayName);
            DisplayNameDescription.Text = L.Get(LocalizationKeys.Settings_DisplayNameDescription);
            GitHubAccountTitle.Text = L.Get(LocalizationKeys.Settings_GitHub_TokenTitle);
            OllamaAccountTitle.Text = L.Get(LocalizationKeys.Settings_OllamaAccount_Title);

            // Update settings.
            CheckUpdatesRow.Title = L.Get(LocalizationKeys.Settings_CheckUpdates_Title);
            CheckUpdatesRow.Description = L.Get(LocalizationKeys.Settings_CheckUpdates_Description);
            AutoUpdatesRow.Title = L.Get(LocalizationKeys.Settings_AutoInstall_Title);
            AutoUpdatesRow.Description = L.Get(LocalizationKeys.Settings_AutoInstall_Description);
            AppChannelRow.Title = L.Get(LocalizationKeys.Settings_AppChannel_Title);
            AppChannelRow.Description = L.Get(LocalizationKeys.Settings_AppChannel_Description);
            ModuleModeRow.Title = L.Get(LocalizationKeys.Settings_ModuleUpdateMode_Title);
            ModuleModeRow.Description = L.Get(LocalizationKeys.Settings_ModuleUpdateMode_Description);
            ModuleChannelRow.Title = L.Get(LocalizationKeys.Settings_ModuleChannel_Title);
            ModuleChannelRow.Description = L.Get(LocalizationKeys.Settings_ModuleChannel_Description);
            ManualUpdateTitle.Text = L.Get(LocalizationKeys.Settings_ManualCheck_Title);
            CheckAslmUpdateButton.Text = L.Get(LocalizationKeys.Settings_CheckAslmUpdates);
            PrepareAppUpdateButton.Text = L.Get(LocalizationKeys.Settings_PrepareAslmUpdate);
            RestartAppUpdateButton.Text = L.Get(LocalizationKeys.Settings_RestartNow);
            OllamaUpdateTitle.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_Title);
            CheckOllamaUpdateButton.Text = L.Get(LocalizationKeys.Settings_CheckOllamaUpdates);
            OllamaUpdateButton.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_Button);

            // Personalization settings.
            LanguageRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Language);
            LanguageRow.Description = L.Get(LocalizationKeys.Settings_Personalization_LanguageDescription);
            AppearanceRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Mode);
            AppearanceRow.Description = L.Get(LocalizationKeys.Settings_Personalization_ModeDescription);
            ThemeManagementRow.Title = L.Get(LocalizationKeys.Settings_Personalization_Themes);
            ThemeManagementRow.Description = L.Get(LocalizationKeys.Settings_Personalization_ThemesDescription);
            ActiveThemeRow.Title = L.Get(LocalizationKeys.Settings_Personalization_ActiveTheme);
            CreateThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_New);
            ImportThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Import);
            ExportThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Export);
            DeleteThemeButton.Text = L.Get(LocalizationKeys.Settings_Personalization_Delete);
            BaseAppearanceRow.Title = L.Get(LocalizationKeys.Settings_ThemeEditor_Base);
            BaseAppearanceRow.Description = L.Get(LocalizationKeys.Settings_ThemeEditor_BaseDescription);
        }

        /// <summary>
        /// Updates the helper text for the active custom-theme picker.
        /// </summary>
        public void SetActiveThemeDescription(string description)
        {
            ActiveThemeRow.Description = description;
        }
    }
}
