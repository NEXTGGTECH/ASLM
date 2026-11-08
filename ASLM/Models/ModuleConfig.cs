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
        // Manifest properties

        /// <summary>
        /// Version of the manifest schema used for compatibility checks.
        /// </summary>
        [JsonPropertyName("fileVersion")]
        public int FileVersion { get; set; } = 1;

        /// <summary>
        /// Stable module identifier used throughout the application.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable module name shown in the UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short description of the module's purpose.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Version string of the packaged module.
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        /// <summary>
        /// Optional development-stage badge marking the module as beta, independent of trust and the update channel.
        /// Omitted from the manifest when false.
        /// </summary>
        [JsonPropertyName("is_beta")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsBeta { get; set; }

        /// <summary>
        /// Optional development-stage badge marking the module as experimental, independent of trust and the
        /// update channel. The manifest key keeps its established spelling <c>is_experemental</c>; it is omitted
        /// from the manifest when false.
        /// </summary>
        [JsonPropertyName("is_experemental")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsExperimental { get; set; }

        /// <summary>
        /// Module author displayed to the user.
        /// </summary>
        [JsonPropertyName("author")]
        public string Author { get; set; } = string.Empty;

        /// <summary>
        /// Module type used by the application to interpret the package.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        /// <summary>
        /// Optional category tags for future grouping or discovery (manifest-only metadata).
        /// </summary>
        [JsonPropertyName("category")]
        public List<string> Category { get; set; } = [];

        /// <summary>
        /// Indicates whether the module contributes a page to the shell UI.
        /// </summary>
        [JsonPropertyName("hasPage")]
        public bool HasPage { get; set; }

        /// <summary>
        /// Optional relative path to the main module icon.
        /// </summary>
        [JsonPropertyName("icon")]
        public string? Icon { get; set; }

        /// <summary>
        /// Resolved absolute path to the main module icon inside the module directory.
        /// </summary>
        [JsonIgnore]
        public string? IconFullPath => ResolveModuleAssetPath(Icon);

        /// <summary>
        /// Optional relative path to the sidebar icon.
        /// </summary>
        [JsonPropertyName("sidebarIcon")]
        public string? SidebarIcon { get; set; }

        /// <summary>
        /// Resolved absolute path to the sidebar icon inside the module directory.
        /// </summary>
        [JsonIgnore]
        public string? SidebarIconFullPath => ResolveModuleAssetPath(SidebarIcon);

        /// <summary>
        /// Remote source definition for the module package.
        /// </summary>
        [JsonPropertyName("source")]
        public ModuleSource Source { get; set; } = new();

        /// <summary>
        /// Optional module-level restriction, required by the v2 schema.
        /// </summary>
        [JsonPropertyName("supportedPlatforms")]
        public List<SupportedPlatform> SupportedPlatforms { get; set; } = [];

        /// <summary>
        /// Third-party engine manifests provided by this module (ModulesAPI v2 only).
        /// </summary>
        [JsonPropertyName("engines")]
        public List<EngineConfig> Engines { get; set; } = [];

        /// <summary>
        /// Update behavior for this module package.
        /// </summary>
        [JsonPropertyName("update")]
        public ModuleUpdateConfig Update { get; set; } = new();

        /// <summary>
        /// Engine, module, and model dependencies required by the module.
        /// </summary>
        [JsonPropertyName("dependencies")]
        public ModuleDependencies Dependencies { get; set; } = new();

        /// <summary>
        /// Commands used to prepare and launch the module.
        /// </summary>
        [JsonPropertyName("commands")]
        public ModuleCommands Commands { get; set; } = new();

        /// <summary>
        /// Declarative groups used to render user-configurable settings.
        /// </summary>
        [JsonPropertyName("settingCategories")]
        public List<ModuleSettingCategory> SettingCategories { get; set; } = [];

        /// <summary>
        /// User-configurable settings exposed by the module.
        /// </summary>
        [JsonPropertyName("settings")]
        public List<ModuleSetting> Settings { get; set; } = [];

        /// <summary>
        /// Optional bridge that exposes dynamic download catalogs and install plans to ASLM.
        /// </summary>
        [JsonPropertyName("downloadsBridge")]
        public ModuleDownloadsBridge? DownloadsBridge { get; set; }

        /// <summary>
        /// Optional declaration that the module consumes the host module interop API and env injection.
        /// </summary>
        [JsonPropertyName("moduleInterop")]
        public ModuleInteropManifest? ModuleInterop { get; set; }

        /// <summary>
        /// Persisted installation and runtime state of the module.
        /// </summary>
        [JsonPropertyName("status")]
        public ModuleStatus Status { get; set; } = new();


        // Runtime state

        /// <summary>
        /// Absolute path to the source manifest file resolved at runtime.
        /// </summary>
        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether the original manifest explicitly declared an update block.
        /// </summary>
        [JsonIgnore]
        public bool HasDeclaredUpdateConfig { get; set; }

        /// <summary>
        /// Whether the manifest supports the currently resolved host platform.
        /// </summary>
        [JsonIgnore]
        public bool IsSupportedOnCurrentPlatform { get; private set; } = true;

        /// <summary>
        /// Runtime intersection of module and required-engine platforms; never persisted to the manifest.
        /// </summary>
        [JsonIgnore]
        public IReadOnlyList<SupportedPlatform> EffectiveSupportedPlatforms { get; private set; } = [];

        /// <summary>
        /// Canonical platform key used for the latest compatibility resolution.
        /// </summary>
        [JsonIgnore]
        public string ActivePlatformKey { get; private set; } = string.Empty;

        /// <summary>
        /// Non-fatal schema diagnostics retained for logs and authoring tools.
        /// </summary>
        [JsonIgnore]
        public List<string> ValidationWarnings { get; set; } = [];


        // Normalization and platform resolution

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

            // Trim category tags and drop blank and case-insensitive duplicate ones.
            Category ??= [];
            Category = Category
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Normalize the platform restriction, then keep one entry per os/arch pair.
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

            // Normalize the engine definitions embedded in the module manifest.
            Engines ??= [];
            foreach (var engine in Engines)
            {
                engine?.Normalize();
            }

            // Keep only setting categories that have an id, since settings reference them by id.
            SettingCategories ??= [];
            foreach (var settingCategory in SettingCategories)
            {
                settingCategory?.Normalize();
            }

            SettingCategories = SettingCategories
                .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
                .ToList();

            // Treat blank icon paths as absent and make sure the runtime path is never null.
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
        /// Intersects declared module platforms, when present, with every required engine.
        /// Parsing alone resolves the module declaration; catalog/install callers supply engine lookup.
        /// </summary>
        /// <param name="osKey">Host operating system token, for example <c>windows</c>.</param>
        /// <param name="archKey">Host architecture token, for example <c>amd64</c>.</param>
        /// <param name="findEngine">Looks up a required engine by id; null skips the engine intersection.</param>
        public void ResolveForPlatform(string osKey, string archKey, Func<string, EngineConfig?>? findEngine = null)
        {
            // Start from the module's own platform declaration; null means it declares no restriction.
            ActivePlatformKey = $"{SupportedPlatform.CanonicalToken(osKey)}-{SupportedPlatform.CanonicalToken(archKey)}";
            List<SupportedPlatform>? platforms = SupportedPlatforms.Count > 0
                ? SupportedPlatforms.Select(platform => SupportedPlatform.FromKey(
                        $"{SupportedPlatform.CanonicalToken(platform.Os)}-{SupportedPlatform.CanonicalToken(platform.Arch)}"))
                    .DistinctBy(platform => platform.Key, StringComparer.OrdinalIgnoreCase).ToList()
                : null;

            // Narrow the platforms to those that every required engine supports as well.
            if (findEngine != null)
            {
                foreach (var engineId in Dependencies.Engines
                    .Where(dependency => dependency != null)
                    .Select(dependency => dependency.Id.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    // An unresolved required engine cannot confirm any compatible platform.
                    var enginePlatforms = findEngine(engineId)?.GetSupportedPlatforms() ?? [];
                    platforms = platforms == null
                        ? enginePlatforms.ToList()
                        : platforms.Where(platform => enginePlatforms.Any(enginePlatform =>
                            enginePlatform.Matches(platform.Os, platform.Arch))).ToList();
                }
            }

            // Without any restriction every platform is supported; otherwise the host must be in the result.
            EffectiveSupportedPlatforms = platforms ?? [];
            IsSupportedOnCurrentPlatform = platforms == null ||
                platforms.Any(platform => platform.Matches(osKey, archKey));
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
        // Constants

        /// <summary>
        /// Virtual picker value that tracks the newest allowed GitHub release.
        /// </summary>
        public const string LatestReleaseTag = "latest";


        // Update settings

        /// <summary>
        /// Update mode: release assets/release archive, pre-release stream, or a repository branch archive.
        /// </summary>
        [JsonPropertyName("mode")]
        public string Mode { get; set; } = "release";

        /// <summary>
        /// Release channel: release only, or release plus pre-release.
        /// </summary>
        [JsonPropertyName("channel")]
        public string Channel { get; set; } = "release";

        /// <summary>
        /// Catalog entries follow ASLM's default until the user saves their own selection.
        /// Installed modules always retain their own update configuration.
        /// </summary>
        [JsonPropertyName("useDefaultChannel")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? UseDefaultChannel { get; set; }

        /// <summary>
        /// Branch selected when mode is branch.
        /// </summary>
        [JsonPropertyName("branch")]
        public string Branch { get; set; } = "main";

        /// <summary>
        /// Optional release asset name. When absent, release zipball is used for modules.
        /// </summary>
        [JsonPropertyName("assetName")]
        public string? AssetName { get; set; }

        /// <summary>
        /// Relative files and directories copied out before replacement and restored after update.
        /// </summary>
        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

        /// <summary>
        /// Indicates whether first-run setup should run again after files are replaced.
        /// </summary>
        [JsonPropertyName("runFirstRunAfterUpdate")]
        public bool RunFirstRunAfterUpdate { get; set; } = true;

        /// <summary>
        /// Commit identifying the current branch installation; null after installing a release.
        /// </summary>
        [JsonPropertyName("installedCommitSha")]
        public string? InstalledCommitSha { get; set; }

        /// <summary>
        /// Tag identifying the current release installation; null after installing a branch.
        /// </summary>
        [JsonPropertyName("installedReleaseTag")]
        public string? InstalledReleaseTag { get; set; }

        /// <summary>
        /// User-selected release tag used for installs, including rollbacks.
        /// </summary>
        [JsonPropertyName("selectedReleaseTag")]
        public string? SelectedReleaseTag { get; set; }

        /// <summary>
        /// Last known available update remembered for the module card badge across UI refresh and app restart.
        /// </summary>
        [JsonPropertyName("pendingUpdate")]
        public ModulePendingUpdate? PendingUpdate { get; set; }


        // Normalization

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

            // Keep only relative preserve paths that stay inside the module folder: drop blanks, "." and any path
            // containing "..", and use forward slashes without leading or trailing separators.
            Preserve ??= [];
            Preserve = Preserve
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path.Trim().Replace('\\', '/').Trim('/'))
                .Where(static path => path.Length > 0 && path != "." && !path.Contains("..", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // A pending update without a remote version cannot be shown or applied, so it is dropped.
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
            // An explicit mode wins over the channel.
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

            // Without a mode, older manifests signalled the pre-release stream through the channel only.
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
        // Snapshot properties

        /// <summary>
        /// Update mode (release, pre-release or branch) that was active when the snapshot was taken.
        /// A snapshot is ignored once the module switches to a different mode.
        /// </summary>
        [JsonPropertyName("updateMode")]
        public string UpdateMode { get; set; } = "release";

        /// <summary>
        /// Tracked branch of a branch-mode snapshot; null for release snapshots.
        /// </summary>
        [JsonPropertyName("branch")]
        public string? Branch { get; set; }

        /// <summary>
        /// Selected release tag (possibly the virtual latest alias) that the snapshot belongs to;
        /// null for branch snapshots.
        /// </summary>
        [JsonPropertyName("releaseSelectionKey")]
        public string? ReleaseSelectionKey { get; set; }

        /// <summary>
        /// Version text of the available update. A snapshot without it is discarded.
        /// </summary>
        [JsonPropertyName("remoteVersion")]
        public string RemoteVersion { get; set; } = string.Empty;

        /// <summary>
        /// Optional human-readable label of the update.
        /// </summary>
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        /// <summary>
        /// Git tag of the release that the update comes from, if any.
        /// </summary>
        [JsonPropertyName("releaseTag")]
        public string? ReleaseTag { get; set; }

        /// <summary>
        /// Commit that a branch update points to; compared with the installed commit to detect changes.
        /// </summary>
        [JsonPropertyName("commitSha")]
        public string? CommitSha { get; set; }

        /// <summary>
        /// Name of the Git reference (tag or branch) that the update was resolved from.
        /// </summary>
        [JsonPropertyName("referenceName")]
        public string? ReferenceName { get; set; }

        /// <summary>
        /// URL of the archive or release asset to download.
        /// </summary>
        [JsonPropertyName("downloadUrl")]
        public string? DownloadUrl { get; set; }

        /// <summary>
        /// Whether the update is the moving <c>latest</c> alias rather than a fixed release.
        /// </summary>
        [JsonPropertyName("isVirtualLatest")]
        public bool IsVirtualLatest { get; set; }

        /// <summary>
        /// Whether the update is a pre-release.
        /// </summary>
        [JsonPropertyName("isPrerelease")]
        public bool IsPrerelease { get; set; }

        /// <summary>
        /// Release channel of the update.
        /// </summary>
        [JsonPropertyName("channel")]
        public string? Channel { get; set; }

        /// <summary>
        /// UTC timestamp (round-trip format) at which the update was discovered.
        /// </summary>
        [JsonPropertyName("checkedUtc")]
        public string? CheckedUtc { get; set; }


        // Normalization and creation

        /// <summary>
        /// Trims optional fields and normalizes mode text after deserialization.
        /// </summary>
        public void Normalize()
        {
            // Blank optional fields become null so they are not persisted as empty text.
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
            // Branch updates remember the tracked branch; release updates remember the selected release instead.
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
        /// <summary>
        /// Source provider identifier. The default provider is GitHub.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "github";

        /// <summary>
        /// Repository path in provider-specific format, for example <c>User/Repo</c>.
        /// </summary>
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
        /// <summary>
        /// Required engine runtimes and their package dependencies.
        /// </summary>
        [JsonPropertyName("engines")]
        public List<ModuleEngineDependency> Engines { get; set; } = [];

        /// <summary>
        /// Required ASLM modules that must be installed and running before this module starts.
        /// </summary>
        [JsonPropertyName("modules")]
        public List<ModuleModuleDependency> Modules { get; set; } = [];

        /// <summary>
        /// Required model categories matched against installed models.
        /// </summary>
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

            // Recreate and normalize module dependencies when needed.
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


    // Module-to-module dependencies

    /// <summary>
    /// Describes the dependency on another installed ASLM module.
    /// </summary>
    public class ModuleModuleDependency
    {
        /// <summary>
        /// Stable identifier of the required module (matches <see cref="ModuleConfig.Id"/>).
        /// </summary>
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
        /// <summary>
        /// Identifier of the required engine runtime.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Additional libraries that must be installed for this engine.
        /// </summary>
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
        /// <summary>
        /// Commands executed once during the initial module setup.
        /// </summary>
        [JsonPropertyName("firstRun")]
        public List<ModuleCommand> FirstRun { get; set; } = [];

        /// <summary>
        /// Commands executed during the normal module launch flow.
        /// </summary>
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
        /// <summary>
        /// Human-readable command name shown in the UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Short explanation of what the command does.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Engine identifier used to execute the command, if required.
        /// </summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        /// <summary>
        /// Execution string relative to the module directory.
        /// </summary>
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
        /// <summary>
        /// Stable category identifier that settings reference through <see cref="ModuleSetting.Category"/>.
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Title of the category group shown in the settings UI; falls back to the identifier when empty.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Optional help text shown with the category group.
        /// </summary>
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
        // Manifest properties

        /// <summary>
        /// Stable key used to read and persist the setting value.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable setting name shown in the UI.
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Help text that explains the setting to the user.
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Setting type that controls validation and rendering behavior (string, port, bool, theme, locale, …).
        /// Host-managed types are resolved by ASLM and applied through setExec without UI or environment injection.
        /// </summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        /// <summary>
        /// Default persisted value used when the current value is absent.
        /// </summary>
        [JsonPropertyName("default")]
        public object? Default { get; set; }

        /// <summary>
        /// Current persisted value for the setting.
        /// </summary>
        [JsonPropertyName("value")]
        public object? Value { get; set; }

        /// <summary>
        /// Indicates whether a system-managed setting should use a user-provided custom value.
        /// </summary>
        [JsonPropertyName("useCustomValue")]
        public bool UseCustomValue { get; set; }

        /// <summary>
        /// Allowed values for choice-based settings.
        /// </summary>
        [JsonPropertyName("allowedValues")]
        public List<string>? AllowedValues { get; set; }

        /// <summary>
        /// Engine identifier used to execute the get and set commands, if required.
        /// </summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        /// <summary>
        /// Command that reads the current value of the setting.
        /// </summary>
        [JsonPropertyName("getExec")]
        public string GetExec { get; set; } = string.Empty;

        /// <summary>
        /// Command that applies a new value to the setting.
        /// </summary>
        [JsonPropertyName("setExec")]
        public string SetExec { get; set; } = string.Empty;

        /// <summary>
        /// Optional visual category id for user-configurable settings.
        /// </summary>
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        /// <summary>
        /// Optional bool setting key that controls whether this setting is shown.
        /// </summary>
        [JsonPropertyName("dependsOn")]
        public string? DependsOn { get; set; }


        // Computed properties

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
        /// Gets whether ASLM must reconcile this host-controlled value before a module process starts.
        /// </summary>
        [JsonIgnore]
        public bool IsSynchronizedOnLaunch => NormalizedType is
            "port" or
            "theme" or
            "locale" or
            "key-aslm" or
            "key-gh" or
            "engine" or
            "path" or
            "data" or
            "models";

        /// <summary>
        /// Gets whether ASLM supplies an authenticated subsystem key for this setting.
        /// </summary>
        [JsonIgnore]
        public bool IsHostKey => NormalizedType is "key-aslm" or "key-gh";


        // Normalization

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


        // Input parsing and formatting

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

            // Blank input clears typed settings (booleans, numbers, JSON) but stays as text for string-like types.
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
        /// <param name="rawValue">Value produced by a settings control: text, a JSON element or an already typed value.</param>
        /// <returns>The value in the representation that is saved to the manifest.</returns>
        public object? NormalizeUserValue(object? rawValue)
        {
            // Text is parsed by the declared type, JSON scalars are unwrapped, other values pass through.
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
        /// <param name="rawValue">Serialized value, for example an environment variable text or a command output.</param>
        /// <returns>The typed value, or the original text when it does not fit the declared type.</returns>
        public object? ParseSerializedValue(string? rawValue)
        {
            if (rawValue is null)
            {
                return null;
            }

            // Numbers are parsed with the invariant culture; unparsable text is kept so no input is lost.
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
            // Unwrap JSON scalars first so the checks below see plain CLR values.
            value = NormalizeScalarValue(value);
            if (value is null)
            {
                return string.Empty;
            }

            // Text and raw JSON (objects, arrays) are shown as they are.
            if (value is string text)
            {
                return text;
            }

            if (value is JsonElement jsonElement)
            {
                return jsonElement.GetRawText();
            }

            // Booleans use lower-case JSON spelling and numbers the invariant culture, so output is locale independent.
            if (value is bool boolValue)
            {
                return boolValue ? "true" : "false";
            }

            if (value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            // Any other object is serialized as JSON.
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
                // Malformed JSON is not an error: the text is simply kept unchanged.
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
        /// <summary>
        /// Indicates whether the module manifest is present in the installed modules directory.
        /// </summary>
        [JsonPropertyName("installed")]
        public bool Installed { get; set; }

        /// <summary>
        /// Indicates whether the module is currently enabled.
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Indicates whether the module completed its first-run setup.
        /// </summary>
        [JsonPropertyName("firstRunCompleted")]
        public bool FirstRunCompleted { get; set; }

        /// <summary>
        /// Version string of the installed module, if known.
        /// </summary>
        [JsonPropertyName("installedVersion")]
        public string? InstalledVersion { get; set; }

        /// <summary>
        /// Timestamp of the latest update check.
        /// </summary>
        [JsonPropertyName("lastChecked")]
        public string? LastChecked { get; set; }

        /// <summary>
        /// Timestamp of the latest successful update.
        /// </summary>
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
