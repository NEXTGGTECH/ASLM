// Copyright NEXTGGTECH. Apache License 2.0.

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ASLM.Models;
using ASLM.Localization;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Checks, prepares, and applies ASLM, module, and engine updates.
    /// This part holds update checks, ASLM self-update staging, catalog installation, module and engine
    /// application, and the shared version, path, and process helpers. Manual module actions, repository
    /// source resolution, and reinstallation live in the other parts of this class.
    /// </summary>
    public sealed partial class UpdateManager
    {
        // Fields and constants

        /// <summary>
        /// Name of the work folder under the application root that holds staged ASLM updates
        /// and the scratch folders of module and engine update attempts.
        /// </summary>
        private const string UpdateWorkDirName = ".aslm-update";

        /// <summary>
        /// Name of the marker file inside the work folder that describes the prepared ASLM update.
        /// </summary>
        private const string PendingFileName = "pending.json";

        /// <summary>
        /// Name of the shipped file that declares where ASLM itself is updated from.
        /// </summary>
        private const string UpdateSourceFileName = "ASLM_UpdateSource.json";

        /// <summary>
        /// Persisted application settings, including the selected update channel and installed release tag.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Installs, removes, and registers modules; owns the module catalog and content locks.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Discovers and installs engine runtimes and persists their manifests.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Starts and stops module processes and hosts the per-module console logs.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Launches a module or confirms it is running, used to start a freshly installed module and its dependencies.
        /// </summary>
        private readonly ModuleLaunchCoordinator _moduleLaunchCoordinator;

        /// <summary>
        /// Resolves module trust levels; its community-reviewed list is refreshed after a module update.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// Reconciles engines declared by modules after a module is installed or updated.
        /// </summary>
        private readonly ModuleEngineReconciler _moduleEngineReconciler;

        /// <summary>
        /// Settings store of the Ollama service; used to stop the runtime it started before engine files are replaced.
        /// </summary>
        private readonly OllamaSettingsStore _ollamaSettings;

        /// <summary>
        /// GitHub API client that lists releases, branches, and downloads with shared rate-limit accounting.
        /// </summary>
        private readonly GitHubUpdateClient _github;

        /// <summary>
        /// Notification center that receives update-available and update-result notifications.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Logger for update diagnostics.
        /// </summary>
        private readonly ILogger<UpdateManager> _logger;

        /// <summary>
        /// Queue that runs module install and removal jobs and tracks them by key.
        /// </summary>
        private readonly DownloadQueue _downloadQueue;

        /// <summary>
        /// Tracks running app and engine update operations so concurrent requests join one operation.
        /// </summary>
        private readonly UpdateOperationTracker _updateOperations = new();

        /// <summary>
        /// JSON settings for reading and writing update configuration and pending-update files.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };


        // Initialization

        /// <summary>
        /// Creates the update manager.
        /// </summary>
        public UpdateManager(
            AppDataStore appData,
            ModuleInstaller moduleInstaller,
            EngineInstaller engineInstaller,
            ModuleRunner moduleRunner,
            ModuleLaunchCoordinator moduleLaunchCoordinator,
            ModuleTrustService moduleTrustService,
            ModuleEngineReconciler moduleEngineReconciler,
            OllamaSettingsStore ollamaSettings,
            GitHubUpdateClient github,
            NotificationCenter notifications,
            ILogger<UpdateManager> logger,
            DownloadQueue downloadQueue)
        {
            _appData = appData;
            _moduleInstaller = moduleInstaller;
            _engineInstaller = engineInstaller;
            _moduleRunner = moduleRunner;
            _moduleLaunchCoordinator = moduleLaunchCoordinator;
            _moduleTrustService = moduleTrustService;
            _moduleEngineReconciler = moduleEngineReconciler;
            _ollamaSettings = ollamaSettings;
            _github = github;
            _notifications = notifications;
            _logger = logger;
            _downloadQueue = downloadQueue;
        }


        // State access

        /// <summary>
        /// Gets whether a prepared ASLM self-update is waiting for restart.
        /// </summary>
        public bool HasPendingAppUpdate => SupportsSelfUpdate && File.Exists(GetPendingUpdatePath());

        /// <summary>
        /// Enables in-place updates only on platforms with a packaged, verified launcher and patcher.
        /// </summary>
        public static bool SupportsSelfUpdate =>
#if LINUX
            LinuxAppRelauncher.SupportsSelfUpdate;
#else
            !OperatingSystem.IsLinux();
#endif

        /// <summary>
        /// Gets the version label of the running ASLM build shown in settings.
        /// </summary>
        public string CurrentAppVersion => ResolveCurrentAppDisplayVersion();

        /// <summary>
        /// Publishes live app and engine operation changes regardless of which caller initiated the update.
        /// </summary>
        internal event EventHandler? UpdateOperationStateChanged
        {
            add => _updateOperations.StateChanged += value;
            remove => _updateOperations.StateChanged -= value;
        }

        /// <summary>
        /// Returns the active update stage and download progress for one app or engine target.
        /// </summary>
        internal UpdateOperationSnapshot? GetUpdateOperation(string targetKind, string targetId)
        {
            return _updateOperations.GetSnapshot(NotificationCenter.BuildOperationKey(targetKind, targetId));
        }


        // Configuration

        /// <summary>
        /// Loads the shipped ASLM update source configuration.
        /// </summary>
        public async Task<AppUpdateSourceConfig?> LoadAppUpdateSourceAsync(CancellationToken ct = default)
        {
            var path = Path.Combine(GetRootDirectory(), "Data", "App", UpdateSourceFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<AppUpdateSourceConfig>(stream, _jsonOptions, ct);
            config?.Normalize();
            return config;
        }


        // Update checks

        /// <summary>
        /// Checks the configured GitHub release channel for an ASLM build update.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="publishUpdateNotification">When false, skips publishing an update-available notification (used when auto-updates will apply immediately).</param>
        /// <param name="isManualRequest">True for an explicit user request, so GitHub usage is attributed to manual instead of automatic checks.</param>
        public async Task<UpdateCandidate?> CheckAppUpdateAsync(
            CancellationToken ct = default,
            bool publishUpdateNotification = true,
            bool isManualRequest = false)
        {
            // Development packages without a compatible launcher/helper cannot apply self-updates.
            if (!SupportsSelfUpdate) return null;

            // Resolve channel, repository, and the platform-specific release asset name.
            var source = await LoadAppUpdateSourceAsync(ct);
            if (!IsValidAppGitHubSource(source))
            {
                return null;
            }

            var channel = string.IsNullOrWhiteSpace(_appData.Data.Updates.AppChannel)
                ? source!.DefaultChannel
                : _appData.Data.Updates.AppChannel;
            var includePrerelease = IsPrereleaseChannel(channel);
            var appSource = source!;
            var assetName = GetConfiguredAppAssetName(appSource);
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return null;
            }

            // Walk releases until a newer tag exposes the configured asset download URL.
            var releases = await _github.GetReleasesAsync(
                appSource.Source.Repo,
                includePrerelease,
                ResolveRequestSource(isManualRequest),
                ct);
            foreach (var release in releases)
            {
                var asset = release.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, assetName, StringComparison.OrdinalIgnoreCase));
                if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                {
                    continue;
                }

                if (!ShouldOfferAppUpdate(release.TagName))
                {
                    continue;
                }

                var candidate = new UpdateCandidate
                {
                    TargetKind = "app",
                    TargetId = "aslm",
                    Name = "ASLM",
                    CurrentVersion = _appData.Data.Updates.InstalledReleaseTag ?? string.Empty,
                    RemoteVersion = release.TagName,
                    Channel = includePrerelease ? "pre-release" : "release",
                    Mode = "release",
                    DownloadUrl = asset.BrowserDownloadUrl,
                    ReleaseTag = release.TagName,
                    IsPrerelease = release.Prerelease
                };

                if (publishUpdateNotification)
                {
                    _notifications.PublishUpdateCandidate(candidate);
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Checks one module for an available update.
        /// </summary>
        /// <param name="module">Installed module to check.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="publishUpdateNotification">When false, skips publishing an update-available notification (used when auto-updates will apply immediately).</param>
        /// <param name="isManualRequest">True for an explicit user request, so GitHub usage is attributed to manual instead of automatic checks.</param>
        public async Task<UpdateCandidate?> CheckModuleUpdateAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool publishUpdateNotification = true,
            bool isManualRequest = false)
        {
            // Hold the content lease while checking; only modules with a GitHub source can be checked.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            module.Normalize();
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(module.Source.Repo))
            {
                return null;
            }

            // Resolve unavailable streams identically for setup, manual and automatic updates.
            var options = await GetModuleDownloadOptionsAsync(module, ct, isManualRequest);
            options.ApplyTo(module);

            // Branch and release modes use different GitHub resolution strategies.
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                var branchCandidate = await CheckModuleBranchUpdateAsync(module, isManualRequest, ct);
                if (branchCandidate != null && publishUpdateNotification)
                {
                    _notifications.PublishUpdateCandidate(branchCandidate);
                }

                return branchCandidate;
            }

            // Release mode: compare the selected release with the installed one.
            var releaseCandidate = CheckModuleReleaseUpdate(module, options.Releases);
            if (releaseCandidate != null && publishUpdateNotification)
            {
                _notifications.PublishUpdateCandidate(releaseCandidate);
            }

            return releaseCandidate;
        }

        /// <summary>
        /// Checks one installed engine for an available GitHub release update.
        /// </summary>
        public async Task<UpdateCandidate?> CheckEngineUpdateAsync(
            EngineConfig engine,
            CancellationToken ct = default,
            bool publishUpdateNotification = true,
            bool isManualRequest = false)
        {
            // Only installed engines can be updated.
            engine.Normalize();
            if (!engine.Status.Installed)
            {
                return null;
            }

            // Older manifests may lack the update block; restore it and persist the manifest before reading it.
            if (EngineInstaller.EnsureManifestMetadata(engine) && !string.IsNullOrWhiteSpace(engine.SourcePath))
            {
                await _engineInstaller.SaveEngineConfigAsync(engine);
                _engineInstaller.InvalidateCache();
            }

            if (engine.Update == null ||
                string.IsNullOrWhiteSpace(engine.Update.Repo))
            {
                return null;
            }

            // Align the recorded release tag with the real runtime so the comparison below starts from the truth.
            await TrySyncEngineInstalledReleaseTagFromRuntimeAsync(engine, ct, isManualRequest);

            // Resolve the platform-specific asset name and the channel; engines follow the application channel.
            var assetName = GetConfiguredEngineAssetName(engine);
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return null;
            }

            var channel = string.IsNullOrWhiteSpace(_appData.Data.Updates.AppChannel)
                ? "release"
                : _appData.Data.Updates.AppChannel;
            var includePrerelease = IsPrereleaseChannel(channel);

            // Walk releases until a newer tag exposes the configured asset download URL.
            var releases = await _github.GetReleasesAsync(
                engine.Update.Repo,
                includePrerelease,
                ResolveRequestSource(isManualRequest),
                ct);

            foreach (var release in releases)
            {
                var asset = release.Assets.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, assetName, StringComparison.OrdinalIgnoreCase));
                if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                {
                    continue;
                }

                if (!ShouldOfferEngineUpdate(engine, release.TagName))
                {
                    continue;
                }

                // The first release that carries the asset and is newer than the installed tag becomes the candidate.
                var candidate = new UpdateCandidate
                {
                    TargetKind = "engine",
                    TargetId = engine.Id,
                    Name = engine.Name,
                    DisplayName = engine.Name,
                    CurrentVersion = ResolveInstalledEngineReleaseTag(engine),
                    RemoteVersion = release.TagName,
                    Channel = includePrerelease ? "pre-release" : "release",
                    Mode = "release",
                    DownloadUrl = asset.BrowserDownloadUrl,
                    ReleaseTag = release.TagName,
                    IsPrerelease = release.Prerelease,
                    PublishedAt = release.PublishedAt,
                    Engine = engine
                };

                if (publishUpdateNotification)
                {
                    _notifications.PublishUpdateCandidate(candidate);
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Resolves and persists the installed release tag from the engine runtime when possible.
        /// </summary>
        public async Task<string?> TrySyncEngineInstalledReleaseTagFromRuntimeAsync(
            EngineConfig engine,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            // Version probes must not rewrite engine metadata during a shared update operation.
            if (GetUpdateOperation("engine", engine.Id) != null)
            {
                return ResolveInstalledEngineReleaseTag(engine);
            }

            // Restore missing update metadata on older manifests before deciding whether a probe applies.
            engine.Normalize();
            if (EngineInstaller.EnsureManifestMetadata(engine) && !string.IsNullOrWhiteSpace(engine.SourcePath))
            {
                await _engineInstaller.SaveEngineConfigAsync(engine);
                _engineInstaller.InvalidateCache();
            }

            // Only installed engines with a GitHub update source have a release tag worth syncing.
            if (!engine.Status.Installed ||
                engine.Update == null ||
                string.IsNullOrWhiteSpace(engine.Update.Repo))
            {
                return ResolveInstalledEngineReleaseTag(engine);
            }

            // Probe the real runtime; an update that started during the probe makes the result unreliable.
            var installedTagBeforeProbe = ResolveInstalledEngineReleaseTag(engine);
            var probedVersion = await TryProbeEngineRuntimeVersionAsync(engine, ct);
            if (GetUpdateOperation("engine", engine.Id) != null || string.IsNullOrWhiteSpace(probedVersion))
            {
                return ResolveInstalledEngineReleaseTag(engine);
            }

            // An update may have both started and finished during the probe. Never persist its older result
            // over the installed version now held by the refreshed engine catalog.
            var currentEngine = _engineInstaller.DiscoverEngines().FirstOrDefault(current =>
                string.Equals(current.SourcePath, engine.SourcePath, StringComparison.OrdinalIgnoreCase)) ?? engine;
            var currentInstalledTag = ResolveInstalledEngineReleaseTag(currentEngine);
            if (!string.Equals(installedTagBeforeProbe, currentInstalledTag, StringComparison.OrdinalIgnoreCase))
            {
                return currentInstalledTag;
            }

            // Nothing to persist when the recorded tag already names the probed version.
            var normalizedTag = NormalizeEngineVersionReference(probedVersion);
            if (ReleaseTagOrdering.AreEquivalentVersionReferences(
                    engine.Status.InstalledReleaseTag ?? string.Empty,
                    normalizedTag))
            {
                return normalizedTag;
            }

            // Record the probed version as the installed tag and drop the cached engine catalog.
            engine.Status.InstalledReleaseTag = normalizedTag;
            engine.Status.InstalledVersion = normalizedTag;
            engine.Status.LastChecked = DateTime.UtcNow.ToString("o");
            await _engineInstaller.SaveEngineConfigAsync(engine);
            _engineInstaller.InvalidateCache();
            return normalizedTag;
        }

        /// <summary>
        /// Resolves the concrete install target that should be used for a module during setup.
        /// </summary>
        public async Task<UpdateCandidate?> ResolveModuleInstallCandidateAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            module.Normalize();
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(module.Source.Repo))
            {
                return null;
            }

            // Apply the effective channel first, then resolve through the branch or release strategy.
            var options = await GetModuleDownloadOptionsAsync(module, ct, isManualRequest);
            options.ApplyTo(module);
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                return await ResolveModuleBranchInstallCandidateAsync(module, isManualRequest, ct);
            }

            return ResolveModuleReleaseInstallCandidate(module, options.Releases);
        }

        /// <summary>
        /// Returns selectable release or pre-release versions for one module repository.
        /// </summary>
        public async Task<List<UpdateCandidate>> GetModuleReleaseCandidatesAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            var options = await GetModuleDownloadOptionsAsync(module, ct, isManualRequest);
            options.ApplyTo(module);
            return options.Releases.ToList();
        }

        /// <summary>
        /// Checks ASLM, every installed module, and every installed engine with an update source for available updates.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="publishUpdateNotifications">When false, skips publishing update-available notifications for every discovery (used when auto-updates will apply immediately).</param>
        /// <param name="isManualRequest">True for an explicit user request, so GitHub usage is attributed to manual instead of automatic checks.</param>
        public async Task<List<UpdateCandidate>> CheckAllUpdatesAsync(
            CancellationToken ct = default,
            bool publishUpdateNotifications = true,
            bool isManualRequest = false)
        {
            var candidates = new List<UpdateCandidate>();

            // ASLM check failures must not prevent module discovery from running.
            try
            {
                var appCandidate = await CheckAppUpdateAsync(ct, publishUpdateNotifications, isManualRequest);
                if (appCandidate != null)
                {
                    candidates.Add(appCandidate);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ASLM update check failed.");
            }

            // Each module is checked independently so one repository error does not block the rest.
            var modules = await _moduleInstaller.DiscoverInstalledModulesAsync();
            foreach (var module in modules)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var candidate = await CheckModuleUpdateAsync(module, ct, publishUpdateNotifications, isManualRequest);
                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Module update check failed for {ModuleId}.", module.Id);
                }
            }

            // Each installed engine with update metadata is checked independently.
            var engines = await DiscoverInstalledEnginesAsync(ct);
            foreach (var engine in engines)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var candidate = await CheckEngineUpdateAsync(engine, ct, publishUpdateNotifications, isManualRequest);
                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Engine update check failed for {EngineId}.", engine.Id);
                }
            }

            return candidates;
        }


        // Auto-apply

        /// <summary>
        /// Applies automatic updates for a list returned by <see cref="CheckAllUpdatesAsync"/>: modules and engines
        /// immediately, ASLM self-update prepared for the next launcher start when not already staged.
        /// </summary>
        public async Task ApplyDiscoveredUpdatesAsync(
            IReadOnlyList<UpdateCandidate> updates,
            IProgress<string>? log,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            foreach (var update in updates)
            {
                ct.ThrowIfCancellationRequested();

                // ASLM self-updates are staged once; modules are applied immediately.
                if (string.Equals(update.TargetKind, "app", StringComparison.OrdinalIgnoreCase))
                {
                    if (!HasPendingAppUpdate)
                    {
                        await PrepareAppUpdateAsync(update, log, null, isManualRequest, ct);
                    }

                    continue;
                }

                if (string.Equals(update.TargetKind, "module", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyModuleUpdateAsync(update, log, null, isManualRequest, ct);
                    continue;
                }

                if (string.Equals(update.TargetKind, "engine", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplyEngineUpdateAsync(update, log, null, isManualRequest, ct);
                }
            }
        }

        /// <summary>
        /// Returns every installed module manifest for background update scheduling.
        /// </summary>
        public Task<List<ModuleConfig>> DiscoverInstalledModulesAsync() =>
            _moduleInstaller.DiscoverInstalledModulesAsync();

        /// <summary>
        /// Returns every installed engine manifest that supports GitHub release updates.
        /// </summary>
        public Task<List<EngineConfig>> DiscoverInstalledEnginesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var engines = _engineInstaller.DiscoverEngines()
                .Where(engine =>
                    engine.Status.Installed &&
                    engine.Update != null &&
                    !string.IsNullOrWhiteSpace(engine.Update.Repo))
                .ToList();
            return Task.FromResult(engines);
        }

        /// <summary>
        /// Returns selectable branches for one module repository.
        /// </summary>
        public Task<List<GitHubBranchInfo>> GetModuleBranchesAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            return string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase)
                ? _github.GetBranchesAsync(module.Source.Repo, ResolveRequestSource(isManualRequest), ct)
                : Task.FromResult(new List<GitHubBranchInfo>());
        }

        /// <summary>
        /// Saves one module manifest after update preferences changed in UI.
        /// </summary>
        public void SaveModuleUpdatePreferences(ModuleConfig module)
        {
            module.Update.Normalize();
            module.Update.UseDefaultChannel = false;

            // Persist without ModulesChanged: the shell rebuilds module cards on that event and would drop
            // in-memory update-check results (HasUpdate / candidate) tied to the current ModuleViewModel instances.
            _moduleInstaller.SaveModuleUpdatePreferences(module);
        }


        // Pending update restoration

        /// <summary>
        /// Rebuilds a module <see cref="UpdateCandidate"/> from <see cref="ModuleUpdateConfig.PendingUpdate"/> when the
        /// persisted snapshot still reflects an update newer than the local installation.
        /// </summary>
        public bool TryRestorePendingUpdateCandidate(ModuleConfig module, [NotNullWhen(true)] out UpdateCandidate? candidate)
        {
            candidate = null;
            module.Normalize();

            // Reject empty or mode-mismatched pending snapshots before rebuilding a candidate.
            var pending = module.Update.PendingUpdate;
            if (pending == null || string.IsNullOrWhiteSpace(pending.RemoteVersion))
            {
                return false;
            }

            pending.Normalize();
            if (string.IsNullOrWhiteSpace(pending.RemoteVersion))
            {
                return false;
            }

            if (!string.Equals(pending.UpdateMode, module.Update.Mode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Branch mode: require a newer commit on the configured tracking branch.
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pending.CommitSha) || string.IsNullOrWhiteSpace(pending.Branch))
                {
                    return false;
                }

                if (!string.Equals(
                    pending.Branch.Trim(),
                    module.Update.Branch.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                candidate = new UpdateCandidate
                {
                    TargetKind = "module",
                    TargetId = module.Id,
                    Name = module.Name,
                    CurrentVersion = BuildModuleCurrentVersion(module),
                    RemoteVersion = pending.RemoteVersion,
                    Channel = "branch",
                    Mode = "branch",
                    ReferenceName = pending.ReferenceName ?? pending.Branch,
                    CommitSha = pending.CommitSha,
                    Module = module
                };

                // A snapshot that equals the installed commit is stale and must not surface as an update.
                if (IsModuleAlreadyAtInstallTarget(module, candidate))
                {
                    candidate = null;
                    return false;
                }

                return true;
            }

            // Release mode: tag must differ from installed and still match the user's selection policy.
            if (string.IsNullOrWhiteSpace(pending.ReleaseTag))
            {
                return false;
            }

            // A snapshot recorded for a different pinned release selection no longer applies.
            if (!IsLatestReleaseSelection(module.Update.SelectedReleaseTag) &&
                !string.IsNullOrWhiteSpace(module.Update.SelectedReleaseTag) &&
                !string.IsNullOrWhiteSpace(pending.ReleaseSelectionKey) &&
                !string.Equals(
                    pending.ReleaseSelectionKey.Trim(),
                    module.Update.SelectedReleaseTag.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Rebuild the presentation fields stored in the snapshot, falling back to the mode-derived channel.
            var displayName = pending.IsVirtualLatest
                ? ModuleUpdateConfig.LatestReleaseTag
                : (!string.IsNullOrWhiteSpace(pending.DisplayName) ? pending.DisplayName : pending.RemoteVersion);

            var channel = !string.IsNullOrWhiteSpace(pending.Channel)
                ? pending.Channel
                : (IsPrereleaseMode(module.Update.Mode) ? "pre-release" : "release");

            candidate = new UpdateCandidate
            {
                TargetKind = "module",
                TargetId = module.Id,
                Name = module.Name,
                DisplayName = displayName,
                CurrentVersion = BuildModuleCurrentVersion(module),
                RemoteVersion = pending.RemoteVersion,
                Channel = channel,
                Mode = module.Update.Mode,
                DownloadUrl = pending.DownloadUrl ?? string.Empty,
                ReleaseTag = pending.ReleaseTag,
                IsVirtualLatest = pending.IsVirtualLatest,
                IsPrerelease = pending.IsPrerelease,
                PublishedAt = null,
                Module = module
            };

            // A snapshot that equals the installed release is stale and must not surface as an update.
            if (IsModuleAlreadyAtInstallTarget(module, candidate))
            {
                candidate = null;
                return false;
            }

            return true;
        }


        // App update preparation

        /// <summary>
        /// Downloads and stages an ASLM app update for the external patcher.
        /// </summary>
        public Task<bool> PrepareAppUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
            if (!SupportsSelfUpdate)
                throw new PlatformNotSupportedException("This ASLM package must be updated using its distribution mechanism.");

            // Reserve the target before asynchronous work so manual and automatic requests cannot race.
            return _updateOperations.RunAsync(NotificationCenter.BuildOperationKey("app", candidate.TargetId),
                candidate.ReleaseTag ?? candidate.RemoteVersion,
                operation => PrepareAppUpdateCoreAsync(candidate, operation, log, progress, isManualRequest, ct), ct);
        }

        /// <summary>
        /// Stages one reserved ASLM update and publishes its transfer and preparation stages.
        /// </summary>
        private async Task<bool> PrepareAppUpdateCoreAsync(UpdateCandidate candidate,
            UpdateOperationTracker.Operation operation, IProgress<string>? log,
            IProgress<DownloadProgress>? progress, bool isManualRequest, CancellationToken ct)
        {
            // A completed competing request may already have produced the payload before this reservation.
            if (HasPendingAppUpdate)
            {
                return true;
            }

            // Surface download progress and validate the shipped update source before touching disk.
            var operationKey = NotificationCenter.BuildOperationKey("app-update", candidate.TargetId);
            await _notifications.StartDownloadAsync(
                operationKey,
                "Preparing ASLM update",
                $"{candidate.Name} {candidate.RemoteVersion}",
                candidate.TargetKind,
                candidate.TargetId);

            var source = await LoadAppUpdateSourceAsync(ct);
            if (source == null)
            {
                log?.Report("ASLM update source is not configured.");
                _notifications.FailDownload(operationKey, "ASLM update source is not configured.");
                return false;
            }

            // Stage under .aslm-update so the external patcher can apply on next launcher start.
            var rootDir = GetRootDirectory();
            var workDir = Path.Combine(rootDir, UpdateWorkDirName);
            var stagingRoot = Path.Combine(workDir, "staging", SanitizeFileName(candidate.RemoteVersion));
            var extractDir = Path.Combine(stagingRoot, "extract");
            var archivePath = Path.Combine(stagingRoot, GetArchiveFileName(candidate.DownloadUrl, "download"));
            var backupPath = Path.Combine(workDir, "backup", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

            // Start from an empty staging folder; stale content from an earlier attempt must not leak in.
            TryDeleteDirectory(stagingRoot);
            Directory.CreateDirectory(extractDir);

            // Progress is recorded in the shared operation first, then forwarded to the notification.
            var notificationProgress = operation.WrapProgress(
                _notifications.CreateDownloadProgressBridge(operationKey, progress));
            var prepared = false;
            var pendingTemporaryPath = GetPendingUpdatePath() + ".tmp";

            try
            {
#if LINUX
                // Installed files may be read-only even though the separate XDG profile is writable.
                LinuxAppRelauncher.EnsureUpdateTargetWritable();
#endif

                // Download, extract off the UI thread, then persist pending.json for the patcher.
                operation.SetPhase(UpdateOperationPhase.Downloading);
                await _github.DownloadFileAsync(
                    candidate.DownloadUrl,
                    archivePath,
                    log,
                    notificationProgress,
                    ResolveRequestSource(isManualRequest),
                    ct);

                // The package is complete: the remaining work extracts and stages it, so report the applying stage.
                operation.SetPhase(UpdateOperationPhase.Applying);
                var payloadDir = await Task.Run(() =>
                {
                    // Archive extraction and payload inspection can touch a lot of files, so keep it off the UI thread.
                    ExtractZipSafe(archivePath, extractDir);
                    return ResolveSinglePayloadDirectory(extractDir);
                }, ct);

                // macOS replaces its bundle; Linux replaces either its portable tree or the per-user program directories.
                var macBundleRoot = MacBundleLocator.FindBundleRoot();
                var targetRoot = macBundleRoot ?? rootDir;
                var preserve = macBundleRoot != null ? [] : source.Preserve;
#if LINUX
                targetRoot = LinuxAppRelauncher.InstallDirectory;
                LinuxAppRelauncher.ValidateUpdatePayload(payloadDir);
                preserve = LinuxAppRelauncher.GetUpdatePreservePaths(preserve);
#endif

                // Describe the staged payload for the external patcher: where it is, where it goes, what to keep,
                // and the downloaded archive that becomes the payload archive of a shared installation folder.
                var pending = new PendingAppUpdate
                {
                    Version = candidate.RemoteVersion,
                    StagingPath = payloadDir,
                    TargetRoot = targetRoot,
                    BackupPath = backupPath,
                    Preserve = preserve,
                    ArchivePath = archivePath,
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                };

                Directory.CreateDirectory(workDir);
                var pendingJson = JsonSerializer.Serialize(pending, _jsonOptions);

                // Publish the marker atomically: the launcher must never observe a truncated pending operation.
                await File.WriteAllTextAsync(pendingTemporaryPath, pendingJson, ct);
                File.Move(pendingTemporaryPath, GetPendingUpdatePath(), overwrite: true);
                prepared = true;
                log?.Report("ASLM update prepared. It will be applied on restart.");
                _notifications.CompleteDownload(operationKey, "ASLM update prepared.");
                return true;
            }
            catch (OperationCanceledException)
            {
                // Surface the cancellation in the notification, then let the caller observe it.
                _notifications.FailDownload(operationKey, "ASLM update download canceled.");
                throw;
            }
            catch
            {
                // Any other failure is reported to the user and still propagates to the caller.
                _notifications.FailDownload(operationKey, "ASLM update download failed.");
                throw;
            }
            finally
            {
                // A canceled or invalid package is not a pending update and must not accumulate on disk.
                if (!prepared) TryDeleteDirectory(stagingRoot);
                if (File.Exists(pendingTemporaryPath)) File.Delete(pendingTemporaryPath);
            }
        }


        // Catalog installation

        /// <summary>
        /// Applies the application channel preference to an unconfigured catalog module.
        /// </summary>
        internal void ApplyCatalogDefaults(ModuleConfig module, bool installed) =>
            ApplyCatalogDefaults(module, installed, _appData.Data.Updates.ModuleDefaultChannel);

        /// <summary>
        /// Preserves installed and explicitly configured modules when assigning catalog defaults.
        /// </summary>
        internal static void ApplyCatalogDefaults(ModuleConfig module, bool installed, string defaultChannel)
        {
            if (installed || module.Update.UseDefaultChannel == false) return;
            module.Update.Mode = module.Update.Channel = IsPrereleaseMode(defaultChannel) ? "pre-release" : "release";
            module.Update.SelectedReleaseTag ??= ModuleUpdateConfig.LatestReleaseTag;
            module.HasDeclaredUpdateConfig = true;
        }

        /// <summary>
        /// Returns the version shown for a catalog entry: the release an installation would select,
        /// or the manifest version when the entry is installed or the lookup fails.
        /// Presentation only: never replaces the local manifest or its installed version.
        /// </summary>
        internal async Task<string> GetModuleCatalogVersionAsync(
            ModuleConfig module, bool installed, CancellationToken ct = default)
        {
            if (!CanDownloadModule(module) || installed) return module.Version;
            try
            {
                // Presentation must not race the editable selectors or persist a fallback on its own.
                var draft = JsonSerializer.Deserialize<ModuleConfig>(JsonSerializer.Serialize(module))!;
                var options = await GetModuleDownloadOptionsAsync(draft, ct, isManualRequest: true);
                options.ApplyTo(draft);
                ct.ThrowIfCancellationRequested();
                return SelectModuleReleaseInstallTarget(draft, options.Releases)?.RemoteVersion ?? module.Version;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not refresh catalog version for {ModuleId}.", module.Id);
                return module.Version;
            }
        }

        /// <summary>
        /// Loads the catalog icon artwork of a module repository, or null when the lookup fails.
        /// Independent of release metadata so cached artwork can be displayed immediately.
        /// </summary>
        internal async Task<GitHubUpdateClient.ModuleArtwork?> GetModuleCatalogArtworkAsync(
            ModuleConfig module, CancellationToken ct = default)
        {
            if (!CanDownloadModule(module)) return null;
            try
            {
                return await _github.GetModuleArtworkAsync(module.Source.Repo, module.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not refresh catalog icon for {ModuleId}.", module.Id);

                // null means the lookup failed; an artwork result with no icon means the manifest has none.
                return null;
            }
        }

        /// <summary>
        /// Lists optional repository documents without requiring a local module installation.
        /// </summary>
        internal Task<IReadOnlyList<ModuleRepositoryDocument>> GetModuleDocumentsAsync(ModuleConfig module, CancellationToken ct) =>
            CanDownloadModule(module) ? _github.GetModuleDocumentsAsync(module.Source.Repo, ct)
                : Task.FromResult<IReadOnlyList<ModuleRepositoryDocument>>([]);

        /// <summary>
        /// Loads one selected document through the authenticated, quota-aware GitHub client.
        /// </summary>
        internal Task<string> GetModuleDocumentHtmlAsync(ModuleConfig module, ModuleRepositoryDocument document, CancellationToken ct) =>
            _github.GetModuleDocumentHtmlAsync(module.Source.Repo, document, ct);

        /// <summary>
        /// Accepts supported GitHub repository coordinates without path or query fragments.
        /// </summary>
        internal static bool CanDownloadModule(ModuleConfig module)
        {
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase)) return false;
            var parts = module.Source.Repo.Trim().Trim('/').Split('/');
            return parts.Length == 2 && parts.All(part => part.Length > 0 && part is not "." and not ".." &&
                part.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
        }

        /// <summary>
        /// Installs a catalog entry and its dependencies, then launches it through the shared module lifecycle.
        /// </summary>
        public Task<ModuleLaunchResult> InstallCatalogModuleAsync(ModuleConfig selected, IProgress<string> log,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<ModuleLaunchResult>(ct);
            _moduleInstaller.RecordManualRequest(selected.Id);

            // The prepare step creates the request; the queued work and the finish step consume it.
            ModuleInstaller.CatalogInstallation? request = null;
            return _downloadQueue.EnqueueAsync("module:" + selected.Id,
                token => InstallCatalogModuleCoreAsync(request!, log, progress, token), ct,
                kind: DownloadQueueKind.Modules,
                prepare: async token =>
                {
                    // Plan under the shared gate so removals and other plans see a consistent reservation.
                    await _moduleInstaller.CatalogPlanGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        // Resolve the entry and its dependency order from the current catalog.
                        var catalog = await _moduleInstaller.DiscoverModulesAsync().ConfigureAwait(false);
                        var module = catalog.SingleOrDefault(item => string.Equals(item.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, selected.Name));
                        var order = ModuleDependencyResolver.ExpandInstallOrder([module], catalog);
                        var registered = _moduleInstaller.Registry.ReadIds();

                        // Reject the whole plan up front if any module is unsupported or has no downloadable source.
                        foreach (var item in order)
                        {
                            ApplyCatalogDefaults(item, registered.Contains(item.Id));
                            if (!item.IsSupportedOnCurrentPlatform)
                                throw new InvalidOperationException(L.Get(LocalizationKeys.SetupWizard_ModuleUnsupported, PlatformInfo.PlatformKey));
                            if (!registered.Contains(item.Id) && !CanDownloadModule(item))
                                throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_SourceUnavailable, item.Name));
                        }

                        // Reserve the modules of the plan; the reservation lives until the finish step disposes it.
                        token.ThrowIfCancellationRequested();
                        request = _moduleInstaller.BeginCatalogInstallation(module, order);
                    }
                    finally { _moduleInstaller.CatalogPlanGate.Release(); }
                },
                finish: async canceled =>
                {
                    // Undo partial work after a cancellation, and always release the reservation.
                    if (request == null) return;
                    try
                    {
                        if (canceled) await _moduleInstaller.RollbackCatalogInstallationAsync(request, log).ConfigureAwait(false);
                    }
                    finally { request.Dispose(); }
                });
        }

        /// <summary>
        /// Reports both queued and active catalog installations.
        /// </summary>
        public bool IsCatalogInstallPending(string moduleId) => _downloadQueue.Contains("module:" + moduleId);

        /// <summary>
        /// Cancels a root catalog request through its dependency-aware queue.
        /// </summary>
        public void CancelCatalogInstall(string moduleId) => _downloadQueue.Cancel("module:" + moduleId);

        /// <summary>
        /// Reserves and queues removal without racing dependency installation plans.
        /// </summary>
        public async Task<string?> UninstallModuleAsync(ModuleConfig module)
        {
            Task<string?> job;

            // Publish the removal reservation and its queue task atomically with respect to install plans.
            await _moduleInstaller.CatalogPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Join a removal that is already queued instead of queueing a second one.
                if (_downloadQueue.GetTask("module-remove:" + module.Id) is Task<string?> existing) job = existing;
                else
                {
                    // Refuse to remove a module that other pending installations still need.
                    var consumers = _moduleInstaller.GetPendingConsumers(module.Id);
                    if (!string.IsNullOrEmpty(consumers))
                        throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveRequiredFormat, consumers));

                    // Mark the module as queued for removal; the state moves to removing when the queue starts the job
                    // and is cleared once the job finishes, whatever its outcome.
                    _moduleInstaller.SetRemovalState(module.Id, DownloadOperationState.Queued);
                    job = _downloadQueue.EnqueueAsync("module-remove:" + module.Id, async token =>
                    {
                        _moduleInstaller.SetRemovalState(module.Id, DownloadOperationState.Removing);
                        return await _moduleInstaller.UninstallQueuedAsync(module, token).ConfigureAwait(false);
                    }, kind: DownloadQueueKind.ModuleRemoval, removal: true,
                    finish: _ =>
                    {
                        _moduleInstaller.SetRemovalState(module.Id, null);
                        return Task.CompletedTask;
                    });
                }
            }
            finally { _moduleInstaller.CatalogPlanGate.Release(); }

            // Wait outside the plan gate so other plans are not blocked while files are removed.
            return await job.ConfigureAwait(false);
        }

        /// <summary>
        /// Installs a reserved dependency plan, optionally using an exact root target resolved before reinstallation.
        /// </summary>
        private async Task<ModuleLaunchResult> InstallCatalogModuleCoreAsync(ModuleInstaller.CatalogInstallation request, IProgress<string> log,
            IProgress<DownloadProgress>? progress, CancellationToken ct, UpdateCandidate? reinstallTarget = null)
        {
            // Do not hold a content-operation lease while waiting: removal needs exclusive file access.
            foreach (var pending in request.Plan)
                if (_downloadQueue.GetTask("module-remove:" + pending.Id) is { } removal)
                {
                    try { await removal.WaitAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Report(L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                    }
                }

            // Take the content lease for every module of the plan and start the maintenance console log.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, request.Plan.ToArray()).ConfigureAwait(false);
            var module = request.Root;
            var order = request.Plan;
            request.SetPhase(module.Id, DownloadOperationState.Running);
            ct.ThrowIfCancellationRequested();
            log = _moduleRunner.ConsoleStore.CreateMaintenanceLog(module, log, reset: true);
            log.Report(L.Get(LocalizationKeys.ModuleInfo_Downloading, module.Name));
            var registered = _moduleInstaller.Registry.ReadIds();

            // Install the plan in dependency order; modules that already finished their first run are skipped.
            foreach (var item in order)
            {
                ct.ThrowIfCancellationRequested();
                var current = await _moduleInstaller.LoadModuleConfig(item.SourcePath).ConfigureAwait(false);
                if (registered.Contains(item.Id) && current?.Status.FirstRunCompleted == true) continue;
                request.SetPhase(item.Id, DownloadOperationState.Running);
                ct.ThrowIfCancellationRequested();
                using var activity = _moduleRunner.ConsoleStore.BeginActivity(item.SourcePath, ModuleActivity.Installing);
                var isDownloaded = registered.Contains(item.Id);
                if (!isDownloaded) request.TrackNewContent(item);

                // Modules with a declared update source install through the shared update pipeline,
                // using the exact target resolved before a reinstall when one was supplied.
                if (!isDownloaded && item.HasDeclaredUpdateConfig)
                {
                    var candidate = (string.Equals(item.Id, reinstallTarget?.TargetId, StringComparison.OrdinalIgnoreCase)
                        ? reinstallTarget : await ResolveModuleInstallCandidateAsync(item, ct, isManualRequest: true))
                        ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_SourceUnavailable, item.Name));
                    try
                    {
                        var success = await ApplyModuleUpdateAsync(candidate, log, progress, isManualRequest: true, ct: ct);
                        ct.ThrowIfCancellationRequested();
                        if (!success)
                            throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_InstallFailed, item.Name));
                    }
                    finally
                    {
                        // Even a failed first-run must leave downloaded files visible and manageable.
                        var installed = await _moduleInstaller.LoadModuleConfig(item.SourcePath);
                        if (installed?.Status.Installed == true)
                            await _moduleInstaller.SaveConfigAsync(installed);
                    }

                    continue;
                }

                // Other modules are downloaded here (unless already present), then reconciled and run once.
                var key = NotificationCenter.BuildOperationKey("module-install", item.Id);
                await _notifications.StartDownloadAsync(key, L.Get(LocalizationKeys.ModuleInfo_Downloading, item.Name),
                    item.Name, "module", item.Id);
                try
                {
                    if (!isDownloaded)
                    {
                        var downloaded = await _moduleInstaller.DownloadSourceAsync(item, log,
                            _notifications.CreateDownloadProgressBridge(key, progress), ct);
                        ct.ThrowIfCancellationRequested();
                        if (!downloaded)
                            throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_InstallFailed, item.Name));
                    }

                    // Install the engines the module declares before its first run needs them.
                    var installed = await _moduleInstaller.LoadModuleConfig(item.SourcePath)
                        ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, item.Name));
                    await _moduleInstaller.ReconcileRequiredEnginesAsync(installed, log, progress, ct);
                    var success = await _moduleRunner.ExecuteFirstRunAsync(installed, log, ct);
                    await _moduleInstaller.SaveConfigAsync(installed);
                    ct.ThrowIfCancellationRequested();
                    if (!success)
                        throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_InstallFailed, item.Name));
                    _notifications.CompleteDownload(key, L.Get(LocalizationKeys.ModuleInfo_Installed));
                }
                catch (OperationCanceledException)
                {
                    // Mark the notification canceled and let the queue handle the rollback.
                    _notifications.FailDownload(key, L.Get(LocalizationKeys.ModuleInfo_Canceled));
                    throw;
                }
                catch (Exception ex)
                {
                    // Report the failure in the notification and stop the remaining plan.
                    _notifications.FailDownload(key, L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                    throw;
                }
            }

            // Launch only after every dependency and first-run step has succeeded. The coordinator
            // reloads the installed manifest and starts dependencies without duplicating running processes.
            ct.ThrowIfCancellationRequested();
            var launch = await _moduleLaunchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                module.SourcePath, log, ct);
            ct.ThrowIfCancellationRequested();
            return launch;
        }


        // Module and engine update application

        /// <summary>
        /// Downloads and applies one module update.
        /// The same pipeline performs a first installation when the module is not registered yet.
        /// </summary>
        public async Task<bool> ApplyModuleUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
            // Lock the module content and record whether this is an installation or an update.
            var module = candidate.Module ?? throw new InvalidOperationException(
                "Module update candidate does not contain module metadata.");
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            module.Normalize();
            var wasEnabled = module.Status.Enabled;
            var isInstallation = !_moduleInstaller.Registry.ReadIds().Contains(module.Id);
            using var activity = _moduleRunner.ConsoleStore.BeginActivity(module.SourcePath,
                isInstallation ? ModuleActivity.Installing : ModuleActivity.Updating);
            log = _moduleRunner.ConsoleStore.CreateMaintenanceLog(module, log, reset: true);

            // Skip download and file replacement when the candidate already matches local state.
            if (!isInstallation && IsModuleAlreadyAtInstallTarget(module, candidate))
            {
                log?.Report("The selected version is already installed.");
                return false;
            }

            // Show the transfer in the notification center and resolve the installed module directory.
            var operationKey = NotificationCenter.BuildOperationKey("module-update", module.Id);
            await _notifications.StartDownloadAsync(
                operationKey,
                isInstallation ? L.Get(LocalizationKeys.ModuleInfo_Downloading, module.Name) : "Updating module",
                $"{module.Name} {candidate.RemoteVersion}",
                candidate.TargetKind,
                candidate.TargetId);

            var moduleDir = Path.GetDirectoryName(module.SourcePath);
            if (string.IsNullOrWhiteSpace(moduleDir))
            {
                _notifications.FailDownload(operationKey, "Module update target could not be resolved.");
                return false;
            }

            // Use a unique scratch folder per attempt; the finally block below removes it.
            var updateRoot = Path.Combine(
                GetRootDirectory(),
                UpdateWorkDirName,
                "modules",
                module.Id,
                Guid.NewGuid().ToString("N"));
            var archivePath = Path.Combine(updateRoot, "module.zip");
            var extractDir = Path.Combine(updateRoot, "extract");

            try
            {
                Directory.CreateDirectory(extractDir);

                // Download, extract, and validate the archive manifest on a background thread.
                var notificationProgress = _notifications.CreateDownloadProgressBridge(operationKey, progress);
                await DownloadModuleArchiveAsync(
                    module,
                    candidate,
                    archivePath,
                    log,
                    notificationProgress,
                    isManualRequest,
                    ct);

                var preparedUpdate = await Task.Run(() =>
                {
                    // Extraction and manifest validation are local filesystem work and should not block the UI thread.
                    ExtractZipSafe(archivePath, extractDir);

                    var newManifestPath = FindUpdatedModuleManifest(extractDir);
                    var newConfig = LoadModuleConfigFromPath(newManifestPath);
                    return new PreparedModuleUpdate(
                        NewManifestPath: newManifestPath,
                        NewConfig: newConfig,
                        ModuleSourceDir: Path.GetDirectoryName(newManifestPath)!);
                }, ct);

                // Verify the archive really contains this module and supports this platform before stopping anything.
                var newConfig = preparedUpdate.NewConfig;
                if (newConfig == null || !string.Equals(newConfig.Id, module.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Downloaded module archive does not match the installed module id.");
                }

                if (!newConfig.IsSupportedOnCurrentPlatform)
                {
                    throw new PlatformNotSupportedException(
                        $"Module '{newConfig.Name}' does not support {PlatformInfo.PlatformKey}.");
                }

                // Stop the module and any orphaned processes holding locks under the install directory.
                if (wasEnabled)
                {
                    log?.Report($"Stopping {module.Name} before update...");
                    await _moduleRunner.StopModuleAsync(module.SourcePath);
                    module.Status.Enabled = false;
                    _moduleInstaller.SaveModuleConfig(module, raiseModulesChanged: false);
                }

                await StopProcessesRunningFromDirectoryAsync(moduleDir, log, ct);

                // Replace the module files with the prepared payload, run first-run setup if needed,
                // and restart the module when it was running before.
                var success = await ApplyPreparedModuleUpdateAsync(
                    module,
                    candidate,
                    preparedUpdate,
                    moduleDir,
                    wasEnabled,
                    isInstallation,
                    log,
                    ct);
                ct.ThrowIfCancellationRequested();

                // Report the outcome to the user.
                if (success)
                {
                    _notifications.CompleteDownload(operationKey, isInstallation
                        ? L.Get(LocalizationKeys.ModuleInfo_Installed) : $"{module.Name} updated successfully.");

                    // Refresh the signed community-reviewed list when the remote trust API is enabled.
                    await _moduleTrustService.RefreshReviewedListAsync(ct);
                }
                else
                {
                    _notifications.FailDownload(operationKey, isInstallation
                        ? L.Get(LocalizationKeys.ModuleInfo_InstallFailed, module.Name) : $"{module.Name} update failed.");
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                // Mark the notification canceled, then let the caller observe the cancellation.
                _notifications.FailDownload(operationKey, isInstallation
                    ? L.Get(LocalizationKeys.ModuleInfo_Canceled) : $"{module.Name} update canceled.");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Other failures are logged, shown to the user, and reported to the caller as a plain failure.
                _logger.LogError(ex, "Module update failed for {ModuleId}.", module.Id);
                log?.Report($"Module update failed: {ex.Message}");
                _notifications.FailDownload(operationKey, isInstallation
                    ? L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message) : $"Module update failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDeleteDirectory(updateRoot);
            }
        }

        /// <summary>
        /// Downloads and applies one engine runtime update; concurrent callers for the same engine share one run.
        /// </summary>
        public Task<bool> ApplyEngineUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
            // Engine reservations are independent, but every caller for the same engine joins one task.
            var engineId = candidate.Engine?.Id ?? candidate.TargetId;
            return _updateOperations.RunAsync(NotificationCenter.BuildOperationKey("engine", engineId),
                candidate.ReleaseTag ?? candidate.RemoteVersion,
                operation => ApplyEngineUpdateCoreAsync(candidate, operation, log, progress, isManualRequest, ct), ct);
        }

        /// <summary>
        /// Applies one reserved engine update: downloads the runtime, replaces it while the enabled modules are stopped,
        /// and restarts those modules afterwards.
        /// </summary>
        private async Task<bool> ApplyEngineUpdateCoreAsync(UpdateCandidate candidate,
            UpdateOperationTracker.Operation updateOperation, IProgress<string>? log,
            IProgress<DownloadProgress>? progress, bool isManualRequest, CancellationToken ct)
        {
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct).ConfigureAwait(false);
            var engine = candidate.Engine ?? throw new InvalidOperationException(
                "Engine update candidate does not contain engine metadata.");

            // A card may retain an old candidate after another caller finished updating this engine.
            engine = _engineInstaller.DiscoverEngines().FirstOrDefault(current =>
                string.Equals(current.SourcePath, engine.SourcePath, StringComparison.OrdinalIgnoreCase)) ?? engine;
            engine.Normalize();

            // Nothing to do when the refreshed engine already matches the candidate; this counts as success.
            if (IsEngineAlreadyAtInstallTarget(engine, candidate))
            {
                log?.Report("The selected version is already installed.");
                return true;
            }

            // Show the transfer in the notification center and resolve the engine directory.
            var operationKey = NotificationCenter.BuildOperationKey("engine-update", engine.Id);
            await _notifications.StartDownloadAsync(
                operationKey,
                "Updating engine",
                $"{engine.Name} {candidate.RemoteVersion}",
                candidate.TargetKind,
                candidate.TargetId);

            var engineDir = Path.GetDirectoryName(engine.SourcePath);
            if (string.IsNullOrWhiteSpace(engineDir))
            {
                _notifications.FailDownload(operationKey, "Engine update target could not be resolved.");
                return false;
            }

            // Use a unique scratch folder per attempt; the finally block below removes it.
            var runtimeDir = Path.Combine(engineDir, "runtime");
            var updateRoot = Path.Combine(
                GetRootDirectory(),
                UpdateWorkDirName,
                "engines",
                engine.Id,
                Guid.NewGuid().ToString("N"));
            var archivePath = Path.Combine(updateRoot, GetArchiveFileName(candidate.DownloadUrl, "engine"));
            List<string>? enabledModuleSourcePaths = null;
            var modulesWereStopped = false;

            try
            {
                // Download the runtime archive while the engine and its dependent modules keep running.
                log?.Report($"Downloading {engine.Name} {candidate.RemoteVersion}...");
                var notificationProgress = updateOperation.WrapProgress(
                    _notifications.CreateDownloadProgressBridge(operationKey, progress));
                updateOperation.SetPhase(UpdateOperationPhase.Downloading);
                await _github.DownloadFileAsync(
                    candidate.DownloadUrl,
                    archivePath,
                    log,
                    notificationProgress,
                    ResolveRequestSource(isManualRequest),
                    ct);

                updateOperation.SetPhase(UpdateOperationPhase.Applying);

                // Stop every enabled module and any process that holds files of the engine; the finally block
                // restarts the modules once the runtime is replaced or the update failed.
                enabledModuleSourcePaths = await CaptureEnabledModuleSourcePathsAsync(ct);
                await StopModulesForEngineUpdateAsync(engine, engineDir, enabledModuleSourcePaths, log, ct);
                modulesWereStopped = true;

                log?.Report($"Stopping {engine.Name} before update...");
                await StopProcessesRunningFromDirectoryAsync(engineDir, log, ct);

                // Replace the runtime folder content with the new archive (off the UI thread).
                await Task.Run(() =>
                {
                    if (Directory.Exists(runtimeDir))
                    {
                        ClearDirectory(runtimeDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    }
                    else
                    {
                        Directory.CreateDirectory(runtimeDir);
                    }

                    ExtractZipSafe(archivePath, runtimeDir);
                }, ct);

                EngineInstaller.EnsureExecutablePermission(engine);

                // Record the new release as installed and persist the manifest.
                var installedTag = candidate.ReleaseTag ?? candidate.RemoteVersion;
                engine.Status.Installed = true;
                engine.Status.InstalledReleaseTag = installedTag;
                engine.Status.InstalledVersion = installedTag;
                engine.Status.LastChecked = DateTime.UtcNow.ToString("o");
                engine.Status.InstalledManifestHash = EngineManifestFingerprint.Compute(engine);

                await _engineInstaller.SaveEngineConfigAsync(engine);
                _engineInstaller.InvalidateCache();

                log?.Report($"{engine.Name} updated successfully.");
                _notifications.CompleteDownload(operationKey, $"{engine.Name} updated successfully.");
                return true;
            }
            catch (OperationCanceledException)
            {
                // Mark the notification canceled, then let the caller observe the cancellation.
                _notifications.FailDownload(operationKey, $"{engine.Name} update canceled.");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Other failures are logged, shown to the user, and reported to the caller as a plain failure.
                _logger.LogError(ex, "Engine update failed for {EngineId}.", engine.Id);
                log?.Report($"Engine update failed: {ex.Message}");
                _notifications.FailDownload(operationKey, $"Engine update failed: {ex.Message}");
                return false;
            }
            finally
            {
                // Remove the scratch folder and restart the stopped modules even after a failure or cancellation;
                // the restart ignores the caller's token so a canceled update still brings the modules back.
                TryDeleteDirectory(updateRoot);

                if (modulesWereStopped && enabledModuleSourcePaths is { Count: > 0 })
                {
                    await RestoreModulesAfterEngineUpdateAsync(enabledModuleSourcePaths, log, CancellationToken.None);
                }
            }
        }

        /// <summary>
        /// Returns source paths for modules that were enabled before an engine update begins.
        /// </summary>
        private async Task<List<string>> CaptureEnabledModuleSourcePathsAsync(CancellationToken ct)
        {
            var modules = await _moduleInstaller.DiscoverInstalledModulesAsync();
            return modules
                .Where(module => module.Status.Enabled)
                .Select(module => module.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Stops enabled modules and engine-owned processes before replacing an engine runtime.
        /// </summary>
        private async Task StopModulesForEngineUpdateAsync(
            EngineConfig engine,
            string engineDir,
            IReadOnlyList<string> enabledModuleSourcePaths,
            IProgress<string>? log,
            CancellationToken ct)
        {
            // Stop the module processes, then any orphaned process still holding files under a module folder.
            if (enabledModuleSourcePaths.Count > 0)
            {
                log?.Report($"Stopping {enabledModuleSourcePaths.Count} module(s) before engine update...");
                await _moduleRunner.StopAllModulesAsync();

                foreach (var moduleSourcePath in enabledModuleSourcePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    var moduleDir = Path.GetDirectoryName(moduleSourcePath);
                    if (string.IsNullOrWhiteSpace(moduleDir))
                    {
                        continue;
                    }

                    await StopProcessesRunningFromDirectoryAsync(moduleDir, log, ct);
                }
            }

            // ASLM may own an Ollama process started for its settings UI; stop that handle before touching the files.
            if (string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                _ollamaSettings.StopManagedRuntime();
            }

            // Finally stop whatever still runs from the engine directory.
            await StopProcessesRunningFromDirectoryAsync(engineDir, log, ct);
        }

        /// <summary>
        /// Restarts modules that were enabled before an engine update completed.
        /// </summary>
        private async Task RestoreModulesAfterEngineUpdateAsync(
            IReadOnlyList<string> enabledModuleSourcePaths,
            IProgress<string>? log,
            CancellationToken ct)
        {
            log?.Report($"Restarting {enabledModuleSourcePaths.Count} module(s)...");

            foreach (var moduleSourcePath in enabledModuleSourcePaths)
            {
                ct.ThrowIfCancellationRequested();

                // One module failing to restart must not prevent the remaining modules from coming back.
                try
                {
                    // Modules without a run command have nothing to restart.
                    var module = await _moduleInstaller.LoadModuleConfig(moduleSourcePath);
                    if (module == null || module.Commands.Run.Count == 0)
                    {
                        continue;
                    }

                    module.Status.Enabled = true;
                    await _moduleInstaller.SaveConfigAsync(module, raiseModulesChanged: false);
                    await _moduleRunner.ExecuteRunAsync(
                        module,
                        log ?? NoOpProgress<string>.Instance,
                        ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to restart module after engine update: {ModulePath}", moduleSourcePath);
                    log?.Report($"Failed to restart module: {Path.GetFileName(Path.GetDirectoryName(moduleSourcePath))}");
                }
            }
        }


        // App check helpers

        /// <summary>
        /// Returns whether the shipped ASLM update source contains a valid GitHub configuration.
        /// </summary>
        private static bool IsValidAppGitHubSource(AppUpdateSourceConfig? source)
        {
            return source != null &&
                   string.Equals(source.Source.Type, "github", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(source.Source.Repo);
        }

        /// <summary>
        /// Returns the configured ASLM asset name for the current OS and architecture.
        /// </summary>
        private string? GetConfiguredAppAssetName(AppUpdateSourceConfig source)
        {
            var assetName = PlatformInfo.ResolveFromMap(source.Assets);
            if (!string.IsNullOrWhiteSpace(assetName))
            {
                return assetName;
            }

            _logger.LogWarning("ASLM update asset for platform '{PlatformKey}' is not configured.", PlatformInfo.PlatformKey);
            return null;
        }

        /// <summary>
        /// Returns the configured engine asset name for the resolved active platform.
        /// </summary>
        private string? GetConfiguredEngineAssetName(EngineConfig engine)
        {
            engine.Update?.Normalize();
            var assetName = engine.Update?.AssetName;
            if (!string.IsNullOrWhiteSpace(assetName))
            {
                return assetName;
            }

            _logger.LogWarning(
                "Engine update asset for platform '{PlatformKey}' is not configured for {EngineId}.",
                PlatformInfo.PlatformKey,
                engine.Id);
            return null;
        }

        /// <summary>
        /// Returns whether the installed engine should treat the release tag as an available update.
        /// </summary>
        private static bool ShouldOfferEngineUpdate(EngineConfig engine, string releaseTag)
        {
            if (string.IsNullOrWhiteSpace(releaseTag))
            {
                return false;
            }

            var currentTag = ResolveInstalledEngineReleaseTag(engine);
            if (IsPlaceholderEngineReleaseTag(currentTag))
            {
                return true;
            }

            return ReleaseTagOrdering.ComparePrecedence(releaseTag.Trim(), currentTag.Trim()) > 0;
        }

        /// <summary>
        /// Returns whether an engine manifest still stores a placeholder instead of a release tag.
        /// </summary>
        private static bool HasPlaceholderEngineReleaseTag(EngineConfig engine)
        {
            if (!string.IsNullOrWhiteSpace(engine.Status.InstalledReleaseTag) &&
                !IsPlaceholderEngineReleaseTag(engine.Status.InstalledReleaseTag))
            {
                return false;
            }

            var version = engine.Status.InstalledVersion ?? engine.Version;
            return IsPlaceholderEngineReleaseTag(engine.Status.InstalledReleaseTag) &&
                   IsPlaceholderEngineReleaseTag(version);
        }

        /// <summary>
        /// Recognizes missing runtime tags and the unresolved latest alias.
        /// </summary>
        private static bool IsPlaceholderEngineReleaseTag(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Probes the installed engine runtime for its version string.
        /// </summary>
        private async Task<string?> TryProbeEngineRuntimeVersionAsync(EngineConfig engine, CancellationToken ct)
        {
            // Prefer the version reported by the running Ollama service when its API answers.
            if (string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                var apiVersion = await TryProbeOllamaApiVersionAsync(ct);
                if (!string.IsNullOrWhiteSpace(apiVersion))
                {
                    return apiVersion;
                }
            }

            // Fall back to asking the installed executable for its version.
            var executablePath = _engineInstaller.GetEngineExecutablePath(engine.Id);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return null;
            }

            return await TryProbeExecutableVersionAsync(executablePath, ct);
        }

        /// <summary>
        /// Reads a local Ollama runtime version when its API is available.
        /// </summary>
        internal static async Task<string?> TryProbeOllamaApiVersionAsync(CancellationToken ct, Uri? endpoint = null)
        {
            try
            {
                // A short timeout keeps update checks responsive when the service is not running.
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(2)
                };
                using var response = await client.GetAsync(endpoint ?? new Uri("http://127.0.0.1:11434/api/version"), ct);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.TryGetProperty("version", out var versionElement))
                {
                    return versionElement.GetString();
                }
            }
            catch
            {
                // Runtime probe is best-effort only.
            }

            return null;
        }

        /// <summary>
        /// Reads version output from the installed engine executable without a console window.
        /// </summary>
        private static async Task<string?> TryProbeExecutableVersionAsync(string executablePath, CancellationToken ct)
        {
            try
            {
                // Run "--version" without a console window and capture its output.
                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return null;
                }

                // Read standard output only; the first x.y.z version number in it is the runtime version.
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return TryExtractVersionToken(output);
            }
            catch
            {
                // Best effort: a missing, blocked, or crashing executable simply means the version is unknown.
                return null;
            }
        }

        /// <summary>
        /// Extracts a numeric runtime version from executable output.
        /// </summary>
        private static string? TryExtractVersionToken(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                output,
                @"\d+\.\d+\.\d+(?:\.\d+)?");
            return match.Success ? match.Value : null;
        }

        /// <summary>
        /// Normalizes a probed runtime version to a release-tag reference.
        /// </summary>
        private static string NormalizeEngineVersionReference(string version)
        {
            var trimmed = version.Trim();
            return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed : $"v{trimmed}";
        }

        /// <summary>
        /// Returns the locally installed release tag or version string for one engine.
        /// </summary>
        private static string ResolveInstalledEngineReleaseTag(EngineConfig engine)
        {
            return engine.Status.InstalledReleaseTag ?? engine.Status.InstalledVersion ?? engine.Version;
        }

        /// <summary>
        /// Returns whether one engine update candidate already matches the installed runtime.
        /// </summary>
        internal static bool IsEngineAlreadyAtInstallTarget(EngineConfig engine, UpdateCandidate candidate)
        {
            var targetTag = candidate.ReleaseTag ?? candidate.RemoteVersion;
            if (string.IsNullOrWhiteSpace(targetTag))
            {
                return false;
            }

            return ReleaseTagOrdering.AreEquivalentVersionReferences(
                ResolveInstalledEngineReleaseTag(engine),
                targetTag);
        }


        // Module check helpers

        /// <summary>
        /// Checks whether a branch-tracked module has moved to a newer commit.
        /// </summary>
        private async Task<UpdateCandidate?> CheckModuleBranchUpdateAsync(
            ModuleConfig module,
            bool isManualRequest,
            CancellationToken ct)
        {
            // Find the tracked branch and its head commit.
            var branches = await _github.GetBranchesAsync(
                module.Source.Repo,
                ResolveRequestSource(isManualRequest),
                ct);
            var selected = branches.FirstOrDefault(branch =>
                string.Equals(branch.Name, module.Update.Branch, StringComparison.OrdinalIgnoreCase));

            if (selected == null || string.IsNullOrWhiteSpace(selected.CommitSha))
            {
                return null;
            }

            // The branch head becomes the candidate unless it is the commit that is already installed.
            var candidate = new UpdateCandidate
            {
                TargetKind = "module",
                TargetId = module.Id,
                Name = module.Name,
                CurrentVersion = BuildModuleCurrentVersion(module),
                RemoteVersion = $"{selected.Name}@{ShortSha(selected.CommitSha)}",
                Channel = "branch",
                Mode = "branch",
                ReferenceName = selected.Name,
                CommitSha = selected.CommitSha,
                Module = module
            };
            return IsModuleAlreadyAtInstallTarget(module, candidate) ? null : candidate;
        }

        /// <summary>
        /// Resolves the branch install target selected by the module manifest.
        /// </summary>
        private async Task<UpdateCandidate?> ResolveModuleBranchInstallCandidateAsync(
            ModuleConfig module,
            bool isManualRequest,
            CancellationToken ct)
        {
            // Find the selected branch and its head commit.
            var branches = await _github.GetBranchesAsync(
                module.Source.Repo,
                ResolveRequestSource(isManualRequest),
                ct);
            var selected = branches.FirstOrDefault(branch =>
                string.Equals(branch.Name, module.Update.Branch, StringComparison.OrdinalIgnoreCase));
            if (selected == null || string.IsNullOrWhiteSpace(selected.CommitSha))
            {
                return null;
            }

            // Nothing to install when the files already come from this very commit.
            if (IsInstalledFromBranch(module) &&
                string.Equals(
                    selected.CommitSha.Trim(),
                    module.Update.InstalledCommitSha!.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new UpdateCandidate
            {
                TargetKind = "module",
                TargetId = module.Id,
                Name = module.Name,
                CurrentVersion = BuildModuleCurrentVersion(module),
                RemoteVersion = $"{selected.Name}@{ShortSha(selected.CommitSha)}",
                Channel = "branch",
                Mode = "branch",
                ReferenceName = selected.Name,
                CommitSha = selected.CommitSha,
                Module = module
            };
        }

        /// <summary>
        /// Checks whether a release-tracked module has a newer GitHub release.
        /// </summary>
        private static UpdateCandidate? CheckModuleReleaseUpdate(
            ModuleConfig module, IReadOnlyList<UpdateCandidate> candidates)
        {
            // A manifest version or a tag left over from an earlier installation does not identify current files.
            var selected = SelectModuleReleaseInstallTarget(module, candidates);
            return selected != null && !IsModuleAlreadyAtInstallTarget(module, selected) ? selected : null;
        }

        /// <summary>
        /// Resolves the release install target selected by the module manifest.
        /// </summary>
        private static UpdateCandidate? ResolveModuleReleaseInstallCandidate(
            ModuleConfig module, IReadOnlyList<UpdateCandidate> candidates)
        {
            var resolved = SelectModuleReleaseInstallTarget(module, candidates);
            if (resolved == null || string.IsNullOrWhiteSpace(resolved.ReleaseTag))
            {
                return null;
            }

            if (!ShouldOfferReleaseInstallCandidate(module, resolved.ReleaseTag))
            {
                return null;
            }

            return resolved;
        }

        /// <summary>
        /// Picks the release matching the module's selected tag: the newest candidate for an unset or latest selection,
        /// otherwise the candidate with exactly that tag.
        /// Only latest may follow the release stream; a missing pinned tag must not install a different version.
        /// </summary>
        internal static UpdateCandidate? SelectModuleReleaseInstallTarget(ModuleConfig module, IReadOnlyList<UpdateCandidate> candidates)
        {
            var tag = module.Update.SelectedReleaseTag;
            return string.IsNullOrWhiteSpace(tag) || IsLatestReleaseSelection(tag)
                ? candidates.FirstOrDefault()
                : candidates.FirstOrDefault(candidate => string.Equals(candidate.ReleaseTag, tag, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Resolves the archive URL used to update one module release.
        /// </summary>
        private static string ResolveModuleDownloadUrl(ModuleConfig module, GitHubReleaseInfo release)
        {
            if (!string.IsNullOrWhiteSpace(module.Update.AssetName))
            {
                return release.Assets.FirstOrDefault(asset =>
                    string.Equals(asset.Name, module.Update.AssetName, StringComparison.OrdinalIgnoreCase))
                    ?.BrowserDownloadUrl ?? string.Empty;
            }

            return release.ZipballUrl;
        }

        /// <summary>
        /// Builds one release-backed module update candidate from a GitHub release payload.
        /// </summary>
        private static UpdateCandidate BuildModuleReleaseCandidate(
            ModuleConfig module,
            GitHubReleaseInfo release,
            string downloadUrl)
        {
            return new UpdateCandidate
            {
                TargetKind = "module",
                TargetId = module.Id,
                Name = module.Name,
                DisplayName = BuildReleaseDisplayName(release),
                CurrentVersion = BuildModuleCurrentVersion(module),
                RemoteVersion = release.TagName,
                Channel = release.Prerelease ? "pre-release" : "release",
                Mode = module.Update.Mode,
                DownloadUrl = downloadUrl,
                ReleaseTag = release.TagName,
                IsPrerelease = release.Prerelease,
                PublishedAt = release.PublishedAt,
                Module = module
            };
        }


        // Module apply helpers

        /// <summary>
        /// Downloads the archive for one module update candidate.
        /// </summary>
        private async Task DownloadModuleArchiveAsync(
            ModuleConfig module,
            UpdateCandidate candidate,
            string archivePath,
            IProgress<string>? log,
            IProgress<DownloadProgress>? progress,
            bool isManualRequest,
            CancellationToken ct)
        {
            var requestSource = ResolveRequestSource(isManualRequest);
            if (string.Equals(candidate.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                await _github.DownloadRepositoryZipAsync(
                    module.Source.Repo,
                    candidate.ReferenceName ?? module.Update.Branch,
                    archivePath,
                    log,
                    progress,
                    requestSource,
                    ct);
                return;
            }

            await _github.DownloadFileAsync(candidate.DownloadUrl, archivePath, log, progress, requestSource, ct);
        }

        /// <summary>
        /// Locates the module manifest inside one extracted archive.
        /// </summary>
        private static string FindUpdatedModuleManifest(string extractDir)
        {
            var manifestPath = Directory
                .EnumerateFiles(extractDir, "ASLM_Module.json", SearchOption.AllDirectories)
                .FirstOrDefault();

            return manifestPath ?? throw new InvalidOperationException(
                "Downloaded module archive does not contain ASLM_Module.json.");
        }

        /// <summary>
        /// Loads one module configuration directly from a specific manifest path.
        /// </summary>
        private async Task<ModuleConfig?> LoadModuleConfigFromPathAsync(string path, CancellationToken ct)
        {
            var config = await ModuleManifestParser.LoadAsync(path, ct);
            _moduleInstaller.ResolvePlatformSupport(config);
            return config;
        }

        /// <summary>
        /// Loads one module configuration directly from a specific manifest path synchronously.
        /// </summary>
        private ModuleConfig? LoadModuleConfigFromPath(string path)
        {
            var config = ModuleManifestParser.Parse(File.ReadAllText(path), path);
            _moduleInstaller.ResolvePlatformSupport(config);
            return config;
        }

        /// <summary>
        /// Applies a prepared module update while keeping the heavy file operations off the UI thread.
        /// </summary>
        private async Task<bool> ApplyPreparedModuleUpdateAsync(
            ModuleConfig module,
            UpdateCandidate candidate,
            PreparedModuleUpdate preparedUpdate,
            string moduleDir,
            bool wasEnabled,
            bool isInstallation,
            IProgress<string>? log,
            CancellationToken ct)
        {
            try
            {
                // Replace module files on a worker thread while honoring declared preserve paths.
                await Task.Run(async () =>
                {
                    var preservePaths = BuildPreservePathSet(module.Update.Preserve);
                    ReportPreservedPaths(moduleDir, preservePaths, log);

                    // Replace stale module files while leaving declared local state in place.
                    await ClearDirectoryForUpdateAsync(moduleDir, preservePaths, log, ct);
                    CopyDirectory(preparedUpdate.ModuleSourceDir, moduleDir, preservePaths);
                }, ct);

                // Reload merged manifest, optionally run first-run, then re-enable when the module was active.
                var installedPath = Path.Combine(moduleDir, "ASLM_Module.json");
                var installed = await _moduleInstaller.LoadModuleConfig(installedPath)
                    ?? throw new InvalidOperationException("Updated module manifest could not be loaded.");

                // Carry the user's state into the new manifest and persist the installed content.
                MergeModuleState(module, installed, candidate);
                await _moduleInstaller.SaveInstalledContentAsync(installed, raiseModulesChanged: false);

                // The new version may declare engines that must be present before setup or launch.
                await _moduleEngineReconciler.ReconcileRequiredEnginesAsync(
                    installed,
                    log ?? NoOpProgress<string>.Instance,
                    downloadProgress: null,
                    ct: ct);

                // First installations, modules that never finished setup, and modules that ask for it run first-run setup.
                if (isInstallation || !module.Status.FirstRunCompleted || installed.Update.RunFirstRunAfterUpdate)
                {
                    log?.Report($"Running first-run setup for {installed.Name}...");
                    installed.Status.FirstRunCompleted = false;

                    var setupSuccess = await Task.Run(
                        () => _moduleRunner.ExecuteFirstRunAsync(
                            installed,
                            log ?? NoOpProgress<string>.Instance,
                            ct),
                        ct);

                    if (!setupSuccess)
                    {
                        await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);
                        log?.Report("Module update applied, but setup failed.");
                        return false;
                    }
                }

                // Bring the module back only if it was enabled before the update stopped it.
                if (wasEnabled && installed.Commands.Run.Count > 0)
                {
                    log?.Report($"Restarting {installed.Name} after update...");
                    installed.Status.Enabled = true;
                    var runStarted = await _moduleRunner.ExecuteRunAsync(
                        installed,
                        log ?? NoOpProgress<string>.Instance,
                        ct);
                    if (!runStarted)
                    {
                        installed.Status.Enabled = false;
                        await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);
                        log?.Report("Module update applied, but restart failed.");
                        return false;
                    }
                }

                // Stamp and persist the final state of a fully applied update.
                installed.Status.LastUpdated = DateTime.UtcNow.ToString("o");
                await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);
                log?.Report($"{installed.Name} updated to {candidate.RemoteVersion}.");
                return true;
            }
            catch
            {
                // Failed updates are not rolled back; tell the user so before the exception propagates to the caller.
                log?.Report("Update failed. Module backup restoration is disabled.");
                throw;
            }
        }

        /// <summary>
        /// Carries forward user state and update metadata from the old module into the new manifest.
        /// </summary>
        /// <param name="oldConfig">Manifest of the module as it was installed before the update.</param>
        /// <param name="newConfig">Manifest loaded from the freshly extracted files; modified in place.</param>
        /// <param name="candidate">Applied candidate, which determines the new installed fingerprint.</param>
        internal static void MergeModuleState(ModuleConfig oldConfig, ModuleConfig newConfig, UpdateCandidate candidate)
        {
            // Carry forward user-edited setting values keyed by setting name.
            var oldSettingValues = oldConfig.Settings
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Key))
                .ToDictionary(
                    setting => setting.Key,
                    setting => (setting.Value, setting.UseCustomValue),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var setting in newConfig.Settings)
            {
                if (!oldSettingValues.TryGetValue(setting.Key, out var oldValue))
                {
                    continue;
                }

                setting.Value = oldValue.Value;
                setting.UseCustomValue = oldValue.UseCustomValue;
            }

            // Runtime status and update settings are owned by the local installation, not by the downloaded package.
            newConfig.Status.Installed = true;
            newConfig.Status.Enabled = false;
            newConfig.Status.InstalledVersion = newConfig.Version;
            newConfig.Status.LastChecked = DateTime.UtcNow.ToString("o");
            newConfig.Status.LastUpdated = DateTime.UtcNow.ToString("o");

            // Keep the user's update preferences; the preserve lists of the old and the new package are merged.
            newConfig.Update.Mode = oldConfig.Update.Mode;
            newConfig.Update.Channel = oldConfig.Update.Channel;
            newConfig.Update.UseDefaultChannel = oldConfig.Update.UseDefaultChannel;
            newConfig.Update.Branch = candidate.ReferenceName ?? oldConfig.Update.Branch;
            newConfig.Update.AssetName = oldConfig.Update.AssetName ?? newConfig.Update.AssetName;
            newConfig.Update.RunFirstRunAfterUpdate = oldConfig.Update.RunFirstRunAfterUpdate;
            newConfig.Update.Preserve = oldConfig.Update.Preserve
                .Concat(newConfig.Update.Preserve)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // These fields identify the current files, not the history of previously installed sources.
            // Clearing the opposite fingerprint is essential when switching branch -> release -> branch.
            var installedFromBranch = string.Equals(candidate.Mode, "branch", StringComparison.OrdinalIgnoreCase);
            newConfig.Update.InstalledCommitSha = installedFromBranch ? candidate.CommitSha : null;
            newConfig.Update.InstalledReleaseTag = installedFromBranch ? null : candidate.ReleaseTag;

            // A latest (or unset) selection stays on the moving alias; a pinned tag follows the applied candidate.
            newConfig.Update.SelectedReleaseTag =
                string.IsNullOrWhiteSpace(oldConfig.Update.SelectedReleaseTag) ||
                IsLatestReleaseSelection(oldConfig.Update.SelectedReleaseTag)
                    ? ModuleUpdateConfig.LatestReleaseTag
                    : candidate.ReleaseTag ?? oldConfig.Update.SelectedReleaseTag;

            // The applied update is no longer pending.
            newConfig.Update.PendingUpdate = null;
            newConfig.Update.Normalize();
        }


        // Version helpers

        /// <summary>
        /// Identifies explicit user requests separately from automatic background checks.
        /// </summary>
        private static string ResolveRequestSource(bool isManualRequest) =>
            isManualRequest ? GitHubRequestSources.Manual : GitHubRequestSources.Auto;

        /// <summary>
        /// Returns whether the selected update channel should include GitHub pre-releases.
        /// </summary>
        private static bool IsPrereleaseChannel(string? channel)
        {
            return string.Equals(channel, "pre-release", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether the selected module update mode includes GitHub pre-releases.
        /// </summary>
        private static bool IsPrereleaseMode(string? mode)
        {
            return IsPrereleaseChannel(mode);
        }

        /// <summary>
        /// Returns the best local version label for one module.
        /// </summary>
        private static string BuildModuleCurrentVersion(ModuleConfig module)
        {
            if (IsInstalledFromBranch(module))
            {
                // The selected tracking branch may have changed; only the recorded commit is an installed identity.
                return ShortSha(module.Update.InstalledCommitSha!);
            }

            return ResolveInstalledModuleReleaseTag(module);
        }

        /// <summary>
        /// Returns the locally installed release tag or version string for one module.
        /// </summary>
        private static string ResolveInstalledModuleReleaseTag(ModuleConfig module)
        {
            return IsInstalledFromRelease(module)
                ? module.Update.InstalledReleaseTag!
                : module.Status.InstalledVersion ?? module.Version;
        }

        /// <summary>
        /// Identifies a current branch installation; legacy manifests containing both fingerprints are ambiguous.
        /// </summary>
        private static bool IsInstalledFromBranch(ModuleConfig module) =>
            !string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha) &&
            string.IsNullOrWhiteSpace(module.Update.InstalledReleaseTag);

        /// <summary>
        /// Identifies a current release installation without trusting a tag retained from a previous source.
        /// </summary>
        private static bool IsInstalledFromRelease(ModuleConfig module) =>
            !string.IsNullOrWhiteSpace(module.Update.InstalledReleaseTag) &&
            string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha);

        /// <summary>
        /// Returns whether the module manifest records a successful remote source install.
        /// </summary>
        internal static bool HasRecordedRemoteSourceInstall(ModuleConfig module)
        {
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                return IsInstalledFromBranch(module);
            }

            return IsInstalledFromRelease(module);
        }

        /// <summary>
        /// Returns whether a resolved release install candidate should be offered for download.
        /// </summary>
        internal static bool ShouldOfferReleaseInstallCandidate(ModuleConfig module, string resolvedReleaseTag)
        {
            if (string.IsNullOrWhiteSpace(resolvedReleaseTag))
            {
                return false;
            }

            if (!IsInstalledFromRelease(module))
            {
                return true;
            }

            var installed = ResolveInstalledModuleReleaseTag(module);
            return !ReleaseTagOrdering.AreEquivalentVersionReferences(resolvedReleaseTag, installed);
        }

        /// <summary>
        /// Returns whether the install candidate already matches the local installation (no file work needed).
        /// </summary>
        internal static bool IsModuleAlreadyAtInstallTarget(ModuleConfig module, UpdateCandidate candidate)
        {
            // Compare the candidate with the actual installed source, never with the newly selected channel.
            if (string.Equals(candidate.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(candidate.CommitSha) || !IsInstalledFromBranch(module))
                {
                    return false;
                }

                return string.Equals(
                    candidate.CommitSha.Trim(),
                    module.Update.InstalledCommitSha!.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(candidate.ReleaseTag))
            {
                return false;
            }

            if (!IsInstalledFromRelease(module))
            {
                return false;
            }

            var installed = ResolveInstalledModuleReleaseTag(module);
            return ReleaseTagOrdering.AreEquivalentVersionReferences(candidate.ReleaseTag, installed);
        }

        /// <summary>
        /// Resolves the local ASLM version label shown in settings when no GitHub tag is known yet.
        /// </summary>
        private static string ResolveCurrentAppDisplayVersion()
        {
            var informationalVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                var buildMetadataIndex = informationalVersion.IndexOf('+');
                return buildMetadataIndex > 0
                    ? informationalVersion[..buildMetadataIndex]
                    : informationalVersion;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }

        /// <summary>
        /// Returns the GitHub release tag stored with a prepared ASLM self-update when the manifest is present.
        /// </summary>
        public string? TryGetPendingPreparedAppVersion() => TryReadPendingAppUpdateVersion();

        /// <summary>
        /// Returns whether the current ASLM installation should treat the release tag as an available update.
        /// </summary>
        private bool ShouldOfferAppUpdate(string releaseTag)
        {
            if (string.IsNullOrWhiteSpace(releaseTag))
            {
                return false;
            }

            // Compare against the higher-precedence of installed and already-staged release tags.
            var baseline = MergeInstalledAndPendingReleaseBaselines(
                _appData.Data.Updates.InstalledReleaseTag,
                TryReadPendingAppUpdateVersion());

            if (baseline == null)
            {
                // No persisted GitHub baseline from the patcher yet, and no staged self-update — product/assembly
                // versions are not aligned with repository tags, so the semantically newest channel release applies.
                return true;
            }

            return ReleaseTagOrdering.ComparePrecedence(releaseTag.Trim(), baseline) > 0;
        }

        /// <summary>
        /// Returns the higher-precedence reference between the installed tag and an already staged self-update.
        /// </summary>
        private static string? MergeInstalledAndPendingReleaseBaselines(string? installedReleaseTag, string? pendingVersion)
        {
            var installed = string.IsNullOrWhiteSpace(installedReleaseTag) ? null : installedReleaseTag.Trim();
            var pending = string.IsNullOrWhiteSpace(pendingVersion) ? null : pendingVersion.Trim();
            if (installed == null)
            {
                return pending;
            }

            if (pending == null)
            {
                return installed;
            }

            return ReleaseTagOrdering.ComparePrecedence(installed, pending) >= 0 ? installed : pending;
        }

        /// <summary>
        /// Reads the target tag stored with a prepared ASLM self-update without failing callers when the file is invalid.
        /// </summary>
        private string? TryReadPendingAppUpdateVersion()
        {
            // Pending packages prepared by another desktop platform cannot be applied by this head.
            if (!SupportsSelfUpdate) return null;
            try
            {
                var path = GetPendingUpdatePath();
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                var pending = JsonSerializer.Deserialize<PendingAppUpdate>(json, _jsonOptions);
                if (pending == null || string.IsNullOrWhiteSpace(pending.Version))
                {
                    return null;
                }

                return pending.Version.Trim();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unable to read pending ASLM self-update version.");
                return null;
            }
        }

        /// <summary>
        /// Returns whether the selected release marker points at the moving latest target.
        /// </summary>
        internal static bool IsLatestReleaseSelection(string? selectedReleaseTag)
        {
            return string.IsNullOrWhiteSpace(selectedReleaseTag) ||
                   string.Equals(
                       selectedReleaseTag.Trim(),
                       ModuleUpdateConfig.LatestReleaseTag,
                       StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns a short display form of one commit SHA.
        /// </summary>
        private static string ShortSha(string sha)
        {
            return string.IsNullOrWhiteSpace(sha) ? string.Empty : sha[..Math.Min(7, sha.Length)];
        }

        /// <summary>
        /// Builds the compact picker label shown for one release candidate.
        /// </summary>
        private static string BuildReleaseDisplayName(GitHubReleaseInfo release)
        {
            var label = string.IsNullOrWhiteSpace(release.TagName)
                ? release.Name
                : release.TagName;

            if (release.PublishedAt.HasValue)
            {
                label += " - " + release.PublishedAt.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }

            return label;
        }


        // File system helpers

        /// <summary>
        /// Builds a local archive file name from a download URL, keeping tar-family extensions and defaulting to
        /// <c>.zip</c>. Preserves compression extensions even when the URL contains a query or fragment.
        /// </summary>
        internal static string GetArchiveFileName(string? downloadUrl, string baseName)
        {
            var path = Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
                ? uri.AbsolutePath
                : (downloadUrl ?? string.Empty).Split('?', '#')[0];

            // Test compound extensions before the fallback: treating a .tar.zst as ZIP breaks engine updates.
            foreach (var extension in new[] { ".tar.zst", ".tar.gz", ".tgz", ".tar" })
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    return baseName + extension;
            }

            return baseName + ".zip";
        }

        /// <summary>
        /// Extracts one ZIP or tar-family archive and rejects ZIP entries that escape the target directory.
        /// </summary>
        private static void ExtractZipSafe(string zipPath, string destination)
        {
            if (ArchiveExtractor.IsTarArchive(zipPath))
            {
                ArchiveExtractor.ExtractToDirectory(zipPath, destination);
                return;
            }

            Directory.CreateDirectory(destination);
            var destinationPrefix = EnsureTrailingSeparator(Path.GetFullPath(destination));

            // Reject zip-slip entries before extracting any file onto disk.
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!targetPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"ZIP entry '{entry.FullName}' escapes the destination directory.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }

        /// <summary>
        /// Collapses archives that unpack into a single wrapper folder.
        /// </summary>
        private static string ResolveSinglePayloadDirectory(string extractDir)
        {
            var directories = Directory.GetDirectories(extractDir);
            var files = Directory.GetFiles(extractDir);
            return directories.Length == 1 && files.Length == 0 ? directories[0] : extractDir;
        }

        /// <summary>
        /// Builds the normalized set of paths that must stay in place during module replacement.
        /// </summary>
        private static HashSet<string> BuildPreservePathSet(IEnumerable<string> relativePaths)
        {
            var preservePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var relativePath in relativePaths)
            {
                // Absolute or empty entries cannot name a module-relative path and are ignored.
                if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                {
                    continue;
                }

                // Reject entries that point at the module root or try to leave it.
                var normalized = NormalizeRelativePath(relativePath);
                if (normalized.Length == 0 ||
                    normalized == "." ||
                    normalized.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }

                preservePaths.Add(normalized);
            }

            return preservePaths;
        }

        /// <summary>
        /// Logs preserved paths without traversing their contents.
        /// </summary>
        private static void ReportPreservedPaths(
            string moduleRoot,
            IReadOnlySet<string> preservePaths,
            IProgress<string>? log)
        {
            foreach (var relativePath in preservePaths)
            {
                var fullPath = ResolveChildPath(moduleRoot, relativePath);
                if (File.Exists(fullPath))
                {
                    log?.Report($"Preserving file in place: {relativePath}");
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    log?.Report($"Preserving directory in place: {relativePath}");
                }
            }
        }

        /// <summary>
        /// Recursively copies one directory into another while skipping preserved module paths.
        /// </summary>
        private static void CopyDirectory(
            string sourceDir,
            string destDir,
            IReadOnlySet<string> preservePaths,
            string relativeRoot = "")
        {
            Directory.CreateDirectory(destDir);

            // Copy the files of this level, overwriting older ones; preserved files keep their local content.
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var relativePath = CombineRelativePath(relativeRoot, Path.GetFileName(file));
                if (IsPreservedPath(relativePath, preservePaths))
                {
                    continue;
                }

                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            // Recurse into subdirectories, tracking the module-relative path for the preserve check.
            foreach (var subdir in Directory.EnumerateDirectories(sourceDir))
            {
                var relativePath = CombineRelativePath(relativeRoot, Path.GetFileName(subdir));
                if (IsPreservedPath(relativePath, preservePaths))
                {
                    continue;
                }

                CopyDirectory(
                    subdir,
                    Path.Combine(destDir, Path.GetFileName(subdir)),
                    preservePaths,
                    relativePath);
            }
        }

        /// <summary>
        /// Removes every non-preserved file and directory inside one root directory.
        /// </summary>
        private static void ClearDirectory(string directory, IReadOnlySet<string> preservePaths)
        {
            Directory.CreateDirectory(directory);
            ClearDirectory(directory, directory, preservePaths);
        }

        /// <summary>
        /// Removes non-preserved contents from one directory while preserving declared descendants.
        /// </summary>
        private static void ClearDirectory(
            string currentDir,
            string rootDir,
            IReadOnlySet<string> preservePaths)
        {
            // Delete the files of this level, clearing read-only attributes first so the delete cannot fail on them.
            foreach (var file in Directory.EnumerateFiles(currentDir))
            {
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDir, file));
                if (IsPreservedPath(relativePath, preservePaths))
                {
                    continue;
                }

                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            // Remove subdirectories: preserved ones stay, ones that contain preserved paths are cleared selectively
            // (and removed only when nothing is left), all others are deleted with their content.
            foreach (var subdir in Directory.EnumerateDirectories(currentDir))
            {
                var relativePath = NormalizeRelativePath(Path.GetRelativePath(rootDir, subdir));
                if (IsPreservedPath(relativePath, preservePaths))
                {
                    continue;
                }

                if (HasPreservedDescendant(relativePath, preservePaths))
                {
                    ClearDirectory(subdir, rootDir, preservePaths);
                    if (!Directory.EnumerateFileSystemEntries(subdir).Any())
                    {
                        Directory.Delete(subdir);
                    }

                    continue;
                }

                Directory.Delete(subdir, recursive: true);
            }
        }

        /// <summary>
        /// Clears a module directory with retries for Windows file locks from recently stopped processes.
        /// </summary>
        private static async Task ClearDirectoryForUpdateAsync(
            string directory,
            IReadOnlySet<string> preservePaths,
            IProgress<string>? log,
            CancellationToken ct)
        {
            const int maxAttempts = 6;

            // Retry cleanup when Windows still holds handles from recently stopped module processes.
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await Task.Run(() => ClearDirectory(directory, preservePaths), ct);
                    return;
                }
                catch (Exception ex) when (IsTransientFileAccessException(ex) && attempt < maxAttempts)
                {
                    log?.Report($"Module files are still in use. Retrying cleanup ({attempt}/{maxAttempts - 1})...");
                    await StopProcessesRunningFromDirectoryAsync(directory, log, ct);
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
                }
            }
        }

        /// <summary>
        /// Returns whether a failed delete may succeed after process cleanup or a short retry delay.
        /// </summary>
        private static bool IsTransientFileAccessException(Exception ex)
        {
            return ex is IOException or UnauthorizedAccessException;
        }

        /// <summary>
        /// Stops untracked or orphaned processes whose executable lives under one module or engine directory
        /// and returns how many were stopped.
        /// </summary>
        private static async Task<int> StopProcessesRunningFromDirectoryAsync(
            string directory,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var moduleRoot = EnsureTrailingSeparator(Path.GetFullPath(directory));
            var currentProcessId = Environment.ProcessId;
            var stoppedCount = 0;

            // Best-effort scan: kill only processes whose main executable lives under the module root.
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (process.Id == currentProcessId || process.HasExited)
                    {
                        continue;
                    }

                    var executablePath = TryGetProcessExecutablePath(process);
                    if (!IsPathUnderDirectory(executablePath, moduleRoot))
                    {
                        continue;
                    }

                    log?.Report(
                        $"Stopping process using module files: {Path.GetFileName(executablePath)} (PID {process.Id}).");

                    process.Kill(entireProcessTree: true);
                    stoppedCount++;
                    await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Best effort only. The following delete retry will surface persistent blockers.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return stoppedCount;
        }

        /// <summary>
        /// Returns the full executable path for a process when the OS allows it.
        /// </summary>
        private static string? TryGetProcessExecutablePath(Process process)
        {
            try
            {
                return process.MainModule?.FileName;
            }
            catch
            {
                // Access is denied for protected or already exited processes; their path is simply unknown.
                return null;
            }
        }

        /// <summary>
        /// Returns whether one path is inside a normalized directory root.
        /// </summary>
        private static bool IsPathUnderDirectory(string? path, string normalizedRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                return Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // A path that cannot be normalized cannot be under the root.
                return false;
            }
        }

        /// <summary>
        /// Waits briefly after killing a process tree so Windows can release executable file handles.
        /// </summary>
        private static async Task WaitForProcessExitAsync(
            Process process,
            TimeSpan timeout,
            CancellationToken ct)
        {
            if (process.HasExited)
            {
                return;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Continue to the delete retry; it will report a real blocker if the process survived.
            }
        }

        /// <summary>
        /// Returns the full path to the pending ASLM self-update file.
        /// </summary>
        private string GetPendingUpdatePath()
        {
            return Path.Combine(GetRootDirectory(), UpdateWorkDirName, PendingFileName);
        }

        /// <summary>
        /// Combines relative path segments for module path comparisons.
        /// </summary>
        private static string CombineRelativePath(string basePath, string childName)
        {
            return string.IsNullOrWhiteSpace(basePath)
                ? NormalizeRelativePath(childName)
                : NormalizeRelativePath($"{basePath}/{childName}");
        }

        /// <summary>
        /// Returns whether one relative module path is preserved or lives inside a preserved directory.
        /// </summary>
        private static bool IsPreservedPath(string relativePath, IReadOnlySet<string> preservePaths)
        {
            var normalized = NormalizeRelativePath(relativePath);
            return preservePaths.Contains(normalized) ||
                   preservePaths.Any(path =>
                       normalized.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns whether one relative directory contains a preserved descendant.
        /// </summary>
        private static bool HasPreservedDescendant(string relativePath, IReadOnlySet<string> preservePaths)
        {
            var normalized = NormalizeRelativePath(relativePath);
            var prefix = normalized.Length == 0 ? string.Empty : normalized + "/";
            return preservePaths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Normalizes relative paths for cross-platform module path comparisons.
        /// </summary>
        private static string NormalizeRelativePath(string relativePath)
        {
            return relativePath.Trim().Replace('\\', '/').Trim('/');
        }

        /// <summary>
        /// Resolves one relative child path and rejects directory traversal.
        /// </summary>
        private static string ResolveChildPath(string rootPath, string relativePath)
        {
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException("Absolute paths are not allowed.");
            }

            var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
            var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!EnsureTrailingSeparator(combined).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path '{relativePath}' escapes the target root.");
            }

            return combined;
        }

        /// <summary>
        /// Ensures directory paths always end with a separator for secure prefix checks.
        /// </summary>
        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Sanitizes free-form text so it can be used as a filesystem name.
        /// </summary>
        private static string SanitizeFileName(string value)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string((value ?? string.Empty)
                .Select(character => invalidChars.Contains(character) ? '_' : character)
                .ToArray());

            return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
        }

        /// <summary>
        /// Deletes one directory on a best-effort basis.
        /// </summary>
        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup only.
            }
        }

        /// <summary>
        /// Returns the ASLM root directory above the running App folder.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Nested types

        /// <summary>
        /// Represents a no-op progress sink used when the caller does not provide logging.
        /// </summary>
        private sealed class NoOpProgress<T> : IProgress<T>
        {
            /// <summary>
            /// Shared instance, since the sink holds no state.
            /// </summary>
            public static NoOpProgress<T> Instance { get; } = new();

            /// <summary>
            /// Ignores one reported value.
            /// </summary>
            public void Report(T value)
            {
            }
        }

        /// <summary>
        /// Carries the validated extracted module payload into the apply phase.
        /// </summary>
        /// <param name="NewManifestPath">Path of <c>ASLM_Module.json</c> inside the extracted archive.</param>
        /// <param name="NewConfig">Manifest parsed from the extracted archive, or null when it could not be loaded.</param>
        /// <param name="ModuleSourceDir">Extracted folder whose content replaces the installed module files.</param>
        private sealed record PreparedModuleUpdate(
            string NewManifestPath,
            ModuleConfig? NewConfig,
            string ModuleSourceDir);
    }
}
