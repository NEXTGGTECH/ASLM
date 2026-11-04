// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Ensures declared module dependencies are installed and ready before a dependent module runs.
    /// </summary>
    public sealed class ModuleDependencyService
    {
        // Fields

        /// <summary>
        /// Ids of the dependencies on the path currently being prepared in this async flow, used to detect cycles.
        /// It lives in an <see cref="AsyncLocal{T}"/> so a nested call in the same flow joins the outer traversal.
        /// </summary>
        private static readonly AsyncLocal<HashSet<string>?> DependencyVisitStack = new();

        /// <summary>
        /// Installer that discovers modules, reconciles their engines and saves their manifests.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Runner that executes the first-run setup of a dependency module.
        /// </summary>
        private readonly ModuleRunner _runner;

        /// <summary>
        /// Logger for dependency preparation failures.
        /// </summary>
        private readonly ILogger<ModuleDependencyService> _logger;


        // Initialization

        /// <summary>
        /// Creates the module dependency service.
        /// </summary>
        public ModuleDependencyService(
            ModuleInstaller installer,
            ModuleRunner runner,
            ILogger<ModuleDependencyService> logger)
        {
            _installer = installer;
            _runner = runner;
            _logger = logger;
        }


        // Dependency preparation

        /// <summary>
        /// Runs first-run setup for every declared module dependency that is not ready yet.
        /// Returns false when a dependency is missing, unsupported, circular or fails to prepare;
        /// the reason is written to <paramref name="log"/>.
        /// </summary>
        public async Task<bool> EnsureFirstRunCompletedAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct)
        {
            // The outermost call creates the visit stack and clears it again; nested calls reuse it.
            var visitStack = DependencyVisitStack.Value;
            var ownsVisitStack = visitStack == null;
            if (ownsVisitStack)
            {
                visitStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DependencyVisitStack.Value = visitStack;
            }

            try
            {
                return await EnsureFirstRunCompletedCoreAsync(module, log, ct, visitStack!);
            }
            finally
            {
                if (ownsVisitStack)
                {
                    DependencyVisitStack.Value = null;
                }
            }
        }

        /// <summary>
        /// Traverses transitive dependencies depth first and runs the setup of each one that is not ready yet,
        /// so deeper dependencies are prepared before the modules that need them.
        /// </summary>
        private async Task<bool> EnsureFirstRunCompletedCoreAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct,
            HashSet<string> visitStack)
        {
            foreach (var dependency in module.Dependencies.Modules)
            {
                var dependencyId = dependency.Id?.Trim();
                if (string.IsNullOrEmpty(dependencyId))
                {
                    continue;
                }

                // Cycle guard: a module that depends on itself, or an id already on the active path, stops the run.
                if (string.Equals(dependencyId, module.Id, StringComparison.OrdinalIgnoreCase))
                {
                    log.Report($"Circular module dependency detected: '{module.Name}' depends on itself.");
                    return false;
                }

                if (!visitStack.Add(dependencyId))
                {
                    log.Report($"Circular module dependency detected involving '{dependencyId}'.");
                    return false;
                }

                try
                {
                    // The dependency must be installed and usable on this platform.
                    var dependencyModule = await ResolveInstalledModuleAsync(dependencyId, log, ct);
                    if (dependencyModule == null)
                    {
                        return false;
                    }

                    if (!dependencyModule.IsSupportedOnCurrentPlatform)
                    {
                        log.Report(
                            $"Required module dependency '{dependencyModule.Name}' does not support {PlatformInfo.PlatformKey}.");
                        return false;
                    }

                    // Prepare the dependency's own dependencies first.
                    if (!await EnsureFirstRunCompletedCoreAsync(dependencyModule, log, ct, visitStack))
                    {
                        return false;
                    }

                    // A dependency that already completed its first run needs no further work.
                    if (dependencyModule.Status.FirstRunCompleted)
                    {
                        continue;
                    }

                    try
                    {
                        // A dependency module needs its own runtimes before its setup commands can execute.
                        await _installer.ReconcileRequiredEnginesAsync(
                            dependencyModule,
                            log,
                            downloadProgress: null,
                            ct: ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(
                            ex,
                            "Engine preparation failed for dependency module '{ModuleId}'.",
                            dependencyId);
                        log.Report(
                            $"✗ Required engines could not be prepared for dependency module '{dependencyModule.Name}': {ex.Message}");
                        return false;
                    }

                    // Run the setup; its own dependencies were handled above, so the runner must not repeat them.
                    log.Report($"[Deps] Running first-run setup for dependency module '{dependencyModule.Name}'...");
                    if (!await _runner.ExecuteFirstRunAsync(dependencyModule, log, ct, skipModuleDependencies: true))
                    {
                        log.Report($"✗ First-run setup failed for dependency module '{dependencyModule.Name}'.");
                        return false;
                    }

                    // Persist the completed setup so the dependency is not prepared again.
                    dependencyModule.Status.Installed = true;
                    dependencyModule.Status.FirstRunCompleted = true;
                    await _installer.SaveConfigAsync(dependencyModule);
                }
                finally
                {
                    // Leave the active path, so the same dependency may legitimately be reached from another branch.
                    visitStack.Remove(dependencyId);
                }
            }

            return true;
        }


        // Module lookup

        /// <summary>
        /// Resolves one installed dependency manifest by its stable module identifier.
        /// Returns null (after logging the reason) when discovery fails or no module has that id;
        /// when several modules share the id, the one with the alphabetically first manifest path wins.
        /// </summary>
        private async Task<ModuleConfig?> ResolveInstalledModuleAsync(
            string moduleId,
            IProgress<string> log,
            CancellationToken ct)
        {
            List<ModuleConfig> modules;
            try
            {
                modules = await _installer.DiscoverModulesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Module discovery failed while resolving dependency '{ModuleId}'.", moduleId);
                log.Report($"✗ Failed to discover modules while resolving dependency '{moduleId}': {ex.Message}");
                return null;
            }

            // Sort by manifest path so the choice among duplicated ids is deterministic.
            var matches = modules
                .Where(module => string.Equals(module.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(module => module.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matches.Count == 0)
            {
                log.Report($"✗ Required module dependency '{moduleId}' was not found.");
                return null;
            }

            if (matches.Count > 1)
            {
                _logger.LogWarning(
                    "Multiple installed modules share dependency id '{ModuleId}'; using manifest at {SourcePath}.",
                    moduleId,
                    matches[0].SourcePath);
            }

            return matches[0];
        }
    }
}
