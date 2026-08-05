// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Engine manifest

    /// <summary>
    /// Describes an engine package loaded from <c>ASLM_Engine.json</c>.
    /// </summary>
    [JsonConverter(typeof(EngineConfigJsonConverter))]
    public class EngineConfig
    {
        // Version of the manifest schema used for compatibility checks.
        public int FileVersion { get; set; } = 2;

        // Stable engine identifier used throughout the application.
        public string Id { get; set; } = string.Empty;

        // Human-readable engine name shown in the UI.
        public string Name { get; set; } = string.Empty;

        // Short engine description displayed in setup and management screens.
        public string Description { get; set; } = string.Empty;

        // Version string of the packaged engine runtime.
        public string Version { get; set; } = string.Empty;

        // Resource type stored in the manifest, for example "runtime".
        public string Type { get; set; } = string.Empty;

        // Platforms this engine can install on, each mapping an os/arch pair to a block key.
        public List<SupportedPlatform> SupportedPlatforms { get; set; } = [];

        // Platform-specific install blocks keyed by platform key (e.g. "windows-amd64").
        public Dictionary<string, EnginePlatform> Platforms { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        // Persisted installation state of the engine (platform independent).
        public EngineStatus Status { get; set; } = new();

        // Absolute path to the source manifest file resolved at runtime.
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

        // Platform key whose block backs the flat view below; defaults before resolution.
        [JsonIgnore]
        public string ActivePlatformKey { get; private set; } = "windows-amd64";

        // Whether the current platform was found in supportedPlatforms during resolution.
        [JsonIgnore]
        public bool IsSupportedOnCurrentPlatform { get; private set; }


        // Active platform view

        /// <summary>
        /// Returns the resolved platform block or null when the active key has no block yet.
        /// </summary>
        [JsonIgnore]
        public EnginePlatform? ActivePlatform =>
            Platforms.TryGetValue(ActivePlatformKey, out var platform) ? platform : null;

        /// <summary>
        /// Returns the active platform block, creating and registering it on first write.
        /// </summary>
        private EnginePlatform EnsureActivePlatform()
        {
            if (!Platforms.TryGetValue(ActivePlatformKey, out var platform))
            {
                platform = new EnginePlatform();
                Platforms[ActivePlatformKey] = platform;
            }

            return platform;
        }

        // Relative path to the primary executable inside the engine folder.
        [JsonIgnore]
        public string ExecutablePath
        {
            get => ActivePlatform?.ExecutablePath ?? string.Empty;
            set => EnsureActivePlatform().ExecutablePath = value;
        }

        // Package manager configuration used to install engine-specific libraries.
        [JsonIgnore]
        public EnginePackageManager? PackageManager
        {
            get => ActivePlatform?.PackageManager;
            set => EnsureActivePlatform().PackageManager = value;
        }

        // Optional per-module environment configuration used to isolate dependencies.
        [JsonIgnore]
        public EngineModuleEnvironment? ModuleEnvironment
        {
            get => ActivePlatform?.ModuleEnvironment;
            set => EnsureActivePlatform().ModuleEnvironment = value;
        }

        // Informational system requirements declared by the active platform block.
        [JsonIgnore]
        public EngineRequirements? Requirements
        {
            get => ActivePlatform?.Requirements;
            set => EnsureActivePlatform().Requirements = value;
        }

        // Installation steps executed in the declared order for the active platform.
        [JsonIgnore]
        public List<InstallStep> Install
        {
            get => ActivePlatform?.Install ?? [];
            set => EnsureActivePlatform().Install = value;
        }

        // Additional steps executed after the main installation pipeline completes.
        [JsonIgnore]
        public List<InstallStep> PostInstall
        {
            get => ActivePlatform?.PostInstall ?? [];
            set => EnsureActivePlatform().PostInstall = value;
        }

        // Remote update configuration used to check GitHub releases for the active platform.
        [JsonIgnore]
        public EngineUpdateConfig? Update
        {
            get => ActivePlatform?.Update;
            set => EnsureActivePlatform().Update = value;
        }


        // Normalization and resolution

        /// <summary>
        /// Restores nested objects, collections, and strings after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id ??= string.Empty;
            Name ??= string.Empty;
            Description ??= string.Empty;
            Version ??= string.Empty;
            Type ??= string.Empty;
            SourcePath ??= string.Empty;

            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            SupportedPlatforms ??= [];
            foreach (var platform in SupportedPlatforms)
            {
                platform?.Normalize();
            }

            Platforms ??= new(StringComparer.OrdinalIgnoreCase);
            foreach (var block in Platforms.Values)
            {
                block?.Normalize();
            }

            // Synthesize platform metadata for in-code or flat manifests that declared blocks
            // without a supportedPlatforms list.
            if (SupportedPlatforms.Count == 0 && Platforms.Count > 0)
            {
                SupportedPlatforms = Platforms.Keys
                    .Select(SupportedPlatform.FromKey)
                    .ToList();
            }

            Status ??= new();
            Status.Normalize();
        }

        /// <summary>
        /// Selects the platform block for one os/arch pair and exposes it through the flat view.
        /// </summary>
        public void ResolveForPlatform(string osKey, string archKey)
        {
            var match = SupportedPlatforms.FirstOrDefault(platform =>
                ArchEquals(platform.Os, osKey) && ArchEquals(platform.Arch, archKey));

            string? resolvedKey = match?.Key;
            if (string.IsNullOrWhiteSpace(resolvedKey) || !Platforms.ContainsKey(resolvedKey))
            {
                resolvedKey = null;
                foreach (var candidate in PlatformKeyCandidates(osKey, archKey))
                {
                    if (Platforms.ContainsKey(candidate))
                    {
                        resolvedKey = candidate;
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(resolvedKey) && Platforms.ContainsKey(resolvedKey))
            {
                ActivePlatformKey = resolvedKey;
                IsSupportedOnCurrentPlatform = true;
            }
            else
            {
                ActivePlatformKey = $"{osKey}-{archKey}";
                IsSupportedOnCurrentPlatform = false;
            }
        }

        /// <summary>
        /// Returns the platform-key candidates for an os/arch pair, tolerating the legacy x64 alias.
        /// </summary>
        private static IReadOnlyList<string> PlatformKeyCandidates(string osKey, string archKey) =>
            string.Equals(archKey, "amd64", StringComparison.OrdinalIgnoreCase)
                ? [$"{osKey}-amd64", $"{osKey}-x64"]
                : [$"{osKey}-{archKey}"];

        /// <summary>
        /// Compares two os/arch tokens, treating amd64 and x64 as equivalent.
        /// </summary>
        private static bool ArchEquals(string left, string right)
        {
            left = CanonicalToken(left);
            right = CanonicalToken(right);
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static string CanonicalToken(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.ToLowerInvariant() switch
            {
                "x64" => "amd64",
                "x86_64" => "amd64",
                "aarch64" => "arm64",
                "osx" => "macos",
                "win" => "windows",
                _ => value
            };
        }
    }


    // Platform support

    /// <summary>
    /// Maps one supported os/arch pair to the platform block key that carries its install data.
    /// </summary>
    public class SupportedPlatform
    {
        // Operating system token, for example "windows".
        [JsonPropertyName("os")]
        public string Os { get; set; } = string.Empty;

        // Architecture token, for example "amd64" or "arm64".
        [JsonPropertyName("arch")]
        public string Arch { get; set; } = string.Empty;

        // Name of the top-level manifest block that holds this platform's install data.
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Restores required string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Os = (Os ?? string.Empty).Trim();
            Arch = (Arch ?? string.Empty).Trim();
            Key = string.IsNullOrWhiteSpace(Key) ? $"{Os}-{Arch}" : Key.Trim();
        }

        /// <summary>
        /// Builds a platform descriptor from a "{os}-{arch}" key, used to backfill metadata.
        /// </summary>
        public static SupportedPlatform FromKey(string key)
        {
            var trimmed = (key ?? string.Empty).Trim();
            var separator = trimmed.IndexOf('-');
            var platform = separator > 0
                ? new SupportedPlatform
                {
                    Os = trimmed[..separator],
                    Arch = trimmed[(separator + 1)..],
                    Key = trimmed
                }
                : new SupportedPlatform { Os = trimmed, Arch = string.Empty, Key = trimmed };
            platform.Normalize();
            return platform;
        }
    }


    // Platform block

    /// <summary>
    /// Holds the platform-specific install data for one engine platform key.
    /// </summary>
    public class EnginePlatform
    {
        // Relative path to the primary executable inside the engine folder.
        [JsonPropertyName("executablePath")]
        public string ExecutablePath { get; set; } = string.Empty;

        // Package manager configuration used to install engine-specific libraries.
        [JsonPropertyName("packageManager")]
        public EnginePackageManager? PackageManager { get; set; }

        // Optional per-module environment configuration used to isolate dependencies.
        [JsonPropertyName("moduleEnvironment")]
        public EngineModuleEnvironment? ModuleEnvironment { get; set; }

        // Informational system requirements declared by this platform block.
        [JsonPropertyName("requirements")]
        public EngineRequirements? Requirements { get; set; }

        // Installation steps executed in the declared order.
        [JsonPropertyName("install")]
        public List<InstallStep> Install { get; set; } = [];

        // Additional steps executed after the main installation pipeline completes.
        [JsonPropertyName("postInstall")]
        public List<InstallStep> PostInstall { get; set; } = [];

        // Remote update configuration used to check GitHub releases.
        [JsonPropertyName("update")]
        public EngineUpdateConfig? Update { get; set; }

        /// <summary>
        /// Restores nested objects, collections, and strings after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            ExecutablePath ??= string.Empty;

            PackageManager?.Normalize();
            ModuleEnvironment?.Normalize();
            Requirements?.Normalize();

            Install ??= [];
            foreach (var step in Install)
            {
                step?.Normalize();
            }

            PostInstall ??= [];
            foreach (var step in PostInstall)
            {
                step?.Normalize();
            }

            Update?.Normalize();
        }
    }


    // Installation steps

    /// <summary>
    /// Describes one declarative installation action in the engine manifest.
    /// </summary>
    public class InstallStep
    {
        // Action name that determines which fields are meaningful for this step.
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        // Optional human-readable label used in logs and progress UI.
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        // Download source URL.
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        // Expected SHA-256 checksum for downloaded content.
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        // Destination path used by download, extract, or move actions.
        [JsonPropertyName("dest")]
        public string? Dest { get; set; }

        // Source path used by extract or move actions.
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        // File path targeted by modify-file actions.
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        // Text fragment to locate inside a file modification action.
        [JsonPropertyName("find")]
        public string? Find { get; set; }

        // Replacement text for a file modification action.
        [JsonPropertyName("replace")]
        public string? Replace { get; set; }

        // Command line used by execute actions.
        [JsonPropertyName("command")]
        public string? Command { get; set; }

        // File or directory removed by cleanup actions.
        [JsonPropertyName("target")]
        public string? Target { get; set; }

        /// <summary>
        /// Restores the required action name after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep the action field non-null because step dispatch depends on it.
            Action ??= string.Empty;
        }
    }


    // Package management

    /// <summary>
    /// Describes how the engine installs third-party libraries.
    /// </summary>
    public class EnginePackageManager
    {
        // Arguments passed before package names, for example "-m pip install".
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        // Optional executable path used instead of the engine's main executable.
        [JsonPropertyName("executable")]
        public string? Executable { get; set; }

        /// <summary>
        /// Restores required string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep package installation commands safe for command construction.
            Command ??= string.Empty;
        }
    }


    // Module environments

    /// <summary>
    /// Describes how one engine creates and uses isolated environments per module.
    /// </summary>
    public class EngineModuleEnvironment
    {
        // Allows an engine manifest to opt out while keeping the block for documentation.
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        // Environment folder prefix inside the engine directory.
        [JsonPropertyName("directoryPrefix")]
        public string DirectoryPrefix { get; set; } = "venv-";

        // Optional descriptive kind, for example "python-venv" or "node-prefix".
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        // Optional command run with the engine executable to create the environment.
        [JsonPropertyName("createCommand")]
        public string CreateCommand { get; set; } = string.Empty;

        // Executable used for module commands after environment creation.
        [JsonPropertyName("executablePath")]
        public string ExecutablePath { get; set; } = string.Empty;

        // Optional package-manager executable override for environment-scoped installs.
        [JsonPropertyName("packageManagerExecutable")]
        public string PackageManagerExecutable { get; set; } = string.Empty;

        // Optional package-manager command override for environment-scoped installs.
        [JsonPropertyName("packageManagerCommand")]
        public string PackageManagerCommand { get; set; } = string.Empty;

        // Environment variables injected when commands run through this module environment.
        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Restores string values and dictionaries after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            DirectoryPrefix = string.IsNullOrWhiteSpace(DirectoryPrefix) ? "venv-" : DirectoryPrefix;
            Kind ??= string.Empty;
            CreateCommand ??= string.Empty;
            ExecutablePath ??= string.Empty;
            PackageManagerExecutable ??= string.Empty;
            PackageManagerCommand ??= string.Empty;

            Environment ??= new(StringComparer.OrdinalIgnoreCase);
            Environment = Environment
                .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(
                    static pair => pair.Key.Trim(),
                    static pair => pair.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
        }
    }


    // Requirements

    /// <summary>
    /// Stores the system requirements declared by an engine platform block.
    /// </summary>
    public class EngineRequirements
    {
        // Required free disk space in megabytes. The os/arch dimension lives in supportedPlatforms.
        [JsonPropertyName("diskSpaceMb")]
        public int DiskSpaceMb { get; set; }

        /// <summary>
        /// Placeholder for symmetry with other config blocks after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
        }
    }


    // Update configuration

    /// <summary>
    /// Describes how one engine platform checks and downloads updates from GitHub releases.
    /// </summary>
    public class EngineUpdateConfig
    {
        // GitHub repository in owner/name form.
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        // Release asset name for this platform block (already platform-specific).
        [JsonPropertyName("assetName")]
        public string AssetName { get; set; } = string.Empty;

        /// <summary>
        /// Restores required string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Repo = string.IsNullOrWhiteSpace(Repo) ? string.Empty : Repo.Trim();
            AssetName = string.IsNullOrWhiteSpace(AssetName) ? string.Empty : AssetName.Trim();
        }
    }


    // Status tracking

    /// <summary>
    /// Tracks the persisted installation state of an engine package.
    /// </summary>
    public class EngineStatus
    {
        // Indicates whether the engine is currently installed.
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        // Version string of the installed engine, if known.
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        // GitHub release tag of the installed engine, if known.
        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        // Timestamp of the latest installation check or update.
        [JsonPropertyName("lastChecked")]
        public string? LastChecked { get; set; }

        /// <summary>
        /// Normalizes optional persisted values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Empty text values are treated as missing persisted state.
            InstalledVersion = string.IsNullOrWhiteSpace(InstalledVersion) ? null : InstalledVersion;
            InstalledReleaseTag = string.IsNullOrWhiteSpace(InstalledReleaseTag) ? null : InstalledReleaseTag;
            LastChecked = string.IsNullOrWhiteSpace(LastChecked) ? null : LastChecked;
        }
    }


    // Serialization

    /// <summary>
    /// Reads engine manifests of fileVersion 1 (flat) and 2 (per-platform blocks), and always
    /// writes fileVersion 2 so manifests converge on the platform-aware layout.
    /// </summary>
    public sealed class EngineConfigJsonConverter : JsonConverter<EngineConfig>
    {
        private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "fileVersion", "id", "name", "description", "version", "type", "supportedPlatforms", "status",
            // Legacy v1 flat sections lifted into a single platform block.
            "executablePath", "packageManager", "moduleEnvironment", "requirements", "install", "postInstall", "update"
        };

        public override EngineConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Engine manifest root must be a JSON object.");
            }

            var config = new EngineConfig
            {
                FileVersion = GetInt(root, "fileVersion", 1),
                Id = GetString(root, "id"),
                Name = GetString(root, "name"),
                Description = GetString(root, "description"),
                Version = GetString(root, "version"),
                Type = GetString(root, "type")
            };

            if (root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Object)
            {
                config.Status = statusElement.Deserialize<EngineStatus>(options) ?? new EngineStatus();
            }

            if (root.TryGetProperty("supportedPlatforms", out var supportedElement) &&
                supportedElement.ValueKind == JsonValueKind.Array)
            {
                config.SupportedPlatforms = supportedElement.Deserialize<List<SupportedPlatform>>(options) ?? [];
            }

            if (config.FileVersion >= 2)
            {
                ReadPlatformBlocks(root, config, options);
            }
            else
            {
                ReadLegacyFlatManifest(root, config, options);
            }

            config.Normalize();
            return config;
        }

        public override void Write(Utf8JsonWriter writer, EngineConfig value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("fileVersion", 2);
            writer.WriteString("id", value.Id);
            writer.WriteString("name", value.Name);
            writer.WriteString("description", value.Description);
            writer.WriteString("version", value.Version);
            writer.WriteString("type", value.Type);

            writer.WritePropertyName("supportedPlatforms");
            JsonSerializer.Serialize(writer, value.SupportedPlatforms, options);

            // Emit platform blocks in supportedPlatforms order first, then any remaining blocks.
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in value.SupportedPlatforms)
            {
                if (!string.IsNullOrWhiteSpace(platform.Key) &&
                    value.Platforms.TryGetValue(platform.Key, out var orderedBlock) &&
                    written.Add(platform.Key))
                {
                    writer.WritePropertyName(platform.Key);
                    JsonSerializer.Serialize(writer, orderedBlock, options);
                }
            }

            foreach (var pair in value.Platforms)
            {
                if (written.Add(pair.Key))
                {
                    writer.WritePropertyName(pair.Key);
                    JsonSerializer.Serialize(writer, pair.Value, options);
                }
            }

            writer.WritePropertyName("status");
            JsonSerializer.Serialize(writer, value.Status, options);
            writer.WriteEndObject();
        }

        /// <summary>
        /// Reads every non-reserved object property as a platform block.
        /// </summary>
        private static void ReadPlatformBlocks(JsonElement root, EngineConfig config, JsonSerializerOptions options)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (ReservedKeys.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var block = property.Value.Deserialize<EnginePlatform>(options);
                if (block != null)
                {
                    config.Platforms[property.Name] = block;
                }
            }
        }

        /// <summary>
        /// Lifts a flat fileVersion 1 manifest into a single platform block.
        /// </summary>
        private static void ReadLegacyFlatManifest(JsonElement root, EngineConfig config, JsonSerializerOptions options)
        {
            var block = new EnginePlatform
            {
                ExecutablePath = GetString(root, "executablePath")
            };

            if (root.TryGetProperty("packageManager", out var packageManager) && packageManager.ValueKind == JsonValueKind.Object)
            {
                block.PackageManager = packageManager.Deserialize<EnginePackageManager>(options);
            }

            if (root.TryGetProperty("moduleEnvironment", out var moduleEnvironment) && moduleEnvironment.ValueKind == JsonValueKind.Object)
            {
                block.ModuleEnvironment = moduleEnvironment.Deserialize<EngineModuleEnvironment>(options);
            }

            string legacyOs = "windows";
            string legacyArch = "amd64";
            if (root.TryGetProperty("requirements", out var requirements) && requirements.ValueKind == JsonValueKind.Object)
            {
                block.Requirements = requirements.Deserialize<EngineRequirements>(options);
                legacyOs = NormalizeLegacyToken(GetString(requirements, "os"), legacyOs);
                legacyArch = NormalizeLegacyToken(GetString(requirements, "arch"), legacyArch);
            }

            if (root.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Array)
            {
                block.Install = install.Deserialize<List<InstallStep>>(options) ?? [];
            }

            if (root.TryGetProperty("postInstall", out var postInstall) && postInstall.ValueKind == JsonValueKind.Array)
            {
                block.PostInstall = postInstall.Deserialize<List<InstallStep>>(options) ?? [];
            }

            if (root.TryGetProperty("update", out var update) && update.ValueKind == JsonValueKind.Object)
            {
                block.Update = ReadLegacyUpdate(update, $"{legacyOs}-{legacyArch}", options);
            }

            var key = $"{legacyOs}-{legacyArch}";
            config.Platforms[key] = block;
            if (config.SupportedPlatforms.Count == 0)
            {
                config.SupportedPlatforms = [SupportedPlatform.FromKey(key)];
            }
        }

        /// <summary>
        /// Reads a legacy update block whose assetName may be a platform-keyed object and
        /// collapses it to the asset for the manifest's own platform key.
        /// </summary>
        private static EngineUpdateConfig ReadLegacyUpdate(JsonElement update, string platformKey, JsonSerializerOptions options)
        {
            var result = new EngineUpdateConfig
            {
                Repo = GetString(update, "repo")
            };

            if (!update.TryGetProperty("assetName", out var assetName))
            {
                return result;
            }

            if (assetName.ValueKind == JsonValueKind.String)
            {
                result.AssetName = assetName.GetString() ?? string.Empty;
            }
            else if (assetName.ValueKind == JsonValueKind.Object)
            {
                var map = assetName.Deserialize<Dictionary<string, string>>(options) ?? new();
                foreach (var candidate in PlatformKeyCandidates(platformKey))
                {
                    if (map.TryGetValue(candidate, out var value) && !string.IsNullOrWhiteSpace(value))
                    {
                        result.AssetName = value;
                        break;
                    }
                }
            }

            return result;
        }

        private static IReadOnlyList<string> PlatformKeyCandidates(string platformKey)
        {
            var separator = platformKey.IndexOf('-');
            if (separator <= 0)
            {
                return [platformKey];
            }

            var os = platformKey[..separator];
            var arch = platformKey[(separator + 1)..];
            return string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase)
                ? [$"{os}-amd64", $"{os}-x64"]
                : [platformKey];
        }

        private static string NormalizeLegacyToken(string value, string fallback)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return fallback;
            }

            return value.ToLowerInvariant() switch
            {
                "x64" or "x86_64" => "amd64",
                "aarch64" => "arm64",
                "osx" => "macos",
                "win" => "windows",
                _ => value
            };
        }

        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static int GetInt(JsonElement element, string name, int fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;
    }
}
