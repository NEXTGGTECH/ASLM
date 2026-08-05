// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models;

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
public sealed record LegalDocument(
    string Id,
    string Title,
    string FileName,
    string Markdown,
    string Sha256);

/// <summary>
/// Records one accepted legal document and the content hash that was accepted.
/// </summary>
public sealed record AcceptedLegalDocument(
    string Id,
    string Title,
    string FileName,
    string Sha256,
    DateTimeOffset AcceptedAtUtc);
