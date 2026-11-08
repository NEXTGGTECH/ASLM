// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;

namespace ASLM.Installer
{
    // Legal document service

    /// <summary>
    /// Reads legal documents generated from markdown files during build.
    /// </summary>
    public sealed class LegalDocumentService
    {
        // Fields and initialization

        /// <summary>
        /// Name of the app asset that contains the generated legal document bundle.
        /// </summary>
        private const string LegalDocumentsFileName = "legal-documents.json";

        /// <summary>
        /// Opens an app asset by file name; replaced in tests so no packaged assets are needed.
        /// </summary>
        private readonly Func<string, Task<Stream>> _openAsset;

        /// <summary>
        /// Creates a service that reads legal documents from packaged app assets.
        /// </summary>
        public LegalDocumentService()
            : this(openAsset: null)
        {
        }

        /// <summary>
        /// Creates a service with a test-injected app-asset source.
        /// </summary>
        /// <param name="openAsset">Opens an app asset by file name; null uses the MAUI app package.</param>
        internal LegalDocumentService(Func<string, Task<Stream>>? openAsset)
        {
            _openAsset = openAsset ?? OpenPackagedAssetAsync;
        }


        // Asset access

        /// <summary>
        /// Loads the generated legal document bundle from app assets.
        /// </summary>
        /// <param name="cancellationToken">Cancels reading and parsing the bundle.</param>
        /// <returns>The legal documents in the order in which the wizard shows them.</returns>
        /// <exception cref="InvalidOperationException">The bundle is missing or contains no documents.</exception>
        public async Task<IReadOnlyList<LegalDocument>> LoadAsync(CancellationToken cancellationToken = default)
        {
            await using var stream = await _openAsset(LegalDocumentsFileName);
            var documents = await JsonSerializer.DeserializeAsync<List<LegalDocument>>(stream, JsonOptions.Default, cancellationToken);

            return documents is { Count: > 0 }
                ? documents
                : throw new InvalidOperationException("Installer legal documents are missing or empty.");
        }

        /// <summary>
        /// Opens one packaged app asset, used only when no injected source exists.
        /// </summary>
        private static Task<Stream> OpenPackagedAssetAsync(string fileName)
        {
            return FileSystem.OpenAppPackageFileAsync(fileName);
        }
    }


    // Legal document data

    /// <summary>
    /// Describes one legal document shown before installation.
    /// </summary>
    /// <param name="Id">Stable identifier of the document, used to track its acceptance.</param>
    /// <param name="Title">Title shown in the wizard.</param>
    /// <param name="FileName">Name of the markdown file the document was generated from.</param>
    /// <param name="Markdown">Full document text displayed to the user.</param>
    /// <param name="Sha256">SHA-256 hash of the document.</param>
    public sealed record LegalDocument(
        string Id,
        string Title,
        string FileName,
        string Markdown,
        string Sha256);


    // JSON settings

    /// <summary>
    /// Provides shared JSON options for installer metadata.
    /// </summary>
    internal static class JsonOptions
    {
        /// <summary>
        /// Camel-case, indented options used to read the legal bundle and to write the manifest and acceptance files.
        /// </summary>
        public static readonly JsonSerializerOptions Default = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
    }
}
