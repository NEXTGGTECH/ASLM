// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    /// <summary>
    /// Describes the shipped ASLM update source.
    /// </summary>
    public sealed class AppUpdateSourceConfig
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("source")]
        public ModuleSource Source { get; set; } = new();

        [JsonPropertyName("defaultChannel")]
        public string DefaultChannel { get; set; } = "release";

        [JsonPropertyName("assets")]
        public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

        /// <summary>
        /// Restores safe defaults and canonical source values after deserialization.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion == 0)
            {
                FileVersion = 1;
            }

            Source ??= new();
            Source.Normalize();
            DefaultChannel = string.Equals(DefaultChannel, "pre-release", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(DefaultChannel, "prerelease", StringComparison.OrdinalIgnoreCase)
                ? "pre-release"
                : "release";

            Assets ??= new(StringComparer.OrdinalIgnoreCase);
            Assets = Assets
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

            Preserve ??= [];
            Preserve = Preserve
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim().Replace('\\', '/').Trim('/'))
                .Where(static item => item.Length > 0 && item != "." && !item.Contains("..", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Represents one remote update available for ASLM or a module.
    /// </summary>
    public sealed class UpdateCandidate
    {
        public string TargetKind { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string RemoteVersion { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string? ReferenceName { get; set; }
        public string? ReleaseTag { get; set; }
        public string? CommitSha { get; set; }
        public bool IsVirtualLatest { get; set; }
        public bool IsPrerelease { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public ModuleConfig? Module { get; set; }
        public EngineConfig? Engine { get; set; }
    }

    /// <summary>
    /// Stores the actionable part of a discovered update across application restarts.
    /// </summary>
    public sealed class PersistedUpdateCandidate
    {
        [JsonPropertyName("targetKind")]
        public string TargetKind { get; set; } = string.Empty;

        [JsonPropertyName("targetId")]
        public string TargetId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("remoteVersion")]
        public string RemoteVersion { get; set; } = string.Empty;

        [JsonPropertyName("channel")]
        public string Channel { get; set; } = "release";

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("releaseTag")]
        public string? ReleaseTag { get; set; }

        [JsonPropertyName("isPrerelease")]
        public bool IsPrerelease { get; set; }

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>
        /// Normalizes persisted candidate values and rejects incomplete snapshots.
        /// </summary>
        public bool Normalize()
        {
            TargetKind = TargetKind?.Trim() ?? string.Empty;
            TargetId = TargetId?.Trim() ?? string.Empty;
            Name = Name?.Trim() ?? string.Empty;
            RemoteVersion = RemoteVersion?.Trim() ?? string.Empty;
            Channel = string.Equals(Channel, "pre-release", StringComparison.OrdinalIgnoreCase)
                ? "pre-release"
                : "release";
            DownloadUrl = DownloadUrl?.Trim() ?? string.Empty;
            ReleaseTag = string.IsNullOrWhiteSpace(ReleaseTag) ? null : ReleaseTag.Trim();
            return TargetKind.Length > 0 &&
                   TargetId.Length > 0 &&
                   RemoteVersion.Length > 0 &&
                   DownloadUrl.Length > 0;
        }

        /// <summary>
        /// Creates a compact persisted snapshot from one discovered update.
        /// </summary>
        public static PersistedUpdateCandidate FromCandidate(UpdateCandidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            return new PersistedUpdateCandidate
            {
                TargetKind = candidate.TargetKind,
                TargetId = candidate.TargetId,
                Name = candidate.Name,
                RemoteVersion = candidate.RemoteVersion,
                Channel = candidate.Channel,
                DownloadUrl = candidate.DownloadUrl,
                ReleaseTag = candidate.ReleaseTag,
                IsPrerelease = candidate.IsPrerelease,
                PublishedAt = candidate.PublishedAt
            };
        }

        /// <summary>
        /// Rebuilds an actionable candidate and attaches current engine metadata when required.
        /// </summary>
        public UpdateCandidate ToCandidate(EngineConfig? engine = null)
        {
            return new UpdateCandidate
            {
                TargetKind = TargetKind,
                TargetId = TargetId,
                Name = Name,
                DisplayName = Name,
                CurrentVersion = engine?.Status.InstalledReleaseTag ?? string.Empty,
                RemoteVersion = RemoteVersion,
                Channel = Channel,
                Mode = "release",
                DownloadUrl = DownloadUrl,
                ReleaseTag = ReleaseTag,
                IsPrerelease = IsPrerelease,
                PublishedAt = PublishedAt,
                Engine = engine
            };
        }
    }

    /// <summary>
    /// Stores the pending self-update operation consumed by the external patcher.
    /// </summary>
    public sealed class PendingAppUpdate
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "app";

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("stagingPath")]
        public string StagingPath { get; set; } = string.Empty;

        [JsonPropertyName("targetRoot")]
        public string TargetRoot { get; set; } = string.Empty;

        [JsonPropertyName("backupPath")]
        public string BackupPath { get; set; } = string.Empty;

        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

        [JsonPropertyName("createdUtc")]
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");

        /// <summary>
        /// Restores safe pending-update values after deserialization.
        /// </summary>
        public void Normalize()
        {
            Kind = string.IsNullOrWhiteSpace(Kind) ? "app" : Kind.Trim();
            Version ??= string.Empty;
            StagingPath ??= string.Empty;
            TargetRoot ??= string.Empty;
            BackupPath ??= string.Empty;
            CreatedUtc = string.IsNullOrWhiteSpace(CreatedUtc) ? DateTime.UtcNow.ToString("o") : CreatedUtc;
            Preserve ??= [];
        }
    }

    /// <summary>
    /// Describes one GitHub branch returned for a module repository.
    /// </summary>
    public sealed record GitHubBranchInfo(string Name, string CommitSha);
}
