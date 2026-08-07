// Copyright NEXTGGTECH. Apache License 2.0.

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
        private const int TotalSteps = 3;

        private readonly AppDataStore _appData;
        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleRunner _moduleRunner;
        private readonly UpdateManager _updateManager;
        private readonly LegalAcceptanceService _legalAcceptance;
        private readonly AppLocalizationService _localization;
        private readonly SunriseService _sunriseService;
        private readonly IServiceProvider _services;

        private readonly List<(ModuleConfig Module, CheckBox Check)> _moduleChecks = [];
        private readonly StringBuilder _logBuffer = new();

        private int _currentStep;
        private CancellationTokenSource? _cts;
        private bool _logVisible;
        private string _installLogSessionKey = string.Empty;
        private bool _moduleListLoaded;
        private readonly object _logLock = new();
        private int _logFlushQueued;
        private int _logFlushRequested;
        private int _installLogLayoutRefreshQueued;
        private bool _suppressAccountSelectionEvents;
        private bool _isAccountActionRunning;
        private CancellationTokenSource? _accountActionCts;

        private long _lastDownloadedBytes;
        private DateTime _lastSpeedUpdate = DateTime.UtcNow;
        private double _lastSpeed;

        // Initialization

        /// <summary>
        /// Creates the setup wizard and preloads persisted defaults into the form.
        /// </summary>
        public SetupWizardPage(
            AppDataStore appData,
            EngineInstaller engineInstaller,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            UpdateManager updateManager,
            LegalAcceptanceService legalAcceptance,
            AppLocalizationService localization,
            SunriseService sunriseService,
            IServiceProvider services)
        {
            _appData = appData;
            _engineInstaller = engineInstaller;
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
            _updateManager = updateManager;
            _legalAcceptance = legalAcceptance;
            _localization = localization;
            _sunriseService = sunriseService;
            _services = services;

            InitializeComponent();
            BindingContext = this;
            LocalizableAttach.Hook(this, _localization, this);

            // Reuse the saved profile name when available, otherwise fall back to the Windows user name.
            var existingName = _appData.Data.User.Name;
            UsernameEntry.Text = string.IsNullOrWhiteSpace(existingName)
                ? Environment.UserName
                : existingName;

            _suppressAccountSelectionEvents = true;
            LocalAccountRadioButton.IsChecked = _appData.Data.User.AccountMode == AppAccountMode.Local;
            CloudAccountRadioButton.IsChecked = _appData.Data.User.AccountMode == AppAccountMode.Cloud;
            _suppressAccountSelectionEvents = false;
            UpdateAccountSelectionUI();

            ModulePortEntry.Text = _appData.Data.Ports.ModulesStart.ToString();

            // Loaded is more reliable than OnAppearing for this WinUI startup flow.
            Loaded += OnLoaded;
        }


        // Initial load

        /// <summary>
        /// Populates the module selection list once after the page is ready.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            if (_moduleListLoaded)
            {
                return;
            }

            _moduleListLoaded = true;
            await PopulateModuleListAsync();
            LegalAcceptanceOverlay.PresentIfRequired(OverlayContainer, _legalAcceptance, _services);
        }


        // Setup actions

        /// <summary>
        /// Starts the step-by-step setup flow.
        /// </summary>
        private void OnSetupClicked(object? sender, EventArgs e)
        {
            _currentStep = 1;
            UpdateStepUI();
        }

        /// <summary>
        /// Saves default values and jumps directly to module selection.
        /// </summary>
        private async void OnFastSetupClicked(object? sender, EventArgs e)
        {
            await _sunriseService.SelectLocalAccountAsync();
            _appData.Data.User.Name = Environment.UserName;
            _appData.Data.User.LocalName = Environment.UserName;
            var defaultPorts = new AppPortConfig();
            _appData.Data.Ports.ModulesStart = defaultPorts.ModulesStart;
            await _appData.SaveAsync();

            _currentStep = 3;
            UpdateStepUI();
        }

        /// <summary>
        /// Selects the local account card when any empty area inside it is clicked.
        /// </summary>
        private void OnLocalAccountCardTapped(object? sender, TappedEventArgs e)
        {
            if (!_isAccountActionRunning)
            {
                LocalAccountRadioButton.IsChecked = true;
            }
        }

        /// <summary>
        /// Selects the cloud account card and starts browser authorization.
        /// </summary>
        private void OnCloudAccountCardTapped(object? sender, TappedEventArgs e)
        {
            if (!_isAccountActionRunning)
            {
                CloudAccountRadioButton.IsChecked = true;
            }
        }

        /// <summary>
        /// Switches back to the local profile and removes credentials retained for SUNRISE.
        /// </summary>
        private async void OnLocalAccountCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (!e.Value || _suppressAccountSelectionEvents || _isAccountActionRunning)
            {
                return;
            }

            try
            {
                _isAccountActionRunning = true;
                UpdateAccountSelectionUI();
                await _sunriseService.SelectLocalAccountAsync();
                UsernameEntry.Text = string.IsNullOrWhiteSpace(_appData.Data.User.Name)
                    ? Environment.UserName
                    : _appData.Data.User.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetupWizard] Failed to select local account: {ex}");
                await DisplayAlertAsync(
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
        /// Starts browser authorization as soon as the cloud account option is selected.
        /// </summary>
        private async void OnCloudAccountCheckedChanged(object? sender, CheckedChangedEventArgs e)
        {
            if (!e.Value || _suppressAccountSelectionEvents)
            {
                return;
            }

            UpdateAccountSelectionUI();
            await AuthenticateCloudAccountAsync();
        }

        /// <summary>
        /// Retries browser authorization for the selected cloud account option.
        /// </summary>
        private async void OnCloudAccountActionClicked(object? sender, EventArgs e)
        {
            if (_isAccountActionRunning)
            {
                _accountActionCts?.Cancel();
                return;
            }

            if (!CloudAccountRadioButton.IsChecked)
            {
                CloudAccountRadioButton.IsChecked = true;
                return;
            }

            await AuthenticateCloudAccountAsync();
        }

        /// <summary>
        /// Authorizes SUNRISE in the browser and refreshes the wizard account state.
        /// </summary>
        private async Task<bool> AuthenticateCloudAccountAsync()
        {
            if (_isAccountActionRunning)
            {
                return false;
            }

            var actionCts = new CancellationTokenSource();
            try
            {
                _isAccountActionRunning = true;
                _accountActionCts = actionCts;
                UpdateAccountSelectionUI();

                var result = await _sunriseService.AuthenticateApplicationAsync(actionCts.Token);
                if (actionCts.IsCancellationRequested)
                {
                    if (result.Success && _sunriseService.IsCloudAccount)
                    {
                        await _sunriseService.SelectLocalAccountAsync();
                    }

                    return false;
                }

                if (result.Success)
                {
                    UsernameEntry.Text = _appData.Data.User.Name;
                    UpdateAccountSelectionUI();
                    return true;
                }

                var error = string.IsNullOrWhiteSpace(result.Error)
                    ? L.Get(LocalizationKeys.SetupWizard_CloudAccountRequired)
                    : result.Error;
                CloudAccountStatusLabel.Text = L.Get(
                    LocalizationKeys.SetupWizard_CloudAccountAuthenticationFailed,
                    error);
                await DisplayAlertAsync(
                    L.Get(LocalizationKeys.Settings_AslmAccount_Title),
                    CloudAccountStatusLabel.Text,
                    L.Get(LocalizationKeys.Common_OK));
                return false;
            }
            catch (OperationCanceledException) when (actionCts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetupWizard] SUNRISE authorization failed: {ex}");
                CloudAccountStatusLabel.Text = L.Get(
                    LocalizationKeys.SetupWizard_CloudAccountAuthenticationFailed,
                    ex.Message);
                await DisplayAlertAsync(
                    L.Get(LocalizationKeys.Settings_AslmAccount_Title),
                    CloudAccountStatusLabel.Text,
                    L.Get(LocalizationKeys.Common_OK));
                return false;
            }
            finally
            {
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
        private void UpdateAccountSelectionUI(bool preserveFailureStatus = false)
        {
            var useCloud = CloudAccountRadioButton.IsChecked;
            LocalAccountPanel.IsVisible = !useCloud;
            CloudAccountPanel.IsVisible = useCloud;

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

            if (_isAccountActionRunning && useCloud)
            {
                CloudAccountStatusLabel.Text = L.Get(LocalizationKeys.SetupWizard_CloudAccountConnecting);
                CloudAccountActionButton.Text = L.Get(LocalizationKeys.Common_Cancel);
                return;
            }

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


        // Module list

        /// <summary>
        /// Builds the selectable module list shown on the last setup step.
        /// </summary>
        private async Task PopulateModuleListAsync()
        {
            ModuleList.Children.Clear();
            _moduleChecks.Clear();

            List<ModuleConfig> modules;
            try
            {
                modules = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync());
            }
            catch (Exception ex)
            {
                ModuleList.Children.Add(new Label
                {
                    Text = $"Failed to load module list: {ex.Message}",
                    FontSize = 14,
                    TextColor = GetColorResource("ActionRed", Color.FromArgb("#FF453A"))
                });
                return;
            }

            foreach (var module in modules)
            {
                var isSupported = module.IsSupportedOnCurrentPlatform;
                var check = new CheckBox
                {
                    IsChecked = isSupported,
                    IsEnabled = isSupported
                };
                check.SetDynamicResource(CheckBox.ColorProperty, "LabelPrimary");
                var row = new HorizontalStackLayout { Spacing = 10 };

                var nameLabel = new Label
                {
                    Text = module.Version.All(c => char.IsDigit(c) || c == '.')
                        ? $"{module.Name} v{module.Version}"
                        : $"{module.Name} {module.Version}",
                    FontSize = 14,
                    VerticalOptions = LayoutOptions.Center
                };
                nameLabel.SetDynamicResource(Label.TextColorProperty, "LabelPrimary");

                var descLabel = new Label
                {
                    Text = module.Description,
                    FontSize = 12,
                    VerticalOptions = LayoutOptions.Center
                };
                descLabel.SetDynamicResource(Label.TextColorProperty, "SystemGray");

                row.Children.Add(check);
                row.Children.Add(nameLabel);
                row.Children.Add(descLabel);

                if (!isSupported)
                {
                    var unsupportedLabel = new Label
                    {
                        Text = L.Get(
                            LocalizationKeys.SetupWizard_ModuleUnsupported,
                            PlatformInfo.PlatformKey),
                        FontSize = 12,
                        VerticalOptions = LayoutOptions.Center
                    };
                    unsupportedLabel.SetDynamicResource(Label.TextColorProperty, "ActionRed");
                    row.Children.Add(unsupportedLabel);
                }

                ModuleList.Children.Add(row);
                _moduleChecks.Add((module, check));
            }

            if (modules.Count == 0)
            {
                var emptyLabel = new Label
                {
                    Text = "No modules found in Modules/ directory.",
                    FontSize = 14
                };
                emptyLabel.SetDynamicResource(Label.TextColorProperty, "SystemGray");
                ModuleList.Children.Add(emptyLabel);
            }
        }


        // Navigation

        /// <summary>
        /// Moves one step back in the wizard when possible.
        /// </summary>
        private void OnBackClicked(object? sender, EventArgs e)
        {
            if (_currentStep <= 1)
            {
                return;
            }

            _currentStep--;
            UpdateStepUI();
        }

        /// <summary>
        /// Advances the wizard or starts installation on the last step.
        /// </summary>
        private async void OnNextClicked(object? sender, EventArgs e)
        {
            if (_isAccountActionRunning)
            {
                return;
            }

            if (_currentStep < TotalSteps)
            {
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
                        await DisplayAlertAsync("Error", displayNameErrorMessage, "OK");
                        return;
                    }
                    else
                    {
                        UsernameEntry.Text = validatedUserName;
                    }
                }

                if (_currentStep == 2 && !ValidatePorts())
                {
                    return;
                }

                _currentStep++;
                UpdateStepUI();
                return;
            }

            await StartInstallAsync();
        }

        // Localization

        /// <summary>
        /// Applies localized strings to wizard labels and refreshes step UI.
        /// </summary>
        public void ApplyLocalization()
        {
            Title = L.Get(LocalizationKeys.SetupWizard_Title);
            HeaderTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_Title);
            WelcomeTitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_WelcomeTitle);
            WelcomeSubtitleLabel.Text = L.Get(LocalizationKeys.SetupWizard_WelcomeSubtitle);
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
            InstallStatusLabel.Text = L.Get(LocalizationKeys.SetupWizard_Preparing);
            OverallProgressLabel.Text = L.Get(LocalizationKeys.SetupWizard_OverallProgress);
            BackButton.Text = L.Get(LocalizationKeys.Common_Back);
            UpdateAccountSelectionUI();
            UpdateStepUI();
        }


        // Step UI

        /// <summary>
        /// Switches visible panels and button states for the current wizard step.
        /// </summary>
        private void UpdateStepUI()
        {
            Step0Panel.IsVisible = _currentStep == 0;
            Step1Panel.IsVisible = _currentStep == 1;
            Step2Panel.IsVisible = _currentStep == 2;
            Step3Panel.IsVisible = _currentStep == 3;

            HeaderRow.IsVisible = _currentStep > 0;
            ButtonPanel.IsVisible = _currentStep > 0;
            BackButton.IsVisible = _currentStep > 1;
            NextButton.Text = _currentStep == TotalSteps
                ? L.Get(LocalizationKeys.SetupWizard_Next_Install)
                : L.Get(LocalizationKeys.Common_Next);
            ResetNavigationButtons();

            StepLabel.Text = _currentStep switch
            {
                1 => L.Get(LocalizationKeys.SetupWizard_StepFormat, 1, 3, L.Get(LocalizationKeys.Settings_Accounts_SectionAslm)),
                2 => L.Get(LocalizationKeys.SetupWizard_StepFormat, 2, 3, L.Get(LocalizationKeys.SetupWizard_Step_PortConfiguration)),
                3 => L.Get(LocalizationKeys.SetupWizard_StepFormat, 3, 3, L.Get(LocalizationKeys.SetupWizard_Step_ModuleSelection)),
                _ => string.Empty
            };
        }


        // Port validation

        /// <summary>
        /// Validates the module start port draft.
        /// </summary>
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
        /// Shows the current port validation error.
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
        /// Gets whether the installation log panel is visible.
        /// </summary>
        public bool IsInstallLogVisible => _logVisible;

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
        /// Shows or hides the installation log panel.
        /// </summary>
        private void OnToggleLogClicked(object? sender, EventArgs e)
        {
            _logVisible = !_logVisible;
            OnPropertyChanged(nameof(IsInstallLogVisible));
            ToggleLogButton.Text = _logVisible
                ? L.Get(LocalizationKeys.SetupWizard_HideLog)
                : L.Get(LocalizationKeys.SetupWizard_ShowLog);
            QueueInstallLogLayoutRefresh();
        }

        /// <summary>
        /// Schedules layout refresh passes so the native console host measures like <see cref="ConsolesView"/>.
        /// </summary>
        private void QueueInstallLogLayoutRefresh()
        {
            RefreshInstallLogLayout();

            if (Dispatcher == null || Interlocked.Exchange(ref _installLogLayoutRefreshQueued, 1) == 1)
            {
                return;
            }

            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), RefreshInstallLogLayout);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(48), () =>
            {
                RefreshInstallLogLayout();
                Interlocked.Exchange(ref _installLogLayoutRefreshQueued, 0);
            });
        }

        /// <summary>
        /// Invalidates the install panel and console host after log visibility or text changes.
        /// </summary>
        private void RefreshInstallLogLayout()
        {
            InstallPanel.InvalidateMeasure();
            InstallLogConsole.InvalidateMeasure();
        }


        // Installation

        /// <summary>
        /// Saves the wizard data and installs the selected module stack.
        /// </summary>
        private async Task StartInstallAsync()
        {
            // Persist the profile and port values before any installation begins.
            if (_appData.Data.User.AccountMode == AppAccountMode.Local &&
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

            var selectedModules = _moduleChecks
                .Where(moduleCheck => moduleCheck.Check.IsEnabled && moduleCheck.Check.IsChecked)
                .Select(moduleCheck => moduleCheck.Module)
                .ToList();

            if (selectedModules.Count == 0)
            {
                await FinishSetupAsync();
                return;
            }

            // Switch the final wizard step from selection mode into install mode.
            ButtonPanel.IsVisible = false;
            ModuleListScroll.IsVisible = false;
            InstallPanel.IsVisible = true;
            ToggleLogButton.IsVisible = true;
            StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Installing);

            lock (_logLock)
            {
                _logBuffer.Clear();
            }

            _installLogSessionKey = $"setup-install-{DateTime.UtcNow.Ticks}";
            OnPropertyChanged(nameof(InstallLogSessionKey));
            OnPropertyChanged(nameof(InstallLogText));
            QueueInstallLogLayoutRefresh();

            _cts = new CancellationTokenSource();
            var logProgress = new InlineProgress<string>(AddLog);

            List<ModuleConfig> catalog;
            try
            {
                catalog = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync(), _cts.Token);
            }
            catch (Exception ex)
            {
                AddLog($"Failed to load module catalog: {ex.Message}");
                ConfigureRetryAndSkipButtons();
                return;
            }

            var installModules = ModuleDependencyResolver.ExpandInstallOrder(selectedModules, catalog);

            var totalSteps = 0;
            var completedSteps = 0;
            var hasFailures = false;

            var requiredEngineIds = installModules
                .SelectMany(module => module.Dependencies.Engines)
                .Select(engine => engine.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _engineInstaller.InvalidateCache();
            var allEngines = await Task.Run(() => _engineInstaller.DiscoverEngines(), _cts.Token);
            totalSteps += requiredEngineIds.Count;
            totalSteps += installModules.Sum(GetModuleInstallStepCount);

            // Reset rolling download metrics before the first transfer begins.
            ResetDownloadMetrics();

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
                var unsupportedModule = installModules.FirstOrDefault(module => !module.IsSupportedOnCurrentPlatform);
                if (unsupportedModule != null)
                {
                    throw new PlatformNotSupportedException(
                        $"Module '{unsupportedModule.Name}' does not support {PlatformInfo.PlatformKey}.");
                }

                // Install each required engine before module downloads begin.
                foreach (var engineId in requiredEngineIds)
                {
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

                    if (engine.Status.Installed)
                    {
                        AddLog($"[OK] Engine '{engine.Name}' already installed.");
                        completedSteps++;
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

                    UpdateInstallStatus($"Installing engine: {engine.Name}...");
                    ResetFileProgress();
                    await Task.Run(
                        () => _engineInstaller.InstallAsync(engine, logProgress, downloadProgress, _cts.Token),
                        _cts.Token);
                    completedSteps++;
                    UpdateOverallProgress(completedSteps, totalSteps);
                    ResetFileProgress();
                }

                // Install each selected module (and declared module dependencies) either through the update-aware pipeline or the legacy download flow.
                foreach (var module in installModules)
                {
                    if (module.Status.FirstRunCompleted)
                    {
                        AddLog($"[OK] {module.Name} already set up.");
                        completedSteps += GetModuleInstallStepCount(module);
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

                    if (ShouldUseConfiguredUpdateInstall(module))
                    {
                        UpdateInstallStatus($"Installing {module.Name}...");
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
                        continue;
                    }

                    UpdateInstallStatus($"Downloading {module.Name}...");
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

                    if (!downloaded)
                    {
                        AddLog($"[Error] Source download failed for {module.Name}");
                        hasFailures = true;
                        completedSteps++;
                        UpdateOverallProgress(completedSteps, totalSteps);
                        continue;
                    }

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

                    UpdateInstallStatus($"Setting up {installedModule.Name}...");
                    var success = await Task.Run(
                        () => _moduleRunner.ExecuteFirstRunAsync(installedModule, logProgress, _cts.Token),
                        _cts.Token);
                    if (success)
                    {
                        await _moduleInstaller.SaveConfigAsync(installedModule);
                    }
                    completedSteps++;
                    UpdateOverallProgress(completedSteps, totalSteps);

                    AddLog(success
                        ? $"[OK] {module.Name} installed successfully"
                        : $"[Error] Setup failed for {module.Name}");
                    hasFailures |= !success;
                }

                UpdateInstallStatus("Setup complete!");
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_SetupComplete);
            }
            catch (OperationCanceledException)
            {
                hasFailures = true;
                UpdateInstallStatus("Installation canceled.");
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Canceled);
            }
            catch (Exception ex)
            {
                hasFailures = true;
                UpdateInstallStatus($"Error: {ex.Message}");
                StepLabel.Text = L.Get(LocalizationKeys.SetupWizard_Failed);
                AddLog($"Error: {ex.Message}");
            }

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
        /// Restores the default back and next button handlers.
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
            BackButton.Text = "Back";
        }

        /// <summary>
        /// Converts the main action button into the finish action.
        /// </summary>
        private void ConfigureFinishButton()
        {
            ResetNavigationButtons();
            BackButton.IsVisible = false;
            NextButton.Text = L.Get(LocalizationKeys.SetupWizard_Finish);
            NextButton.Clicked -= OnNextClicked;
            NextButton.Clicked += OnFinishClicked;
        }

        /// <summary>
        /// Converts the buttons into retry and skip actions after a failed install.
        /// </summary>
        private void ConfigureRetryAndSkipButtons()
        {
            ResetNavigationButtons();

            NextButton.Text = L.Get(LocalizationKeys.SetupWizard_Retry);
            NextButton.Clicked -= OnNextClicked;
            NextButton.Clicked += OnRetryInstallClicked;

            BackButton.Text = "Skip";
            BackButton.IsVisible = true;
            BackButton.Clicked -= OnBackClicked;
            BackButton.Clicked += OnSkipClicked;
        }


        // Install completion actions

        /// <summary>
        /// Restarts the installation phase after a failure.
        /// </summary>
        private async void OnRetryInstallClicked(object? sender, EventArgs e)
        {
            ButtonPanel.IsVisible = false;
            await StartInstallAsync();
        }

        /// <summary>
        /// Completes the wizard and opens the main application shell.
        /// </summary>
        private async void OnFinishClicked(object? sender, EventArgs e)
        {
            await FinishSetupAsync();
        }

        /// <summary>
        /// Completes the wizard even when installation had failures.
        /// </summary>
        private async void OnSkipClicked(object? sender, EventArgs e)
        {
            await FinishSetupAsync();
        }


        // Progress UI

        /// <summary>
        /// Updates the current installation status text.
        /// </summary>
        private void UpdateInstallStatus(string message)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                InstallStatusLabel.Text = message);
        }

        /// <summary>
        /// Updates the overall installation progress bar.
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
        /// Clears the per-file progress UI before the next download.
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
        /// Returns whether the module should be installed through the update-aware pipeline.
        /// </summary>
        private static bool ShouldUseConfiguredUpdateInstall(ModuleConfig module)
        {
            return module.HasDeclaredUpdateConfig &&
                   string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(module.Source.Repo);
        }

        /// <summary>
        /// Returns how many overall progress steps one module consumes during setup.
        /// </summary>
        private static int GetModuleInstallStepCount(ModuleConfig module)
        {
            return ShouldUseConfiguredUpdateInstall(module) ? 1 : 2;
        }

        /// <summary>
        /// Installs one module through the updater so setup respects the manifest update configuration.
        /// </summary>
        private async Task<bool> InstallModuleFromUpdateConfigAsync(
            ModuleConfig module,
            IProgress<string> logProgress,
            IProgress<DownloadProgress> downloadProgress,
            CancellationToken ct)
        {
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
        /// Marks the first run as completed and opens the main shell.
        /// </summary>
        private async Task FinishSetupAsync()
        {
            try
            {
                _appData.Data.FirstRunCompleted = true;
                await _appData.SaveAsync();

                if (Application.Current?.Windows.Count > 0)
                {
                    var mainPage = _services.GetRequiredService<AppShellPage>();
                    Application.Current.Windows[0].Page = mainPage;
                    _localization.SyncFlowDirection();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SetupWizard] FinishSetupAsync error: {ex}");
                await DisplayAlertAsync("Error", $"Failed to navigate: {ex.Message}", "OK");
            }
        }


        // Logging

        /// <summary>
        /// Appends one log line; batched flushes push updates through install-log bindings.
        /// </summary>
        private void AddLog(string message)
        {
            lock (_logLock)
            {
                _logBuffer.AppendLine(message);
            }

            Interlocked.Exchange(ref _logFlushRequested, 1);
            if (Interlocked.Exchange(ref _logFlushQueued, 1) == 1)
            {
                return;
            }

            _ = FlushLogAsync();
        }

        /// <summary>
        /// Flushes buffered log lines at a steady cadence while installers keep streaming output.
        /// </summary>
        private async Task FlushLogAsync()
        {
            try
            {
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
        /// Finds a named color resource with a defensive fallback when the key is absent.
        /// </summary>
        private static Color GetColorResource(string key, Color fallback) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color c
                ? c
                : fallback;

        /// <summary>
        /// Reports progress synchronously on the producer thread so UI updates can be throttled explicitly.
        /// </summary>
        private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
        {
            public void Report(T value)
            {
                handler(value);
            }
        }
    }
}
