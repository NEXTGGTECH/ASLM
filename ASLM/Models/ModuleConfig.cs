// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace ASLM.Models
{
    // Module manifest

    /// <summary>
    /// Describes an installable module loaded from <c>ASLM_Module.json</c>.
    /// </summary>
    public class ModuleConfig
    {
        // Version of the manifest schema used for compatibility checks.
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        // Stable module identifier used throughout the application.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Human-readable module name shown in the UI.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Short description of the module's purpose.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Version string of the packaged module.
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        // Module author displayed to the user.
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        // Module type used by the application to interpret the package.
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        // Optional category tags for future grouping or discovery (manifest-only metadata).
        [JsonPropertyName("category")]
        public List<string> Category { get; set; } = [];

        // Indicates whether the module contributes a page to the shell UI.
        [JsonPropertyName("hasPage")]
        public bool HasPage { get; set; }

        // Optional relative path to the main module icon.
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        // Resolved absolute path to the main module icon inside the module directory.
        [JsonIgnore]
        public string? IconFullPath => ResolveModuleAssetPath(Icon);

        // Optional relative path to the sidebar icon.
        [JsonPropertyName("sidebarIcon")]
        public string? SidebarIcon { get; set; }

        // Resolved absolute path to the sidebar icon inside the module directory.
        [JsonIgnore]
        public string? SidebarIconFullPath => ResolveModuleAssetPath(SidebarIcon);

        // Remote source definition for the module package.
        [JsonPropertyName("source")]
        public ModuleSource Source { get; set; } = new();

        // Platforms supported by ModulesAPI v2 manifests. Legacy v1 modules are platform agnostic.
        [JsonPropertyName("supportedPlatforms")]
        public List<SupportedPlatform> SupportedPlatforms { get; set; } = [];

        // Third-party engine manifests provided by this module (ModulesAPI v2 only).
        [JsonPropertyName("engines")]
        public List<EngineConfig> Engines { get; set; } = [];

        // Update behavior for this module package.
        [JsonPropertyName("update")]
        public ModuleUpdateConfig Update { get; set; } = new();

        // Engine, module, and model dependencies required by the module.
        [JsonPropertyName("dependencies")]
        public ModuleDependencies Dependencies { get; set; } = new();

        // Commands used to prepare and launch the module.
        [JsonPropertyName("commands")]
        public ModuleCommands Commands { get; set; } = new();

        // Declarative groups used to render user-configurable settings.
        [JsonPropertyName("settingCategories")]
        public List<ModuleSettingCategory> SettingCategories { get; set; } = [];

        // User-configurable settings exposed by the module.
        [JsonPropertyName("settings")]
        public List<ModuleSetting> Settings { get; set; } = [];

        // Optional bridge that exposes dynamic download catalogs and install plans to ASLM.
        [JsonPropertyName("downloadsBridge")]
        public ModuleDownloadsBridge? DownloadsBridge { get; set; }

        // Optional declaration that the module consumes the host module interop API and env injection.
        [JsonPropertyName("moduleInterop")]
        public ModuleInteropManifest? ModuleInterop { get; set; }

        // Persisted installation and runtime state of the module.
        [JsonPropertyName("status")]
        public ModuleStatus Status { get; set; } = new();

        // Absolute path to the source manifest file resolved at runtime.
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

        // Indicates whether the original manifest explicitly declared an update block.
        [JsonIgnore]
        public bool HasDeclaredUpdateConfig { get; set; }

        // Whether the manifest supports the currently resolved host platform.
        [JsonIgnore]
        public bool IsSupportedOnCurrentPlatform { get; private set; } = true;

        // Canonical platform key used for the latest compatibility resolution.
        [JsonIgnore]
        public string ActivePlatformKey { get; private set; } = string.Empty;

        // Non-fatal schema diagnostics retained for logs and authoring tools.
        [JsonIgnore]
        public List<string> ValidationWarnings { get; set; } = [];

        /// <summary>
        /// Restores nested objects, collections, and strings after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep scalar string fields safe for downstream code and UI bindings.
            Id ??= string.Empty;
            Name ??= string.Empty;
            Description ??= string.Empty;
            Version ??= string.Empty;
            Author ??= string.Empty;
            Type ??= string.Empty;
            Category ??= [];
            Category = Category
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            SupportedPlatforms ??= [];
            foreach (var platform in SupportedPlatforms)
            {
                platform?.Normalize();
            }

            SupportedPlatforms = SupportedPlatforms
                .Where(static platform => platform != null &&
                    !string.IsNullOrWhiteSpace(platform.Os) &&
                    !string.IsNullOrWhiteSpace(platform.Arch))
                .GroupBy(static platform => $"{platform.Os}::{platform.Arch}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToList();

            Engines ??= [];
            foreach (var engine in Engines)
            {
                engine?.Normalize();
            }

            SettingCategories ??= [];
            foreach (var settingCategory in SettingCategories)
            {
                settingCategory?.Normalize();
            }

            SettingCategories = SettingCategories
                .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
                .ToList();
            Icon = string.IsNullOrWhiteSpace(Icon) ? null : Icon;
            SidebarIcon = string.IsNullOrWhiteSpace(SidebarIcon) ? null : SidebarIcon;
            SourcePath ??= string.Empty;

            // Recreate and normalize nested configuration blocks.
            Source ??= new();
            Source.Normalize();

            Dependencies ??= new();
            Dependencies.Normalize();

            Commands ??= new();
            Commands.Normalize();

            // Normalize every persisted module setting.
            Settings ??= [];
            foreach (var setting in Settings)
            {
                setting?.Normalize();
            }

            // Normalize the optional downloads bridge only when the manifest declares it.
            DownloadsBridge?.Normalize();

            // Normalize optional module interop client declaration.
            ModuleInterop?.Normalize();

            // Normalize module update preferences and preservation rules.
            Update ??= new();
            Update.Normalize();

            // Ensure persisted runtime status is always available.
            Status ??= new();
            Status.Normalize();

            ValidationWarnings ??= [];
        }

        /// <summary>
        /// Resolves whether this module supports one host os/architecture pair.
        /// Version 1 manifests remain compatible with every platform supported by ASLM.
        /// </summary>
        public void ResolveForPlatform(string osKey, string archKey)
        {
            ActivePlatformKey = $"{SupportedPlatform.CanonicalToken(osKey)}-{SupportedPlatform.CanonicalToken(archKey)}";
            IsSupportedOnCurrentPlatform = FileVersion < 2 ||
                SupportedPlatforms.Any(platform => platform.Matches(osKey, archKey));
        }


        // Asset resolution

        /// <summary>
        /// Resolves a module asset path and rejects paths that escape the module directory.
        /// </summary>
        private string? ResolveModuleAssetPath(string? relativePath)
        {
            // Asset resolution is only possible when both the relative asset path and manifest path exist.
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(SourcePath))
            {
                return null;
            }

            // Resolve the module directory from the manifest location.
            var moduleDir = Path.GetDirectoryName(SourcePath);
            if (string.IsNullOrWhiteSpace(moduleDir))
            {
                return null;
            }

            // Normalize both paths before comparing them to prevent directory traversal.
            var fullModuleDir = Path.GetFullPath(moduleDir);
            var fullPath = Path.GetFullPath(Path.Combine(fullModuleDir, relativePath));
            var moduleDirPrefix = fullModuleDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            return fullPath.StartsWith(moduleDirPrefix, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
    }


    // Module updates

    /// <summary>
    /// Describes how ASLM should check and apply updates for a module.
    /// </summary>
    public class ModuleUpdateConfig
    {
        // Virtual picker value that tracks the newest allowed GitHub release.
        public const string LatestReleaseTag = "latest";

        // Update mode: release assets/release archive, pre-release stream, or a repository branch archive.
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "release";

        // Release channel: release only, or release plus pre-release.
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = "release";

        // Branch selected when mode is branch.
        [JsonPropertyName("branch")]
        public string Branch { get; set; } = "main";

        // Optional release asset name. When absent, release zipball is used for modules.
        [JsonPropertyName("assetName")]
        public string? AssetName { get; set; }

        // Relative files and directories copied out before replacement and restored after update.
        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

        // Indicates whether first-run setup should run again after files are replaced.
        [JsonPropertyName("runFirstRunAfterUpdate")]
        public bool RunFirstRunAfterUpdate { get; set; } = true;

        // Last branch commit installed by the updater.
        [JsonPropertyName("installedCommitSha")]
        public string? InstalledCommitSha { get; set; }

        // Last release tag installed by the updater.
        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        // User-selected release tag used for installs, including rollbacks.
        [JsonPropertyName("selectedReleaseTag")]
        public string? SelectedReleaseTag { get; set; }

        /// <summary>
        /// Last known available update remembered for the module card badge across UI refresh and app restart.
        /// </summary>
        [JsonPropertyName("pendingUpdate")]
        public ModulePendingUpdate? PendingUpdate { get; set; }

        /// <summary>
        /// Restores defaults and removes unsafe empty preservation entries.
        /// </summary>
        public void Normalize()
        {
            // Keep backward compatibility with the earlier shape where pre-release was stored in Channel.
            Mode = NormalizeMode(Mode, Channel);
            Channel = Mode == "pre-release" ? "pre-release" : "release";
            Branch = string.IsNullOrWhiteSpace(Branch) ? "main" : Branch.Trim();
            AssetName = string.IsNullOrWhiteSpace(AssetName) ? null : AssetName.Trim();
            InstalledCommitSha = string.IsNullOrWhiteSpace(InstalledCommitSha) ? null : InstalledCommitSha.Trim();
            InstalledReleaseTag = string.IsNullOrWhiteSpace(InstalledReleaseTag) ? null : InstalledReleaseTag.Trim();
            SelectedReleaseTag = NormalizeSelectedReleaseTag(SelectedReleaseTag);

            Preserve ??= [];
            Preserve = Preserve
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path.Trim().Replace('\\', '/').Trim('/'))
                .Where(static path => path.Length > 0 && path != "." && !path.Contains("..", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (PendingUpdate != null)
            {
                PendingUpdate.Normalize();
                if (string.IsNullOrWhiteSpace(PendingUpdate.RemoteVersion))
                {
                    PendingUpdate = null;
                }
            }
        }

        /// <summary>
        /// Normalizes the requested update mode while preserving legacy pre-release channel settings.
        /// </summary>
        private static string NormalizeMode(string? mode, string? channel)
        {
            if (string.Equals(mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                return "branch";
            }

            if (IsPrereleaseValue(mode))
            {
                return "pre-release";
            }

            if (string.Equals(mode, "release", StringComparison.OrdinalIgnoreCase))
            {
                return "release";
            }

            if (string.IsNullOrWhiteSpace(mode) && IsPrereleaseValue(channel))
            {
                return "pre-release";
            }

            return "release";
        }

        /// <summary>
        /// Normalizes the selected release marker while preserving the virtual latest option.
        /// </summary>
        private static string? NormalizeSelectedReleaseTag(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim();
            return string.Equals(normalized, LatestReleaseTag, StringComparison.OrdinalIgnoreCase)
                ? LatestReleaseTag
                : normalized;
        }

        /// <summary>
        /// Returns whether the provided text represents the pre-release stream.
        /// </summary>
        private static bool IsPrereleaseValue(string? value)
        {
            return string.Equals(value, "pre-release", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "prerelease", StringComparison.OrdinalIgnoreCase);
        }
    }


    /// <summary>
    /// Persisted snapshot of a discovered module update used to restore the card &quot;Update&quot; badge.
    /// </summary>
    public sealed class ModulePendingUpdate
    {
        [JsonPropertyName("updateMode")]
        public string UpdateMode { get; set; } = "release";

        [JsonPropertyName("branch")]
        public string? Branch { get; set; }

        [JsonPropertyName("releaseSelectionKey")]
        public string? ReleaseSelectionKey { get; set; }

        [JsonPropertyName("remoteVersion")]
        public string RemoteVersion { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("releaseTag")]
        public string? ReleaseTag { get; set; }

        [JsonPropertyName("commitSha")]
        public string? CommitSha { get; set; }

        [JsonPropertyName("referenceName")]
        public string? ReferenceName { get; set; }

        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonPropertyName("isVirtualLatest")]
        public bool IsVirtualLatest { get; set; }

        [JsonPropertyName("isPrerelease")]
        public bool IsPrerelease { get; set; }

        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        [JsonPropertyName("checkedUtc")]
        public string? CheckedUtc { get; set; }

        /// <summary>
        /// Trims optional fields and normalizes mode text after deserialization.
        /// </summary>
        public void Normalize()
        {
            UpdateMode = string.IsNullOrWhiteSpace(UpdateMode) ? "release" : UpdateMode.Trim();
            Branch = string.IsNullOrWhiteSpace(Branch) ? null : Branch.Trim();
            ReleaseSelectionKey = string.IsNullOrWhiteSpace(ReleaseSelectionKey) ? null : ReleaseSelectionKey.Trim();
            RemoteVersion = RemoteVersion?.Trim() ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim();
            ReleaseTag = string.IsNullOrWhiteSpace(ReleaseTag) ? null : ReleaseTag.Trim();
            CommitSha = string.IsNullOrWhiteSpace(CommitSha) ? null : CommitSha.Trim();
            ReferenceName = string.IsNullOrWhiteSpace(ReferenceName) ? null : ReferenceName.Trim();
            DownloadUrl = string.IsNullOrWhiteSpace(DownloadUrl) ? null : DownloadUrl.Trim();
            Channel = string.IsNullOrWhiteSpace(Channel) ? null : Channel.Trim();
            CheckedUtc = string.IsNullOrWhiteSpace(CheckedUtc) ? null : CheckedUtc.Trim();
        }

        /// <summary>
        /// Builds a persisted snapshot from the live update candidate and current update preferences.
        /// </summary>
        public static ModulePendingUpdate FromCandidate(ModuleConfig module, UpdateCandidate candidate)
        {
            var isBranch = string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase);
            return new ModulePendingUpdate
            {
                UpdateMode = module.Update.Mode,
                Branch = isBranch ? module.Update.Branch : null,
                ReleaseSelectionKey = isBranch ? null : module.Update.SelectedReleaseTag,
                RemoteVersion = candidate.RemoteVersion,
                DisplayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? null : candidate.DisplayName,
                ReleaseTag = candidate.ReleaseTag,
                CommitSha = candidate.CommitSha,
                ReferenceName = candidate.ReferenceName,
                DownloadUrl = string.IsNullOrWhiteSpace(candidate.DownloadUrl) ? null : candidate.DownloadUrl,
                IsVirtualLatest = candidate.IsVirtualLatest,
                IsPrerelease = candidate.IsPrerelease,
                Channel = string.IsNullOrWhiteSpace(candidate.Channel) ? null : candidate.Channel,
                CheckedUtc = DateTime.UtcNow.ToString("o")
            };
        }
    }


    // Module source

    /// <summary>
    /// Describes where a module package is hosted.
    /// </summary>
    public class ModuleSource
    {
        // Source provider identifier. The default provider is GitHub.
        [JsonPropertyName("type")]
        public string Type { get; set; } = "github";

        // Repository path in provider-specific format, for example "User/Repo".
        [JsonPropertyName("repo")]
        public string Repo { get; set; } = string.Empty;

        /// <summary>
        /// Restores required string values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep source values non-null and preserve the expected default provider.
            Type = string.IsNullOrWhiteSpace(Type) ? "github" : Type;
            Repo ??= string.Empty;
        }
    }


    // Module dependencies

    /// <summary>
    /// Declares the engines, modules, and model categories required by a module.
    /// </summary>
    public class ModuleDependencies
    {
        // Required engine runtimes and their package dependencies.
        [JsonPropertyName("engines")]
        public List<ModuleEngineDependency> Engines { get; set; } = [];

        // Required ASLM modules that must be installed and running before this module starts.
        [JsonPropertyName("modules")]
        public List<ModuleModuleDependency> Modules { get; set; } = [];

        // Required model categories matched against installed models.
        [JsonPropertyName("models")]
        public List<string> Models { get; set; } = [];

        /// <summary>
        /// Restores dependency collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Recreate and normalize engine dependencies when needed.
            Engines ??= [];
            foreach (var engine in Engines)
            {
                engine?.Normalize();
            }

            Modules ??= [];
            foreach (var module in Modules)
            {
                module?.Normalize();
            }

            // Remove empty model category entries while preserving valid ones.
            Models ??= [];
            Models = Models
                .Where(static model => !string.IsNullOrWhiteSpace(model))
                .ToList();
        }
    }


    // Module dependencies

    /// <summary>
    /// Describes the dependency on another installed ASLM module.
    /// </summary>
    public class ModuleModuleDependency
    {
        // Stable identifier of the required module (matches <see cref="ModuleConfig.Id"/>).
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Restores module dependency values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id ??= string.Empty;
            Id = Id.Trim();
        }
    }


    // Engine dependencies

    /// <summary>
    /// Describes the dependency on a specific engine runtime.
    /// </summary>
    public class ModuleEngineDependency
    {
        // Identifier of the required engine runtime.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Additional libraries that must be installed for this engine.
        [JsonPropertyName("libraries")]
        public List<string> Libraries { get; set; } = [];

        /// <summary>
        /// Restores engine dependency values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep the dependency identifier safe for lookup logic.
            Id ??= string.Empty;

            // Remove empty library entries while preserving valid ones.
            Libraries ??= [];
            Libraries = Libraries
                .Where(static library => !string.IsNullOrWhiteSpace(library))
                .ToList();
        }
    }


    // Module commands

    /// <summary>
    /// Groups the commands used during module setup and normal execution.
    /// </summary>
    public class ModuleCommands
    {
        // Commands executed once during the initial module setup.
        [JsonPropertyName("firstRun")]
        public List<ModuleCommand> FirstRun { get; set; } = [];

        // Commands executed during the normal module launch flow.
        [JsonPropertyName("run")]
        public List<ModuleCommand> Run { get; set; } = [];

        /// <summary>
        /// Restores command collections after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Recreate and normalize first-run commands when needed.
            FirstRun ??= [];
            foreach (var command in FirstRun)
            {
                command?.Normalize();
            }

            // Recreate and normalize launch commands when needed.
            Run ??= [];
            foreach (var command in Run)
            {
                command?.Normalize();
            }
        }
    }


    // Individual commands

    /// <summary>
    /// Describes one executable module command.
    /// </summary>
    public class ModuleCommand
    {
        // Human-readable command name shown in the UI.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Short explanation of what the command does.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Engine identifier used to execute the command, if required.
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        // Execution string relative to the module directory.
        [JsonPropertyName("exec")]
        public string Exec { get; set; } = string.Empty;

        /// <summary>
        /// Restores command values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep command fields non-null for process construction.
            Name ??= string.Empty;
            Description ??= string.Empty;
            Engine ??= string.Empty;
            Exec ??= string.Empty;
        }
    }


    // Module settings

    /// <summary>
    /// Describes one visual category for user-configurable module settings.
    /// </summary>
    public sealed class ModuleSettingCategory
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Normalizes category text so rendering and lookup use stable values.
        /// </summary>
        public void Normalize()
        {
            Id = (Id ?? string.Empty).Trim();
            Name = string.IsNullOrWhiteSpace(Name) ? Id : Name.Trim();
            Description = (Description ?? string.Empty).Trim();
        }
    }


    // Individual module settings

    /// <summary>
    /// Describes one user-configurable module setting.
    /// </summary>
    public class ModuleSetting
    {
        // Stable key used to read and persist the setting value.
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        // Human-readable setting name shown in the UI.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Help text that explains the setting to the user.
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        // Setting type that controls validation and rendering behavior (string, port, bool, theme, locale, …).
        // Host-managed types are resolved by ASLM and applied through setExec without UI or environment injection.
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        // Default persisted value used when the current value is absent.
        [JsonPropertyName("default")]
        public object? Default { get; set; }

        // Current persisted value for the setting.
        [JsonPropertyName("value")]
        public object? Value { get; set; }

        // Indicates whether a system-managed setting should use a user-provided custom value.
        [JsonPropertyName("useCustomValue")]
        public bool UseCustomValue { get; set; }

        // Allowed values for choice-based settings.
        [JsonPropertyName("allowedValues")]
        public List<string>? AllowedValues { get; set; }

        // Engine identifier used to execute the get and set commands, if required.
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        // Command that reads the current value of the setting.
        [JsonPropertyName("getExec")]
        public string GetExec { get; set; } = string.Empty;

        // Command that applies a new value to the setting.
        [JsonPropertyName("setExec")]
        public string SetExec { get; set; } = string.Empty;

        // Optional visual category id for user-configurable settings.
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        // Optional bool setting key that controls whether this setting is shown.
        [JsonPropertyName("dependsOn")]
        public string? DependsOn { get; set; }

        /// <summary>
        /// Gets the normalized setting type used by parsing and rendering logic.
        /// </summary>
        [JsonIgnore]
        public string NormalizedType => Type.Trim().ToLowerInvariant();

        /// <summary>
        /// Gets whether the setting is normally managed by ASLM unless a custom override is enabled.
        /// </summary>
        [JsonIgnore]
        public bool IsAutomaticallyManaged => NormalizedType is "path" or "data" or "models";

        /// <summary>
        /// Gets whether ASLM supplies an authenticated subsystem key for this setting.
        /// </summary>
        [JsonIgnore]
        public bool IsHostKey => NormalizedType is "key-aslm" or "key-gh";

        /// <summary>
        /// Restores string fields and normalizes persisted scalar values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Keep scalar string fields safe for UI binding and command generation.
            Key ??= string.Empty;
            Name ??= string.Empty;
            Description ??= string.Empty;
            Type = string.IsNullOrWhiteSpace(Type) ? "string" : Type;
            Engine ??= string.Empty;
            GetExec ??= string.Empty;
            SetExec ??= string.Empty;
            Category = string.IsNullOrWhiteSpace(Category) ? null : Category.Trim();
            DependsOn = string.IsNullOrWhiteSpace(DependsOn) ? null : DependsOn.Trim();

            // Convert JSON scalar wrappers into CLR values expected by the rest of the codebase.
            Default = NormalizeScalarValue(Default);
            Value = NormalizeScalarValue(Value);

            // Remove empty allowed values while preserving valid entries.
            if (AllowedValues != null)
            {
                AllowedValues = AllowedValues
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
            }
        }


        // Input parsing

        /// <summary>
        /// Converts raw settings UI input into the most suitable persisted scalar value.
        /// </summary>
        /// <param name="rawValue">The text entered by the user.</param>
        /// <returns>A scalar value suitable for JSON serialization.</returns>
        public object? ParseUserInput(string? rawValue)
        {
            // A missing text value should remain missing in the persisted payload.
            if (rawValue is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return NormalizedType switch
                {
                    "bool" or "engine" or "int" or "integer" or "long" or "float" or "double" or "number" or "port"
                        or "json" or "object" or "array" => null,
                    _ => rawValue
                };
            }

            // Parse well-known scalar types and fall back to the original text when parsing fails.
            return ParseSerializedValue(rawValue);
        }

        /// <summary>
        /// Normalizes values returned by controls before they are persisted.
        /// </summary>
        public object? NormalizeUserValue(object? rawValue)
        {
            return rawValue switch
            {
                null => null,
                string text => ParseUserInput(text),
                JsonElement jsonElement => NormalizeScalarValue(jsonElement),
                _ => rawValue
            };
        }

        /// <summary>
        /// Parses a textual setting value according to the declared setting type.
        /// </summary>
        public object? ParseSerializedValue(string? rawValue)
        {
            if (rawValue is null)
            {
                return null;
            }

            return NormalizedType switch
            {
                "bool" or "engine" => bool.TryParse(rawValue, out var boolValue) ? boolValue : rawValue,
                "int" or "integer" or "port" => int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue)
                    ? intValue
                    : rawValue,
                "long" => long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue)
                    ? longValue
                    : rawValue,
                "float" or "double" or "number" => double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue)
                    ? doubleValue
                    : rawValue,
                "json" or "object" or "array" => TryParseJsonValue(rawValue),
                _ => TryParseJsonValue(rawValue)
            };
        }

        /// <summary>
        /// Formats a persisted value for UI display and command arguments.
        /// </summary>
        public string FormatValueForDisplay(object? value)
        {
            value = NormalizeScalarValue(value);
            if (value is null)
            {
                return string.Empty;
            }

            if (value is string text)
            {
                return text;
            }

            if (value is JsonElement jsonElement)
            {
                return jsonElement.GetRawText();
            }

            if (value is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }

            if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            return JsonSerializer.Serialize(value);
        }


        // Scalar conversion

        /// <summary>
        /// Converts JSON scalar wrappers into CLR values used by the settings runtime.
        /// </summary>
        private static object? NormalizeScalarValue(object? value)
        {
            // Non-JSON values are already in their final CLR representation.
            if (value is not JsonElement jsonElement)
            {
                return value;
            }

            // Convert scalar JSON values while preserving unsupported payloads as raw text.
            return jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.Number when jsonElement.TryGetInt64(out var int64Value) => int64Value,
                JsonValueKind.Number when jsonElement.TryGetDouble(out var doubleValue) => doubleValue,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => jsonElement.GetRawText()
            };
        }

        /// <summary>
        /// Parses JSON objects and arrays while keeping plain text unchanged.
        /// </summary>
        private static object? TryParseJsonValue(string rawValue)
        {
            var trimmed = rawValue.Trim();
            if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
            {
                return rawValue;
            }

            try
            {
                using var jsonDocument = JsonDocument.Parse(trimmed);
                return jsonDocument.RootElement.Clone();
            }
            catch
            {
                return rawValue;
            }
        }
    }


    // Module status

    /// <summary>
    /// Tracks the persisted lifecycle state of a module package.
    /// </summary>
    public class ModuleStatus
    {
        // Indicates whether the module manifest is present in the installed modules directory.
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        // Indicates whether the module is currently enabled.
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        // Indicates whether the module completed its first-run setup.
        [JsonPropertyName("firstRunCompleted")]
        public bool FirstRunCompleted { get; set; }

        // Version string of the installed module, if known.
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        // Timestamp of the latest update check.
        [JsonPropertyName("lastChecked")]
        public string? LastChecked { get; set; }

        // Timestamp of the latest successful update.
        [JsonPropertyName("lastUpdated")]
        public string? LastUpdated { get; set; }

        /// <summary>
        /// Normalizes optional persisted values after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            // Empty text values are treated as missing persisted state.
            InstalledVersion = string.IsNullOrWhiteSpace(InstalledVersion) ? null : InstalledVersion;
            LastChecked = string.IsNullOrWhiteSpace(LastChecked) ? null : LastChecked;
            LastUpdated = string.IsNullOrWhiteSpace(LastUpdated) ? null : LastUpdated;
        }
    }
}
