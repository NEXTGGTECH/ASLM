// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Update operation progress

    /// <summary>
    /// Distinguishes preparation, network transfer, and file replacement in a shared update operation.
    /// </summary>
    internal enum UpdateOperationPhase
    {
        /// <summary>
        /// Resolving the release and getting ready; no data has been transferred yet.
        /// </summary>
        Preparing,

        /// <summary>
        /// Transferring the update package from the network.
        /// </summary>
        Downloading,

        /// <summary>
        /// Extracting the package and replacing the installed files.
        /// </summary>
        Applying
    }

    /// <summary>
    /// Carries an immutable, session-only update snapshot independently of the initiating view.
    /// </summary>
    /// <param name="Phase">Current operation stage.</param>
    /// <param name="Version">Release being downloaded or applied.</param>
    /// <param name="DownloadFraction">Known download completion, or null outside a measurable transfer.</param>
    internal sealed record UpdateOperationSnapshot(
        UpdateOperationPhase Phase,
        string Version,
        double? DownloadFraction = null);


    // Update source

    /// <summary>
    /// Describes the shipped ASLM update source.
    /// </summary>
    public sealed class AppUpdateSourceConfig
    {
        // Configuration fields

        /// <summary>
        /// Version of the configuration file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Provider and repository from which ASLM releases are downloaded.
        /// </summary>
        [JsonPropertyName("source")]
        public ModuleSource Source { get; set; } = new();

        /// <summary>
        /// Release channel used when the user has not chosen one: <c>release</c> or <c>pre-release</c>.
        /// </summary>
        [JsonPropertyName("defaultChannel")]
        public string DefaultChannel { get; set; } = "release";

        /// <summary>
        /// Release asset file names by platform key, resolved for the current OS and architecture.
        /// </summary>
        [JsonPropertyName("assets")]
        public Dictionary<string, string> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Relative paths, such as user data folders, that an update must not overwrite or delete.
        /// </summary>
        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];


        // Normalization

        /// <summary>
        /// Restores safe defaults and canonical source values after deserialization.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion == 0)
            {
                FileVersion = 1;
            }

            // Normalize the repository source and map the channel to one of the two supported names.
            Source ??= new();
            Source.Normalize();
            DefaultChannel = string.Equals(DefaultChannel, "pre-release", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(DefaultChannel, "prerelease", StringComparison.OrdinalIgnoreCase)
                ? "pre-release"
                : "release";

            // Keep only asset entries that have both a platform key and a file name.
            Assets ??= new(StringComparer.OrdinalIgnoreCase);
            Assets = Assets
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

            // Reduce preserve entries to clean relative paths and drop anything that could escape the install folder.
            Preserve ??= [];
            Preserve = Preserve
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item.Trim().Replace('\\', '/').Trim('/'))
                .Where(static item => item.Length > 0 && item != "." && !item.Contains("..", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }


    // Update candidates

    /// <summary>
    /// Represents one remote update available for ASLM or a module.
    /// </summary>
    public sealed class UpdateCandidate
    {
        // Target

        /// <summary>
        /// Kind of component to update: <c>app</c>, <c>module</c>, or <c>engine</c>.
        /// </summary>
        public string TargetKind { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the component to update within its kind.
        /// </summary>
        public string TargetId { get; set; } = string.Empty;

        /// <summary>
        /// Component name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Label shown to the user. For modules it is <c>latest</c> for the virtual latest release, otherwise
        /// the remote version; for engines it is the component name; it is empty for ASLM itself.
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;


        // Versions and channel

        /// <summary>
        /// Version that is installed now, or empty when it is not known.
        /// </summary>
        public string CurrentVersion { get; set; } = string.Empty;

        /// <summary>
        /// Version offered by the remote source.
        /// </summary>
        public string RemoteVersion { get; set; } = string.Empty;

        /// <summary>
        /// Release channel the candidate came from: <c>release</c> or <c>pre-release</c>.
        /// </summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>
        /// How the update is taken from the source: <c>release</c> for a published release, <c>branch</c> for a
        /// branch commit.
        /// </summary>
        public string Mode { get; set; } = string.Empty;


        // Source references

        /// <summary>
        /// URL of the package that is downloaded to install the update.
        /// </summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Branch name for branch-mode candidates, otherwise null.
        /// </summary>
        public string? ReferenceName { get; set; }

        /// <summary>
        /// Tag of the release the update comes from, or null for branch-mode candidates.
        /// </summary>
        public string? ReleaseTag { get; set; }

        /// <summary>
        /// Commit hash of the branch head for branch-mode candidates, otherwise null.
        /// </summary>
        public string? CommitSha { get; set; }

        /// <summary>
        /// Whether the candidate stands for the virtual <c>latest</c> release that always follows the newest release.
        /// </summary>
        public bool IsVirtualLatest { get; set; }

        /// <summary>
        /// Whether the release is marked as a pre-release.
        /// </summary>
        public bool IsPrerelease { get; set; }

        /// <summary>
        /// Publication time of the release, or null when it is unknown.
        /// </summary>
        public DateTimeOffset? PublishedAt { get; set; }


        // Related objects

        /// <summary>
        /// Installed module configuration for module updates, otherwise null.
        /// </summary>
        public ModuleConfig? Module { get; set; }

        /// <summary>
        /// Engine configuration for engine updates, otherwise null.
        /// </summary>
        public EngineConfig? Engine { get; set; }
    }

    /// <summary>
    /// Stores the actionable part of a discovered update across application restarts.
    /// </summary>
    public sealed class PersistedUpdateCandidate
    {
        // Persisted fields

        /// <summary>
        /// Kind of component to update, for example <c>app</c> or <c>engine</c>.
        /// </summary>
        [JsonPropertyName("targetKind")]
        public string TargetKind { get; set; } = string.Empty;

        /// <summary>
        /// Identifier of the component to update within its kind.
        /// </summary>
        [JsonPropertyName("targetId")]
        public string TargetId { get; set; } = string.Empty;

        /// <summary>
        /// Component name shown to the user.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Version offered by the remote source.
        /// </summary>
        [JsonPropertyName("remoteVersion")]
        public string RemoteVersion { get; set; } = string.Empty;

        /// <summary>
        /// Release channel of the update: <c>release</c> or <c>pre-release</c>.
        /// </summary>
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = "release";

        /// <summary>
        /// URL of the package that is downloaded to install the update.
        /// </summary>
        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Tag of the release the update comes from, or null when it is not known.
        /// </summary>
        [JsonPropertyName("releaseTag")]
        public string? ReleaseTag { get; set; }

        /// <summary>
        /// Whether the release is marked as a pre-release.
        /// </summary>
        [JsonPropertyName("isPrerelease")]
        public bool IsPrerelease { get; set; }

        /// <summary>
        /// Publication time of the release, or null when it is unknown.
        /// </summary>
        [JsonPropertyName("publishedAt")]
        public DateTimeOffset? PublishedAt { get; set; }


        // Normalization

        /// <summary>
        /// Normalizes persisted candidate values and rejects incomplete snapshots.
        /// </summary>
        /// <returns>
        /// True when the target, version and download URL are all present, so the candidate can be acted on.
        /// </returns>
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


        // Conversion

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


    // Pending application update

    /// <summary>
    /// Stores the pending self-update operation consumed by the external patcher.
    /// </summary>
    public sealed class PendingAppUpdate
    {
        // Persisted fields

        /// <summary>
        /// Kind of update; currently always <c>app</c>.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "app";

        /// <summary>
        /// Version being installed.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path of the folder that holds the extracted new files.
        /// </summary>
        [JsonPropertyName("stagingPath")]
        public string StagingPath { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path of the installation folder whose files are replaced; the patcher falls back to its own
        /// root when empty.
        /// </summary>
        [JsonPropertyName("targetRoot")]
        public string TargetRoot { get; set; } = string.Empty;

        /// <summary>
        /// Absolute path of the folder that receives a backup of the replaced files; the patcher picks a
        /// timestamped folder when empty.
        /// </summary>
        [JsonPropertyName("backupPath")]
        public string BackupPath { get; set; } = string.Empty;

        /// <summary>
        /// Relative paths that the patcher must neither replace nor delete, for example user data.
        /// </summary>
        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

        /// <summary>
        /// UTC time at which the pending update was created, in round-trip ("o") format.
        /// </summary>
        [JsonPropertyName("createdUtc")]
        public string CreatedUtc { get; set; } = DateTime.UtcNow.ToString("o");


        // Normalization

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


    // Repository branches

    /// <summary>
    /// Describes one GitHub branch returned for a module repository.
    /// </summary>
    /// <param name="Name">Branch name.</param>
    /// <param name="CommitSha">Hash of the commit at the head of the branch.</param>
    public sealed record GitHubBranchInfo(string Name, string CommitSha);
}
