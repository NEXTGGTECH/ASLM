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
        private static readonly TimeSpan MinInterCheckDelay = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxInterCheckDelay = TimeSpan.FromMinutes(30);

        private readonly string _filePath;
        private readonly ILogger<GitHubRateLimitStore> _logger;
        private readonly object _sync = new();
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private readonly TimeProvider _clock;
        private ExhaustionNotice? _activeNotice;

        public event EventHandler? StateChanged;

        internal sealed record ExhaustionNotice(DateTimeOffset ResetUtc, bool Authenticated);
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Gets the current persisted GitHub rate-limit state.
        /// </summary>
        public GitHubRateLimitData Data { get; private set; } = new();

        /// <summary>
        /// Creates the store and resolves the persisted data file path.
        /// </summary>
        public GitHubRateLimitStore(ILogger<GitHubRateLimitStore> logger)
            : this(logger, Path.Combine(GetRootDirectory(), "Data", "App", "ASLM_GitHubRateLimit.json"), TimeProvider.System)
        {
        }

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

            lock (_sync)
            {
                Data = new GitHubRateLimitData();
                Data.Normalize();
            }

            await SaveAsync();
        }

        /// <summary>
        /// Updates the known GitHub rate-limit window from response headers.
        /// </summary>
        public void UpdateFromHeaders(int limit, int remaining, long resetEpochSeconds, bool? authenticated = null)
        {
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
                var hasPrevious = DateTimeOffset.TryParse(Data.ResetUtc, out var previous);
                if (hasPrevious && reset < previous)
                    return;
                Data.KnownLimit = Math.Clamp(limit, 1, 15000);
                Data.KnownRemaining = hasPrevious && reset == previous
                    ? Math.Min(Data.KnownRemaining, remaining)
                    : Math.Clamp(remaining, 0, Data.KnownLimit);
                Data.ResetUtc = reset.UtcDateTime.ToString("o");
            }

            try { Save(); }
            finally { PublishStateChanged(); }
        }

        /// <summary>Tracks the hourly REST quota, not search or secondary throttling.</summary>
        internal void UpdateFromResponse(HttpResponseMessage response, bool authenticated)
        {
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

        /// <summary>Reserves one notice across page transitions and concurrent responses.</summary>
        internal ExhaustionNotice? TryBeginExhaustionNotice()
        {
            lock (_sync)
            {
                Data.Normalize();
                if (_activeNotice != null || Data.KnownRemaining != 0 ||
                    !DateTimeOffset.TryParse(Data.ResetUtc, out var reset) || reset <= _clock.GetUtcNow())
                    return null;
                var authenticated = Data.Authenticated == true;
                var last = authenticated ? Data.LastAuthenticatedNoticeResetUtc : Data.LastAnonymousNoticeResetUtc;
                if (DateTimeOffset.TryParse(last, out var acknowledged) &&
                    (reset <= acknowledged || acknowledged > _clock.GetUtcNow()))
                    return null;
                return _activeNotice = new ExhaustionNotice(reset, authenticated);
            }
        }

        /// <summary>Only a button acknowledges the window; unloading merely releases its reservation.</summary>
        internal async Task EndExhaustionNoticeAsync(ExhaustionNotice notice, bool acknowledged)
        {
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

        /// <summary>
        /// Records one GitHub API request and persists the updated history.
        /// </summary>
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
        /// </summary>
        public TimeSpan CalculateInterCheckDelay()
        {
            lock (_sync)
            {
                var remainingBudget = GetAutoRequestsRemaining();
                if (remainingBudget <= 0)
                {
                    return MaxInterCheckDelay;
                }

                var delayUntilReset = GetDelayUntilReset();
                if (delayUntilReset <= TimeSpan.Zero)
                {
                    return MinInterCheckDelay;
                }

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

        /// <summary>
        /// Updates the cached primary rate-limit budget based on authentication state.
        /// </summary>
        public void ApplyAuthenticatedLimitHint(bool isAuthenticated)
        {
            lock (_sync)
            {
                Data.Normalize();
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

        private void PublishStateChanged()
        {
            if (StateChanged is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "GitHub rate-limit UI notification failed."); }
            }
        }

        /// <summary>
        /// Saves the current GitHub usage data asynchronously.
        /// </summary>
        public async Task SaveAsync()
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
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

        private int CountAutoRequestsInCurrentWindow()
        {
            var windowStart = Data.ResolveWindowStartUtc();
            return Data.Requests.Count(record => record.IsAutoRequest() && record.IsWithinWindow(windowStart));
        }

        private void EnsureDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
