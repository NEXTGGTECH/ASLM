// Copyright NEXTGGTECH. Apache License 2.0.

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
        // Build configuration

        /// <summary>
        /// Whether this build enables ASLM accounts (configured in ASLM.csproj).
        /// </summary>
        public static bool IsEnabled { get; } =
#if ASLM_ACCOUNT_ENABLED
            true;
#else
            false;
#endif


        // Constants and limits

        /// <summary>
        /// Schema version written to the persisted SUNRISE documents.
        /// </summary>
        private const int CurrentFileVersion = 2;

        /// <summary>
        /// Maximum size in bytes of the HTTP request line and headers accepted on the authentication callback.
        /// </summary>
        private const int CallbackHeaderLimit = 16 * 1024;

        /// <summary>
        /// Maximum size in bytes of the form body accepted on the authentication callback.
        /// </summary>
        private const int CallbackBodyLimit = 32 * 1024;

        /// <summary>
        /// Maximum number of connections examined while waiting for one valid authentication callback.
        /// </summary>
        private const int CallbackConnectionLimit = 8;

        /// <summary>
        /// How long the browser authentication may take before it is abandoned.
        /// </summary>
        private static readonly TimeSpan CallbackTimeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Branded loopback host for the one-time auth callback.
        /// SUNRISE redirects here; listener accepts only this exact Host header.
        /// </summary>
        private const string CallbackHostName = "aslm.nextggtech.localhost";

        /// <summary>
        /// File name of the persisted SUNRISE domain catalog below <c>Data/App</c>.
        /// </summary>
        private const string DomainsFileName = "SUNRISE_Domains.json";

        /// <summary>
        /// File name of the persisted SUNRISE endpoint catalog below <c>Data/App</c>.
        /// </summary>
        private const string UrlsFileName = "SUNRISE_URLs.json";

        /// <summary>
        /// File name of the persisted JWT tokens below <c>Data/App</c>.
        /// </summary>
        private const string TokensFileName = "SUNRISE_Tokens.json";

        /// <summary>
        /// File name of the persisted SUNRISE account data below <c>Data/App</c>.
        /// </summary>
        private const string UserDataFileName = "SUNRISE_UserData.json";

        /// <summary>
        /// Domain type of the default SUNRISE backend, which every default endpoint belongs to.
        /// </summary>
        private const string DefaultDomainType = "NGGT_OSS";

        /// <summary>
        /// Application identifier sent to SUNRISE when starting the browser authentication.
        /// </summary>
        private const string ApplicationIdentifier = "ASLM";


        // Endpoint names

        /// <summary>
        /// Endpoint name of the account sign-up API.
        /// </summary>
        public const string SignupEndpoint = "API_Singup";

        /// <summary>
        /// Endpoint name of the API that exchanges credentials for a JWT token pair.
        /// </summary>
        public const string AuthenticationEndpoint = "JWT_Auth";

        /// <summary>
        /// Endpoint name of the API that exchanges a refresh token for a new token pair.
        /// </summary>
        public const string RefreshEndpoint = "JWT_Refresh";

        /// <summary>
        /// Endpoint name of the API that verifies a refresh or access token.
        /// </summary>
        public const string VerifyEndpoint = "JWT_Verify";

        /// <summary>
        /// Endpoint name of the SUNRISE profile web page.
        /// </summary>
        public const string ProfileEndpoint = "WEB_Profile";

        /// <summary>
        /// Endpoint name of the SUNRISE password recovery web page.
        /// </summary>
        public const string PasswordRecoveryEndpoint = "WEB_PasswordRecovery";

        /// <summary>
        /// Endpoint name of the web page that authorizes ASLM in the browser.
        /// </summary>
        public const string ApplicationAuthenticationEndpoint = "WEB_AuthApp";

        /// <summary>
        /// Endpoint name of the web page the browser is sent to after the callback was accepted.
        /// </summary>
        public const string ApplicationAuthenticationSuccessEndpoint = "WEB_AuthAppSuccess";

        /// <summary>
        /// Endpoint name of the API that returns the account and its ASLM profile.
        /// </summary>
        public const string AslmGetUserDataEndpoint = "API_AslmGetUserData";

        /// <summary>
        /// Endpoint name of the API that creates the ASLM profile of an account.
        /// </summary>
        public const string AslmCreateProfileEndpoint = "API_AslmCreateProfile";


        // Error codes

        /// <summary>
        /// Error code: the account data response could not be read or contains no account.
        /// </summary>
        public const string ErrorUserData = "ErrorUserData";

        /// <summary>
        /// Error code: the SUNRISE account is not active.
        /// </summary>
        public const string ErrorUserIsActive = "ErrorUserIsActive";

        /// <summary>
        /// Error code: the account has no ASLM profile yet.
        /// </summary>
        public const string ErrorProfileAslm = "ErrorProfileASLM";

        /// <summary>
        /// Error code: the ASLM profile is not active.
        /// </summary>
        public const string ErrorAslmIsActive = "ErrorAslmIsActive";

        /// <summary>
        /// Error code: the ASLM profile is banned.
        /// </summary>
        public const string ErrorAslmIsBanned = "ErrorAslmIsBanned";

        /// <summary>
        /// Error code: the system browser could not be opened for the authentication.
        /// </summary>
        public const string ErrorBrowserLaunch = "ErrorBrowserLaunch";

        /// <summary>
        /// Error code: the user or the application cancelled the browser authentication.
        /// </summary>
        public const string ErrorAuthenticationCancelled = "ErrorAuthenticationCancelled";

        /// <summary>
        /// Error code: no callback arrived before the authentication timeout.
        /// </summary>
        public const string ErrorAuthenticationTimeout = "ErrorAuthenticationTimeout";

        /// <summary>
        /// Error code: the authentication callback was rejected, or the sign-in failed with a network or I/O error.
        /// </summary>
        public const string ErrorAuthenticationCallback = "ErrorAuthenticationCallback";

        /// <summary>
        /// Error code: no refresh token is stored or SUNRISE did not return usable tokens for it.
        /// </summary>
        public const string ErrorTokenRefresh = "ErrorTokenRefresh";

        /// <summary>
        /// Error code: the account request failed; also the fallback when a failure carries no code.
        /// </summary>
        public const string ErrorAccountRequest = "ErrorAccountRequest";

        /// <summary>
        /// Error code: the missing ASLM profile could not be created.
        /// </summary>
        public const string ErrorProfileCreation = "ErrorProfileCreation";


        // Dependencies and file paths

        /// <summary>
        /// Logger for request, storage and authentication problems; it never receives tokens or passwords.
        /// </summary>
        private readonly ILogger<SunriseService> _logger;

        /// <summary>
        /// Persisted application settings that hold the selected account mode and user name.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// HTTP client for SUNRISE requests, created on first use.
        /// </summary>
        private readonly Lazy<HttpClient> _httpClient;

        /// <summary>
        /// Serializes access to the cached documents and their files.
        /// </summary>
        private readonly SemaphoreSlim _dataGate = new(1, 1);

        /// <summary>
        /// Allows only one account operation (sign-in, synchronization, local switch) at a time.
        /// </summary>
        private readonly SemaphoreSlim _accountOperationGate = new(1, 1);

        /// <summary>
        /// Absolute path of the domain catalog file.
        /// </summary>
        private readonly string _domainsFilePath;

        /// <summary>
        /// Absolute path of the endpoint catalog file.
        /// </summary>
        private readonly string _urlsFilePath;

        /// <summary>
        /// Absolute path of the JWT token file.
        /// </summary>
        private readonly string _tokensFilePath;

        /// <summary>
        /// Absolute path of the account data file.
        /// </summary>
        private readonly string _userDataFilePath;


        // Serialization and cached documents

        /// <summary>
        /// JSON settings for the persisted SUNRISE documents: indented, case-insensitive, nulls omitted.
        /// </summary>
        private readonly JsonSerializerOptions _storageJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// JSON settings for SUNRISE API requests and responses; property names are case-insensitive.
        /// </summary>
        private readonly JsonSerializerOptions _apiJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Cached domain catalog; replaced with the persisted one during initialization.
        /// </summary>
        private SunriseDomainsData _domainsData = CreateDefaultDomainsData();

        /// <summary>
        /// Cached endpoint catalog; replaced with the persisted one during initialization.
        /// </summary>
        private SunriseUrlsData _urlsData = CreateDefaultUrlsData();

        /// <summary>
        /// Cached JWT tokens; replaced with the persisted ones during initialization.
        /// </summary>
        private SunriseTokensData _tokensData = CreateDefaultTokensData();

        /// <summary>
        /// Cached account data; replaced with the persisted document during initialization.
        /// </summary>
        private SunriseUserDataDocument _userDataDocument = CreateDefaultUserDataDocument();

        /// <summary>
        /// Set once the persisted documents were loaded.
        /// </summary>
        private bool _initialized;

        /// <summary>
        /// Set once the service was disposed.
        /// </summary>
        private bool _disposed;


        // State access

        /// <summary>
        /// Gets the currently loaded SUNRISE domain catalog (protocol and host per domain type).
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
        public bool IsCloudAccount => IsEnabled && AccountMode == AppAccountMode.Cloud;


        // Construction

        /// <summary>
        /// Creates the service and resolves all persisted data paths below <c>Data/App</c>.
        /// The HTTP client is created lazily on the first request.
        /// </summary>
        public SunriseService(ILogger<SunriseService> logger, AppDataStore appData)
            : this(logger, appData, new Lazy<HttpClient>(() => new HttpClient()))
        {
        }

        /// <summary>
        /// Creates the service with an explicit HTTP client factory (test isolation).
        /// </summary>
        internal SunriseService(ILogger<SunriseService> logger, AppDataStore appData, Lazy<HttpClient> httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _appData = appData ?? throw new ArgumentNullException(nameof(appData));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            var dataDirectory = Path.Combine(AppRoot.Directory, "Data", "App");
            _domainsFilePath = Path.Combine(dataDirectory, DomainsFileName);
            _urlsFilePath = Path.Combine(dataDirectory, UrlsFileName);
            _tokensFilePath = Path.Combine(dataDirectory, TokensFileName);
            _userDataFilePath = Path.Combine(dataDirectory, UserDataFileName);
        }


        // Initialization

        /// <summary>
        /// Loads all SUNRISE configuration and account data once for the process lifetime.
        /// Later calls return immediately.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (!IsEnabled)
            {
                throw new NotSupportedException("ASLM accounts are disabled in this build.");
            }

            await _dataGate.WaitAsync(ct);
            try
            {
                if (_initialized)
                {
                    return;
                }

                // Load the four documents. Domains and URLs persist their defaults when the files are missing
                // or unreadable; tokens and account data stay in memory until the first real write.
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

                // Add the default domain and endpoints that older files lack, and save only what changed.
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
        /// The service must be initialized; an unknown endpoint, a missing domain type or an invalid URI throws.
        /// </summary>
        public Uri CreateFullUri(string endpointName)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(endpointName))
            {
                throw new ArgumentException("SUNRISE endpoint name is required.", nameof(endpointName));
            }

            // Find the endpoint by its exact name.
            var endpoint = _urlsData.Urls.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, endpointName, StringComparison.Ordinal));
            if (endpoint == null)
            {
                throw new KeyNotFoundException($"SUNRISE endpoint '{endpointName}' is not configured.");
            }

            // Find the domain that the endpoint belongs to and check that it is complete.
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

            // Combine the domain's base address with the endpoint's relative path.
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
        /// The URI carries the application id, the callback address and the anti-forgery state as query parameters.
        /// </summary>
        public Uri GetApplicationAuthenticationUri(Uri redirectUri, string state)
        {
            ArgumentNullException.ThrowIfNull(redirectUri);

            // Only the fixed branded loopback callback and a well-formed state may be sent to SUNRISE.
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

            // Append to an existing query when the configured endpoint already has one.
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
        /// The URI must be an HTTP or HTTPS address without user information, because the browser is
        /// redirected to it.
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

            // Resolve the target and build the request; only POST and PUT carry a body.
            var method = NormalizeHttpMethod(httpRequestVerb);
            var uri = CreateFullUri(endpointName);

            using var request = new HttpRequestMessage(method, uri);
            if (method == HttpMethod.Post || method == HttpMethod.Put)
            {
                request.Content = CreateBodyContent(body ?? string.Empty);
            }

            ApplyHeaders(request, headers);

            // Send the request and return the fully buffered response to the caller.
            try
            {
                return await _httpClient.Value.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead,
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled the request: pass the cancellation on without logging it.
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
        /// The response is returned to the caller, which must dispose it.
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
        /// The response is returned to the caller, which must dispose it.
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
        /// The response is returned to the caller, which must dispose it.
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
        /// The sign-up request is tagged as coming from ASLM.
        /// The response is returned to the caller, which must dispose it.
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
        /// Sends one JWT-authorized ASLM request: an empty POST that carries the stored access token
        /// in the <c>Authorization: JWT</c> header.
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
        /// Failures are returned as an error code in the result; the previous credentials are restored when
        /// the new ones cannot be used.
        /// </summary>
        public async Task<SunriseAppAuthenticationResult> AuthenticateApplicationAsync(
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await _accountOperationGate.WaitAsync(ct);
            try
            {
                await InitializeAsync(ct);

                // Remember the current credentials for a rollback, and create the one-time anti-forgery state.
                var previousRefresh = _tokensData.Jwt.TokenRefresh;
                var previousAccess = _tokensData.Jwt.TokenAccess;
                var state = CreateCallbackState();

                // Listen on a free loopback port; the redirect address uses the branded host name instead.
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start(1);

                var localEndpoint = (IPEndPoint)listener.LocalEndpoint;
                var redirectUri = new Uri($"http://{CallbackHostName}:{localEndpoint.Port}/sunrise-auth/");
                var authenticationUri = GetApplicationAuthenticationUri(redirectUri, state);
                var authenticationSuccessUri = GetApplicationAuthenticationSuccessUri();

                // Send the user to the SUNRISE authorization page in the system browser.
                bool browserOpened;
                try
                {
                    browserOpened = await Launcher.Default.OpenAsync(authenticationUri);
                }
                catch (Exception ex) when (ex is FeatureNotSupportedException or InvalidOperationException)
                {
                    // The platform cannot launch a browser, so the flow ends before waiting for a callback.
                    _logger.LogWarning(ex, "The SUNRISE authentication page could not be opened.");
                    return AuthenticationFailure(ErrorBrowserLaunch);
                }

                if (!browserOpened)
                {
                    return AuthenticationFailure(ErrorBrowserLaunch);
                }

                // Wait for the browser to post the tokens back, but not longer than the callback timeout.
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
                    // Only the internal timeout fired, so the user did not finish in time.
                    return AuthenticationFailure(ErrorAuthenticationTimeout);
                }
                catch (OperationCanceledException)
                {
                    // The caller cancelled the sign-in.
                    return AuthenticationFailure(ErrorAuthenticationCancelled);
                }
                catch (InvalidDataException ex)
                {
                    // No valid callback arrived within the connection limit.
                    _logger.LogWarning(ex, "The SUNRISE authentication callback was rejected.");
                    return AuthenticationFailure(ErrorAuthenticationCallback);
                }

                // Store the received tokens, then validate them by loading the account with them.
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

                    // The tokens work: select the cloud account and take over its profile name.
                    await ApplyCloudAccountAsync(syncResult.Account, selectCloud: true, ct);
                    return new SunriseAppAuthenticationResult
                    {
                        Success = true,
                        Account = syncResult.Account
                    };
                }
                catch
                {
                    // Any unexpected failure also rolls the credentials back before it propagates.
                    await RestoreTokensAfterFailedAuthenticationAsync(previousRefresh, previousAccess);
                    throw;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller cancelled the sign-in while the tokens were being stored or validated.
                return AuthenticationFailure(ErrorAuthenticationCancelled);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
            {
                // Network and storage failures become a result, so the caller can show the error.
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
        /// The synchronization is skipped, and reported as successful, when accounts are disabled
        /// or the local account is selected.
        /// </summary>
        public async Task<SunriseAccountSyncResult> SynchronizeCloudAccountAsync(
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (!IsEnabled)
            {
                return new SunriseAccountSyncResult { Success = true, Skipped = true };
            }

            await _accountOperationGate.WaitAsync(ct);
            try
            {
                await InitializeAsync(ct);

                // Local accounts have nothing to synchronize.
                if (!IsCloudAccount)
                {
                    return new SunriseAccountSyncResult
                    {
                        Success = true,
                        Skipped = true,
                        Account = _userDataDocument.UserData.Account
                    };
                }

                // Refresh the tokens and account; a successful result updates the stored profile name.
                var result = await SynchronizeAccountCoreAsync(ct);
                if (result.Success && result.Account != null)
                {
                    await ApplyCloudAccountAsync(result.Account, selectCloud: false, ct);
                }

                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is the caller's decision and is not turned into a result.
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

        /// <summary>
        /// Exchanges the stored refresh token for new tokens, loads the account, creates the ASLM profile when it
        /// is missing, validates the account state and persists it. Failures are returned as an error code.
        /// The caller holds the account operation gate.
        /// </summary>
        private async Task<SunriseAccountSyncResult> SynchronizeAccountCoreAsync(CancellationToken ct)
        {
            // A refresh token is required, and SUNRISE must accept it and return a new token pair.
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

            // Load the account with the new access token.
            var profileCreated = false;
            var accountResponse = await RequestAccountJsonAsync(ct);
            if (!accountResponse.Success)
            {
                return SyncFailure(accountResponse.Error);
            }

            // Any account problem other than a missing ASLM profile ends the synchronization.
            if (!CheckUserData(accountResponse.Json, out var accountError))
            {
                if (!string.Equals(accountError, ErrorProfileAslm, StringComparison.Ordinal))
                {
                    return SyncFailure(accountError);
                }

                // The account has no ASLM profile yet: try to create it once.
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

                // Still no profile after a failed create means the profile could not be created.
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

            // Persist the validated account and report it.
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

        /// <summary>
        /// Requests the account JSON with the stored access token; a non-success status yields the account request error.
        /// </summary>
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

        /// <summary>
        /// Applies a synchronized account to the application settings: the user name becomes the ASLM profile name
        /// and, when requested, the cloud account mode is selected. The settings are saved afterwards.
        /// </summary>
        /// <param name="account">Validated account returned by SUNRISE.</param>
        /// <param name="selectCloud">Whether to switch the application to the cloud account mode.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task ApplyCloudAccountAsync(
            SunriseUserAccount account,
            bool selectCloud,
            CancellationToken ct)
        {
            // Switching from local mode: keep the local name so it can be restored later.
            if (selectCloud && _appData.Data.User.AccountMode == AppAccountMode.Local)
            {
                _appData.Data.User.LocalName = _appData.Data.User.Name;
            }

            // Show the ASLM profile name as the user name.
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

        /// <summary>
        /// Creates a failed authentication result; an empty error code becomes the account request error.
        /// </summary>
        private static SunriseAppAuthenticationResult AuthenticationFailure(string error) => new()
        {
            Error = string.IsNullOrWhiteSpace(error) ? ErrorAccountRequest : error
        };

        /// <summary>
        /// Creates a failed synchronization result; an empty error code becomes the account request error.
        /// </summary>
        private static SunriseAccountSyncResult SyncFailure(string error) => new()
        {
            Error = string.IsNullOrWhiteSpace(error) ? ErrorAccountRequest : error
        };

        /// <summary>
        /// Writes the credentials that existed before a failed browser sign-in back to storage.
        /// A failure to restore is only logged, so the original authentication error is still reported.
        /// </summary>
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
        /// Returns false when accounts are disabled, the service is not initialized yet or no token is stored.
        /// </summary>
        public bool TryGetRefreshToken(out string refreshToken)
        {
            ThrowIfDisposed();

            refreshToken = IsEnabled ? _tokensData.Jwt.TokenRefresh : string.Empty;
            return _initialized && !string.IsNullOrEmpty(refreshToken);
        }

        /// <summary>
        /// Parses a SUNRISE token response and persists both refresh and access tokens.
        /// Returns false, without changing the stored tokens, when the response is malformed or lacks either token.
        /// </summary>
        public async Task<bool> UpdateTokensAsync(string tokensJson, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            // Parse the response without logging its content, which contains the tokens.
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

        /// <summary>
        /// Replaces the cached tokens and writes them to the token file under the data gate.
        /// Empty values clear the stored credentials.
        /// </summary>
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
        /// Returns false with the first failed check as <paramref name="errorCode"/>: unreadable data, inactive
        /// account, missing ASLM profile, inactive profile, banned profile.
        /// </summary>
        public bool CheckUserData(string json, out string errorCode)
        {
            errorCode = string.Empty;

            // The response must parse and contain an account.
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

            // The SUNRISE account itself must be active.
            if (!account.IsActive)
            {
                errorCode = ErrorUserIsActive;
                return false;
            }

            // The ASLM profile must exist, be active and not be banned.
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
        /// Returns false, without changing the stored account, when the response is malformed or has no account.
        /// </summary>
        public async Task<bool> UpdateUserDataAsync(string json, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await InitializeAsync(ct);

            // Parse and normalize the account before touching the cached document.
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

            // Replace the cached account and write the account data file.
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

        /// <summary>
        /// Accepts connections on the loopback listener until one carries a valid authentication callback.
        /// A valid callback is a loopback POST with the exact branded Host header and the expected state;
        /// it is answered with a redirect to the success page. Rejected connections receive a 400 response, and
        /// after <see cref="CallbackConnectionLimit"/> connections the last rejection is thrown.
        /// </summary>
        /// <param name="listener">Listener bound to the loopback address.</param>
        /// <param name="expectedState">State created for this authentication attempt.</param>
        /// <param name="successRedirectUri">SUNRISE page the browser is sent to after an accepted callback.</param>
        /// <param name="ct">Cancellation token that also carries the authentication timeout.</param>
        /// <exception cref="InvalidDataException">No valid callback was received.</exception>
        private static async Task<SunriseCallbackPayload> ReceiveAuthenticationCallbackAsync(
            TcpListener listener,
            string expectedState,
            Uri successRedirectUri,
            CancellationToken ct)
        {
            InvalidDataException? lastError = null;
            for (var attempt = 0; attempt < CallbackConnectionLimit; attempt++)
            {
                // Peers that do not connect from a loopback address are dropped without a response.
                using var client = await listener.AcceptTcpClientAsync(ct);
                if (client.Client.RemoteEndPoint is not IPEndPoint remoteEndpoint ||
                    !IPAddress.IsLoopback(remoteEndpoint.Address))
                {
                    lastError = new InvalidDataException("The callback did not originate from loopback.");
                    continue;
                }

                try
                {
                    // Read the request and validate its method, path, content type and token fields. Only the exact
                    // branded Host is accepted, so the browser must have followed the intended redirect.
                    var request = await ReadCallbackRequestAsync(client.GetStream(), ct);
                    var callbackPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                    var payload = ParseCallbackRequest(
                        request.Method,
                        request.Target,
                        request.Headers,
                        request.Body,
                        $"{CallbackHostName}:{callbackPort}");

                    // The state proves the callback belongs to this authentication attempt.
                    if (!StateMatches(expectedState, payload.State))
                    {
                        throw new InvalidDataException("The callback state did not match the authorization attempt.");
                    }

                    await WriteCallbackRedirectAsync(client.GetStream(), successRedirectUri, ct);
                    return payload;
                }
                catch (InvalidDataException ex)
                {
                    // Remember the rejection, tell the peer, and wait for the next connection.
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

        /// <summary>
        /// Reads one HTTP/1.x request with a fixed-length body from the callback connection.
        /// The request line, headers and body are size-limited and strictly checked: ASCII headers only,
        /// no duplicated headers, no <c>Transfer-Encoding</c>, a positive <c>Content-Length</c> and no trailing data.
        /// </summary>
        /// <param name="stream">Stream of the accepted callback connection.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The request method, target, headers (case-insensitive) and body bytes.</returns>
        /// <exception cref="InvalidDataException">The request is incomplete, oversized or malformed.</exception>
        private static async Task<(
            string Method,
            string Target,
            Dictionary<string, string> Headers,
            byte[] Body)> ReadCallbackRequestAsync(
                NetworkStream stream,
                CancellationToken ct)
        {
            // Read until the blank line that ends the headers, within the header size limit.
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

            // Headers must be plain ASCII.
            var allBytes = received.GetBuffer();
            for (var index = 0; index < headerEnd; index++)
            {
                if (allBytes[index] > 0x7f)
                {
                    throw new InvalidDataException("The callback headers were not ASCII.");
                }
            }

            // Parse the request line: method, target and an HTTP/1.x version.
            var headerText = Encoding.ASCII.GetString(allBytes, 0, headerEnd);
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (requestParts.Length != 3 || !requestParts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The callback request line was invalid.");
            }

            // Parse the header lines; empty and duplicated header names are rejected.
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

            // The body length must be declared and within the body limit; chunked bodies are not supported.
            if (headers.ContainsKey("Transfer-Encoding") ||
                !headers.TryGetValue("Content-Length", out var lengthText) ||
                !int.TryParse(lengthText, out var contentLength) ||
                contentLength <= 0 || contentLength > CallbackBodyLimit)
            {
                throw new InvalidDataException("The callback content length was invalid.");
            }

            // Reuse body bytes that arrived together with the headers, then read the rest from the stream.
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

        /// <summary>
        /// Validates a received callback request and extracts its token payload.
        /// The request must be a POST to <c>/sunrise-auth/</c> with the exact expected Host, a form-encoded
        /// UTF-8 body and the <c>state</c>, <c>access</c> and <c>refresh</c> fields. The state format is checked
        /// here; whether it matches the attempt is decided by the caller.
        /// </summary>
        /// <param name="method">HTTP method of the request.</param>
        /// <param name="target">Request target (path) of the request.</param>
        /// <param name="headers">Request headers with case-insensitive names.</param>
        /// <param name="body">Raw request body.</param>
        /// <param name="expectedHost">Host header value, including the port, that the request must carry.</param>
        /// <returns>The state and the access and refresh tokens from the form.</returns>
        /// <exception cref="InvalidDataException">Any part of the request is not acceptable.</exception>
        internal static SunriseCallbackPayload ParseCallbackRequest(
            string method,
            string target,
            IReadOnlyDictionary<string, string> headers,
            byte[] body,
            string expectedHost)
        {
            // Only the fixed callback route accepts a POST.
            if (!string.Equals(method, "POST", StringComparison.Ordinal) ||
                !string.Equals(target, "/sunrise-auth/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The callback must be a POST to /sunrise-auth/.");
            }

            // The Host header must be exactly the branded name with the listener port.
            if (!headers.TryGetValue("Host", out var host) ||
                !string.Equals(host, expectedHost, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The callback Host header must be exactly '{expectedHost}'.");
            }

            // The body must be an HTML form post.
            if (!headers.TryGetValue("Content-Type", out var contentType) ||
                !string.Equals(
                    contentType.Split(';', 2)[0].Trim(),
                    "application/x-www-form-urlencoded",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The callback content type was invalid.");
            }

            // Decode strictly as UTF-8; invalid byte sequences are rejected instead of replaced.
            string formText;
            try
            {
                formText = new UTF8Encoding(false, true).GetString(body);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("The callback body was not valid UTF-8.", ex);
            }

            // All three fields are required and the state must have the expected shape.
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

        /// <summary>
        /// Parses an <c>application/x-www-form-urlencoded</c> body into a name/value map.
        /// Pairs without an equals sign or a name, and duplicated names, are rejected.
        /// </summary>
        /// <exception cref="InvalidDataException">The body is malformed or repeats a field.</exception>
        internal static Dictionary<string, string> ParseFormBody(string formBody)
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

        /// <summary>
        /// Decodes one form-encoded name or value: plus signs become spaces, then percent escapes are resolved.
        /// </summary>
        /// <exception cref="InvalidDataException">The value contains invalid escaping.</exception>
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

        /// <summary>
        /// Answers an accepted callback with a 303 redirect to the SUNRISE success page.
        /// The response is not cacheable and sends no referrer.
        /// </summary>
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

        /// <summary>
        /// Answers a rejected callback with a 400 response and a short plain-text message.
        /// The response is not cacheable and sends no referrer.
        /// </summary>
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

        /// <summary>
        /// Returns the index of the first <c>CRLF CRLF</c> sequence that ends the HTTP headers within the
        /// first <paramref name="length"/> bytes, or -1 when the headers are not complete yet.
        /// </summary>
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

        /// <summary>
        /// Creates the one-time anti-forgery state: 32 random bytes in unpadded URL-safe Base64.
        /// </summary>
        private static string CreateCallbackState()
        {
            var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return state.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Returns whether a state value has the expected shape: 22 to 128 URL-safe Base64 characters.
        /// </summary>
        internal static bool IsValidCallbackState(string? state)
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

        /// <summary>
        /// Compares the expected and received state in constant time, so timing does not reveal matching characters.
        /// </summary>
        private static bool StateMatches(string expected, string received)
        {
            var expectedBytes = Encoding.ASCII.GetBytes(expected);
            var receivedBytes = Encoding.ASCII.GetBytes(received);
            return expectedBytes.Length == receivedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(expectedBytes, receivedBytes);
        }

        /// <summary>
        /// Returns whether a redirect URI is exactly the fixed callback address:
        /// <c>http://{branded host}:{port}/sunrise-auth/</c> with a port of 1024 or higher and no query,
        /// fragment or user information.
        /// </summary>
        internal static bool IsValidLoopbackRedirectUri(Uri redirectUri) =>
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
        /// A missing, empty or unreadable file yields the defaults, which are written back only when requested.
        /// </summary>
        /// <param name="filePath">Path of the JSON file.</param>
        /// <param name="createDefault">Factory for the default document.</param>
        /// <param name="normalize">Repairs missing or invalid values of a loaded or default document.</param>
        /// <param name="persistDefaults">Whether the default document is saved to <paramref name="filePath"/>.</param>
        /// <param name="ct">Cancellation token.</param>
        private async Task<T> LoadDocumentAsync<T>(
            string filePath,
            Func<T> createDefault,
            Action<T> normalize,
            bool persistDefaults,
            CancellationToken ct)
            where T : class
        {
            // Try the persisted document first.
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

            // Fall back to the defaults when the file is absent, empty or damaged.
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
        /// Domains with an empty protocol, host or type are removed. Returns true when the catalog changed.
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
        /// Endpoints with an empty name, path or domain type are removed. Returns true when the catalog changed.
        /// </summary>
        private bool EnsureDefaultUrls()
        {
            _urlsData.Normalize();
            var changed = false;

            // Drop incomplete entries.
            var removed = _urlsData.Urls.RemoveAll(url =>
                string.IsNullOrWhiteSpace(url.Name) ||
                string.IsNullOrWhiteSpace(url.Url) ||
                string.IsNullOrWhiteSpace(url.DomainType));
            changed |= removed > 0;

            // Add shipped endpoints that the file does not list yet; existing ones keep their configured path.
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

            // Mark the catalog as migrated to the current schema version.
            if (_urlsData.FileVersion < CurrentFileVersion)
            {
                _urlsData.FileVersion = CurrentFileVersion;
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Creates the default domain catalog with only the default domain.
        /// </summary>
        private static SunriseDomainsData CreateDefaultDomainsData() => new()
        {
            FileVersion = CurrentFileVersion,
            Domains = [CreateDefaultDomain()]
        };

        /// <summary>
        /// Creates the default SUNRISE domain: the backend at <c>127.0.0.1:8000</c> over HTTP.
        /// </summary>
        private static SunriseDomain CreateDefaultDomain() => new()
        {
            Protocol = "http",
            Domain = "127.0.0.1:8000",
            DomainType = DefaultDomainType
        };

        /// <summary>
        /// Creates the default endpoint catalog with all shipped endpoints.
        /// </summary>
        private static SunriseUrlsData CreateDefaultUrlsData() => new()
        {
            FileVersion = CurrentFileVersion,
            Urls = CreateDefaultUrls()
        };

        /// <summary>
        /// Creates the shipped endpoints with their paths relative to the domain root.
        /// </summary>
        private static List<SunriseUrl> CreateDefaultUrls() =>
        [
            CreateUrl(SignupEndpoint, "api/id/signup/"),
            CreateUrl(AuthenticationEndpoint, "api/id/token/"),
            CreateUrl(RefreshEndpoint, "api/id/token/refresh/"),
            CreateUrl(VerifyEndpoint, "api/id/token/verify/"),
            CreateUrl(ProfileEndpoint, "id/"),
            CreateUrl(PasswordRecoveryEndpoint, "id/"),
            CreateUrl(ApplicationAuthenticationEndpoint, "id/authapp/"),
            CreateUrl(ApplicationAuthenticationSuccessEndpoint, "id/authapp/success/"),
            CreateUrl(AslmGetUserDataEndpoint, "api/aslm/getuserdata/"),
            CreateUrl(AslmCreateProfileEndpoint, "api/aslm/createprofile/")
        ];

        /// <summary>
        /// Creates one endpoint entry that belongs to the default domain type.
        /// </summary>
        private static SunriseUrl CreateUrl(string name, string url) => new()
        {
            Name = name,
            Url = url,
            DomainType = DefaultDomainType
        };

        /// <summary>
        /// Creates an empty token document without credentials.
        /// </summary>
        private static SunriseTokensData CreateDefaultTokensData() => new()
        {
            FileVersion = CurrentFileVersion,
            Jwt = new SunriseJwtTokens()
        };

        /// <summary>
        /// Creates an empty account document with a blank account, an empty ASLM profile and one blank saved account.
        /// </summary>
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

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> when the service was already disposed.
        /// </summary>
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

            // Dispose the HTTP client only when it was actually created.
            _disposed = true;
            if (_httpClient.IsValueCreated)
            {
                _httpClient.Value.Dispose();
            }

            _dataGate.Dispose();
            _accountOperationGate.Dispose();
        }
    }
}
