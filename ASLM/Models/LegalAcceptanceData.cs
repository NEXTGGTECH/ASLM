// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    // Legal acceptance

    /// <summary>
    /// Persists the user's acceptance of legal documents in <c>Data/App/ASLM_LegalAcceptance.json</c>.
    /// </summary>
    public sealed class LegalAcceptanceData
    {
        /// <summary>
        /// Gets or sets the accepted legal documents.
        /// </summary>
        public List<AcceptedLegalDocument> AcceptedDocuments { get; set; } = [];
    }

    /// <summary>
    /// Describes one legal document bundled with the application.
    /// </summary>
    /// <param name="Id">Stable document identifier that links a document to its recorded acceptance.</param>
    /// <param name="Title">Document title shown in the review overlay.</param>
    /// <param name="FileName">Name of the source file the document was generated from.</param>
    /// <param name="Markdown">Document text in Markdown format.</param>
    /// <param name="Sha256">SHA-256 hash of the document content; a changed hash requires a new acceptance.</param>
    public sealed record LegalDocument(
        string Id,
        string Title,
        string FileName,
        string Markdown,
        string Sha256);

    /// <summary>
    /// Records one accepted legal document and the content hash that was accepted.
    /// </summary>
    /// <param name="Id">Identifier of the accepted <see cref="LegalDocument"/>.</param>
    /// <param name="Title">Document title at the time of acceptance.</param>
    /// <param name="FileName">Name of the document source file at the time of acceptance.</param>
    /// <param name="Sha256">
    /// Content hash that was accepted; a document with a different hash is not accepted yet.
    /// </param>
    /// <param name="AcceptedAtUtc">Moment of acceptance in UTC.</param>
    public sealed record AcceptedLegalDocument(
        string Id,
        string Title,
        string FileName,
        string Sha256,
        DateTimeOffset AcceptedAtUtc);
}
