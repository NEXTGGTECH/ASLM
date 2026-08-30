// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Provides the single version-aware entry point for reading ASLM module manifests.
    /// </summary>
    public static class ModuleManifestParser
    {
        public const int CurrentFileVersion = 2;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Parses, normalizes, validates, and resolves one manifest for the current platform.
        /// </summary>
        public static ModuleConfig Parse(string json, string sourcePath = "")
        {
            // Reject invalid JSON before creating a partially normalized module model.
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("Module manifest is empty.");
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Module manifest root must be a JSON object.");
            }

            // Select the schema before deserialization so future versions never fall back silently.
            var fileVersion = ReadFileVersion(document.RootElement);
            if (fileVersion is < 1 or > CurrentFileVersion)
            {
                throw new NotSupportedException(
                    $"Unsupported module fileVersion {fileVersion}. Supported versions are 1 and {CurrentFileVersion}.");
            }

            // Normalize the shared model and resolve platform-specific runtime metadata once.
            var config = JsonSerializer.Deserialize<ModuleConfig>(json, JsonOptions)
                ?? throw new InvalidDataException("Module manifest could not be deserialized.");

            config.FileVersion = fileVersion;
            config.SourcePath = sourcePath ?? string.Empty;
            config.HasDeclaredUpdateConfig = document.RootElement.TryGetProperty("update", out _);
            config.Normalize();
            config.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);

            foreach (var engine in config.Engines)
            {
                engine.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey);
            }

            Validate(config);
            return config;
        }

        /// <summary>
        /// Loads a manifest from disk through the same parser used by install and update flows.
        /// </summary>
        public static async Task<ModuleConfig> LoadAsync(string path, CancellationToken ct = default)
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return Parse(json, path);
        }

        /// <summary>
        /// Attempts to parse a manifest without throwing to discovery callers.
        /// </summary>
        public static bool TryParse(string json, string sourcePath, out ModuleConfig? config, out string error)
        {
            try
            {
                config = Parse(json, sourcePath);
                error = string.Empty;
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                config = null;
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Reads the schema version while preserving compatibility with versionless manifests.
        /// </summary>
        private static int ReadFileVersion(JsonElement root)
        {
            if (!root.TryGetProperty("fileVersion", out var value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return 1;
            }

            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version))
            {
                throw new InvalidDataException("Module fileVersion must be an integer.");
            }

            return version <= 0 ? 1 : version;
        }

        /// <summary>
        /// Validates structural invariants required for safe discovery and installation.
        /// </summary>
        private static void Validate(ModuleConfig config)
        {
            // Identity and platform declarations are required to install a v2 module safely.
            if (string.IsNullOrWhiteSpace(config.Id))
            {
                throw new InvalidDataException("Module id is required.");
            }

            if (config.FileVersion >= 2 && config.SupportedPlatforms.Count == 0)
            {
                throw new InvalidDataException("ModulesAPI v2 requires at least one supportedPlatforms entry.");
            }

            // Stable identifiers must stay unique because lookups are case-insensitive.
            var duplicateSettingKey = config.Settings
                .Where(static setting => setting != null && !string.IsNullOrWhiteSpace(setting.Key))
                .GroupBy(static setting => setting.Key, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateSettingKey != null)
            {
                throw new InvalidDataException($"Duplicate module setting key '{duplicateSettingKey.Key}'.");
            }

            var duplicateCategoryId = config.SettingCategories
                .GroupBy(static category => category.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateCategoryId != null)
            {
                throw new InvalidDataException($"Duplicate setting category id '{duplicateCategoryId.Key}'.");
            }

            var duplicateEngineId = config.Engines
                .Where(static engine => !string.IsNullOrWhiteSpace(engine.Id))
                .GroupBy(static engine => engine.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicateEngineId != null)
            {
                throw new InvalidDataException($"Duplicate module-provided engine id '{duplicateEngineId.Key}'.");
            }

            // Embedded engines belong to v2; their own schema must also be supported.
            if (config.FileVersion < 2 && config.Engines.Count > 0)
            {
                config.ValidationWarnings.Add("Module-provided engines are ignored unless fileVersion is 2.");
                config.Engines.Clear();
            }

            foreach (var engine in config.Engines)
            {
                if (string.IsNullOrWhiteSpace(engine.Id))
                {
                    throw new InvalidDataException("Module-provided engine id is required.");
                }

                if (engine.FileVersion is < 1 or > 2)
                {
                    throw new NotSupportedException(
                        $"Module-provided engine '{engine.Id}' uses unsupported fileVersion {engine.FileVersion}.");
                }
            }

            // User-setting metadata is diagnostic and remains fail-open at runtime.
            ValidateSettingMetadata(config);
            foreach (var warning in config.ValidationWarnings)
            {
                Debug.WriteLine($"Module manifest warning ({config.Id}): {warning}");
            }
        }

        /// <summary>
        /// Reports invalid category and dependency metadata without rejecting the module.
        /// </summary>
        private static void ValidateSettingMetadata(ModuleConfig config)
        {
            var settings = config.Settings
                .Where(static setting => setting != null && !string.IsNullOrWhiteSpace(setting.Key))
                .ToDictionary(static setting => setting.Key, StringComparer.OrdinalIgnoreCase);
            var categories = config.SettingCategories
                .Select(static category => category.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Check category references and ensure every explicit controller is a user bool.
            foreach (var setting in settings.Values.Where(IsMetadataEligible))
            {
                if (!string.IsNullOrWhiteSpace(setting.Category) && !categories.Contains(setting.Category))
                {
                    config.ValidationWarnings.Add(
                        $"Setting '{setting.Key}' references unknown category '{setting.Category}'.");
                }

                if (string.IsNullOrWhiteSpace(setting.DependsOn))
                {
                    continue;
                }

                if (string.Equals(setting.Key, setting.DependsOn, StringComparison.OrdinalIgnoreCase))
                {
                    config.ValidationWarnings.Add($"Setting '{setting.Key}' cannot depend on itself.");
                    continue;
                }

                if (!settings.TryGetValue(setting.DependsOn, out var controller))
                {
                    config.ValidationWarnings.Add(
                        $"Setting '{setting.Key}' references missing dependency '{setting.DependsOn}'.");
                    continue;
                }

                if (!IsMetadataEligible(controller) || controller.NormalizedType != "bool")
                {
                    config.ValidationWarnings.Add(
                        $"Setting '{setting.Key}' dependency '{setting.DependsOn}' must be a user bool setting.");
                }
            }

            // Report cycles separately so authors can locate every affected setting.
            foreach (var setting in settings.Values.Where(IsMetadataEligible))
            {
                if (HasDependencyCycle(setting, settings))
                {
                    config.ValidationWarnings.Add($"Dependency cycle detected at setting '{setting.Key}'.");
                }
            }
        }

        /// <summary>
        /// Detects cycles in a setting's explicit boolean dependency chain.
        /// </summary>
        private static bool HasDependencyCycle(
            ModuleSetting start,
            IReadOnlyDictionary<string, ModuleSetting> settings)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = start;
            while (!string.IsNullOrWhiteSpace(current.DependsOn) &&
                   settings.TryGetValue(current.DependsOn, out var next) &&
                   IsMetadataEligible(next) &&
                   next.NormalizedType == "bool")
            {
                if (!visited.Add(current.Key) ||
                    string.Equals(next.Key, start.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                current = next;
            }

            return false;
        }

        /// <summary>
        /// Excludes ASLM-managed settings from user category and dependency metadata.
        /// </summary>
        private static bool IsMetadataEligible(ModuleSetting setting) =>
            !setting.IsHostKey &&
            setting.NormalizedType is not ("port" or "theme" or "locale");
    }
}
