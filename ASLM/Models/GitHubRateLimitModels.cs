// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Request classification

    /// <summary>
    /// Known GitHub request source values persisted with each API call record.
    /// </summary>
    public static class GitHubRequestSources
    {
        /// <summary>
        /// Request made by a background operation such as the automatic update check.
        /// </summary>
        public const string Auto = "auto";

        /// <summary>
        /// Request made on behalf of an explicit user action.
        /// </summary>
        public const string Manual = "manual";
    }

    /// <summary>
    /// Known GitHub request type values persisted with each API call record.
    /// </summary>
    public static class GitHubRequestTypes
    {
        /// <summary>
        /// Request that lists or reads releases.
        /// </summary>
        public const string Releases = "releases";

        /// <summary>
        /// Request that lists or reads branches.
        /// </summary>
        public const string Branches = "branches";

        /// <summary>
        /// Request that downloads a file or archive; also the fallback type for records without one.
        /// </summary>
        public const string Download = "download";

        /// <summary>
        /// Request to the rate-limit endpoint that refreshes the known quota.
        /// </summary>
        public const string RateLimit = "rate_limit";
    }


    // Rate-limit state

    /// <summary>
    /// Stores persisted GitHub API usage for rate-limit budgeting across restarts.
    /// </summary>
    public sealed class GitHubRateLimitData
    {
        // Persisted state

        /// <summary>
        /// Version of the persisted file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Requests per hour allowed by GitHub for the current authentication state; 60 for anonymous access.
        /// </summary>
        [JsonPropertyName("knownLimit")]
        public int KnownLimit { get; set; } = 60;

        /// <summary>
        /// Requests left in the current window according to the latest response headers.
        /// </summary>
        [JsonPropertyName("knownRemaining")]
        public int KnownRemaining { get; set; } = 60;

        /// <summary>
        /// UTC time in round-trip ("o") format at which the current window resets, or null when it is not known yet.
        /// </summary>
        [JsonPropertyName("resetUtc")]
        public string? ResetUtc { get; set; }

        /// <summary>
        /// Whether the known quota belongs to an authenticated token; null until it is determined.
        /// </summary>
        [JsonPropertyName("authenticated")]
        public bool? Authenticated { get; set; }

        /// <summary>
        /// Reset time of the window for which the exhaustion notice was last acknowledged while anonymous.
        /// </summary>
        [JsonPropertyName("lastAnonymousNoticeResetUtc")]
        public string? LastAnonymousNoticeResetUtc { get; set; }

        /// <summary>
        /// Reset time of the window for which the exhaustion notice was last acknowledged while authenticated.
        /// </summary>
        [JsonPropertyName("lastAuthenticatedNoticeResetUtc")]
        public string? LastAuthenticatedNoticeResetUtc { get; set; }

        /// <summary>
        /// Requests recorded in the current rate-limit window.
        /// </summary>
        [JsonPropertyName("requests")]
        public List<GitHubRequestRecord> Requests { get; set; } = [];


        // Normalization

        /// <summary>
        /// Restores safe defaults and trims request history to the active rate-limit window.
        /// </summary>
        public void Normalize()
        {
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            // Keep the known quota inside plausible bounds; a limit above 60 implies an authenticated token.
            KnownLimit = Math.Clamp(KnownLimit <= 0 ? 60 : KnownLimit, 1, 15000);
            KnownRemaining = Math.Clamp(KnownRemaining, 0, KnownLimit);
            Authenticated ??= KnownLimit > 60;
            ResetUtc = string.IsNullOrWhiteSpace(ResetUtc) ? null : ResetUtc.Trim();

            Requests ??= [];
            PruneRequestsOutsideCurrentWindow();
            TrimRequestHistory();
        }


        // Request window

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

            // Drop the oldest records first.
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
            // The window lasts one hour, so it started one hour before a reset that is still in the future.
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
        // Request data

        /// <summary>
        /// UTC time of the request in round-trip ("o") format.
        /// </summary>
        [JsonPropertyName("timestampUtc")]
        public string TimestampUtc { get; set; } = string.Empty;

        /// <summary>
        /// URL that was requested.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Kind of the request, one of the <see cref="GitHubRequestTypes"/> values.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Origin of the request, one of the <see cref="GitHubRequestSources"/> values.
        /// </summary>
        [JsonPropertyName("source")]
        public string Source { get; set; } = GitHubRequestSources.Auto;

        /// <summary>
        /// HTTP status code of the response, or null when no response was received.
        /// </summary>
        [JsonPropertyName("statusCode")]
        public int? StatusCode { get; set; }


        // Normalization

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


        // Queries

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
