// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Blocking overlay that presents required legal documents until they are accepted.
    /// </summary>
    public partial class LegalAcceptanceView : ContentView, ILocalizable
    {
        // Fields

        /// <summary>
        /// Service that loads the bundled documents and stores the acceptance.
        /// </summary>
        private readonly LegalAcceptanceService _legalAcceptance;

        /// <summary>
        /// IDs of the documents the user has ticked in the current session.
        /// </summary>
        private readonly HashSet<string> _acceptedLegalDocumentIds = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// All bundled documents; also used to keep earlier acceptance records when the new ones are merged.
        /// </summary>
        private IReadOnlyList<LegalDocument> _allDocuments = [];

        /// <summary>
        /// Documents the wizard walks through: all of them on a first start, otherwise only the new or updated ones.
        /// </summary>
        private IReadOnlyList<LegalDocument> _legalDocuments = [];

        /// <summary>
        /// Zero-based index of the wizard step, which is also the index of the shown document in <c>_legalDocuments</c>.
        /// </summary>
        private int _step;

        /// <summary>
        /// Set while <c>RenderStep</c> updates the controls, so the programmatic checkbox change is not treated as user input.
        /// </summary>
        private bool _isRendering;

        /// <summary>
        /// Set after the user tried to continue without accepting; turns on the red validation highlight.
        /// </summary>
        private bool _showLegalRequired;


        // Events

        /// <summary>
        /// Raised after the user accepts every required legal document.
        /// </summary>
        public event EventHandler? AcceptanceCompleted;


        // Construction

        /// <summary>
        /// Creates the legal acceptance overlay view.
        /// </summary>
        public LegalAcceptanceView(LegalAcceptanceService legalAcceptance)
        {
            InitializeComponent();
            _legalAcceptance = legalAcceptance;
        }


        // Localization

        /// <summary>
        /// Applies localized labels for the legal acceptance flow, including the step-dependent texts.
        /// </summary>
        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.Legal_Title);
            SubtitleLabel.Text = L.Get(LocalizationKeys.Legal_Subtitle);
            UpdatedNoticeLabel.Text = L.Get(LocalizationKeys.Legal_DocumentsUpdated);
            BackButton.Text = L.Get(LocalizationKeys.Common_Back);
            DeclineButton.Text = L.Get(LocalizationKeys.Legal_Decline);
            RenderStep();
        }


        // Public API

        /// <summary>
        /// Loads bundled documents and prepares the first wizard step for display.
        /// </summary>
        public async Task OpenAsync()
        {
            // Reset the wizard so every opening starts from a clean state.
            _acceptedLegalDocumentIds.Clear();
            _allDocuments = [];
            _legalDocuments = [];
            _step = 0;
            _showLegalRequired = false;
            FooterLabel.Text = string.Empty;

            // The "documents updated" notice applies only when the user has accepted documents before.
            await LoadLegalDocumentsAsync();
            UpdatedNoticeBorder.IsVisible = _legalAcceptance.HasStoredAcceptance;
            RenderStep();
        }


        // Input handlers

        /// <summary>
        /// Advances the wizard or saves acceptance when the final document is confirmed.
        /// </summary>
        private async void OnNextClicked(object? sender, EventArgs e)
        {
            FooterLabel.Text = string.Empty;

            // The current document must be ticked before the wizard can continue.
            if (!IsCurrentLegalDocumentAccepted())
            {
                _showLegalRequired = true;
                FooterLabel.Text = L.Get(LocalizationKeys.Legal_AcceptRequired);
                RenderLegalDocumentValidation();
                return;
            }

            _showLegalRequired = false;

            // Show the next document, or save the acceptance when the last one was just confirmed.
            if (_step < _legalDocuments.Count - 1)
            {
                _step++;
                RenderStep();
                return;
            }

            await SaveAcceptanceAsync();
        }

        /// <summary>
        /// Moves back to the previous legal document step when the Back button is pressed.
        /// </summary>
        private void OnBackClicked(object? sender, EventArgs e)
        {
            FooterLabel.Text = string.Empty;
            _step = Math.Max(_step - 1, 0);
            RenderStep();
        }

        /// <summary>
        /// Quits the application when the user presses the decline button.
        /// </summary>
        private void OnDeclineClicked(object? sender, EventArgs e)
        {
            Application.Current?.Quit();
        }

        /// <summary>
        /// Records or withdraws the acceptance of the displayed legal document when its checkbox is toggled.
        /// </summary>
        private void OnAcceptLegalChanged(object? sender, CheckedChangedEventArgs e)
        {
            // Checkbox changes made by RenderStep are not user decisions.
            if (_isRendering)
            {
                return;
            }

            var document = GetCurrentLegalDocument();
            if (document is null)
            {
                return;
            }

            // Ticking clears the pending "required" highlight; unticking only withdraws the acceptance.
            if (e.Value)
            {
                _acceptedLegalDocumentIds.Add(document.Id);
                _showLegalRequired = false;
            }
            else
            {
                _acceptedLegalDocumentIds.Remove(document.Id);
            }

            // Drop the "acceptance required" footer message once the highlight is no longer shown.
            if (!_showLegalRequired)
            {
                FooterLabel.Text = string.Empty;
            }

            RenderLegalDocumentValidation();
        }

        /// <summary>
        /// Swallows taps inside the dialog so they do not reach the shell behind the overlay.
        /// </summary>
        private void OnDialogTapped(object? sender, EventArgs e)
        {
            // Intentionally left blank so dialog taps do not bubble through the overlay.
        }


        // Document loading

        /// <summary>
        /// Loads bundled documents and limits the wizard to pending new or updated terms.
        /// </summary>
        private async Task LoadLegalDocumentsAsync()
        {
            // Documents are loaded only once per opening.
            if (_allDocuments.Count > 0)
            {
                return;
            }

            // A returning user sees only pending documents; a first start shows all of them.
            try
            {
                _allDocuments = await _legalAcceptance.LoadDocumentsAsync();
                _legalDocuments = _legalAcceptance.HasStoredAcceptance
                    ? _legalAcceptance.GetPendingDocuments(_allDocuments)
                    : _allDocuments;
            }
            catch (Exception ex)
            {
                // The load error is shown in the footer; the wizard then has no document to display.
                FooterLabel.Text = ex.Message;
            }
        }


        // Persistence

        /// <summary>
        /// Persists accepted documents and notifies the host to hide the overlay.
        /// </summary>
        private async Task SaveAcceptanceAsync()
        {
            try
            {
                // Disable the button against double submission, then build one record per accepted document.
                NextButton.IsEnabled = false;

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

                // Save the records merged with earlier acceptance, then tell the host that the overlay can close.
                await _legalAcceptance.MergeAcceptedDocumentsAsync(_allDocuments, acceptedDocuments);
                AcceptanceCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Show the failure and let the user retry.
                FooterLabel.Text = ex.Message;
                NextButton.IsEnabled = true;
            }
        }


        // Wizard rendering

        /// <summary>
        /// Applies the current wizard step to visible labels, document content, and buttons.
        /// </summary>
        private void RenderStep()
        {
            // The checkbox is set programmatically below; its change handler must ignore that.
            _isRendering = true;

            // Navigation buttons and the step counter.
            BackButton.IsVisible = _step > 0;
            BackButton.IsEnabled = _step > 0;
            NextButton.IsEnabled = true;

            var totalSteps = Math.Max(_legalDocuments.Count, 1);
            StepLabel.Text = L.Get(LocalizationKeys.Legal_StepFormat, _step + 1, totalSteps);

            // Document title and text, and the acceptance checkbox restored from the session.
            var document = GetCurrentLegalDocument();
            LegalDocumentTitleLabel.Text = document?.Title ?? string.Empty;
            LegalDocumentEditor.Text = document?.Markdown ?? string.Empty;

            var isAccepted = document is not null && _acceptedLegalDocumentIds.Contains(document.Id);
            AcceptLegalCheckBox.IsChecked = isAccepted;
            AcceptLegalLabel.Text = document is null
                ? string.Empty
                : L.Get(LocalizationKeys.Legal_AcceptDocumentFormat, document.Title);

            // The last step confirms and finishes instead of moving on.
            NextButton.Text = _step < _legalDocuments.Count - 1
                ? L.Get(LocalizationKeys.Common_Next)
                : L.Get(LocalizationKeys.Legal_AcceptContinue);

            RenderLegalDocumentValidation();
            _isRendering = false;
        }

        /// <summary>
        /// Highlights the acceptance checkbox and document border when confirmation is required.
        /// </summary>
        private void RenderLegalDocumentValidation()
        {
            // Theme colors fall back to fixed colors when the resources are not available.
            var showError = _showLegalRequired && !IsCurrentLegalDocumentAccepted();
            var errorColor = (Color?)Application.Current?.Resources["SystemRed"] ?? Colors.Red;
            var normalBorderColor = (Color?)Application.Current?.Resources["Separator"] ?? Colors.Gray;
            var normalLabelColor = (Color?)Application.Current?.Resources["LabelPrimary"] ?? Colors.White;

            LegalDocumentBorder.Stroke = showError ? errorColor : normalBorderColor;
            AcceptLegalLabel.TextColor = showError ? errorColor : normalLabelColor;
        }


        // Wizard helpers

        /// <summary>
        /// Returns the legal document shown at the current wizard step.
        /// </summary>
        private LegalDocument? GetCurrentLegalDocument()
        {
            return _step >= 0 && _step < _legalDocuments.Count
                ? _legalDocuments[_step]
                : null;
        }

        /// <summary>
        /// Returns whether the current legal document has been explicitly accepted.
        /// </summary>
        private bool IsCurrentLegalDocumentAccepted()
        {
            var document = GetCurrentLegalDocument();
            return document is not null && _acceptedLegalDocumentIds.Contains(document.Id);
        }
    }
}
