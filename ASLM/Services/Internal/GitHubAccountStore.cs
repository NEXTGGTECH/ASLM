// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Loads and verifies the persisted GitHub personal access token.
    /// </summary>
    public sealed class GitHubAccountStore
    {
        private const string UserEndpoint = "https://api.github.com/user";
        private const string FineGrainedTokenCreationBaseUrl =
            "https://github.com/settings/personal-access-tokens/new";
        private const int TokenLifetimeDays = 365;

        /// <summary>
        /// Builds the GitHub fine-grained token page URL pre-filled for ASLM.
        /// </summary>
        public static string BuildTokenCreationUrl()
        {
            var query = string.Create(
                CultureInfo.InvariantCulture,
                $"?name={Uri.EscapeDataString("ASLM")}" +
                $"&expires_in={TokenLifetimeDays}");
            return FineGrainedTokenCreationBaseUrl + query;
        }

        private readonly AppDataStore _appData;
        private readonly GitHubRateLimitStore _rateLimitStore;
        private readonly ILogger<GitHubAccountStore> _logger;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly object _sync = new();

        private GitHubAccountState _state = new();

        /// <summary>
        /// Creates the GitHub account store.
        /// </summary>
        public GitHubAccountStore(
            AppDataStore appData,
            GitHubRateLimitStore rateLimitStore,
            ILogger<GitHubAccountStore> logger)
        {
            _appData = appData ?? throw new ArgumentNullException(nameof(appData));
            _rateLimitStore = rateLimitStore ?? throw new ArgumentNullException(nameof(rateLimitStore));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-Updater");
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        /// <summary>
        /// Returns the current GitHub account state cached for the settings UI.
        /// </summary>
        public GitHubAccountState GetState()
        {
            lock (_sync)
            {
                return new GitHubAccountState
                {
                    IsConnected = _state.IsConnected,
                    UserName = _state.UserName,
                    ErrorMessage = _state.ErrorMessage
                };
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

        /// <summary>
        /// Initializes the cached GitHub account state from persisted data.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            _appData.Data.GitHub.Normalize();
            var token = _appData.Data.GitHub.PersonalAccessToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                SetState(new GitHubAccountState());
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: false);
                return;
            }

            try
            {
                var userName = await VerifyTokenAsync(token, ct);
                SetState(new GitHubAccountState
                {
                    IsConnected = true,
                    UserName = userName
                });
                _appData.Data.GitHub.UserName = userName;
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stored GitHub token verification failed during startup; clearing persisted credentials.");
                await ClearInvalidStoredCredentialsAsync();
                SetState(new GitHubAccountState());
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

        /// <summary>
        /// Verifies and persists one GitHub personal access token.
        /// </summary>
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
                var userName = await VerifyTokenAsync(normalizedToken, ct);
                _appData.Data.GitHub.PersonalAccessToken = normalizedToken;
                _appData.Data.GitHub.UserName = userName;
                _appData.Data.GitHub.Normalize();
                await _appData.SaveAsync();

                var state = new GitHubAccountState
                {
                    IsConnected = true,
                    UserName = userName
                };
                SetState(state);
                _rateLimitStore.ApplyAuthenticatedLimitHint(isAuthenticated: true);

                return new GitHubAccountActionResult
                {
                    Success = true,
                    Message = userName,
                    State = state
                };
            }
            catch (Exception ex)
            {
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
        /// Clears the persisted GitHub personal access token.
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

        private async Task<string> VerifyTokenAsync(string token, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UserEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(body)
                        ? $"GitHub API returned {(int)response.StatusCode}."
                        : body);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var payload = await JsonSerializer.DeserializeAsync<GitHubUserPayload>(stream, _jsonOptions, ct);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Login))
            {
                throw new InvalidOperationException("GitHub API did not return an account login.");
            }

            return payload.Login.Trim();
        }

        private void SetState(GitHubAccountState state)
        {
            lock (_sync)
            {
                _state = state;
            }
        }

        private sealed class GitHubUserPayload
        {
            [JsonPropertyName("login")]
            public string Login { get; set; } = string.Empty;
        }
    }
}
