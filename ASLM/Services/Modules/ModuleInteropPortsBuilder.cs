// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Builds the ports/hosts payload for the module interop API responses.
    /// Extracted for testability; all methods are pure and do not allocate new ports.
    /// </summary>
    internal static class ModuleInteropPortsBuilder
    {
        // Response blocks

        /// <summary>
        /// Builds the ASLM API block for interop responses.
        /// When the ASLM API server is disabled, only <c>Enabled</c> is set (to false) and all other values are null.
        /// </summary>
        internal static AslmApiInfoDto BuildAslmApiDto(
            bool apiEnabled,
            int? apiPort,
            bool apiRunning)
        {
            if (!apiEnabled)
                return new AslmApiInfoDto(Enabled: false, Running: null, Port: null, BaseUrl: null);

            return new AslmApiInfoDto(
                Enabled: true,
                Running: apiRunning,
                Port: apiPort,
                BaseUrl: apiPort.HasValue ? PortRegistry.BuildLoopbackUrl(apiPort.Value) : null);
        }

        /// <summary>
        /// Builds the list of host entries for one running module, ordered by port and then by key.
        /// Entries without a positive port are skipped; the mirror URL is set only when a mirror base URL is given.
        /// </summary>
        internal static List<ModuleHostDto> BuildHosts(
            ModuleConfig module,
            IReadOnlyDictionary<string, int>? assignedPorts,
            string? apiMirrorBaseUrl)
        {
            if (assignedPorts == null || assignedPorts.Count == 0)
                return [];

            // One host per assigned port: its direct loopback URL and its path below the API mirror.
            var hosts = new List<ModuleHostDto>();
            foreach (var (hostKey, port) in assignedPorts.OrderBy(static p => p.Value).ThenBy(static p => p.Key))
            {
                if (port <= 0)
                    continue;

                var routeKey = PortRegistry.BuildHostRouteKey(hostKey);
                var targetUrl = PortRegistry.BuildLoopbackUrl(port);
                string? mirrorUrl = apiMirrorBaseUrl != null
                    ? $"{apiMirrorBaseUrl.TrimEnd('/')}/{module.Id}/{routeKey}/"
                    : null;

                hosts.Add(new ModuleHostDto(hostKey, routeKey, port, targetUrl, mirrorUrl));
            }

            return hosts;
        }

        /// <summary>
        /// Builds a running-module ports entry for one module config from the ports already assigned to it.
        /// The instance folder is the name of the directory that holds the module manifest.
        /// </summary>
        internal static RunningModulePortsDto BuildRunningModulePorts(
            ModuleConfig module,
            PortRegistry portRegistry,
            string? apiMirrorBaseUrl)
        {
            var assignedPorts = portRegistry.TryGetAssignedPorts(module.Id);
            var pageUrl = portRegistry.TryGetModulePageUrl(module);
            var hosts = BuildHosts(module, assignedPorts, apiMirrorBaseUrl);
            var instanceFolder = Path.GetFileName(Path.GetDirectoryName(module.SourcePath)) ?? string.Empty;

            return new RunningModulePortsDto(
                Id: module.Id,
                Name: module.Name,
                InstanceFolder: instanceFolder,
                SourcePath: module.SourcePath,
                PageUrl: pageUrl,
                Hosts: hosts);
        }

        /// <summary>
        /// Resolves the API mirror base URL to include in host <c>mirrorUrl</c> fields.
        /// Returns <c>null</c> when the ASLM API is disabled or has no port assigned.
        /// </summary>
        /// <param name="apiEnabled">Whether the ASLM API server is enabled.</param>
        /// <param name="apiPort">Port assigned to the ASLM API mirror server, if any.</param>
        internal static string? ResolveMirrorBaseUrl(bool apiEnabled, int? apiPort)
        {
            if (!apiEnabled || !apiPort.HasValue)
                return null;

            return PortRegistry.BuildLoopbackUrl(apiPort.Value);
        }
    }


    // Interop response DTOs

    /// <summary>
    /// ASLM API mirror server state for interop API responses.
    /// </summary>
    /// <param name="Enabled">Whether the ASLM API server is enabled.</param>
    /// <param name="Running">Whether the server is running, or null when it is disabled.</param>
    /// <param name="Port">Port assigned to the server, or null when disabled or unassigned.</param>
    /// <param name="BaseUrl">Loopback root URL of the server, or null when there is no port.</param>
    internal sealed record AslmApiInfoDto(
        bool Enabled,
        bool? Running,
        int? Port,
        string? BaseUrl);

    /// <summary>
    /// One host (port-map entry) of a running module.
    /// </summary>
    /// <param name="HostKey">Key of the port in the module's port map.</param>
    /// <param name="RouteKey">Host key reduced to the route segment used below the API mirror URL.</param>
    /// <param name="Port">Assigned TCP port.</param>
    /// <param name="TargetUrl">Direct loopback URL of the port.</param>
    /// <param name="MirrorUrl">URL of this host below the ASLM API mirror, or null when no mirror is available.</param>
    internal sealed record ModuleHostDto(
        string HostKey,
        string RouteKey,
        int Port,
        string TargetUrl,
        string? MirrorUrl);

    /// <summary>
    /// Ports and hosts for one running module instance.
    /// </summary>
    /// <param name="Id">Module id.</param>
    /// <param name="Name">Module display name.</param>
    /// <param name="InstanceFolder">Name of the folder that holds the module manifest.</param>
    /// <param name="SourcePath">Path of the module manifest.</param>
    /// <param name="PageUrl">Loopback URL of the module page, or null when no port is assigned.</param>
    /// <param name="Hosts">All hosts of the module.</param>
    internal sealed record RunningModulePortsDto(
        string Id,
        string Name,
        string InstanceFolder,
        string SourcePath,
        string? PageUrl,
        List<ModuleHostDto> Hosts);
}
