// Copyright NGGT.LightKeeper. All Rights Reserved.

using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using ASLM.Services.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.ApplicationModel;

namespace ASLM.Services.Sunrise
{
    /// <summary>
    /// Provides SUNRISE ID storage, endpoint resolution, authentication, and web API requests.
    /// </summary>
    public sealed class SunriseService : IDisposable
    {
        private const int CurrentFileVersion = 2;

        private const int CallbackHeaderLimit = 16 * 1024;
        private const int CallbackBodyLimit = 32 * 1024;
        private const int CallbackConnectionLimit = 8;
        private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Branded loopback host for the one-time auth callback.
        /// SUNRISE redirects here; listener accepts only this exact Host header.
        /// </summary>
        private const string CallbackHostName = "aslm.nextggtech.localhost";

        private const string DomainsFileName = "SUNRISE_Domains.json";
        private const string UrlsFileName = "SUNRISE_URLs.json";
        private const string TokensFileName = "SUNRISE_Tokens.json";
        private const string UserDataFileName = "SUNRISE_UserData.json";

        private const string DefaultDomainType = "NGGT_OSS";
        private const string ApplicationIdentifier = "ASLM";

        public const string SignupEndpoint = "API_Singup";
        public const string AuthenticationEndpoint = "JWT_Auth";
        public const string RefreshEndpoint = "JWT_Refresh";
        public const string VerifyEndpoint = "JWT_Verify";
        public const string PasswordRecoveryEndpoint = "WEB_PasswordRecovery";
        public const string ApplicationAuthenticationEndpoint = "WEB_AuthApp";
        public const string ApplicationAuthenticationSuccessEndpoint = "WEB_AuthAppSuccess";
        public const string AslmGetUserDataEndpoint = "API_AslmGetUserData";
        public const string AslmCreateProfileEndpoint = "API_AslmCreateProfile";

        public const string ErrorUserData = "ErrorUserData";
        public const string ErrorUserIsActive = "ErrorUserIsActive";
        public const string ErrorProfileAslm = "ErrorProfileASLM";
        public const string ErrorAslmIsActive = "ErrorAslmIsActive";
        public const string ErrorAslmIsBanned = "ErrorAslmIsBanned";
        public const string ErrorBrowserLaunch = "ErrorBrowserLaunch";
        public const string ErrorAuthenticationCancelled = "ErrorAuthenticationCancelled";
        public const string ErrorAuthenticationTimeout = "ErrorAuthenticationTimeout";
        public const string ErrorAuthenticationCallback = "ErrorAuthenticationCallback";
        public const string ErrorTokenRefresh = "ErrorTokenRefresh";
        public const string ErrorAccountRequest = "ErrorAccountRequest";
        public const string ErrorProfileCreation = "ErrorProfileCreation";

        private readonly ILogger<SunriseService> _logger;
        private readonly AppDataStore _appData;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _dataGate = new(1, 1);
        private readonly SemaphoreSlim _accountOperationGate = new(1, 1);

        private readonly string _domainsFilePath;
        private readonly string _urlsFilePath;
        private readonly string _tokensFilePath;
        private readonly string _userDataFilePath;

        private readonly JsonSerializerOptions _storageJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly JsonSerializerOptions _apiJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private SunriseDomainsData _domainsData = CreateDefaultDomainsData();
        private SunriseUrlsData _urlsData = CreateDefaultUrlsData();
        private SunriseTokensData _tokensData = CreateDefaultTokensData();
        private SunriseUserDataDocument _userDataDocument = CreateDefaultUserDataDocument();

        private bool _initialized;
        private bool _disposed;


        // State access

        /// <summary>
        /// Gets the currently loaded SUNRISE domain catalog.
        /// </summary>
        public IReadOnlyList<SunriseDomain> Domains => _domainsData.Domains;

        /// <summary>
        /// Gets the currently loaded SUNRISE endpoint catalog.
        /// </summary>
        public IReadOnlyList<SunriseUrl> Urls => _urlsData.Urls;

        /// <summary>
        /// Gets the current in-memory JWT values.
        /// </summary>
        public SunriseJwtTokens Tokens => _tokensData.Jwt;

        /// <summary>
        /// Gets the current persisted SUNRISE user data.
        /// </summary>
        public SunriseUserData UserData => _userDataDocument.UserData;

        /// <summary>
        /// Gets the selected ASLM account mode persisted in <c>ASLM_Data.json</c>.
        /// </summary>
        public AppAccountMode AccountMode => _appData.Data.User.AccountMode;

        /// <summary>
        /// Gets whether ASLM currently uses a SUNRISE cloud account.
        /// </summary>
        public bool IsCloudAccount => AccountMode == AppAccountMode.Cloud;


        // Construction

        /// <summary>
        /// Creates the service and resolves all persisted data paths below <c>Data/App</c>.
        /// </summary>
        public SunriseService(ILogger<SunriseService> logger, AppDataStore appData)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appData = appData ?? throw new ArgumentNullException(nameof(appData));
            _httpClient = new HttpClient();

            var dataDirectory = Path.Combine(AppRoot.Directory, "Data", "App");
            _domainsFilePath = Path.Combine(dataDirectory, DomainsFileName);
            _urlsFilePath = Path.Combine(dataDirectory, UrlsFileName);
            _tokensFilePath = Path.Combine(dataDirectory, TokensFileName);
            _userDataFilePath = Path.Combine(dataDirectory, UserDataFileName);
        }


        // Initialization

