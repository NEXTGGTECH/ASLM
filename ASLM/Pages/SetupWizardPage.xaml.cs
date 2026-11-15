// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Guides the first-run flow from profile setup through module installation.
    /// </summary>
    public partial class SetupWizardPage : ContentPage, ILocalizable, INotifyPropertyChanged
    {
        // Fields and constants

        /// <summary>
        /// Index of the last wizard step (module selection and installation). Steps count from 0 (welcome),
        /// followed by the account step (only with SUNRISE), the port step and the module step.
        /// </summary>
        private const int TotalSteps = 3;

        /// <summary>
        /// Shows the GitHub rate-limit notice over the wizard and offers to open the GitHub account settings.
        /// </summary>
        private readonly GitHubRateLimitNotice _gitHubLimitNotice;

        /// <summary>
        /// Gets the index of the first step of the step-by-step setup: the account step when SUNRISE is enabled,
        /// otherwise the port step.
        /// </summary>
        private static int FirstSetupStep => SunriseService.IsEnabled ? 1 : 2;

        /// <summary>
        /// Saved application settings; the wizard stores the profile, ports and first-run flag here.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Installer that discovers and installs the engines the selected modules depend on.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Installer that discovers module manifests and downloads module sources.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Runner that executes the first-run steps of a freshly installed module.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Coordinator that starts installed modules with their dependencies.
        /// </summary>
        private readonly ModuleLaunchCoordinator _moduleLaunchCoordinator;

        /// <summary>
        /// Update service that resolves install sources and applies module installs through the update pipeline.
        /// </summary>
        private readonly UpdateManager _updateManager;

        /// <summary>
        /// Service that tracks which legal documents the user still has to accept.
        /// </summary>
        private readonly LegalAcceptanceService _legalAcceptance;

        /// <summary>
        /// Localization service used to switch and re-apply the language.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// SUNRISE service that owns the local and cloud account state shown on the account step.
        /// </summary>
        private readonly SunriseService _sunriseService;

        /// <summary>
        /// Service provider used to resolve overlays, dialogs and the main shell page.
        /// </summary>
        private readonly IServiceProvider _services;

        /// <summary>
        /// Cancels the pending card presentation lookups (icons, versions) when the wizard is unloaded.
        /// </summary>
        private readonly CancellationTokenSource _modulePresentationCts = new();

        /// <summary>
        /// Task of the first module list load; shared so repeated loads and the installation reuse one discovery.
        /// </summary>
        private Task? _moduleListLoadTask;

        /// <summary>
        /// Localization key of the message shown instead of the module list, kept to re-localize it.
        /// </summary>
        private string _moduleListMessageKey = LocalizationKeys.Downloads_NoItems;

        /// <summary>
        /// Module information view shown as an overlay; created on first use.
        /// </summary>
        private ModuleInfo? _moduleInfo;

        /// <summary>
        /// Card whose information overlay is open, or null when no overlay is open.
        /// </summary>
        private ModuleCatalogCardViewModel? _selectedModuleCard;

        /// <summary>
        /// Gets the module cards offered for selection on the last step.
        /// </summary>
        public ObservableCollection<ModuleCatalogCardViewModel> ModuleCards { get; } = [];

        /// <summary>
        /// Text of the installation log; changes are guarded by <see cref="_logLock"/>.
        /// </summary>
        private readonly StringBuilder _logBuffer = new();

        /// <summary>
        /// Index of the visible wizard step; 0 is the welcome page.
        /// </summary>
        private int _currentStep;

        /// <summary>
        /// Whether the welcome page is already working on leaving itself, which blocks a second start.
        /// </summary>
        private bool _isStartingSetup;

        /// <summary>
        /// Cancellation source of the current installation run, replaced on every run.
        /// </summary>
        private CancellationTokenSource? _cts;

        /// <summary>
        /// Key that identifies the current installation run to the console view, so it resets its scroll position.
        /// </summary>
        private string _installLogSessionKey = string.Empty;

        /// <summary>
        /// Phase of the installation, which decides the navigation buttons shown.
        /// </summary>
        private InstallationState _installState;

        /// <summary>
        /// Guards <see cref="_logBuffer"/> against concurrent writers and readers.
        /// </summary>
        private readonly object _logLock = new();

        /// <summary>
        /// Set to 1 while a log flush loop is running, so only one loop exists at a time.
        /// </summary>
        private int _logFlushQueued;

        /// <summary>
        /// Set to 1 when log lines arrived since the running flush loop started its last pass.
        /// </summary>
        private int _logFlushRequested;

        /// <summary>
        /// Set to 1 while the delayed layout refresh passes of the install log are scheduled.
        /// </summary>
        private int _installLogLayoutRefreshQueued;

        /// <summary>
        /// Whether the account radio buttons are being set from code, so their change events are ignored.
        /// </summary>
        private bool _suppressAccountSelectionEvents;

        /// <summary>
        /// Whether a local-account switch or a cloud authorization is running, which locks the account controls.
        /// </summary>
        private bool _isAccountActionRunning;

        /// <summary>
        /// Cancellation source of the running cloud authorization, or null when none is running.
        /// </summary>
        private CancellationTokenSource? _accountActionCts;

        /// <summary>
        /// Downloaded byte count at the last transfer speed update.
        /// </summary>
        private long _lastDownloadedBytes;

        /// <summary>
        /// Time of the last transfer speed update.
        /// </summary>
        private DateTime _lastSpeedUpdate = DateTime.UtcNow;

        /// <summary>
        /// Most recently measured transfer speed in bytes per second.
        /// </summary>
        private double _lastSpeed;


        // Nested types

        /// <summary>
        /// Phase of the module installation shown on the last step.
        /// Preserves navigation actions when localization is reapplied during or after installation.
        /// </summary>
        private enum InstallationState
        {
            /// <summary>
            /// The installation has not been started; the module selection is shown.
            /// </summary>
            NotStarted,

            /// <summary>
            /// Engines and modules are being installed.
            /// </summary>
            Running,

            /// <summary>
            /// The installation ended with a failure or was canceled; retry and skip are offered.
            /// </summary>
            Failed,

            /// <summary>
            /// Everything was installed and started; the finish action is offered.
            /// </summary>
            Completed
        }


        // Initialization

        /// <summary>
        /// Creates the setup wizard, binds it to itself and preloads persisted defaults (language, display name,
        /// account mode, module port) into the form. The module list is loaded later, once the page is loaded.
        /// </summary>
        /// <param name="appData">Saved application settings.</param>
        /// <param name="engineInstaller">Installer for the engines the selected modules need.</param>
        /// <param name="moduleInstaller">Installer that discovers and downloads modules.</param>
        /// <param name="moduleRunner">Runner that executes first-run steps of installed modules.</param>
        /// <param name="moduleLaunchCoordinator">Coordinator that starts installed modules.</param>
        /// <param name="updateManager">Update service used to install modules through the update pipeline.</param>
        /// <param name="legalAcceptance">Service that tracks pending legal documents.</param>
        /// <param name="localization">Localization service used to switch the language.</param>
        /// <param name="sunriseService">SUNRISE service that owns the account state.</param>
        /// <param name="services">Service provider used to resolve overlays and the main shell.</param>
        public SetupWizardPage(
            AppDataStore appData,
            EngineInstaller engineInstaller,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            ModuleLaunchCoordinator moduleLaunchCoordinator,
            UpdateManager updateManager,
            LegalAcceptanceService legalAcceptance,
            AppLocalizationService localization,
            SunriseService sunriseService,
            IServiceProvider services)
        {
            // Keep the services, then build the UI. The rate-limit notice stays hidden while a legal document
            // still has to be accepted manually.
            _appData = appData;
            _engineInstaller = engineInstaller;
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
            _moduleLaunchCoordinator = moduleLaunchCoordinator;
            _updateManager = updateManager;
            _legalAcceptance = legalAcceptance;
            _localization = localization;
            _sunriseService = sunriseService;
            _services = services;

            InitializeComponent();
            Dialogs.Initialize(this, SetupSurface, _services);
            _gitHubLimitNotice = new(this, Dialogs, _services, OpenGitHubAccounts,
                () => !_legalAcceptance.ManualAcceptanceRequired);
            BindingContext = this;
            LocalizableAttach.Hook(this, _localization, this);

            // Fill the language picker and select the active language; the change handler is attached last
            // so filling the picker does not trigger a language switch.
            foreach (var language in AppLocalizationService.SupportedLanguages)
            {
                LanguagePicker.Items.Add(AppLocalizationService.GetPickerDisplayName(language.Id));
            }

            LanguagePicker.SelectedItem = AppLocalizationService.GetPickerDisplayName(_localization.GetCurrentLanguage());
            LanguagePicker.SelectedIndexChanged += OnLanguagePickerChanged;

            // Reuse the saved profile name when available, otherwise fall back to the Windows user name.
            var existingName = _appData.Data.User.Name;
            UsernameEntry.Text = string.IsNullOrWhiteSpace(existingName)
                ? Environment.UserName
                : existingName;

            // Select the saved account mode without triggering the account selection handlers.
            _suppressAccountSelectionEvents = true;
            LocalAccountRadioButton.IsChecked = _appData.Data.User.AccountMode == AppAccountMode.Local;
            CloudAccountRadioButton.IsChecked = _appData.Data.User.AccountMode == AppAccountMode.Cloud;
            _suppressAccountSelectionEvents = false;
            UpdateAccountSelectionUI();

            // Show the saved first module port, then wait for the page to be loaded.
            ModulePortEntry.Text = _appData.Data.Ports.ModulesStart.ToString();

            // Loaded is more reliable than OnAppearing for this WinUI startup flow.
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            ApplyLocalization();
        }


        // Accounts overlay and initial load

        /// <summary>
        /// Opens the accounts category of the settings above the wizard and any module information overlay.
        /// Used by the rate-limit notice to let the user connect a GitHub account.
        /// </summary>
        private void OpenGitHubAccounts()
        {
            // Keep setup and any installation progress intact underneath settings.
            var settings = _services.GetRequiredService<SettingsView>();
            settings.SelectCategory(SettingsCategoryKind.Accounts);
            settings.CloseRequested += OnClose;
            GitHubAccountsOverlay.Content = settings;
            GitHubAccountsOverlay.IsVisible = true;

            // Restores the underlying wizard when account settings are closed.
            void OnClose(object? sender, EventArgs e)
            {
                settings.CloseRequested -= OnClose;
                GitHubAccountsOverlay.IsVisible = false;
                GitHubAccountsOverlay.Content = null;
            }
        }

        /// <summary>
        /// Starts loading the module selection list when the page is loaded; later loads reuse the same task.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            // Multiple native Loaded events share discovery and never reset the user's selections.
            await (_moduleListLoadTask ??= PopulateModuleListAsync());
        }


        // Setup actions

        /// <summary>
        /// Starts the step-by-step setup flow when the Setup button is clicked.
        /// </summary>
        private async void OnSetupClicked(object? sender, EventArgs e) =>
            await StartSetupAsync(fastSetup: false);

        /// <summary>
        /// Saves default values and jumps directly to module selection when the Fast setup button is clicked.
        /// </summary>
        private async void OnFastSetupClicked(object? sender, EventArgs e) =>
            await StartSetupAsync(fastSetup: true);

        /// <summary>
        /// Leaves the welcome page only after any pending legal documents have been accepted.
        /// A fast setup first stores the default profile and ports; a normal setup goes to the first step.
        /// </summary>
        /// <param name="fastSetup">Whether to store the defaults and jump straight to the module selection.</param>
        private async Task StartSetupAsync(bool fastSetup)
        {
            // Ignore repeated clicks and calls from any step other than the welcome page.
            if (_isStartingSetup || !Step0Panel.IsEnabled || _currentStep != 0)
            {
                return;
            }

            // Lock the welcome page while the legal acceptance overlay and the optional defaults are processed.
            _isStartingSetup = true;
            Step0Panel.IsEnabled = false;
            try
            {
                // Legal acceptance comes first; the rate-limit notice may only be shown after it.
                await LegalAcceptanceOverlay.PresentIfRequiredAsync(OverlayContainer, _legalAcceptance, _services);
                _gitHubLimitNotice.Refresh();

                // Fast setup: use the local account (with SUNRISE) and the default ports without asking.
                if (fastSetup)
                {
                    if (SunriseService.IsEnabled)
                    {
                        await _sunriseService.SelectLocalAccountAsync();
                        _appData.Data.User.Name = Environment.UserName;
                        _appData.Data.User.LocalName = Environment.UserName;
                    }

                    var defaultPorts = new AppPortConfig();
                    _appData.Data.Ports.ModulesStart = defaultPorts.ModulesStart;
                    await _appData.SaveAsync();
                }

                _currentStep = fastSetup ? TotalSteps : FirstSetupStep;
                UpdateStepUI();
            }
            catch (Exception ex)
            {
                // Stay on the welcome page and tell the user why setup could not start.
                Debug.WriteLine($"[SetupWizard] Failed to start setup: {ex}");
                await DialogService.For(this).AlertAsync(this,
                    L.Get(LocalizationKeys.SetupWizard_Title),
                    ex.Message,
                    L.Get(LocalizationKeys.Common_OK));
            }
            finally
            {
                // Unlock the welcome page on every outcome.
                _isStartingSetup = false;
                Step0Panel.IsEnabled = true;
            }
        }


        // Account selection

        /// <summary>
        /// Selects the local account card when any empty area inside it is clicked, unless an account action
        /// is running.
        /// </summary>
        private void OnLocalAccountCardTapped(object? sender, TappedEventArgs e)
        {
            if (!_isAccountActionRunning)
            {
                LocalAccountRadioButton.IsChecked = true;
            }
        }

        /// <summary>
        /// Selects the cloud account card when any empty area inside it is clicked, unless an account action
        /// is running. The selection change handler then starts browser authorization.
        /// </summary>
        private void OnCloudAccountCardTapped(object? sender, TappedEventArgs e)
        {
            if (!_isAccountActionRunning)
            {
                CloudAccountRadioButton.IsChecked = true;
            }
        }

        /// <summary>
        /// Switches back to the local profile and removes credentials retained for SUNRISE when the local account
        /// option becomes selected. Ignores programmatic changes and unselecting.
        /// </summary>
        private async void OnLocalAccountCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (!SunriseService.IsEnabled || !e.Value || _suppressAccountSelectionEvents || _isAccountActionRunning)
            {
                return;
            }

            try
            {
                // Lock the account controls while the account is switched, then show the stored name again.
                _isAccountActionRunning = true;
                UpdateAccountSelectionUI();
                await _sunriseService.SelectLocalAccountAsync();
                UsernameEntry.Text = string.IsNullOrWhiteSpace(_appData.Data.User.Name)
                    ? Environment.UserName
                    : _appData.Data.User.Name;
            }
            catch (Exception ex)
            {
                // Report the failure; the finally block unlocks the controls.
                Debug.WriteLine($"[SetupWizard] Failed to select local account: {ex}");
                await DialogService.For(this).AlertAsync(this,
                    L.Get(LocalizationKeys.Settings_AslmAccount_Title),
                    ex.Message,
                    L.Get(LocalizationKeys.Common_OK));
            }
            finally
            {
                _isAccountActionRunning = false;
                UpdateAccountSelectionUI();
            }
        }

        /// <summary>
        /// Starts browser authorization as soon as the cloud account option becomes selected.
        /// Ignores programmatic changes and unselecting.
        /// </summary>
        private async void OnCloudAccountCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (!SunriseService.IsEnabled || !e.Value || _suppressAccountSelectionEvents)
            {
                return;
            }

            UpdateAccountSelectionUI();
            await AuthenticateCloudAccountAsync();
        }

        /// <summary>
        /// Handles the cloud account action button: it cancels a running authorization, selects the cloud option
        /// first when needed, and otherwise retries browser authorization.
        /// </summary>
        private async void OnCloudAccountActionClicked(object? sender, EventArgs e)
        {
            // While authorization is running the button acts as Cancel.
            if (_isAccountActionRunning)
            {
                _accountActionCts?.Cancel();
                return;
            }

            // Selecting the option starts the authorization through its change handler.
            if (!CloudAccountRadioButton.IsChecked)
            {
                CloudAccountRadioButton.IsChecked = true;
                return;
            }

            await AuthenticateCloudAccountAsync();
        }

        /// <summary>
        /// Authorizes SUNRISE in the browser and refreshes the wizard account state. Shows an alert on failure.
        /// Returns false immediately when SUNRISE is disabled or another account action is already running.
        /// </summary>
        /// <returns>True when the cloud account is authorized; false when it failed or the user canceled.</returns>
        private async Task<bool> AuthenticateCloudAccountAsync()
        {
            if (!SunriseService.IsEnabled || _isAccountActionRunning)
            {
                return false;
            }

            // Lock the account controls; the cancellation source lets the action button cancel the browser flow.
            var actionCts = new CancellationTokenSource();
            try
            {
                _isAccountActionRunning = true;
                _accountActionCts = actionCts;
                UpdateAccountSelectionUI();

                // The user may cancel while the authorization completes. In that case a cloud account that was
                // just established is dropped again, so a canceled attempt leaves the local profile active.
                var result = await _sunriseService.AuthenticateApplicationAsync(actionCts.Token);
                if (actionCts.IsCancellationRequested)
                {
                    if (result.Success && _sunriseService.IsCloudAccount)
                    {
                        await _sunriseService.SelectLocalAccountAsync();
                    }

                    return false;
                }

                // On success the display name entry shows the name of the cloud profile.
                if (result.Success)
                {
                    UsernameEntry.Text = _appData.Data.User.Name;
                    UpdateAccountSelectionUI();
                    return true;
                }

                // On failure show the reason on the card and in an alert.
                var error = string.IsNullOrWhiteSpace(result.Error)
                    ? L.Get(LocalizationKeys.SetupWizard_CloudAccountRequired)
                    : result.Error;
                CloudAccountStatusLabel.Text = L.Get(
                    LocalizationKeys.SetupWizard_CloudAccountAuthenticationFailed,
                    error);
                await DialogService.For(this).AlertAsync(this,
                    L.Get(LocalizationKeys.Settings_AslmAccount_Title),
                    CloudAccountStatusLabel.Text,
                    L.Get(LocalizationKeys.Common_OK));
                return false;
            }
            catch (OperationCanceledException) when (actionCts.IsCancellationRequested)
            {
                // The user canceled the browser authorization; nothing to report.
                return false;
            }
            catch (Exception ex)
            {
                // An unexpected failure is reported the same way as a failed authorization result.
                Debug.WriteLine($"[SetupWizard] SUNRISE authorization failed: {ex}");
                CloudAccountStatusLabel.Text = L.Get(
                    LocalizationKeys.SetupWizard_CloudAccountAuthenticationFailed,
                    ex.Message);
                await DialogService.For(this).AlertAsync(this,
                    L.Get(LocalizationKeys.Settings_AslmAccount_Title),
                    CloudAccountStatusLabel.Text,
                    L.Get(LocalizationKeys.Common_OK));
                return false;
            }
            finally
            {
                // Release the cancellation source and unlock the controls. A failure message stays visible
                // on the card unless the user canceled.
                var wasCancelled = actionCts.IsCancellationRequested;
                if (ReferenceEquals(_accountActionCts, actionCts))
                {
                    _accountActionCts = null;
                }

                actionCts.Dispose();
                _isAccountActionRunning = false;
                UpdateAccountSelectionUI(preserveFailureStatus: !wasCancelled);
            }
        }

        /// <summary>
        /// Updates account-specific panels, selected-card chrome, and authorization status.
        /// </summary>
        /// <param name="preserveFailureStatus">
        /// Whether to keep the current status text instead of replacing it with "not connected" while no cloud
        /// account is connected, so an authorization failure message stays visible.
        /// </param>
        private void UpdateAccountSelectionUI(bool preserveFailureStatus = false)
        {
            // Without SUNRISE there is no account step.
            if (!SunriseService.IsEnabled)
            {
                Step1Panel.IsVisible = false;
                return;
            }

            // Show the panel of the selected account type.
            var useCloud = CloudAccountRadioButton.IsChecked;
            LocalAccountPanel.IsVisible = !useCloud;
            CloudAccountPanel.IsVisible = useCloud;

            // Highlight the selected card, dim the other one and lock the controls during an account action.
            LocalAccountCard.SetDynamicResource(
                Border.BackgroundColorProperty,
                useCloud ? "BackgroundSecondary" : "SystemBlueOverlay");
            LocalAccountCard.SetDynamicResource(
                Border.StrokeProperty,
                useCloud ? "Separator" : "ActionBlue");
            CloudAccountCard.SetDynamicResource(
                Border.BackgroundColorProperty,
                useCloud ? "SystemBlueOverlay" : "BackgroundSecondary");
            CloudAccountCard.SetDynamicResource(
                Border.StrokeProperty,
                useCloud ? "ActionBlue" : "Separator");
            LocalAccountCard.Opacity = useCloud ? 0.72 : 1.0;
            CloudAccountCard.Opacity = useCloud ? 1.0 : 0.72;
            LocalAccountRadioButton.IsEnabled = !_isAccountActionRunning;
            CloudAccountRadioButton.IsEnabled = !_isAccountActionRunning;
            CloudAccountActionButton.IsEnabled = useCloud;
            CloudAccountActionButton.IsVisible = useCloud &&
                (_isAccountActionRunning || !_sunriseService.IsCloudAccount);

            // While authorization runs, the status shows progress and the action button becomes Cancel.
            if (_isAccountActionRunning && useCloud)
            {
                CloudAccountStatusLabel.Text = L.Get(LocalizationKeys.SetupWizard_CloudAccountConnecting);
                CloudAccountActionButton.Text = L.Get(LocalizationKeys.Common_Cancel);
                return;
            }

            // Otherwise show whether a cloud account is connected, optionally keeping an earlier failure message.
            CloudAccountActionButton.Text = L.Get(LocalizationKeys.SetupWizard_CloudAccountAction);
            if (_sunriseService.IsCloudAccount)
            {
                CloudAccountStatusLabel.Text = L.Get(
                    LocalizationKeys.SetupWizard_CloudAccountConnected,
                    GetCloudAccountDisplayName(_sunriseService.UserData.Account));
            }
            else if (!preserveFailureStatus)
            {
                CloudAccountStatusLabel.Text = L.Get(LocalizationKeys.SetupWizard_CloudAccountNotConnected);
            }
        }

        /// <summary>
        /// Resolves the best user-facing name exposed by a SUNRISE account: the ASLM username, then the account
        /// username, then the e-mail address.
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


        // Module list

        /// <summary>
        /// Builds the shared catalog cards and defaults the selection to discovered required modules.
        /// Shows a message instead of the list when no module is found or discovery fails.
        /// </summary>
        private async Task PopulateModuleListAsync()
        {
            ModuleCards.Clear();
            ModuleListMessageLabel.IsVisible = false;
            try
            {
                // Catalog discovery stays independent of repository artwork and version requests.
                var modules = await Task.Run(_moduleInstaller.DiscoverModulesAsync);
                if (_modulePresentationCts.IsCancellationRequested) return;

                // Create one selectable card per module, with the application's catalog defaults applied.
                var trust = _services.GetRequiredService<ModuleTrustService>();
                var registered = _moduleInstaller.Registry.ReadIds();
                foreach (var module in modules)
                {
                    _updateManager.ApplyCatalogDefaults(module, registered.Contains(module.Id));
                    ModuleCards.Add(new ModuleCatalogCardViewModel(module, trust.Resolve(module), _moduleInstaller,
                        showSelection: true));
                }

                // An empty catalog shows the "nothing found" message.
                _moduleListMessageKey = LocalizationKeys.Downloads_NoItems;
                ModuleListMessageLabel.Text = L.Get(_moduleListMessageKey);
                ModuleListMessageLabel.IsVisible = ModuleCards.Count == 0;

                // Display every local card immediately; cached icons and remote metadata arrive independently.
                foreach (var card in ModuleCards)
                    _ = RefreshModuleCardAsync(card);
            }
            catch (Exception ex)
            {
                // Replace the list with a "failed to load" message instead of aborting the wizard.
                Debug.WriteLine($"[SetupWizard] Failed to load module list: {ex}");
                _moduleListMessageKey = LocalizationKeys.ModulesDownloads_LoadFailed;
                ModuleListMessageLabel.Text = L.Get(_moduleListMessageKey);
                ModuleListMessageLabel.IsVisible = true;
            }
        }

        /// <summary>
        /// Refreshes the presentation of a card (icon, versions) without blocking selection or allowing failed
        /// repository lookups to abort setup.
        /// </summary>
        private async Task RefreshModuleCardAsync(ModuleCatalogCardViewModel card)
        {
            try
            {
                await card.RefreshPresentationAsync(_updateManager, _modulePresentationCts.Token);
            }
            catch (OperationCanceledException) when (_modulePresentationCts.IsCancellationRequested)
            {
                // Leaving first-run setup invalidates its pending presentation requests.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetupWizard] Failed to refresh module '{card.Module.Id}': {ex}");
            }
        }

        /// <summary>
        /// Opens the regular module information view with configuration enabled and installation actions hidden,
        /// when a module card is selected for details. Ignored once the installation has started.
        /// </summary>
        private async void OnModuleSelected(object? sender, ModuleConfig module)
        {
            if (_installState != InstallationState.NotStarted) return;

            // Create the info view once and reuse it for every module.
            if (_moduleInfo == null)
            {
                _moduleInfo = _services.GetRequiredService<ModuleInfo>();
                _moduleInfo.CloseRequested += OnModuleInfoClosed;
                ModuleInfoOverlay.Content = _moduleInfo;
            }

            // Remember the card so its configuration can be refreshed when the overlay closes.
            _selectedModuleCard = ModuleCards.FirstOrDefault(card => card.Module == module);
            ModuleInfoOverlay.IsVisible = true;
            await _moduleInfo.OpenAsync(module, ModuleInfo.Section.Info, checkForUpdates: false,
                allowInstallationActions: false);
        }

        /// <summary>
        /// Hides the module information overlay when it closes, and refreshes the card of the inspected module
        /// without resetting any checkboxes.
        /// </summary>
        private async void OnModuleInfoClosed(object? sender, EventArgs e)
        {
            ModuleInfoOverlay.IsVisible = false;
            var card = _selectedModuleCard;
            _selectedModuleCard = null;
            if (card == null) return;
            try
            {
                // ModuleInfo may edit a fresh manifest instance; copy only its saved download preferences.
                var saved = await _moduleInstaller.LoadModuleConfig(card.Module.SourcePath);
                if (saved == null || _modulePresentationCts.IsCancellationRequested) return;
                card.Module.Update = saved.Update;
                _updateManager.ApplyCatalogDefaults(card.Module, _moduleInstaller.Registry.ReadIds().Contains(card.Module.Id));
                await RefreshModuleCardAsync(card);
            }
            catch (Exception ex)
            {
                // The card keeps its previous presentation; a refresh failure must not block the wizard.
                Debug.WriteLine($"[SetupWizard] Failed to refresh module configuration: {ex}");
            }
        }

        /// <summary>
        /// Stops view-owned presentation lookups when the wizard is removed from the window.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            _modulePresentationCts.Cancel();
        }


        // Navigation

        /// <summary>
        /// Moves one step back in the wizard, but never before the first step of the step-by-step setup.
        /// </summary>
        private void OnBackClicked(object? sender, EventArgs e)
        {
            if (_currentStep <= FirstSetupStep)
            {
                return;
            }

            _currentStep--;
            UpdateStepUI();
        }

        /// <summary>
        /// Validates the current step and advances the wizard; on the last step it starts the installation.
        /// Ignored while an account action is running.
        /// </summary>
        private async void OnNextClicked(object? sender, EventArgs e)
        {
            if (_isAccountActionRunning)
            {
                return;
            }

            if (_currentStep < TotalSteps)
            {
                // Account step: a cloud account must be connected (authorizing it if needed), a local account
                // needs a valid display name, which is written back normalized.
                if (_currentStep == 1)
                {
                    if (CloudAccountRadioButton.IsChecked)
                    {
                        if (!_sunriseService.IsCloudAccount && !await AuthenticateCloudAccountAsync())
                        {
                            return;
                        }
                    }
                    else if (!SettingsService.TryValidateDisplayName(UsernameEntry.Text, out var validatedUserName, out var displayNameErrorMessage))
                    {
                        await DialogService.For(this).AlertAsync(this,
                            DialogText.Localized(LocalizationKeys.Settings_ValidationError), displayNameErrorMessage);
                        return;
                    }
                    else
                    {
                        UsernameEntry.Text = validatedUserName;
                    }
                }

                // Port step: the module port draft must be valid before moving on.
                if (_currentStep == 2 && !ValidatePorts())
                {
                    return;
                }

                _currentStep++;
                UpdateStepUI();
                return;
            }

            // The last step installs the selected modules.
            await StartInstallAsync();
        }


        // Localization

        /// <summary>
        /// Applies and persists the welcome-page language override without completing first-run setup.
        /// If saving fails, the previous language is restored and the error is shown.
        /// </summary>
        private async void OnLanguagePickerChanged(object? sender, EventArgs e)
        {
            // Ignore an empty selection and a selection of the language that is already active.
            var selectedIndex = LanguagePicker.SelectedIndex;
            if (selectedIndex < 0 || selectedIndex >= AppLocalizationService.SupportedLanguages.Count)
            {
                return;
            }

            var language = AppLocalizationService.SupportedLanguages[selectedIndex].Id;
            var previousLanguage = _localization.GetCurrentLanguage();
            if (string.Equals(language, previousLanguage, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Apply the new culture first so the page switches at once, then persist it.
            Step0Panel.IsEnabled = false;
            try
            {
                _appData.Data.Personalization.Language = language;
                _localization.ApplyCulture();
                await _appData.SaveAsync();
            }
            catch (Exception ex)
            {
                // Roll back to the previous language, including the picker selection, and report the failure.
                Debug.WriteLine($"[SetupWizard] Failed to save language: {ex}");
                _appData.Data.Personalization.Language = previousLanguage;
                LanguagePicker.SelectedItem = AppLocalizationService.GetPickerDisplayName(previousLanguage);
                _localization.ApplyCulture();
                await DialogService.For(this).AlertAsync(this,
                    L.Get(LocalizationKeys.SetupWizard_SelectLanguage),
                    ex.Message,
                    L.Get(LocalizationKeys.Common_OK));
            }
            finally
            {
                Step0Panel.IsEnabled = true;
            }
        }

        /// <summary>
        /// Applies localized strings to wizard labels, module cards and messages, then refreshes the account
        /// and step UI. The installation status text is only replaced before the installation has started.
        /// </summary>
        public void ApplyLocalization()
        {
            // Static texts of the page and its steps.
            Title = L.Get(LocalizationKeys.SetupWizard_Title);
            HeaderTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_Title);
            WelcomeTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_WelcomeTitle);
            WelcomeSubtitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_WelcomeSubtitle);
            LanguageLabel.Text = L.Get(LocalizationKeys.SetupWizard_SelectLanguage);
            SetupButton.Text = L.Get(LocalizationKeys.SetupWizard_Setup);
            FastSetupButton.Text = L.Get(LocalizationKeys.SetupWizard_FastSetup);
            FastSetupHintLabel.Text = L.Get(LocalizationKeys.SetupWizard_FastSetupHint);
            AccountTypeTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_AccountTypeTitle);
            LocalAccountRadioButton.Content = L.Get(LocalizationKeys.SetupWizard_LocalAccount);
            LocalAccountDescriptionLabel.Text = L.Get(LocalizationKeys.SetupWizard_LocalAccountDescription);
            CloudAccountRadioButton.Content = L.Get(LocalizationKeys.SetupWizard_CloudAccount);
            CloudAccountDescriptionLabel.Text = L.Get(LocalizationKeys.SetupWizard_CloudAccountDescription);
            DisplayNameTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_DisplayNameTitle);
            UsernameEntry.Placeholder = L.Get(LocalizationKeys.SetupWizard_DisplayNamePlaceholder);
            PortAllocationTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_PortAllocationTitle);
            ModulePortLabel.Text = L.Get(LocalizationKeys.SetupWizard_ModulesPortLabel);

            // Install status and module list texts; a running or finished install keeps its own status text.
            if (_installState == InstallationState.NotStarted)
                InstallStatusLabel.Text = L.Get(LocalizationKeys.SetupWizard_Preparing);
            OverallProgressLabel.Text = L.Get(LocalizationKeys.SetupWizard_OverallProgress);
            foreach (var card in ModuleCards) card.RefreshLocalization();
            if (ModuleListMessageLabel.IsVisible)
                ModuleListMessageLabel.Text = L.Get(_moduleListMessageKey);

            // Texts built from state (account status, step label, buttons).
            UpdateAccountSelectionUI();
            UpdateStepUI();
        }


        // Step UI

        /// <summary>
        /// Switches visible panels, navigation buttons and the step label for the current wizard step and
        /// installation state.
        /// </summary>
        private void UpdateStepUI()
        {
            // Show only the panel of the current step; the header and buttons appear after the welcome page.
            Step0Panel.IsVisible = _currentStep == 0;
            Step1Panel.IsVisible = SunriseService.IsEnabled && _currentStep == 1;
            Step2Panel.IsVisible = _currentStep == 2;
            Step3Panel.IsVisible = _currentStep == 3;

            HeaderRow.IsVisible = _currentStep > 0;
            ButtonPanel.IsVisible = _currentStep > 0 && _installState != InstallationState.Running;
            BackButton.IsVisible = _currentStep > FirstSetupStep;

            // Default Back/Next buttons, then the finish or retry/skip variant when the installation has ended.
            NextButton.Text = _currentStep == TotalSteps
                ? L.Get(LocalizationKeys.SetupWizard_Next_Install)
                : L.Get(LocalizationKeys.Common_Next);
            ResetNavigationButtons();
            if (_installState == InstallationState.Completed) ConfigureFinishButton();
            else if (_installState == InstallationState.Failed) ConfigureRetryAndSkipButtons();

            // The step label counts only the steps that are shown: without SUNRISE the account step is skipped.
            var visibleStepCount = TotalSteps - FirstSetupStep + 1;
            var visibleStep = _currentStep - FirstSetupStep + 1;
            StepLabel.Text = _currentStep switch
            {
                1 => L.Get(LocalizationKeys.SetupWizard_StepFormat, visibleStep, visibleStepCount, L.Get(LocalizationKeys.Settings_Accounts_SectionAslm)),
                2 => L.Get(LocalizationKeys.SetupWizard_StepFormat, visibleStep, visibleStepCount, L.Get(LocalizationKeys.SetupWizard_Step_PortConfiguration)),
                3 => L.Get(LocalizationKeys.SetupWizard_StepFormat, visibleStep, visibleStepCount, L.Get(LocalizationKeys.SetupWizard_Step_ModuleSelection)),
                _ => string.Empty
            };

            // Once the installation has started the label shows its progress instead of the step name.
            if (_installState != InstallationState.NotStarted)
            {
                StepLabel.Text = L.Get(_installState switch
                {
                    InstallationState.Running => LocalizationKeys.SetupWizard_Installing,
                    InstallationState.Completed => LocalizationKeys.SetupWizard_SetupComplete,
                    _ => LocalizationKeys.SetupWizard_Failed
                });
            }
        }


        // Port validation

        /// <summary>
        /// Validates the module start port draft and shows the error when it is invalid.
        /// </summary>
        /// <returns>True when the entered port is valid.</returns>
        private bool ValidatePorts()
        {
            PortErrorLabel.IsVisible = false;
            var portResult = SettingsService.TryParsePortStart(
                ModulePortEntry.Text?.Trim() ?? string.Empty);
            if (!portResult.Success)
            {
                ShowPortError(portResult.ErrorMessage);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Shows the port validation error below the port entry.
        /// </summary>
        private void ShowPortError(string message)
        {
            PortErrorLabel.Text = message;
            PortErrorLabel.IsVisible = true;
        }


        // Install log (bound console host)

        /// <summary>
        /// Raised when an install-log bindable property changes.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Gets the installation log text rendered by <see cref="ConsoleOutputView"/>.
        /// </summary>
        public string InstallLogText
        {
            get
            {
                lock (_logLock)
                {
                    return _logBuffer.ToString();
                }
            }
        }

        /// <summary>
        /// Gets the session key used to reset console scroll position for a new install run.
        /// </summary>
        public string InstallLogSessionKey => _installLogSessionKey;

        /// <summary>
        /// Schedules layout refresh passes so the native console host measures like <see cref="ConsolesView"/>.
        /// Refreshes at once, then again 16 ms and 48 ms later, unless those delayed passes are already scheduled.
        /// </summary>
        private void QueueInstallLogLayoutRefresh()
        {
            RefreshInstallLogLayout();

            // Only one set of delayed passes may be pending at a time.
            if (Dispatcher == null || Interlocked.Exchange(ref _installLogLayoutRefreshQueued, 1) == 1)
            {
                return;
            }

            // Two delayed passes; the last one allows scheduling again.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), RefreshInstallLogLayout);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(48), () =>
            {
                RefreshInstallLogLayout();
                Interlocked.Exchange(ref _installLogLayoutRefreshQueued, 0);
            });
        }

        /// <summary>
        /// Invalidates the install panel and console host after text changes.
        /// </summary>
        private void RefreshInstallLogLayout()
        {
            InstallPanel.InvalidateMeasure();
            InstallLogConsole.InvalidateMeasure();
        }


        // Installation

        /// <summary>
        /// Saves the wizard data and installs the selected module stack. Does nothing while an installation is
        /// already running, and turns an unexpected failure into a log entry with retry and skip actions.
        /// </summary>
        private async Task StartInstallAsync()
        {
            // Mark the installation as running first, so a second start is rejected.
            if (_installState == InstallationState.Running) return;
            _installState = InstallationState.Running;
            ButtonPanel.IsVisible = false;
            try
            {
                await InstallSelectedModulesAsync();
            }
            catch (Exception ex)
            {
                // Early persistence/discovery failures still need a visible log and a retry action.
                ModuleSelectionPanel.IsVisible = false;
                InstallPanel.IsVisible = true;
                var message = L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message);
                AddLog(message);
                UpdateInstallStatus(message);
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Failed);
                ConfigureRetryAndSkipButtons();
                ButtonPanel.IsVisible = true;
            }
        }

        /// <summary>
        /// Persists setup choices and installs their dependency graph using saved module download preferences.
        /// Required engines are installed first, then every module in dependency order is installed and started.
        /// The outcome is reflected in the log, the status texts and the navigation buttons: finish after complete
        /// success, retry and skip after a failure or cancellation.
        /// </summary>
        private async Task InstallSelectedModulesAsync()
        {
            // Make sure the cards exist, so the selection below reflects the user's choices.
            await (_moduleListLoadTask ??= PopulateModuleListAsync());

            // Persist the profile and port values before any installation begins.
            if (SunriseService.IsEnabled && _appData.Data.User.AccountMode == AppAccountMode.Local &&
                SettingsService.TryValidateDisplayName(UsernameEntry.Text, out var validatedUserName, out _))
            {
                _appData.Data.User.Name = validatedUserName;
                _appData.Data.User.LocalName = validatedUserName;
            }

            var portResult = SettingsService.TryParsePortStart(
                ModulePortEntry.Text?.Trim() ?? string.Empty);
            if (portResult.Success)
            {
                _appData.Data.Ports.ModulesStart = portResult.ModulesStart;
            }

            await _appData.SaveAsync();

            // Remember which supported modules the user ticked.
            var selectedIds = ModuleCards
                .Where(card => card.IsSelected && card.Module.IsSupportedOnCurrentPlatform)
                .Select(card => card.Module.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Switch the final wizard step from selection mode into install mode.
            ButtonPanel.IsVisible = false;
            ModuleSelectionPanel.IsVisible = false;
            InstallPanel.IsVisible = true;
            StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Installing);

            // Start a fresh log; the new session key makes the console view reset its scroll position.
            lock (_logLock)
            {
                _logBuffer.Clear();
            }

            _installLogSessionKey = $"setup-install-{DateTime.UtcNow.Ticks}";
            OnPropertyChanged(nameof(InstallLogSessionKey));
            OnPropertyChanged(nameof(InstallLogText));
            QueueInstallLogLayoutRefresh();

            // Each run gets its own cancellation source, and installer output goes straight into the log.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var logProgress = new InlineProgress<string>(AddLog);

            // Resolve the modules to install: the selection plus required modules, expanded with their dependencies.
            List<ModuleConfig> catalog;
            IReadOnlyList<ModuleConfig> installModules;
            try
            {
                catalog = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync(), _cts.Token);

                // Required choices do not depend on the checkbox's enabled state or list-loading timing.
                // Entries without a discovered manifest are intentionally skipped.
                var selectedModules = catalog.Where(module => ModuleRegistry.IsRequired(module.Id) ||
                    (module.IsSupportedOnCurrentPlatform && selectedIds.Contains(module.Id))).ToList();
                installModules = ModuleDependencyResolver.ExpandInstallOrder(selectedModules, catalog);
            }
            catch (Exception ex)
            {
                // Without a catalog nothing can be installed: offer retry and skip.
                AddLog($"Failed to load module catalog: {ex.Message}");
                ButtonPanel.IsVisible = true;
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Failed);
                ConfigureRetryAndSkipButtons();
                return;
            }

            // Count the work for the overall progress bar: one step per required engine plus the steps of
            // every module. The counters are captured by the progress callback below.
            var totalSteps = 0;
            var completedSteps = 0;
            var hasFailures = false;

            // Engines that the modules declare as dependencies, each counted once.
            var requiredEngineIds = installModules
                .SelectMany(module => module.Dependencies.Engines)
                .Select(engine => engine.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Rediscover the engines from disk so installed state is current, then total up the steps.
            _engineInstaller.InvalidateCache();
            var allEngines = await Task.Run(() => _engineInstaller.DiscoverEngines(), _cts.Token);
            totalSteps += requiredEngineIds.Count;
            totalSteps += installModules.Sum(GetModuleInstallStepCount);

            // Reset rolling download metrics before the first transfer begins.
            ResetDownloadMetrics();

            // Progress callback shared by all downloads: it updates the per-file bar, the overall bar and the
            // "size / size - speed" detail text. A download counts for at most 90% of its step in the overall bar.
            var downloadProgress = new Progress<DownloadProgress>(progress =>
            {
                if (progress.TotalBytes <= 0)
                {
                    return;
                }

                // Combine the current file progress with the completed step count for the overall bar.
                var fileFraction = (double)progress.DownloadedBytes / progress.TotalBytes;
                var overallFraction = (completedSteps + fileFraction * 0.9) / totalSteps;

                // Refresh the transfer speed at a steady interval to avoid noisy UI updates.
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastSpeedUpdate).TotalSeconds;
                if (elapsed >= 0.5)
                {
                    _lastSpeed = (progress.DownloadedBytes - _lastDownloadedBytes) / elapsed;
                    _lastDownloadedBytes = progress.DownloadedBytes;
                    _lastSpeedUpdate = now;
                }

                var detail = $"{FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes)}";
                if (_lastSpeed > 0)
                {
                    detail += $" - {FormatBytes((long)_lastSpeed)}/s";
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    InstallProgress.Progress = overallFraction;
                    FileProgress.Progress = fileFraction;
                    DownloadDetailLabel.Text = detail;
                });
            });

            try
            {
                // Refuse the whole installation before any download when a module cannot run on this platform.
                var unsupportedModule = installModules.FirstOrDefault(module => !module.IsSupportedOnCurrentPlatform);
                if (unsupportedModule != null)
                {
                    throw new PlatformNotSupportedException(
                        $"Module '{unsupportedModule.Name}' does not support {PlatformInfo.PlatformKey}.");
                }

                // Install each required engine before module downloads begin.
                foreach (var engineId in requiredEngineIds)
                {
                    // A missing engine definition counts as a failure, but the remaining items are still processed.
                    var engine = allEngines.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, engineId, StringComparison.OrdinalIgnoreCase));
                    if (engine == null)
                    {
                        AddLog($"[Missing] Engine '{engineId}' not found.");
                        hasFailures = true;
                        completedSteps++;
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

                    // An engine that is already installed needs no work.
                    if (engine.Status.Installed)
                    {
                        AddLog($"[OK] Engine '{engine.Name}' already installed.");
                        completedSteps++;
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

                    // Install the engine; an installer exception ends the whole run through the catch blocks below.
                    UpdateInstallStatus($"{engine.Name}: {L.Get(LocalizationKeys.SetupWizard_Installing)}");
                    ResetFileProgress();
                    await Task.Run(
                        () => _engineInstaller.InstallAsync(engine, logProgress, downloadProgress, _cts.Token),
                        _cts.Token);
                    completedSteps++;
                    UpdateOverallProgress(completedSteps, totalSteps);
                    ResetFileProgress();
                }

                // Install each selected module (and declared module dependencies) either through the update-aware
                // pipeline or the legacy download flow.
                foreach (var module in installModules)
                {
                    // A module that was already set up is only started again.
                    if (module.Status.FirstRunCompleted)
                    {
                        AddLog($"[OK] {module.Name} already set up.");
                        completedSteps += GetModuleInstallStepCount(module);
                        UpdateOverallProgress(completedSteps, totalSteps);
                        hasFailures |= !await LaunchInstalledModuleAsync(module, logProgress, _cts.Token);
                        continue;
                    }

                    // Update-aware path: the updater installs the module in one step, then it is started.
                    if (ShouldUseConfiguredUpdateInstall(module))
                    {
                        UpdateInstallStatus($"{module.Name}: {L.Get(LocalizationKeys.SetupWizard_Installing)}");
                        ResetFileProgress();
                        ResetDownloadMetrics();

                        var updateInstalled = await InstallModuleFromUpdateConfigAsync(
                            module,
                            logProgress,
                            downloadProgress,
                            _cts.Token);

                        completedSteps += GetModuleInstallStepCount(module);
                        UpdateOverallProgress(completedSteps, totalSteps);
                        ResetFileProgress();

                        AddLog(updateInstalled
                            ? $"[OK] {module.Name} installed successfully"
                            : $"[Error] Installation failed for {module.Name}");
                        hasFailures |= !updateInstalled;
                        if (updateInstalled)
                            hasFailures |= !await LaunchInstalledModuleAsync(module, logProgress, _cts.Token);
                        continue;
                    }

                    // Legacy path, step 1 of 2: download the module source.
                    UpdateInstallStatus($"{module.Name}: {L.Get(LocalizationKeys.Settings_Downloading)}");
                    ResetFileProgress();
                    ResetDownloadMetrics();

                    var downloaded = await Task.Run(
                        () => _moduleInstaller.DownloadSourceAsync(
                            module,
                            logProgress,
                            downloadProgress,
                            _cts.Token),
                        _cts.Token);
                    completedSteps++;
                    UpdateOverallProgress(completedSteps, totalSteps);
                    ResetFileProgress();

                    // A failed download skips the setup step too, so the progress still reaches 100%.
                    if (!downloaded)
                    {
                        AddLog($"[Error] Source download failed for {module.Name}");
                        hasFailures = true;
                        completedSteps++;
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

                    // Reload the downloaded manifest, then reconcile the engines it declares and depends on.
                    var installedModule = await _moduleInstaller.LoadModuleConfig(module.SourcePath)
                        ?? throw new InvalidOperationException(
                            $"Installed manifest for '{module.Name}' could not be loaded.");
                    if (!installedModule.IsSupportedOnCurrentPlatform)
                    {
                        throw new PlatformNotSupportedException(
                            $"Module '{installedModule.Name}' does not support {PlatformInfo.PlatformKey}.");
                    }

                    await _moduleInstaller.ReconcileRequiredEnginesAsync(
                        installedModule,
                        logProgress,
                        downloadProgress,
                        _cts.Token);

                    // Legacy path, step 2 of 2: run the module's first-run setup and save its config on success.
                    UpdateInstallStatus($"{installedModule.Name}: {L.Get(LocalizationKeys.SetupWizard_Installing)}");
                    var success = await Task.Run(
                        () => _moduleRunner.ExecuteFirstRunAsync(installedModule, logProgress, _cts.Token),
                        _cts.Token);
                    if (success)
                    {
                        await _moduleInstaller.SaveConfigAsync(installedModule);
                    }

                    completedSteps++;
                    UpdateOverallProgress(completedSteps, totalSteps);

                    // Log the result and start the module only if its setup succeeded.
                    AddLog(success
                        ? $"[OK] {module.Name} installed successfully"
                        : $"[Error] Setup failed for {module.Name}");
                    hasFailures |= !success;
                    if (success)
                        hasFailures |= !await LaunchInstalledModuleAsync(installedModule, logProgress, _cts.Token);
                }

                // Show the final status of the whole run.
                var completionStatus = L.Get(hasFailures
                    ? LocalizationKeys.SetupWizard_Failed : LocalizationKeys.SetupWizard_SetupComplete);
                UpdateInstallStatus(completionStatus);
                StepLabel.Text = completionStatus;
            }
            catch (OperationCanceledException)
            {
                // Cancellation counts as a failed run, so retry and skip are offered.
                hasFailures = true;
                UpdateInstallStatus(L.Get(LocalizationKeys.SetupWizard_Canceled));
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Canceled);
            }
            catch (Exception ex)
            {
                // An installer failure ends the run; the details go to the log.
                hasFailures = true;
                UpdateInstallStatus(L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Failed);
                AddLog($"Error: {ex.Message}");
            }

            // Offer the buttons that match the outcome: finish after success, retry and skip otherwise.
            ButtonPanel.IsVisible = true;

            if (!hasFailures)
            {
                ConfigureFinishButton();
            }
            else
            {
                ConfigureRetryAndSkipButtons();
            }
        }


        // Install button state

        /// <summary>
        /// Restores the default back and next button handlers, colors and Back caption, removing the retry,
        /// skip and finish handlers.
        /// </summary>
        private void ResetNavigationButtons()
        {
            NextButton.Clicked -= OnRetryInstallClicked;
            NextButton.Clicked -= OnFinishClicked;
            NextButton.Clicked -= OnNextClicked;
            NextButton.Clicked += OnNextClicked;

            BackButton.Clicked -= OnSkipClicked;
            BackButton.Clicked -= OnBackClicked;
            BackButton.Clicked += OnBackClicked;

            NextButton.BackgroundColor = GetColorResource("ActionBlue", Color.FromArgb("#007AFF"));
            BackButton.BackgroundColor = GetColorResource("BackgroundTertiary", Color.FromArgb("#3A3A3C"));
            BackButton.Text = L.Get(LocalizationKeys.Common_Back);
        }

        /// <summary>
        /// Marks the installation as completed and converts the main action button into the finish action;
        /// the back button is hidden.
        /// </summary>
        private void ConfigureFinishButton()
        {
            _installState = InstallationState.Completed;
            ResetNavigationButtons();
            BackButton.IsVisible = false;
            NextButton.Text = L.Get(LocalizationKeys.SetupWizard_Finish);
            NextButton.Clicked -= OnNextClicked;
            NextButton.Clicked += OnFinishClicked;
        }

        /// <summary>
        /// Marks the installation as failed and converts the buttons into retry (next) and skip (back) actions.
        /// </summary>
        private void ConfigureRetryAndSkipButtons()
        {
            _installState = InstallationState.Failed;
            ResetNavigationButtons();

            NextButton.Text = L.Get(LocalizationKeys.SetupWizard_Retry);
            NextButton.Clicked -= OnNextClicked;
            NextButton.Clicked += OnRetryInstallClicked;

            BackButton.Text = L.Get(LocalizationKeys.SetupWizard_Skip);
            BackButton.IsVisible = true;
            BackButton.Clicked -= OnBackClicked;
            BackButton.Clicked += OnSkipClicked;
        }


        // Install completion actions

        /// <summary>
        /// Hides the buttons and runs the installation again after a failure.
        /// </summary>
        private async void OnRetryInstallClicked(object? sender, EventArgs e)
        {
            ButtonPanel.IsVisible = false;
            await StartInstallAsync();
        }

        /// <summary>
        /// Completes the wizard and opens the main application shell when the finish button is clicked.
        /// </summary>
        private async void OnFinishClicked(object? sender, EventArgs e)
        {
            await FinishSetupAsync();
        }

        /// <summary>
        /// Completes the wizard even when installation had failures, when the skip button is clicked.
        /// </summary>
        private async void OnSkipClicked(object? sender, EventArgs e)
        {
            await FinishSetupAsync();
        }


        // Progress UI

        /// <summary>
        /// Updates the current installation status text on the main thread.
        /// </summary>
        private void UpdateInstallStatus(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                InstallStatusLabel.Text = message);
        }

        /// <summary>
        /// Updates the overall installation progress bar on the main thread; ignored when there are no steps.
        /// </summary>
        private void UpdateOverallProgress(int completed, int total)
        {
            if (total <= 0)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
                InstallProgress.Progress = (double)completed / total);
        }

        /// <summary>
        /// Clears the per-file progress bar and detail text on the main thread before the next download.
        /// </summary>
        private void ResetFileProgress()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                FileProgress.Progress = 0;
                DownloadDetailLabel.Text = string.Empty;
            });
        }

        /// <summary>
        /// Resets the rolling metrics used to calculate the download speed label.
        /// </summary>
        private void ResetDownloadMetrics()
        {
            _lastDownloadedBytes = 0;
            _lastSpeedUpdate = DateTime.UtcNow;
            _lastSpeed = 0;
        }

        /// <summary>
        /// Returns whether the module should be installed through the update-aware pipeline, which holds when
        /// its source is a valid GitHub repository.
        /// </summary>
        private static bool ShouldUseConfiguredUpdateInstall(ModuleConfig module)
        {
            return UpdateManager.CanDownloadModule(module);
        }

        /// <summary>
        /// Returns how many overall progress steps one module consumes during setup: one for the update-aware
        /// install, two (download and first-run setup) for the legacy flow.
        /// </summary>
        private static int GetModuleInstallStepCount(ModuleConfig module)
        {
            return ShouldUseConfiguredUpdateInstall(module) ? 1 : 2;
        }

        /// <summary>
        /// Uses the same launch pipeline as catalog installation, including dependency startup and retry safety.
        /// </summary>
        /// <param name="module">The installed module to start.</param>
        /// <param name="log">Receives the launch log messages.</param>
        /// <param name="ct">Cancels the launch; cancellation propagates as an exception.</param>
        /// <returns>True when the module is running or has nothing to run; false when the launch failed.</returns>
        private async Task<bool> LaunchInstalledModuleAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            UpdateInstallStatus($"{module.Name}: {L.Get(LocalizationKeys.Modules_Starting)}");
            var launch = await Task.Run(() => _moduleLaunchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                module.SourcePath, log, ct), ct);

            // The coordinator returns Error for cancellation; preserve the wizard's canceled state.
            ct.ThrowIfCancellationRequested();
            if (launch.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning or ModuleLaunchStatus.NoRunCommands)
                return true;

            // Keep the completed installation so Retry only needs to attempt startup again.
            log.Report(L.Get(LocalizationKeys.ModuleInfo_Failed, $"{module.Name}: {launch.Message ?? launch.Status.ToString()}"));
            return false;
        }

        /// <summary>
        /// Installs one module through the updater so setup respects the manifest update configuration.
        /// </summary>
        /// <param name="module">The module to install.</param>
        /// <param name="logProgress">Receives the installer log messages.</param>
        /// <param name="downloadProgress">Receives download progress for the progress bars.</param>
        /// <param name="ct">Cancels the installation.</param>
        /// <returns>
        /// True when the module was installed; false when no install source could be resolved or the install failed.
        /// </returns>
        private async Task<bool> InstallModuleFromUpdateConfigAsync(
            ModuleConfig module,
            IProgress<string> logProgress,
            IProgress<DownloadProgress> downloadProgress,
            CancellationToken ct)
        {
            // Apply application defaults before the common resolver falls back to available repository channels.
            _updateManager.ApplyCatalogDefaults(module, _moduleInstaller.Registry.ReadIds().Contains(module.Id));

            // Setup should resolve the same branch or release candidate the normal updater would use.
            var candidate = await Task.Run(
                () => _updateManager.ResolveModuleInstallCandidateAsync(module, ct, isManualRequest: true),
                ct);
            if (candidate == null)
            {
                logProgress.Report($"[Error] Could not resolve install source for {module.Name}");
                return false;
            }

            // Reuse the full module update pipeline so preserve rules and first-run behavior stay consistent.
            return await Task.Run(
                () => _updateManager.ApplyModuleUpdateAsync(
                    candidate,
                    logProgress,
                    downloadProgress,
                    isManualRequest: true,
                    ct: ct),
                ct);
        }


        // Byte formatting

        /// <summary>
        /// Formats byte counts into readable size labels.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824)
            {
                return $"{bytes / 1_073_741_824.0:F1} GB";
            }

            if (bytes >= 1_048_576)
            {
                return $"{bytes / 1_048_576.0:F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }

            return $"{bytes} B";
        }


        // Finish flow

        /// <summary>
        /// Marks the first run as completed, saves it and replaces the wizard with the main shell page.
        /// A failure is shown in an alert instead of being thrown.
        /// </summary>
        private async Task FinishSetupAsync()
        {
            // Capture the window first: once its page is replaced this page has no window, but a late failure
            // is still shown over whatever page the window then holds.
            var window = Window;
            try
            {
                // Persist the completed first run, then swap the window content for the main shell.
                _appData.Data.FirstRunCompleted = true;
                await _appData.SaveAsync();

                if (window != null)
                {
                    var mainPage = _services.GetRequiredService<AppShellPage>();
                    window.Page = mainPage;
                    _localization.SyncFlowDirection();
                }
            }
            catch (Exception ex)
            {
                // Show the error over the main shell if it is already in place, otherwise over the wizard.
                Debug.WriteLine($"[SetupWizard] FinishSetupAsync error: {ex}");
                var host = window?.Page ?? this;
                await DialogService.For(host).AlertAsync(host,
                    DialogText.Localized(LocalizationKeys.SetupWizard_Title),
                    DialogText.Localized(LocalizationKeys.SetupWizard_FinishFailedFormat, ex.Message));
            }
        }


        // Logging

        /// <summary>
        /// Appends one log line; batched flushes push updates through install-log bindings.
        /// May be called from any thread. Starts a flush loop unless one is already running.
        /// </summary>
        private void AddLog(string message)
        {
            lock (_logLock)
            {
                _logBuffer.AppendLine(message);
            }

            // Tell a running flush loop that more lines arrived; only start a new loop when none is running.
            Interlocked.Exchange(ref _logFlushRequested, 1);
            if (Interlocked.Exchange(ref _logFlushQueued, 1) == 1)
            {
                return;
            }

            _ = FlushLogAsync();
        }

        /// <summary>
        /// Flushes buffered log lines at a steady cadence while installers keep streaming output.
        /// Raises the install-log change notification at most once per 75 ms pass and repeats while new
        /// lines keep arriving.
        /// </summary>
        private async Task FlushLogAsync()
        {
            try
            {
                // Each pass clears the request flag before waiting, so lines added meanwhile trigger another pass.
                do
                {
                    Interlocked.Exchange(ref _logFlushRequested, 0);
                    await Task.Delay(75);

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        OnPropertyChanged(nameof(InstallLogText));
                        QueueInstallLogLayoutRefresh();
                    });
                }
                while (Interlocked.CompareExchange(ref _logFlushRequested, 0, 0) == 1);
            }
            finally
            {
                // Release the loop flag; a line that arrived in the gap after the last check restarts the loop,
                // unless another caller already started one.
                Interlocked.Exchange(ref _logFlushQueued, 0);
                if (Interlocked.CompareExchange(ref _logFlushRequested, 0, 0) == 1 &&
                    Interlocked.Exchange(ref _logFlushQueued, 1) == 0)
                {
                    _ = FlushLogAsync();
                }
            }
        }


        // Helpers

        /// <summary>
        /// Raises a bindable install-log property change notification.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        /// <summary>
        /// Finds a named color resource with a defensive fallback when the key is absent or is not a color.
        /// </summary>
        private static Color GetColorResource(string key, Color fallback) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color c
                ? c
                : fallback;
    }
}
