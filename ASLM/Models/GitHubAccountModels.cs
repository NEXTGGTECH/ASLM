// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    /// <summary>
    /// Stores persisted GitHub account credentials in <c>ASLM_Data.json</c>.
    /// </summary>
    public sealed class AppGitHubSettings
    {
        [JsonPropertyName("personalAccessToken")]
        public string? PersonalAccessToken { get; set; }

        [JsonPropertyName("userName")]
        public string? UserName { get; set; }

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            PersonalAccessToken = string.IsNullOrWhiteSpace(PersonalAccessToken)
                ? null
                : PersonalAccessToken.Trim();
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim();
        }
    }

    /// <summary>
    /// Describes the current GitHub account state shown in settings.
    /// </summary>
    public sealed class GitHubAccountState
    {
        public bool IsConnected { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Captures the result of one GitHub account connect or disconnect action.
    /// </summary>
    public sealed class GitHubAccountActionResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public GitHubAccountState State { get; init; } = new();
    }
}
