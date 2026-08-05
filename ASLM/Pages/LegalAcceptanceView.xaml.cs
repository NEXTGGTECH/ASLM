// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages;

/// <summary>
/// Blocking overlay that presents required legal documents until they are accepted.
/// </summary>
public partial class LegalAcceptanceView : ContentView, ILocalizable
{
    private readonly LegalAcceptanceService _legalAcceptance;

    private readonly HashSet<string> _acceptedLegalDocumentIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LegalDocument> _allDocuments = [];
    private IReadOnlyList<LegalDocument> _legalDocuments = [];
    private int _step;
    private bool _isRendering;
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
    /// Applies localized labels for the legal acceptance flow.
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
        _acceptedLegalDocumentIds.Clear();
        _allDocuments = [];
        _legalDocuments = [];
        _step = 0;
        _showLegalRequired = false;
        FooterLabel.Text = string.Empty;

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

        if (!IsCurrentLegalDocumentAccepted())
        {
            _showLegalRequired = true;
            FooterLabel.Text = L.Get(LocalizationKeys.Legal_AcceptRequired);
            RenderLegalDocumentValidation();
            return;
        }

        _showLegalRequired = false;

        if (_step < _legalDocuments.Count - 1)
        {
            _step++;
            RenderStep();
            return;
        }

        await SaveAcceptanceAsync();
    }

    /// <summary>
    /// Moves back to the previous legal document step.
    /// </summary>
    private void OnBackClicked(object? sender, EventArgs e)
    {
        FooterLabel.Text = string.Empty;
        _step = Math.Max(_step - 1, 0);
        RenderStep();
    }

    /// <summary>
    /// Exits the application when the user declines the legal documents.
    /// </summary>
    private void OnDeclineClicked(object? sender, EventArgs e)
    {
        Application.Current?.Quit();
    }

    /// <summary>
    /// Tracks acceptance for the currently displayed legal document.
    /// </summary>
    private void OnAcceptLegalChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (_isRendering)
        {
            return;
        }

        var document = GetCurrentLegalDocument();
        if (document is null)
        {
            return;
        }

        if (e.Value)
        {
            _acceptedLegalDocumentIds.Add(document.Id);
            _showLegalRequired = false;
        }
        else
        {
            _acceptedLegalDocumentIds.Remove(document.Id);
        }

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
        if (_allDocuments.Count > 0)
        {
            return;
        }

        try
        {
            _allDocuments = await _legalAcceptance.LoadDocumentsAsync();
            _legalDocuments = _legalAcceptance.HasStoredAcceptance
                ? _legalAcceptance.GetPendingDocuments(_allDocuments)
                : _allDocuments;
        }
        catch (Exception ex)
        {
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

            await _legalAcceptance.MergeAcceptedDocumentsAsync(_allDocuments, acceptedDocuments);
            AcceptanceCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
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
        _isRendering = true;

        BackButton.IsVisible = _step > 0;
        BackButton.IsEnabled = _step > 0;
        NextButton.IsEnabled = true;

        var totalSteps = Math.Max(_legalDocuments.Count, 1);
        StepLabel.Text = L.Get(LocalizationKeys.Legal_StepFormat, _step + 1, totalSteps);

        var document = GetCurrentLegalDocument();
        LegalDocumentTitleLabel.Text = document?.Title ?? string.Empty;
        LegalDocumentEditor.Text = document?.Markdown ?? string.Empty;

        var isAccepted = document is not null && _acceptedLegalDocumentIds.Contains(document.Id);
        AcceptLegalCheckBox.IsChecked = isAccepted;
        AcceptLegalLabel.Text = document is null
            ? string.Empty
            : L.Get(LocalizationKeys.Legal_AcceptDocumentFormat, document.Title);

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
