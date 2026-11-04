// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Loads and saves GitHub API usage in <c>Data/App/ASLM_GitHubRateLimit.json</c>.
    /// </summary>
    public sealed class GitHubRateLimitStore
    {
        // Fields and constants

        /// <summary>
        /// Shortest pause between two automatic update-check requests.
        /// </summary>
        private static readonly TimeSpan MinInterCheckDelay = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Longest pause between two automatic update-check requests.
        /// </summary>
        private static readonly TimeSpan MaxInterCheckDelay = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Absolute path of the <c>ASLM_GitHubRateLimit.json</c> data file.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Logger for load, save and notification failures.
        /// </summary>
        private readonly ILogger<GitHubRateLimitStore> _logger;

        /// <summary>
        /// Guards <see cref="Data"/> and <see cref="_activeNotice"/>.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Serializes file writes so parallel responses cannot write the data file at the same time.
        /// </summary>
        private readonly SemaphoreSlim _saveGate = new(1, 1);

        /// <summary>
        /// Time source for window checks; replaced in tests.
        /// </summary>
        private readonly TimeProvider _clock;

        /// <summary>
        /// Exhaustion notice currently shown to the user, or null when none is reserved.
        /// </summary>
        private ExhaustionNotice? _activeNotice;


        // Events and nested types

        /// <summary>
        /// Raised after the quota state or the request history changed.
        /// </summary>
        public event EventHandler? StateChanged;

        /// <summary>
        /// Describes the exhausted rate-limit window that a notice was reserved for.
        /// </summary>
        /// <param name="ResetUtc">Time at which the exhausted window resets.</param>
        /// <param name="Authenticated">Whether the exhausted quota belongs to an authenticated token.</param>
        internal sealed record ExhaustionNotice(DateTimeOffset ResetUtc, bool Authenticated);


        // Serialization and state

        /// <summary>
        /// JSON settings used to read and write the data file.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Gets the current persisted GitHub rate-limit state.
        /// </summary>
        public GitHubRateLimitData Data { get; private set; } = new();


        // Initialization

        /// <summary>
        /// Creates the store and resolves the persisted data file path.
        /// </summary>
        public GitHubRateLimitStore(ILogger<GitHubRateLimitStore> logger)
            : this(logger, Path.Combine(GetRootDirectory(), "Data", "App", "ASLM_GitHubRateLimit.json"), TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates the store with an explicit data file path and time source (test isolation).
        /// </summary>
        internal GitHubRateLimitStore(ILogger<GitHubRateLimitStore> logger, string filePath, TimeProvider clock)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _filePath = filePath;
            _clock = clock;
        }

        /// <summary>
        /// Initializes the store by loading persisted data once at startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadAsync();
        }


        // Loading and snapshot

        /// <summary>
        /// Returns a thread-safe snapshot of the current primary GitHub API rate-limit window.
        /// </summary>
        public (int Limit, int Remaining, string? ResetUtc) GetSnapshot()
        {
            lock (_sync)
            {
                Data.Normalize();
                return (Data.KnownLimit, Data.KnownRemaining, Data.ResetUtc);
            }
        }

        /// <summary>
        /// Loads persisted GitHub usage data or recreates defaults when the file is missing or invalid.
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                // Read the persisted snapshot and replace the in-memory data with it.
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        lock (_sync)
                        {
                            Data = JsonSerializer.Deserialize<GitHubRateLimitData>(json, _jsonOptions) ?? new GitHubRateLimitData();
                            Data.Normalize();
                        }

                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load GitHub rate-limit data from {FilePath}. Falling back to defaults.", _filePath);
            }

            // The file is missing, empty or unreadable: start from defaults and write them out.
            lock (_sync)
            {
                Data = new GitHubRateLimitData();
                Data.Normalize();
            }

            await SaveAsync();
        }


        // Quota tracking

        /// <summary>
        /// Updates the known GitHub rate-limit window from response headers.
        /// Stale responses (older window, or sent under a different authentication state) are ignored.
        /// </summary>
        /// <param name="limit">Value of <c>X-RateLimit-Limit</c>: requests allowed per window.</param>
        /// <param name="remaining">Value of <c>X-RateLimit-Remaining</c>: requests left in the window.</param>
        /// <param name="resetEpochSeconds">Value of <c>X-RateLimit-Reset</c>: Unix time at which the window resets.</param>
        /// <param name="authenticated">Whether the response was sent with a token; null skips the staleness check.</param>
        public void UpdateFromHeaders(int limit, int remaining, long resetEpochSeconds, bool? authenticated = null)
        {
            // Reject values GitHub never sends, such as a remaining count above the limit.
            if (limit <= 0 || remaining < 0 || remaining > limit || resetEpochSeconds <= 0 ||
                resetEpochSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                return;
            var reset = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
            lock (_sync)
            {
                Data.Normalize();

                // A response sent before connecting/disconnecting belongs to the old quota.
                if (authenticated.HasValue && authenticated != Data.Authenticated)
                    return;

                // A response from an older window than the known one is stale.
                var hasPrevious = DateTimeOffset.TryParse(Data.ResetUtc, out var previous);
                if (hasPrevious && reset < previous)
                    return;

                // Within one window the remaining count only decreases; a new window takes the reported value.
                Data.KnownLimit = Math.Clamp(limit, 1, 15000);
                Data.KnownRemaining = hasPrevious && reset == previous
                    ? Math.Min(Data.KnownRemaining, remaining)
                    : Math.Clamp(remaining, 0, Data.KnownLimit);
                Data.ResetUtc = reset.UtcDateTime.ToString("o");
            }

            try { Save(); }
            finally { PublishStateChanged(); }
        }

        /// <summary>
        /// Tracks the hourly REST quota, not search or secondary throttling.
        /// Reads the <c>X-RateLimit-*</c> headers of a GitHub response and forwards complete sets to
        /// <see cref="UpdateFromHeaders"/>.
        /// </summary>
        internal void UpdateFromResponse(HttpResponseMessage response, bool authenticated)
        {
            // Responses that count against another resource (search, graphql, ...) do not describe the core quota.
            if (response.Headers.TryGetValues("X-RateLimit-Resource", out var resources) &&
                !string.Equals(resources.FirstOrDefault(), "core", StringComparison.OrdinalIgnoreCase))
                return;
            if (response.Headers.TryGetValues("X-RateLimit-Limit", out var limits) &&
                int.TryParse(limits.FirstOrDefault(), out var limit) &&
                response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                int.TryParse(remainingValues.FirstOrDefault(), out var remaining) &&
                response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                long.TryParse(resets.FirstOrDefault(), out var reset))
                UpdateFromHeaders(limit, remaining, reset, authenticated);
        }


        // Exhaustion notice

        /// <summary>
        /// Reserves one notice across page transitions and concurrent responses.
        /// Returns null when a notice is already reserved, the quota is not exhausted, the window already
        /// reset, or the user acknowledged this window before.
        /// </summary>
        internal ExhaustionNotice? TryBeginExhaustionNotice()
        {
            lock (_sync)
            {
                Data.Normalize();

                // Only an exhausted quota in a window that has not reset yet needs a notice.
                if (_activeNotice != null || Data.KnownRemaining != 0 ||
                    !DateTimeOffset.TryParse(Data.ResetUtc, out var reset) || reset <= _clock.GetUtcNow())
                    return null;

                // Do not repeat a notice the user already acknowledged for this window (anonymous and
                // authenticated quotas are acknowledged separately).
                var authenticated = Data.Authenticated == true;
                var last = authenticated ? Data.LastAuthenticatedNoticeResetUtc : Data.LastAnonymousNoticeResetUtc;
                if (DateTimeOffset.TryParse(last, out var acknowledged) &&
                    (reset <= acknowledged || acknowledged > _clock.GetUtcNow()))
                    return null;
                return _activeNotice = new ExhaustionNotice(reset, authenticated);
            }
        }

        /// <summary>
        /// Only a button acknowledges the window; unloading merely releases its reservation.
        /// An acknowledged window is persisted so the same notice is not shown again.
        /// </summary>
        /// <param name="notice">Notice previously returned by <see cref="TryBeginExhaustionNotice"/>; others are ignored.</param>
        /// <param name="acknowledged">Whether the user acknowledged the notice, as opposed to it being unloaded.</param>
        internal async Task EndExhaustionNoticeAsync(ExhaustionNotice notice, bool acknowledged)
        {
            // Release the reservation and remember the acknowledged reset time for the matching quota kind.
            lock (_sync)
            {
                if (!ReferenceEquals(_activeNotice, notice)) return;
                _activeNotice = null;
                if (acknowledged)
                {
                    var value = notice.ResetUtc.UtcDateTime.ToString("o");
                    if (notice.Authenticated) Data.LastAuthenticatedNoticeResetUtc = value;
                    else Data.LastAnonymousNoticeResetUtc = value;
                }
            }

            if (acknowledged)
            {
                try { await SaveAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not persist the GitHub rate-limit notice acknowledgment."); }
            }

            PublishStateChanged();
        }


        // Request history and automatic-check budget

        /// <summary>
        /// Records one GitHub API request and persists the updated history.
        /// </summary>
        /// <param name="url">Requested URL.</param>
        /// <param name="type">Kind of request (see <see cref="GitHubRequestTypes"/>); blank means a download.</param>
        /// <param name="source">Whether the user triggered it (manual) or the application did (automatic).</param>
        /// <param name="statusCode">HTTP status of the response, or null when no response arrived.</param>
        public void RecordRequest(string url, string type, string source, int? statusCode)
        {
            var record = new GitHubRequestRecord
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Url = url ?? string.Empty,
                Type = string.IsNullOrWhiteSpace(type) ? GitHubRequestTypes.Download : type.Trim(),
                Source = string.Equals(source, GitHubRequestSources.Manual, StringComparison.OrdinalIgnoreCase)
                    ? GitHubRequestSources.Manual
                    : GitHubRequestSources.Auto,
                StatusCode = statusCode
            };
            record.Normalize();

            lock (_sync)
            {
                Data.Requests.Add(record);
                Data.Normalize();
            }

            Save();
        }

        /// <summary>
        /// Returns whether automatic update checks still have budget in the current window.
        /// Automatic requests may use at most half of the known limit, so manual requests always keep headroom.
        /// </summary>
        public bool CanMakeAutoRequest()
        {
            lock (_sync)
            {
                Data.Normalize();
                return CountAutoRequestsInCurrentWindow() < Data.KnownLimit / 2;
            }
        }

        /// <summary>
        /// Returns how many automatic requests remain in the current window budget.
        /// </summary>
        public int GetAutoRequestsRemaining()
        {
            lock (_sync)
            {
                Data.Normalize();
                var budget = Data.KnownLimit / 2;
                return Math.Max(0, budget - CountAutoRequestsInCurrentWindow());
            }
        }

        /// <summary>
        /// Returns the remaining time until the GitHub rate-limit window resets.
        /// </summary>
        public TimeSpan GetDelayUntilReset()
        {
            lock (_sync)
            {
                if (!DateTimeOffset.TryParse(Data.ResetUtc, out var resetUtc))
                {
                    return TimeSpan.Zero;
                }

                var delay = resetUtc - DateTimeOffset.UtcNow;
                return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Calculates the delay before the next automatic update check request.
        /// Spreads the remaining automatic budget evenly over the time until the window resets,
        /// limited to the range between <see cref="MinInterCheckDelay"/> and <see cref="MaxInterCheckDelay"/>.
        /// </summary>
        public TimeSpan CalculateInterCheckDelay()
        {
            lock (_sync)
            {
                // With no budget left, back off to the longest delay.
                var remainingBudget = GetAutoRequestsRemaining();
                if (remainingBudget <= 0)
                {
                    return MaxInterCheckDelay;
                }

                // The window already reset (or is unknown): the quota is fresh, so check at the fastest pace.
                var delayUntilReset = GetDelayUntilReset();
                if (delayUntilReset <= TimeSpan.Zero)
                {
                    return MinInterCheckDelay;
                }

                // Divide the time left in the window by the remaining budget and clamp the result.
                var calculated = TimeSpan.FromTicks(delayUntilReset.Ticks / Math.Max(1, remainingBudget));
                if (calculated < MinInterCheckDelay)
                {
                    return MinInterCheckDelay;
                }

                if (calculated > MaxInterCheckDelay)
                {
                    return MaxInterCheckDelay;
                }

                return calculated;
            }
        }


        // Authentication hint and notifications

        /// <summary>
        /// Updates the cached primary rate-limit budget based on authentication state.
        /// When the state changes, the quota resets to GitHub's default limit for that state
        /// (5000 per hour with a token, 60 without) until a response reports the real values.
        /// </summary>
        /// <param name="isAuthenticated">Whether requests are now sent with a personal access token.</param>
        public void ApplyAuthenticatedLimitHint(bool isAuthenticated)
        {
            lock (_sync)
            {
                Data.Normalize();

                // A different authentication state means a different quota, so the old window is discarded.
                if (Data.Authenticated != isAuthenticated)
                {
                    Data.Authenticated = isAuthenticated;
                    Data.KnownLimit = isAuthenticated ? 5000 : 60;
                    Data.KnownRemaining = Data.KnownLimit;
                    Data.ResetUtc = null;
                }
            }

            Save();
            PublishStateChanged();
        }

        /// <summary>
        /// Raises <see cref="StateChanged"/> for each subscriber separately, so one failing handler
        /// cannot stop the others.
        /// </summary>
        private void PublishStateChanged()
        {
            if (StateChanged is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "GitHub rate-limit UI notification failed."); }
            }
        }


        // Persistence and helpers

        /// <summary>
        /// Saves the current GitHub usage data asynchronously.
        /// </summary>
        public async Task SaveAsync()
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Serialize under the data lock, then write the file outside of it.
                EnsureDirectoryExists();
                string json;
                lock (_sync) { json = JsonSerializer.Serialize(Data, _jsonOptions); }
                await File.WriteAllTextAsync(_filePath, json).ConfigureAwait(false);
            }
            finally { _saveGate.Release(); }
        }

        /// <summary>
        /// Saves the current GitHub usage data synchronously.
        /// </summary>
        private void Save()
        {
            // Parallel repository responses must not write the same history file simultaneously.
            _saveGate.Wait();
            try
            {
                EnsureDirectoryExists();
                string json;
                lock (_sync) { json = JsonSerializer.Serialize(Data, _jsonOptions); }
                File.WriteAllText(_filePath, json);
            }
            finally { _saveGate.Release(); }
        }

        /// <summary>
        /// Counts the automatic requests recorded since the start of the current rate-limit window.
        /// Callers hold <see cref="_sync"/>.
        /// </summary>
        private int CountAutoRequestsInCurrentWindow()
        {
            var windowStart = Data.ResolveWindowStartUtc();
            return Data.Requests.Count(record => record.IsAutoRequest() && record.IsWithinWindow(windowStart));
        }

        /// <summary>
        /// Creates the folder of the data file when it does not exist yet.
        /// </summary>
        private void EnsureDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Resolves the application root directory.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
