// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Windows.Input;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages;

public partial class ModuleInfo
{
    // Optional repository documentation; installation and configuration never wait for these requests.

    private CancellationTokenSource? _documentListCts;
    private CancellationTokenSource? _documentContentCts;
    private bool _documentsLoaded;
    private DocumentTab? _selectedDocument;
    private string? _documentError;
    private bool _hasDocumentContent;

    public ObservableCollection<DocumentTab> Documents { get; } = [];
    public bool HasMultipleDocuments => Documents.Count > 1;
    public bool HasSelectedDocument => _selectedDocument != null;
    public string SelectedDocumentName => _selectedDocument?.Name ?? string.Empty;
    public bool HasDocumentError => _documentError != null;
    public string DocumentError => _documentError == null ? string.Empty : L.Get(LocalizationKeys.ModuleInfo_Failed, _documentError);
    public bool HasDocumentContent => _hasDocumentContent;
    public bool HasDocumentSection => Documents.Count > 0 || HasDocumentError;

    /// <summary>
    /// Clears the previous module's documents before exposing a new module in the overlay.
    /// </summary>
    private void ResetDocuments()
    {
        CancelDocumentRequests();
        _documentsLoaded = false;
        _selectedDocument = null;
        _documentError = null;
        _hasDocumentContent = false;
        Documents.Clear();
        DocumentPreview.SetDocument(null);
        RaiseDocumentProperties();
    }

    /// <summary>
    /// Cancels view-owned requests without interfering with shared installation/update work.
    /// </summary>
    private void CancelDocumentRequests()
    {
        // Each async owner disposes its own token source after observing cancellation.
        _documentListCts?.Cancel();
        _documentContentCts?.Cancel();
        _documentListCts = null;
        _documentContentCts = null;
    }

    /// <summary>
    /// Lists permitted root files and selects README (or the first available document) without blocking opening.
    /// </summary>
    private async Task LoadDocumentsAsync()
    {
        if (_module == null || !_isOpen || _documentListCts != null) return;
        var module = _module;
        var version = _openVersion;
        using var cts = new CancellationTokenSource();
        _documentListCts = cts;
        _documentError = null;
        RaiseDocumentProperties();
        try
        {
            var documents = await _updates.GetModuleDocumentsAsync(module, cts.Token);
            // A different module, closed overlay or newer retry must not receive these results.
            if (cts.IsCancellationRequested || !_isOpen || version != _openVersion) return;
            Documents.Clear();
            foreach (var document in documents) Documents.Add(new DocumentTab(document, SelectDocumentAsync));
            _documentsLoaded = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _isOpen && version == _openVersion) _documentError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_documentListCts, cts))
            {
                _documentListCts = null;
                RaiseDocumentProperties();
            }
        }
        if (!cts.IsCancellationRequested && _isOpen && version == _openVersion && Documents.FirstOrDefault() is { } first)
            await SelectDocumentAsync(first);
    }

    /// <summary>
    /// Fetches only the selected file and rejects responses belonging to an earlier selection.
    /// </summary>
    private async Task SelectDocumentAsync(DocumentTab tab)
    {
        if (_module == null || !_isOpen) return;
        if (ReferenceEquals(_selectedDocument, tab) && (_hasDocumentContent || _documentContentCts != null)) return;
        _documentContentCts?.Cancel();
        using var cts = new CancellationTokenSource();
        _documentContentCts = cts;
        var module = _module;
        var version = _openVersion;
        _selectedDocument = tab;
        _documentError = null;
        _hasDocumentContent = false;
        DocumentPreview.SetDocument(null);
        RaiseDocumentProperties();
        try
        {
            var html = await _updates.GetModuleDocumentHtmlAsync(module, tab.Document, cts.Token);
            if (cts.IsCancellationRequested || !_isOpen || version != _openVersion) return;
            _hasDocumentContent = !string.IsNullOrWhiteSpace(html);
            DocumentPreview.SetDocument(_hasDocumentContent ? html : null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && _isOpen && version == _openVersion) _documentError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_documentContentCts, cts))
            {
                _documentContentCts = null;
                RaiseDocumentProperties();
            }
        }
    }

    /// <summary>
    /// Retries a failed directory or document request without reloading the rest of the module information.
    /// </summary>
    private async void OnDocumentRetryClicked(object? sender, EventArgs e)
    {
        if (_selectedDocument is { } selected) await SelectDocumentAsync(selected);
        else await LoadDocumentsAsync();
    }

    /// <summary>
    /// Updates only the repository-document section after a selection, localization or network-state change.
    /// </summary>
    private void RaiseDocumentProperties()
    {
        foreach (var property in new[] { nameof(HasMultipleDocuments), nameof(HasSelectedDocument), nameof(SelectedDocumentName),
                     nameof(HasDocumentError), nameof(DocumentError), nameof(HasDocumentContent), nameof(HasDocumentSection) })
            OnPropertyChanged(property);
    }

    /// <summary>
    /// A filename-labelled selector; conventional repository filenames are data rather than translated UI captions.
    /// </summary>
    public sealed class DocumentTab
    {
        internal ModuleRepositoryDocument Document { get; }
        public string Name => Document.Name;
        public ICommand SelectCommand { get; }

        /// <summary>
        /// Binds one accepted root file to the owning view's asynchronous selection handler.
        /// </summary>
        internal DocumentTab(ModuleRepositoryDocument document, Func<DocumentTab, Task> select)
        {
            Document = document;
            SelectCommand = new Command(async () => await select(this));
        }
    }
}
