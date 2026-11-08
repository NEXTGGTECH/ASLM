// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.Windows.Input;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Repository documentation part of the module information view. The documentation is optional:
    /// installation and configuration never wait for these requests.
    /// </summary>
    public partial class ModuleInfo
    {
        // Fields

        /// <summary>
        /// Cancellation source of the running document list request, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _documentListCts;

        /// <summary>
        /// Cancellation source of the running request for the content of the selected document, or null when none runs.
        /// </summary>
        private CancellationTokenSource? _documentContentCts;

        /// <summary>
        /// Whether the document list of the shown module was loaded successfully, so it is not requested again.
        /// </summary>
        private bool _documentsLoaded;

        /// <summary>
        /// Tab of the document whose content is shown or being loaded, or null when none is selected.
        /// </summary>
        private DocumentTab? _selectedDocument;

        /// <summary>
        /// Error message of the last failed list or content request, or null when there is none.
        /// </summary>
        private string? _documentError;

        /// <summary>
        /// Whether the selected document produced content that is shown in the preview.
        /// </summary>
        private bool _hasDocumentContent;


        // Document data

        /// <summary>
        /// Selectors for the repository documents that were found, one per accepted root file.
        /// </summary>
        public ObservableCollection<DocumentTab> Documents { get; } = [];

        /// <summary>
        /// Whether there is more than one document, which makes the selector row visible.
        /// </summary>
        public bool HasMultipleDocuments => Documents.Count > 1;

        /// <summary>
        /// Whether a document is selected.
        /// </summary>
        public bool HasSelectedDocument => _selectedDocument != null;

        /// <summary>
        /// File name of the selected document, or an empty string when none is selected.
        /// </summary>
        public string SelectedDocumentName => _selectedDocument?.Name ?? string.Empty;

        /// <summary>
        /// Whether the last document request failed.
        /// </summary>
        public bool HasDocumentError => _documentError != null;

        /// <summary>
        /// Localized failure message of the last document request, or an empty string when there is none.
        /// </summary>
        public string DocumentError => _documentError == null ? string.Empty : L.Get(LocalizationKeys.ModuleInfo_Failed, _documentError);

        /// <summary>
        /// Whether the preview has document content to show.
        /// </summary>
        public bool HasDocumentContent => _hasDocumentContent;

        /// <summary>
        /// Whether the documentation section is shown at all: documents were found or a request failed.
        /// </summary>
        public bool HasDocumentSection => Documents.Count > 0 || HasDocumentError;


        // Document lifecycle

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
            // One list request at a time, and only for an open overlay.
            if (_module == null || !_isOpen || _documentListCts != null) return;

            // Remember which opening this request belongs to, so a later opening can reject its results.
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
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The request was canceled by a newer opening or by closing the overlay, so nothing is reported.
            }
            catch (Exception ex)
            {
                // Only a failure of the current request is shown; stale ones are ignored.
                if (!cts.IsCancellationRequested && _isOpen && version == _openVersion) _documentError = ex.Message;
            }
            finally
            {
                // Release the request slot only if a newer request has not taken it over.
                if (ReferenceEquals(_documentListCts, cts))
                {
                    _documentListCts = null;
                    RaiseDocumentProperties();
                }
            }

            // Show the first document (the list puts the README first) when the request is still current.
            if (!cts.IsCancellationRequested && _isOpen && version == _openVersion && Documents.FirstOrDefault() is { } first)
                await SelectDocumentAsync(first);
        }

        /// <summary>
        /// Fetches only the selected file and rejects responses belonging to an earlier selection.
        /// </summary>
        private async Task SelectDocumentAsync(DocumentTab tab)
        {
            // Nothing to do for a closed overlay or for a tab whose content is already shown or being loaded.
            if (_module == null || !_isOpen) return;
            if (ReferenceEquals(_selectedDocument, tab) && (_hasDocumentContent || _documentContentCts != null)) return;

            // A newer selection cancels the request of the previous one; the version guards against a later opening.
            _documentContentCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _documentContentCts = cts;
            var module = _module;
            var version = _openVersion;

            // Show the tab as selected at once and clear the preview until its content arrives.
            _selectedDocument = tab;
            _documentError = null;
            _hasDocumentContent = false;
            DocumentPreview.SetDocument(null);
            RaiseDocumentProperties();
            try
            {
                var html = await _updates.GetModuleDocumentHtmlAsync(module, tab.Document, cts.Token);

                // Reject a response that belongs to an earlier selection, a closed overlay or another opening.
                if (cts.IsCancellationRequested || !_isOpen || version != _openVersion) return;
                _hasDocumentContent = !string.IsNullOrWhiteSpace(html);
                DocumentPreview.SetDocument(_hasDocumentContent ? html : null);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // The request was replaced or the overlay closed, so nothing is reported.
            }
            catch (Exception ex)
            {
                // Only a failure of the current request is shown; stale ones are ignored.
                if (!cts.IsCancellationRequested && _isOpen && version == _openVersion) _documentError = ex.Message;
            }
            finally
            {
                // Release the request slot only if a newer selection has not taken it over.
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
            // A selected document means the content request failed; otherwise the list itself could not be loaded.
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


        // Nested types

        /// <summary>
        /// A filename-labelled selector; conventional repository filenames are data rather than translated UI captions.
        /// </summary>
        public sealed class DocumentTab
        {
            /// <summary>
            /// Repository file that this tab selects.
            /// </summary>
            internal ModuleRepositoryDocument Document { get; }

            /// <summary>
            /// File name shown on the tab.
            /// </summary>
            public string Name => Document.Name;

            /// <summary>
            /// Command that selects this document and loads its content.
            /// </summary>
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
}
