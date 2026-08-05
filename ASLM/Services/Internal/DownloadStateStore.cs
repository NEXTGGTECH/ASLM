// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Persists shared download catalog installation state to disk.
    /// </summary>
    public class DownloadStateStore
    {
        private readonly string _filePath;
        private readonly ILogger<DownloadStateStore> _logger;
        private readonly object _lock = new();

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private DownloadCatalogStateFile? _state;


        // Initialization

        /// <summary>
        /// Creates the state store and resolves the on-disk storage path.
        /// </summary>
        public DownloadStateStore(ILogger<DownloadStateStore> logger)
        {
            _logger = logger;

            var rootDir = GetRootDirectory();
            _filePath = Path.Combine(rootDir, "Data", "App", "ASLM_Downloads.json");
        }


        // State access

        /// <summary>
        /// Returns the persisted state for one resource.
        /// </summary>
        public DownloadCatalogResourceState? GetResourceState(string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return null;
            }

            lock (_lock)
            {
                var state = EnsureLoaded();
                return state.Resources.TryGetValue(resourceKey, out var resourceState)
                    ? resourceState
                    : null;
            }
        }


        // State writes

        /// <summary>
        /// Marks one resource as installed and persists the change.
        /// </summary>
        public async Task MarkInstalledAsync(string resourceKey, string version, string providerModuleId)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return;
            }

            lock (_lock)
            {
                var state = EnsureLoaded();
                state.Resources[resourceKey] = new DownloadCatalogResourceState
                {
                    Installed = true,
                    InstalledVersion = string.IsNullOrWhiteSpace(version) ? null : version,
                    LastInstalledUtc = DateTime.UtcNow.ToString("o"),
                    ProviderModuleId = string.IsNullOrWhiteSpace(providerModuleId) ? null : providerModuleId
                };
            }

            await SaveAsync();
        }

        /// <summary>
        /// Marks one resource as removed and persists the change.
        /// </summary>
        public async Task MarkUninstalledAsync(string resourceKey)
        {
            if (string.IsNullOrWhiteSpace(resourceKey))
            {
                return;
            }

            lock (_lock)
            {
                var state = EnsureLoaded();
                state.Resources[resourceKey] = new DownloadCatalogResourceState
                {
                    Installed = false,
                    InstalledVersion = null,
                    LastInstalledUtc = null,
                    ProviderModuleId = null
                };
            }

            await SaveAsync();
        }


        // Loading and saving

        /// <summary>
        /// Loads the persisted state on first access.
        /// </summary>
        private DownloadCatalogStateFile EnsureLoaded()
        {
            if (_state != null)
            {
                return _state;
            }

            try
            {
                // Deserialize the on-disk snapshot once, then normalize it for callers.
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        _state = JsonSerializer.Deserialize<DownloadCatalogStateFile>(json, _jsonOptions) ?? new DownloadCatalogStateFile();

                        _state.Normalize();
                        return _state;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load download catalog state from {FilePath}. Falling back to defaults.", _filePath);
            }

            // Fall back to an empty catalog when the file is missing or unreadable.
            _state = new DownloadCatalogStateFile();
            _state.Normalize();
            return _state;
        }

        /// <summary>
        /// Persists the current state snapshot to disk.
        /// </summary>
        private async Task SaveAsync()
        {
            DownloadCatalogStateFile snapshot;

            lock (_lock)
            {
                snapshot = EnsureLoaded();
            }

            try
            {
                // Ensure the state directory exists before writing the serialized file.
                var directory = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save download catalog state to {FilePath}.", _filePath);
            }
        }


        // Path helpers

        /// <summary>
        /// Resolves the application root directory.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
