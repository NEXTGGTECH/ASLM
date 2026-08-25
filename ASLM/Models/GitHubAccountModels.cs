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
        [JsonIgnore]
        public bool IsConnected { get; set; }

        [JsonPropertyName("login")]
        public string UserName { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("node_id")]
        public string NodeId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("notification_email")]
        public string? NotificationEmail { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("html_url")]
        public string ProfileUrl { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string AccountType { get; set; } = string.Empty;

        [JsonPropertyName("site_admin")]
        public bool IsSiteAdmin { get; set; }

        [JsonPropertyName("company")]
        public string? Company { get; set; }

        [JsonPropertyName("blog")]
        public string? Blog { get; set; }

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("bio")]
        public string? Bio { get; set; }

        [JsonPropertyName("twitter_username")]
        public string? TwitterUserName { get; set; }

        [JsonPropertyName("hireable")]
        public bool? IsHireable { get; set; }

        [JsonPropertyName("public_repos")]
        public int PublicRepositories { get; set; }

        [JsonPropertyName("public_gists")]
        public int PublicGists { get; set; }

        [JsonPropertyName("followers")]
        public int Followers { get; set; }

        [JsonPropertyName("following")]
        public int Following { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonPropertyName("private_gists")]
        public int? PrivateGists { get; set; }

        [JsonPropertyName("total_private_repos")]
        public int? TotalPrivateRepositories { get; set; }

        [JsonPropertyName("owned_private_repos")]
        public int? OwnedPrivateRepositories { get; set; }

        [JsonPropertyName("disk_usage")]
        public int? DiskUsage { get; set; }

        [JsonPropertyName("collaborators")]
        public int? Collaborators { get; set; }

        [JsonPropertyName("two_factor_authentication")]
        public bool? HasTwoFactorAuthentication { get; set; }

        [JsonPropertyName("plan")]
        public GitHubAccountPlan? Plan { get; set; }

        [JsonIgnore]
        public string? ErrorMessage { get; set; }

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
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("space")]
        public long Space { get; set; }

        [JsonPropertyName("private_repos")]
        public int PrivateRepositories { get; set; }

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
