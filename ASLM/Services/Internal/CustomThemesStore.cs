// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Loads and saves user-defined themes from <c>Data/App/ASLM_CustomThemes.json</c>.
    /// </summary>
    public class CustomThemesStore
    {
        private readonly string _filePath;
        private readonly ILogger<CustomThemesStore> _logger;

        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static readonly Regex TrailingNumberSuffix = new(@"\s+#\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        // State access

        /// <summary>
        /// Gets the currently loaded custom themes root.
        /// </summary>
        public CustomThemesRoot Root { get; private set; } = new();

        // Initialization

        /// <summary>
        /// Creates the custom themes store.
        /// </summary>
        public CustomThemesStore(ILogger<CustomThemesStore> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var rootDir = GetRootDirectory();
            _filePath = Path.Combine(rootDir, "Data", "App", "ASLM_CustomThemes.json");
        }


        // Loading

        /// <summary>
        /// Loads the persisted custom themes or initializes an empty collection when the file is absent.
        /// </summary>
        public async Task LoadAsync()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = await File.ReadAllTextAsync(_filePath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        Root = JsonSerializer.Deserialize<CustomThemesRoot>(json, _jsonOptions) ?? new CustomThemesRoot();
                        Root.Normalize();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load custom themes from {FilePath}. Falling back to empty collection.", _filePath);
            }

            Root = new CustomThemesRoot();
        }


        // Saving

        /// <summary>
        /// Persists the current custom themes collection to disk.
        /// </summary>
        public async Task SaveAsync()
        {
            EnsureDirectoryExists();

            Root.Normalize();
            var json = JsonSerializer.Serialize(Root, _jsonOptions);
            await File.WriteAllTextAsync(_filePath, json);
        }


        // Theme helpers

        /// <summary>
        /// Returns the theme with the requested identifier, or null when not found.
        /// </summary>
        public CustomTheme? FindById(string? id) =>
            string.IsNullOrWhiteSpace(id)
                ? null
                : Root.Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Adds a new theme with a generated identifier and returns it.
        /// </summary>
        public CustomTheme CreateTheme(string name, string baseAppearance)
        {
            var uniqueName = AllocateUniqueDisplayName(name);
            var theme = new CustomTheme
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = uniqueName,
                BaseAppearance = CustomTheme.NormalizeBaseAppearance(baseAppearance),
                Colors = new(StringComparer.OrdinalIgnoreCase)
            };

            Root.Themes.Add(theme);
            return theme;
        }

        /// <summary>
        /// Returns a display name that does not collide with existing themes (adds " #2", " #3", … when needed).
        /// </summary>
        public string AllocateUniqueDisplayName(string desiredName, string? exceptThemeId = null)
        {
            var candidate = string.IsNullOrWhiteSpace(desiredName) ? "Theme" : desiredName.Trim();
            if (!IsDisplayNameTaken(candidate, exceptThemeId))
            {
                return candidate;
            }

            var stem = TrailingNumberSuffix.Replace(candidate, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "Theme";
            }

            var index = 2;
            while (IsDisplayNameTaken($"{stem} #{index}", exceptThemeId))
            {
                index++;
            }

            return $"{stem} #{index}";
        }

        /// <summary>
        /// Renames one theme while preserving display-name uniqueness.
        /// </summary>
        public CustomTheme? RenameTheme(string id, string desiredName)
        {
            var theme = FindById(id);
            if (theme == null || string.IsNullOrWhiteSpace(desiredName))
            {
                return null;
            }

            theme.Name = AllocateUniqueDisplayName(desiredName, theme.Id);
            return theme;
        }

        /// <summary>
        /// Adds a copy of an imported theme with a new id and a non-colliding name.
        /// </summary>
        public CustomTheme ImportThemeCopy(CustomTheme source)
        {
            source.Normalize();
            var copy = new CustomTheme
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = AllocateUniqueDisplayName(source.Name),
                BaseAppearance = source.BaseAppearance,
                Colors = new Dictionary<string, string>(source.Colors, StringComparer.OrdinalIgnoreCase)
            };

            Root.Themes.Add(copy);
            return copy;
        }

        /// <summary>
        /// Serializes one theme for sharing or backup.
        /// </summary>
        public string SerializeThemeForExport(CustomTheme theme)
        {
            theme.Normalize();
            return JsonSerializer.Serialize(theme, _jsonOptions);
        }

        /// <summary>
        /// Parses JSON from a single-theme export or a root document (first theme is used).
        /// </summary>
        public CustomTheme? DeserializeImportedTheme(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("themes", out var themesEl) &&
                    themesEl.ValueKind == JsonValueKind.Array)
                {
                    var root = JsonSerializer.Deserialize<CustomThemesRoot>(json, _jsonOptions);
                    root?.Normalize();
                    return root?.Themes?.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse custom themes root from import JSON.");
            }

            try
            {
                var theme = JsonSerializer.Deserialize<CustomTheme>(json, _jsonOptions);
                theme?.Normalize();
                return theme;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse single custom theme from import JSON.");
                return null;
            }
        }

        /// <summary>
        /// Returns whether another theme already uses the requested display name.
        /// </summary>
        private bool IsDisplayNameTaken(string name, string? exceptThemeId) =>
            Root.Themes.Any(t =>
                (exceptThemeId == null ||
                 !string.Equals(t.Id, exceptThemeId, StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Removes the theme with the requested identifier.
        /// </summary>
        public bool DeleteTheme(string id) =>
            Root.Themes.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;


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

        // Root path

        /// <summary>
        /// Returns the application root directory above the deployed app folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
