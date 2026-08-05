// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.API
{
    /// <summary>
    /// Hosts a local JSON HTTP API that exposes module registry data and coordinates module starts.
    /// </summary>
    public sealed class AslmModuleInteropServer : IDisposable
    {
        private readonly AppDataStore _appData;
        private readonly PortRegistry _ports;
        private readonly AslmMirrorServer _mirrorServer;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleRunner _moduleRunner;
        private readonly ModuleLaunchCoordinator _launchCoordinator;
        private readonly ModuleInteropHostState _interopHostState;
        private readonly ILogger<AslmModuleInteropServer> _logger;

        private readonly object _stateLock = new();
        private readonly JsonSerializerOptions _jsonReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly JsonSerializerOptions _jsonWriteOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

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
        /// Creates the module interop server.
        /// </summary>
        public AslmModuleInteropServer(
            AppDataStore appData,
            PortRegistry ports,
            AslmMirrorServer mirrorServer,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            ModuleLaunchCoordinator launchCoordinator,
            ModuleInteropHostState interopHostState,
            ILogger<AslmModuleInteropServer> logger)
        {
            _appData = appData;
            _ports = ports;
            _mirrorServer = mirrorServer;
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
            _launchCoordinator = launchCoordinator;
            _interopHostState = interopHostState;
            _logger = logger;
        }


        // State access

        /// <summary>
        /// Gets whether the JSON server is currently listening.
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
        /// Gets the localhost port reserved for the interop server.
        /// </summary>
        public int Port
        {
            get
            {
                lock (_stateLock)
                {
                    return _activePort ??
                           _ports.TryGetInternalServicePort(
                               PortRegistry.AslmModuleInteropServiceId,
                               PortRegistry.AslmModuleInteropPortKey) ??
                           _appData.Data.Ports.ModulesStart;
                }
            }
        }

        /// <summary>
        /// Gets the root URL clients should use for interop requests.
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
        /// Reserves the interop port in the shared map and starts the JSON listener. This host service is always enabled.
        /// </summary>
        public async Task EnsureStartedAsync()
        {
            _ports.RedistributePorts(_appData.Data.Api.ServerEnabled);
            await StartAsync();
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
                    _interopHostState.SetListening(BuildBaseUrl(assignedPort), assignedPort);
                    _listenerTask = Task.Run(() => RunListenerLoopAsync(_listener, _listenerCts.Token));
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.LogError(ex, "Failed to start module interop server.");
                    _interopHostState.Clear();
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
                    _logger.LogDebug(ex, "Module interop listener stop failed.");
                }

                listenerTask = _listenerTask;
                CleanupListener();
            }

            _interopHostState.Clear();

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

                _ = Task.Run(() => HandleContextAsync(context), ct);
            }
        }

        /// <summary>
        /// Dispatches one interop request to a registry or module-start handler.
        /// </summary>
        private async Task HandleContextAsync(HttpListenerContext context)
        {
            try
            {
                // Only loopback clients may call the interop API.
                if (!IsLoopback(context.Request))
                {
                    await WriteJsonAsync(context, 403, new ErrorEnvelope("forbidden", "Only loopback clients are allowed."), CancellationToken.None);
                    return;
                }

                // Dispatch known GET and POST routes.
                var path = NormalizePath(context.Request.Url?.AbsolutePath ?? "/");
                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/v1/registry", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRegistryGetAsync(context);
                    return;
                }

                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/v1/ports", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePortsGetAsync(context);
                    return;
                }

                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/v1/modules/start", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleModulesStartPostAsync(context);
                    return;
                }

                await WriteJsonAsync(context, 404, new ErrorEnvelope("not_found", "No route matches this URL."), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Module interop request failed.");
                await WriteJsonAsync(context, 500, new ErrorEnvelope("error", ex.Message), CancellationToken.None);
            }
        }


        // Registry endpoint

        /// <summary>
        /// Returns installed and running module snapshots for GET /v1/registry.
        /// Also includes ASLM API state and per-module port/host information for running modules.
        /// </summary>
        private async Task HandleRegistryGetAsync(HttpListenerContext context)
        {
            var installed = await BuildInstalledModulesAsync();
            var aslmApi = BuildAslmApiResponseDto();
            var running = BuildRunningModulesResponseDtos();

            var payload = new RegistryResponse(
                BaseUrl.TrimEnd('/') + "/",
                aslmApi,
                installed,
                running);

            await WriteJsonAsync(context, 200, payload, CancellationToken.None);
        }

        // Ports endpoint

        /// <summary>
        /// Returns ASLM API state and per-module port/host information for GET /v1/ports.
        /// Omits the installed-modules list for a lighter-weight response.
        /// </summary>
        private async Task HandlePortsGetAsync(HttpListenerContext context)
        {
            var aslmApi = BuildAslmApiResponseDto();
            var running = BuildRunningModulesResponseDtos();

            var payload = new PortsResponse(aslmApi, running);

            await WriteJsonAsync(context, 200, payload, CancellationToken.None);
        }

        // Ports / host builder helpers

        /// <summary>
        /// Builds the ASLM API status block for interop responses.
        /// </summary>
        private AslmApiResponseDto BuildAslmApiResponseDto()
        {
            var apiEnabled = _appData.Data.Api.ServerEnabled;
            var apiPort = _ports.TryGetInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey);
            var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled, apiPort, _mirrorServer.IsRunning);
            return new AslmApiResponseDto(dto.Enabled, dto.Running, dto.Port, dto.BaseUrl);
        }

        /// <summary>
        /// Builds the running-modules list with port and host data.
        /// </summary>
        private List<RunningModuleDto> BuildRunningModulesResponseDtos()
        {
            var apiEnabled = _appData.Data.Api.ServerEnabled;
            var apiPort = _ports.TryGetInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey);
            var mirrorBase = ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled, apiPort);

            return _moduleRunner.GetRunningModuleConfigs()
                .Select(module =>
                {
                    var portDto = ModuleInteropPortsBuilder.BuildRunningModulePorts(module, _ports, mirrorBase);
                    var hosts = portDto.Hosts
                        .Select(static h => new ModuleHostDto(h.HostKey, h.RouteKey, h.Port, h.TargetUrl, h.MirrorUrl))
                        .ToList();
                    return new RunningModuleDto(
                        portDto.Id,
                        portDto.Name,
                        portDto.InstanceFolder,
                        portDto.SourcePath,
                        portDto.PageUrl,
                        hosts);
                })
                .ToList();
        }

        /// <summary>
        /// Builds the installed-module list grouped by stable module id.
        /// </summary>
        private async Task<List<InstalledModuleDto>> BuildInstalledModulesAsync()
        {
            var modules = await _moduleInstaller.DiscoverModulesAsync();
            var byId = modules
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Group installed manifests by id and emit one row per instance.
            var list = new List<InstalledModuleDto>();
            foreach (var group in byId.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = group.OrderBy(m => m.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();
                var hasMultiple = ordered.Count > 1;
                foreach (var module in ordered)
                {
                    list.Add(new InstalledModuleDto(
                        module.Id,
                        module.Name,
                        module.Version,
                        module.Status.Installed,
                        module.Status.Enabled,
                        module.Status.FirstRunCompleted,
                        module.Commands.Run.Count > 0,
                        hasMultiple,
                        Path.GetFileName(Path.GetDirectoryName(module.SourcePath)) ?? string.Empty));
                }
            }

            return list;
        }


        // Module start endpoint

        /// <summary>
        /// Starts or ensures running state for modules requested by POST /v1/modules/start.
        /// </summary>
        private async Task HandleModulesStartPostAsync(HttpListenerContext context)
        {
            // Require a JSON request body.
            if (!context.Request.HasEntityBody)
            {
                await WriteJsonAsync(context, 400, new ErrorEnvelope("bad_request", "JSON body is required."), CancellationToken.None);
                return;
            }

            // Deserialize the POST body.
            StartModulesRequest? body;
            try
            {
                await using var ms = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(ms);
                body = JsonSerializer.Deserialize<StartModulesRequest>(ms.ToArray(), _jsonReadOptions);
            }
            catch (Exception ex)
            {
                await WriteJsonAsync(context, 400, new ErrorEnvelope("bad_request", $"Invalid JSON: {ex.Message}"), CancellationToken.None);
                return;
            }

            // Validate caller and target module ids.
            if (body == null || string.IsNullOrWhiteSpace(body.CallerModuleId))
            {
                await WriteJsonAsync(context, 400, new ErrorEnvelope("bad_request", "callerModuleId is required."), CancellationToken.None);
                return;
            }

            if (body.ModuleIds == null || body.ModuleIds.Count == 0)
            {
                await WriteJsonAsync(context, 400, new ErrorEnvelope("bad_request", "moduleIds must contain at least one id."), CancellationToken.None);
                return;
            }

            // The caller module must already be running.
            var callerId = body.CallerModuleId.Trim();
            var running = _moduleRunner.GetRunningModulesSnapshot();
            if (!running.Any(m => string.Equals(m.Id, callerId, StringComparison.OrdinalIgnoreCase)))
            {
                await WriteJsonAsync(
                    context,
                    403,
                    new ErrorEnvelope("caller_not_running", "callerModuleId must match a module that is currently running."),
                    CancellationToken.None);
                return;
            }

            // Launch or ensure each requested module is running.
            var results = new List<StartModuleItemResult>();
            foreach (var rawId in body.ModuleIds)
            {
                if (string.IsNullOrWhiteSpace(rawId))
                {
                    results.Add(new StartModuleItemResult(string.Empty, "error", "Empty module id."));
                    continue;
                }

                var id = rawId.Trim();
                var log = new Progress<string>(message => _logger.LogDebug("Interop launch {ModuleId}: {Message}", id, message));
                var launch = await _launchCoordinator.LaunchOrEnsureRunningAsync(id, log, CancellationToken.None);
                var (status, message) = MapLaunchStatus(launch.Status, launch.Message);
                results.Add(new StartModuleItemResult(id, status, message));
            }

            await WriteJsonAsync(context, 200, new StartModulesResponse(results), CancellationToken.None);
        }

        /// <summary>
        /// Maps a coordinator launch status to the interop JSON status string.
        /// </summary>
        private static (string status, string? message) MapLaunchStatus(ModuleLaunchStatus status, string? message)
        {
            return status switch
            {
                ModuleLaunchStatus.Started => ("started", null),
                ModuleLaunchStatus.AlreadyRunning => ("alreadyRunning", null),
                ModuleLaunchStatus.NotFound => ("notFound", message),
                ModuleLaunchStatus.NoRunCommands => ("noRunCommands", message),
                ModuleLaunchStatus.FirstRunFailed => ("firstRunFailed", message),
                ModuleLaunchStatus.Error => ("error", message),
                _ => ("error", message ?? "Unknown launch status.")
            };
        }


        // Shared helpers

        /// <summary>
        /// Returns whether the request originated from the loopback interface.
        /// </summary>
        private static bool IsLoopback(HttpListenerRequest request)
        {
            var remote = request.RemoteEndPoint?.Address;
            return remote != null && IPAddress.IsLoopback(remote);
        }

        /// <summary>
        /// Normalizes an absolute path for route comparison.
        /// </summary>
        private static string NormalizePath(string path)
        {
            var trimmed = path.Trim();
            if (trimmed.Length > 1 && trimmed.EndsWith('/'))
            {
                trimmed = trimmed.TrimEnd('/');
            }

            return trimmed.Length == 0 ? "/" : trimmed;
        }

        /// <summary>
        /// Writes a JSON response and closes the listener context.
        /// </summary>
        private async Task WriteJsonAsync<T>(HttpListenerContext context, int statusCode, T payload, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(payload, _jsonWriteOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        }

        /// <summary>
        /// Notifies subscribers that listener state changed.
        /// </summary>
        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Clears listener fields after stop or failed start.
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
        /// Reserves or returns the interop port from the shared port map.
        /// </summary>
        private int GetAssignedPort()
        {
            _ports.GetOrAssignInternalServicePort(
                PortRegistry.AslmModuleInteropServiceId,
                PortRegistry.AslmModuleInteropPortKey);
            _ports.EnsurePortsAvailable(PortRegistry.AslmModuleInteropServiceId);
            return _ports.GetOrAssignInternalServicePort(
                PortRegistry.AslmModuleInteropServiceId,
                PortRegistry.AslmModuleInteropPortKey);
        }

        /// <summary>
        /// Builds the loopback root URL for one assigned port.
        /// </summary>
        private static string BuildBaseUrl(int port) => $"http://127.0.0.1:{port}/";


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


        // JSON DTOs

        /// <summary>
        /// Standard error envelope returned by the interop API.
        /// </summary>
        private sealed record ErrorEnvelope(string Code, string Message);

        /// <summary>
        /// Registry payload for GET /v1/registry.
        /// </summary>
        private sealed record RegistryResponse(
            string InteropBaseUrl,
            AslmApiResponseDto AslmApi,
            List<InstalledModuleDto> InstalledModules,
            List<RunningModuleDto> RunningModules);

        /// <summary>
        /// Ports-only payload for GET /v1/ports.
        /// </summary>
        private sealed record PortsResponse(
            AslmApiResponseDto AslmApi,
            List<RunningModuleDto> RunningModules);

        /// <summary>
        /// ASLM API mirror server state included in interop responses.
        /// </summary>
        private sealed record AslmApiResponseDto(
            bool Enabled,
            bool? Running,
            int? Port,
            string? BaseUrl);

        /// <summary>
        /// One installed module instance exposed in the registry response.
        /// </summary>
        private sealed record InstalledModuleDto(
            string Id,
            string Name,
            string Version,
            bool Installed,
            bool Enabled,
            bool FirstRunCompleted,
            bool HasRunCommands,
            bool HasMultipleInstances,
            string InstanceFolder);

        /// <summary>
        /// One running module instance with port and host information.
        /// </summary>
        private sealed record RunningModuleDto(
            string Id,
            string Name,
            string InstanceFolder,
            string SourcePath,
            string? PageUrl,
            List<ModuleHostDto> Hosts);

        /// <summary>
        /// One port-map host entry for a running module.
        /// </summary>
        private sealed record ModuleHostDto(
            string HostKey,
            string RouteKey,
            int Port,
            string TargetUrl,
            string? MirrorUrl);

        /// <summary>
        /// Request body for POST /v1/modules/start.
        /// </summary>
        private sealed record StartModulesRequest(string? CallerModuleId, List<string>? ModuleIds);

        /// <summary>
        /// Per-module result returned by POST /v1/modules/start.
        /// </summary>
        private sealed record StartModuleItemResult(string ModuleId, string Status, string? Message);

        /// <summary>
        /// Response body for POST /v1/modules/start.
        /// </summary>
        private sealed record StartModulesResponse(List<StartModuleItemResult> Results);
    }
}
