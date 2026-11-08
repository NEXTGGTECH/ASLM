// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    // Startup resolution

    /// <summary>
    /// Describes how startup legal acceptance was resolved.
    /// </summary>
    public enum LegalAcceptanceResolution
    {
        /// <summary>
        /// All bundled documents are already accepted at their current content hash.
        /// </summary>
        UpToDate,

        /// <summary>
        /// New or updated documents were accepted automatically without showing the overlay.
        /// </summary>
        AutoAccepted,

        /// <summary>
        /// The user must review and accept documents in the blocking overlay.
        /// </summary>
        ManualRequired
    }


    // Acceptance service

    /// <summary>
    /// Loads bundled legal documents and persists user acceptance in <c>Data/App/ASLM_LegalAcceptance.json</c>.
    /// A document counts as accepted only while its content hash matches the hash that was accepted.
    /// </summary>
    public class LegalAcceptanceService
    {
        // Fields

        /// <summary>
        /// Name of the embedded resource that holds the generated legal document bundle.
        /// </summary>
        private const string LegalDocumentsFileName = "legal-documents.json";

        /// <summary>
        /// Name of the acceptance file inside <c>Data/App</c>.
        /// </summary>
        private const string LegalAcceptanceFileName = "ASLM_LegalAcceptance.json";

        /// <summary>
        /// Absolute path of the persisted acceptance file.
        /// </summary>
        private readonly string _acceptanceFilePath;

        /// <summary>
        /// Logger that reports load failures.
        /// </summary>
        private readonly ILogger<LegalAcceptanceService> _logger;

        /// <summary>
        /// JSON settings for the document bundle and the acceptance file (camelCase names, null values omitted).
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };


        // State

        /// <summary>
        /// Gets the currently loaded acceptance data.
        /// </summary>
        public LegalAcceptanceData Data { get; private set; } = new();

        /// <summary>
        /// Gets whether the user has previously accepted at least one legal document.
        /// </summary>
        public bool HasStoredAcceptance => Data.AcceptedDocuments.Count > 0;

        /// <summary>
        /// Gets whether the blocking legal overlay must be shown on the next host page.
        /// </summary>
        public bool ManualAcceptanceRequired { get; private set; }


        // Construction

        /// <summary>
        /// Creates the service and resolves the persisted acceptance file path.
        /// </summary>
        /// <param name="logger">Logger that reports load failures.</param>
        public LegalAcceptanceService(ILogger<LegalAcceptanceService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var rootDir = GetRootDirectory();
            _acceptanceFilePath = Path.Combine(rootDir, "Data", "App", LegalAcceptanceFileName);
        }


        // Initialization

        /// <summary>
        /// Loads persisted acceptance data once at startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadAcceptanceAsync();
        }


        // Bundled documents

        /// <summary>
        /// Loads the generated legal document bundle from app assets.
        /// </summary>
        /// <param name="cancellationToken">Token that cancels reading the bundle.</param>
        /// <exception cref="FileNotFoundException">Thrown when the bundle is not embedded in the assembly.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the bundle contains no documents.</exception>
        public async Task<IReadOnlyList<LegalDocument>> LoadDocumentsAsync(CancellationToken cancellationToken = default)
        {
            // The bundle is an embedded resource of this assembly.
            await using var stream = typeof(LegalAcceptanceService).Assembly.GetManifestResourceStream(LegalDocumentsFileName)
                ?? throw new FileNotFoundException("Embedded legal documents were not found.", LegalDocumentsFileName);
            var documents = await JsonSerializer.DeserializeAsync<List<LegalDocument>>(stream, _jsonOptions, cancellationToken);

            return documents is { Count: > 0 }
                ? documents
                : throw new InvalidOperationException("Legal documents are missing or empty.");
        }


        // Acceptance persistence

        /// <summary>
        /// Loads persisted acceptance data or resets to defaults when the file is missing or invalid.
        /// </summary>
        public async Task LoadAcceptanceAsync()
        {
            try
            {
                // Read and deserialize the existing file when present and non-empty.
                if (File.Exists(_acceptanceFilePath))
                {
                    var json = await File.ReadAllTextAsync(_acceptanceFilePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        Data = JsonSerializer.Deserialize<LegalAcceptanceData>(json, _jsonOptions) ?? new LegalAcceptanceData();
                        Data.AcceptedDocuments ??= [];
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Fall back to defaults when the file cannot be read or parsed.
                _logger.LogError(ex, "Failed to load legal acceptance from {FilePath}. Falling back to defaults.", _acceptanceFilePath);
            }

            // The file is only rewritten when the user (or auto-acceptance) saves a new acceptance.
            Data = new LegalAcceptanceData();
        }

        /// <summary>
        /// Returns bundled documents that were not yet accepted or whose content hash changed.
        /// </summary>
        /// <param name="currentDocuments">Documents of the current bundle.</param>
        public IReadOnlyList<LegalDocument> GetPendingDocuments(IReadOnlyList<LegalDocument> currentDocuments)
        {
            var acceptedById = Data.AcceptedDocuments
                .ToDictionary(document => document.Id, document => document.Sha256, StringComparer.OrdinalIgnoreCase);

            return currentDocuments
                .Where(document =>
                    !acceptedById.TryGetValue(document.Id, out var acceptedHash)
                    || !string.Equals(acceptedHash, document.Sha256, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Returns whether all current legal documents have been accepted at their current content hash.
        /// </summary>
        /// <param name="currentDocuments">Documents of the current bundle; an empty list is never considered up to date.</param>
        public bool IsUpToDate(IReadOnlyList<LegalDocument> currentDocuments)
        {
            if (currentDocuments.Count == 0)
            {
                return false;
            }

            var acceptedById = Data.AcceptedDocuments
                .ToDictionary(document => document.Id, document => document.Sha256, StringComparer.OrdinalIgnoreCase);

            return currentDocuments.All(document =>
                acceptedById.TryGetValue(document.Id, out var acceptedHash)
                && string.Equals(acceptedHash, document.Sha256, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves startup legal acceptance, optionally auto-accepting new and updated documents.
        /// Auto-acceptance applies only when the user accepted documents before and enabled it in the legal settings;
        /// a first start always requires manual acceptance.
        /// </summary>
        /// <param name="appData">Application data store that holds the auto-accept preference.</param>
        public async Task<LegalAcceptanceResolution> ResolveStartupAcceptanceAsync(AppDataStore appData)
        {
            ManualAcceptanceRequired = false;

            // If the bundle cannot be loaded, the user is asked to review the documents instead of skipping the check.
            IReadOnlyList<LegalDocument> currentDocuments;
            try
            {
                currentDocuments = await LoadDocumentsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load bundled legal documents during startup.");
                ManualAcceptanceRequired = true;
                return LegalAcceptanceResolution.ManualRequired;
            }

            // Nothing changed since the last acceptance.
            if (IsUpToDate(currentDocuments))
            {
                return LegalAcceptanceResolution.UpToDate;
            }

            // A first run has no stored acceptance and must always be confirmed by the user.
            if (!HasStoredAcceptance)
            {
                ManualAcceptanceRequired = true;
                return LegalAcceptanceResolution.ManualRequired;
            }

            // Documents were added or changed: accept silently when the user allowed it, otherwise ask.
            appData.Data.Legal.Normalize();
            if (appData.Data.Legal.AutoAcceptUpdates)
            {
                await SaveDocumentsAsAcceptedAsync(currentDocuments);
                return LegalAcceptanceResolution.AutoAccepted;
            }

            ManualAcceptanceRequired = true;
            return LegalAcceptanceResolution.ManualRequired;
        }

        /// <summary>
        /// Clears the manual overlay requirement after the user accepts documents in the UI.
        /// </summary>
        public void ClearManualAcceptanceRequired()
        {
            ManualAcceptanceRequired = false;
        }

        /// <summary>
        /// Merges newly accepted pending documents with unchanged prior acceptance records.
        /// Records of documents that left the bundle are dropped.
        /// </summary>
        /// <param name="allCurrentDocuments">All documents of the current bundle.</param>
        /// <param name="newlyAcceptedPending">Acceptance records for the documents that were pending.</param>
        public async Task MergeAcceptedDocumentsAsync(
            IReadOnlyList<LegalDocument> allCurrentDocuments,
            IReadOnlyList<AcceptedLegalDocument> newlyAcceptedPending)
        {
            // Keep earlier records only for documents that are still bundled and unchanged.
            var pendingIds = GetPendingDocuments(allCurrentDocuments)
                .Select(document => document.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var currentIds = allCurrentDocuments
                .Select(document => document.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var merged = Data.AcceptedDocuments
                .Where(document => currentIds.Contains(document.Id) && !pendingIds.Contains(document.Id))
                .ToList();

            merged.AddRange(newlyAcceptedPending);
            await SaveAcceptanceAsync(merged);
        }

        /// <summary>
        /// Replaces the stored acceptance records and persists them.
        /// </summary>
        /// <param name="acceptedDocuments">Complete set of acceptance records to store.</param>
        public async Task SaveAcceptanceAsync(IReadOnlyList<AcceptedLegalDocument> acceptedDocuments)
        {
            Data = new LegalAcceptanceData
            {
                AcceptedDocuments = acceptedDocuments.ToList()
            };

            EnsureDirectoryExists();

            var json = JsonSerializer.Serialize(Data, _jsonOptions);
            await File.WriteAllTextAsync(_acceptanceFilePath, json);
        }

        /// <summary>
        /// Persists every bundled document as accepted at the current UTC time.
        /// </summary>
        /// <param name="documents">Documents to record as accepted, with their current content hashes.</param>
        public async Task SaveDocumentsAsAcceptedAsync(IReadOnlyList<LegalDocument> documents)
        {
            var acceptedAtUtc = DateTimeOffset.UtcNow;
            var acceptedDocuments = documents
                .Select(document => new AcceptedLegalDocument(
                    document.Id,
                    document.Title,
                    document.FileName,
                    document.Sha256,
                    acceptedAtUtc))
                .ToList();

            await SaveAcceptanceAsync(acceptedDocuments);
        }


        // File system helpers

        /// <summary>
        /// Ensures the parent directory for the acceptance file exists.
        /// </summary>
        private void EnsureDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_acceptanceFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Returns the ASLM data root directory that contains the <c>Data</c> folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