        /// <summary>
        /// Loads all SUNRISE configuration and account data once for the process lifetime.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            await _dataGate.WaitAsync(ct);
            try
            {
                if (_initialized)
                {
                    return;
                }

                _domainsData = await LoadDocumentAsync(
                    _domainsFilePath,
                    CreateDefaultDomainsData,
                    data => data.Normalize(),
                    persistDefaults: true,
                    ct);
                _urlsData = await LoadDocumentAsync(
                    _urlsFilePath,
                    CreateDefaultUrlsData,
                    data => data.Normalize(),
                    persistDefaults: true,
                    ct);
                _tokensData = await LoadDocumentAsync(
                    _tokensFilePath,
                    CreateDefaultTokensData,
                    data => data.Normalize(),
                    persistDefaults: false,
                    ct);
                _userDataDocument = await LoadDocumentAsync(
                    _userDataFilePath,
                    CreateDefaultUserDataDocument,
                    data => data.Normalize(),
                    persistDefaults: false,
                    ct);

                var domainsChanged = EnsureDefaultDomain();
                var urlsChanged = EnsureDefaultUrls();

                if (domainsChanged)
                {
                    await SaveDocumentCoreAsync(_domainsFilePath, _domainsData, ct);
                }

                if (urlsChanged)
                {
                    await SaveDocumentCoreAsync(_urlsFilePath, _urlsData, ct);
                }

                _initialized = true;
            }
            finally
            {
                _dataGate.Release();
            }
        }


        // URL resolution

        /// <summary>
        /// Resolves one configured endpoint name into its absolute SUNRISE URI.
        /// </summary>
        public Uri CreateFullUri(string endpointName)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(endpointName))
            {
                throw new ArgumentException("SUNRISE endpoint name is required.", nameof(endpointName));
            }

            var endpoint = _urlsData.Urls.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, endpointName, StringComparison.Ordinal));
            if (endpoint == null)
            {
                throw new KeyNotFoundException($"SUNRISE endpoint '{endpointName}' is not configured.");
            }

            var domain = _domainsData.Domains.FirstOrDefault(candidate =>
                string.Equals(candidate.DomainType, endpoint.DomainType, StringComparison.Ordinal));
            if (domain == null)
            {
                throw new InvalidOperationException(
                    $"SUNRISE domain type '{endpoint.DomainType}' required by endpoint '{endpoint.Name}' is not configured.");
            }

            if (string.IsNullOrWhiteSpace(domain.Protocol) || string.IsNullOrWhiteSpace(domain.Domain))
            {
                throw new InvalidOperationException(
                    $"SUNRISE domain type '{domain.DomainType}' has an incomplete protocol or domain value.");
            }

            var baseUrl = $"{domain.Protocol.TrimEnd(':', '/', '\\')}://{domain.Domain.TrimEnd('/')}/";
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            {
                throw new InvalidOperationException(
                    $"SUNRISE domain type '{domain.DomainType}' does not form a valid absolute URI.");
            }

            var relativeUrl = endpoint.Url.TrimStart('/');
            if (!Uri.TryCreate(baseUri, relativeUrl, out var fullUri))
            {
                throw new InvalidOperationException(
                    $"SUNRISE endpoint '{endpoint.Name}' does not form a valid URI.");
            }

            return fullUri;
        }

        /// <summary>
        /// Returns the configured password-recovery web page URI.
        /// </summary>
        public Uri GetPasswordRecoveryUri() => CreateFullUri(PasswordRecoveryEndpoint);

        /// <summary>
        /// Builds the SUNRISE application-authentication page URI for a validated loopback callback.
        /// </summary>
        public Uri GetApplicationAuthenticationUri(Uri redirectUri, string state)
        {
            ArgumentNullException.ThrowIfNull(redirectUri);

            if (!IsValidLoopbackRedirectUri(redirectUri))
            {
                throw new ArgumentException(
                    $"The SUNRISE callback must use the fixed /sunrise-auth/ path on {CallbackHostName}.",
                    nameof(redirectUri));
            }

            if (!IsValidCallbackState(state))
            {
                throw new ArgumentException("The SUNRISE callback state is invalid.", nameof(state));
            }

            var endpoint = CreateFullUri(ApplicationAuthenticationEndpoint);
            var separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
            return new Uri(
                endpoint.AbsoluteUri + separator +
                "app=" + Uri.EscapeDataString(ApplicationIdentifier) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri.AbsoluteUri) +
                "&state=" + Uri.EscapeDataString(state),
                UriKind.Absolute);
        }

        /// <summary>
        /// Returns the SUNRISE page shown after the loopback callback is accepted.
        /// </summary>
        public Uri GetApplicationAuthenticationSuccessUri()
        {
            var endpoint = CreateFullUri(ApplicationAuthenticationSuccessEndpoint);
            if (!endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(endpoint.UserInfo))
            {
                throw new InvalidOperationException(
                    "The SUNRISE application-authentication success endpoint must be an HTTP or HTTPS URI without user information.");
            }

            return endpoint;
        }


        // Generic web request

        /// <summary>
        /// Sends one request to a named SUNRISE endpoint with an arbitrary array of headers.
        /// </summary>
        /// <remarks>
        /// Supported verbs are GET, POST, PUT, and DELETE. Unknown verbs fall back to GET,
        /// matching the original SUNRISE request behavior. A body is attached only to POST and PUT.
        /// The returned response belongs to the caller and must be disposed.
        /// </remarks>
        public async Task<HttpResponseMessage> SendAsync(
            string endpointName,
            string httpRequestVerb,
            SunriseHeader[]? headers = null,
            string? body = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            var method = NormalizeHttpMethod(httpRequestVerb);
            var uri = CreateFullUri(endpointName);

            using var request = new HttpRequestMessage(method, uri);
            if (method == HttpMethod.Post || method == HttpMethod.Put)
            {
                request.Content = CreateBodyContent(body ?? string.Empty);
            }

            ApplyHeaders(request, headers);

            try
            {
                return await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Never include request bodies or header values here: they may contain passwords or JWTs.
                _logger.LogWarning(
                    ex,
                    "SUNRISE {Method} request to endpoint {EndpointName} failed.",
                    method.Method,
                    endpointName);
                throw;
            }
        }

        // Authentication API

        /// <summary>
        /// Authenticates a SUNRISE account with a username and password.
        /// </summary>
        public Task<HttpResponseMessage> AuthenticateAsync(
            string username,
            string password,
            CancellationToken ct = default)
        {
            var body = SerializeApiPayload(new SunriseAuthRequest
            {
                Username = username,
                Password = password
            });

            return SendJsonAsync(AuthenticationEndpoint, body, ct);
        }

        /// <summary>
        /// Requests a new SUNRISE token pair using a refresh token.
        /// </summary>
        public Task<HttpResponseMessage> RefreshTokenAsync(
            string refreshToken,
            CancellationToken ct = default)
        {
            var body = SerializeApiPayload(new SunriseRefreshRequest
            {
                Refresh = refreshToken
            });

            return SendJsonAsync(RefreshEndpoint, body, ct);
        }

        /// <summary>
        /// Verifies one SUNRISE refresh or access token.
        /// </summary>
        public Task<HttpResponseMessage> VerifyTokenAsync(
            string refreshOrAccessToken,
            CancellationToken ct = default)
        {
            var body = SerializeApiPayload(new SunriseVerifyRequest
            {
                Token = refreshOrAccessToken
            });

            return SendJsonAsync(VerifyEndpoint, body, ct);
        }

        /// <summary>
        /// Creates a SUNRISE account for the ASLM application.
        /// </summary>
        public Task<HttpResponseMessage> SignUpAsync(
            string email,
            string username,
            string password1,
            string password2,
            CancellationToken ct = default)
        {
            var body = SerializeApiPayload(new SunriseSignupRequest
            {
                Email = email,
                Username = username,
                Password1 = password1,
                Password2 = password2,
                SignupApp = "ASLM"
            });

            return SendJsonAsync(SignupEndpoint, body, ct);
        }

        /// <summary>
        /// Sends an application/json POST request to one named endpoint.
        /// </summary>
        private Task<HttpResponseMessage> SendJsonAsync(
            string endpointName,
            string body,
            CancellationToken ct) =>
            SendAsync(
                endpointName,
                "POST",
                [new SunriseHeader("Content-Type", "application/json")],
                body,
                ct);


        // ASLM API

        /// <summary>
        /// Gets the current account and ASLM profile data using the stored access token.
        /// </summary>
        public Task<HttpResponseMessage> GetAslmUserDataAsync(CancellationToken ct = default) =>
            SendAslmAuthorizedAsync(AslmGetUserDataEndpoint, ct);

        /// <summary>
        /// Creates an ASLM profile for the current account using the stored access token.
        /// </summary>
        public Task<HttpResponseMessage> CreateAslmProfileAsync(CancellationToken ct = default) =>
            SendAslmAuthorizedAsync(AslmCreateProfileEndpoint, ct);

        /// <summary>
        /// Sends one JWT-authorized ASLM request.
        /// </summary>
        private async Task<HttpResponseMessage> SendAslmAuthorizedAsync(
            string endpointName,
            CancellationToken ct)
        {
            // Load persisted tokens before constructing the Authorization header. This also
            // keeps direct service use correct when startup initialization has not run yet.
            await InitializeAsync(ct);

            return await SendAsync(
                endpointName,
                "POST",
                [new SunriseHeader("Authorization", $"JWT {_tokensData.Jwt.TokenAccess}")],
                string.Empty,
                ct);
        }


        // ASLM account integration

        /// <summary>
        /// Opens SUNRISE auth page and receives tokens via a temporary loopback listener.
        /// Binds to 127.0.0.1 but accepts only the exact branded Host header.
        /// Tokens arrive in a state-protected form POST.
        /// </summary>
        public async Task<SunriseAppAuthenticationResult> AuthenticateApplicationAsync(
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await _accountOperationGate.WaitAsync(ct);
            try
            {
                await InitializeAsync(ct);

                var previousRefresh = _tokensData.Jwt.TokenRefresh;
                var previousAccess = _tokensData.Jwt.TokenAccess;
                var state = CreateCallbackState();

                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(1);

                var localEndpoint = (IPEndPoint)listener.LocalEndpoint;
                var redirectUri = new Uri($"http://{CallbackHostName}:{localEndpoint.Port}/sunrise-auth/");
                var authenticationUri = GetApplicationAuthenticationUri(redirectUri, state);
                var authenticationSuccessUri = GetApplicationAuthenticationSuccessUri();

                bool browserOpened;
                try
                {
                    browserOpened = await Launcher.Default.OpenAsync(authenticationUri);
                }
                catch (Exception ex) when (ex is FeatureNotSupportedException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "The SUNRISE authentication page could not be opened.");
                    return AuthenticationFailure(ErrorBrowserLaunch);
                }

                if (!browserOpened)
                {
                    return AuthenticationFailure(ErrorBrowserLaunch);
                }

                SunriseCallbackPayload callback;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(CallbackTimeout);
                try
                {
                    callback = await ReceiveAuthenticationCallbackAsync(
                        listener,
                        state,
                        authenticationSuccessUri,
                        timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return AuthenticationFailure(ErrorAuthenticationTimeout);
                }
                catch (OperationCanceledException)
                {
                    return AuthenticationFailure(ErrorAuthenticationCancelled);
                }
                catch (InvalidDataException ex)
                {
                    _logger.LogWarning(ex, "The SUNRISE authentication callback was rejected.");
                    return AuthenticationFailure(ErrorAuthenticationCallback);
                }

                try
                {
                    // From this point onward every unsuccessful exit restores the credentials
                    // that existed before the browser flow, even if the candidate write itself
                    // is interrupted after partially reaching persistent storage.
                    await StoreTokensAsync(callback.Refresh, callback.Access, ct);
                    var syncResult = await SynchronizeAccountCoreAsync(ct);
                    if (!syncResult.Success || syncResult.Account == null)
                    {
                        await RestoreTokensAfterFailedAuthenticationAsync(previousRefresh, previousAccess);
                        return AuthenticationFailure(syncResult.Error);
                    }

                    await ApplyCloudAccountAsync(syncResult.Account, selectCloud: true, ct);
                    return new SunriseAppAuthenticationResult
                    {
                        Success = true,
                        Account = syncResult.Account
                    };
                }
                catch
                {
                    await RestoreTokensAfterFailedAuthenticationAsync(previousRefresh, previousAccess);
                    throw;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return AuthenticationFailure(ErrorAuthenticationCancelled);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                _logger.LogWarning(ex, "SUNRISE application authentication failed.");
                return AuthenticationFailure(ErrorAuthenticationCallback);
            }
            finally
            {
                _accountOperationGate.Release();
            }
        }

        /// <summary>
        /// Refreshes tokens and account data for the selected cloud account. A missing ASLM
        /// profile is created once and the account data is then requested again.
        /// </summary>
        public async Task<SunriseAccountSyncResult> SynchronizeCloudAccountAsync(
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await _accountOperationGate.WaitAsync(ct);
            try
            {
                await InitializeAsync(ct);
                if (!IsCloudAccount)
                {
                    return new SunriseAccountSyncResult
                    {
                        Success = true,
                        Skipped = true,
                        Account = _userDataDocument.UserData.Account
                    };
                }

                var result = await SynchronizeAccountCoreAsync(ct);
                if (result.Success && result.Account != null)
                {
                    await ApplyCloudAccountAsync(result.Account, selectCloud: false, ct);
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                // Startup callers can report this result without replacing persisted credentials
                // or preventing the rest of ASLM from loading.
                _logger.LogWarning(ex, "SUNRISE cloud-account synchronization failed.");
                return SyncFailure(ErrorAccountRequest);
            }
            finally
            {
                _accountOperationGate.Release();
            }
        }

        /// <summary>
        /// Selects the existing local ASLM account and clears SUNRISE credentials.
        /// </summary>
        public async Task SelectLocalAccountAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await _accountOperationGate.WaitAsync(ct);
            try
            {
                await InitializeAsync(ct);

                // Once a local switch starts, finish credential cleanup even if the settings
                // surface closes and cancels its UI operation. Clearing credentials before
                // persisting Local also prevents a local-mode file from retaining valid JWTs.
                await StoreTokensAsync(string.Empty, string.Empty, CancellationToken.None);
                await ClearUserDataAsync(CancellationToken.None);

                _appData.Data.User.AccountMode = AppAccountMode.Local;
                _appData.Data.User.Name = _appData.Data.User.LocalName;
                await _appData.SaveAsync();
            }
            finally
            {
                _accountOperationGate.Release();
            }
        }

        /// <summary>
        /// Signs out of the SUNRISE cloud account and returns ASLM to local-account mode.
        /// </summary>
        public Task SignOutAsync(CancellationToken ct = default) => SelectLocalAccountAsync(ct);

        private async Task<SunriseAccountSyncResult> SynchronizeAccountCoreAsync(CancellationToken ct)
        {
            if (!TryGetRefreshToken(out var refreshToken))
            {
                return SyncFailure(ErrorTokenRefresh);
            }

            using (var refreshResponse = await RefreshTokenAsync(refreshToken, ct))
            {
                if (!refreshResponse.IsSuccessStatusCode)
                {
                    return SyncFailure(ErrorTokenRefresh);
                }

                var tokenJson = await refreshResponse.Content.ReadAsStringAsync(ct);
                if (!await UpdateTokensAsync(tokenJson, ct))
                {
                    return SyncFailure(ErrorTokenRefresh);
                }
            }

            var profileCreated = false;
            var accountResponse = await RequestAccountJsonAsync(ct);
            if (!accountResponse.Success)
            {
                return SyncFailure(accountResponse.Error);
            }

            if (!CheckUserData(accountResponse.Json, out var accountError))
            {
                if (!string.Equals(accountError, ErrorProfileAslm, StringComparison.Ordinal))
                {
                    return SyncFailure(accountError);
                }

                bool createSucceeded;
                using (var createResponse = await CreateAslmProfileAsync(ct))
                {
                    createSucceeded = createResponse.IsSuccessStatusCode;
                }

                // Always refetch after the create attempt. A second ASLM client may have
                // created the same profile after our initial read, in which case SUNRISE
                // returns profile_exists while the desired final state is already present.
                accountResponse = await RequestAccountJsonAsync(ct);
                if (!accountResponse.Success)
                {
                    return SyncFailure(createSucceeded ? accountResponse.Error : ErrorProfileCreation);
                }

                if (!CheckUserData(accountResponse.Json, out accountError))
                {
                    if (!createSucceeded && string.Equals(accountError, ErrorProfileAslm, StringComparison.Ordinal))
                    {
                        return SyncFailure(ErrorProfileCreation);
                    }

                    return SyncFailure(accountError);
                }

                profileCreated = createSucceeded;
            }

            if (!await UpdateUserDataAsync(accountResponse.Json, ct))
            {
                return SyncFailure(ErrorUserData);
            }

            return new SunriseAccountSyncResult
            {
                Success = true,
                ProfileCreated = profileCreated,
                Account = _userDataDocument.UserData.Account
            };
        }

        private async Task<(bool Success, string Json, string Error)> RequestAccountJsonAsync(
            CancellationToken ct)
        {
            using var response = await GetAslmUserDataAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return (false, string.Empty, ErrorAccountRequest);
            }

            return (true, await response.Content.ReadAsStringAsync(ct), string.Empty);
        }

        private async Task ApplyCloudAccountAsync(
            SunriseUserAccount account,
            bool selectCloud,
            CancellationToken ct)
        {
            if (selectCloud && _appData.Data.User.AccountMode == AppAccountMode.Local)
            {
                _appData.Data.User.LocalName = _appData.Data.User.Name;
            }

            var profileName = account.Aslm?.Username;
            if (!string.IsNullOrWhiteSpace(profileName))
            {
                _appData.Data.User.Name = profileName.Trim();
            }

            if (selectCloud)
            {
                _appData.Data.User.AccountMode = AppAccountMode.Cloud;
            }

            await _appData.SaveAsync();
        }

        private static SunriseAppAuthenticationResult AuthenticationFailure(string error) => new()
        {
            Error = string.IsNullOrWhiteSpace(error) ? ErrorAccountRequest : error
        };

        private static SunriseAccountSyncResult SyncFailure(string error) => new()
        {
            Error = string.IsNullOrWhiteSpace(error) ? ErrorAccountRequest : error
        };

        private async Task RestoreTokensAfterFailedAuthenticationAsync(
            string refreshToken,
            string accessToken)
        {
            try
            {
                await StoreTokensAsync(refreshToken, accessToken, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore SUNRISE tokens after an unsuccessful authentication attempt.");
            }
        }


        // Token persistence

        /// <summary>
        /// Returns the stored refresh token when SUNRISE credentials are available.
        /// </summary>
        public bool TryGetRefreshToken(out string refreshToken)
        {
            ThrowIfDisposed();

            refreshToken = _tokensData.Jwt.TokenRefresh;
            return _initialized && !string.IsNullOrEmpty(refreshToken);
        }

        /// <summary>
        /// Parses a SUNRISE token response and persists both refresh and access tokens.
        /// </summary>
        public async Task<bool> UpdateTokensAsync(string tokensJson, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            SunriseTokenResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SunriseTokenResponse>(tokensJson, _apiJsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "SUNRISE token response could not be parsed.");
                return false;
            }

            if (response?.Refresh == null || response.Access == null)
            {
                return false;
            }

            await StoreTokensAsync(response.Refresh, response.Access, ct);
            return true;
        }

        /// <summary>
        /// Clears all stored SUNRISE tokens.
        /// </summary>
        public async Task ClearTokensAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await StoreTokensAsync(string.Empty, string.Empty, ct);
        }

        private async Task StoreTokensAsync(
            string refreshToken,
            string accessToken,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);
            await _dataGate.WaitAsync(ct);
            try
            {
                _tokensData.Jwt.TokenRefresh = refreshToken ?? string.Empty;
                _tokensData.Jwt.TokenAccess = accessToken ?? string.Empty;
                _tokensData.Normalize();
                await SaveDocumentCoreAsync(_tokensFilePath, _tokensData, ct);
            }
            finally
            {
                _dataGate.Release();
            }
        }


        // User data persistence and validation

        /// <summary>
        /// Clears the cached and persisted SUNRISE account graph.
        /// </summary>
        public async Task ClearUserDataAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            await _dataGate.WaitAsync(ct);
            try
            {
                _userDataDocument = CreateDefaultUserDataDocument();
                _userDataDocument.Normalize();
                await SaveDocumentCoreAsync(_userDataFilePath, _userDataDocument, ct);
            }
            finally
            {
                _dataGate.Release();
            }
        }

        /// <summary>
        /// Validates the account and ASLM state returned in a <c>user_data</c> API response.
        /// </summary>
        public bool CheckUserData(string json, out string errorCode)
        {
            errorCode = string.Empty;

            SunriseUserDataResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SunriseUserDataResponse>(json, _apiJsonOptions);
            }
            catch (JsonException)
            {
                errorCode = ErrorUserData;
                return false;
            }

            var account = response?.UserData;
            if (account == null)
            {
                errorCode = ErrorUserData;
                return false;
            }

            if (!account.IsActive)
            {
                errorCode = ErrorUserIsActive;
                return false;
            }

            if (account.Aslm == null)
            {
                errorCode = ErrorProfileAslm;
                return false;
            }

            if (!account.Aslm.IsActive)
            {
                errorCode = ErrorAslmIsActive;
                return false;
            }

            if (account.Aslm.IsBanned)
            {
                errorCode = ErrorAslmIsBanned;
                return false;
            }

            return true;
        }

        /// <summary>
        /// Parses and persists the account contained in a <c>user_data</c> API response.
        /// </summary>
        public async Task<bool> UpdateUserDataAsync(string json, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            SunriseUserDataResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SunriseUserDataResponse>(json, _apiJsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "SUNRISE user-data response could not be parsed.");
                return false;
            }

            if (response?.UserData == null)
            {
                return false;
            }

            response.UserData.Normalize();

            await _dataGate.WaitAsync(ct);
            try
            {
                _userDataDocument.UserData.Account = response.UserData;
                _userDataDocument.Normalize();
                await SaveDocumentCoreAsync(_userDataFilePath, _userDataDocument, ct);
                return true;
            }
            finally
            {
                _dataGate.Release();
            }
        }

        /// <summary>
        /// Persists the current user-data model after caller changes.
        /// </summary>
        public async Task SaveUserDataAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            await _dataGate.WaitAsync(ct);
            try
            {
                _userDataDocument.Normalize();
                await SaveDocumentCoreAsync(_userDataFilePath, _userDataDocument, ct);
            }
            finally
            {
                _dataGate.Release();
            }
        }


        // Browser callback helpers

        private static async Task<SunriseCallbackPayload> ReceiveAuthenticationCallbackAsync(
            TcpListener listener,
            string expectedState,
            Uri successRedirectUri,
            CancellationToken ct)
        {
            InvalidDataException? lastError = null;
            for (var attempt = 0; attempt < CallbackConnectionLimit; attempt++)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndpoint ||
                    !IPAddress.IsLoopback(remoteEndpoint.Address))
                {
                    lastError = new InvalidDataException("The callback did not originate from loopback.");
                    continue;
                }

                // Bind on 127.0.0.1. Accept only the exact branded Host so the browser
                // must have followed the intended redirect and other names are rejected.

                try
                {
                    var request = await ReadCallbackRequestAsync(client.GetStream(), ct);
                    var callbackPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var payload = ParseCallbackRequest(
                        request.Method,
                        request.Target,
                        request.Headers,
                        request.Body,
                        $"{CallbackHostName}:{callbackPort}");
                    if (!StateMatches(expectedState, payload.State))
                    {
                        throw new InvalidDataException("The callback state did not match the authorization attempt.");
                    }

                    await WriteCallbackRedirectAsync(client.GetStream(), successRedirectUri, ct);
                    return payload;
                }
                catch (InvalidDataException ex)
                {
                    lastError = ex;
                    try
                    {
                        await WriteCallbackErrorResponseAsync(client.GetStream(), ct);
                    }
                    catch (Exception responseError) when (responseError is IOException or SocketException)
                    {
                        // The rejected peer may close before reading the response.
                    }
                }
            }

            throw lastError ?? new InvalidDataException("No valid SUNRISE callback was received.");
        }

        private static async Task<(
            string Method,
            string Target,
            Dictionary<string, string> Headers,
            byte[] Body)> ReadCallbackRequestAsync(
                NetworkStream stream,
                CancellationToken ct)
        {
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (read == 0)
                {
                    throw new InvalidDataException("The callback request ended before its headers were complete.");
                }

                received.Write(buffer, 0, read);
                headerEnd = FindHeaderTerminator(received.GetBuffer(), checked((int)received.Length));
                if (headerEnd < 0 && received.Length > CallbackHeaderLimit)
                {
                    throw new InvalidDataException("The callback headers exceeded their size limit.");
                }
            }

            if (headerEnd > CallbackHeaderLimit)
            {
                throw new InvalidDataException("The callback headers exceeded their size limit.");
            }

            var allBytes = received.GetBuffer();
            for (var index = 0; index < headerEnd; index++)
            {
                if (allBytes[index] > 0x7f)
                {
                    throw new InvalidDataException("The callback headers were not ASCII.");
                }
            }

            var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length != 3 || !requestParts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The callback request line was invalid.");
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 1; index < lines.Length; index++)
            {
                var separator = lines[index].IndexOf(':');
                if (separator <= 0)
                {
                    throw new InvalidDataException("A callback header was invalid.");
                }

                var name = lines[index][..separator].Trim();
                var value = lines[index][(separator + 1)..].Trim();
                if (string.IsNullOrEmpty(name) || !headers.TryAdd(name, value))
                {
                    throw new InvalidDataException("A callback header was empty or duplicated.");
                }
            }

            if (headers.ContainsKey("Transfer-Encoding") ||
                !headers.TryGetValue("Content-Length", out var lengthText) ||
                !int.TryParse(lengthText, out var contentLength) ||
                contentLength <= 0 || contentLength > CallbackBodyLimit)
            {
                throw new InvalidDataException("The callback content length was invalid.");
            }

            var bodyOffset = headerEnd + 4;
            var bufferedBodyLength = checked((int)received.Length) - bodyOffset;
            if (bufferedBodyLength > contentLength)
            {
                throw new InvalidDataException("The callback contained unexpected trailing data.");
            }

            var body = new byte[contentLength];
            if (bufferedBodyLength > 0)
            {
                Buffer.BlockCopy(allBytes, bodyOffset, body, 0, bufferedBodyLength);
            }

            var bodyRead = bufferedBodyLength;
            while (bodyRead < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(bodyRead, contentLength - bodyRead), ct);
                if (read == 0)
                {
                    throw new InvalidDataException("The callback body ended early.");
                }

                bodyRead += read;
            }

            return (requestParts[0], requestParts[1], headers, body);
        }

        private static SunriseCallbackPayload ParseCallbackRequest(
            string method,
            string target,
            IReadOnlyDictionary<string, string> headers,
            byte[] body,
            string expectedHost)
        {
            if (!string.Equals(method, "POST", StringComparison.Ordinal) ||
                !string.Equals(target, "/sunrise-auth/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The callback must be a POST to /sunrise-auth/.");
            }

            if (!headers.TryGetValue("Host", out var host) ||
                !string.Equals(host, expectedHost, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The callback Host header must be exactly '{expectedHost}'.");
            }

            if (!headers.TryGetValue("Content-Type", out var contentType) ||
                !string.Equals(
                    contentType.Split(';', 2)[0].Trim(),
                    "application/x-www-form-urlencoded",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The callback content type was invalid.");
            }

            string formText;
            try
            {
                formText = new UTF8Encoding(false, true).GetString(body);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("The callback body was not valid UTF-8.", ex);
            }

            var form = ParseFormBody(formText);
            if (!form.TryGetValue("state", out var state) || !IsValidCallbackState(state) ||
                !form.TryGetValue("access", out var access) || string.IsNullOrWhiteSpace(access) ||
                !form.TryGetValue("refresh", out var refresh) || string.IsNullOrWhiteSpace(refresh))
            {
                throw new InvalidDataException("The callback did not contain a complete token payload.");
            }

            return new SunriseCallbackPayload
            {
                State = state,
                Access = access,
                Refresh = refresh
            };
        }

        private static Dictionary<string, string> ParseFormBody(string formBody)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in formBody.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                {
                    throw new InvalidDataException("The callback form body was malformed.");
                }

                var name = DecodeFormValue(pair[..separator]);
                var value = DecodeFormValue(pair[(separator + 1)..]);
                if (!values.TryAdd(name, value))
                {
                    throw new InvalidDataException("The callback form contained a duplicated field.");
                }
            }

            return values;
        }

        private static string DecodeFormValue(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            catch (UriFormatException ex)
            {
                throw new InvalidDataException("The callback form contained invalid escaping.", ex);
            }
        }

        private static async Task WriteCallbackRedirectAsync(
            NetworkStream stream,
            Uri redirectUri,
            CancellationToken ct)
        {
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 303 See Other\r\n" +
                $"Location: {redirectUri.AbsoluteUri}\r\n" +
                "Content-Length: 0\r\n" +
                "Cache-Control: no-store\r\n" +
                "Pragma: no-cache\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(response.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }

        private static async Task WriteCallbackErrorResponseAsync(
            NetworkStream stream,
            CancellationToken ct)
        {
            const string message = "ASLM rejected this authentication response. Return to ASLM and try again.";
            var bodyBytes = Encoding.UTF8.GetBytes(message);
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 400 Bad Request\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Cache-Control: no-store\r\n" +
                "Pragma: no-cache\r\n" +
                "Referrer-Policy: no-referrer\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(response.AsMemory(), ct);
            await stream.WriteAsync(bodyBytes.AsMemory(), ct);
            await stream.FlushAsync(ct);
        }

        private static int FindHeaderTerminator(byte[] bytes, int length)
        {
            for (var index = 0; index <= length - 4; index++)
            {
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' &&
                    bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                {
                    return index;
                }
            }

            return -1;
        }

        private static string CreateCallbackState()
        {
            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return state.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static bool IsValidCallbackState(string? state)
        {
            if (state == null || state.Length is < 22 or > 128)
            {
                return false;
            }

            return state.All(character =>
                character is >= 'A' and <= 'Z' or
                >= 'a' and <= 'z' or
                >= '0' and <= '9' or
                '-' or '_');
        }

        private static bool StateMatches(string expected, string received)
        {
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var receivedBytes = Encoding.ASCII.GetBytes(received);
            return expectedBytes.Length == receivedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, receivedBytes);
        }

        private static bool IsValidLoopbackRedirectUri(Uri redirectUri) =>
            redirectUri.IsAbsoluteUri &&
            string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) &&
            string.Equals(redirectUri.Host, CallbackHostName, StringComparison.Ordinal) &&
            redirectUri.Port is >= 1024 and <= 65535 &&
            string.Equals(redirectUri.Authority, $"{CallbackHostName}:{redirectUri.Port}", StringComparison.Ordinal) &&
            string.Equals(redirectUri.AbsolutePath, "/sunrise-auth/", StringComparison.Ordinal) &&
            string.IsNullOrEmpty(redirectUri.Query) &&
            string.IsNullOrEmpty(redirectUri.Fragment) &&
            string.IsNullOrEmpty(redirectUri.UserInfo);


        // Header and payload helpers

        /// <summary>
        /// Applies every supplied request or content header without imposing a fixed count.
        /// </summary>
        private static void ApplyHeaders(HttpRequestMessage request, SunriseHeader[]? headers)
        {
            if (headers == null || headers.Length == 0)
            {
                return;
            }

            foreach (var header in headers)
            {
                if (header == null || string.IsNullOrWhiteSpace(header.Name))
                {
                    throw new ArgumentException("SUNRISE request headers must have a non-empty name.", nameof(headers));
                }

                var headerName = header.Name.Trim();
                var headerValue = header.Value ?? string.Empty;

                if (request.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    continue;
                }

                // HttpClient separates entity headers such as Content-Type from request headers.
                // An empty content container lets a bodyless request still carry such a header.
                request.Content ??= new ByteArrayContent([]);
                if (!request.Content.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    throw new ArgumentException(
                        $"'{headerName}' is not a valid SUNRISE request or content header.",
                        nameof(headers));
                }
            }
        }

        /// <summary>
        /// Creates UTF-8 request content without adding an implicit media type.
        /// </summary>
        private static HttpContent CreateBodyContent(string body) =>
            new ByteArrayContent(Encoding.UTF8.GetBytes(body));

        /// <summary>
        /// Normalizes supported request verbs and falls back to GET.
        /// </summary>
        private static HttpMethod NormalizeHttpMethod(string? httpRequestVerb) =>
            httpRequestVerb?.Trim().ToUpperInvariant() switch
            {
                "POST" => HttpMethod.Post,
                "PUT" => HttpMethod.Put,
                "DELETE" => HttpMethod.Delete,
                _ => HttpMethod.Get
            };

        /// <summary>
        /// Serializes one strongly typed SUNRISE API payload.
        /// </summary>
        private string SerializeApiPayload<T>(T payload) =>
            JsonSerializer.Serialize(payload, _apiJsonOptions);


        // Storage helpers

        /// <summary>
        /// Loads and normalizes one typed JSON document or creates its defaults when unavailable.
        /// </summary>
        private async Task<T> LoadDocumentAsync<T>(
            string filePath,
            Func<T> createDefault,
            Action<T> normalize,
            bool persistDefaults,
            CancellationToken ct)
            where T : class
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var json = await File.ReadAllTextAsync(filePath, ct);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonSerializer.Deserialize<T>(json, _storageJsonOptions);
                        if (loaded != null)
                        {
                            normalize(loaded);
                            return loaded;
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.LogError(
                    ex,
                    "Failed to load SUNRISE data from {FilePath}. Falling back to defaults.",
                    filePath);
            }

            var defaults = createDefault();
            normalize(defaults);
            if (persistDefaults)
            {
                await SaveDocumentCoreAsync(filePath, defaults, ct);
            }

            return defaults;
        }

        /// <summary>
        /// Persists one typed SUNRISE document below the shared application data root.
        /// </summary>
        private async Task SaveDocumentCoreAsync<T>(string filePath, T data, CancellationToken ct)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, _storageJsonOptions);
            await File.WriteAllTextAsync(filePath, json, ct);
        }


        // Configuration defaults and migrations

        /// <summary>
        /// Ensures the default NGGT OSS domain exists without replacing user-configured values.
        /// </summary>
        private bool EnsureDefaultDomain()
        {
            _domainsData.Normalize();
            var removed = _domainsData.Domains.RemoveAll(domain =>
                string.IsNullOrWhiteSpace(domain.Protocol) ||
                string.IsNullOrWhiteSpace(domain.Domain) ||
                string.IsNullOrWhiteSpace(domain.DomainType));
            var changed = removed > 0;

            if (_domainsData.Domains.Any(domain =>
                    string.Equals(domain.DomainType, DefaultDomainType, StringComparison.Ordinal)))
            {
                return changed;
            }

            _domainsData.Domains.Add(CreateDefaultDomain());
            return true;
        }

        /// <summary>
        /// Adds current endpoints that are absent from the persisted catalog.
        /// </summary>
        private bool EnsureDefaultUrls()
        {
            _urlsData.Normalize();
            var changed = false;

            var removed = _urlsData.Urls.RemoveAll(url =>
                string.IsNullOrWhiteSpace(url.Name) ||
                string.IsNullOrWhiteSpace(url.Url) ||
                string.IsNullOrWhiteSpace(url.DomainType));
            changed |= removed > 0;

            foreach (var defaultUrl in CreateDefaultUrls())
            {
                if (_urlsData.Urls.Any(url =>
                        string.Equals(url.Name, defaultUrl.Name, StringComparison.Ordinal)))
                {
                    continue;
                }

                _urlsData.Urls.Add(defaultUrl);
                changed = true;
            }

            if (_urlsData.FileVersion < CurrentFileVersion)
            {
                _urlsData.FileVersion = CurrentFileVersion;
                changed = true;
            }

            return changed;
        }

        private static SunriseDomainsData CreateDefaultDomainsData() => new()
        {
            FileVersion = CurrentFileVersion,
            Domains = [CreateDefaultDomain()]
        };

        private static SunriseDomain CreateDefaultDomain() => new()
        {
            Protocol = "http",
            Domain = "127.0.0.1:8000",
            DomainType = DefaultDomainType
        };

        private static SunriseUrlsData CreateDefaultUrlsData() => new()
        {
            FileVersion = CurrentFileVersion,
            Urls = CreateDefaultUrls()
        };

        private static List<SunriseUrl> CreateDefaultUrls() =>
        [
            CreateUrl(SignupEndpoint, "api/id/signup/"),
            CreateUrl(AuthenticationEndpoint, "api/id/token/"),
            CreateUrl(RefreshEndpoint, "api/id/token/refresh/"),
            CreateUrl(VerifyEndpoint, "api/id/token/verify/"),
            CreateUrl(PasswordRecoveryEndpoint, "id/"),
            CreateUrl(ApplicationAuthenticationEndpoint, "id/authapp/"),
            CreateUrl(ApplicationAuthenticationSuccessEndpoint, "id/authapp/success/"),
            CreateUrl(AslmGetUserDataEndpoint, "api/aslm/getuserdata/"),
            CreateUrl(AslmCreateProfileEndpoint, "api/aslm/createprofile/")
        ];

        private static SunriseUrl CreateUrl(string name, string url) => new()
        {
            Name = name,
            Url = url,
            DomainType = DefaultDomainType
        };

        private static SunriseTokensData CreateDefaultTokensData() => new()
        {
            FileVersion = CurrentFileVersion,
            Jwt = new SunriseJwtTokens()
        };

        private static SunriseUserDataDocument CreateDefaultUserDataDocument() => new()
        {
            FileVersion = CurrentFileVersion,
            UserData = new SunriseUserData
            {
                Account = new SunriseUserAccount
                {
                    Aslm = new SunriseAslmProfile()
                },
                SavedAccounts = [new SunriseSavedAccount()]
            }
        };


        // Lifecycle helpers

        /// <summary>
        /// Ensures a synchronous operation only uses initialized service state.
        /// </summary>
        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("SunriseService must be initialized before this operation.");
            }
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        /// <summary>
        /// Releases the process-wide HTTP client and synchronization primitive.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _httpClient.Dispose();
            _dataGate.Dispose();
            _accountOperationGate.Dispose();
        }
    }
}
