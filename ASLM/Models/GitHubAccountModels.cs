// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Persisted credentials

    /// <summary>
    /// Stores persisted GitHub account credentials in <c>ASLM_Data.json</c>.
    /// </summary>
    public sealed class AppGitHubSettings
    {
        /// <summary>
        /// Personal access token used to authorize GitHub API requests, or null when no account is connected.
        /// </summary>
        [JsonPropertyName("personalAccessToken")]
        public string? PersonalAccessToken { get; set; }

        /// <summary>
        /// GitHub login verified for the stored token, or null when it is unknown.
        /// </summary>
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


    // Account state

    /// <summary>
    /// Describes the current GitHub account state shown in settings.
    /// </summary>
    public sealed class GitHubAccountState
    {
        // Connection state

        /// <summary>
        /// Whether a GitHub account is connected. It is not part of the GitHub response and is never serialized.
        /// </summary>
        [JsonIgnore]
        public bool IsConnected { get; set; }


        // Public profile

        /// <summary>
        /// GitHub login name of the account.
        /// </summary>
        [JsonPropertyName("login")]
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Numeric GitHub account identifier.
        /// </summary>
        [JsonPropertyName("id")]
        public long Id { get; set; }

        /// <summary>
        /// Global GraphQL node identifier of the account.
        /// </summary>
        [JsonPropertyName("node_id")]
        public string NodeId { get; set; } = string.Empty;

        /// <summary>
        /// Display name of the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Publicly visible email address, or null when it is hidden.
        /// </summary>
        [JsonPropertyName("email")]
        public string? Email { get; set; }

        /// <summary>
        /// Email address that receives GitHub notifications, or null when it is not reported.
        /// </summary>
        [JsonPropertyName("notification_email")]
        public string? NotificationEmail { get; set; }

        /// <summary>
        /// URL of the account avatar image.
        /// </summary>
        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        /// <summary>
        /// URL of the account profile page on GitHub.
        /// </summary>
        [JsonPropertyName("html_url")]
        public string ProfileUrl { get; set; } = string.Empty;

        /// <summary>
        /// Kind of account reported by GitHub, for example <c>User</c> or <c>Organization</c>.
        /// </summary>
        [JsonPropertyName("type")]
        public string AccountType { get; set; } = string.Empty;

        /// <summary>
        /// Whether the account is a GitHub site administrator.
        /// </summary>
        [JsonPropertyName("site_admin")]
        public bool IsSiteAdmin { get; set; }

        /// <summary>
        /// Company shown on the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("company")]
        public string? Company { get; set; }

        /// <summary>
        /// Blog or website URL shown on the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("blog")]
        public string? Blog { get; set; }

        /// <summary>
        /// Location shown on the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("location")]
        public string? Location { get; set; }

        /// <summary>
        /// Biography shown on the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("bio")]
        public string? Bio { get; set; }

        /// <summary>
        /// Twitter username shown on the profile, or null when it is not set.
        /// </summary>
        [JsonPropertyName("twitter_username")]
        public string? TwitterUserName { get; set; }

        /// <summary>
        /// Whether the account is marked as available for hire, or null when the profile does not say.
        /// </summary>
        [JsonPropertyName("hireable")]
        public bool? IsHireable { get; set; }


        // Public statistics

        /// <summary>
        /// Number of public repositories owned by the account.
        /// </summary>
        [JsonPropertyName("public_repos")]
        public int PublicRepositories { get; set; }

        /// <summary>
        /// Number of public gists owned by the account.
        /// </summary>
        [JsonPropertyName("public_gists")]
        public int PublicGists { get; set; }

        /// <summary>
        /// Number of accounts that follow this account.
        /// </summary>
        [JsonPropertyName("followers")]
        public int Followers { get; set; }

        /// <summary>
        /// Number of accounts this account follows.
        /// </summary>
        [JsonPropertyName("following")]
        public int Following { get; set; }

        /// <summary>
        /// Creation time of the account as an ISO 8601 timestamp.
        /// </summary>
        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        /// <summary>
        /// Last profile update time of the account as an ISO 8601 timestamp.
        /// </summary>
        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;


        // Details visible only to the account owner

        /// <summary>
        /// Number of private gists, or null when GitHub does not report it for this token.
        /// </summary>
        [JsonPropertyName("private_gists")]
        public int? PrivateGists { get; set; }

        /// <summary>
        /// Total number of private repositories the account can access, or null when GitHub does not report it.
        /// </summary>
        [JsonPropertyName("total_private_repos")]
        public int? TotalPrivateRepositories { get; set; }

        /// <summary>
        /// Number of private repositories owned by the account, or null when GitHub does not report it.
        /// </summary>
        [JsonPropertyName("owned_private_repos")]
        public int? OwnedPrivateRepositories { get; set; }

        /// <summary>
        /// Disk usage in kilobytes as reported by GitHub, or null when it is not reported.
        /// </summary>
        [JsonPropertyName("disk_usage")]
        public int? DiskUsage { get; set; }

        /// <summary>
        /// Number of collaborators across the account's private repositories, or null when it is not reported.
        /// </summary>
        [JsonPropertyName("collaborators")]
        public int? Collaborators { get; set; }

        /// <summary>
        /// Whether two-factor authentication is enabled, or null when GitHub does not report it for this token.
        /// </summary>
        [JsonPropertyName("two_factor_authentication")]
        public bool? HasTwoFactorAuthentication { get; set; }

        /// <summary>
        /// Plan of the account, or null when GitHub does not report it for this token.
        /// </summary>
        [JsonPropertyName("plan")]
        public GitHubAccountPlan? Plan { get; set; }


        // Error state

        /// <summary>
        /// Message of the last failed connect or verification attempt, or null when there was no failure.
        /// It is not part of the GitHub response and is never serialized.
        /// </summary>
        [JsonIgnore]
        public string? ErrorMessage { get; set; }


        // Copying

        /// <summary>
        /// Creates a detached copy for consumers that must not mutate the cached account state.
        /// </summary>
        public GitHubAccountState Clone() => new()
        {
            IsConnected = IsConnected,
            UserName = UserName,
            Id = Id,
            NodeId = NodeId,
            Name = Name,
            Email = Email,
            NotificationEmail = NotificationEmail,
            AvatarUrl = AvatarUrl,
            ProfileUrl = ProfileUrl,
            AccountType = AccountType,
            IsSiteAdmin = IsSiteAdmin,
            Company = Company,
            Blog = Blog,
            Location = Location,
            Bio = Bio,
            TwitterUserName = TwitterUserName,
            IsHireable = IsHireable,
            PublicRepositories = PublicRepositories,
            PublicGists = PublicGists,
            Followers = Followers,
            Following = Following,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            PrivateGists = PrivateGists,
            TotalPrivateRepositories = TotalPrivateRepositories,
            OwnedPrivateRepositories = OwnedPrivateRepositories,
            DiskUsage = DiskUsage,
            Collaborators = Collaborators,
            HasTwoFactorAuthentication = HasTwoFactorAuthentication,
            Plan = Plan?.Clone(),
            ErrorMessage = ErrorMessage
        };
    }

    /// <summary>
    /// Describes the plan metadata returned for an authenticated GitHub account.
    /// </summary>
    public sealed class GitHubAccountPlan
    {
        /// <summary>
        /// Name of the GitHub plan, for example <c>free</c> or <c>pro</c>.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Disk space included in the plan, in kilobytes.
        /// </summary>
        [JsonPropertyName("space")]
        public long Space { get; set; }

        /// <summary>
        /// Number of private repositories included in the plan.
        /// </summary>
        [JsonPropertyName("private_repos")]
        public int PrivateRepositories { get; set; }

        /// <summary>
        /// Number of collaborators included in the plan.
        /// </summary>
        [JsonPropertyName("collaborators")]
        public int Collaborators { get; set; }

        /// <summary>
        /// Creates a detached copy of the plan metadata.
        /// </summary>
        public GitHubAccountPlan Clone() => new()
        {
            Name = Name,
            Space = Space,
            PrivateRepositories = PrivateRepositories,
            Collaborators = Collaborators
        };
    }


    // Action results

    /// <summary>
    /// Captures the result of one GitHub account connect or disconnect action.
    /// </summary>
    public sealed class GitHubAccountActionResult
    {
        /// <summary>
        /// Whether the action completed successfully.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Outcome text: the connected login after a successful connect, the failure reason after a failed one,
        /// and empty after a disconnect.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// Account state after the action ran.
        /// </summary>
        public GitHubAccountState State { get; init; } = new();
    }
}
