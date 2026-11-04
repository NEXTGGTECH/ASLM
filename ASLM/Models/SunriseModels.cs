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
        /// <summary>
        /// Version of the persisted file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Configured SUNRISE server domains, one per domain type.
        /// </summary>
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
        /// <summary>
        /// URL scheme used to reach the domain, for example <c>http</c> or <c>https</c>.
        /// </summary>
        [JsonPropertyName("protocol")]
        public string Protocol { get; set; } = string.Empty;

        /// <summary>
        /// Host name of the SUNRISE server, optionally followed by a port.
        /// </summary>
        [JsonPropertyName("domain")]
        public string Domain { get; set; } = string.Empty;

        /// <summary>
        /// Logical type that endpoints in <see cref="SunriseUrl"/> use to select this domain.
        /// </summary>
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
        /// <summary>
        /// Version of the persisted file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Named SUNRISE endpoints.
        /// </summary>
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
        /// <summary>
        /// Stable endpoint name that the code looks up, for example the sign-up or token refresh endpoint.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// URL path relative to the domain of <see cref="DomainType"/>.
        /// </summary>
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        /// <summary>
        /// Type of the <see cref="SunriseDomain"/> that serves this endpoint.
        /// </summary>
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
        /// <summary>
        /// Version of the persisted file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Tokens of the currently selected SUNRISE account.
        /// </summary>
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
        /// <summary>
        /// Long-lived token used to obtain new access tokens; empty when no account is signed in.
        /// </summary>
        [JsonPropertyName("tokenRefresh")]
        public string TokenRefresh { get; set; } = string.Empty;

        /// <summary>
        /// Short-lived token sent as the <c>JWT</c> authorization header of API requests; empty when not signed in.
        /// </summary>
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
        /// <summary>
        /// Version of the persisted file layout.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Account data of the selected SUNRISE account and the locally remembered accounts.
        /// </summary>
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
        /// <summary>
        /// Account currently selected for the cloud profile.
        /// </summary>
        [JsonPropertyName("account")]
        public SunriseUserAccount Account { get; set; } = new();

        /// <summary>
        /// Accounts remembered on this device so the user can switch between them.
        /// </summary>
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
        /// <summary>
        /// Numeric account identifier on the SUNRISE server.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Public unique identifier of the account.
        /// </summary>
        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        /// <summary>
        /// Account user name.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Account email address.
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Whether the account is active.
        /// </summary>
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        /// <summary>
        /// Whether the account has staff status on the SUNRISE server.
        /// </summary>
        [JsonPropertyName("is_staff")]
        public bool IsStaff { get; set; }

        /// <summary>
        /// Whether the account is a SUNRISE superuser.
        /// </summary>
        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; set; }

        /// <summary>
        /// ASLM-specific profile of the account, or null when the account has none yet.
        /// </summary>
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
        /// <summary>
        /// Numeric identifier of the ASLM profile on the SUNRISE server.
        /// </summary>
        [JsonPropertyName("id")]
        public int Id { get; set; }

        /// <summary>
        /// Public unique identifier of the ASLM profile.
        /// </summary>
        [JsonPropertyName("uid")]
        public string Uid { get; set; } = string.Empty;

        /// <summary>
        /// Profile name that ASLM shows as the user name while the cloud account is selected.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Whether the ASLM profile is active.
        /// </summary>
        [JsonPropertyName("is_active")]
        public bool IsActive { get; set; }

        /// <summary>
        /// Whether the ASLM profile has staff status.
        /// </summary>
        [JsonPropertyName("is_staff")]
        public bool IsStaff { get; set; }

        /// <summary>
        /// Whether the ASLM profile has superuser status.
        /// </summary>
        [JsonPropertyName("is_superuser")]
        public bool IsSuperuser { get; set; }

        /// <summary>
        /// Whether the profile is banned from using ASLM cloud features.
        /// </summary>
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
        /// <summary>
        /// User name of the remembered account.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Email address of the remembered account.
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// Refresh token that lets the user switch back to this account without signing in again.
        /// </summary>
        [JsonPropertyName("tokenRefresh")]
        public string TokenRefresh { get; set; } = string.Empty;

        /// <summary>
        /// Time of the last sign-in to this account, stored as text.
        /// </summary>
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
        // Initialization

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


        // Header data

        /// <summary>
        /// Header name, for example <c>Authorization</c>.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Header value.
        /// </summary>
        public string Value { get; set; } = string.Empty;
    }


    // Account integration results

    /// <summary>
    /// Describes the result of browser-based SUNRISE authorization for ASLM.
    /// </summary>
    public sealed class SunriseAppAuthenticationResult
    {
        /// <summary>
        /// Whether the browser authorization completed and the account was synchronized and selected as the
        /// cloud account.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Error code or message when the authorization failed; empty on success.
        /// </summary>
        public string Error { get; init; } = string.Empty;

        /// <summary>
        /// Account that signed in, or null when the authorization failed.
        /// </summary>
        public SunriseUserAccount? Account { get; init; }
    }

    /// <summary>
    /// Describes one background synchronization of a selected SUNRISE cloud account.
    /// </summary>
    public sealed class SunriseAccountSyncResult
    {
        /// <summary>
        /// Whether the synchronization finished without an error; a skipped synchronization also counts as successful.
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// Whether nothing was synchronized because SUNRISE is disabled or the selected account is not a cloud account.
        /// </summary>
        public bool Skipped { get; init; }

        /// <summary>
        /// Whether the missing ASLM profile had to be created on the server during this synchronization.
        /// </summary>
        public bool ProfileCreated { get; init; }

        /// <summary>
        /// Error code or message when the synchronization failed; empty otherwise.
        /// </summary>
        public string Error { get; init; } = string.Empty;

        /// <summary>
        /// Refreshed account data, or null when the synchronization failed.
        /// </summary>
        public SunriseUserAccount? Account { get; init; }
    }


    // Web API payloads

    /// <summary>
    /// Request body of the sign-in endpoint that exchanges credentials for tokens.
    /// </summary>
    internal sealed class SunriseAuthRequest
    {
        /// <summary>
        /// Account user name.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;

        /// <summary>
        /// Account password.
        /// </summary>
        [JsonPropertyName("password")]
        public string Password { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request body of the endpoint that exchanges a refresh token for a new access token.
    /// </summary>
    internal sealed class SunriseRefreshRequest
    {
        /// <summary>
        /// Refresh token to exchange.
        /// </summary>
        [JsonPropertyName("refresh")]
        public string Refresh { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request body of the endpoint that checks whether a token is still valid.
    /// </summary>
    internal sealed class SunriseVerifyRequest
    {
        /// <summary>
        /// Token to verify.
        /// </summary>
        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;
    }

    /// <summary>
    /// Request body of the endpoint that registers a new SUNRISE account.
    /// </summary>
    internal sealed class SunriseSignupRequest
    {
        /// <summary>
        /// Email address of the new account.
        /// </summary>
        [JsonPropertyName("email")]
        public string Email { get; init; } = string.Empty;

        /// <summary>
        /// User name of the new account.
        /// </summary>
        [JsonPropertyName("username")]
        public string Username { get; init; } = string.Empty;

        /// <summary>
        /// Password of the new account.
        /// </summary>
        [JsonPropertyName("password1")]
        public string Password1 { get; init; } = string.Empty;

        /// <summary>
        /// Repetition of the password for confirmation; the server requires it to match <see cref="Password1"/>.
        /// </summary>
        [JsonPropertyName("password2")]
        public string Password2 { get; init; } = string.Empty;

        /// <summary>
        /// Name of the application the account is registered from.
        /// </summary>
        [JsonPropertyName("signup_app")]
        public string SignupApp { get; init; } = "ASLM";
    }

    /// <summary>
    /// Response body of the token endpoints.
    /// </summary>
    internal sealed class SunriseTokenResponse
    {
        /// <summary>
        /// New refresh token, or null when the server did not return one.
        /// </summary>
        [JsonPropertyName("refresh")]
        public string? Refresh { get; set; }

        /// <summary>
        /// New access token, or null when the server did not return one.
        /// </summary>
        [JsonPropertyName("access")]
        public string? Access { get; set; }
    }

    /// <summary>
    /// Response body of the endpoint that returns the account data of the signed-in user.
    /// </summary>
    internal sealed class SunriseUserDataResponse
    {
        /// <summary>
        /// Account data of the signed-in user, or null when the response contains none.
        /// </summary>
        [JsonPropertyName("user_data")]
        public SunriseUserAccount? UserData { get; set; }
    }

    /// <summary>
    /// Tokens delivered by the browser to the local callback listener after SUNRISE authorization.
    /// </summary>
    internal sealed class SunriseCallbackPayload
    {
        /// <summary>
        /// Opaque state value that ties the callback to the authorization request that ASLM started.
        /// </summary>
        public string State { get; init; } = string.Empty;

        /// <summary>
        /// Access token issued for the authorized account.
        /// </summary>
        public string Access { get; init; } = string.Empty;

        /// <summary>
        /// Refresh token issued for the authorized account.
        /// </summary>
        public string Refresh { get; init; } = string.Empty;
    }
}
