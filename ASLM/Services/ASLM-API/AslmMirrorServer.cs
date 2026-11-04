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
        // Constants and shared state

        /// <summary>
        /// Maximum number of same-backend redirects the proxy follows itself before answering the browser.
        /// </summary>
        private const int BackendRedirectLimit = 10;

        /// <summary>
        /// Request header that tells the backend under which mirror route prefix it is mounted.
        /// </summary>
        private const string RouteHeaderName = "X-ASLM-Mirror-Route";

        /// <summary>
        /// HTTP client shared by all proxied backend requests.
        /// </summary>
        private static readonly HttpClient ProxyClient = CreateProxyClient();


        // Dependencies

        /// <summary>
        /// Persisted application settings; provide the enabled switch and the fallback port for the mirror.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Shared port map from which the mirror port is read and reserved.
        /// </summary>
        private readonly PortRegistry _ports;

        /// <summary>
        /// Module registry and discovery, used to expose only installed modules and to show their enabled state.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Logger for startup failures, request errors and port map problems.
        /// </summary>
        private readonly ILogger<AslmMirrorServer> _logger;

        /// <summary>
        /// JSON settings used to read the port map file; property names are case-insensitive.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };


        // Server state

        /// <summary>
        /// Guards the listener fields and the last error against concurrent start, stop and reads.
        /// </summary>
        private readonly object _stateLock = new();

        /// <summary>
        /// Absolute path of <c>Data/App/ASLM_Ports.json</c>, which lists the hosts of running modules.
        /// </summary>
        private readonly string _portMapPath;

        /// <summary>
        /// Active HTTP listener, or null while the server is stopped.
        /// </summary>
        private HttpListener? _listener;

        /// <summary>
        /// Cancellation source that ends the request loop of the active listener.
        /// </summary>
        private CancellationTokenSource? _listenerCts;

        /// <summary>
        /// Background task that accepts requests for the active listener.
        /// </summary>
        private Task? _listenerTask;

        /// <summary>
        /// Port the listener is bound to, or null while the server is stopped.
        /// </summary>
        private int? _activePort;

        /// <summary>
        /// Set once the server was disposed, so a repeated disposal does nothing.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Message of the most recent startup failure; empty after a successful start.
        /// </summary>
        private string _lastError = string.Empty;


        // Events

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
        /// Gets the localhost port of the mirror server: the bound port while listening, otherwise the port reserved
        /// in the shared port map, otherwise the start of the module port range.
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
        /// The port map is redistributed either way, so the mirror port is only reserved while the proxy is enabled.
        /// </summary>
        public async Task StartIfEnabledAsync()
        {
            // A disabled proxy gives its reserved port back to the shared pool.
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
            // Nothing to do when the state does not change.
            if (_appData.Data.Api.ServerEnabled == enabled)
            {
                return;
            }

            _appData.Data.Api.ServerEnabled = enabled;
            _appData.Save();

            // Reserve the port before starting, and release it only after the listener has stopped.
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
        /// Starts the local listener and background request loop. Does nothing when the listener is already running;
        /// a startup failure is stored in <see cref="LastError"/> instead of being thrown.
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
                    // Bind to the loopback address on the port reserved for this service.
                    var assignedPort = GetAssignedPort();
                    _listenerCts = new CancellationTokenSource();
                    _listener = new HttpListener();
                    _listener.Prefixes.Add(BuildBaseUrl(assignedPort));
                    _listener.Start();

                    // Remember the bound port and begin accepting requests.
                    _activePort = assignedPort;
                    _lastError = string.Empty;
                    _listenerTask = Task.Run(() => RunListenerLoopAsync(_listener, _listenerCts.Token));
                }
                catch (Exception ex)
                {
                    // Roll back to the stopped state so a later start can retry.
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
                // Signal the request loop and close the listener; a failure here is only logged.
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

                // Keep the loop task to await it outside the lock, then reset the listener fields.
                listenerTask = _listenerTask;
                CleanupListener();
            }

            // Give the request loop a short time to exit.
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
        /// Returns assigned hosts only for modules present in the installed registry,
        /// ordered by module id, then port, then host key. The online state of each host is left unknown.
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
            // Probe all hosts in parallel so one unresponsive port does not delay the others.
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
                    // The listener was closed by a stop request, so the loop ends.
                    break;
                }
                catch (HttpListenerException)
                {
                    // The pending accept was aborted (for example by stopping the listener), so the loop ends.
                    break;
                }
                catch (InvalidOperationException)
                {
                    // The listener is no longer listening, so the loop ends.
                    break;
                }

                // Each request is handled on its own task so a slow handler never blocks the accept loop.
                _ = Task.Run(() => HandleContextAsync(context, ct), ct);
            }
        }

        /// <summary>
        /// Routes one incoming request to an index page, module host page, or proxied backend request.
        /// Unexpected failures are logged and answered with an error page.
        /// </summary>
        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
        {
            try
            {
                // The port map is read per request, so newly started modules become reachable immediately.
                var portMap = LoadPortMap();
                var route = ResolveRoute(context.Request, portMap);

                // Answer according to the kind of route the URL resolved to.
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
                // Log the failure and show it to the browser as an error page.
                _logger.LogError(ex, "ASLM API reverse proxy request failed.");
                await WriteErrorPageAsync(context, ex, ct);
            }
        }


        // Route resolution

        /// <summary>
        /// Resolves the incoming URL into a root page, listing page, or backend proxy route.
        /// The URL layout is <c>/{moduleId}/{hostRouteKey}/{backend path}</c>.
        /// </summary>
        private static ProxyRoute ResolveRoute(HttpListenerRequest request, IReadOnlyDictionary<string, Dictionary<string, int>> portMap)
        {
            var pathSegments = GetPathSegments(request.Url?.AbsolutePath ?? "/");

            // "/" lists all modules.
            if (pathSegments.Count == 0)
            {
                return ProxyRoute.RootIndex();
            }

            // The first segment names the module. An unknown module may still be a root-relative request
            // (for example an asset URL) issued by a page that is already served through a mirror route.
            var moduleId = pathSegments[0];
            if (!portMap.TryGetValue(moduleId, out var hosts))
            {
                return TryResolveRefererProxyRoute(request, portMap, pathSegments, out var refererRoute)
                    ? refererRoute
                    : ProxyRoute.ModuleNotFound(moduleId);
            }

            // "/{moduleId}" lists the hosts of that module.
            if (pathSegments.Count == 1)
            {
                return ProxyRoute.ModuleIndex(moduleId, hosts);
            }

            // The second segment selects one host of the module by its public route key.
            var routeHostKey = pathSegments[1];
            if (!TryResolveHostRoute(hosts, routeHostKey, out var hostKey, out var port))
            {
                return ProxyRoute.HostNotFound(moduleId, routeHostKey, hosts);
            }

            // The host root needs its trailing slash so relative links in the page resolve below the route.
            if (pathSegments.Count == 2 && request.Url?.AbsolutePath.EndsWith('/') == false)
            {
                return ProxyRoute.RedirectToSlash(moduleId, hostKey, BuildHostRouteKey(hostKey));
            }

            // Everything after the host segment is forwarded to the backend.
            var preserveTrailingSlash = request.Url?.AbsolutePath.EndsWith("/", StringComparison.Ordinal) == true;
            var targetPath = BuildTargetPath(pathSegments.Skip(2), preserveTrailingSlash);
            return ProxyRoute.Proxy(moduleId, hostKey, BuildHostRouteKey(hostKey), port, targetPath, hosts);
        }

        /// <summary>
        /// Resolves root-relative asset or API requests by using the mirror route from the referring page.
        /// Returns false when the request has no referrer or the referrer is not itself below a known mirror route.
        /// </summary>
        private static bool TryResolveRefererProxyRoute(
            HttpListenerRequest request,
            IReadOnlyDictionary<string, Dictionary<string, int>> portMap,
            IReadOnlyList<string> requestSegments,
            out ProxyRoute route)
        {
            route = ProxyRoute.RootIndex();

            // Only requests that carry a referrer can be attributed to a mirror route.
            var referer = request.UrlReferrer;
            if (referer == null)
            {
                return false;
            }

            // The referrer path must start with /{moduleId}/{hostRouteKey}.
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

            // Send the whole request path to the backend of the referring route.
            var preserveTrailingSlash = request.Url?.AbsolutePath.EndsWith("/", StringComparison.Ordinal) == true;
            route = ProxyRoute.Proxy(moduleId, hostKey, BuildHostRouteKey(hostKey), port, BuildTargetPath(requestSegments, preserveTrailingSlash), hosts);
            return true;
        }

        /// <summary>
        /// Resolves a public host route segment back to the underlying port-map host key.
        /// An exact host key wins; otherwise the hosts are scanned by port and key for a matching public route key.
        /// </summary>
        private static bool TryResolveHostRoute(
            IReadOnlyDictionary<string, int> hosts,
            string routeHostKey,
            out string hostKey,
            out int port)
        {
            // The segment may already be the raw host key.
            if (hosts.TryGetValue(routeHostKey, out port))
            {
                hostKey = routeHostKey;
                return true;
            }

            // Otherwise compare against the public route key derived from each host key.
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
        /// Builds a backend path from decoded path segments, re-escaping each segment.
        /// A trailing slash is kept when the browser sent one.
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
        /// Builds a backend path from decoded path segments, re-escaping each segment and never adding a trailing slash.
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
        /// HTML responses are buffered and rewritten to stay under the mirror route; other bodies are streamed unchanged.
        /// </summary>
        private static async Task ProxyRequestAsync(HttpListenerContext context, ProxyRoute route, CancellationToken ct)
        {
            // Never connect to a port outside the valid TCP range.
            if (route.Port < 1 || route.Port > 65535)
            {
                await WritePlainTextAsync(context, 502, "Invalid target port.", ct);
                return;
            }

            // WebSocket upgrades are relayed frame by frame instead of through the HTTP path.
            if (context.Request.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(context, route, ct);
                return;
            }

            // Forward the request and mirror the backend status and headers to the browser.
            using var responseMessage = await SendHttpRequestWithInternalRedirectsAsync(context.Request, route, ct);
            context.Response.StatusCode = (int)responseMessage.StatusCode;
            CopyResponseHeaders(responseMessage, context.Response, route);

            // Write the body: rewrite HTML so links stay below the route, copy everything else as is.
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
        /// Redirects to the same backend are followed only for GET and HEAD, at most <see cref="BackendRedirectLimit"/>
        /// times, and a redirect loop returns the redirect response to the browser.
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
                // Only the first request carries the browser's body; followed redirects are bodyless.
                using var requestMessage = CreateHttpRequest(sourceRequest, currentRoute, currentQuery, includeBody: redirectCount == 0);
                var responseMessage = await ProxyClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);

                // A response that is not a followable local redirect is the final answer.
                if (!TryResolveInternalRedirect(sourceRequest, responseMessage, currentRoute, out var nextRoute, out var nextQuery))
                {
                    return responseMessage;
                }

                // Stop at a redirect loop and let the browser see the redirect itself.
                var visitKey = $"{nextRoute.TargetPath}?{nextQuery}";
                if (!visited.Add(visitKey))
                {
                    return responseMessage;
                }

                // Follow the redirect with a fresh backend request.
                responseMessage.Dispose();
                currentRoute = nextRoute;
                currentQuery = nextQuery;
            }

            // The redirect limit is used up: send one last bodyless request to the current target and return it as is.
            return await ProxyClient.SendAsync(
                CreateHttpRequest(sourceRequest, currentRoute, currentQuery, includeBody: false),
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }

        /// <summary>
        /// Builds one backend HTTP request for the resolved proxy route.
        /// The target is always the loopback address on the route's port.
        /// </summary>
        private static HttpRequestMessage CreateHttpRequest(
            HttpListenerRequest sourceRequest,
            ProxyRoute route,
            string query,
            bool includeBody)
        {
            // Same method, backend path and query as the browser request, aimed at the backend port.
            var builder = new UriBuilder("http", "127.0.0.1", route.Port, route.TargetPath)
            {
                Query = query
            };

            var requestMessage = new HttpRequestMessage(new HttpMethod(sourceRequest.HttpMethod), builder.Uri);

            // Stream the browser body to the backend together with its content type and length.
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

            // Forward the remaining browser headers, then add the headers that describe the mount point.
            CopyRequestHeaders(sourceRequest.Headers, requestMessage);
            AddForwardedHeaders(sourceRequest, requestMessage, route);
            return requestMessage;
        }

        /// <summary>
        /// Copies browser request headers that are safe to forward to the backend.
        /// Content headers that do not fit the request headers are added to the body headers.
        /// </summary>
        private static void CopyRequestHeaders(NameValueCollection sourceHeaders, HttpRequestMessage targetRequest)
        {
            foreach (var headerName in sourceHeaders.AllKeys.OfType<string>())
            {
                // Transport-level and rewritten headers are set by the proxy itself.
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

            // Request uncompressed bodies: the proxy client does not decompress, and HTML must be rewritten as text.
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

            // Origin and Referer are passed on only when the browser sent them; both then name the backend, not the mirror.
            if (!string.IsNullOrWhiteSpace(sourceRequest.Headers["Origin"]))
            {
                targetRequest.Headers.TryAddWithoutValidation("Origin", backendOrigin);
            }

            if (sourceRequest.UrlReferrer != null)
            {
                var backendReferer = RewriteMirrorUriToBackendUri(sourceRequest.UrlReferrer, route, backendOrigin);
                targetRequest.Headers.TryAddWithoutValidation("Referer", backendReferer);
            }

            // Describe the original request and the mount prefix; a route header sent by the browser is replaced.
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
        /// When it does, <paramref name="nextRoute"/> and <paramref name="nextQuery"/> describe the redirect target.
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

            // Only a redirect that carries a Location and answers a GET or HEAD request is followed.
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
                // An absolute redirect is internal only when it names the same loopback backend port.
                if (!location.IsLoopback || location.Port != currentRoute.Port)
                {
                    return false;
                }

                targetUri = location;
            }
            else
            {
                // A relative redirect is resolved against the backend root; a mirror prefix is removed first.
                var locationText = location.ToString();
                if (IsMirrorPath(locationText, currentRoute.RoutePrefix))
                {
                    locationText = StripMirrorPrefix(locationText, currentRoute.RoutePrefix);
                }

                targetUri = new Uri(new Uri($"http://127.0.0.1:{currentRoute.Port}/"), locationText);
            }

            // Continue on the same route with the redirect's path and query.
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
        /// The browser connection is accepted only after the backend connection was established.
        /// </summary>
        private static async Task ProxyWebSocketAsync(HttpListenerContext context, ProxyRoute route, CancellationToken ct)
        {
            // Connect to the backend first, offering the subprotocol the browser asked for.
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
                // A backend that cannot be reached is reported to the browser as 502 instead of an upgrade.
                await WritePlainTextAsync(context, 502, "WebSocket target is unavailable.", ct);
                return;
            }

            // Accept the browser socket and relay frames both ways until one side ends.
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
        /// Relays WebSocket frames from one socket to the other until either side closes or errors.
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
                    // A broken or aborted source connection ends the relay for this direction.
                    break;
                }

                // Pass a close handshake on to the other side and stop.
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await CloseWebSocketSafeAsync(target, result.CloseStatus, result.CloseStatusDescription, ct);
                    break;
                }

                // Forward the received frame unchanged, keeping its message type and fragmentation.
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
            // General response headers; a redirect Location is rewritten to stay under the mirror route.
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

            // Content headers; the content type has its own property on the listener response.
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

            // Cookies are copied one by one, with their domain and path scoped to the mirror route.
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
        /// The <c>Domain</c> attribute is dropped so the cookie stays bound to the mirror host.
        /// </summary>
        private static string RewriteSetCookie(string cookie, ProxyRoute route)
        {
            var parts = cookie.Split(';', StringSplitOptions.TrimEntries);
            var rebuilt = new List<string>();
            var hasPath = false;

            // Drop Domain, replace Path with the route root, and keep every other attribute.
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

            // A cookie without a Path gets the route root, so it is scoped to this route as well.
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
            // An empty Location points back to the route root.
            if (string.IsNullOrWhiteSpace(location))
            {
                return route.RouteRoot;
            }

            // An absolute URL of this backend becomes a path below the mirror route.
            if (Uri.TryCreate(location, UriKind.Absolute, out var absoluteUri) &&
                absoluteUri.IsLoopback &&
                absoluteUri.Port == route.Port)
            {
                return route.ToMirrorPath(NormalizeBackendPath(absoluteUri.AbsolutePath), absoluteUri.Query, absoluteUri.Fragment);
            }

            // A path that already carries the route prefix needs no change.
            if (IsMirrorPath(location, route.RoutePrefix))
            {
                return location;
            }

            // A root-relative path (not protocol-relative) is mounted below the route.
            if (location.StartsWith("/", StringComparison.Ordinal) && !location.StartsWith("//", StringComparison.Ordinal))
            {
                return route.ToMirrorPath(NormalizeBackendPath(location), query: string.Empty, fragment: string.Empty);
            }

            // Relative and external locations are passed through unchanged.
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
        /// Returns whether a request header is controlled by the proxy transport, or is rebuilt separately
        /// (<c>Origin</c>, <c>Referer</c>), and therefore should not be copied.
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
        /// The backend's <c>Content-Security-Policy</c> and <c>Referrer-Policy</c> headers are not forwarded either,
        /// and cookies are handled separately.
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
        /// Returns whether a backend response body can be rewritten as text. Only <c>text/html</c> is rewritten.
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
            // Absolute backend URLs first, then root-relative URLs.
            var rewritten = RewriteLoopbackBackendUrls(content, route);
            rewritten = RewriteRootRelativeUrls(rewritten, route);

            // HTML documents also get the base element and the browser-side shim.
            if (mediaType != null && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                rewritten = InjectHtmlMirrorSupport(rewritten, route);
            }

            return rewritten;
        }

        /// <summary>
        /// Rewrites absolute <c>http(s)://127.0.0.1:{port}</c> and <c>localhost:{port}</c> URLs that point at the
        /// backend target into mirror-root URLs.
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
        /// Rewrites common root-relative URLs in the text, including inline CSS, JavaScript, and JSON fragments.
        /// </summary>
        private static string RewriteRootRelativeUrls(string content, ProxyRoute route)
        {
            // Quoted URL attributes such as href, src and action.
            var rewritten = Regex.Replace(
                content,
                @"(?<prefix>\b(?:href|src|action|poster|data|formaction|manifest)\s*=\s*)(?<quote>[""'])(?<url>/[^""']*)(\k<quote>)",
                match => RewriteAttributeUrl(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // CSS url(...) references.
            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\burl\(\s*)(?<quote>[""']?)(?<url>/[^""')]+)(?<suffix>\k<quote>\s*\))",
                match => RewriteCssUrl(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // srcset candidate lists.
            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\bsrcset\s*=\s*)(?<quote>[""'])(?<value>[^""']*)(\k<quote>)",
                match => RewriteSrcSet(match, route),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Meta refresh targets of the form content="0; url=/path".
            rewritten = Regex.Replace(
                rewritten,
                @"(?<prefix>\bcontent\s*=\s*)(?<quote>[""'])(?<value>[^""']*;\s*url=)(?<url>/[^""']*)(\k<quote>)",
                match => $"{match.Groups["prefix"].Value}{match.Groups["quote"].Value}{match.Groups["value"].Value}{RewriteOneRootRelativeUrl(match.Groups["url"].Value, route)}{match.Groups["quote"].Value}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Any other quoted string that starts with a slash, which covers URLs in scripts and JSON.
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
        /// Protocol-relative URLs and URLs that already carry the route prefix are returned unchanged.
        /// </summary>
        private static string RewriteOneRootRelativeUrl(string url, ProxyRoute route)
        {
            // Leave empty, non-root-relative, protocol-relative and already mounted URLs alone.
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith("/", StringComparison.Ordinal) ||
                url.StartsWith("//", StringComparison.Ordinal) ||
                IsMirrorPath(url, route.RoutePrefix))
            {
                return url;
            }

            // Split the URL into path, query and fragment, then mount the path below the route root.
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
        /// The markup goes right after the opening head tag, or at the start of a document without one.
        /// </summary>
        private static string InjectHtmlMirrorSupport(string html, ProxyRoute route)
        {
            var insert = $"<base href=\"{route.RouteRoot}\">{CreateRuntimeShim(route)}";
            if (!html.Contains("<head", StringComparison.OrdinalIgnoreCase))
            {
                return insert + html;
            }

            // Insert the markup after each opening tag matched by the head pattern (the match has a 100 ms timeout).
            return Regex.Replace(
                html,
                @"<head(?<attrs>[^>]*)>",
                match => match.Value + insert,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }

        /// <summary>
        /// Creates a browser-side shim for runtime APIs that construct root-relative URLs after page load.
        /// The script prefixes such URLs with the route root in <c>fetch</c>, <c>XMLHttpRequest</c>, history state
        /// changes, <c>WebSocket</c> constructors, link clicks and form submissions.
        /// </summary>
        private static string CreateRuntimeShim(ProxyRoute route)
        {
            // Escape the route root for use inside a single-quoted JavaScript string.
            var routeRoot = route.RouteRoot.Replace("\\", "\\\\").Replace("'", "\\'");

            // The script is emitted verbatim; only the route root is interpolated.
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
        /// Resolves the response text encoding from the content type charset, falling back to UTF-8
        /// when the charset is missing or unknown.
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
            // Page header, optional warning message and the enabled state of each module.
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
                // One section per module, each with a link for every host ordered by port.
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

            // Wrap the generated body in the shared page template and send it with the requested status.
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
            // Heading with the module id (and a badge when the module is disabled), then the optional message.
            var body = new StringBuilder();
            body.Append("<h1>");
            body.Append(HtmlEncode(moduleId));
            var moduleStates = await LoadModuleDisplayStatesAsync();
            AppendDisabledBadge(body, moduleId, moduleStates);
            body.Append("</h1>");
            body.Append("<p>Available hosts for this module.</p>");
            AppendMessage(body, message);
            body.Append("<ul>");

            // List the hosts ordered by port, then by key.
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
        /// Nothing is written when the response stream can no longer be written to.
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
        /// The title is HTML-encoded; the body is inserted as given, so callers must encode their own content.
        /// </summary>
        private static async Task WriteHtmlAsync(HttpListenerContext context, int statusCode, string title, string body, CancellationToken ct)
        {
            // Page template: dark theme styles and the caller's body inside a single main element.
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

            // Send the document as UTF-8 with an explicit length and close the response.
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
        /// Appends one routed host link to a generated index page, together with the backend address it points to.
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
            if (moduleStates.TryGetValue(moduleId, out var isEnabled) && isEnabled)
            {
                return;
            }

            body.Append("<span class=\"badge\">");
            body.Append(HtmlEncode(L.Get(LocalizationKeys.AslmApi_Disabled)));
            body.Append("</span>");
        }

        /// <summary>
        /// Loads enabled states from installed module manifests for the generated API index.
        /// Returns an empty map when discovery fails, so the index page is still shown.
        /// </summary>
        private async Task<IReadOnlyDictionary<string, bool>> LoadModuleDisplayStatesAsync()
        {
            try
            {
                var modules = await _moduleInstaller.DiscoverInstalledModulesAsync();
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
        /// Loads registered module routes from disk, including the older flat module-to-port shape.
        /// Only modules in the installed registry are returned; an unreadable file or registry yields an empty map.
        /// </summary>
        private Dictionary<string, Dictionary<string, int>> LoadPortMap()
        {
            // No port map means no module has been assigned ports yet.
            if (!File.Exists(_portMapPath))
            {
                return new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            }

            // Read the ids of installed modules.
            HashSet<string> installed;
            try
            {
                // The registry is authoritative even when an old assignment or catalog manifest remains.
                // Apply this to routing as well as listings: a recycled port may now belong to another module.
                installed = _moduleInstaller.Registry.ReadIds();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                _logger.LogWarning(ex, "Failed to read installed modules for ASLM API routes.");
                return new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            }

            // Current schema: module id -> host key -> port.
            try
            {
                var json = File.ReadAllText(_portMapPath);
                var nested = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json, _jsonOptions);
                if (nested != null)
                {
                    return NormalizePortMap(nested, installed);
                }
            }
            catch
            {
                // Try the legacy flat schema before giving up on the runtime port map.
            }

            // Legacy schema: module id -> one port, exposed as a single host named "port".
            try
            {
                var json = File.ReadAllText(_portMapPath);
                var flat = JsonSerializer.Deserialize<Dictionary<string, int>>(json, _jsonOptions);
                return flat?
                    .Where(pair => installed.Contains(pair.Key) && IsPublicModulePortOwner(pair.Key) && IsValidPort(pair.Value))
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
        /// Removes uninstalled owners and invalid host entries from a deserialized port map.
        /// </summary>
        private static Dictionary<string, Dictionary<string, int>> NormalizePortMap(
            Dictionary<string, Dictionary<string, int>> source, IReadOnlySet<string> installed)
        {
            return source
                .Where(module => installed.Contains(module.Key) && IsPublicModulePortOwner(module.Key) && module.Value != null)
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
        /// Owners whose id starts with two underscores are internal host services and are never exposed.
        /// </summary>
        private static bool IsPublicModulePortOwner(string ownerId)
        {
            return !string.IsNullOrWhiteSpace(ownerId) &&
                   !ownerId.StartsWith("__", StringComparison.Ordinal);
        }


        // Shared helpers

        /// <summary>
        /// Creates one host info object for the ASLM UI and web index, with the mirror URL and the direct backend URL.
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
        /// Redirects a module host route to its slash-terminated root with a 302 response.
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
        /// A connection attempt that takes longer than 250 ms counts as not open.
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
        /// It follows no redirects, decompresses nothing, keeps no cookies and has no timeout,
        /// so the proxy receives and relays the raw backend response itself.
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

        /// <summary>
        /// Stops the listener once and blocks until the shutdown has finished.
        /// </summary>
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
        /// <param name="Kind">What the request resolved to: an index page, an error page, a redirect or a proxy.</param>
        /// <param name="ModuleId">Module id taken from the first URL segment, or empty for the root index.</param>
        /// <param name="HostKey">Host key from the port map, or the requested key when it was not found.</param>
        /// <param name="RouteHostKey">Public route key of the host as it appears in the URL.</param>
        /// <param name="Port">Backend port to proxy to; 0 for routes that are not proxied.</param>
        /// <param name="TargetPath">Root-relative backend path that is requested from the backend.</param>
        /// <param name="Hosts">Hosts of the module, used by listing pages.</param>
        private sealed record ProxyRoute(
            ProxyRouteKind Kind,
            string ModuleId,
            string HostKey,
            string RouteHostKey,
            int Port,
            string TargetPath,
            IReadOnlyDictionary<string, int> Hosts)
        {
            // Route paths

            /// <summary>
            /// Gets the mounted mirror route prefix without a trailing slash.
            /// </summary>
            public string RoutePrefix => $"/{Uri.EscapeDataString(ModuleId)}/{Uri.EscapeDataString(RouteHostKey)}";

            /// <summary>
            /// Gets the mounted mirror route root with a trailing slash.
            /// </summary>
            public string RouteRoot => RoutePrefix + "/";


            // Route factories

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


            // Path building

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
            /// <summary>
            /// The root URL, answered with the list of all modules.
            /// </summary>
            RootIndex,

            /// <summary>
            /// A module URL, answered with the list of that module's hosts.
            /// </summary>
            ModuleIndex,

            /// <summary>
            /// The module is not in the port map, answered with the root index and status 404.
            /// </summary>
            ModuleNotFound,

            /// <summary>
            /// The module exists but not the requested host, answered with the module page and status 404.
            /// </summary>
            HostNotFound,

            /// <summary>
            /// A host URL without the trailing slash, answered with a redirect to the slash-terminated URL.
            /// </summary>
            RedirectToSlash,

            /// <summary>
            /// A request below a host route, forwarded to the module backend.
            /// </summary>
            Proxy
        }
    }

    /// <summary>
    /// Describes one ASLM API mirror host displayed in the UI.
    /// </summary>
    /// <param name="ModuleId">Id of the module that owns the host.</param>
    /// <param name="HostKey">Key of the host in the module's port map.</param>
    /// <param name="Port">Backend port the module host listens on.</param>
    /// <param name="MirrorUrl">Browser URL of the host below the mirror server root.</param>
    /// <param name="TargetUrl">Direct loopback URL of the module host.</param>
    /// <param name="IsOnline">Whether the backend port accepted a connection, or null when it was not probed.</param>
    public record AslmMirrorHostInfo(
        string ModuleId,
        string HostKey,
        int Port,
        string MirrorUrl,
        string TargetUrl,
        bool? IsOnline);
}
