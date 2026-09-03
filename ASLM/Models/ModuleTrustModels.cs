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
        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

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
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("reviewedListUrl")]
        public string? ReviewedListUrl { get; set; }

        [JsonPropertyName("publicKeyBase64")]
        public string? PublicKeyBase64 { get; set; }

        [JsonPropertyName("refreshIntervalHours")]
        public int RefreshIntervalHours { get; set; } = 24;

        /// <summary>
        /// Restores defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            ReviewedListUrl = string.IsNullOrWhiteSpace(ReviewedListUrl) ? null : ReviewedListUrl.Trim();
            PublicKeyBase64 = string.IsNullOrWhiteSpace(PublicKeyBase64) ? null : PublicKeyBase64.Trim();

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
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("issuedAt")]
        public string IssuedAt { get; set; } = string.Empty;

        [JsonPropertyName("modules")]
        public List<ReviewedModuleTrustEntry> Modules { get; set; } = [];

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes and removes invalid trust rules after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            IssuedAt = string.IsNullOrWhiteSpace(IssuedAt) ? string.Empty : IssuedAt.Trim();
            Signature = string.IsNullOrWhiteSpace(Signature) ? string.Empty : Signature.Trim();
            Modules ??= [];

            foreach (var module in Modules)
            {
                module?.Normalize();
            }

            Modules = Modules
                .Where(module => module != null && ModuleTrustIdentity.IsValidRule(
                    module.Source,
                    module.Id,
                    module.Repo))
                .ToList();
        }

        /// <summary>
        /// Builds the unsigned body used for signature verification.
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
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("issuedAt")]
        public string IssuedAt { get; set; } = string.Empty;

        [JsonPropertyName("modules")]
        public List<ReviewedModuleTrustEntry> Modules { get; set; } = [];
    }


    // Reviewed modules cache

    /// <summary>
    /// Persisted cache of the last successfully verified community-reviewed list.
    /// </summary>
    public sealed class ReviewedModulesCacheDocument
    {
        [JsonPropertyName("fetchedAt")]
        public string FetchedAt { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public SignedReviewedModulesPayload Payload { get; set; } = new();

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;
    }


    // Identity normalization

    /// <summary>
    /// Normalizes and compares module trust identities.
    /// </summary>
    public static class ModuleTrustIdentity
    {
        public const string GitHubSource = "github";

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

        /// <summary>
        /// Returns whether a rule contains a complete exact identity or GitHub author pattern.
        /// </summary>
        public static bool IsValidRule(string? source, string? id, string? repo)
        {
            var normalizedSource = NormalizeSource(source);
            var normalizedId = NormalizeOptionalId(id);
            var normalizedRepo = NormalizeRepo(repo);
            if (string.IsNullOrEmpty(normalizedSource) || string.IsNullOrEmpty(normalizedRepo))
            {
                return false;
            }

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
