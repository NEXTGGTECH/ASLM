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
        /// <summary>
        /// Builds the ASLM API block for interop responses.
        /// Returns <c>null</c> when the ASLM API server is disabled.
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
        /// Builds the list of host entries for one running module.
        /// </summary>
        internal static List<ModuleHostDto> BuildHosts(
            ModuleConfig module,
            IReadOnlyDictionary<string, int>? assignedPorts,
            string? apiMirrorBaseUrl)
        {
            if (assignedPorts == null || assignedPorts.Count == 0)
                return [];

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
        /// Builds a running-module ports entry for one module config.
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
        internal static string? ResolveMirrorBaseUrl(bool apiEnabled, int? apiPort)
        {
            if (!apiEnabled || !apiPort.HasValue)
                return null;

            return PortRegistry.BuildLoopbackUrl(apiPort.Value);
        }
    }

    /// <summary>
    /// ASLM API mirror server state for interop API responses.
    /// </summary>
    internal sealed record AslmApiInfoDto(
        bool Enabled,
        bool? Running,
        int? Port,
        string? BaseUrl);

    /// <summary>
    /// One host (port-map entry) of a running module.
    /// </summary>
    internal sealed record ModuleHostDto(
        string HostKey,
        string RouteKey,
        int Port,
        string TargetUrl,
        string? MirrorUrl);

    /// <summary>
    /// Ports and hosts for one running module instance.
    /// </summary>
    internal sealed record RunningModulePortsDto(
        string Id,
        string Name,
        string InstanceFolder,
        string SourcePath,
        string? PageUrl,
        List<ModuleHostDto> Hosts);
}
