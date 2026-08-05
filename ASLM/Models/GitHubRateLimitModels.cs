// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    /// <summary>
    /// Known GitHub request source values persisted with each API call record.
    /// </summary>
    public static class GitHubRequestSources
    {
        public const string Auto = "auto";
        public const string Manual = "manual";
    }

    /// <summary>
    /// Known GitHub request type values persisted with each API call record.
    /// </summary>
    public static class GitHubRequestTypes
    {
        public const string Releases = "releases";
        public const string Branches = "branches";
        public const string Download = "download";
        public const string RateLimit = "rate_limit";
    }

    /// <summary>
    /// Stores persisted GitHub API usage for rate-limit budgeting across restarts.
    /// </summary>
    public sealed class GitHubRateLimitData
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("knownLimit")]
        public int KnownLimit { get; set; } = 60;

        [JsonPropertyName("knownRemaining")]
        public int KnownRemaining { get; set; } = 60;

        [JsonPropertyName("resetUtc")]
        public string? ResetUtc { get; set; }

        [JsonPropertyName("requests")]
        public List<GitHubRequestRecord> Requests { get; set; } = [];

        /// <summary>
        /// Restores safe defaults and trims request history to the active rate-limit window.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            KnownLimit = Math.Clamp(KnownLimit <= 0 ? 60 : KnownLimit, 1, 15000);
            KnownRemaining = Math.Clamp(KnownRemaining, 0, KnownLimit);
            ResetUtc = string.IsNullOrWhiteSpace(ResetUtc) ? null : ResetUtc.Trim();

            Requests ??= [];
            PruneRequestsOutsideCurrentWindow();
            TrimRequestHistory();
        }

        /// <summary>
        /// Removes request records that fall outside the current GitHub rate-limit window.
        /// </summary>
        public void PruneRequestsOutsideCurrentWindow()
        {
            var windowStart = ResolveWindowStartUtc();
            Requests = Requests
                .Where(record => record.IsWithinWindow(windowStart))
                .ToList();
        }

        /// <summary>
        /// Keeps the request history bounded to the configured GitHub rate limit.
        /// </summary>
        public void TrimRequestHistory()
        {
            if (Requests.Count <= KnownLimit)
            {
                return;
            }

            Requests = Requests
                .OrderBy(record => record.TimestampUtc, StringComparer.Ordinal)
                .Skip(Requests.Count - KnownLimit)
                .ToList();
        }

        /// <summary>
        /// Returns the UTC timestamp when the active GitHub rate-limit window started.
        /// </summary>
        public DateTimeOffset ResolveWindowStartUtc()
        {
            if (DateTimeOffset.TryParse(ResetUtc, out var resetUtc) && resetUtc > DateTimeOffset.UtcNow)
            {
                return resetUtc.AddHours(-1);
            }

            return DateTimeOffset.UtcNow.AddHours(-1);
        }
    }

    /// <summary>
    /// Describes one GitHub API request recorded for rate-limit planning.
    /// </summary>
    public sealed class GitHubRequestRecord
    {
        [JsonPropertyName("timestampUtc")]
        public string TimestampUtc { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = GitHubRequestSources.Auto;

        [JsonPropertyName("statusCode")]
        public int? StatusCode { get; set; }

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            TimestampUtc = string.IsNullOrWhiteSpace(TimestampUtc)
                ? DateTime.UtcNow.ToString("o")
                : TimestampUtc.Trim();
            Url ??= string.Empty;
            Type = string.IsNullOrWhiteSpace(Type) ? GitHubRequestTypes.Download : Type.Trim();
            Source = string.Equals(Source, GitHubRequestSources.Manual, StringComparison.OrdinalIgnoreCase)
                ? GitHubRequestSources.Manual
                : GitHubRequestSources.Auto;
        }

        /// <summary>
        /// Returns whether this record belongs to the active GitHub rate-limit window.
        /// </summary>
        public bool IsWithinWindow(DateTimeOffset windowStartUtc)
        {
            return DateTimeOffset.TryParse(TimestampUtc, out var timestamp) && timestamp >= windowStartUtc;
        }

        /// <summary>
        /// Returns whether this record was initiated by automatic update checks.
        /// </summary>
        public bool IsAutoRequest() =>
            string.Equals(Source, GitHubRequestSources.Auto, StringComparison.OrdinalIgnoreCase);
    }
}
