// Copyright NEXTGGTECH. Apache License 2.0.

using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Checks, prepares, and applies ASLM and module updates.
    /// </summary>
    public sealed class UpdateManager
    {
        // Fields and constants

        private const string UpdateWorkDirName = ".aslm-update";
        private const string PendingFileName = "pending.json";
        private const string UpdateSourceFileName = "ASLM_UpdateSource.json";

        private readonly AppDataStore _appData;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleRunner _moduleRunner;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly ModuleEngineReconciler _moduleEngineReconciler;
        private readonly OllamaSettingsStore _ollamaSettings;
        private readonly GitHubUpdateClient _github;
        private readonly NotificationCenter _notifications;
        private readonly ILogger<UpdateManager> _logger;

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
            ModuleTrustService moduleTrustService,
            ModuleEngineReconciler moduleEngineReconciler,
            OllamaSettingsStore ollamaSettings,
            GitHubUpdateClient github,
            NotificationCenter notifications,
            ILogger<UpdateManager> logger)
        {
            _appData = appData;
            _moduleInstaller = moduleInstaller;
            _engineInstaller = engineInstaller;
            _moduleRunner = moduleRunner;
            _moduleTrustService = moduleTrustService;
            _moduleEngineReconciler = moduleEngineReconciler;
            _ollamaSettings = ollamaSettings;
            _github = github;
            _notifications = notifications;
            _logger = logger;
        }


        // State access

        /// <summary>
        /// Gets whether a prepared ASLM self-update is waiting for restart.
        /// </summary>
        public bool HasPendingAppUpdate => File.Exists(GetPendingUpdatePath());

        /// <summary>
        /// Returns the current ASLM app version.
        /// </summary>
        public string CurrentAppVersion => ResolveCurrentAppDisplayVersion();


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
        public async Task<UpdateCandidate?> CheckAppUpdateAsync(
            CancellationToken ct = default,
            bool publishUpdateNotification = true,
            bool isManualRequest = false)
        {
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
        /// <param name="publishUpdateNotification">When false, skips publishing an update-available notification (used when auto-updates will apply immediately).</param>
        public async Task<UpdateCandidate?> CheckModuleUpdateAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool publishUpdateNotification = true,
            bool isManualRequest = false)
        {
            module.Normalize();
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(module.Source.Repo))
            {
                return null;
            }

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

            var releaseCandidate = await CheckModuleReleaseUpdateAsync(module, isManualRequest, ct);
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
            engine.Normalize();
            if (!engine.Status.Installed)
            {
                return null;
            }

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

            await TrySyncEngineInstalledReleaseTagFromRuntimeAsync(engine, ct, isManualRequest);

            var assetName = GetConfiguredEngineAssetName(engine);
            if (string.IsNullOrWhiteSpace(assetName))
            {
                return null;
            }

            var channel = string.IsNullOrWhiteSpace(_appData.Data.Updates.AppChannel)
                ? "release"
                : _appData.Data.Updates.AppChannel;
            var includePrerelease = IsPrereleaseChannel(channel);

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
            engine.Normalize();
            if (EngineInstaller.EnsureManifestMetadata(engine) && !string.IsNullOrWhiteSpace(engine.SourcePath))
            {
                await _engineInstaller.SaveEngineConfigAsync(engine);
                _engineInstaller.InvalidateCache();
            }

            if (!engine.Status.Installed ||
                engine.Update == null ||
                string.IsNullOrWhiteSpace(engine.Update.Repo))
            {
                return ResolveInstalledEngineReleaseTag(engine);
            }

            var probedVersion = await TryProbeEngineRuntimeVersionAsync(engine, ct);
            if (string.IsNullOrWhiteSpace(probedVersion))
            {
                return ResolveInstalledEngineReleaseTag(engine);
            }

            var normalizedTag = NormalizeEngineVersionReference(probedVersion);
            if (ReleaseTagOrdering.AreEquivalentVersionReferences(
                    engine.Status.InstalledReleaseTag ?? string.Empty,
                    normalizedTag))
            {
                return normalizedTag;
            }

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

            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                return await ResolveModuleBranchInstallCandidateAsync(module, isManualRequest, ct);
            }

            return await ResolveModuleReleaseInstallCandidateAsync(module, isManualRequest, ct);
        }

        /// <summary>
        /// Returns selectable release or pre-release versions for one module repository.
        /// </summary>
        public async Task<List<UpdateCandidate>> GetModuleReleaseCandidatesAsync(
            ModuleConfig module,
            CancellationToken ct = default,
            bool isManualRequest = false)
        {
            module.Normalize();
            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(module.Source.Repo))
            {
                return [];
            }

            var includePrerelease = IsPrereleaseMode(module.Update.Mode);
            var releases = await _github.GetReleasesAsync(
                module.Source.Repo,
                includePrerelease,
                ResolveRequestSource(isManualRequest),
                ct);
            var candidates = new List<UpdateCandidate>();

            foreach (var release in releases)
            {
                var downloadUrl = ResolveModuleDownloadUrl(module, release);
                if (string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                candidates.Add(BuildModuleReleaseCandidate(module, release, downloadUrl));
            }

            return candidates;
        }

        /// <summary>
        /// Checks ASLM and every installed module for available updates.
        /// </summary>
        /// <param name="publishUpdateNotifications">When false, skips publishing update-available notifications for every discovery (used when auto-updates will apply immediately).</param>
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
            var modules = await _moduleInstaller.DiscoverModulesAsync();
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
        /// Applies automatic updates for a list returned by <see cref="CheckAllUpdatesAsync"/>: modules immediately,
        /// ASLM self-update prepared for the next launcher start when not already staged.
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
            _moduleInstaller.DiscoverModulesAsync();

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
            // Persist without ModulesChanged: the shell rebuilds module cards on that event and would drop
            // in-memory update-check results (HasUpdate / candidate) tied to the current ModuleViewModel instances.
            _moduleInstaller.SaveModuleConfig(module, raiseModulesChanged: false);
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

                if (string.Equals(
                    pending.CommitSha.Trim(),
                    module.Update.InstalledCommitSha?.Trim(),
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
                return true;
            }

            // Release mode: tag must differ from installed and still match the user's selection policy.
            if (string.IsNullOrWhiteSpace(pending.ReleaseTag))
            {
                return false;
            }

            var installed = ResolveInstalledModuleReleaseTag(module);
            if (ReleaseTagOrdering.AreEquivalentVersionReferences(pending.ReleaseTag, installed))
            {
                return false;
            }

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
            return true;
        }


        // App update preparation

        /// <summary>
        /// Downloads and stages an ASLM app update for the external patcher.
        /// </summary>
        public async Task<bool> PrepareAppUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
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

            TryDeleteDirectory(stagingRoot);
            Directory.CreateDirectory(extractDir);

            var notificationProgress = _notifications.CreateDownloadProgressBridge(operationKey, progress);

            try
            {
                // Download, extract off the UI thread, then persist pending.json for the patcher.
                await _github.DownloadFileAsync(
                    candidate.DownloadUrl,
                    archivePath,
                    log,
                    notificationProgress,
                    ResolveRequestSource(isManualRequest),
                    ct);
                var payloadDir = await Task.Run(() =>
                {
                    // Archive extraction and payload inspection can touch a lot of files, so keep it off the UI thread.
                    ExtractZipSafe(archivePath, extractDir);
                    return ResolveSinglePayloadDirectory(extractDir);
                }, ct);

                // On macOS the payload is the new .app bundle and user data lives outside it,
                // so the swap targets the bundle and nothing needs preserving.
                var macBundleRoot = MacBundleLocator.FindBundleRoot();
                var pending = new PendingAppUpdate
                {
                    Version = candidate.RemoteVersion,
                    StagingPath = payloadDir,
                    TargetRoot = macBundleRoot ?? rootDir,
                    BackupPath = backupPath,
                    Preserve = macBundleRoot != null ? [] : source.Preserve,
                    CreatedUtc = DateTime.UtcNow.ToString("o")
                };

                Directory.CreateDirectory(workDir);
                var pendingJson = JsonSerializer.Serialize(pending, _jsonOptions);
                await File.WriteAllTextAsync(GetPendingUpdatePath(), pendingJson, ct);
                log?.Report("ASLM update prepared. It will be applied on restart.");
                _notifications.CompleteDownload(operationKey, "ASLM update prepared.");
                return true;
            }
            catch (OperationCanceledException)
            {
                _notifications.FailDownload(operationKey, "ASLM update download canceled.");
                throw;
            }
            catch
            {
                _notifications.FailDownload(operationKey, "ASLM update download failed.");
                throw;
            }
        }


        // Module update application

        /// <summary>
        /// Downloads and applies one module update.
        /// </summary>
        public async Task<bool> ApplyModuleUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
            var module = candidate.Module ?? throw new InvalidOperationException(
                "Module update candidate does not contain module metadata.");
            module.Normalize();

            // Skip download and file replacement when the candidate already matches local state.
            if (IsModuleAlreadyAtInstallTarget(module, candidate))
            {
                log?.Report("The selected version is already installed.");
                return false;
            }

            var operationKey = NotificationCenter.BuildOperationKey("module-update", module.Id);
            await _notifications.StartDownloadAsync(
                operationKey,
                "Updating module",
                $"{module.Name} {candidate.RemoteVersion}",
                candidate.TargetKind,
                candidate.TargetId);

            var moduleDir = Path.GetDirectoryName(module.SourcePath);
            if (string.IsNullOrWhiteSpace(moduleDir))
            {
                _notifications.FailDownload(operationKey, "Module update target could not be resolved.");
                return false;
            }

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

                var wasEnabled = module.Status.Enabled;

                // Stop the module and any orphaned processes holding locks under the install directory.
                if (wasEnabled)
                {
                    log?.Report($"Stopping {module.Name} before update...");
                    await _moduleRunner.StopModuleAsync(module.SourcePath);
                    module.Status.Enabled = false;
                    _moduleInstaller.SaveModuleConfig(module, raiseModulesChanged: false);
                }

                await StopProcessesRunningFromDirectoryAsync(moduleDir, log, ct);

                var success = await ApplyPreparedModuleUpdateAsync(
                    module,
                    candidate,
                    preparedUpdate,
                    moduleDir,
                    wasEnabled,
                    log,
                    ct);
                if (success)
                {
                    _notifications.CompleteDownload(operationKey, $"{module.Name} updated successfully.");

                    // Refresh the signed community-reviewed list when the remote trust API is enabled.
                    await _moduleTrustService.RefreshReviewedListAsync(ct);
                }
                else
                {
                    _notifications.FailDownload(operationKey, $"{module.Name} update failed.");
                }

                return success;
            }
            catch (OperationCanceledException)
            {
                _notifications.FailDownload(operationKey, $"{module.Name} update canceled.");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Module update failed for {ModuleId}.", module.Id);
                log?.Report($"Module update failed: {ex.Message}");
                _notifications.FailDownload(operationKey, $"Module update failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDeleteDirectory(updateRoot);
            }
        }

        /// <summary>
        /// Downloads and applies one engine runtime update.
        /// </summary>
        public async Task<bool> ApplyEngineUpdateAsync(
            UpdateCandidate candidate,
            IProgress<string>? log = null,
            IProgress<DownloadProgress>? progress = null,
            bool isManualRequest = false,
            CancellationToken ct = default)
        {
            var engine = candidate.Engine ?? throw new InvalidOperationException(
                "Engine update candidate does not contain engine metadata.");
            engine.Normalize();

            if (IsEngineAlreadyAtInstallTarget(engine, candidate))
            {
                log?.Report("The selected version is already installed.");
                return false;
            }

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
                log?.Report($"Downloading {engine.Name} {candidate.RemoteVersion}...");
                var notificationProgress = _notifications.CreateDownloadProgressBridge(operationKey, progress);
                await _github.DownloadFileAsync(
                    candidate.DownloadUrl,
                    archivePath,
                    log,
                    notificationProgress,
                    ResolveRequestSource(isManualRequest),
                    ct);

                enabledModuleSourcePaths = await CaptureEnabledModuleSourcePathsAsync(ct);
                await StopModulesForEngineUpdateAsync(engine, engineDir, enabledModuleSourcePaths, log, ct);
                modulesWereStopped = true;

                log?.Report($"Stopping {engine.Name} before update...");
                await StopProcessesRunningFromDirectoryAsync(engineDir, log, ct);

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
                _notifications.FailDownload(operationKey, $"{engine.Name} update canceled.");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Engine update failed for {EngineId}.", engine.Id);
                log?.Report($"Engine update failed: {ex.Message}");
                _notifications.FailDownload(operationKey, $"Engine update failed: {ex.Message}");
                return false;
            }
            finally
            {
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
            var modules = await _moduleInstaller.DiscoverModulesAsync();
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

            if (string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                _ollamaSettings.StopManagedRuntime();
            }

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

                try
                {
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

        private static bool IsPlaceholderEngineReleaseTag(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Probes the installed engine runtime for its version string.
        /// </summary>
        private async Task<string?> TryProbeEngineRuntimeVersionAsync(EngineConfig engine, CancellationToken ct)
        {
            if (string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase))
            {
                var apiVersion = await TryProbeOllamaApiVersionAsync(ct);
                if (!string.IsNullOrWhiteSpace(apiVersion))
                {
                    return apiVersion;
                }
            }

            var executablePath = _engineInstaller.GetEngineExecutablePath(engine.Id);
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return null;
            }

            return await TryProbeExecutableVersionAsync(executablePath, ct);
        }

        private static async Task<string?> TryProbeOllamaApiVersionAsync(CancellationToken ct)
        {
            try
            {
                using var client = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(2)
                };
                using var response = await client.GetAsync("http://127.0.0.1:11434/api/version", ct);
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

        private static async Task<string?> TryProbeExecutableVersionAsync(string executablePath, CancellationToken ct)
        {
            try
            {
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

                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                return TryExtractVersionToken(output);
            }
            catch
            {
                return null;
            }
        }

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
            var branches = await _github.GetBranchesAsync(
                module.Source.Repo,
                ResolveRequestSource(isManualRequest),
                ct);
            var selected = branches.FirstOrDefault(branch =>
                string.Equals(branch.Name, module.Update.Branch, StringComparison.OrdinalIgnoreCase));

            if (selected == null ||
                string.IsNullOrWhiteSpace(selected.CommitSha) ||
                string.Equals(selected.CommitSha, module.Update.InstalledCommitSha, StringComparison.OrdinalIgnoreCase))
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
        /// Resolves the branch install target selected by the module manifest.
        /// </summary>
        private async Task<UpdateCandidate?> ResolveModuleBranchInstallCandidateAsync(
            ModuleConfig module,
            bool isManualRequest,
            CancellationToken ct)
        {
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

            if (!string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha) &&
                string.Equals(
                    selected.CommitSha.Trim(),
                    module.Update.InstalledCommitSha.Trim(),
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
        private async Task<UpdateCandidate?> CheckModuleReleaseUpdateAsync(
            ModuleConfig module,
            bool isManualRequest,
            CancellationToken ct)
        {
            var currentTag = ResolveInstalledModuleReleaseTag(module);
            var candidates = await GetModuleReleaseCandidatesAsync(module, ct, isManualRequest);
            if (candidates.Count == 0)
            {
                return null;
            }

            // Pinned release: offer an update only when the selected tag differs from installed.
            if (!IsLatestReleaseSelection(module.Update.SelectedReleaseTag) &&
                !string.IsNullOrWhiteSpace(module.Update.SelectedReleaseTag))
            {
                var selected = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.ReleaseTag, module.Update.SelectedReleaseTag, StringComparison.OrdinalIgnoreCase));

                if (selected == null || string.IsNullOrWhiteSpace(selected.ReleaseTag))
                {
                    return null;
                }

                return !ReleaseTagOrdering.AreEquivalentVersionReferences(selected.ReleaseTag, currentTag) ? selected : null;
            }

            // Latest selection: compare the newest remote tag against the installed baseline.
            var latest = candidates[0];
            return !ReleaseTagOrdering.AreEquivalentVersionReferences(latest.ReleaseTag ?? string.Empty, currentTag)
                ? latest
                : null;
        }

        /// <summary>
        /// Resolves the release install target selected by the module manifest.
        /// </summary>
        private async Task<UpdateCandidate?> ResolveModuleReleaseInstallCandidateAsync(
            ModuleConfig module,
            bool isManualRequest,
            CancellationToken ct)
        {
            var candidates = await GetModuleReleaseCandidatesAsync(module, ct, isManualRequest);
            if (candidates.Count == 0)
            {
                return null;
            }

            // Resolve pinned tag when set; otherwise take the newest release from the candidate list.
            UpdateCandidate resolved;
            if (!IsLatestReleaseSelection(module.Update.SelectedReleaseTag) &&
                !string.IsNullOrWhiteSpace(module.Update.SelectedReleaseTag))
            {
                var selected = candidates.FirstOrDefault(candidate =>
                    string.Equals(candidate.ReleaseTag, module.Update.SelectedReleaseTag, StringComparison.OrdinalIgnoreCase));
                resolved = selected ?? candidates[0];
            }
            else
            {
                resolved = candidates[0];
            }

            if (string.IsNullOrWhiteSpace(resolved.ReleaseTag))
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
            return await ModuleManifestParser.LoadAsync(path, ct);
        }

        /// <summary>
        /// Loads one module configuration directly from a specific manifest path synchronously.
        /// </summary>
        private ModuleConfig? LoadModuleConfigFromPath(string path)
        {
            return ModuleManifestParser.Parse(File.ReadAllText(path), path);
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

                MergeModuleState(module, installed, candidate);
                await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);

                await _moduleEngineReconciler.ReconcileRequiredEnginesAsync(
                    installed,
                    log ?? NoOpProgress<string>.Instance,
                    downloadProgress: null,
                    ct: ct);

                if (installed.Update.RunFirstRunAfterUpdate)
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

                if (wasEnabled && installed.Commands.Run.Count > 0)
                {
                    installed.Status.Enabled = true;
                    await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);
                    _ = Task.Run(() => _moduleRunner.ExecuteRunAsync(
                        installed,
                        log ?? NoOpProgress<string>.Instance,
                        CancellationToken.None));
                }

                installed.Status.LastUpdated = DateTime.UtcNow.ToString("o");
                await _moduleInstaller.SaveConfigAsync(installed, raiseModulesChanged: false);
                log?.Report($"{installed.Name} updated to {candidate.RemoteVersion}.");
                return true;
            }
            catch
            {
                log?.Report("Update failed. Module backup restoration is disabled.");
                throw;
            }
        }

        /// <summary>
        /// Carries forward user state and update metadata from the old module into the new manifest.
        /// </summary>
        private static void MergeModuleState(ModuleConfig oldConfig, ModuleConfig newConfig, UpdateCandidate candidate)
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

            newConfig.Update.Mode = oldConfig.Update.Mode;
            newConfig.Update.Channel = oldConfig.Update.Channel;
            newConfig.Update.Branch = candidate.ReferenceName ?? oldConfig.Update.Branch;
            newConfig.Update.AssetName = oldConfig.Update.AssetName ?? newConfig.Update.AssetName;
            newConfig.Update.RunFirstRunAfterUpdate = oldConfig.Update.RunFirstRunAfterUpdate;
            newConfig.Update.Preserve = oldConfig.Update.Preserve
                .Concat(newConfig.Update.Preserve)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            newConfig.Update.InstalledCommitSha = candidate.CommitSha ?? oldConfig.Update.InstalledCommitSha;
            newConfig.Update.InstalledReleaseTag = candidate.ReleaseTag ?? oldConfig.Update.InstalledReleaseTag;
            newConfig.Update.SelectedReleaseTag =
                string.IsNullOrWhiteSpace(oldConfig.Update.SelectedReleaseTag) ||
                IsLatestReleaseSelection(oldConfig.Update.SelectedReleaseTag)
                    ? ModuleUpdateConfig.LatestReleaseTag
                    : candidate.ReleaseTag ?? oldConfig.Update.SelectedReleaseTag;
            newConfig.Update.PendingUpdate = null;
            newConfig.Update.Normalize();
        }


        // Version helpers

        /// <summary>
        /// Returns whether the selected update channel should include GitHub pre-releases.
        /// </summary>
        private static string ResolveRequestSource(bool isManualRequest) =>
            isManualRequest ? GitHubRequestSources.Manual : GitHubRequestSources.Auto;

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
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha))
            {
                return $"{module.Update.Branch}@{ShortSha(module.Update.InstalledCommitSha)}";
            }

            return ResolveInstalledModuleReleaseTag(module);
        }

        /// <summary>
        /// Returns the locally installed release tag or version string for one module.
        /// </summary>
        private static string ResolveInstalledModuleReleaseTag(ModuleConfig module)
        {
            return module.Update.InstalledReleaseTag ?? module.Status.InstalledVersion ?? module.Version;
        }

        /// <summary>
        /// Returns whether the module manifest records a successful remote source install.
        /// </summary>
        internal static bool HasRecordedRemoteSourceInstall(ModuleConfig module)
        {
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha);
            }

            return !string.IsNullOrWhiteSpace(module.Update.InstalledReleaseTag);
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

            if (!HasRecordedRemoteSourceInstall(module))
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
            if (string.Equals(module.Update.Mode, "branch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Mode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(candidate.CommitSha) ||
                    string.IsNullOrWhiteSpace(module.Update.InstalledCommitSha))
                {
                    return false;
                }

                return string.Equals(
                    candidate.CommitSha.Trim(),
                    module.Update.InstalledCommitSha.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(candidate.ReleaseTag))
            {
                return false;
            }

            if (!HasRecordedRemoteSourceInstall(module))
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

            if (release.Prerelease)
            {
                label += " pre-release";
            }

            if (release.PublishedAt.HasValue)
            {
                label += $" - {release.PublishedAt.Value:yyyy-MM-dd}";
            }

            return label;
        }


        // File system helpers

        /// <summary>
        /// Keeps the source archive extension so extraction can pick zip or tar handling.
        /// </summary>
        private static string GetArchiveFileName(string? downloadUrl, string baseName)
        {
            var url = downloadUrl ?? string.Empty;
            if (url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                return baseName + ".tar.gz";
            }

            if (url.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                return baseName + ".tgz";
            }

            return baseName + ".zip";
        }

        /// <summary>
        /// Extracts one ZIP or tar.gz archive and rejects entries that escape the target directory.
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
                if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                {
                    continue;
                }

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
        /// Stops untracked or orphaned processes whose executable lives under one module directory.
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


        // Helper types

        /// <summary>
        /// Represents a no-op progress sink used when the caller does not provide logging.
        /// </summary>
        private sealed class NoOpProgress<T> : IProgress<T>
        {
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
        private sealed record PreparedModuleUpdate(
            string NewManifestPath,
            ModuleConfig? NewConfig,
            string ModuleSourceDir);
    }
}
