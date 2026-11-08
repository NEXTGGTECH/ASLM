// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Module trust level

    /// <summary>
    /// Describes how much trust ASLM assigns to an installed module package.
    /// </summary>
    public enum ModuleTrustLevel
    {
        /// <summary>
        /// Module is developed and guaranteed by NGGT (official catalog match).
        /// </summary>
        Official,

        /// <summary>
        /// Module is on the signed community-reviewed list.
        /// </summary>
        CommunityReviewed,

        /// <summary>
        /// Module has not been verified or community-reviewed.
        /// </summary>
        Unreviewed
    }


    // Official catalog entry

    /// <summary>
    /// One official exact-module or source-author trust rule.
    /// </summary>
    public sealed class OfficialModuleTrustEntry
    {
        /// <summary>
        /// Creates and normalizes one official trust rule.
        /// </summary>
        /// <param name="source">Expected source provider, for example <c>github</c>.</param>
        /// <param name="id">Exact module id, or null for a source-author rule.</param>
        /// <param name="repo">Exact repository or an <c>Author/*</c> pattern.</param>
        public OfficialModuleTrustEntry(string source, string? id, string repo)
        {
            Source = ModuleTrustIdentity.NormalizeSource(source);
            Id = ModuleTrustIdentity.NormalizeOptionalId(id);
            Repo = ModuleTrustIdentity.NormalizeRepo(repo);
        }

        /// <summary>
        /// Gets the expected source provider.
        /// </summary>
        public string Source { get; }

        /// <summary>
        /// Gets the exact module id, or null for an author rule.
        /// </summary>
        public string? Id { get; }

        /// <summary>
        /// Gets an exact repository or an <c>Author/*</c> pattern.
        /// </summary>
        public string Repo { get; }
    }


    // Reviewed list entry

    /// <summary>
    /// One community-reviewed exact-module or source-author trust rule.
    /// </summary>
    public sealed class ReviewedModuleTrustEntry
    {
        /// <summary>
        /// Gets or sets the expected source provider, for example <c>github</c>.
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the exact module id, or null for an author rule.
        /// </summary>
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        /// <summary>
        /// Gets or sets an exact repository or an <c>Author/*</c> pattern.
        /// </summary>
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes identity fields after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Source = ModuleTrustIdentity.NormalizeSource(Source);
            Id = ModuleTrustIdentity.NormalizeOptionalId(Id);
            Repo = ModuleTrustIdentity.NormalizeRepo(Repo);
        }
    }


    // Shipped trust source config

    /// <summary>
    /// Shipped configuration for loading the signed community-reviewed module list.
    /// </summary>
    public sealed class ModuleTrustSourceConfig
    {
        /// <summary>
        /// Gets or sets the version of the config file schema.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Gets or sets the URL of the signed community-reviewed list; null disables remote loading.
        /// </summary>
        [JsonPropertyName("reviewedListUrl")]
        public string? ReviewedListUrl { get; set; }

        /// <summary>
        /// Gets or sets the Base64 SubjectPublicKeyInfo RSA public key that verifies the list signature.
        /// </summary>
        [JsonPropertyName("publicKeyBase64")]
        public string? PublicKeyBase64 { get; set; }

        /// <summary>
        /// Gets or sets the minimum number of hours between remote list refreshes.
        /// </summary>
        [JsonPropertyName("refreshIntervalHours")]
        public int RefreshIntervalHours { get; set; } = 24;

        /// <summary>
        /// Restores defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // A missing or invalid schema version means the first version.
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            // Blank text values are treated as not configured.
            ReviewedListUrl = string.IsNullOrWhiteSpace(ReviewedListUrl) ? null : ReviewedListUrl.Trim();
            PublicKeyBase64 = string.IsNullOrWhiteSpace(PublicKeyBase64) ? null : PublicKeyBase64.Trim();

            // A non-positive interval falls back to the daily default.
            if (RefreshIntervalHours <= 0)
            {
                RefreshIntervalHours = 24;
            }
        }
    }


    // Signed remote payload

    /// <summary>
    /// Signed payload returned by the community-reviewed modules API.
    /// </summary>
    public sealed class SignedReviewedModulesPayload
    {
        /// <summary>
        /// Gets or sets the version of the payload schema.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Gets or sets the timestamp at which the server issued the list.
        /// </summary>
        [JsonPropertyName("issuedAt")]
        public string IssuedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the community-reviewed trust rules.
        /// </summary>
        [JsonPropertyName("modules")]
        public List<ReviewedModuleTrustEntry> Modules { get; set; } = [];

        /// <summary>
        /// Gets or sets the detached Base64 RSA signature over the unsigned body.
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes and removes invalid trust rules after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // A missing or invalid schema version means the first version.
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            // Trim the textual fields; blank values become empty strings.
            IssuedAt = string.IsNullOrWhiteSpace(IssuedAt) ? string.Empty : IssuedAt.Trim();
            Signature = string.IsNullOrWhiteSpace(Signature) ? string.Empty : Signature.Trim();
            Modules ??= [];

            // Normalize each rule first so the validity check below sees canonical identities.
            foreach (var module in Modules)
            {
                module?.Normalize();
            }

            // Keep only rules that form a complete exact identity or a GitHub author pattern.
            Modules = Modules
                .Where(module => module != null && ModuleTrustIdentity.IsValidRule(
                    module.Source,
                    module.Id,
                    module.Repo))
                .ToList();
        }

        /// <summary>
        /// Builds the unsigned body used for signature verification.
        /// The body carries every field except <see cref="Signature"/>.
        /// </summary>
        public ReviewedModulesPayloadBody ToUnsignedBody() =>
            new()
            {
                FileVersion = FileVersion,
                IssuedAt = IssuedAt,
                Modules = Modules
            };
    }

    /// <summary>
    /// Canonical unsigned reviewed-modules payload used for RSA verification.
    /// </summary>
    public sealed class ReviewedModulesPayloadBody
    {
        /// <summary>
        /// Gets or sets the version of the payload schema.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Gets or sets the timestamp at which the server issued the list.
        /// </summary>
        [JsonPropertyName("issuedAt")]
        public string IssuedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the community-reviewed trust rules covered by the signature.
        /// </summary>
        [JsonPropertyName("modules")]
        public List<ReviewedModuleTrustEntry> Modules { get; set; } = [];
    }


    // Reviewed modules cache

    /// <summary>
    /// Persisted cache of the last successfully verified community-reviewed list.
    /// </summary>
    public sealed class ReviewedModulesCacheDocument
    {
        /// <summary>
        /// Gets or sets the UTC timestamp (round-trip format) at which the list was verified and cached.
        /// </summary>
        [JsonPropertyName("fetchedAt")]
        public string FetchedAt { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the cached payload; its signature is checked again every time the cache is loaded.
        /// </summary>
        [JsonPropertyName("payload")]
        public SignedReviewedModulesPayload Payload { get; set; } = new();

        /// <summary>
        /// Gets or sets the signature stored beside the payload; it takes precedence over the payload's own signature.
        /// </summary>
        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }


    // Identity normalization

    /// <summary>
    /// Normalizes and compares module trust identities.
    /// </summary>
    public static class ModuleTrustIdentity
    {
        // Constants

        /// <summary>
        /// Normalized source provider name of GitHub, the only provider that supports author rules.
        /// </summary>
        public const string GitHubSource = "github";


        // Normalization

        /// <summary>
        /// Normalizes a source provider for trust comparisons.
        /// </summary>
        public static string NormalizeSource(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

        /// <summary>
        /// Normalizes a module id for trust comparisons.
        /// </summary>
        public static string NormalizeId(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

        /// <summary>
        /// Normalizes an optional module id while preserving author-only rules.
        /// </summary>
        public static string? NormalizeOptionalId(string? value)
        {
            var normalized = NormalizeId(value);
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        /// <summary>
        /// Normalizes a repository path without repairing malformed identities.
        /// </summary>
        public static string NormalizeRepo(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();


        // Rule validation and matching

        /// <summary>
        /// Returns whether a rule contains a complete exact identity or GitHub author pattern.
        /// </summary>
        public static bool IsValidRule(string? source, string? id, string? repo)
        {
            // A rule needs both a source provider and a repository.
            var normalizedSource = NormalizeSource(source);
            var normalizedId = NormalizeOptionalId(id);
            var normalizedRepo = NormalizeRepo(repo);
            if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(normalizedRepo))
            {
                return false;
            }

            // Author rules (Author/*) are GitHub only and carry no module id; exact rules need an id and no wildcard.
            var isAuthorRule = TrySplitGitHubRepo(normalizedRepo, out _, out var repository) && repository == "*";
            return isAuthorRule
                ? normalizedSource == GitHubSource && normalizedId == null
                : normalizedId != null && !normalizedRepo.Contains('*');
        }

        /// <summary>
        /// Returns whether a module config matches one exact-module or source-author rule.
        /// </summary>
        public static bool Matches(ModuleConfig config, string source, string? id, string repo)
        {
            ArgumentNullException.ThrowIfNull(config);
            config.Normalize();

            // The module must come from the rule's source provider and the rule itself must be well formed.
            var expectedSource = NormalizeSource(source);
            var actualSource = NormalizeSource(config.Source.Type);
            if (!string.Equals(actualSource, expectedSource, StringComparison.Ordinal) ||
                !IsValidRule(expectedSource, id, repo))
            {
                return false;
            }

            // Author patterns trust every GitHub repository owned by that account.
            if (TrySplitGitHubRepo(repo, out var expectedOwner, out var expectedRepository) &&
                expectedRepository == "*")
            {
                return actualSource == GitHubSource &&
                       TrySplitGitHubRepo(config.Source.Repo, out var actualOwner, out var actualRepository) &&
                       actualRepository != "*" &&
                       string.Equals(actualOwner, expectedOwner, StringComparison.Ordinal);
            }

            // Exact rules require the same module id and the same repository.
            return string.Equals(NormalizeId(config.Id), NormalizeId(id), StringComparison.Ordinal) &&
                   string.Equals(NormalizeRepo(config.Source.Repo), NormalizeRepo(repo), StringComparison.Ordinal);
        }

        /// <summary>
        /// Splits an <c>Author/Repository</c> value used by GitHub author rules.
        /// </summary>
        private static bool TrySplitGitHubRepo(string? value, out string owner, out string repository)
        {
            owner = string.Empty;
            repository = string.Empty;

            var normalized = NormalizeRepo(value);
            var parts = normalized.Split('/');
            if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            {
                return false;
            }

            owner = parts[0];
            repository = parts[1];
            return true;
        }
    }
}
