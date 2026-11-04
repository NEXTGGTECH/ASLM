// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Loads and verifies the persisted GitHub personal access token.
    /// Keeps the verified account state cached for the settings UI and syncs the rate-limit store with it.
    /// </summary>
    public sealed class GitHubAccountStore
    {
        // Constants

        /// <summary>
        /// GitHub REST endpoint that returns the profile of the authenticated user.
        /// </summary>
        private const string UserEndpoint = "https://api.github.com/user";

        /// <summary>
        /// GitHub page for creating a fine-grained personal access token.
        /// </summary>
        private const string FineGrainedTokenCreationBaseUrl = "https://github.com/settings/personal-access-tokens/new";

        /// <summary>
        /// Lifetime in days requested for newly created tokens.
        /// </summary>
        private const int TokenLifetimeDays = 365;


        // Token creation URL

        /// <summary>
        /// Builds the GitHub token page URL with a name based on the current local date and time.
        /// </summary>
        public static string BuildTokenCreationUrl() => BuildTokenCreationUrl(DateTimeOffset.Now);

        /// <summary>
        /// Builds the GitHub token page URL for a given creation time.
        /// Formats a timestamped token name independently of the application's selected language.
        /// </summary>
        /// <param name="createdAt">Local time written into the suggested token name.</param>
        internal static string BuildTokenCreationUrl(DateTimeOffset createdAt)
        {
            // Preserve local time and a stable date format; encode spaces as %20 in the query.
            var tokenName = string.Create(CultureInfo.InvariantCulture, $"ASLM {createdAt:dd.MM.yyyy HH:mm:ss}");
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"?name={Uri.EscapeDataString(tokenName)}" +
                $"&expires_in={TokenLifetimeDays}");
            return FineGrainedTokenCreationBaseUrl + query;
        }


        // Fields

        /// <summary>
        /// Persisted application data that holds the stored token and user name.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Quota tracker that is told whether requests are authenticated and receives the verification response headers.
        /// </summary>
        private readonly GitHubRateLimitStore _rateLimitStore;

        /// <summary>
        /// Logger for verification failures.
        /// </summary>
        private readonly ILogger<GitHubAccountStore> _logger;

        /// <summary>
        /// HTTP client used for token verification requests.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// JSON settings used to read the authenticated-user response.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Guards <see cref="_state"/>.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Cached account state shown by the settings UI.
        /// </summary>
        private GitHubAccountState _state = new();


        // Initialization

        /// <summary>
        /// Creates the GitHub account store with its own HTTP client.
        /// </summary>
        public GitHubAccountStore(
            AppDataStore appData,
            GitHubRateLimitStore rateLimitStore,
            ILogger<GitHubAccountStore> logger)
            : this(appData, rateLimitStore, logger, new HttpClient())
        {
        }

        /// <summary>
        /// Creates the GitHub account store with an explicit HTTP client (test isolation).
        /// The client is configured with the headers GitHub requires for REST API requests.
        /// </summary>
        internal GitHubAccountStore(AppDataStore appData, GitHubRateLimitStore rateLimitStore,
            ILogger<GitHubAccountStore> logger, HttpClient httpClient)
        {
            _appData = appData ?? throw new ArgumentNullException(nameof(appData));
            _rateLimitStore = rateLimitStore ?? throw new ArgumentNullException(nameof(rateLimitStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-Updater");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }


        // Account state

        /// <summary>
        /// Returns the current GitHub account state cached for the settings UI.
        /// </summary>
        public GitHubAccountState GetState()
        {
            lock (_sync)
            {
                return _state.Clone();
            }
        }

        /// <summary>
        /// Returns the persisted personal access token when one is configured.
        /// </summary>
        public string? GetPersonalAccessToken()
        {
            _appData.Data.GitHub.Normalize();
            return _appData.Data.GitHub.PersonalAccessToken;
        }


        // Startup verification

        /// <summary>
        /// Initializes the cached GitHub account state from persisted data.
        /// Verifies a stored token against GitHub: an invalid token is erased, while a temporary failure keeps
        /// the stored credentials.
        /// </summary>
        /// <param name="ct">Cancellation token; cancellation is propagated to the caller.</param>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            // Without a stored token the application runs anonymously.
            _appData.Data.GitHub.Normalize();
            var token = _appData.Data.GitHub.PersonalAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                SetState(new GitHubAccountState());
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: false);
                return;
            }

            // Assume the authenticated quota while the token is being verified.
            _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: true);
            try
            {
                var state = await VerifyTokenAsync(token, ct);
                SetState(state);
                _appData.Data.GitHub.UserName = state.UserName;
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: true);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // GitHub rejected the token (revoked or expired), so drop it from the persisted data.
                _logger.LogWarning(ex, "Stored GitHub token verification failed during startup; clearing persisted credentials.");
                await ClearInvalidStoredCredentialsAsync();
                SetState(new GitHubAccountState());
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Exhausted quota, network errors and server failures do not invalidate a token.
                _logger.LogWarning(ex, "GitHub account verification is temporarily unavailable; retaining stored credentials.");
                SetState(new GitHubAccountState
                {
                    IsConnected = true,
                    UserName = _appData.Data.GitHub.UserName ?? string.Empty,
                    ErrorMessage = ex.Message
                });
            }
        }

        /// <summary>
        /// Removes persisted GitHub credentials after verification fails.
        /// </summary>
        private async Task ClearInvalidStoredCredentialsAsync()
        {
            _appData.Data.GitHub.PersonalAccessToken = null;
            _appData.Data.GitHub.UserName = null;
            _appData.Data.GitHub.Normalize();
            await _appData.SaveAsync();
            _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: false);
        }


        // Connecting and disconnecting

        /// <summary>
        /// Verifies and persists one GitHub personal access token.
        /// The token is saved only after GitHub accepted it; failures are reported in the returned result.
        /// </summary>
        /// <param name="token">Token entered by the user; surrounding whitespace is trimmed.</param>
        /// <param name="ct">Cancellation token for the verification request.</param>
        public async Task<GitHubAccountActionResult> ConnectAsync(string token, CancellationToken ct = default)
        {
            var normalizedToken = token?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedToken))
            {
                return new GitHubAccountActionResult
                {
                    Success = false,
                    Message = "Personal access token is required.",
                    State = GetState()
                };
            }

            try
            {
                // Verify first, then persist the token together with the resolved user name.
                var state = await VerifyTokenAsync(normalizedToken, ct);
                _appData.Data.GitHub.PersonalAccessToken = normalizedToken;
                _appData.Data.GitHub.UserName = state.UserName;
                _appData.Data.GitHub.Normalize();
                await _appData.SaveAsync();

                SetState(state);
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: true);

                return new GitHubAccountActionResult
                {
                    Success = true,
                    Message = state.UserName,
                    State = state
                };
            }
            catch (Exception ex)
            {
                // Verification or saving failed: expose the error through the cached state and the result.
                _logger.LogWarning(ex, "GitHub account connection failed.");
                var state = new GitHubAccountState
                {
                    IsConnected = false,
                    ErrorMessage = ex.Message
                };
                SetState(state);
                return new GitHubAccountActionResult
                {
                    Success = false,
                    Message = ex.Message,
                    State = state
                };
            }
        }

        /// <summary>
        /// Clears the persisted GitHub personal access token and resets the cached account state to anonymous.
        /// </summary>
        public async Task<GitHubAccountActionResult> DisconnectAsync()
        {
            _appData.Data.GitHub.PersonalAccessToken = null;
            _appData.Data.GitHub.UserName = null;
            _appData.Data.GitHub.Normalize();
            await _appData.SaveAsync();

            var state = new GitHubAccountState();
            SetState(state);
            _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: false);

            return new GitHubAccountActionResult
            {
                Success = true,
                State = state
            };
        }

        /// <summary>
        /// Verifies a token and returns all profile metadata exposed by GitHub's authenticated-user endpoint.
        /// Throws <see cref="HttpRequestException"/> with the GitHub status code when the request is rejected.
        /// </summary>
        private async Task<GitHubAccountState> VerifyTokenAsync(string token, CancellationToken ct)
        {
            // Ask GitHub who owns the token.
            using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct);

            // Only the saved account belongs to the application's current quota.
            // Verifying a new token must not overwrite the anonymous quota before connection succeeds.
            if (string.Equals(token, GetPersonalAccessToken(), StringComparison.Ordinal))
                _rateLimitStore.UpdateFromResponse(response, authenticated: true);

            // Surface the GitHub error body (or the status code) together with the status for callers to inspect.
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    string.IsNullOrWhiteSpace(body)
                        ? $"GitHub API returned {(int)response.StatusCode}."
                        : body, null, response.StatusCode);
            }

            // A valid response must contain the account login; mark the account as connected.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var state = await JsonSerializer.DeserializeAsync<GitHubAccountState>(stream, _jsonOptions, ct);
            if (state == null || string.IsNullOrWhiteSpace(state.UserName))
            {
                throw new InvalidOperationException("GitHub API did not return an account login.");
            }

            state.IsConnected = true;
            state.UserName = state.UserName.Trim();
            return state;
        }


        // State helpers

        /// <summary>
        /// Replaces the cached GitHub account snapshot under synchronization.
        /// </summary>
        private void SetState(GitHubAccountState state)
        {
            lock (_sync)
            {
                _state = state;
            }
        }
    }
}
