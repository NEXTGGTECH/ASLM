// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Allocates and persists per-module ports inside the configured application ranges.
    /// </summary>
    public class PortRegistry
    {
        // Port-map owner used by the internal ASLM API mirror server.
        public const string AslmMirrorServiceId = "__aslm-mirror";

        // Port key used by the internal ASLM API mirror server.
        public const string AslmMirrorPortKey = "mirror-port";

        private const int MaxPort = 65535;

        // Port-map owner used by the internal module interop JSON server.
        public const string AslmModuleInteropServiceId = "__aslm-module-interop";

        // Port key used by the internal module interop server.
        public const string AslmModuleInteropPortKey = "server-port";

        private readonly AppDataStore _appData;
        private readonly string _portMapPath;
        private readonly object _lock = new();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // moduleId -> portName -> assigned port
        private Dictionary<string, Dictionary<string, int>> _portMap = [];
        private bool _loaded;
        private DateTimeOffset _lastOrphanCleanupUtc = DateTimeOffset.MinValue;

        /// <summary>
        /// Raised after runtime port assignments are redistributed and persisted.
        /// </summary>
        public event EventHandler? PortsRedistributed;

        // Initialization

        /// <summary>
        /// Creates the port manager.
        /// </summary>
        public PortRegistry(AppDataStore appData)
        {
            _appData = appData;

            _portMapPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Ports.json");
        }


        // Port access

        /// <summary>
        /// Returns all ports assigned to a module and allocates missing ones when needed.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetOrAssignPorts(ModuleConfig module)
        {
            lock (_lock)
            {
                EnsureLoaded();

                CleanupOrphanedPortsIfDue();

                var portSettings = module.Settings?
                    .Where(setting => setting.Type.Equals("port", StringComparison.OrdinalIgnoreCase))
                    .Select(setting => setting.Key)
                    .ToList() ?? [];

                if (portSettings.Count == 0)
                {
                    portSettings.Add("http");
                }

                if (!_portMap.TryGetValue(module.Id, out var existing))
                {
                    existing = new Dictionary<string, int>();
                    _portMap[module.Id] = existing;
                }

                var changed = false;

                // Remove assignments that no longer correspond to current port settings.
                var keysToRemove = existing.Keys.Except(portSettings).ToList();
                foreach (var key in keysToRemove)
                {
                    existing.Remove(key);
                    changed = true;
                }

                // Allocate any missing or invalid ports inside the active range.
                foreach (var key in portSettings)
                {
                    if (existing.TryGetValue(key, out var port) && IsPortValid(module.Id, port))
                    {
                        continue;
                    }

                    existing[key] = AllocatePort(module.Id);
                    changed = true;
                }

                if (changed)
                {
                    SavePortMap();
                }

                return existing;
            }
        }

        /// <summary>
        /// Verifies all assigned ports for an owner are free on the system.
        /// Reallocates any that are in use and persists the updated map.
        /// Returns true when any port was changed.
        /// </summary>
        public bool EnsurePortsAvailable(string ownerId)
        {
            lock (_lock)
            {
                EnsureLoaded();

                if (!_portMap.TryGetValue(ownerId, out var existing) || existing.Count == 0)
                {
                    return false;
                }

                var changed = false;
                var usedPorts = new HashSet<int>(_portMap.Values.SelectMany(value => value.Values));

                foreach (var portKey in existing.Keys.ToList())
                {
                    var port = existing[portKey];
                    if (port > 0 && IsPortFreeOnSystem(port))
                    {
                        continue;
                    }

                    usedPorts.Remove(port);
                    existing[portKey] = AllocatePort(ownerId, usedPorts);
                    changed = true;
                }

                if (changed)
                {
                    SavePortMap();
                }

                return changed;
            }
        }

        /// <summary>
        /// Returns a shared-pool port for an internal ASLM service and allocates it when needed.
        /// </summary>
        public int GetOrAssignInternalServicePort(string serviceId, string portKey)
        {
            lock (_lock)
            {
                EnsureLoaded();

                if (!_portMap.TryGetValue(serviceId, out var existing))
                {
                    existing = new Dictionary<string, int>();
                    _portMap[serviceId] = existing;
                }

                if (existing.TryGetValue(portKey, out var port) && IsPortValid(serviceId, port))
                {
                    return port;
                }

                // Internal ASLM services use the same shared range and still reserve the port
                // in the runtime port map so modules cannot receive it later.
                port = AllocatePort(serviceId);
                existing[portKey] = port;
                SavePortMap();
                return port;
            }
        }

        /// <summary>
        /// Returns an existing internal service port without allocating a new one.
        /// </summary>
        public int? TryGetInternalServicePort(string serviceId, string portKey)
        {
            lock (_lock)
            {
                EnsureLoaded();

                return _portMap.TryGetValue(serviceId, out var existing) &&
                       existing.TryGetValue(portKey, out var port)
                    ? port
                    : null;
            }
        }

        /// <summary>
        /// Rebuilds persisted port assignments for modules and internal listeners.
        /// When <paramref name="reserveAslmMirrorServer"/> is true, reserves the ASLM mirror server port; always reserves the module interop listener port.
        /// </summary>
        public bool RedistributePorts(bool reserveAslmMirrorServer)
        {
            var changed = false;

            lock (_lock)
            {
                EnsureLoaded();

                if (reserveAslmMirrorServer)
                {
                    if (!_portMap.TryGetValue(AslmMirrorServiceId, out var apiPorts))
                    {
                        apiPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        _portMap[AslmMirrorServiceId] = apiPorts;
                    }

                    apiPorts.TryAdd(AslmMirrorPortKey, 0);
                }
                else
                {
                    _portMap.Remove(AslmMirrorServiceId);
                }

                if (!_portMap.TryGetValue(AslmModuleInteropServiceId, out var interopPorts))
                {
                    interopPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _portMap[AslmModuleInteropServiceId] = interopPorts;
                }

                interopPorts.TryAdd(AslmModuleInteropPortKey, 0);

                var nextMap = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                var usedPorts = new HashSet<int>();

                foreach (var owner in GetRedistributionOwners(reserveAslmMirrorServer))
                {
                    if (!nextMap.TryGetValue(owner.Id, out var ports))
                    {
                        ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        nextMap[owner.Id] = ports;
                    }

                    foreach (var portKey in owner.PortKeys)
                    {
                        ports[portKey] = AllocatePort(owner.Id, usedPorts);
                    }
                }

                if (PortMapsEqual(_portMap, nextMap))
                {
                    return false;
                }

                _portMap = nextMap;
                SavePortMap();
                changed = true;
            }

            PortsRedistributed?.Invoke(this, EventArgs.Empty);
            return changed;
        }

        // Single port

        /// <summary>
        /// Returns one assigned port for a module when it already exists.
        /// </summary>
        public int? TryGetPort(string moduleId, string portKey = "port")
        {
            lock (_lock)
            {
                EnsureLoaded();

                if (_portMap.TryGetValue(moduleId, out var ports))
                {
                    if (ports.TryGetValue(portKey, out var port))
                    {
                        return port;
                    }

                    if (ports.ContainsKey("http"))
                    {
                        return ports["http"];
                    }

                    if (ports.Count > 0)
                    {
                        return ports.Values.First();
                    }
                }

                return null;
            }
        }

        // Read-only port access

        /// <summary>
        /// Returns a read-only copy of all assigned port keys for one owner, or <c>null</c> when no assignment exists.
        /// Does not allocate new ports.
        /// </summary>
        public IReadOnlyDictionary<string, int>? TryGetAssignedPorts(string ownerId)
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _portMap.TryGetValue(ownerId, out var existing)
                    ? new Dictionary<string, int>(existing)
                    : null;
            }
        }

        /// <summary>
        /// Returns the loopback root URL for the primary WebView port of a module, or <c>null</c> when no
        /// assignment exists. Does not allocate new ports.
        /// </summary>
        public string? TryGetModulePageUrl(ModuleConfig module)
        {
            var ports = TryGetAssignedPorts(module.Id);
            if (ports == null || ports.Count == 0)
                return null;

            var portKey = ResolveModulePagePortKey(module);
            if (!ports.TryGetValue(portKey, out var port) || port <= 0)
            {
                port = ports.Values.FirstOrDefault(v => v > 0);
            }

            return port > 0 ? BuildLoopbackUrl(port) : null;
        }

        /// <summary>
        /// Builds a loopback root URL for the given port number.
        /// </summary>
        public static string BuildLoopbackUrl(int port) => $"http://127.0.0.1:{port}/";

        /// <summary>
        /// Converts a port-map host key to the URL route segment used by the ASLM API mirror.
        /// Strips known suffixes (<c>-port</c>, <c>_port</c>, <c> port</c>) from the key.
        /// </summary>
        public static string BuildHostRouteKey(string hostKey)
        {
            var value = (hostKey ?? string.Empty).Trim();
            foreach (var suffix in new[] { "-port", "_port", " port" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    value.Length > suffix.Length)
                {
                    return value[..^suffix.Length];
                }
            }

            return value;
        }


        // Module URL

        /// <summary>
        /// Returns the port-map key that should back a module WebView page.
        /// Prefers explicit UI listen ports over legacy <c>port</c>/<c>http</c> keys.
        /// </summary>
        public static string ResolveModulePagePortKey(ModuleConfig module)
        {
            var portKeys = module.Settings?
                .Where(setting => setting.Type.Equals("port", StringComparison.OrdinalIgnoreCase))
                .Select(setting => setting.Key)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToList() ?? [];

            if (portKeys.Count == 0)
            {
                return "http";
            }

            foreach (var preferred in new[] { "ui-port", "http", "port" })
            {
                var match = portKeys.FirstOrDefault(key =>
                    key.Equals(preferred, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            return portKeys[0];
        }

        /// <summary>
        /// Returns the base HTTP URL for a module page.
        /// </summary>
        public string GetModuleUrl(ModuleConfig module)
        {
            var ports = GetOrAssignPorts(module);
            var portKey = ResolveModulePagePortKey(module);

            if (!ports.TryGetValue(portKey, out var port) || port <= 0)
            {
                port = ports.Values.FirstOrDefault(value => value > 0);
            }

            if (port <= 0)
            {
                throw new InvalidOperationException($"No port assigned for module '{module.Id}'.");
            }

            return $"http://127.0.0.1:{port}/";
        }

        // Module cleanup

        /// <summary>
        /// Removes all port assignments for one module.
        /// </summary>
        public void RemoveModulePorts(string moduleId)
        {
            lock (_lock)
            {
                EnsureLoaded();

                if (_portMap.Remove(moduleId))
                {
                    SavePortMap();
                }
            }
        }

        // Orphan cleanup

        /// <summary>
        /// Removes assignments for modules that are no longer present.
        /// </summary>
        public void CleanupOrphanedPorts(IEnumerable<string> activeModuleIds)
        {
            lock (_lock)
            {
                EnsureLoaded();

                var activeSet = new HashSet<string>(activeModuleIds, StringComparer.OrdinalIgnoreCase);
                var orphanedIds = _portMap.Keys
                    .Where(id => !activeSet.Contains(id) && !IsInternalServiceId(id))
                    .ToList();

                if (orphanedIds.Count == 0)
                {
                    return;
                }

                foreach (var id in orphanedIds)
                {
                    _portMap.Remove(id);
                }

                SavePortMap();
            }
        }

        /// <summary>
        /// Periodically removes stale assignments without scanning Modules on every port lookup.
        /// </summary>
        private void CleanupOrphanedPortsIfDue()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastOrphanCleanupUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }

            _lastOrphanCleanupUtc = now;

            var modulesRoot = Path.Combine(AppRoot.Directory, "Modules");

            if (!Directory.Exists(modulesRoot))
            {
                return;
            }

            CleanupOrphanedPorts(DiscoverInstalledModuleIds(modulesRoot));
        }

        /// <summary>
        /// Reads stable module ids from installed manifests under <paramref name="modulesRoot"/>.
        /// </summary>
        private static IEnumerable<string> DiscoverInstalledModuleIds(string modulesRoot)
        {
            foreach (var jsonFile in ModuleManifestDiscovery.EnumerateInstalledManifests(modulesRoot))
            {
                string? moduleId = null;
                try
                {
                    using var stream = File.OpenRead(jsonFile);
                    using var document = JsonDocument.Parse(stream);
                    if (document.RootElement.TryGetProperty("id", out var idProperty))
                    {
                        moduleId = idProperty.GetString()?.Trim();
                    }
                }
                catch
                {
                    // Skip unreadable manifests during best-effort orphan cleanup.
                }

                if (!string.IsNullOrWhiteSpace(moduleId))
                {
                    yield return moduleId;
                }
            }
        }


        // Port validation

        /// <summary>
        /// Checks whether an assigned port is still valid for the current start port.
        /// </summary>
        private bool IsPortValid(string ownerId, int port)
        {
            var start = _appData.Data.Ports.ModulesStart;

            if (port >= start && port <= MaxPort)
            {
                return true;
            }

            // Keep older fallback assignments valid to avoid unnecessary churn.
            return port >= 40000 && port < 50000;
        }

        // Port allocation

        /// <summary>
        /// Allocates the next available port for a module.
        /// </summary>
        private int AllocatePort(string ownerId)
        {
            var start = _appData.Data.Ports.ModulesStart;
            var usedPorts = new HashSet<int>(_portMap.Values.SelectMany(value => value.Values));

            return AllocatePort(ownerId, usedPorts, start);
        }

        /// <summary>
        /// Allocates the next available port for a module using an explicit used-port set.
        /// </summary>
        private int AllocatePort(string ownerId, HashSet<int> usedPorts)
        {
            var start = _appData.Data.Ports.ModulesStart;
            return AllocatePort(ownerId, usedPorts, start);
        }

        /// <summary>
        /// Allocates the next available port from the configured start or deterministic fallback.
        /// </summary>
        private static int AllocatePort(string ownerId, HashSet<int> usedPorts, int start)
        {
            for (var candidate = start; candidate <= MaxPort; candidate++)
            {
                if (!usedPorts.Contains(candidate))
                {
                    usedPorts.Add(candidate);
                    return candidate;
                }
            }

            const int fallbackStart = 40000;
            const int fallbackCount = 10000;
            var seed = (int)(uint)ownerId.GetHashCode();

            for (var offset = 0; offset < fallbackCount; offset++)
            {
                var candidate = fallbackStart + ((seed + offset) % fallbackCount);
                if (!usedPorts.Contains(candidate))
                {
                    usedPorts.Add(candidate);
                    return candidate;
                }
            }

            throw new InvalidOperationException("No ports are available in the configured or fallback ranges.");
        }

        /// <summary>
        /// Checks whether a TCP port can be bound on loopback.
        /// </summary>
        private static bool IsPortFreeOnSystem(int port)
        {
            if (port <= 0 || port > MaxPort)
            {
                return false;
            }

            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns the owners and port keys in stable redistribution order.
        /// </summary>
        private List<RedistributionOwner> GetRedistributionOwners(bool reserveAslmMirrorServer)
        {
            return _portMap
                .Where(pair =>
                    reserveAslmMirrorServer || !pair.Key.Equals(AslmMirrorServiceId, StringComparison.OrdinalIgnoreCase))
                .Where(static pair => pair.Value.Count > 0)
                .Select(pair => new RedistributionOwner(
                    pair.Key,
                    pair.Value
                        .OrderBy(static port => port.Value)
                        .ThenBy(static port => port.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(static port => port.Key)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    pair.Value.Values.DefaultIfEmpty(int.MaxValue).Min()))
                .OrderBy(static owner => GetRedistributionOwnerRank(owner.Id))
                .ThenBy(static owner => owner.FirstPort)
                .ThenBy(static owner => owner.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Sorts internal services before module owners during redistribution.
        /// </summary>
        private static int GetRedistributionOwnerRank(string ownerId)
        {
            if (ownerId.Equals(AslmMirrorServiceId, StringComparison.OrdinalIgnoreCase) ||
                ownerId.Equals(AslmModuleInteropServiceId, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (IsInternalServiceId(ownerId))
            {
                return 1;
            }

            return 2;
        }

        /// <summary>
        /// Compares two port maps without relying on dictionary insertion order.
        /// </summary>
        private static bool PortMapsEqual(
            Dictionary<string, Dictionary<string, int>> left,
            Dictionary<string, Dictionary<string, int>> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var owner in left)
            {
                if (!right.TryGetValue(owner.Key, out var rightPorts) ||
                    owner.Value.Count != rightPorts.Count)
                {
                    return false;
                }

                foreach (var port in owner.Value)
                {
                    if (!rightPorts.TryGetValue(port.Key, out var rightPort) ||
                        rightPort != port.Value)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Returns whether a port map owner is managed by ASLM itself rather than by an installed module folder.
        /// </summary>
        private static bool IsInternalServiceId(string ownerId)
        {
            return ownerId.StartsWith("__", StringComparison.Ordinal);
        }

        /// <summary>
        /// Stores one owner and its port keys while rebuilding the runtime map.
        /// </summary>
        private sealed record RedistributionOwner(string Id, List<string> PortKeys, int FirstPort);

        // Lazy load

        /// <summary>
        /// Loads the persisted port map once on first access.
        /// </summary>
        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            if (!File.Exists(_portMapPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_portMapPath);

                try
                {
                    _portMap = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json, _jsonOptions) ?? [];
                }
                catch
                {
                    // Migrate the old flat schema into the current per-port-key format.
                    var oldMap = JsonSerializer.Deserialize<Dictionary<string, int>>(json, _jsonOptions);
                    _portMap = oldMap?.ToDictionary(
                        pair => pair.Key,
                        pair => new Dictionary<string, int> { { "port", pair.Value } }) ?? [];
                }
            }
            catch
            {
                _portMap = [];
            }
        }

        // Persistence

        /// <summary>
        /// Saves the current port map to disk.
        /// </summary>
        private void SavePortMap()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_portMapPath)!);

            var json = JsonSerializer.Serialize(_portMap, _jsonOptions);
            File.WriteAllText(_portMapPath, json);
        }
    }
}
