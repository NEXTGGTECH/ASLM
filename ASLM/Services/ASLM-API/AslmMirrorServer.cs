// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.API
{
    /// <summary>
    /// Hosts a local path-mounted reverse proxy for module endpoints declared in <c>Data/App/ASLM_Ports.json</c>.
    /// </summary>
    public class AslmMirrorServer : IDisposable
    {
        private const int BackendRedirectLimit = 10;
        private const string RouteHeaderName = "X-ASLM-Mirror-Route";
        private static readonly HttpClient ProxyClient = CreateProxyClient();

        private readonly AppDataStore _appData;
        private readonly PortRegistry _ports;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ILogger<AslmMirrorServer> _logger;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly object _stateLock = new();
        private readonly string _portMapPath;
        private HttpListener? _listener;
        private CancellationTokenSource? _listenerCts;
        private Task? _listenerTask;
        private int? _activePort;
        private bool _disposed;
        private string _lastError = string.Empty;

        /// <summary>
        /// Raised when the server state changes and bound views should refresh.
        /// </summary>
        public event EventHandler? StateChanged;

        // Initialization

        /// <summary>
        /// Creates the ASLM mirror server (path-mounted reverse proxy) and resolves its runtime data path.
        /// </summary>
        public AslmMirrorServer(
            AppDataStore appData,
            PortRegistry ports,
            ModuleInstaller moduleInstaller,
            ILogger<AslmMirrorServer> logger)
        {
            _appData = appData;
            _ports = ports;
            _moduleInstaller = moduleInstaller;
            _logger = logger;

            var rootDir = GetRootDirectory();
            _portMapPath = Path.Combine(rootDir, "Data", "App", "ASLM_Ports.json");
        }


        // State access

        /// <summary>
        /// Gets whether the reverse proxy is currently listening.
        /// </summary>
        public bool IsRunning
        {
            get
            {
                lock (_stateLock)
                {
                    return _listener?.IsListening == true;
                }
            }
        }

        /// <summary>
        /// Gets the persisted enabled state for the reverse proxy.
        /// </summary>
        public bool IsEnabled => _appData.Data.Api.ServerEnabled;

        /// <summary>
        /// Gets the localhost port reserved for the ASLM mirror server.
        /// </summary>
        public int Port
        {
            get
            {
                lock (_stateLock)
                {
                    return _activePort ??
                           _ports.TryGetInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey) ??
                           _appData.Data.Ports.ModulesStart;
                }
            }
        }

        /// <summary>
        /// Gets the browser-friendly root URL for the reverse proxy.
        /// </summary>
        public string BaseUrl => BuildBaseUrl(Port);

        /// <summary>
        /// Gets the most recent server startup error, if any.
        /// </summary>
        public string LastError
        {
            get
            {
                lock (_stateLock)
                {
                    return _lastError;
                }
            }
        }


        // Server lifecycle

        /// <summary>
        /// Starts the reverse proxy when persisted settings say it should be enabled.
        /// </summary>
        public async Task StartIfEnabledAsync()
        {
            if (!IsEnabled)
            {
                _ports.RedistributePorts(reserveAslmMirrorServer: false);
                return;
            }

            _ports.RedistributePorts(reserveAslmMirrorServer: true);
            await StartAsync();
        }

        /// <summary>
        /// Persists the enabled state and starts or stops the proxy to match it.
        /// </summary>
        public async Task SetEnabledAsync(bool enabled)
        {
            if (_appData.Data.Api.ServerEnabled == enabled)
            {
                return;
            }

            _appData.Data.Api.ServerEnabled = enabled;
            _appData.Save();

            if (enabled)
            {
                _ports.RedistributePorts(reserveAslmMirrorServer: true);
                await StartAsync();
            }
            else
            {
                await StopAsync();
                _ports.RedistributePorts(reserveAslmMirrorServer: false);
            }
        }

        /// <summary>
        /// Starts the local listener and background request loop.
        /// </summary>
        public Task StartAsync()
        {
            lock (_stateLock)
            {
                if (_listener?.IsListening == true)
                {
                    return Task.CompletedTask;
                }

                try
                {
                    var assignedPort = GetAssignedPort();
                    _listenerCts = new CancellationTokenSource();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(BuildBaseUrl(assignedPort));
                    _listener.Start();
                    _activePort = assignedPort;
                    _lastError = string.Empty;
                    _listenerTask = Task.Run(() => RunListenerLoopAsync(_listener, _listenerCts.Token));
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogError(ex, "Failed to start ASLM API reverse proxy.");
                    CleanupListener();
                }
            }

            RaiseStateChanged();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stops the local listener and waits briefly for the request loop to exit.
        /// </summary>
        public async Task StopAsync()
        {
            Task? listenerTask;

            lock (_stateLock)
            {
                _listenerCts?.Cancel();

                try
                {
                    _listener?.Stop();
                    _listener?.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ASLM API reverse proxy listener stop failed.");
                }

                listenerTask = _listenerTask;
                CleanupListener();
            }

            if (listenerTask != null)
            {
                try
                {
                    await listenerTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // Listener shutdown is best-effort; active requests are allowed to finish independently.
                }
            }

            RaiseStateChanged();
        }


        // Host listing

        /// <summary>
        /// Returns the currently declared module hosts from the dynamic ASLM port map.
        /// </summary>
        public IReadOnlyList<AslmMirrorHostInfo> GetHosts()
        {
            return LoadPortMap()
                .SelectMany(module => module.Value.Select(host => CreateHostInfo(module.Key, host.Key, host.Value, null)))
                .OrderBy(static host => host.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static host => host.Port)
                .ThenBy(static host => host.HostKey, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Returns declared module hosts and marks whether each target TCP port is accepting connections.
        /// </summary>
        public async Task<IReadOnlyList<AslmMirrorHostInfo>> GetHostsWithAvailabilityAsync(CancellationToken ct = default)
        {
            var hosts = GetHosts();
            var tasks = hosts.Select(async host =>
            {
                var isOnline = await IsTcpPortOpenAsync(host.Port, ct);
                return host with { IsOnline = isOnline };
            });

            return await Task.WhenAll(tasks);
        }


        // Request loop

        /// <summary>
        /// Accepts incoming requests until the listener is stopped or cancelled.
        /// </summary>
        private async Task RunListenerLoopAsync(HttpListener listener, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    break;
                }

                _ = Task.Run(() => HandleContextAsync(context, ct), ct);
            }
        }

        /// <summary>
        /// Routes one incoming request to an index page, module host page, or proxied backend request.
        /// </summary>
        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                var portMap = LoadPortMap();
                var route = ResolveRoute(context.Request, portMap);

                switch (route.Kind)
                {
                    case ProxyRouteKind.RootIndex:
                        await WriteRootPageAsync(context, portMap, statusCode: 200, message: null, ct);
                        return;
                    case ProxyRouteKind.ModuleIndex:
                        await WriteModulePageAsync(context, route.ModuleId, route.Hosts, statusCode: 200, message: null, ct);
                        return;
                    case ProxyRouteKind.ModuleNotFound:
                        await WriteRootPageAsync(context, portMap, statusCode: 404, message: $"Module '{route.ModuleId}' was not found.", ct);
                        return;
                    case ProxyRouteKind.HostNotFound:
                        await WriteModulePageAsync(context, route.ModuleId, route.Hosts, statusCode: 404, message: $"Host '{route.HostKey}' was not found for '{route.ModuleId}'.", ct);
                        return;
                    case ProxyRouteKind.RedirectToSlash:
                        RedirectToRouteRoot(context, route.ModuleId, route.RouteHostKey);
                        return;
                    case ProxyRouteKind.Proxy:
                        await ProxyRequestAsync(context, route, ct);
                        return;
                    default:
                        await WriteRootPageAsync(context, portMap, statusCode: 404, message: "Route was not found.", ct);
                        return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ASLM API reverse proxy request failed.");
                await WriteErrorPageAsync(context, ex, ct);
            }
        }


        // Route resolution

        /// <summary>
        /// Resolves the incoming URL into a root page, listing page, or backend proxy route.
        /// </summary>
        private static ProxyRoute ResolveRoute(HttpListenerRequest request, IReadOnlyDictionary<string, Dictionary<string, int>> portMap)
        {
            var pathSegments = GetPathSegments(request.Url?.AbsolutePath ?? "/");

            if (pathSegments.Count == 0)
            {
                return ProxyRoute.RootIndex();
            }

            var moduleId = pathSegments[0];
            if (!portMap.TryGetValue(moduleId, out var hosts))
            {
                return TryResolveRefererProxyRoute(request, portMap, pathSegments, out var refererRoute)
                    ? refererRoute
                    : ProxyRoute.ModuleNotFound(moduleId);
            }

            if (pathSegments.Count == 1)
            {
                return ProxyRoute.ModuleIndex(moduleId, hosts);
            }

            var routeHostKey = pathSegments[1];
            if (!TryResolveHostRoute(hosts, routeHostKey, out var hostKey, out var port))
            {
                return ProxyRoute.HostNotFound(moduleId, routeHostKey, hosts);
            }

            if (pathSegments.Count == 2 && request.Url?.AbsolutePath.EndsWith('/') == false)
            {
                return ProxyRoute.RedirectToSlash(moduleId, hostKey, BuildHostRouteKey(hostKey));
            }

            var preserveTrailingSlash = request.Url?.AbsolutePath.EndsWith("/", StringComparison.Ordinal) == true;
            var targetPath = BuildTargetPath(pathSegments.Skip(2), preserveTrailingSlash);
            return ProxyRoute.Proxy(moduleId, hostKey, BuildHostRouteKey(hostKey), port, targetPath, hosts);
        }

        /// <summary>
        /// Resolves root-relative asset or API requests by using the mirror route from the referring page.
        /// </summary>
        private static bool TryResolveRefererProxyRoute(
            HttpListenerRequest request,
            IReadOnlyDictionary<string, Dictionary<string, int>> portMap,
            IReadOnlyList<string> requestSegments,
            out ProxyRoute route)
        {
            route = ProxyRoute.RootIndex();

            var referer = request.UrlReferrer;
            if (referer == null)
            {
                return false;
            }

            var refererSegments = GetPathSegments(referer.AbsolutePath);
            if (refererSegments.Count < 2)
            {
                return false;
            }

            var moduleId = refererSegments[0];
            var routeHostKey = refererSegments[1];
            if (!portMap.TryGetValue(moduleId, out var hosts) ||
                !TryResolveHostRoute(hosts, routeHostKey, out var hostKey, out var port))
            {
                return false;
            }

            var preserveTrailingSlash = request.Url?.AbsolutePath.EndsWith("/", StringComparison.Ordinal) == true;
            route = ProxyRoute.Proxy(moduleId, hostKey, BuildHostRouteKey(hostKey), port, BuildTargetPath(requestSegments, preserveTrailingSlash), hosts);
            return true;
        }

        /// <summary>
        /// Resolves a public host route segment back to the underlying port-map host key.
        /// </summary>
        private static bool TryResolveHostRoute(
            IReadOnlyDictionary<string, int> hosts,
            string routeHostKey,
            out string hostKey,
            out int port)
        {
            if (hosts.TryGetValue(routeHostKey, out port))
            {
                hostKey = routeHostKey;
                return true;
            }

            foreach (var host in hosts
                         .OrderBy(static pair => pair.Value)
                         .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(BuildHostRouteKey(host.Key), routeHostKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                hostKey = host.Key;
                port = host.Value;
                return true;
            }

            hostKey = routeHostKey;
            port = 0;
            return false;
        }

        /// <summary>
        /// Builds a backend path from decoded path segments.
        /// </summary>
        private static string BuildTargetPath(IEnumerable<string> segments, bool preserveTrailingSlash)
        {
            var list = segments.ToList();
            if (list.Count == 0)
            {
                return "/";
            }

            // Django and similar frameworks distinguish POST /route from
            // POST /route/. Keep the browser slash to avoid unsafe redirects.
            var path = "/" + string.Join('/', list.Select(Uri.EscapeDataString));
            return preserveTrailingSlash
                ? path + "/"
                : path;
        }

        /// <summary>
        /// Builds a backend path from decoded path segments without forcing a trailing slash.
        /// </summary>
        private static string BuildTargetPath(IEnumerable<string> segments)
        {
            var list = segments.ToList();
            return list.Count == 0
                ? "/"
                : "/" + string.Join('/', list.Select(Uri.EscapeDataString));
        }

        /// <summary>
        /// Parses decoded path segments from an absolute URL path.
        /// </summary>
        private static List<string> GetPathSegments(string path)
        {
            return (path ?? string.Empty)
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToList();
        }


        // Proxying

        /// <summary>
        /// Proxies one resolved route to the selected localhost backend.
        /// </summary>
        private static async Task ProxyRequestAsync(HttpListenerContext context, ProxyRoute route, CancellationToken ct)
        {
            if (route.Port < 1 || route.Port > 65535)
            {
                await WritePlainTextAsync(context, 502, "Invalid target port.", ct);
                return;
            }

            if (context.Request.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(context, route, ct);
                return;
            }

            using var responseMessage = await SendHttpRequestWithInternalRedirectsAsync(context.Request, route, ct);
            context.Response.StatusCode = (int)responseMessage.StatusCode;
            CopyResponseHeaders(responseMessage, context.Response, route);

            if (ShouldRewriteContent(responseMessage.Content.Headers.ContentType?.MediaType))
            {
                var sourceText = await responseMessage.Content.ReadAsStringAsync(ct);
                var rewrittenText = RewriteTextContent(
                    sourceText,
                    responseMessage.Content.Headers.ContentType?.MediaType,
                    route);
                var encoding = ResolveContentEncoding(responseMessage.Content.Headers.ContentType);
                var responseBytes = encoding.GetBytes(rewrittenText);
                context.Response.ContentLength64 = responseBytes.Length;
                await context.Response.OutputStream.WriteAsync(responseBytes, ct);
            }
            else
            {
                await using var responseStream = await responseMessage.Content.ReadAsStreamAsync(ct);
                await responseStream.CopyToAsync(context.Response.OutputStream, ct);
            }

            context.Response.Close();
        }

        /// <summary>
        /// Sends the backend HTTP request and follows local backend redirects before responding to the browser.
        /// </summary>
        private static async Task<HttpResponseMessage> SendHttpRequestWithInternalRedirectsAsync(
            HttpListenerRequest sourceRequest,
            ProxyRoute route,
            CancellationToken ct)
        {
            var currentRoute = route;
            var currentQuery = sourceRequest.Url?.Query.TrimStart('?') ?? string.Empty;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var redirectCount = 0; redirectCount <= BackendRedirectLimit; redirectCount++)
            {
                using var requestMessage = CreateHttpRequest(sourceRequest, currentRoute, currentQuery, includeBody: redirectCount == 0);
                var responseMessage = await ProxyClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!TryResolveInternalRedirect(sourceRequest, responseMessage, currentRoute, out var nextRoute, out var nextQuery))
                {
                    return responseMessage;
                }

                var visitKey = $"{nextRoute.TargetPath}?{nextQuery}";
                if (!visited.Add(visitKey))
                {
                    return responseMessage;
                }

                responseMessage.Dispose();
                currentRoute = nextRoute;
                currentQuery = nextQuery;
            }

            return await ProxyClient.SendAsync(
                CreateHttpRequest(sourceRequest, currentRoute, currentQuery, includeBody: false),
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }

        /// <summary>
        /// Builds one backend HTTP request for the resolved proxy route.
        /// </summary>
        private static HttpRequestMessage CreateHttpRequest(
            HttpListenerRequest sourceRequest,
            ProxyRoute route,
            string query,
            bool includeBody)
        {
            var builder = new UriBuilder("http", "127.0.0.1", route.Port, route.TargetPath)
            {
                Query = query
            };

            var requestMessage = new HttpRequestMessage(new HttpMethod(sourceRequest.HttpMethod), builder.Uri);

            if (includeBody && sourceRequest.HasEntityBody)
            {
                requestMessage.Content = new StreamContent(sourceRequest.InputStream);
                if (!string.IsNullOrWhiteSpace(sourceRequest.ContentType))
                {
                    requestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(sourceRequest.ContentType, out var contentType)
                        ? contentType
                        : null;
                }

                if (sourceRequest.ContentLength64 >= 0)
                {
                    // Some Python development servers are picky about chunked request
                    // bodies. Preserve the browser's fixed length whenever it is known.
                    requestMessage.Content.Headers.ContentLength = sourceRequest.ContentLength64;
                }
            }

            CopyRequestHeaders(sourceRequest.Headers, requestMessage);
            AddForwardedHeaders(sourceRequest, requestMessage, route);
            return requestMessage;
        }

        /// <summary>
        /// Copies browser request headers that are safe to forward to the backend.
        /// </summary>
        private static void CopyRequestHeaders(NameValueCollection sourceHeaders, HttpRequestMessage targetRequest)
        {
            foreach (var headerName in sourceHeaders.AllKeys.OfType<string>())
            {
                if (ShouldSkipRequestHeader(headerName))
                {
                    continue;
                }

                var values = sourceHeaders.GetValues(headerName);
                if (values == null)
                {
                    continue;
                }

                if (!targetRequest.Headers.TryAddWithoutValidation(headerName, values))
                {
                    targetRequest.Content?.Headers.TryAddWithoutValidation(headerName, values);
                }
            }

            targetRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        }

        /// <summary>
        /// Adds reverse-proxy context headers for frameworks that support mounted path prefixes.
        /// </summary>
        private static void AddForwardedHeaders(HttpListenerRequest sourceRequest, HttpRequestMessage targetRequest, ProxyRoute route)
        {
            var backendOrigin = $"http://127.0.0.1:{route.Port}";
            var backendHost = $"127.0.0.1:{route.Port}";

            // The backend receives a direct localhost request from the proxy, so keep
            // Host, Origin, and Referer aligned with the backend port for CSRF checks.
            targetRequest.Headers.Host = backendHost;
            targetRequest.Headers.Remove("Origin");
            targetRequest.Headers.Remove("Referer");

            if (!string.IsNullOrWhiteSpace(sourceRequest.Headers["Origin"]))
            {
                targetRequest.Headers.TryAddWithoutValidation("Origin", backendOrigin);
            }

            if (sourceRequest.UrlReferrer != null)
            {
                var backendReferer = RewriteMirrorUriToBackendUri(sourceRequest.UrlReferrer, route, backendOrigin);
                targetRequest.Headers.TryAddWithoutValidation("Referer", backendReferer);
            }

            targetRequest.Headers.Remove(RouteHeaderName);
            targetRequest.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", route.RoutePrefix);
            targetRequest.Headers.TryAddWithoutValidation("X-Script-Name", route.RoutePrefix);
            targetRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", sourceRequest.RemoteEndPoint?.Address.ToString());
            targetRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", sourceRequest.UserHostName);
            targetRequest.Headers.TryAddWithoutValidation("X-Forwarded-Port", sourceRequest.LocalEndPoint?.Port.ToString());
            targetRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", sourceRequest.Url?.Scheme ?? "http");
            targetRequest.Headers.TryAddWithoutValidation(RouteHeaderName, route.RoutePrefix);
        }

        /// <summary>
        /// Converts a mirror URL seen by the browser into the equivalent backend URL for server-side checks.
        /// </summary>
        private static string RewriteMirrorUriToBackendUri(Uri mirrorUri, ProxyRoute route, string backendOrigin)
        {
            var backendPath = IsMirrorPath(mirrorUri.AbsolutePath, route.RoutePrefix)
                ? StripMirrorPrefix(mirrorUri.AbsolutePath, route.RoutePrefix)
                : mirrorUri.AbsolutePath;

            return backendOrigin + NormalizeBackendPath(backendPath) + mirrorUri.Query + mirrorUri.Fragment;
        }

        /// <summary>
        /// Returns whether a backend redirect points to the same backend and can be followed internally.
        /// </summary>
        private static bool TryResolveInternalRedirect(
            HttpListenerRequest sourceRequest,
            HttpResponseMessage responseMessage,
            ProxyRoute currentRoute,
            out ProxyRoute nextRoute,
            out string nextQuery)
        {
            nextRoute = currentRoute;
            nextQuery = string.Empty;

            if (!IsRedirectStatusCode(responseMessage.StatusCode) ||
                !IsBodylessRedirectMethod(sourceRequest.HttpMethod) ||
                responseMessage.Headers.Location == null)
            {
                return false;
            }

            var location = responseMessage.Headers.Location;
            Uri targetUri;

            if (location.IsAbsoluteUri)
            {
                if (!location.IsLoopback || location.Port != currentRoute.Port)
                {
                    return false;
                }

                targetUri = location;
            }
            else
            {
                var locationText = location.ToString();
                if (IsMirrorPath(locationText, currentRoute.RoutePrefix))
                {
                    locationText = StripMirrorPrefix(locationText, currentRoute.RoutePrefix);
                }

                targetUri = new Uri(new Uri($"http://127.0.0.1:{currentRoute.Port}/"), locationText);
            }

            var backendPath = IsMirrorPath(targetUri.AbsolutePath, currentRoute.RoutePrefix)
                ? StripMirrorPrefix(targetUri.AbsolutePath, currentRoute.RoutePrefix)
                : targetUri.AbsolutePath;
            nextRoute = currentRoute with { TargetPath = NormalizeBackendPath(backendPath) };
            nextQuery = targetUri.Query.TrimStart('?');
            return true;
        }

        /// <summary>
        /// Returns whether an HTTP status code represents a redirect.
        /// </summary>
        private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code is >= 300 and <= 399;
        }

        /// <summary>
        /// Returns whether an HTTP method can be safely replayed without forwarding a request body twice.
        /// </summary>
        private static bool IsBodylessRedirectMethod(string method)
        {
            return string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        }


        // WebSockets

        /// <summary>
        /// Proxies one WebSocket upgrade to the selected localhost backend.
        /// </summary>
        private static async Task ProxyWebSocketAsync(HttpListenerContext context, ProxyRoute route, CancellationToken ct)
        {
            using var backendSocket = new ClientWebSocket();
            var subProtocol = ResolveWebSocketSubProtocol(context.Request);
            if (!string.IsNullOrWhiteSpace(subProtocol))
            {
                backendSocket.Options.AddSubProtocol(subProtocol);
            }

            try
            {
                await backendSocket.ConnectAsync(CreateWebSocketTargetUri(context.Request, route), ct);
            }
            catch
            {
                await WritePlainTextAsync(context, 502, "WebSocket target is unavailable.", ct);
                return;
            }

            var clientContext = await context.AcceptWebSocketAsync(subProtocol);
            using var clientSocket = clientContext.WebSocket;

            var clientToBackend = PumpWebSocketAsync(clientSocket, backendSocket, ct);
            var backendToClient = PumpWebSocketAsync(backendSocket, clientSocket, ct);
            await Task.WhenAny(clientToBackend, backendToClient);
        }

        /// <summary>
        /// Builds a backend WebSocket URI for the resolved proxy route.
        /// </summary>
        private static Uri CreateWebSocketTargetUri(HttpListenerRequest request, ProxyRoute route)
        {
            var builder = new UriBuilder("ws", "127.0.0.1", route.Port, route.TargetPath)
            {
                Query = request.Url?.Query.TrimStart('?') ?? string.Empty
            };

            return builder.Uri;
        }

        /// <summary>
        /// Relays WebSocket frames until either side closes or errors.
        /// </summary>
        private static async Task PumpWebSocketAsync(WebSocket source, WebSocket target, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];

            while (!ct.IsCancellationRequested &&
                   source.State == WebSocketState.Open &&
                   target.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;

                try
                {
                    result = await source.ReceiveAsync(buffer, ct);
                }
                catch
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseWebSocketSafeAsync(target, result.CloseStatus, result.CloseStatusDescription, ct);
                    break;
                }

                await target.SendAsync(
                    buffer.AsMemory(0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    ct);
            }
        }

        /// <summary>
        /// Closes a WebSocket while tolerating peers that have already disconnected.
        /// </summary>
        private static async Task CloseWebSocketSafeAsync(
            WebSocket socket,
            WebSocketCloseStatus? closeStatus,
            string? closeDescription,
            CancellationToken ct)
        {
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        closeStatus ?? WebSocketCloseStatus.NormalClosure,
                        closeDescription,
                        ct);
                }
            }
            catch
            {
                // The peer may already have closed the connection.
            }
        }

        /// <summary>
        /// Chooses the first requested WebSocket subprotocol when the browser supplied any.
        /// </summary>
        private static string? ResolveWebSocketSubProtocol(HttpListenerRequest request)
        {
            return request.Headers["Sec-WebSocket-Protocol"]?
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(static protocol => protocol.Trim())
                .FirstOrDefault(static protocol => protocol.Length > 0);
        }


        // Response headers

        /// <summary>
        /// Copies backend response headers to the browser response with reverse-proxy rewrites applied.
        /// </summary>
        private static void CopyResponseHeaders(HttpResponseMessage sourceResponse, HttpListenerResponse targetResponse, ProxyRoute route)
        {
            foreach (var header in sourceResponse.Headers)
            {
                if (ShouldSkipResponseHeader(header.Key))
                {
                    continue;
                }

                if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
                {
                    TrySetResponseHeader(targetResponse, header.Key, RewriteLocationHeader(string.Join(",", header.Value), route));
                    continue;
                }

                TrySetResponseHeader(targetResponse, header.Key, string.Join(",", header.Value));
            }

            foreach (var header in sourceResponse.Content.Headers)
            {
                if (ShouldSkipResponseHeader(header.Key))
                {
                    continue;
                }

                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    targetResponse.ContentType = string.Join(",", header.Value);
                    continue;
                }

                TrySetResponseHeader(targetResponse, header.Key, string.Join(",", header.Value));
            }

            foreach (var cookie in ExtractSetCookieHeaders(sourceResponse))
            {
                TrySetCookieHeader(targetResponse, RewriteSetCookie(cookie, route));
            }
        }

        /// <summary>
        /// Adds one Set-Cookie header while tolerating platform restrictions in HttpListener.
        /// </summary>
        private static void TrySetCookieHeader(HttpListenerResponse response, string cookie)
        {
            try
            {
                response.Headers.Add("Set-Cookie", cookie);
            }
            catch
            {
                // Some HttpListener implementations reject Set-Cookie through Headers.
            }
        }

        /// <summary>
        /// Extracts all Set-Cookie values from response headers without merging them.
        /// </summary>
        private static IEnumerable<string> ExtractSetCookieHeaders(HttpResponseMessage response)
        {
            return response.Headers.TryGetValues("Set-Cookie", out var headerValues)
                ? headerValues
                : [];
        }

        /// <summary>
        /// Rewrites backend cookies so their path is scoped to the mounted mirror route.
        /// </summary>
        private static string RewriteSetCookie(string cookie, ProxyRoute route)
        {
            var parts = cookie.Split(';', StringSplitOptions.TrimEntries);
            var rebuilt = new List<string>();
            var hasPath = false;

            foreach (var part in parts)
            {
                if (part.StartsWith("Domain=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (part.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                {
                    rebuilt.Add($"Path={route.RouteRoot}");
                    hasPath = true;
                    continue;
                }

                rebuilt.Add(part);
            }

            if (!hasPath)
            {
                rebuilt.Add($"Path={route.RouteRoot}");
            }

            return string.Join("; ", rebuilt);
        }

        /// <summary>
        /// Rewrites one Location header so browser-visible redirects remain inside the mirror route.
        /// </summary>
        private static string RewriteLocationHeader(string location, ProxyRoute route)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                return route.RouteRoot;
            }

            if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri) &&
                absoluteUri.IsLoopback &&
                absoluteUri.Port == route.Port)
            {
                return route.ToMirrorPath(NormalizeBackendPath(absoluteUri.AbsolutePath), absoluteUri.Query, absoluteUri.Fragment);
            }

            if (IsMirrorPath(location, route.RoutePrefix))
            {
                return location;
            }

            if (location.StartsWith("/", StringComparison.Ordinal) && !location.StartsWith("//", StringComparison.Ordinal))
            {
                return route.ToMirrorPath(NormalizeBackendPath(location), query: string.Empty, fragment: string.Empty);
            }

            return location;
        }

        /// <summary>
        /// Adds a response header when HttpListener allows application code to set it.
        /// </summary>
        private static void TrySetResponseHeader(HttpListenerResponse response, string headerName, string headerValue)
        {
            try
            {
                response.Headers[headerName] = headerValue;
            }
            catch
            {
                // Restricted transport headers are intentionally ignored by the proxy.
            }
        }

        /// <summary>
        /// Returns whether a request header is controlled by the proxy transport and should not be copied.
        /// </summary>
        private static bool ShouldSkipRequestHeader(string headerName)
        {
            return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Origin", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Referer", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether a response header is controlled by HttpListener or invalid after rewriting content.
        /// </summary>
        private static bool ShouldSkipResponseHeader(string headerName)
        {
            return headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Security-Policy", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Content-Security-Policy-Report-Only", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Referrer-Policy", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Server", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);
        }


        // Response rewriting

        /// <summary>
        /// Returns whether a backend response body can be rewritten as text.
        /// </summary>
        private static bool ShouldRewriteContent(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType))
            {
                return false;
            }

            return mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Rewrites HTML backend content so document links stay inside the mirror route.
        /// </summary>
        private static string RewriteTextContent(string content, string? mediaType, ProxyRoute route)
        {
            var rewritten = RewriteLoopbackBackendUrls(content, route);
            rewritten = RewriteRootRelativeUrls(rewritten, route);

            if (mediaType != null && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                rewritten = InjectHtmlMirrorSupport(rewritten, route);
            }

            return rewritten;
        }

        /// <summary>
        /// Rewrites absolute localhost URLs that point at the backend target into mirror-root URLs.
        /// </summary>
        private static string RewriteLoopbackBackendUrls(string content, ProxyRoute route)
        {
            return Regex.Replace(
                content,
                $@"(?<scheme>https?://)(?<host>127\.0\.0\.1|localhost):{route.Port}(?<path>/[^""'\s<)]*)?",
                match =>
                {
                    var path = match.Groups["path"].Success ? match.Groups["path"].Value : "/";
                    return route.ToMirrorPath(NormalizeBackendPath(path), query: string.Empty, fragment: string.Empty);
                },
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Rewrites common root-relative URLs in HTML, CSS, JavaScript, and JSON payloads.
        /// </summary>
        private static string RewriteRootRelativeUrls(string content, ProxyRoute route)
        {
            var rewritten = Regex.Replace(
                content,
                @"(?<prefix>\b(?:href|src|action|poster|data|formaction|manifest)\s*=\s*)(?<quote>[""'])(?<url>/[^""']*)(\k<quote>)",
                match => RewriteAttributeUrl(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\burl\(\s*)(?<quote>[""']?)(?<url>/[^""')]+)(?<suffix>\k<quote>\s*\))",
                match => RewriteCssUrl(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\bsrcset\s*=\s*)(?<quote>[""'])(?<value>[^""']*)(\k<quote>)",
                match => RewriteSrcSet(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\bcontent\s*=\s*)(?<quote>[""'])(?<value>[^""']*;\s*url=)(?<url>/[^""']*)(\k<quote>)",
                match => $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{match.Groups["value"].Value}{RewriteOneRootRelativeUrl(match.Groups["url"].Value, route)}{match.Groups["quote"].Value}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>[""'])(?<url>/[^""'\r\n]*)(?<suffix>[""'])",
                match => $"{match.Groups["prefix"].Value}{RewriteOneRootRelativeUrl(match.Groups["url"].Value, route)}{match.Groups["suffix"].Value}",
                RegexOptions.CultureInvariant);

            return rewritten;
        }

        /// <summary>
        /// Rewrites one HTML attribute URL match.
        /// </summary>
        private static string RewriteAttributeUrl(Match match, ProxyRoute route)
        {
            return $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{RewriteOneRootRelativeUrl(match.Groups["url"].Value, route)}{match.Groups["quote"].Value}";
        }

        /// <summary>
        /// Rewrites one CSS url(...) match.
        /// </summary>
        private static string RewriteCssUrl(Match match, ProxyRoute route)
        {
            return $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{RewriteOneRootRelativeUrl(match.Groups["url"].Value, route)}{match.Groups["suffix"].Value}";
        }

        /// <summary>
        /// Rewrites one srcset attribute while preserving image descriptors.
        /// </summary>
        private static string RewriteSrcSet(Match match, ProxyRoute route)
        {
            var rewrittenValue = string.Join(", ", match.Groups["value"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(candidate =>
                {
                    var trimmed = candidate.Trim();
                    var spaceIndex = trimmed.IndexOf(' ');
                    var url = spaceIndex >= 0 ? trimmed[..spaceIndex] : trimmed;
                    var descriptor = spaceIndex >= 0 ? trimmed[spaceIndex..] : string.Empty;
                    return RewriteOneRootRelativeUrl(url, route) + descriptor;
                }));

            return $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{rewrittenValue}{match.Groups["quote"].Value}";
        }

        /// <summary>
        /// Rewrites one root-relative backend URL to the mounted mirror route when needed.
        /// </summary>
        private static string RewriteOneRootRelativeUrl(string url, ProxyRoute route)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith("/", StringComparison.Ordinal) ||
                url.StartsWith("//", StringComparison.Ordinal) ||
                IsMirrorPath(url, route.RoutePrefix))
            {
                return url;
            }

            var fragmentIndex = url.IndexOf('#');
            var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : string.Empty;
            var beforeFragment = fragmentIndex >= 0 ? url[..fragmentIndex] : url;

            var queryIndex = beforeFragment.IndexOf('?');
            var query = queryIndex >= 0 ? beforeFragment[queryIndex..] : string.Empty;
            var path = queryIndex >= 0 ? beforeFragment[..queryIndex] : beforeFragment;

            return route.ToMirrorPath(NormalizeBackendPath(path), query, fragment);
        }

        /// <summary>
        /// Injects base and browser-side request rewriting support into HTML documents.
        /// </summary>
        private static string InjectHtmlMirrorSupport(string html, ProxyRoute route)
        {
            var insert = $"<base href=\"{route.RouteRoot}\">{CreateRuntimeShim(route)}";
            if (!html.Contains("<head", StringComparison.OrdinalIgnoreCase))
            {
                return insert + html;
            }

            return Regex.Replace(
                html,
                @"<head(?<attrs>[^>]*)>",
                match => match.Value + insert,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        /// <summary>
        /// Creates a browser-side shim for runtime APIs that construct root-relative URLs after page load.
        /// </summary>
        private static string CreateRuntimeShim(ProxyRoute route)
        {
            var routeRoot = route.RouteRoot.Replace("\\", "\\\\").Replace("'", "\\'");
            return $$"""
                <script>
                (() => {
                    const routeRoot = '{{routeRoot}}';
                    const routePrefix = routeRoot.replace(/\/$/, '');
                    const withRoute = value => {
                        if (typeof value !== 'string') return value;
                        try {
                            if (value.startsWith(location.origin + routePrefix + '/')) return value;
                            if (value === location.origin + routePrefix) return value;
                            if (value.startsWith(location.origin + '/') && !value.startsWith(location.origin + routePrefix + '/')) {
                                const parsed = new URL(value);
                                return location.origin + routePrefix + parsed.pathname + parsed.search + parsed.hash;
                            }
                        } catch {}
                        if (!value.startsWith('/') || value.startsWith('//') || value.startsWith(routePrefix + '/') || value === routePrefix) return value;
                        return routePrefix + value;
                    };
                    const originalFetch = window.fetch;
                    if (originalFetch) {
                        window.fetch = (input, init) => originalFetch.call(window, input instanceof Request ? new Request(withRoute(input.url), input) : withRoute(input), init);
                    }
                    const originalOpen = XMLHttpRequest.prototype.open;
                    XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                        return originalOpen.call(this, method, withRoute(url), ...rest);
                    };
                    const originalPushState = history.pushState;
                    history.pushState = function(state, title, url) {
                        return originalPushState.call(this, state, title, withRoute(url));
                    };
                    const originalReplaceState = history.replaceState;
                    history.replaceState = function(state, title, url) {
                        return originalReplaceState.call(this, state, title, withRoute(url));
                    };
                    const OriginalWebSocket = window.WebSocket;
                    if (OriginalWebSocket) {
                        window.WebSocket = function(url, protocols) {
                            if (typeof url === 'string' && url.startsWith('/')) {
                                const scheme = location.protocol === 'https:' ? 'wss://' : 'ws://';
                                url = scheme + location.host + withRoute(url);
                            }
                            return protocols === undefined ? new OriginalWebSocket(url) : new OriginalWebSocket(url, protocols);
                        };
                        window.WebSocket.prototype = OriginalWebSocket.prototype;
                    }
                    document.addEventListener('click', event => {
                        const link = event.target?.closest?.('a[href]');
                        if (!link) return;
                        const href = link.getAttribute('href');
                        if (href) link.setAttribute('href', withRoute(href));
                    }, true);
                    document.addEventListener('submit', event => {
                        const form = event.target;
                        if (!form?.getAttribute) return;
                        const action = form.getAttribute('action');
                        if (action) form.setAttribute('action', withRoute(action));
                    }, true);
                })();
                </script>
                """;
        }

        /// <summary>
        /// Resolves the response text encoding, falling back to UTF-8.
        /// </summary>
        private static Encoding ResolveContentEncoding(MediaTypeHeaderValue? contentType)
        {
            if (!string.IsNullOrWhiteSpace(contentType?.CharSet))
            {
                try
                {
                    return Encoding.GetEncoding(contentType.CharSet.Trim('"'));
                }
                catch
                {
                    // Invalid charset values fall back to UTF-8.
                }
            }

            return Encoding.UTF8;
        }


        // HTML index pages

        /// <summary>
        /// Writes the root index that lists every module and host path declared by the port map.
        /// </summary>
        private async Task WriteRootPageAsync(
            HttpListenerContext context,
            IReadOnlyDictionary<string, Dictionary<string, int>> portMap,
            int statusCode,
            string? message,
            CancellationToken ct)
        {
            var body = new StringBuilder();
            body.Append("<h1>ASLM API</h1>");
            body.Append("<p>Available module hosts from Data/App/ASLM_Ports.json.</p>");
            AppendMessage(body, message);
            var moduleStates = await LoadModuleDisplayStatesAsync();

            if (portMap.Count == 0)
            {
                body.Append("<section><h2>No module hosts</h2><p>Start a module so ASLM can assign ports.</p></section>");
            }
            else
            {
                foreach (var module in portMap.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    body.Append("<section>");
                    AppendModuleHeading(body, module.Key, moduleStates);
                    body.Append("<ul>");

                    foreach (var host in module.Value
                                 .OrderBy(static pair => pair.Value)
                                 .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        AppendHostLink(body, module.Key, host.Key, host.Value);
                    }

                    body.Append("</ul></section>");
                }
            }

            await WriteHtmlAsync(context, statusCode, "ASLM API", body.ToString(), ct);
        }

        /// <summary>
        /// Writes a module page that lists the hosts available under that module route.
        /// </summary>
        private async Task WriteModulePageAsync(
            HttpListenerContext context,
            string moduleId,
            IReadOnlyDictionary<string, int> hosts,
            int statusCode,
            string? message,
            CancellationToken ct)
        {
            var body = new StringBuilder();
            body.Append("<h1>");
            body.Append(HtmlEncode(moduleId));
            var moduleStates = await LoadModuleDisplayStatesAsync();
            AppendDisabledBadge(body, moduleId, moduleStates);
            body.Append("</h1>");
            body.Append("<p>Available hosts for this module.</p>");
            AppendMessage(body, message);
            body.Append("<ul>");

            foreach (var host in hosts
                         .OrderBy(static pair => pair.Value)
                         .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendHostLink(body, moduleId, host.Key, host.Value);
            }

            body.Append("</ul><p><a href=\"/\">Back to ASLM API</a></p>");
            await WriteHtmlAsync(context, statusCode, moduleId, body.ToString(), ct);
        }

        /// <summary>
        /// Writes an error page when request routing or proxying fails unexpectedly.
        /// </summary>
        private static async Task WriteErrorPageAsync(HttpListenerContext context, Exception ex, CancellationToken ct)
        {
            if (!context.Response.OutputStream.CanWrite)
            {
                return;
            }

            var body = $"<h1>ASLM API error</h1><p>{HtmlEncode(ex.Message)}</p><p><a href=\"/\">Back to ASLM API</a></p>";
            await WriteHtmlAsync(context, 500, "ASLM API Error", body, ct);
        }

        /// <summary>
        /// Writes a complete HTML document with the shared minimal ASLM API style.
        /// </summary>
        private static async Task WriteHtmlAsync(HttpListenerContext context, int statusCode, string title, string body, CancellationToken ct)
        {
            var html = $$"""
                <!doctype html>
                <html lang="en">
                <head>
                    <meta charset="utf-8">
                    <meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>{{HtmlEncode(title)}}</title>
                    <style>
                        :root { color-scheme: dark; font-family: "Segoe UI", Arial, sans-serif; background: #0f0f11; color: #f5f5f7; }
                        body { margin: 0; padding: 32px; }
                        main { max-width: 960px; margin: 0 auto; }
                        h1 { font-size: 28px; margin: 0 0 8px; }
                        h2 { font-size: 18px; margin: 0 0 10px; }
                        p { color: #b8b8c0; margin: 0 0 18px; }
                        section { border: 1px solid #38383a; background: #1c1c1e; border-radius: 8px; padding: 16px; margin: 12px 0; }
                        ul { list-style: none; padding: 0; margin: 0; }
                        li { display: flex; gap: 10px; align-items: baseline; padding: 8px 0; border-top: 1px solid #2c2c2e; }
                        li:first-child { border-top: 0; }
                        a { color: #64d2ff; text-decoration: none; }
                        a:hover { text-decoration: underline; }
                        code { color: #ebebf5; background: #2c2c2e; border-radius: 4px; padding: 2px 6px; }
                        .module-heading { display: flex; gap: 8px; align-items: center; }
                        .badge { display: inline-block; color: #b8b8c0; background: #2c2c2e; border: 1px solid #38383a; border-radius: 4px; padding: 2px 6px; font-size: 11px; font-weight: 600; vertical-align: middle; margin-left: 8px; }
                        .module-heading .badge { margin-left: 0; }
                        .message { border: 1px solid #ff9f0a; background: #2b2112; color: #ffd60a; border-radius: 8px; padding: 10px 12px; margin: 14px 0; }
                        .target { color: #8e8e93; font-size: 13px; }
                    </style>
                </head>
                <body>
                    <main>{{body}}</main>
                </body>
                </html>
                """;

            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }

        /// <summary>
        /// Writes a plain text response for low-level proxy validation failures.
        /// </summary>
        private static async Task WritePlainTextAsync(HttpListenerContext context, int statusCode, string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }

        /// <summary>
        /// Appends a warning message to a generated index page.
        /// </summary>
        private static void AppendMessage(StringBuilder body, string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            body.Append("<div class=\"message\">");
            body.Append(HtmlEncode(message));
            body.Append("</div>");
        }

        /// <summary>
        /// Appends one routed host link to a generated index page.
        /// </summary>
        private void AppendHostLink(StringBuilder body, string moduleId, string hostKey, int port)
        {
            var hostInfo = CreateHostInfo(moduleId, hostKey, port, null);
            body.Append("<li><a href=\"");
            body.Append(HtmlEncode(hostInfo.MirrorUrl));
            body.Append("\"><code>");
            body.Append(HtmlEncode($"{moduleId}/{BuildHostRouteKey(hostKey)}/"));
            body.Append("</code></a><span class=\"target\">127.0.0.1:");
            body.Append(port);
            body.Append("</span></li>");
        }

        /// <summary>
        /// Appends a module heading and disabled status marker for the generated index page.
        /// </summary>
        private static void AppendModuleHeading(
            StringBuilder body,
            string moduleId,
            IReadOnlyDictionary<string, bool> moduleStates)
        {
            body.Append("<h2 class=\"module-heading\"><span>");
            body.Append(HtmlEncode(moduleId));
            body.Append("</span>");
            AppendDisabledBadge(body, moduleId, moduleStates);
            body.Append("</h2>");
        }

        /// <summary>
        /// Appends a compact disabled badge when the module manifest says the module is off.
        /// </summary>
        private static void AppendDisabledBadge(
            StringBuilder body,
            string moduleId,
            IReadOnlyDictionary<string, bool> moduleStates)
        {
            if (!moduleStates.TryGetValue(moduleId, out var isEnabled) || isEnabled)
            {
                return;
            }

            body.Append("<span class=\"badge\">");
            body.Append(HtmlEncode(L.Get(LocalizationKeys.AslmApi_Disabled)));
            body.Append("</span>");
        }

        /// <summary>
        /// Loads enabled states from installed module manifests for the generated API index.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, bool>> LoadModuleDisplayStatesAsync()
        {
            try
            {
                var modules = await _moduleInstaller.DiscoverModulesAsync();
                return modules
                    .Where(static module => !string.IsNullOrWhiteSpace(module.Id))
                    .GroupBy(static module => module.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToDictionary(
                        static module => module.Id,
                        static module => module.Status.Enabled,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to load module enabled states for ASLM API index.");
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }
        }


        // Port map loading

        /// <summary>
        /// Loads the current port map from disk and supports the older flat module-to-port shape.
        /// </summary>
        private Dictionary<string, Dictionary<string, int>> LoadPortMap()
        {
            if (!File.Exists(_portMapPath))
            {
                return new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var json = File.ReadAllText(_portMapPath);
                var nested = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json, _jsonOptions);
                if (nested != null)
                {
                    return NormalizePortMap(nested);
                }
            }
            catch
            {
                // Try the legacy flat schema before giving up on the runtime port map.
            }

            try
            {
                var json = File.ReadAllText(_portMapPath);
                var flat = JsonSerializer.Deserialize<Dictionary<string, int>>(json, _jsonOptions);
                return flat?
                    .Where(static pair => IsPublicModulePortOwner(pair.Key) && IsValidPort(pair.Value))
                    .ToDictionary(
                        static pair => pair.Key,
                        static pair => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["port"] = pair.Value },
                        StringComparer.OrdinalIgnoreCase) ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load ASLM port map from {PortMapPath}", _portMapPath);
                return new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Removes invalid module and host entries from a deserialized port map.
        /// </summary>
        private static Dictionary<string, Dictionary<string, int>> NormalizePortMap(Dictionary<string, Dictionary<string, int>> source)
        {
            return source
                .Where(static module => IsPublicModulePortOwner(module.Key) && module.Value != null)
                .ToDictionary(
                    static module => module.Key,
                    static module => module.Value
                        .Where(static host => !string.IsNullOrWhiteSpace(host.Key) && IsValidPort(host.Value))
                        .ToDictionary(static host => host.Key, static host => host.Value, StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase)
                .Where(static module => module.Value.Count > 0)
                .ToDictionary(static module => module.Key, static module => module.Value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether the port-map owner should be exposed as a mirrorable module route.
        /// </summary>
        private static bool IsPublicModulePortOwner(string ownerId)
        {
            return !string.IsNullOrWhiteSpace(ownerId) &&
                   !ownerId.StartsWith("__", StringComparison.Ordinal);
        }


        // Shared helpers

        /// <summary>
        /// Creates one host info object for the ASLM UI and web index.
        /// </summary>
        private AslmMirrorHostInfo CreateHostInfo(string moduleId, string hostKey, int port, bool? isOnline)
        {
            var route = ProxyRoute.Proxy(moduleId, hostKey, BuildHostRouteKey(hostKey), port, "/", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            return new AslmMirrorHostInfo(
                moduleId,
                hostKey,
                port,
                $"{BaseUrl.TrimEnd('/')}{route.RouteRoot}",
                $"http://127.0.0.1:{port}/",
                isOnline);
        }

        /// <summary>
        /// Redirects a module host route to its slash-terminated root.
        /// </summary>
        private static void RedirectToRouteRoot(HttpListenerContext context, string moduleId, string routeHostKey)
        {
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = ProxyRoute.BuildRouteRoot(moduleId, routeHostKey);
            context.Response.Close();
        }

        /// <summary>
        /// Builds the public route key used for one port-map host.
        /// </summary>
        private static string BuildHostRouteKey(string hostKey) =>
            PortRegistry.BuildHostRouteKey(hostKey);

        /// <summary>
        /// Returns whether a path is already mounted under a mirror route prefix.
        /// </summary>
        private static bool IsMirrorPath(string path, string routePrefix)
        {
            return path.Equals(routePrefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(routePrefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Converts a mirror-prefixed path back into the backend root-relative path.
        /// </summary>
        private static string StripMirrorPrefix(string path, string routePrefix)
        {
            if (path.Equals(routePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return "/";
            }

            return path.StartsWith(routePrefix + "/", StringComparison.OrdinalIgnoreCase)
                ? path[routePrefix.Length..]
                : path;
        }

        /// <summary>
        /// Normalizes a backend path to a root-relative path without query or fragment.
        /// </summary>
        private static string NormalizeBackendPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "/";
            }

            var cleanPath = path;
            var queryIndex = cleanPath.IndexOf('?');
            if (queryIndex >= 0)
            {
                cleanPath = cleanPath[..queryIndex];
            }

            var fragmentIndex = cleanPath.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                cleanPath = cleanPath[..fragmentIndex];
            }

            return cleanPath.StartsWith("/", StringComparison.Ordinal)
                ? cleanPath
                : "/" + cleanPath;
        }

        /// <summary>
        /// HTML-encodes text for generated index pages.
        /// </summary>
        private static string HtmlEncode(string? value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        /// <summary>
        /// Checks whether a localhost TCP port is currently accepting connections.
        /// </summary>
        private static async Task<bool> IsTcpPortOpenAsync(int port, CancellationToken ct)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port, ct).AsTask();
                var completed = await Task.WhenAny(connectTask, Task.Delay(250, ct));
                return completed == connectTask && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks whether a TCP port value can be used in a localhost URL.
        /// </summary>
        private static bool IsValidPort(int port)
        {
            return port is >= 1 and <= 65535;
        }

        /// <summary>
        /// Creates the shared HTTP client used by proxied backend requests.
        /// </summary>
        private static HttpClient CreateProxyClient()
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseCookies = false
            };

            return new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }

        /// <summary>
        /// Raises the state changed event on the current thread.
        /// </summary>
        private void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Clears listener references after stop or failed start.
        /// </summary>
        private void CleanupListener()
        {
            _listener = null;
            _activePort = null;
            _listenerCts?.Dispose();
            _listenerCts = null;
            _listenerTask = null;
        }

        /// <summary>
        /// Gets or reserves the ASLM API port from the shared module port pool.
        /// </summary>
        private int GetAssignedPort()
        {
            _ports.GetOrAssignInternalServicePort(
                PortRegistry.AslmMirrorServiceId,
                PortRegistry.AslmMirrorPortKey);
            _ports.EnsurePortsAvailable(PortRegistry.AslmMirrorServiceId);
            return _ports.GetOrAssignInternalServicePort(
                PortRegistry.AslmMirrorServiceId,
                PortRegistry.AslmMirrorPortKey);
        }

        /// <summary>
        /// Builds the localhost base URL for the requested reverse proxy port.
        /// </summary>
        private static string BuildBaseUrl(int port)
        {
            return $"http://127.0.0.1:{port}/";
        }

        /// <summary>
        /// Returns the application root directory above the deployed app folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Disposal

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopAsync().GetAwaiter().GetResult();
        }


        // Route model

        /// <summary>
        /// Describes the resolved action for one incoming ASLM API request.
        /// </summary>
        private sealed record ProxyRoute(
            ProxyRouteKind Kind,
            string ModuleId,
            string HostKey,
            string RouteHostKey,
            int Port,
            string TargetPath,
            IReadOnlyDictionary<string, int> Hosts)
        {
            /// <summary>
            /// Gets the mounted mirror route prefix without a trailing slash.
            /// </summary>
            public string RoutePrefix => $"/{Uri.EscapeDataString(ModuleId)}/{Uri.EscapeDataString(RouteHostKey)}";

            /// <summary>
            /// Gets the mounted mirror route root with a trailing slash.
            /// </summary>
            public string RouteRoot => RoutePrefix + "/";

            /// <summary>
            /// Creates the root-index route.
            /// </summary>
            public static ProxyRoute RootIndex() =>
                new(ProxyRouteKind.RootIndex, string.Empty, string.Empty, string.Empty, 0, "/", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            /// <summary>
            /// Creates the module-index route.
            /// </summary>
            public static ProxyRoute ModuleIndex(string moduleId, IReadOnlyDictionary<string, int> hosts) =>
                new(ProxyRouteKind.ModuleIndex, moduleId, string.Empty, string.Empty, 0, "/", hosts);

            /// <summary>
            /// Creates the module-not-found route.
            /// </summary>
            public static ProxyRoute ModuleNotFound(string moduleId) =>
                new(ProxyRouteKind.ModuleNotFound, moduleId, string.Empty, string.Empty, 0, "/", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            /// <summary>
            /// Creates the host-not-found route.
            /// </summary>
            public static ProxyRoute HostNotFound(string moduleId, string hostKey, IReadOnlyDictionary<string, int> hosts) =>
                new(ProxyRouteKind.HostNotFound, moduleId, hostKey, hostKey, 0, "/", hosts);

            /// <summary>
            /// Creates the slash-normalization redirect route.
            /// </summary>
            public static ProxyRoute RedirectToSlash(string moduleId, string hostKey, string routeHostKey) =>
                new(ProxyRouteKind.RedirectToSlash, moduleId, hostKey, routeHostKey, 0, "/", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

            /// <summary>
            /// Creates a backend proxy route.
            /// </summary>
            public static ProxyRoute Proxy(string moduleId, string hostKey, string routeHostKey, int port, string targetPath, IReadOnlyDictionary<string, int> hosts) =>
                new(ProxyRouteKind.Proxy, moduleId, hostKey, routeHostKey, port, NormalizeBackendPath(targetPath), hosts);

            /// <summary>
            /// Builds the slash-terminated route root for a module host.
            /// </summary>
            public static string BuildRouteRoot(string moduleId, string hostKey) =>
                $"/{Uri.EscapeDataString(moduleId)}/{Uri.EscapeDataString(hostKey)}/";

            /// <summary>
            /// Converts one backend path into a browser-visible mirror path.
            /// </summary>
            public string ToMirrorPath(string backendPath, string query, string fragment)
            {
                var normalizedPath = NormalizeBackendPath(backendPath);
                var suffix = normalizedPath == "/"
                    ? string.Empty
                    : normalizedPath.TrimStart('/');
                return RouteRoot + suffix + query + fragment;
            }
        }

        /// <summary>
        /// Distinguishes route outcomes for incoming ASLM API requests.
        /// </summary>
        private enum ProxyRouteKind
        {
            RootIndex,
            ModuleIndex,
            ModuleNotFound,
            HostNotFound,
            RedirectToSlash,
            Proxy
        }
    }

    /// <summary>
    /// Describes one ASLM API mirror host displayed in the UI.
    /// </summary>
    public record AslmMirrorHostInfo(
        string ModuleId,
        string HostKey,
        int Port,
        string MirrorUrl,
        string TargetUrl,
        bool? IsOnline);
}
