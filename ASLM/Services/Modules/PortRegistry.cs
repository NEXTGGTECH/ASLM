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
        // Fields and constants

        /// <summary>
        /// Port-map owner used by the internal ASLM API mirror server.
        /// </summary>
        public const string AslmMirrorServiceId = "__aslm-mirror";

        /// <summary>
        /// Port key used by the internal ASLM API mirror server.
        /// </summary>
        public const string AslmMirrorPortKey = "mirror-port";

        /// <summary>
        /// Highest valid TCP port number.
        /// </summary>
        private const int MaxPort = 65535;

        /// <summary>
        /// Port-map owner used by the internal module interop JSON server.
        /// </summary>
        public const string AslmModuleInteropServiceId = "__aslm-module-interop";

        /// <summary>
        /// Port key used by the internal module interop server.
        /// </summary>
        public const string AslmModuleInteropPortKey = "server-port";

        /// <summary>
        /// Application data store that supplies the configured start of the module port range.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Absolute path of the JSON file that persists the port map.
        /// </summary>
        private readonly string _portMapPath;

        /// <summary>
        /// Serializes every read and write of the port map and its persistence.
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        /// JSON settings used to read and write the port map file.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Assigned ports keyed by owner id (module or internal service), then by port key.
        /// </summary>
        private Dictionary<string, Dictionary<string, int>> _portMap = [];

        /// <summary>
        /// Whether the persisted port map has been read already; loading happens lazily on first access.
        /// </summary>
        private bool _loaded;

        /// <summary>
        /// Time of the last successful orphan cleanup, used to rate-limit the cleanup.
        /// </summary>
        private DateTimeOffset _lastOrphanCleanupUtc = DateTimeOffset.MinValue;

        /// <summary>
        /// Registry of installed modules; the source of truth for which port owners still exist.
        /// </summary>
        private readonly ModuleRegistry _modules = new();

        /// <summary>
        /// Module ids that reserved ports in this process before their installation reached the registry;
        /// the orphan cleanup keeps them.
        /// </summary>
        private readonly HashSet<string> _sessionPortOwners = new(StringComparer.OrdinalIgnoreCase);


        // Events

        /// <summary>
        /// Raised after runtime port assignments are redistributed and persisted.
        /// </summary>
        public event EventHandler? PortsRedistributed;


        // Initialization

        /// <summary>
        /// Creates the port manager that persists assignments to <c>Data/App/ASLM_Ports.json</c>.
        /// </summary>
        public PortRegistry(AppDataStore appData)
        {
            _appData = appData;

            _portMapPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Ports.json");
        }

        /// <summary>
        /// Creates the port manager bound to an explicit port-map file and registry (test isolation).
        /// </summary>
        internal PortRegistry(AppDataStore appData, string portMapPath, ModuleRegistry modules)
        {
            _appData = appData;
            _portMapPath = portMapPath;
            _modules = modules;
        }


        // Port access

        /// <summary>
        /// Returns all ports assigned to a module and allocates missing ones when needed.
        /// The returned dictionary is the live map entry, so callers must not modify it.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetOrAssignPorts(ModuleConfig module)
        {
            lock (_lock)
            {
                EnsureLoaded();

                // First-run commands can reserve ports before installation is committed to the registry.
                _sessionPortOwners.Add(module.Id);
                CleanupOrphanedPortsIfDue();

                // Collect the port keys the module declares; a module without port settings gets a single "http" port.
                var portSettings = module.Settings?
                    .Where(setting => setting.Type.Equals("port", StringComparison.OrdinalIgnoreCase))
                    .Select(setting => setting.Key)
                    .ToList() ?? [];

                if (portSettings.Count == 0)
                {
                    portSettings.Add("http");
                }

                // Find or create the module's entry in the port map.
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

                // Ports of all owners are treated as taken so a replacement never collides with another assignment.
                var changed = false;
                var usedPorts = new HashSet<int>(_portMap.Values.SelectMany(value => value.Values));

                // Probe every assigned port by binding it; a busy or invalid port is replaced by a fresh allocation.
                foreach (var portKey in existing.Keys.ToList())
                {
                    var port = existing[portKey];
                    if (port > 0 && IsPortFreeOnSystem(port))
                    {
                        continue;
                    }

                    // Drop the old assignment from the used set, then allocate a replacement for this key.
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
        /// Returns true when the map changed; <see cref="PortsRedistributed"/> is raised outside the lock in that case.
        /// </summary>
        public bool RedistributePorts(bool reserveAslmMirrorServer)
        {
            var changed = false;

            lock (_lock)
            {
                EnsureLoaded();

                // Startup must also retire assignments left by versions that retained removed catalog manifests.
                CleanupOrphanedPortsIfDue(force: true);

                // Make sure the internal listeners own a placeholder entry (port 0) so they take part in the
                // redistribution; the mirror server entry is dropped when it is not wanted.
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

                // Allocate every port from scratch in a stable owner order, so the result is compact and deterministic.
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

                // Persist and publish only when the new layout differs from the current one.
                if (PortMapsEqual(_portMap, nextMap))
                {
                    return false;
                }

                _portMap = nextMap;
                SavePortMap();
                changed = true;
            }

            // Notify listeners after the lock is released so handlers can call back into the registry.
            PortsRedistributed?.Invoke(this, EventArgs.Empty);
            return changed;
        }


        // Single port

        /// <summary>
        /// Returns one assigned port for a module when it already exists.
        /// Falls back to the <c>http</c> port and then to any assigned port when the requested key is missing.
        /// </summary>
        public int? TryGetPort(string moduleId, string portKey = "port")
        {
            lock (_lock)
            {
                EnsureLoaded();

                if (_portMap.TryGetValue(moduleId, out var ports))
                {
                    // Prefer the requested key, then the conventional "http" key, then whatever the module has.
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

            // Use the preferred page port, or the first positive port when that key is missing or unset.
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

            // Strip at most one suffix, and never reduce the key to an empty string.
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

            // Pick the first declared key matching the preference order; otherwise the first declared port key.
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
        /// Returns the base HTTP URL for a module page, allocating its ports first when none exist yet.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when no positive port can be determined.</exception>
        public string GetModuleUrl(ModuleConfig module)
        {
            var ports = GetOrAssignPorts(module);
            var portKey = ResolveModulePagePortKey(module);

            // Use the preferred page port, or the first positive port when that key is missing or unset.
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
                _sessionPortOwners.Remove(moduleId);

                if (_portMap.Remove(moduleId))
                {
                    SavePortMap();
                }
            }
        }


        // Orphan cleanup

        /// <summary>
        /// Removes assignments for modules that are no longer present.
        /// Internal service owners (ids starting with <c>__</c>) are never removed.
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
        /// Removes unregistered owners while preserving ports reserved by installations in this process.
        /// Runs at most every 30 seconds unless <paramref name="force"/> is set.
        /// </summary>
        private void CleanupOrphanedPortsIfDue(bool force = false)
        {
            // Skip the registry read when a cleanup ran recently.
            var now = DateTimeOffset.UtcNow;
            if (!force && now - _lastOrphanCleanupUtc < TimeSpan.FromSeconds(30))
            {
                return;
            }

            try
            {
                // Catalog manifests survive uninstall and do not establish installed ownership.
                var retained = _modules.ReadIds();
                retained.UnionWith(_sessionPortOwners);
                CleanupOrphanedPorts(retained);
                _lastOrphanCleanupUtc = now;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // An unreadable registry is not evidence that its module assignments are orphaned.
                System.Diagnostics.Debug.WriteLine($"[PortRegistry] Could not clean orphaned ports: {ex}");
            }
        }


        // Port validation

        /// <summary>
        /// Checks whether an assigned port is still valid for the current start port.
        /// A port is valid inside the configured module range or inside the legacy fallback range.
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
        /// Allocates the next available port from the configured start, or from the owner-dependent fallback range
        /// when the primary range is exhausted.
        /// The chosen port is added to <paramref name="usedPorts"/>.
        /// </summary>
        private static int AllocatePort(string ownerId, HashSet<int> usedPorts, int start)
        {
            // Primary range: the lowest port at or above the configured start that no owner has taken.
            for (var candidate = start; candidate <= MaxPort; candidate++)
            {
                if (!usedPorts.Contains(candidate))
                {
                    usedPorts.Add(candidate);
                    return candidate;
                }
            }

            // Fallback range 40000-49999: probe from an offset derived from the owner id hash, wrapping around
            // inside the range, and take the first free port. Used only when the primary range is exhausted.
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

            // A short-lived listener is the most direct test: if binding fails, another process owns the port.
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


        // Redistribution helpers

        /// <summary>
        /// Returns the owners and port keys in stable redistribution order.
        /// Internal services come first, then module owners by their lowest current port and finally by id.
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

            // Every owner of the left map must exist on the right with the same number of ports...
            foreach (var owner in left)
            {
                if (!right.TryGetValue(owner.Key, out var rightPorts) ||
                    owner.Value.Count != rightPorts.Count)
                {
                    return false;
                }

                // ...and each port key must map to the same port number.
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


        // Nested types

        /// <summary>
        /// Stores one owner and its port keys while rebuilding the runtime map.
        /// </summary>
        private sealed record RedistributionOwner(string Id, List<string> PortKeys, int FirstPort);


        // Lazy load

        /// <summary>
        /// Loads the persisted port map once on first access.
        /// A missing or unreadable file leaves the map empty; assignments are then created on demand.
        /// </summary>
        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            // Mark as loaded before reading so a failed read is not retried on every access.
            _loaded = true;

            if (!File.Exists(_portMapPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_portMapPath);

                // Try the current schema (owner -> port key -> port) first, then the legacy flat schema.
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
                // An unreadable or corrupt file is treated as no assignments; fresh ports are allocated on demand.
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
