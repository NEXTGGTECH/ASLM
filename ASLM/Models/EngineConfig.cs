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
        // Manifest properties

        /// <summary>
        /// Version of the manifest schema used for compatibility checks.
        /// </summary>
        public int FileVersion { get; set; } = 2;

        /// <summary>
        /// Stable engine identifier used throughout the application.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable engine name shown in the UI.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short engine description displayed in setup and management screens.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Version string of the packaged engine runtime.
        /// </summary>
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Resource type stored in the manifest, for example <c>runtime</c>.
        /// </summary>
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Platforms this engine can install on, each mapping an os/arch pair to a block key.
        /// </summary>
        public List<SupportedPlatform> SupportedPlatforms { get; set; } = [];

        /// <summary>
        /// Platform-specific install blocks keyed by platform key (e.g. <c>windows-amd64</c>).
        /// </summary>
        public Dictionary<string, EnginePlatform> Platforms { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Persisted installation state of the engine (platform independent).
        /// </summary>
        public EngineStatus Status { get; set; } = new();


        // Runtime state

        /// <summary>
        /// Absolute path to the source manifest file resolved at runtime.
        /// </summary>
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Original ASLM_Module.json path for module-provided engine definitions.
        /// </summary>
        [JsonIgnore]
        public string DefinitionSourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Stable owner id for a module-provided engine; empty for standalone engines.
        /// </summary>
        [JsonIgnore]
        public string OwnerModuleId { get; set; } = string.Empty;

        /// <summary>
        /// True when the authoritative definition came from ASLM_Module.json.
        /// </summary>
        [JsonIgnore]
        public bool IsModuleProvided { get; set; }

        /// <summary>
        /// Platform key whose block backs the flat view below; defaults before resolution.
        /// </summary>
        [JsonIgnore]
        public string ActivePlatformKey { get; private set; } = "windows-amd64";

        /// <summary>
        /// Whether the current platform was found in supportedPlatforms during resolution.
        /// </summary>
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


        // Flat view of the active platform block

        /// <summary>
        /// Relative path to the primary executable inside the engine folder.
        /// </summary>
        [JsonIgnore]
        public string ExecutablePath
        {
            get => ActivePlatform?.ExecutablePath ?? string.Empty;
            set => EnsureActivePlatform().ExecutablePath = value;
        }

        /// <summary>
        /// Package manager configuration used to install engine-specific libraries.
        /// </summary>
        [JsonIgnore]
        public EnginePackageManager? PackageManager
        {
            get => ActivePlatform?.PackageManager;
            set => EnsureActivePlatform().PackageManager = value;
        }

        /// <summary>
        /// Optional per-module environment configuration used to isolate dependencies.
        /// </summary>
        [JsonIgnore]
        public EngineModuleEnvironment? ModuleEnvironment
        {
            get => ActivePlatform?.ModuleEnvironment;
            set => EnsureActivePlatform().ModuleEnvironment = value;
        }

        /// <summary>
        /// Informational system requirements declared by the active platform block.
        /// </summary>
        [JsonIgnore]
        public EngineRequirements? Requirements
        {
            get => ActivePlatform?.Requirements;
            set => EnsureActivePlatform().Requirements = value;
        }

        /// <summary>
        /// Installation steps executed in the declared order for the active platform.
        /// </summary>
        [JsonIgnore]
        public List<InstallStep> Install
        {
            get => ActivePlatform?.Install ?? [];
            set => EnsureActivePlatform().Install = value;
        }

        /// <summary>
        /// Additional steps executed after the main installation pipeline completes.
        /// </summary>
        [JsonIgnore]
        public List<InstallStep> PostInstall
        {
            get => ActivePlatform?.PostInstall ?? [];
            set => EnsureActivePlatform().PostInstall = value;
        }

        /// <summary>
        /// Remote update configuration used to check GitHub releases for the active platform.
        /// </summary>
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
            // Keep scalar string fields non-null for downstream code and UI bindings.
            Id ??= string.Empty;
            Name ??= string.Empty;
            Description ??= string.Empty;
            Version ??= string.Empty;
            Type ??= string.Empty;
            SourcePath ??= string.Empty;
            DefinitionSourcePath ??= string.Empty;
            OwnerModuleId ??= string.Empty;

            // A missing or invalid schema version means the original flat layout.
            if (FileVersion <= 0)
            {
                FileVersion = 1;
            }

            // Recreate and normalize the declared platforms and their install blocks.
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

            // Ensure persisted installation status is always available.
            Status ??= new();
            Status.Normalize();
        }

        /// <summary>
        /// Selects the platform block for one os/arch pair and exposes it through the flat view.
        /// </summary>
        /// <param name="osKey">Host operating system token, for example <c>windows</c>.</param>
        /// <param name="archKey">Host architecture token, for example <c>amd64</c>.</param>
        public void ResolveForPlatform(string osKey, string archKey)
        {
            // Without a matching block the key still points at the host platform, so later writes create it there.
            var resolvedKey = ResolvePlatformKey(osKey, archKey);
            ActivePlatformKey = resolvedKey ?? $"{osKey}-{archKey}";
            IsSupportedOnCurrentPlatform = resolvedKey != null;
        }

        /// <summary>
        /// Enumerates installable platforms using the same block lookup as installation,
        /// without changing the active platform of a shared engine configuration.
        /// </summary>
        public IReadOnlyList<SupportedPlatform> GetSupportedPlatforms() =>
            (SupportedPlatforms.Count > 0 ? SupportedPlatforms : Platforms.Keys.Select(SupportedPlatform.FromKey))
                .Where(platform => platform != null &&
                    !string.IsNullOrWhiteSpace(platform.Os) && !string.IsNullOrWhiteSpace(platform.Arch))
                .Select(platform => SupportedPlatform.FromKey(
                    $"{SupportedPlatform.CanonicalToken(platform.Os)}-{SupportedPlatform.CanonicalToken(platform.Arch)}"))
                .DistinctBy(platform => platform.Key, StringComparer.OrdinalIgnoreCase)
                .Where(platform => ResolvePlatformKey(platform.Os, platform.Arch) != null)
                .ToList();

        /// <summary>
        /// Finds the key of the install block that serves one os/arch pair, or null when the engine does not
        /// support that platform.
        /// </summary>
        /// <param name="osKey">Host operating system token, for example <c>windows</c>.</param>
        /// <param name="archKey">Host architecture token, for example <c>amd64</c>.</param>
        /// <returns>The platform key of an existing non-null block, or null when none applies.</returns>
        private string? ResolvePlatformKey(string osKey, string archKey)
        {
            // Prefer the block key declared by the matching supportedPlatforms entry.
            var match = SupportedPlatforms.FirstOrDefault(platform =>
                platform != null && platform.Matches(osKey, archKey));

            // An existing install block must not bypass an explicit platform restriction.
            if (SupportedPlatforms.Count > 0 && match == null) return null;

            // Fall back to a conventional "{os}-{arch}" key when the declared key has no block.
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

            // Report the key only when its block really exists.
            return !string.IsNullOrWhiteSpace(resolvedKey) &&
                Platforms.TryGetValue(resolvedKey, out var block) && block != null
                    ? resolvedKey : null;
        }

        /// <summary>
        /// Returns the platform-key candidates for an os/arch pair, tolerating the legacy x64 alias.
        /// </summary>
        private static IReadOnlyList<string> PlatformKeyCandidates(string osKey, string archKey) =>
            string.Equals(archKey, "amd64", StringComparison.OrdinalIgnoreCase)
                ? [$"{osKey}-amd64", $"{osKey}-x64"]
                : [$"{osKey}-{archKey}"];
    }


    // Platform support

    /// <summary>
    /// Maps one supported os/arch pair to the platform block key that carries its install data.
    /// </summary>
    public class SupportedPlatform
    {
        /// <summary>
        /// Operating system token, for example <c>windows</c>.
        /// </summary>
        [JsonPropertyName("os")]
        public string Os { get; set; } = string.Empty;

        /// <summary>
        /// Architecture token, for example <c>amd64</c> or <c>arm64</c>.
        /// </summary>
        [JsonPropertyName("arch")]
        public string Arch { get; set; } = string.Empty;

        /// <summary>
        /// Name of the top-level manifest block that holds this platform's install data.
        /// </summary>
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
        /// <param name="key">Platform key; a key without a separator becomes an os-only descriptor.</param>
        public static SupportedPlatform FromKey(string key)
        {
            // Split at the first dash into os and arch; the original key is kept as the block key.
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

        /// <summary>
        /// Returns whether this descriptor matches one host os/architecture pair.
        /// </summary>
        public bool Matches(string osKey, string archKey) =>
            string.Equals(CanonicalToken(Os), CanonicalToken(osKey), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(CanonicalToken(Arch), CanonicalToken(archKey), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Normalizes aliases shared by module and engine manifests: <c>x64</c> and <c>x86_64</c> become <c>amd64</c>,
        /// <c>aarch64</c> becomes <c>arm64</c>, <c>osx</c> becomes <c>macos</c> and <c>win</c> becomes <c>windows</c>.
        /// Any other value is returned trimmed and lower-cased.
        /// </summary>
        public static string CanonicalToken(string? value)
        {
            value = (value ?? string.Empty).Trim();
            return value.ToLowerInvariant() switch
            {
                "x64" or "x86_64" => "amd64",
                "aarch64" => "arm64",
                "osx" => "macos",
                "win" => "windows",
                _ => value.ToLowerInvariant()
            };
        }
    }


    // Platform block

    /// <summary>
    /// Holds the platform-specific install data for one engine platform key.
    /// </summary>
    public class EnginePlatform
    {
        // Manifest properties

        /// <summary>
        /// Relative path to the primary executable inside the engine folder.
        /// </summary>
        [JsonPropertyName("executablePath")]
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// Package manager configuration used to install engine-specific libraries.
        /// </summary>
        [JsonPropertyName("packageManager")]
        public EnginePackageManager? PackageManager { get; set; }

        /// <summary>
        /// Optional per-module environment configuration used to isolate dependencies.
        /// </summary>
        [JsonPropertyName("moduleEnvironment")]
        public EngineModuleEnvironment? ModuleEnvironment { get; set; }

        /// <summary>
        /// Informational system requirements declared by this platform block.
        /// </summary>
        [JsonPropertyName("requirements")]
        public EngineRequirements? Requirements { get; set; }

        /// <summary>
        /// Installation steps executed in the declared order.
        /// </summary>
        [JsonPropertyName("install")]
        public List<InstallStep> Install { get; set; } = [];

        /// <summary>
        /// Additional steps executed after the main installation pipeline completes.
        /// </summary>
        [JsonPropertyName("postInstall")]
        public List<InstallStep> PostInstall { get; set; } = [];

        /// <summary>
        /// Remote update configuration used to check GitHub releases.
        /// </summary>
        [JsonPropertyName("update")]
        public EngineUpdateConfig? Update { get; set; }


        // Normalization

        /// <summary>
        /// Restores nested objects, collections, and strings after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Optional single-object sections are normalized only when present.
            ExecutablePath ??= string.Empty;

            PackageManager?.Normalize();
            ModuleEnvironment?.Normalize();
            Requirements?.Normalize();

            // Recreate both step lists and normalize every step.
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
        // Step properties

        /// <summary>
        /// Action name that determines which fields are meaningful for this step: <c>download</c>, <c>extract</c>,
        /// <c>modify_file</c>, <c>execute</c>, <c>move</c>, <c>cleanup</c>, <c>rename_file</c> or <c>delete_file</c>.
        /// </summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        /// <summary>
        /// Optional human-readable label used in logs and progress UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        /// <summary>
        /// Download source URL.
        /// </summary>
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        /// <summary>
        /// Expected SHA-256 checksum for downloaded content.
        /// </summary>
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        /// <summary>
        /// Destination path used by download, extract, move, or rename-file actions.
        /// </summary>
        [JsonPropertyName("dest")]
        public string? Dest { get; set; }

        /// <summary>
        /// Source path used by extract, move, or rename-file actions.
        /// </summary>
        [JsonPropertyName("source")]
        public string? Source { get; set; }

        /// <summary>
        /// File path targeted by modify-file actions.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>
        /// Text fragment to locate inside a file modification action.
        /// </summary>
        [JsonPropertyName("find")]
        public string? Find { get; set; }

        /// <summary>
        /// Replacement text for a file modification action.
        /// </summary>
        [JsonPropertyName("replace")]
        public string? Replace { get; set; }

        /// <summary>
        /// Command line used by execute actions.
        /// </summary>
        [JsonPropertyName("command")]
        public string? Command { get; set; }

        /// <summary>
        /// File or directory removed by cleanup actions, or file removed by delete-file actions.
        /// </summary>
        [JsonPropertyName("target")]
        public string? Target { get; set; }


        // Normalization

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
        /// <summary>
        /// Arguments passed before package names, for example <c>-m pip install</c>.
        /// </summary>
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// Optional executable path used instead of the engine's main executable.
        /// </summary>
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
        // Manifest properties

        /// <summary>
        /// Allows an engine manifest to opt out while keeping the block for documentation.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Environment folder prefix inside the engine directory.
        /// </summary>
        [JsonPropertyName("directoryPrefix")]
        public string DirectoryPrefix { get; set; } = "venv-";

        /// <summary>
        /// Optional descriptive kind, for example <c>python-venv</c> or <c>node-prefix</c>.
        /// </summary>
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        /// <summary>
        /// Optional command run with the engine executable to create the environment.
        /// </summary>
        [JsonPropertyName("createCommand")]
        public string CreateCommand { get; set; } = string.Empty;

        /// <summary>
        /// Executable used for module commands after environment creation.
        /// </summary>
        [JsonPropertyName("executablePath")]
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>
        /// Optional package-manager executable override for environment-scoped installs.
        /// </summary>
        [JsonPropertyName("packageManagerExecutable")]
        public string PackageManagerExecutable { get; set; } = string.Empty;

        /// <summary>
        /// Optional package-manager command override for environment-scoped installs.
        /// </summary>
        [JsonPropertyName("packageManagerCommand")]
        public string PackageManagerCommand { get; set; } = string.Empty;

        /// <summary>
        /// Environment variables injected when commands run through this module environment.
        /// </summary>
        [JsonPropertyName("environment")]
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);


        // Normalization

        /// <summary>
        /// Restores string values and dictionaries after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Fall back to the default folder prefix and keep text fields non-null.
            DirectoryPrefix = string.IsNullOrWhiteSpace(DirectoryPrefix) ? "venv-" : DirectoryPrefix;
            Kind ??= string.Empty;
            CreateCommand ??= string.Empty;
            ExecutablePath ??= string.Empty;
            PackageManagerExecutable ??= string.Empty;
            PackageManagerCommand ??= string.Empty;

            // Drop blank variable names, trim the rest and replace null values with empty strings.
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
        /// <summary>
        /// Required free disk space in megabytes. The os/arch dimension lives in supportedPlatforms.
        /// </summary>
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
        /// <summary>
        /// GitHub repository in owner/name form.
        /// </summary>
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        /// <summary>
        /// Release asset name for this platform block (already platform-specific).
        /// </summary>
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
        /// <summary>
        /// Indicates whether the engine is currently installed.
        /// </summary>
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        /// <summary>
        /// Version string of the installed engine, if known.
        /// </summary>
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        /// <summary>
        /// GitHub release tag of the installed engine, if known.
        /// </summary>
        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        /// <summary>
        /// Timestamp of the latest installation check or update.
        /// </summary>
        [JsonPropertyName("lastChecked")]
        public string? LastChecked { get; set; }

        /// <summary>
        /// Fingerprint of the declarative manifest used for the latest successful installation.
        /// </summary>
        [JsonPropertyName("installedManifestHash")]
        public string? InstalledManifestHash { get; set; }

        /// <summary>
        /// Normalizes optional persisted values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Empty text values are treated as missing persisted state.
            InstalledVersion = string.IsNullOrWhiteSpace(InstalledVersion) ? null : InstalledVersion;
            InstalledReleaseTag = string.IsNullOrWhiteSpace(InstalledReleaseTag) ? null : InstalledReleaseTag;
            LastChecked = string.IsNullOrWhiteSpace(LastChecked) ? null : LastChecked;
            InstalledManifestHash = string.IsNullOrWhiteSpace(InstalledManifestHash) ? null : InstalledManifestHash.Trim();
        }
    }


    // Serialization

    /// <summary>
    /// Reads engine manifests of fileVersion 1 (flat) and 2 (per-platform blocks), and always
    /// writes fileVersion 2 so manifests converge on the platform-aware layout.
    /// </summary>
    public sealed class EngineConfigJsonConverter : JsonConverter<EngineConfig>
    {
        // Fields

        /// <summary>
        /// Top-level manifest keys that are not platform blocks, so version 2 reading never treats them as one.
        /// </summary>
        private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "fileVersion", "id", "name", "description", "version", "type", "supportedPlatforms", "status",
            // Legacy v1 flat sections lifted into a single platform block.
            "executablePath", "packageManager", "moduleEnvironment", "requirements", "install", "postInstall", "update"
        };


        // Converter entry points

        /// <summary>
        /// Reads a manifest of either supported layout and returns it normalized.
        /// </summary>
        public override EngineConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Parse the whole object up front, because the layout depends on fileVersion, which may come later.
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Engine manifest root must be a JSON object.");
            }

            // Read the fields shared by both layouts.
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

            // Version 2 keeps one block per platform; version 1 is a single flat manifest.
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

        /// <summary>
        /// Writes the manifest in the version 2 layout with one top-level property per platform block.
        /// </summary>
        public override void Write(Utf8JsonWriter writer, EngineConfig value, JsonSerializerOptions options)
        {
            // Write the identity fields and the supported platform list.
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

            // The platform-independent installation state closes the manifest.
            writer.WritePropertyName("status");
            JsonSerializer.Serialize(writer, value.Status, options);
            writer.WriteEndObject();
        }


        // Manifest layout readers

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
            // Collect the flat sections into one block.
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

            // Version 1 stored the target platform inside requirements; default to windows-amd64 when absent.
            string legacyOs = "windows";
            string legacyArch = "amd64";
            if (root.TryGetProperty("requirements", out var requirements) && requirements.ValueKind == JsonValueKind.Object)
            {
                block.Requirements = requirements.Deserialize<EngineRequirements>(options);
                legacyOs = NormalizeLegacyToken(GetString(requirements, "os"), legacyOs);
                legacyArch = NormalizeLegacyToken(GetString(requirements, "arch"), legacyArch);
            }

            // Install steps and update settings belong to the platform derived above.
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

            // Register the block under its platform key and declare that platform when none is listed.
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

            // A plain string is used as is; an object maps platform keys to asset names.
            if (assetName.ValueKind == JsonValueKind.String)
            {
                result.AssetName = assetName.GetString() ?? string.Empty;
            }
            else if (assetName.ValueKind == JsonValueKind.Object)
            {
                // Pick the first non-blank entry among the key aliases of this platform.
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


        // JSON and token helpers

        /// <summary>
        /// Returns the platform-key candidates for a combined platform key, tolerating the legacy x64 alias.
        /// </summary>
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

        /// <summary>
        /// Maps a legacy os or architecture token to its canonical alias, or returns the fallback for a blank value.
        /// </summary>
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

        /// <summary>
        /// Returns a string property of a JSON object, or an empty string when it is missing or not a string.
        /// </summary>
        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>
        /// Returns an integer property of a JSON object, or the fallback when it is missing or not an integer.
        /// </summary>
        private static int GetInt(JsonElement element, string name, int fallback) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;
    }
}
