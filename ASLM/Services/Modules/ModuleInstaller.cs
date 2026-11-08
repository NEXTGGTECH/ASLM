// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Discovers module manifests and installs or refreshes module source files.
    /// <para>
    /// Partial class: this part covers module discovery, the bookkeeping of catalog installations that are queued or
    /// running, source and archive installation, and manifest persistence. The removal part
    /// (<c>ModuleInstaller.Removal.cs</c>) covers uninstalling modules and the locking that keeps module content
    /// operations and removals from overlapping.
    /// </para>
    /// </summary>
    public partial class ModuleInstaller
    {
        // Fields and constants

        /// <summary>
        /// Number of attempts to write a manifest before a sharing violation is rethrown.
        /// </summary>
        private const int ManifestWriteAttemptCount = 5;

        /// <summary>
        /// Pause between two manifest write attempts.
        /// </summary>
        private static readonly TimeSpan ManifestWriteRetryDelay = TimeSpan.FromMilliseconds(50);

        /// <summary>
        /// HTTP client used for module archive downloads.
        /// </summary>
        private readonly HttpClient _httpClient = new();

        /// <summary>
        /// Optional GitHub client that authorizes GitHub API downloads and records their rate-limit usage.
        /// </summary>
        private readonly GitHubUpdateClient? _github;

        /// <summary>
        /// Runner that executes module first-run setup and stops running modules before removal.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Trust service whose community-reviewed list is refreshed after an archive installation.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// Installs the engines that an installed module requires or provides.
        /// </summary>
        private readonly ModuleEngineReconciler _moduleEngineReconciler;

        /// <summary>
        /// Engine service used for platform-support checks and for planning engine cleanup on removal.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Optional port registry whose assignments of a module are released when the module is removed.
        /// </summary>
        private readonly PortRegistry? _ports;

        /// <summary>
        /// Registry of the ids of installed modules.
        /// </summary>
        internal ModuleRegistry Registry { get; } = new();

        /// <summary>
        /// Guards the catalog installation bookkeeping: the installations, manual requests, temporary dependencies
        /// and removal states.
        /// </summary>
        private readonly object _installationLock = new();

        /// <summary>
        /// Catalog installations that are queued or running, by their token.
        /// </summary>
        private readonly Dictionary<Guid, CatalogInstallation> _catalogInstallations = [];

        /// <summary>
        /// Ids of modules the user asked to install explicitly; such modules are never rolled back as
        /// temporary dependencies.
        /// </summary>
        private readonly HashSet<string> _manuallyRequested = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Ids of dependency modules whose content was freshly installed for a catalog installation;
        /// they are removed again when that installation is rolled back.
        /// </summary>
        private readonly HashSet<string> _temporaryDependencies = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Removal phase (queued or removing) of the modules that are being removed.
        /// </summary>
        private readonly Dictionary<string, DownloadOperationState> _moduleRemovals = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gate that callers hold while they plan or roll back a catalog installation or queue a removal, so plans
        /// and removals see a consistent view of each other's reservations.
        /// </summary>
        internal SemaphoreSlim CatalogPlanGate { get; } = new(1, 1);

        /// <summary>
        /// JSON settings used to write module manifests.
        /// </summary>
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };


        // Events

        /// <summary>
        /// Raised when module metadata or the set of modules being installed changes.
        /// </summary>
        public event EventHandler? ModulesChanged;


        // Initialization

        /// <summary>
        /// Creates the module installer.
        /// A missing engine installer is replaced by a new one.
        /// </summary>
        public ModuleInstaller(
            ModuleRunner moduleRunner,
            ModuleTrustService moduleTrustService,
            ModuleEngineReconciler moduleEngineReconciler,
            EngineInstaller? engineInstaller = null,
            GitHubUpdateClient? github = null,
            PortRegistry? ports = null)
        {
            _moduleRunner = moduleRunner;
            _github = github;
            _moduleTrustService = moduleTrustService;
            _moduleEngineReconciler = moduleEngineReconciler;
            _engineInstaller = engineInstaller ?? new EngineInstaller();
            _ports = ports;
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ASLM-ModuleInstaller");
        }


        // Discovery

        /// <summary>
        /// Scans <c>Modules/*/ASLM_Module.json</c> files asynchronously.
        /// This is the local catalog: installed modules and catalog entries that are not installed yet.
        /// Manifests that cannot be parsed are skipped. The result is sorted by module name.
        /// </summary>
        public async Task<List<ModuleConfig>> DiscoverModulesAsync()
        {
            var baseDir = GetRootDirectory();
            var modulesRoot = Path.Combine(baseDir, "Modules");
            var modules = new List<ModuleConfig>();

            if (!Directory.Exists(modulesRoot))
            {
                return modules;
            }

            // Only root manifests (Modules/{folder}/ASLM_Module.json) are installed modules.
            var jsonFiles = await Task.Run(() => ModuleManifestDiscovery
                .EnumerateInstalledManifests(modulesRoot)
                .ToList()).ConfigureAwait(false);

            // Use one fresh engine catalog for the entire module discovery pass.
            _engineInstaller.InvalidateCache();

            // Parse all manifests in parallel; unreadable ones come back as null and are dropped.
            var tasks = jsonFiles.Select(path => LoadModuleConfig(path, refreshEngines: false));
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var result in results)
            {
                if (result != null)
                {
                    modules.Add(result);
                }
            }

            return modules
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }


        // Installed and available modules

        /// <summary>
        /// Returns registered modules. The dashboard can also include the current installation plan,
        /// without exposing unfinished modules to services that require an installed module.
        /// </summary>
        /// <param name="includeInstalling">Also returns modules of running or queued catalog installations.</param>
        public async Task<List<ModuleConfig>> DiscoverInstalledModulesAsync(bool includeInstalling = false)
        {
            // A module is installed when the registry lists it (the manifest on disk alone does not count).
            var catalog = await DiscoverModulesAsync().ConfigureAwait(false);
            var ids = Registry.ReadIds();
            var installing = includeInstalling ? GetInstallingModules() : [];
            var installingIds = installing.Select(module => module.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return catalog.Where(module => ids.Contains(module.Id) || installingIds.Contains(module.Id))
                // Keep pending cards visible even while the updater is replacing their manifests.
                .Concat(installing).DistinctBy(module => module.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Returns compatible catalog entries that have not been installed.
        /// Modules of running or queued catalog installations are included, so they stay listed while their
        /// installation runs.
        /// </summary>
        public async Task<List<ModuleConfig>> DiscoverAvailableModulesAsync()
        {
            // Pending modules count as not installed even if they are already registered.
            var catalog = await DiscoverModulesAsync().ConfigureAwait(false);
            var ids = Registry.ReadIds();
            var pending = GetInstallingModules();
            ids.ExceptWith(pending.Select(module => module.Id));
            return catalog
                .Where(module => module.IsSupportedOnCurrentPlatform && !ids.Contains(module.Id))
                .Concat(pending)
                .DistinctBy(module => module.Id, StringComparer.OrdinalIgnoreCase).ToList();
        }


        // Catalog installation tracking

        /// <summary>
        /// Returns whether the module is part of a queued or running catalog installation.
        /// </summary>
        internal bool IsInstalling(string moduleId)
        {
            return GetInstallationState(moduleId) != null;
        }

        /// <summary>
        /// Returns the modules that still have to be installed by all queued or running catalog installations.
        /// </summary>
        private ModuleConfig[] GetInstallingModules()
        {
            lock (_installationLock)
                return _catalogInstallations.Values.SelectMany(request => request.Pending).ToArray();
        }

        /// <summary>
        /// Remembers that the user asked for this module explicitly, so a rollback never treats it as a
        /// temporary dependency.
        /// </summary>
        internal void RecordManualRequest(string id)
        {
            lock (_installationLock) _manuallyRequested.Add(id);
        }

        /// <summary>
        /// Returns the removal phase of a module, or null when the module is not being removed.
        /// </summary>
        internal DownloadOperationState? GetRemovalState(string id)
        {
            lock (_installationLock) return _moduleRemovals.TryGetValue(id, out var state) ? state : null;
        }

        /// <summary>
        /// Sets the removal phase of a module, or clears it when <paramref name="state"/> is null,
        /// and notifies listeners.
        /// </summary>
        internal void SetRemovalState(string id, DownloadOperationState? state)
        {
            lock (_installationLock)
            {
                if (state.HasValue) _moduleRemovals[id] = state.Value;
                else _moduleRemovals.Remove(id);
            }

            RaiseModulesChanged();
        }

        /// <summary>
        /// Returns the comma-separated names of the root modules of all queued or running catalog installations
        /// whose plan includes the given module, or an empty string when no installation needs it.
        /// </summary>
        internal string GetPendingConsumers(string id)
        {
            lock (_installationLock)
                return string.Join(", ", _catalogInstallations.Values.Where(request => request.Plan.Any(module =>
                    string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase))).Select(request => request.Root.Name).Distinct());
        }

        /// <summary>
        /// How a module takes part in the queued or running catalog installations, for display in the UI.
        /// </summary>
        /// <param name="Phase">Furthest phase of the module over all installations that include it.</param>
        /// <param name="IsDependency">True when the module is only installed as a dependency of other modules.</param>
        /// <param name="RequestedBy">Names of the other root modules that require this module.</param>
        internal sealed record InstallationState(DownloadOperationState Phase, bool IsDependency, string RequestedBy)
        {
            /// <summary>
            /// Localized status text for the phase, distinguishing queued, installing and removing modules and
            /// dependencies.
            /// </summary>
            public string Label => L.Get(Phase == DownloadOperationState.Removing ? LocalizationKeys.Modules_Removing
                : Phase == DownloadOperationState.Queued
                    ? IsDependency ? LocalizationKeys.Modules_QueuedDependency : LocalizationKeys.Downloads_Queued
                    : IsDependency ? LocalizationKeys.Modules_InstallingDependency : LocalizationKeys.SetupWizard_Installing);

            /// <summary>
            /// Same text as <see cref="Label"/>, but names the requesting modules for a dependency that is not
            /// being removed.
            /// </summary>
            public string LabelWithRequester => IsDependency && Phase != DownloadOperationState.Removing && !string.IsNullOrWhiteSpace(RequestedBy)
                ? L.Get(Phase == DownloadOperationState.Queued
                    ? LocalizationKeys.Modules_QueuedDependencyFor : LocalizationKeys.Modules_InstallingDependencyFor, RequestedBy)
                : Label;
        }

        /// <summary>
        /// Returns how the module takes part in queued or running catalog installations, or null when it does not.
        /// </summary>
        internal InstallationState? GetInstallationState(string id)
        {
            lock (_installationLock)
            {
                // Collect every installation that still has to install this module.
                var requests = _catalogInstallations.Values.Where(request => request.Pending.Any(module =>
                    string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
                if (requests.Count == 0) return null;

                // It is a dependency when no installation has it as its root; the phase is the furthest one reached.
                var dependency = !requests.Any(request => string.Equals(request.Root.Id, id, StringComparison.OrdinalIgnoreCase));
                return new(requests.Max(request => request.Phases.GetValueOrDefault(id, DownloadOperationState.Queued)),
                    dependency, string.Join(", ", requests.Where(request => !string.Equals(request.Root.Id, id, StringComparison.OrdinalIgnoreCase))
                        .Select(request => request.Root.Name).Distinct(StringComparer.OrdinalIgnoreCase)));
            }
        }

        /// <summary>
        /// Publishes the requested module and dependencies while they are still queued.
        /// The returned reservation lists the plan modules that need work and stays registered until it is disposed.
        /// </summary>
        /// <param name="root">Module the user asked to install.</param>
        /// <param name="plan">Root module and its dependencies in install order.</param>
        /// <param name="reinstall">True when the root is reinstalled even though it is already installed.</param>
        internal CatalogInstallation BeginCatalogInstallation(ModuleConfig root, IReadOnlyList<ModuleConfig> plan,
            bool reinstall = false)
        {
            // Register the installation. Pending modules are those not registered yet, without a completed first run,
            // already part of an installation, being removed, or the reinstalled root.
            var ids = Registry.ReadIds();
            CatalogInstallation scope;
            lock (_installationLock)
            {
                var pending = plan.Where(module => !ids.Contains(module.Id) || !module.Status.FirstRunCompleted ||
                    IsInstalling(module.Id) || GetRemovalState(module.Id) != null ||
                    (reinstall && string.Equals(module.Id, root.Id, StringComparison.OrdinalIgnoreCase))).ToArray();
                scope = new(this, root, plan, pending);
                _catalogInstallations.Add(scope.Token, scope);
            }

            // Tell listeners about the new installation; if that fails, withdraw the registration again.
            try
            {
                RaiseModulesChanged();
                return scope;
            }
            catch
            {
                lock (_installationLock) _catalogInstallations.Remove(scope.Token);
                throw;
            }
        }

        /// <summary>
        /// Reservation for one catalog installation: its root module, dependency plan, the modules that still need
        /// work and the phase of each. Disposing it releases the reservation.
        /// </summary>
        /// <param name="installer">Installer that owns the bookkeeping this reservation is registered in.</param>
        /// <param name="root">Module the user asked to install.</param>
        /// <param name="plan">Root module and its dependencies in install order.</param>
        /// <param name="pending">Plan modules that still have to be installed.</param>
        internal sealed class CatalogInstallation(ModuleInstaller installer, ModuleConfig root,
            IReadOnlyList<ModuleConfig> plan, ModuleConfig[] pending) : IDisposable
        {
            // State

            /// <summary>
            /// Unique key of the reservation in the installer's bookkeeping.
            /// </summary>
            internal Guid Token { get; } = Guid.NewGuid();

            /// <summary>
            /// Module the user asked to install.
            /// </summary>
            internal ModuleConfig Root { get; } = root;

            /// <summary>
            /// Root module and its dependencies in install order.
            /// </summary>
            internal IReadOnlyList<ModuleConfig> Plan { get; } = plan;

            /// <summary>
            /// Plan modules that still have to be installed.
            /// </summary>
            internal ModuleConfig[] Pending { get; } = pending;

            /// <summary>
            /// Current phase of each module of the plan, by module id.
            /// </summary>
            internal Dictionary<string, DownloadOperationState> Phases { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Ids of modules whose content this installation copied freshly; only those may be removed
            /// when the installation is rolled back.
            /// </summary>
            internal HashSet<string> NewlyInstalled { get; } = new(StringComparer.OrdinalIgnoreCase);


            // Progress tracking

            /// <summary>
            /// Records the phase of one module and notifies listeners.
            /// </summary>
            internal void SetPhase(string id, DownloadOperationState phase)
            {
                lock (installer._installationLock) Phases[id] = phase;
                installer.RaiseModulesChanged();
            }

            /// <summary>
            /// Records that this installation freshly installed the module's content.
            /// A module other than the root is also remembered as a temporary dependency.
            /// </summary>
            internal void TrackNewContent(ModuleConfig module)
            {
                lock (installer._installationLock)
                {
                    NewlyInstalled.Add(module.Id);
                    if (!string.Equals(module.Id, Root.Id, StringComparison.OrdinalIgnoreCase))
                        installer._temporaryDependencies.Add(module.Id);
                }
            }


            // Lifecycle

            /// <summary>
            /// Unregisters the installation from the installer and notifies listeners; does nothing when it was
            /// already unregistered.
            /// </summary>
            public void Dispose()
            {
                lock (installer._installationLock)
                    if (!installer._catalogInstallations.Remove(Token)) return;
                installer.RaiseModulesChanged();
            }
        }


        // Single manifest

        /// <summary>
        /// Loads one module configuration from disk.
        /// Returns null when the file does not exist, is not a root module manifest or cannot be parsed.
        /// </summary>
        public Task<ModuleConfig?> LoadModuleConfig(string jsonFile) =>
            LoadModuleConfig(jsonFile, refreshEngines: true);

        /// <summary>
        /// Loads one module configuration and resolves its platform support.
        /// </summary>
        /// <param name="jsonFile">Path of the manifest file.</param>
        /// <param name="refreshEngines">Reloads the engine catalog before resolving; false when the caller already did.</param>
        private async Task<ModuleConfig?> LoadModuleConfig(string jsonFile, bool refreshEngines)
        {
            if (!File.Exists(jsonFile))
            {
                return null;
            }

            // Inside the Modules folder only root manifests (one folder level below it) are module manifests.
            var modulesRoot = Path.Combine(GetRootDirectory(), "Modules");
            if (ModuleManifestDiscovery.IsPathUnderDirectory(modulesRoot, jsonFile) &&
                !ModuleManifestDiscovery.IsInstalledModuleManifest(modulesRoot, jsonFile))
            {
                Debug.WriteLine($"Ignoring nested module manifest: {jsonFile}");
                return null;
            }

            // Parse through the version-aware parser, then decide whether the module can run on this platform.
            try
            {
                var json = await File.ReadAllTextAsync(jsonFile).ConfigureAwait(false);
                var config = ModuleManifestParser.Parse(json, jsonFile);
                ResolvePlatformSupport(config, refreshEngines);
                return config;
            }
            catch (Exception ex)
            {
                // One broken manifest must not hide the other modules.
                Debug.WriteLine($"Failed to parse {jsonFile}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves module compatibility against the engines installation will actually use.
        /// Downloaded definitions replace this module's old embedded engines before files are copied.
        /// </summary>
        internal void ResolvePlatformSupport(ModuleConfig module, bool refreshEngines = true)
        {
            // Optionally reload the engine catalog; engines are only looked up when the module declares engine dependencies.
            if (refreshEngines) _engineInstaller.InvalidateCache();
            var available = module.Dependencies.Engines.Count > 0
                ? _engineInstaller.DiscoverEngines() : [];

            module.ResolveForPlatform(PlatformInfo.OsKey, PlatformInfo.ArchKey, engineId =>
            {
                var existing = available.FirstOrDefault(engine =>
                    string.Equals(engine.Id, engineId, StringComparison.OrdinalIgnoreCase));

                // Keep discovery precedence: standalone engines, then the first module provider.
                if (existing != null && (!existing.IsModuleProvided ||
                    !string.Equals(existing.OwnerModuleId, module.Id, StringComparison.OrdinalIgnoreCase)))
                    return existing;

                // Never validate a new archive using an outdated definition owned by the old module.
                return module.Engines.FirstOrDefault(engine =>
                    string.Equals(engine.Id, engineId, StringComparison.OrdinalIgnoreCase));
            });
        }


        // Source download

        /// <summary>
        /// Downloads the module source archive from GitHub and merges it into the module folder.
        /// Returns true when the source was installed or the module declares no GitHub source (nothing to do),
        /// and false when the platform is unsupported, the module folder is unknown or the download fails
        /// (the reason is written to <paramref name="log"/>).
        /// </summary>
        public async Task<bool> DownloadSourceAsync(
            ModuleConfig module,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            // Register the module as in use, so a removal cannot run while its folder is being rewritten.
            using var operation = await BeginContentOperationAsync(ct, module).ConfigureAwait(false);

            // Refuse modules that cannot run on this platform and skip modules without a GitHub source.
            ResolvePlatformSupport(module);
            if (!module.IsSupportedOnCurrentPlatform)
            {
                log.Report($"Module '{module.Name}' does not support {PlatformInfo.PlatformKey}.");
                return false;
            }

            if (!string.Equals(module.Source.Type, "github", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(module.Source.Repo))
            {
                log.Report("No GitHub source defined, skipping download.");
                return true;
            }

            var moduleDir = Path.GetDirectoryName(module.SourcePath);
            if (string.IsNullOrEmpty(moduleDir))
            {
                return false;
            }

            // Legacy setup installs should keep using main unless the manifest explicitly opted into update tracking.
            var branch = module.HasDeclaredUpdateConfig && !string.IsNullOrWhiteSpace(module.Update.Branch)
                ? module.Update.Branch
                : "main";
            var zipUrl = $"https://api.github.com/repos/{module.Source.Repo}/zipball/{Uri.EscapeDataString(branch)}";
            var tempZip = Path.GetTempFileName();
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "ASLM_ModuleSrc_" + Guid.NewGuid());

            try
            {
                // Download the archive, then extract, validate and merge it on a worker thread.
                log.Report($"Downloading source from: {module.Source.Repo}");
                await DownloadFileAsync(zipUrl, tempZip, log, downloadProgress, ct);

                await Task.Run(() =>
                {
                    // Extract into a temporary folder first so the final module folder is only merged once.
                    Directory.CreateDirectory(tempExtractDir);
                    ZipFile.ExtractToDirectory(tempZip, tempExtractDir);

                    // GitHub archives wrap the repository inside one top-level folder.
                    var innerDir = Directory.GetDirectories(tempExtractDir).FirstOrDefault();
                    var sourceDir = innerDir ?? tempExtractDir;

                    // Validate a downloaded manifest before it can replace the installed one.
                    // Legacy source archives without a manifest continue to use the catalog manifest.
                    var downloadedManifestPath = Path.Combine(sourceDir, ModuleManifestDiscovery.ManifestFileName);
                    if (File.Exists(downloadedManifestPath))
                    {
                        var downloadedConfig = ModuleManifestParser.Parse(
                            File.ReadAllText(downloadedManifestPath),
                            downloadedManifestPath);
                        ResolvePlatformSupport(downloadedConfig);
                        if (!string.Equals(downloadedConfig.Id, module.Id, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Downloaded module id '{downloadedConfig.Id}' does not match '{module.Id}'.");
                        }

                        if (!downloadedConfig.IsSupportedOnCurrentPlatform)
                        {
                            throw new PlatformNotSupportedException(
                                $"Module '{downloadedConfig.Name}' does not support {PlatformInfo.PlatformKey}.");
                        }
                    }

                    // Merge the extracted content into the existing module directory.
                    CopyDirectory(sourceDir, moduleDir);
                }, ct);

                log.Report("Source downloaded.");

                // Keep a downloaded module manageable even if its subsequent first-run setup fails.
                var installed = await LoadModuleConfig(module.SourcePath)
                    ?? throw new InvalidDataException("Downloaded module manifest could not be loaded.");
                await SaveInstalledContentAsync(installed);
                return true;
            }
            catch (Exception ex)
            {
                // Failures are reported through the log instead of an exception.
                log.Report($"Source download failed: {ex.Message}");
                return false;
            }
            finally
            {
                // Temporary files are removed on every path, including failures.
                TryDeleteFile(tempZip);
                TryDeleteDirectory(tempExtractDir);
            }
        }


        // Archive install

        /// <summary>
        /// Records newly copied, validated content, including reinstalls in the same session.
        /// The manifest is saved as installed, which also registers the module, and the removal guard left by an
        /// earlier uninstall of the same module is lifted.
        /// </summary>
        /// <param name="config">Manifest of the module whose content was copied.</param>
        /// <param name="raiseModulesChanged">Whether to notify listeners after the save succeeded.</param>
        internal async Task SaveInstalledContentAsync(ModuleConfig config, bool raiseModulesChanged = true)
        {
            // Register the module as in use and lift the removal guard first, since the save below rejects
            // modules marked as removed.
            using var operation = await BeginContentOperationAsync(CancellationToken.None, config).ConfigureAwait(false);
            var wasRemoved = _removedModuleIds.TryRemove(config.Id, out _);
            try
            {
                config.Status.Installed = true;
                config.Status.InstalledVersion = config.Version;
                config.Status.LastUpdated = DateTime.UtcNow.ToString("o");
                await SaveConfigAsync(config, raiseModulesChanged: false);
            }
            catch
            {
                // The save failed, so the module is still removed: put the guard back.
                if (wasRemoved) _removedModuleIds[config.Id] = 0;
                throw;
            }

            // Disk and registry are committed. A failing observer must not restore the removal guard.
            if (raiseModulesChanged) RaiseModulesChanged();
        }

        /// <summary>
        /// Downloads a module archive, installs it into <c>Modules/{id}</c>, and runs first-run setup.
        /// An existing folder of the same module id is replaced. A failing first-run setup is only logged:
        /// the module stays installed and is returned.
        /// </summary>
        /// <param name="zipUrl">URL of a zip archive that contains an <c>ASLM_Module.json</c>.</param>
        /// <exception cref="InvalidOperationException">Thrown when the archive has no module manifest.</exception>
        /// <exception cref="PlatformNotSupportedException">Thrown when the module does not support this platform.</exception>
        public async Task<ModuleConfig> InstallFromUrlAsync(
            string zipUrl,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default)
        {
            // The module is unknown until the archive is read, so the operation is registered without modules,
            // which makes it exclusive: no removal can run meanwhile.
            using var operation = await BeginContentOperationAsync(ct).ConfigureAwait(false);
            var baseDir = GetRootDirectory();
            var modulesRoot = Path.Combine(baseDir, "Modules");
            var tempZip = Path.GetTempFileName();
            var tempExtractDir = Path.Combine(Path.GetTempPath(), "ASLM_Module_Install_" + Guid.NewGuid());

            try
            {
                // Download the archive into a temporary file and extract it into a temporary folder.
                log.Report($"Downloading module from: {zipUrl}");
                await DownloadFileAsync(zipUrl, tempZip, log, downloadProgress, ct);

                Directory.CreateDirectory(tempExtractDir);

                try
                {
                    log.Report("Extracting archive...");

                    // The first ASLM_Module.json found in the extracted tree defines the module.
                    var jsonFile = await Task.Run(() =>
                    {
                        ZipFile.ExtractToDirectory(tempZip, tempExtractDir);
                        return Directory
                            .EnumerateFiles(tempExtractDir, "ASLM_Module.json", SearchOption.AllDirectories)
                            .FirstOrDefault();
                    }, ct);

                    if (jsonFile == null)
                    {
                        throw new InvalidOperationException("Invalid module: ASLM_Module.json not found in archive.");
                    }

                    // Load the manifest first so the final install location can be derived from the module id.
                    var json = await File.ReadAllTextAsync(jsonFile, ct);
                    var config = ModuleManifestParser.Parse(json, jsonFile);
                    ResolvePlatformSupport(config);
                    if (!config.IsSupportedOnCurrentPlatform)
                    {
                        throw new PlatformNotSupportedException(
                            $"Module '{config.Name}' does not support {PlatformInfo.PlatformKey}.");
                    }

                    var moduleSourceDir = Path.GetDirectoryName(jsonFile)!;
                    var finalDir = Path.Combine(modulesRoot, config.Id);

                    // Replace any previous installation of this module with the extracted folder.
                    log.Report($"Installing to: {finalDir}");

                    await Task.Run(() =>
                    {
                        if (Directory.Exists(finalDir))
                        {
                            log.Report("Removing old version...");
                            Directory.Delete(finalDir, true);
                        }

                        Directory.CreateDirectory(finalDir);

                        // Copy the folder that owns the manifest so extra archive content stays outside the final install.
                        CopyDirectory(moduleSourceDir, finalDir);
                    }, ct);

                    // Point the manifest at its final location and install the engines the module needs.
                    config.SourcePath = Path.Combine(finalDir, "ASLM_Module.json");
                    await _moduleEngineReconciler.ReconcileRequiredEnginesAsync(
                        config,
                        log,
                        downloadProgress,
                        ct);

                    // Mark the module installed, lift an earlier removal guard and persist (which also registers it).
                    config.Status.Installed = true;
                    _removedModuleIds.TryRemove(config.Id, out _);
                    config.Status.InstalledVersion = config.Version;
                    config.Status.LastUpdated = DateTime.UtcNow.ToString("o");

                    await SaveConfigAsync(config);

                    // Run module first-run setup after the files are in their final location.
                    var success = await _moduleRunner.ExecuteFirstRunAsync(config, log, ct);
                    if (success)
                    {
                        await SaveConfigAsync(config);
                        log.Report($"Module '{config.Name}' installed successfully!");
                    }
                    else
                    {
                        log.Report($"Module '{config.Name}' installed, but setup failed.");
                    }

                    // Refresh the signed community-reviewed list when the remote trust API is enabled.
                    await _moduleTrustService.RefreshReviewedListAsync(ct);

                    return config;
                }
                finally
                {
                    // The extracted folder is only a staging area.
                    TryDeleteDirectory(tempExtractDir);
                }
            }
            finally
            {
                TryDeleteFile(tempZip);
            }
        }

        /// <summary>
        /// Reconciles the engine definitions and dependencies declared by an installed module manifest.
        /// Missing engines are installed, and module-provided engines are reinstalled when their definition changed.
        /// </summary>
        public Task ReconcileRequiredEnginesAsync(
            ModuleConfig module,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress = null,
            CancellationToken ct = default) =>
            _moduleEngineReconciler.ReconcileRequiredEnginesAsync(module, log, downloadProgress, ct);


        // File copy

        /// <summary>
        /// Recursively copies one directory into another, merging with existing content
        /// and overwriting files that already exist in the destination.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var subdir in Directory.EnumerateDirectories(sourceDir))
            {
                var destSubdir = Path.Combine(destDir, Path.GetFileName(subdir));
                CopyDirectory(subdir, destSubdir);
            }
        }


        // Download helper

        /// <summary>
        /// Downloads one file and reports throttled progress updates.
        /// GitHub API requests are authorized and counted through the GitHub client when one is available.
        /// </summary>
        private async Task DownloadFileAsync(
            string url,
            string destinationPath,
            IProgress<string> log,
            IProgress<DownloadProgress>? downloadProgress,
            CancellationToken ct)
        {
            // Send the request and fail early on an HTTP error; only the headers are read at this point.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            _github?.PrepareApiRequest(request);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            _github?.TrackApiResponse(request, response);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            log.Report(totalBytes > 0
                ? $"  Downloading: {totalBytes / 1024.0 / 1024.0:F1} MB..."
                : "  Downloading (size unknown)...");

            // Stream the body to the destination file, reporting progress at most every 50 ms.
            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);

            var buffer = new byte[65536];
            long downloaded = 0;
            int bytesRead;
            var throttle = Stopwatch.StartNew();

            var transferLabel = Path.GetFileName(destinationPath);
            downloadProgress?.Report(new DownloadProgress(0, 0, totalBytes, transferLabel));

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;

                if (totalBytes > 0 && throttle.ElapsedMilliseconds >= 50)
                {
                    throttle.Restart();
                    downloadProgress?.Report(new DownloadProgress(
                        (double)downloaded / totalBytes,
                        downloaded,
                        totalBytes,
                        transferLabel));
                }
            }

            // Always finish with a 100% report, even when the throttle skipped the last update.
            downloadProgress?.Report(new DownloadProgress(
                1.0,
                downloaded,
                totalBytes > 0 ? totalBytes : downloaded,
                transferLabel));

            log.Report("  Download complete.");
        }


        // Persistence helpers

        /// <summary>
        /// Returns the application root directory that contains <c>Modules/</c>, <c>Engines/</c> and <c>Data/</c>.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }


        // Saving

        /// <summary>
        /// Saves a module manifest synchronously.
        /// A module marked as installed is also entered in the module registry. Nothing is written for a config
        /// without a source path.
        /// </summary>
        /// <param name="config">Manifest to persist.</param>
        /// <param name="raiseModulesChanged">
        /// When true, raises <see cref="ModulesChanged"/> so hosts reload module lists and dashboards.
        /// Use false for saves that only mirror preferences already held by live view models (for example update-source pickers),
        /// so ephemeral state such as a completed update check is not discarded by rebuilding cards.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the module is being removed, or was removed and has not been reinstalled since.
        /// </exception>
        public void SaveModuleConfig(ModuleConfig config, bool raiseModulesChanged = true)
        {
            // Register the module as in use so a removal cannot overlap the write, and refuse stale removed modules.
            using var operation = BeginContentOperation(config);
            EnsureNotRemoved(config);
            if (string.IsNullOrEmpty(config.SourcePath))
            {
                return;
            }

            // Write the manifest, then make sure an installed module is also listed in the registry.
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            WriteManifest(config.SourcePath, json);
            RegisterInstalledModule(config);
            if (raiseModulesChanged)
            {
                RaiseModulesChanged();
            }
        }

        /// <summary>
        /// Saves only download preferences, including for uninstalled catalog entries.
        /// Never restores stale runtime state, registers a module, or clears removal protection.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the manifest on disk belongs to a different module id.</exception>
        internal void SaveModuleUpdatePreferences(ModuleConfig config)
        {
            // Register the module as in use, then edit the manifest on disk as a JSON tree, so every other
            // property keeps the value stored there.
            using var operation = BeginContentOperation(config);
            var manifest = JsonNode.Parse(File.ReadAllText(config.SourcePath))?.AsObject()
                ?? throw new JsonException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, config.Name));
            if (!string.Equals(manifest["id"]?.GetValue<string>(), config.Id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveStale));

            // Copy only the user-chosen update fields into the stored "update" block, or create the block.
            config.Update.Normalize();
            var preferences = JsonSerializer.SerializeToNode(config.Update, _jsonOptions)!.AsObject();
            if (manifest["update"] is JsonObject stored)
            {
                foreach (var key in new[] { "mode", "channel", "branch", "selectedReleaseTag", "pendingUpdate", "useDefaultChannel" })
                    stored[key] = preferences[key]?.DeepClone();
            }
            else
            {
                manifest["update"] = preferences;
            }

            WriteManifest(config.SourcePath, manifest.ToJsonString(_jsonOptions));
            config.HasDeclaredUpdateConfig = true;
        }

        /// <summary>
        /// Saves a module manifest asynchronously.
        /// Behaves like <see cref="SaveModuleConfig"/> but waits for an overlapping removal instead of failing.
        /// </summary>
        /// <param name="config">Manifest to persist.</param>
        /// <param name="raiseModulesChanged">
        /// When false, skips <see cref="ModulesChanged"/> so hosts do not rebuild module cards mid-flight
        /// (for example during a multi-step module update that would otherwise drop in-progress UI state).
        /// </param>
        /// <exception cref="InvalidOperationException">Thrown when the module was removed and has not been reinstalled since.</exception>
        public async Task SaveConfigAsync(ModuleConfig config, bool raiseModulesChanged = true)
        {
            // Register the module as in use (waiting for an overlapping removal) and refuse stale removed modules.
            using var operation = await BeginContentOperationAsync(CancellationToken.None, config).ConfigureAwait(false);
            EnsureNotRemoved(config);
            if (string.IsNullOrEmpty(config.SourcePath))
            {
                return;
            }

            // Write the manifest, then make sure an installed module is also listed in the registry.
            var json = JsonSerializer.Serialize(config, _jsonOptions);
            await WriteManifestAsync(config.SourcePath, json);
            RegisterInstalledModule(config);
            if (raiseModulesChanged)
            {
                RaiseModulesChanged();
            }
        }

        /// <summary>
        /// Writes a manifest synchronously and retries brief reader/writer collisions.
        /// Other errors, and a collision on the last attempt, are rethrown.
        /// </summary>
        private static void WriteManifest(string path, string json)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(path, json);
                    return;
                }
                catch (IOException ex) when (IsManifestSharingViolation(ex) && attempt < ManifestWriteAttemptCount)
                {
                    // The file is briefly held open by another reader or writer; wait and retry.
                    Thread.Sleep(ManifestWriteRetryDelay);
                }
            }
        }

        /// <summary>
        /// Writes a manifest asynchronously and retries brief reader/writer collisions.
        /// </summary>
        private static async Task WriteManifestAsync(string path, string json)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await File.WriteAllTextAsync(path, json);
                    return;
                }
                catch (IOException ex) when (IsManifestSharingViolation(ex) && attempt < ManifestWriteAttemptCount)
                {
                    // The file is briefly held open by another reader or writer; wait and retry.
                    await Task.Delay(ManifestWriteRetryDelay);
                }
            }
        }

        /// <summary>
        /// Identifies transient Windows sharing and file-lock violations that are safe to retry
        /// (Win32 errors 32 and 33).
        /// </summary>
        private static bool IsManifestSharingViolation(IOException exception)
        {
            var errorCode = exception.HResult & 0xFFFF;
            return errorCode is 32 or 33;
        }


        // Registration

        /// <summary>
        /// Enters a module marked as installed in the module registry, provided its manifest is a root manifest
        /// of the <c>Modules</c> folder.
        /// </summary>
        private void RegisterInstalledModule(ModuleConfig config)
        {
            if (config.Status.Installed && ModuleManifestDiscovery.IsInstalledModuleManifest(
                    Path.Combine(GetRootDirectory(), "Modules"), config.SourcePath))
                Registry.Add(config.Id);
        }


        // Temp cleanup

        /// <summary>
        /// Deletes a temporary file on a best-effort basis.
        /// </summary>
        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // Ignore cleanup failures for temporary files.
            }
        }

        /// <summary>
        /// Deletes a temporary directory on a best-effort basis.
        /// </summary>
        private static void TryDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                }
            }
            catch
            {
                // Ignore cleanup failures for temporary directories.
            }
        }


        // Change notification

        /// <summary>
        /// Notifies listeners that installed module metadata changed.
        /// </summary>
        private void RaiseModulesChanged()
        {
            ModulesChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
