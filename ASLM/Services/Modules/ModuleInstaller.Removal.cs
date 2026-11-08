// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Text.Json;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Removal part of the module installer: uninstalling modules, rolling back canceled catalog installations,
    /// and the locking that keeps module content operations and removals from overlapping.
    /// </summary>
    public partial class ModuleInstaller
    {
        // Fields

        /// <summary>
        /// Guards the content operation set, the removal reservation and the revision counter.
        /// </summary>
        private static readonly object OperationLock = new();

        /// <summary>
        /// Content operations that are currently running, in any module installer instance.
        /// </summary>
        private static readonly HashSet<ContentOperation> ContentOperations = [];

        /// <summary>
        /// Content claimed by the removal that is currently running, or null when no removal is running.
        /// </summary>
        private static ContentAccess? _removalAccess;

        /// <summary>
        /// Counter that increases whenever a content operation or a removal ends. A removal that computed its
        /// content claim earlier recomputes it when the counter changed, because a finished operation may have
        /// changed the module's engine or storage dependencies.
        /// </summary>
        private static long _contentRevision;

        /// <summary>
        /// Ids of modules removed by this installer; saving a manifest of such a module is rejected until the
        /// module is installed again, so stale in-memory state cannot resurrect it.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _removedModuleIds = new(StringComparer.OrdinalIgnoreCase);


        // Content operation locking

        /// <summary>
        /// Registers a synchronous content operation for the given modules and returns the handle that ends it.
        /// Content operations and a removal exclude each other only when their modules, engines or storage paths
        /// overlap; unknown ownership (no modules) stays exclusive. Fails instead of waiting.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when a removal that overlaps the modules is running.</exception>
        internal static IDisposable BeginContentOperation(params ModuleConfig[] modules)
        {
            // Describe what the operation touches, then register it unless a removal is claiming overlapping content.
            var access = ContentAccess.For(modules);
            lock (OperationLock)
            {
                if (_removalAccess?.Overlaps(access) == true)
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveBusy));
                var operation = new ContentOperation(access);
                ContentOperations.Add(operation);
                return operation;
            }
        }

        /// <summary>
        /// Registers a content operation for the given modules and returns the handle that ends it.
        /// Waits (polling every 50 ms) while a removal that overlaps the modules is running.
        /// </summary>
        /// <param name="ct">Cancels the wait.</param>
        /// <param name="modules">Modules the operation touches; none means it touches unknown content and excludes every removal.</param>
        internal static async Task<IDisposable> BeginContentOperationAsync(CancellationToken ct, params ModuleConfig[] modules)
        {
            var access = ContentAccess.For(modules);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lock (OperationLock)
                {
                    if (_removalAccess?.Overlaps(access) != true)
                    {
                        var operation = new ContentOperation(access);
                        ContentOperations.Add(operation);
                        return operation;
                    }
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The content one operation or removal touches: module ids (including the ids of their module
        /// dependencies), engine ids and storage paths. An exclusive access overlaps everything.
        /// </summary>
        /// <param name="Modules">Ids of the modules and of the modules they depend on.</param>
        /// <param name="Engines">Ids of the engines the modules use.</param>
        /// <param name="Paths">Full paths of module folders, custom storage paths, model folders, bridge targets and engine folders.</param>
        /// <param name="Exclusive">True when the touched content is unknown, so the access overlaps every other access.</param>
        private sealed record ContentAccess(HashSet<string> Modules, HashSet<string> Engines, string[] Paths, bool Exclusive)
        {
            /// <summary>
            /// Returns whether two accesses share a module, an engine or overlapping paths, or either is exclusive.
            /// </summary>
            public bool Overlaps(ContentAccess other) => Exclusive || other.Exclusive ||
                Modules.Overlaps(other.Modules) || Engines.Overlaps(other.Engines) ||
                Paths.Any(path => other.Paths.Any(otherPath => PathsOverlap(path, otherPath)));

            /// <summary>
            /// Builds the access of a set of modules. Without modules, or when a path cannot be resolved,
            /// the access is exclusive.
            /// </summary>
            /// <param name="modules">Modules the access describes.</param>
            /// <param name="removalEngines">Engines whose folders are added to the paths when the modules use them.</param>
            public static ContentAccess For(ModuleConfig[] modules, IReadOnlyList<EngineConfig>? removalEngines = null)
            {
                var root = GetRootDirectory();
                var engineIds = modules.SelectMany(GetEngineIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    // Modules: the modules and their module dependencies. Engines: the ids collected above.
                    // Paths: module folders, engine folders, custom storage, model and bridge folders, deduplicated.
                    return new(
                        modules.Select(module => module.Id).Concat(modules.SelectMany(module =>
                            module.Dependencies.Modules.Select(dependency => dependency.Id))).ToHashSet(StringComparer.OrdinalIgnoreCase),
                        engineIds,
                        modules.Select(module => Path.GetDirectoryName(module.SourcePath))
                            .Concat((removalEngines ?? []).Where(engine => engineIds.Contains(engine.Id))
                                .Select(engine => Path.GetDirectoryName(engine.SourcePath)))
                            .Concat(modules.SelectMany(GetCustomPaths))
                            .Concat(modules.SelectMany(module => GetModelDirectories(root, module)))
                            .Concat(modules.SelectMany(module => GetBridgeDirectories(root, module)))
                            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.GetFullPath(path!))
                            .Distinct(PathComparer).ToArray(),
                        modules.Length == 0);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // An unresolved/custom target cannot safely be treated as disjoint storage.
                    return new([], [], [], true);
                }
            }
        }

        /// <summary>
        /// Handle of one registered content operation; disposing it ends the operation.
        /// </summary>
        /// <param name="access">Content the operation touches.</param>
        private sealed class ContentOperation(ContentAccess access) : IDisposable
        {
            /// <summary>
            /// Content the operation touches.
            /// </summary>
            public ContentAccess Access { get; } = access;

            /// <summary>
            /// Ends the operation and advances the content revision so waiting removals recompute their claim.
            /// </summary>
            public void Dispose()
            {
                lock (OperationLock)
                    if (ContentOperations.Remove(this)) _contentRevision++;
            }
        }

        /// <summary>
        /// Rejects a module that was removed by this installer and has not been installed again.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the module is marked as removed.</exception>
        private void EnsureNotRemoved(ModuleConfig module)
        {
            if (_removedModuleIds.ContainsKey(module.Id))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveStale));
        }


        // Uninstall

        /// <summary>
        /// Removes installed content while retaining the manifest and artwork as the local catalog entry.
        /// Returns a cleanup directory only if Windows could not delete staged files after committing removal.
        /// Fails instead of waiting when an overlapping content operation or removal is running.
        /// </summary>
        public Task<string?> UninstallAsync(ModuleConfig selected, CancellationToken ct = default) =>
            UninstallCoreAsync(selected, [], waitForOperations: false, ct);

        /// <summary>
        /// Removes a module like <see cref="UninstallAsync"/> for the download queue: waits for overlapping content
        /// operations instead of failing, and does not refuse removal because of pending installations.
        /// </summary>
        internal Task<string?> UninstallQueuedAsync(ModuleConfig selected, CancellationToken ct) =>
            UninstallCoreAsync(selected, [], waitForOperations: true, ct, checkPendingInstallations: false);

        /// <summary>
        /// Clears a module for immediate reinstallation, including mandatory modules, without removing consumers.
        /// </summary>
        internal Task<string?> RemoveForReinstallAsync(ModuleConfig selected, CancellationToken ct) =>
            UninstallCoreAsync(selected, [], waitForOperations: true, ct, checkPendingInstallations: false, reinstall: true);

        /// <summary>
        /// Removes owned files with shared-storage protection; only a confirmed reinstall may replace required modules.
        /// <para>
        /// Order of work: claim the content of the module exclusively (waiting or failing while overlapping
        /// operations run), check that no installed module or pending installation still needs it, plan which
        /// files are exclusively owned, stop the running module, move the files to a staging folder and update the
        /// manifest and registry, and finally release the claim and notify listeners.
        /// </para>
        /// </summary>
        /// <param name="selected">Module to remove.</param>
        /// <param name="consumers">Modules of other pending installations that must be treated as still installed.</param>
        /// <param name="waitForOperations">True to wait for overlapping content operations; false to fail with a busy error.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="checkPendingInstallations">True to refuse removal while a pending installation plan includes the module.</param>
        /// <param name="reinstall">True to allow removing a required module and one that other modules depend on.</param>
        /// <returns>
        /// A cleanup directory when staged files could not be deleted; null when everything was deleted, when the
        /// module is not installed, or when it is required and this is not a reinstall.
        /// </returns>
        private async Task<string?> UninstallCoreAsync(ModuleConfig selected, IReadOnlyList<ModuleConfig> consumers,
            bool waitForOperations, CancellationToken ct, bool checkPendingInstallations = true, bool reinstall = false)
        {
            // Ordinary uninstall must leave required modules untouched; reinstall is a separate guarded flow.
            if (!reinstall && ModuleRegistry.IsRequired(selected.Id)) return null;

            // Claim the module's content exclusively: retry until no registered content operation and no other
            // removal overlaps it.
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Derive the claim from the manifest on disk and the current engine catalog; the revision
                // detects changes that happen while it is being computed.
                long revision;
                lock (OperationLock) revision = _contentRevision;
                var current = await LoadModuleConfig(selected.SourcePath).ConfigureAwait(false) ?? selected;
                var access = ContentAccess.For([current], _engineInstaller.DiscoverEngines());
                lock (OperationLock)
                {
                    // A completed writer may have changed the target's engine/storage dependencies.
                    if (revision != _contentRevision) continue;
                    if (_removalAccess == null && !ContentOperations.Any(operation => operation.Access.Overlaps(access)))
                    {
                        _removalAccess = access;
                        break;
                    }

                    // Another removal or an overlapping operation is running: fail, or wait and retry.
                    if (!waitForOperations) throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveBusy));
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            // The claim is held from here on; the finally block releases it on every path.
            var changed = false;
            try
            {
                // Only registered modules can be removed.
                var ids = Registry.ReadIds();
                if (!ids.Contains(selected.Id)) return null;

                // Refuse while a pending installation plan still needs the module.
                if (checkPendingInstallations)
                {
                    var requestedBy = GetPendingConsumers(selected.Id);
                    if (!string.IsNullOrEmpty(requestedBy))
                        throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveRequiredFormat, requestedBy));
                }

                // Every registered module must resolve to exactly one manifest; otherwise it cannot be judged
                // which files other modules still depend on.
                var catalog = await DiscoverModulesAsync().ConfigureAwait(false);
                var installed = catalog.Where(module => ids.Contains(module.Id)).ToList();
                if (ids.Any(id => installed.Count(module => string.Equals(module.Id, id, StringComparison.OrdinalIgnoreCase)) != 1))
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));

                // The modules that stay (plus the consumers of other pending installations) must not depend on
                // the target, unless it is being reinstalled.
                var target = installed.Single(module => string.Equals(module.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
                var remaining = installed.Where(module => !ReferenceEquals(module, target)).Concat(consumers)
                    .Where(module => !string.Equals(module.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(module => module.Id, StringComparer.OrdinalIgnoreCase).ToList();
                var dependents = remaining.Where(module => module.Dependencies.Modules.Any(dependency =>
                    string.Equals(dependency.Id, target.Id, StringComparison.OrdinalIgnoreCase))).ToList();
                if (!reinstall && dependents.Count > 0)
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveRequiredFormat,
                        string.Join(", ", dependents.Select(module => module.Name))));

                // The module folder and manifest must be real children of Modules, not links or traversal targets.
                var root = GetRootDirectory();
                var moduleDirectory = Path.GetDirectoryName(target.SourcePath)!;
                ValidateRemovalPath(moduleDirectory, Path.Combine(root, "Modules"));
                ValidateRemovalPath(target.SourcePath, moduleDirectory);
                if (!ModuleManifestDiscovery.IsInstalledModuleManifest(Path.Combine(root, "Modules"), target.SourcePath))
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));

                // Plan which engine, environment and model folders only this module uses (see BuildRemovalPlan),
                // using a freshly discovered engine catalog, and reject trees that contain links.
                _engineInstaller.InvalidateCache();
                var engines = _engineInstaller.DiscoverEngines();
                var plan = BuildRemovalPlan(root, target, remaining, engines);
                foreach (var directory in plan.Directories.Append(moduleDirectory)) ValidateTree(directory);
                ct.ThrowIfCancellationRequested();

                // Stop the module process first, so its files are no longer in use.
                if (_moduleRunner != null)
                {
                    await _moduleRunner.StopModuleAsync(target.SourcePath).ConfigureAwait(false);
                    if (target.Status.Enabled)
                    {
                        // A failed removal must not leave the restored card claiming that its stopped process is running.
                        target.Status.Enabled = false;
                        await WriteManifestAsync(target.SourcePath, JsonSerializer.Serialize(target, _jsonOptions)).ConfigureAwait(false);
                        changed = true;
                    }
                }

                // The accepted reinstall target survives removal; old runtime state and user data do not.
                if (reinstall) target.Update = selected.Update;

                // Move the files away and reset the manifest and registry on a worker thread; afterwards the module
                // counts as removed in this session.
                var result = await Task.Run(() => RemoveInstalledFiles(target, moduleDirectory, plan), ct).ConfigureAwait(false);
                _removedModuleIds[target.Id] = 0;
                changed = true;

                // Removal is committed before releasing assignments, so a failed file transaction keeps its ports.
                try
                {
                    _ports?.RemoveModulePorts(target.Id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The installed registry already excludes this module from API routes.
                    // Startup orphan cleanup retries persistence without reporting a completed uninstall as failed.
                    System.Diagnostics.Debug.WriteLine($"[ModuleInstaller] Could not remove ports for '{target.Id}': {ex}");
                }

                return result;
            }
            finally
            {
                // Always release the claim so waiting operations can resume; the new revision makes other
                // removals recompute their claims. Engine state may have changed, so the cache is dropped too.
                _engineInstaller.InvalidateCache();
                lock (OperationLock)
                {
                    _removalAccess = null;
                    _contentRevision++;
                }

                if (changed) RaiseModulesChanged();
            }
        }


        // Installation rollback

        /// <summary>
        /// Rolls back only fresh content owned exclusively by the canceled request.
        /// Modules are removed in reverse plan order (dependents before their dependencies), and a module is kept
        /// when it was not freshly installed by this request, was requested manually, is required, is part of another
        /// pending installation, or is still needed by an installed module. Removal errors are logged and do not
        /// stop the rollback.
        /// </summary>
        /// <param name="request">Canceled catalog installation to undo.</param>
        /// <param name="log">Receives messages about retained files and failed removals.</param>
        internal async Task RollbackCatalogInstallationAsync(CatalogInstallation request, IProgress<string> log)
        {
            // Plans and rollbacks are serialized through the same gate.
            await CatalogPlanGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Parents first: never remove a dependency while a surviving module still requires it.
                foreach (var candidate in request.Plan.Reverse())
                {
                    // Skip modules this request does not own, and modules that other pending plans need.
                    ModuleConfig[] consumers;
                    lock (_installationLock)
                    {
                        var root = string.Equals(candidate.Id, request.Root.Id, StringComparison.OrdinalIgnoreCase);
                        var owned = root ? request.NewlyInstalled.Contains(candidate.Id)
                            : _temporaryDependencies.Contains(candidate.Id) && !_manuallyRequested.Contains(candidate.Id);
                        if (!owned || ModuleRegistry.IsRequired(candidate.Id)) continue;
                        consumers = _catalogInstallations.Values.Where(other => other.Token != request.Token)
                            .SelectMany(other => other.Plan).ToArray();
                        if (consumers.Any(module => string.Equals(module.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    }

                    // Skip modules that are not installed or that an installed module still depends on.
                    var installed = await DiscoverInstalledModulesAsync().ConfigureAwait(false);
                    if (!installed.Any(module => string.Equals(module.Id, candidate.Id, StringComparison.OrdinalIgnoreCase))) continue;
                    if (installed.Any(module => !string.Equals(module.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                        module.Dependencies.Modules.Any(dependency => string.Equals(dependency.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)))) continue;

                    // Remove the module, waiting for running operations, and report any files left behind.
                    request.SetPhase(candidate.Id, DownloadOperationState.Removing);
                    try
                    {
                        var retained = await UninstallCoreAsync(candidate, consumers, waitForOperations: true,
                            CancellationToken.None, checkPendingInstallations: false).ConfigureAwait(false);
                        if (retained != null) log.Report(L.Get(LocalizationKeys.Modules_RemoveCleanupFormat, retained));
                        lock (_installationLock) _temporaryDependencies.Remove(candidate.Id);
                    }
                    catch (Exception ex)
                    {
                        // Keep recoverable content and report cleanup errors; never broaden removal to force success.
                        log.Report(L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                    }
                }
            }
            finally { CatalogPlanGate.Release(); }
        }


        // Removal planning

        /// <summary>
        /// What a removal deletes besides the module folder.
        /// </summary>
        /// <param name="Directories">Folders that only the removed module uses, without folders nested in another entry.</param>
        /// <param name="Engines">Engines whose runtime is deleted and whose installed state is therefore reset.</param>
        internal sealed record RemovalPlan(List<string> Directories, List<EngineConfig> Engines);

        /// <summary>
        /// Checks all surviving consumers, including command/settings engines and custom paths.
        /// Returns the folders that no remaining module uses: engine runtimes, module environments and model
        /// folders inside ASLM. Throws when ownership of shared files cannot be decided safely.
        /// </summary>
        /// <param name="root">ASLM root directory.</param>
        /// <param name="target">Module being removed.</param>
        /// <param name="remaining">Modules that stay installed after the removal.</param>
        /// <param name="engines">All discovered engines.</param>
        /// <exception cref="InvalidOperationException">Thrown when a remaining module shares files with the target or a path is unsafe.</exception>
        internal static RemovalPlan BuildRemovalPlan(string root, ModuleConfig target,
            IReadOnlyList<ModuleConfig> remaining, IReadOnlyList<EngineConfig> engines)
        {
            // Collect what the target and the surviving modules use: engines and custom storage or bridge folders.
            var directories = new List<string>();
            var unusedEngines = new List<EngineConfig>();
            var targetEngines = GetEngineIds(target);
            var retainedEngines = remaining.SelectMany(GetEngineIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var moduleDirectory = Path.GetDirectoryName(target.SourcePath)!;
            var customReferences = remaining.SelectMany(GetCustomPaths)
                .Concat(remaining.SelectMany(module => GetBridgeDirectories(root, module))).ToList();

            // A surviving alias might point into a candidate directory; do not guess its ownership.
            if (customReferences.Any(HasLinkedAncestor))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));
            if (customReferences.Any(path => PathsOverlap(moduleDirectory, path)))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveSharedFiles));

            // Engines of the target: delete the runtime only when no surviving module uses the engine,
            // and the module's own environment (for example a virtual environment) unless it is shared.
            var modelReferences = remaining.SelectMany(module => GetModelDirectories(root, module)).ToList();
            foreach (var engine in engines.Where(engine => targetEngines.Contains(engine.Id)))
            {
                var engineDirectory = Path.GetDirectoryName(engine.SourcePath)!;
                ValidateRemovalPath(engineDirectory, Path.Combine(root, "Engines"));
                ValidateRemovalPath(engine.SourcePath, engineDirectory);
                var runtime = Path.Combine(engineDirectory, "runtime");
                if (!retainedEngines.Contains(engine.Id) && !customReferences.Any(path => PathsOverlap(engineDirectory, path)))
                {
                    directories.Add(runtime);
                    unusedEngines.Add(engine);
                }

                if (ModuleEnvironmentResolver.HasModuleEnvironment(engine))
                {
                    var environment = ModuleEnvironmentResolver.GetEnvironmentDirectory(target, engine);
                    ValidateRemovalPath(environment, engineDirectory);
                    var sharedEnvironment = remaining.Where(module => GetEngineIds(module).Contains(engine.Id))
                        .Any(module => PathsOverlap(environment, ModuleEnvironmentResolver.GetEnvironmentDirectory(module, engine)));
                    if (!sharedEnvironment && !customReferences.Any(path => PathsOverlap(environment, path)))
                        directories.Add(environment);
                }
            }

            // Model folders inside ASLM are deleted only when nothing else may use them.
            foreach (var models in GetModelDirectories(root, target))
            {
                // A custom location outside ASLM belongs to the user, not to the module uninstaller.
                if (!IsStrictChild(models, Path.Combine(root, "Models"))) continue;
                if (modelReferences.Any(path => PathsOverlap(models, path)) ||
                    customReferences.Any(path => PathsOverlap(models, path))) continue;

                // Model names alone do not identify storage or consumers; keep ambiguous shared stores intact.
                if (remaining.Any(module => module.Dependencies.Models.Count > 0)) continue;
                ValidateRemovalPath(models, Path.Combine(root, "Models"));
                directories.Add(models);
            }

            // Drop duplicates and folders nested inside another entry, so every folder is deleted once.
            var distinct = directories.Distinct(PathComparer).ToList();
            return new RemovalPlan(distinct.Where(path => !distinct.Any(other =>
                !PathComparer.Equals(path, other) && IsStrictChild(path, other))).ToList(), unusedEngines);
        }

        /// <summary>
        /// Returns the ids of all engines a module uses: declared engine dependencies, embedded engines, engines of
        /// first-run and run commands, engines named by settings, the bridge engine, and engines implied by the key
        /// names of engine, path, data and models settings.
        /// </summary>
        private static HashSet<string> GetEngineIds(ModuleConfig module) =>
            module.Dependencies.Engines.Select(dependency => dependency.Id)
                .Concat(module.Engines.Select(engine => engine.Id))
                .Concat(module.Commands.FirstRun.Concat(module.Commands.Run).Select(command => command.Engine))
                .Concat(module.Settings.Select(setting => setting.Engine))
                .Concat(new[] { module.DownloadsBridge?.Engine ?? string.Empty })
                .Concat(module.Settings.Where(setting => setting.NormalizedType is "engine" or "path" or "data" or "models")
                    .Select(setting => GetSettingEngineId(setting.Key)))
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Derives an engine id from a setting key by removing a <c>_models</c>, <c>_path</c> or <c>_data</c> suffix.
        /// </summary>
        private static string GetSettingEngineId(string key) => key.EndsWith("_models", StringComparison.OrdinalIgnoreCase)
            ? key[..^7] : key.EndsWith("_path", StringComparison.OrdinalIgnoreCase) || key.EndsWith("_data", StringComparison.OrdinalIgnoreCase)
                ? key[..^5] : key;

        /// <summary>
        /// Returns the full paths of the custom values of a module's path, data and models settings,
        /// resolved relative to the module folder.
        /// </summary>
        private static IEnumerable<string> GetCustomPaths(ModuleConfig module)
        {
            foreach (var setting in module.Settings.Where(setting => setting.UseCustomValue &&
                         setting.NormalizedType is "path" or "data" or "models"))
            {
                var value = (setting.Value ?? setting.Default)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return Path.GetFullPath(value, Path.GetDirectoryName(module.SourcePath)!);
            }
        }

        /// <summary>
        /// Returns the model folders a module may use: the common <c>Models/{engineId}</c> folder of each engine,
        /// custom values of models settings, and bridge targets that lie inside <c>Models</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when an engine id could escape the <c>Models</c> folder.</exception>
        private static IEnumerable<string> GetModelDirectories(string root, ModuleConfig module)
        {
            // Engine consumers can use the common model store without declaring a models setting.
            foreach (var id in GetEngineIds(module))
            {
                if (id.IndexOfAny(['/', '\\']) >= 0 || id is "." or "..")
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
                yield return Path.Combine(root, "Models", id);
            }

            foreach (var setting in module.Settings.Where(setting => setting.NormalizedType == "models" && setting.UseCustomValue))
            {
                var value = (setting.Value ?? setting.Default)?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                    yield return Path.GetFullPath(value, Path.GetDirectoryName(module.SourcePath)!);
            }

            foreach (var path in GetBridgeDirectories(root, module).Where(path => IsStrictChild(path, Path.Combine(root, "Models"))))
                yield return path;
        }

        /// <summary>
        /// Returns the folders that the module's downloads bridge installs into, resolved from the bridge targets
        /// below the ASLM root, <c>Models</c>, <c>Engines</c>, <c>Modules</c>, <c>Data</c> or <c>Tools</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when a target names an unknown root or leaves its root.</exception>
        private static IEnumerable<string> GetBridgeDirectories(string root, ModuleConfig module)
        {
            if (module.DownloadsBridge == null) yield break;
            foreach (var target in module.DownloadsBridge.Targets.Values)
            {
                var bucket = target.Root.Trim().ToLowerInvariant() switch
                {
                    "root" => root,
                    "models" => Path.Combine(root, "Models"),
                    "engines" => Path.Combine(root, "Engines"),
                    "modules" => Path.Combine(root, "Modules"),
                    "data" => Path.Combine(root, "Data"),
                    "tools" => Path.Combine(root, "Tools"),
                    _ => null
                };
                if (bucket == null) throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnknownDependencies));
                var path = Path.GetFullPath(Path.Combine(bucket, target.Relative));
                if (!PathComparer.Equals(path, bucket) && !IsStrictChild(path, bucket))
                    throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
                yield return path;
            }
        }


        // File removal

        /// <summary>
        /// Deletes the module's files transactionally and resets its manifest to a catalog entry.
        /// <para>
        /// The module folder and the planned folders are first moved into a staging folder. Then the module folder
        /// is recreated with only its artwork, the manifest is reset to an uninstalled state, engine manifests are
        /// reset and the module leaves the registry. If any step fails, everything is moved back and the original
        /// engine manifests are restored. Only when all of this succeeded is the staging folder deleted.
        /// </para>
        /// </summary>
        /// <param name="target">Module being removed; its manifest is reset in place.</param>
        /// <param name="moduleDirectory">Folder of the module.</param>
        /// <param name="plan">Additional folders and engines to remove.</param>
        /// <returns>The staging folder when it could not be deleted completely, otherwise null.</returns>
        private string? RemoveInstalledFiles(ModuleConfig target, string moduleDirectory, RemovalPlan plan)
        {
            // Snapshot everything that must survive or be restorable: engine manifests (for rollback) and the
            // module's icons (recreated in the catalog entry). The staging folder lives below Data/App.
            var stage = Path.Combine(GetRootDirectory(), "Data", "App", "ModuleRemoval-" + Guid.NewGuid().ToString("N"));
            ValidateRemovalPath(stage, Path.Combine(GetRootDirectory(), "Data", "App"));
            var moves = new List<(string Original, string Staged)>();
            var engineManifests = plan.Engines.Where(engine => File.Exists(engine.SourcePath))
                .ToDictionary(engine => engine.SourcePath, engine => File.ReadAllBytes(engine.SourcePath), PathComparer);
            var artwork = new[] { target.IconFullPath, target.SidebarIconFullPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && IsStrictChild(path!, moduleDirectory) && File.Exists(path))
                .Distinct(PathComparer).ToDictionary(path => Path.GetRelativePath(moduleDirectory, path!), path =>
                {
                    ValidateRemovalPath(path!, moduleDirectory);
                    return File.ReadAllBytes(path!);
                });

            Directory.CreateDirectory(stage);
            var catalogCreated = false;
            try
            {
                // Move first, commit metadata last. A locked file or failed registry write rolls back these moves.
                foreach (var directory in plan.Directories.Append(moduleDirectory).Where(Directory.Exists))
                {
                    ValidateTree(directory);
                    var staged = Path.Combine(stage, moves.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Directory.Move(directory, staged);
                    moves.Add((directory, staged));
                }

                // Recreate the module folder as a catalog entry that only holds the saved artwork.
                Directory.CreateDirectory(moduleDirectory);
                catalogCreated = true;
                foreach (var (relative, bytes) in artwork)
                {
                    var destination = Path.Combine(moduleDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, bytes);
                }

                // Reset the manifest to its uninstalled state: no runtime status, pending update, installed update
                // markers or user-chosen setting values remain.
                target.Status = new ModuleStatus();
                target.Update.PendingUpdate = null;
                target.Update.InstalledCommitSha = null;
                target.Update.InstalledReleaseTag = null;
                foreach (var setting in target.Settings)
                {
                    setting.Value = null;
                    setting.UseCustomValue = false;
                }

                File.WriteAllText(target.SourcePath, JsonSerializer.Serialize(target, _jsonOptions));

                // Engines whose runtime was removed no longer count as installed; the module leaves the registry last.
                foreach (var engine in plan.Engines)
                {
                    engine.Status = new EngineStatus();
                    if (engineManifests.ContainsKey(engine.SourcePath))
                        File.WriteAllText(engine.SourcePath, JsonSerializer.Serialize(engine, _jsonOptions));
                }

                Registry.Remove(target.Id);
            }
            catch
            {
                // Roll back: restore the moved folders and the original engine manifests, then rethrow.
                // Never clear the staging area when restoration fails: it contains the recoverable original data.
                if (catalogCreated)
                {
                    ValidateTree(moduleDirectory);
                    Directory.Delete(moduleDirectory, recursive: true);
                }

                foreach (var move in moves.AsEnumerable().Reverse()) Directory.Move(move.Staged, move.Original);
                foreach (var (path, bytes) in engineManifests) File.WriteAllBytes(path, bytes);
                Directory.Delete(stage);
                throw;
            }

            // The removal is committed. Deleting the staged files is best effort: when a file cannot be deleted
            // (for example because Windows keeps it locked), the staging folder is returned so the caller can report it.
            try
            {
                ValidateTree(stage);
                ClearReadOnlyFiles(stage);
                Directory.Delete(stage, recursive: true);
                return null;
            }
            catch (Exception) { return stage; }
        }


        // Path safety

        /// <summary>
        /// Path comparer for the current platform: case-insensitive on Windows and macOS, ordinal elsewhere.
        /// </summary>
        private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>
        /// Returns whether <paramref name="path"/> lies strictly below <paramref name="parent"/>
        /// (the parent itself does not count).
        /// </summary>
        private static bool IsStrictChild(string path, string parent)
        {
            var parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(parentPath,
                PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns whether two paths are equal or one lies below the other.
        /// </summary>
        private static bool PathsOverlap(string first, string second) =>
            PathComparer.Equals(Path.GetFullPath(first), Path.GetFullPath(second)) ||
            IsStrictChild(first, second) || IsStrictChild(second, first);

        /// <summary>
        /// Ensures a path to delete lies strictly below its expected parent and is not reached through a link.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the path is outside the parent or crosses a link.</exception>
        internal static void ValidateRemovalPath(string path, string parent)
        {
            if (!IsStrictChild(path, parent) || HasLinkedAncestor(path))
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
        }

        /// <summary>
        /// Returns whether the path itself or any existing ancestor is a symbolic link or directory junction.
        /// </summary>
        private static bool HasLinkedAncestor(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Rejects a directory that is itself a link. Links inside the tree are allowed and not followed, because
        /// deleting a directory removes the links but never their targets.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the directory is a link.</exception>
        private static void ValidateTree(string directory)
        {
            if (!Directory.Exists(directory)) return;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(L.Get(LocalizationKeys.Modules_RemoveUnsafePath));
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);

                // A venv may contain symlinks. Directory.Delete removes links themselves, never their targets.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) ValidateTree(entry);
            }
        }

        /// <summary>
        /// Clears the read-only attribute of all files below a directory, so they can be deleted.
        /// Links are skipped and not followed.
        /// </summary>
        private static void ClearReadOnlyFiles(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) ClearReadOnlyFiles(entry);
                else File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }
}
