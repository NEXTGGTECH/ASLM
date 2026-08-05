// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Loads and saves the persisted application data stored in <c>Data/App/ASLM_Data.json</c>.
    /// </summary>
    public class AppDataStore
    {
        private readonly string _filePath;
        private readonly ILogger<AppDataStore> _logger;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // State

        /// <summary>
        /// Gets the current application data instance.
        /// </summary>
        public AppData Data { get; private set; } = new();

        /// <summary>
        /// Gets whether the first-run flow still needs to run.
        /// </summary>
        public bool IsFirstRun => !Data.FirstRunCompleted;


        // Construction

        /// <summary>
        /// Creates the store and resolves the persisted data file path.
        /// </summary>
        public AppDataStore(ILogger<AppDataStore> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var rootDir = GetRootDirectory();
            _filePath = Path.Combine(rootDir, "Data", "App", "ASLM_Data.json");
        }


        // Initialization

        /// <summary>
        /// Initializes the store by loading persisted data once at startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            await LoadAsync();
        }


        // Loading

        /// <summary>
        /// Loads persisted application data or recreates defaults when the file is missing or invalid.
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                // Read and deserialize the existing file when present and non-empty.
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        Data = JsonSerializer.Deserialize<AppData>(json, _jsonOptions) ?? new AppData();
                        Data.Normalize();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Fall back to defaults when the file cannot be read or parsed.
                _logger.LogError(ex, "Failed to load app data from {FilePath}. Falling back to defaults.", _filePath);
            }

            // Create defaults and persist them so the next run has a valid file.
            Data = new AppData();
            Data.Normalize();
            await SaveAsync();
        }


        // Saving

        /// <summary>
        /// Saves the current application data synchronously.
        /// </summary>
        public void Save()
        {
            EnsureDirectoryExists();

            var json = JsonSerializer.Serialize(Data, _jsonOptions);
            File.WriteAllText(_filePath, json);
        }

        /// <summary>
        /// Saves the current application data asynchronously.
        /// </summary>
        public async Task SaveAsync()
        {
            EnsureDirectoryExists();

            var json = JsonSerializer.Serialize(Data, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }


        // File system helpers

        /// <summary>
        /// Ensures the parent directory for the data file exists.
        /// </summary>
        private void EnsureDirectoryExists()
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        /// <summary>
        /// Returns the application root directory above the deployed app folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
