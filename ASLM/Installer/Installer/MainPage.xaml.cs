// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Installer
{
    /// <summary>
    /// Coordinates installer wizard state, validation, and installation progress.
    /// </summary>
    public partial class MainPage : ContentPage
    {
        // Wizard state

        /// <summary>
        /// Service that validates the install location and performs the installation.
        /// </summary>
        private readonly InstallerService _installerService = new();

        /// <summary>
        /// Service that loads the legal documents shown in the wizard.
        /// </summary>
        private readonly LegalDocumentService _legalDocumentService = new();

        /// <summary>
        /// Cancellation source passed to the installation; it is never cancelled by the page itself.
        /// </summary>
        private readonly CancellationTokenSource _installCancellation = new();

        /// <summary>
        /// Identifiers of the legal documents the user has accepted so far.
        /// </summary>
        private readonly HashSet<string> _acceptedLegalDocumentIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Legal documents of the wizard, one step each; empty until they are loaded.
        /// </summary>
        private IReadOnlyList<LegalDocument> _legalDocuments = [];

        /// <summary>
        /// Result of the finished installation, or null before it succeeded.
        /// </summary>
        private InstallManifest? _manifest;

        /// <summary>
        /// Zero-based index of the visible wizard step.
        /// </summary>
        private int _step;

        /// <summary>
        /// Whether the installation is running; navigation is locked meanwhile.
        /// </summary>
        private bool _isInstalling;

        /// <summary>
        /// Whether the installation finished successfully and the wizard only offers to finish.
        /// </summary>
        private bool _isInstalled;

        /// <summary>
        /// Whether the step view is being updated by code, so control events are not treated as user input.
        /// </summary>
        private bool _isRendering;

        /// <summary>
        /// Whether the missing acceptance of the current legal document is highlighted as an error.
        /// </summary>
        private bool _showLegalRequired;


        // Page lifecycle

        /// <summary>
        /// Creates the installer wizard and initializes the default path preview.
        /// </summary>
        public MainPage()
        {
            InitializeComponent();
            BasePathEntry.Text = _installerService.GetDefaultInstallBasePath();
            UpdatePathPreview();
        }

        /// <summary>
        /// Loads legal documents when the wizard becomes visible and renders the first step.
        /// </summary>
        protected override async void OnAppearing()
        {
            base.OnAppearing();

            await LoadLegalDocumentsAsync();
            RenderStep();
        }

        /// <summary>
        /// Loads generated legal documents once; a failure is shown in the footer and leaves the list empty.
        /// </summary>
        private async Task LoadLegalDocumentsAsync()
        {
            if (_legalDocuments.Count > 0)
            {
                return;
            }

            try
            {
                _legalDocuments = await _legalDocumentService.LoadAsync();
            }
            catch (Exception ex)
            {
                FooterLabel.Text = ex.Message;
            }
        }


        // Navigation handlers

        /// <summary>
        /// Advances the wizard, finishes it after a successful installation, or starts the installation from the
        /// confirmation step. Leaving the welcome step requires that ASLM is not installed yet, leaving a legal step
        /// requires acceptance, and leaving the path step requires a valid path.
        /// </summary>
        private async void OnNextClicked(object? sender, EventArgs e)
        {
            // Ignore navigation while installation is running.
            if (_isInstalling)
            {
                return;
            }

            FooterLabel.Text = string.Empty;

            // Finish the wizard or launch ASLM after a successful install.
            if (_isInstalled)
            {
                CloseOrLaunch();
                return;
            }

            // A registered installation must be removed first, because both would share one per-user directory.
            if (_step == 0 && _installerService.GetRegisteredInstallationMessage() is { } registeredMessage)
            {
                FooterLabel.Text = registeredMessage;
                return;
            }

            // Require explicit acceptance before leaving a legal step.
            if (IsLegalStep(_step) && !IsCurrentLegalDocumentAccepted())
            {
                _showLegalRequired = true;
                FooterLabel.Text = "Accept this document before continuing.";
                RenderLegalDocumentValidation();
                return;
            }

            _showLegalRequired = false;

            // Validate the target directory before leaving the path step.
            if (_step == PathStep)
            {
                var validation = ValidateCurrentInstallPath();
                if (!validation.IsValid)
                {
                    FooterLabel.Text = validation.Message;
                    return;
                }
            }

            // Start installation instead of advancing to another view step.
            if (_step == ConfirmStep)
            {
                _step = InstallStep;
                RenderStep();
                await StartInstallAsync();
                return;
            }

            // Otherwise move on to the next step, never past the installation step.
            _step = Math.Min(_step + 1, InstallStep);
            RenderStep();
        }

        /// <summary>
        /// Closes the installer when a legal document is declined.
        /// </summary>
        private void OnDeclineClicked(object? sender, EventArgs e)
        {
            Application.Current?.Quit();
        }

        /// <summary>
        /// Moves the wizard one step back; ignored while installing and after a finished installation.
        /// </summary>
        private void OnBackClicked(object? sender, EventArgs e)
        {
            if (_isInstalling || _isInstalled)
            {
                return;
            }

            FooterLabel.Text = string.Empty;
            _step = Math.Max(_step - 1, 0);
            RenderStep();
        }


        // Input handlers

        /// <summary>
        /// Tracks acceptance for the currently displayed legal document.
        /// </summary>
        private void OnAcceptLegalChanged(object? sender, CheckedChangedEventArgs e)
        {
            // Ignore changes made by RenderStep and events that do not belong to a legal step.
            if (_isRendering || !IsLegalStep(_step))
            {
                return;
            }

            var document = GetCurrentLegalDocument();
            if (document is null)
            {
                return;
            }

            // Record the decision; accepting also clears a pending "acceptance required" error.
            if (e.Value)
            {
                _acceptedLegalDocumentIds.Add(document.Id);
                _showLegalRequired = false;
            }
            else
            {
                _acceptedLegalDocumentIds.Remove(document.Id);
            }

            // Refresh the highlighting; the footer message stays while the required-acceptance error is shown.
            if (IsLegalStep(_step))
            {
                if (!_showLegalRequired)
                {
                    FooterLabel.Text = string.Empty;
                }

                RenderLegalDocumentValidation();
            }
        }

        /// <summary>
        /// Opens the Windows folder picker and writes the selected path into the form; other platforms get an
        /// informational alert instead.
        /// </summary>
        private async void OnBrowseClicked(object? sender, EventArgs e)
        {
#if WINDOWS
            try
            {
                FooterLabel.Text = string.Empty;
                var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView;
                var folderPath = WindowsFolderPicker.PickFolder(window, "Select installation directory");

                if (!string.IsNullOrWhiteSpace(folderPath))
                {
                    BasePathEntry.Text = folderPath;
                }
            }
            catch (Exception ex)
            {
                FooterLabel.Text = $"Unable to open folder picker: {ex.Message}";
            }
#else
            await DisplayAlert("ASLM Installer", "Folder browsing is available in the Windows installer build.", "OK");
#endif
        }

        /// <summary>
        /// Updates the install path preview when the base path or folder name text changes.
        /// </summary>
        private void OnInstallPathChanged(object? sender, TextChangedEventArgs e)
        {
            UpdatePathPreview();
        }


        // Installation execution

        /// <summary>
        /// Runs installation and reflects progress in the wizard. Navigation is locked while it runs; on failure the
        /// wizard returns to the confirmation step so the user can retry.
        /// </summary>
        private async Task StartInstallAsync()
        {
            // Lock navigation while the installation runs.
            _isInstalling = true;
            BackButton.IsEnabled = false;
            NextButton.IsEnabled = false;

            try
            {
                // Run the file extraction workflow and surface progress in the install view.
                var options = CreateInstallOptions();
                var progress = new Progress<InstallProgress>(update =>
                {
                    InstallStatusLabel.Text = update.Message;
                    InstallProgressBar.Progress = Math.Clamp(update.Percent, 0, 100) / 100d;
                });

                _manifest = await _installerService.InstallAsync(options, progress, _installCancellation.Token);

                // Switch the wizard into the post-install completion state.
                _isInstalled = true;
                LaunchAfterInstallPanel.IsVisible = true;
                NextButton.Text = "Finish";
                NextButton.IsEnabled = true;
                InstallStatusLabel.Text = "ASLM has been installed successfully.";
                InstallProgressBar.Progress = 1;
            }
            catch (Exception ex)
            {
                // Return to confirmation so the user can adjust options and retry.
                FooterLabel.Text = ex.Message;
                NextButton.Text = "Retry";
                NextButton.IsEnabled = true;
                BackButton.IsEnabled = true;
                _step = ConfirmStep;
            }
            finally
            {
                // Unlock navigation and refresh the view for the success or retry state.
                _isInstalling = false;
                RenderStep();
            }
        }

        /// <summary>
        /// Builds the immutable options sent to the installer service from the current form values.
        /// </summary>
        private InstallOptions CreateInstallOptions()
        {
            // Stamp every accepted document with the same acceptance time.
            var acceptedAtUtc = DateTimeOffset.UtcNow;
            var acceptedDocuments = _legalDocuments
                .Where(document => _acceptedLegalDocumentIds.Contains(document.Id))
                .Select(document => new AcceptedLegalDocument(
                    document.Id,
                    document.Title,
                    document.FileName,
                    document.Sha256,
                    acceptedAtUtc))
                .ToList();

            return new InstallOptions(
                BasePathEntry.Text ?? string.Empty,
                FolderNameEntry.Text ?? "ASLM",
                acceptedDocuments,
                DesktopShortcutSwitch.IsToggled,
                StartMenuShortcutSwitch.IsToggled);
        }


        // Path validation

        /// <summary>
        /// Validates the base path and folder name currently entered in the form.
        /// </summary>
        private InstallPathValidation ValidateCurrentInstallPath()
        {
            return _installerService.ValidateInstallPath(
                BasePathEntry.Text ?? string.Empty,
                FolderNameEntry.Text ?? string.Empty);
        }

        /// <summary>
        /// Shows the resulting install path and, when the input is invalid, the validation message next to it.
        /// </summary>
        private void UpdatePathPreview()
        {
            var validation = ValidateCurrentInstallPath();
            if (validation.IsValid)
            {
                FinalPathLabel.Text = validation.InstallPath;
                PathValidationLabel.Text = string.Empty;
            }
            else
            {
                var basePath = BasePathEntry.Text ?? string.Empty;
                var folderName = FolderNameEntry.Text ?? string.Empty;

                FinalPathLabel.Text = string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(folderName)
                    ? string.Empty
                    : Path.Combine(basePath, folderName);
                PathValidationLabel.Text = validation.Message;
            }
        }


        // Wizard rendering

        /// <summary>
        /// Applies the current wizard step to visible panels, buttons, and headings.
        /// </summary>
        private void RenderStep()
        {
            _isRendering = true;

            // Toggle the panel that matches the active wizard step.
            WelcomeView.IsVisible = _step == 0;
            LegalView.IsVisible = IsLegalStep(_step);
            PathView.IsVisible = _step == PathStep;
            ConfirmView.IsVisible = _step == ConfirmStep;
            InstallView.IsVisible = _step == InstallStep;

            // Update navigation buttons and the step indicator.
            DeclineButton.IsVisible = IsLegalStep(_step);
            BackButton.IsVisible = _step > 0 && !_isInstalled;
            BackButton.IsEnabled = !_isInstalling && _step > 0;
            NextButton.IsEnabled = !_isInstalling;
            StepLabel.Text = $"Step {_step + 1} of {TotalStepCount}";

            // Apply step-specific titles, subtitles, and primary actions.
            if (_step == 0)
            {
                TitleLabel.Text = "Install ASLM";
                SubtitleLabel.Text = "Choose a location and review the required documents.";
                NextButton.Text = "Next";
            }
            else if (IsLegalStep(_step))
            {
                RenderLegalDocumentStep();
                NextButton.Text = "Accept and continue";
            }
            else if (_step == PathStep)
            {
                TitleLabel.Text = "Choose launcher location";
                SubtitleLabel.Text = "Select where the shared launcher and update archive will be placed. The application itself will be installed to your personal AppData folder.";
                NextButton.Text = "Next";
                UpdatePathPreview();
            }
            else if (_step == ConfirmStep)
            {
                TitleLabel.Text = "Confirm installation";
                SubtitleLabel.Text = "Review the selected options before writing files.";
                NextButton.Text = "Install";
                UpdateConfirmation();
            }
            else if (_step == InstallStep)
            {
                TitleLabel.Text = _isInstalled ? "Installation complete" : "Installing ASLM";
                SubtitleLabel.Text = _isInstalled ? "ASLM is ready to use." : "Files are being extracted to the selected locations.";
                NextButton.Text = _isInstalled ? "Finish" : "Installing";
            }

            _isRendering = false;
        }

        /// <summary>
        /// Renders the current legal document page.
        /// </summary>
        private void RenderLegalDocumentStep()
        {
            var document = GetCurrentLegalDocument();
            var legalIndex = CurrentLegalDocumentIndex + 1;

            TitleLabel.Text = document?.Title ?? "Review legal document";
            SubtitleLabel.Text = $"Legal document {legalIndex} of {_legalDocuments.Count}";
            LegalDocumentTitleLabel.Text = document?.FileName ?? string.Empty;
            LegalDocumentEditor.Text = document?.Markdown ?? string.Empty;
            AcceptLegalLabel.Text = document is null
                ? "I have read and accept this document."
                : $"I have read and accept {document.Title}.";
            AcceptLegalCheckBox.IsChecked = document is not null && _acceptedLegalDocumentIds.Contains(document.Id);

            RenderLegalDocumentValidation();
        }

        /// <summary>
        /// Colors the acceptance label, document title and document border as an error when the user tried to
        /// continue without accepting the current document, and with the normal colors otherwise.
        /// </summary>
        private void RenderLegalDocumentValidation()
        {
            var showError = _showLegalRequired && !IsCurrentLegalDocumentAccepted();
            var labelColor = showError
                ? (Color)Application.Current!.Resources["SystemRed"]
                : (Color)Application.Current!.Resources["LabelPrimary"];
            var borderColor = showError
                ? (Color)Application.Current!.Resources["SystemRed"]
                : (Color)Application.Current!.Resources["Separator"];

            AcceptLegalLabel.TextColor = labelColor;
            LegalDocumentTitleLabel.TextColor = labelColor;
            LegalDocumentBorder.Stroke = borderColor;
        }

        /// <summary>
        /// Updates the final confirmation text showing both installation locations.
        /// </summary>
        private void UpdateConfirmation()
        {
            var validation = ValidateCurrentInstallPath();
            var userAppDir = _installerService.GetUserAppDir();

            if (validation.IsValid)
            {
                ConfirmPathLabel.Text =
                    $"Launcher location: {validation.InstallPath}{Environment.NewLine}" +
                    $"Application data:  {userAppDir}";
            }
            else
            {
                ConfirmPathLabel.Text = $"Launcher location: {validation.Message}";
            }
        }


        // Wizard step helpers

        /// <summary>
        /// Checks whether a step displays a legal document; the legal steps lie between the welcome step and the
        /// path step.
        /// </summary>
        private bool IsLegalStep(int step)
        {
            return step >= 1 && step < PathStep;
        }


        // Wizard step indices

        /// <summary>
        /// Returns the zero-based legal document index for the current wizard step; it is only meaningful on
        /// legal steps.
        /// </summary>
        private int CurrentLegalDocumentIndex => _step - 1;

        /// <summary>
        /// Returns the wizard step index for the installation path page.
        /// </summary>
        private int PathStep => 1 + _legalDocuments.Count;

        /// <summary>
        /// Returns the wizard step index for the confirmation page.
        /// </summary>
        private int ConfirmStep => PathStep + 1;

        /// <summary>
        /// Returns the wizard step index for the installation progress page.
        /// </summary>
        private int InstallStep => PathStep + 2;

        /// <summary>
        /// Returns the total number of wizard steps including the welcome page.
        /// </summary>
        private int TotalStepCount => InstallStep + 1;


        // Legal document state

        /// <summary>
        /// Returns the legal document for the current wizard step, or null when the step shows no legal document.
        /// </summary>
        private LegalDocument? GetCurrentLegalDocument()
        {
            var index = CurrentLegalDocumentIndex;

            return index >= 0 && index < _legalDocuments.Count
                ? _legalDocuments[index]
                : null;
        }

        /// <summary>
        /// Checks whether the current legal document has been accepted.
        /// </summary>
        private bool IsCurrentLegalDocumentAccepted()
        {
            var document = GetCurrentLegalDocument();

            return document is not null && _acceptedLegalDocumentIds.Contains(document.Id);
        }


        // Completion

        /// <summary>
        /// Launches ASLM when requested and exits the installer; if launching fails, the error is shown and the
        /// installer stays open.
        /// </summary>
        private void CloseOrLaunch()
        {
            if (_manifest is not null && LaunchAfterInstallCheckBox.IsChecked)
            {
                try
                {
                    _installerService.Launch(_manifest.SharedInstallPath);
                }
                catch (Exception ex)
                {
                    FooterLabel.Text = ex.Message;
                    return;
                }
            }

            Application.Current?.Quit();
        }
    }
}
