// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Domain configuration

    /// <summary>
    /// Stores the domain catalog persisted in <c>Data/App/SUNRISE_Domains.json</c>.
    /// </summary>
    public sealed class SunriseDomainsData
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("domains")]
        public List<SunriseDomain> Domains { get; set; } = [];

        /// <summary>
        /// Restores collection and string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            FileVersion = Math.Max(1, FileVersion);
            Domains ??= [];
            Domains.RemoveAll(domain => domain == null);

            foreach (var domain in Domains)
            {
                domain.Normalize();
            }
        }
    }

    /// <summary>
    /// Describes one SUNRISE server domain and its logical type.
    /// </summary>
    public sealed class SunriseDomain
    {
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        [JsonPropertyName("domainType")]
        public string DomainType { get; set; } = string.Empty;

        /// <summary>
        /// Trims domain values and restores missing strings.
        /// </summary>
        public void Normalize()
        {
            Protocol = Protocol?.Trim() ?? string.Empty;
            Domain = Domain?.Trim() ?? string.Empty;
            DomainType = DomainType?.Trim() ?? string.Empty;
        }
    }


    // URL configuration

    /// <summary>
    /// Stores the named URL catalog persisted in <c>Data/App/SUNRISE_URLs.json</c>.
    /// </summary>
    public sealed class SunriseUrlsData
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("urls")]
        public List<SunriseUrl> Urls { get; set; } = [];

        /// <summary>
        /// Restores collection and string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            FileVersion = Math.Max(1, FileVersion);
            Urls ??= [];
            Urls.RemoveAll(url => url == null);

            foreach (var url in Urls)
            {
                url.Normalize();
            }
        }
    }

    /// <summary>
    /// Maps a stable SUNRISE endpoint name to a relative URL and domain type.
    /// </summary>
    public sealed class SunriseUrl
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("domainType")]
        public string DomainType { get; set; } = string.Empty;

        /// <summary>
        /// Trims endpoint values and restores missing strings.
        /// </summary>
        public void Normalize()
        {
            Name = Name?.Trim() ?? string.Empty;
            Url = Url?.Trim() ?? string.Empty;
            DomainType = DomainType?.Trim() ?? string.Empty;
        }
    }


    // JWT data

    /// <summary>
    /// Stores SUNRISE JWT data persisted in <c>Data/App/SUNRISE_Tokens.json</c>.
    /// </summary>
    public sealed class SunriseTokensData
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("jwt")]
        public SunriseJwtTokens Jwt { get; set; } = new();

        /// <summary>
        /// Restores the token container after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            FileVersion = Math.Max(1, FileVersion);
            Jwt ??= new();
            Jwt.Normalize();
        }
    }

    /// <summary>
    /// Contains the current SUNRISE refresh and access tokens.
    /// </summary>
    public sealed class SunriseJwtTokens
    {
        [JsonPropertyName("tokenRefresh")]
        public string TokenRefresh { get; set; } = string.Empty;

        [JsonPropertyName("tokenAccess")]
        public string TokenAccess { get; set; } = string.Empty;

        /// <summary>
        /// Restores missing token strings without changing token contents.
        /// </summary>
        public void Normalize()
        {
            TokenRefresh ??= string.Empty;
            TokenAccess ??= string.Empty;
        }
    }


    // User data

    /// <summary>
    /// Stores SUNRISE account data persisted in <c>Data/App/SUNRISE_UserData.json</c>.
    /// </summary>
    public sealed class SunriseUserDataDocument
    {
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        [JsonPropertyName("userData")]
        public SunriseUserData UserData { get; set; } = new();

        /// <summary>
        /// Restores the user-data graph after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            FileVersion = Math.Max(1, FileVersion);
            UserData ??= new();
            UserData.Normalize();
        }
    }

    /// <summary>
    /// Contains the active account and locally saved account entries.
    /// </summary>
    public sealed class SunriseUserData
    {
        [JsonPropertyName("account")]
        public SunriseUserAccount Account { get; set; } = new();

        [JsonPropertyName("savedAccounts")]
        public List<SunriseSavedAccount> SavedAccounts { get; set; } = [];

        /// <summary>
        /// Restores nested account and saved-account values.
        /// </summary>
        public void Normalize()
        {
            Account ??= new();
            Account.Normalize();
            SavedAccounts ??= [];
            SavedAccounts.RemoveAll(account => account == null);

            foreach (var savedAccount in SavedAccounts)
            {
                savedAccount.Normalize();
            }
        }
    }

    /// <summary>
    /// Describes a SUNRISE account returned by the web API.
    /// </summary>
    public sealed class SunriseUserAccount
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [JsonPropertyName("is_staff")]
        public bool IsStaff { get; set; }

        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; set; }

        [JsonPropertyName("aslm")]
        public SunriseAslmProfile? Aslm { get; set; }

        /// <summary>
        /// Restores missing account strings and normalizes an optional ASLM profile.
        /// </summary>
        public void Normalize()
        {
            Uid ??= string.Empty;
            Username ??= string.Empty;
            Email ??= string.Empty;
            Aslm?.Normalize();
        }
    }

    /// <summary>
    /// Describes the application-specific ASLM profile nested in a SUNRISE account.
    /// </summary>
    public sealed class SunriseAslmProfile
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        [JsonPropertyName("is_staff")]
        public bool IsStaff { get; set; }

        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; set; }

        [JsonPropertyName("is_banned")]
        public bool IsBanned { get; set; }

        /// <summary>
        /// Restores missing ASLM profile strings.
        /// </summary>
        public void Normalize()
        {
            Uid ??= string.Empty;
            Username ??= string.Empty;
        }
    }

    /// <summary>
    /// Describes one locally remembered SUNRISE account.
    /// </summary>
    public sealed class SunriseSavedAccount
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("tokenRefresh")]
        public string TokenRefresh { get; set; } = string.Empty;

        [JsonPropertyName("lastLogin")]
        public string LastLogin { get; set; } = string.Empty;

        /// <summary>
        /// Restores missing saved-account strings.
        /// </summary>
        public void Normalize()
        {
            Username ??= string.Empty;
            Email ??= string.Empty;
            TokenRefresh ??= string.Empty;
            LastLogin ??= string.Empty;
        }
    }


    // HTTP headers

    /// <summary>
    /// Represents one arbitrary HTTP header passed to a SUNRISE request.
    /// </summary>
    public sealed class SunriseHeader
    {
        /// <summary>
        /// Creates an empty header for serializers and object initializers.
        /// </summary>
        public SunriseHeader()
        {
        }

        /// <summary>
        /// Creates a header from its name and value.
        /// </summary>
        public SunriseHeader(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }


    // Account integration results

    /// <summary>
    /// Describes the result of browser-based SUNRISE authorization for ASLM.
    /// </summary>
    public sealed class SunriseAppAuthenticationResult
    {
        public bool Success { get; init; }

        public string Error { get; init; } = string.Empty;

        public SunriseUserAccount? Account { get; init; }
    }

    /// <summary>
    /// Describes one background synchronization of a selected SUNRISE cloud account.
    /// </summary>
    public sealed class SunriseAccountSyncResult
    {
        public bool Success { get; init; }

        public bool Skipped { get; init; }

        public bool ProfileCreated { get; init; }

        public string Error { get; init; } = string.Empty;

        public SunriseUserAccount? Account { get; init; }
    }


    // Web API payloads

    internal sealed class SunriseAuthRequest
    {
        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; init; } = string.Empty;
    }

    internal sealed class SunriseRefreshRequest
    {
        [JsonPropertyName("refresh")]
        public string Refresh { get; init; } = string.Empty;
    }

    internal sealed class SunriseVerifyRequest
    {
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;
    }

    internal sealed class SunriseSignupRequest
    {
        [JsonPropertyName("email")]
        public string Email { get; init; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;

        [JsonPropertyName("password1")]
        public string Password1 { get; init; } = string.Empty;

        [JsonPropertyName("password2")]
        public string Password2 { get; init; } = string.Empty;

        [JsonPropertyName("signup_app")]
        public string SignupApp { get; init; } = "ASLM";
    }

    internal sealed class SunriseTokenResponse
    {
        [JsonPropertyName("refresh")]
        public string? Refresh { get; set; }

        [JsonPropertyName("access")]
        public string? Access { get; set; }
    }

    internal sealed class SunriseUserDataResponse
    {
        [JsonPropertyName("user_data")]
        public SunriseUserAccount? UserData { get; set; }
    }

    internal sealed class SunriseCallbackPayload
    {
        public string State { get; init; } = string.Empty;

        public string Access { get; init; } = string.Empty;

        public string Refresh { get; init; } = string.Empty;
    }
}
