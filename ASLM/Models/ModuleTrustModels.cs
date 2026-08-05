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
    /// One official module identity bound to an expected source repository.
    /// </summary>
    public sealed class OfficialModuleTrustEntry
    {
        /// <summary>
        /// Creates an official module trust entry.
        /// </summary>
        public OfficialModuleTrustEntry(string id, string repo)
        {
            Id = ModuleTrustIdentity.NormalizeId(id);
            Repo = ModuleTrustIdentity.NormalizeRepo(repo);
        }

        /// <summary>
        /// Gets the stable module identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the expected GitHub repository path.
        /// </summary>
        public string Repo { get; }
    }


    // Reviewed list entry

    /// <summary>
    /// One community-reviewed module identity from the signed remote list.
    /// </summary>
    public sealed class ReviewedModuleTrustEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes identity fields after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id = ModuleTrustIdentity.NormalizeId(Id);
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
        /// Normalizes payload fields after JSON deserialization.
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
                .Where(module => module != null && !string.IsNullOrWhiteSpace(module.Id) && !string.IsNullOrWhiteSpace(module.Repo))
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
    /// Canonical unsigned reviewed-modules payload used for Ed25519 verification.
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
    /// Normalizes module trust identity fields for comparisons.
    /// </summary>
    public static class ModuleTrustIdentity
    {
        /// <summary>
        /// Normalizes a module id for trust comparisons.
        /// </summary>
        public static string NormalizeId(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();

        /// <summary>
        /// Normalizes a repository path for trust comparisons.
        /// </summary>
        public static string NormalizeRepo(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().Trim('/').ToLowerInvariant();
        }

        /// <summary>
        /// Returns whether a module config matches the provided id and repo.
        /// </summary>
        public static bool Matches(ModuleConfig config, string id, string repo)
        {
            config.Normalize();
            return string.Equals(NormalizeId(config.Id), id, StringComparison.Ordinal) &&
                   string.Equals(NormalizeRepo(config.Source.Repo), repo, StringComparison.Ordinal);
        }
    }
}
