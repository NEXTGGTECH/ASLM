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
        private static readonly AsyncLocal<HashSet<string>?> DependencyVisitStack = new();

        private readonly ModuleInstaller _installer;
        private readonly ModuleRunner _runner;
        private readonly ILogger<ModuleDependencyService> _logger;

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

        /// <summary>
        /// Runs first-run setup for every declared module dependency that is not ready yet.
        /// </summary>
        public async Task<bool> EnsureFirstRunCompletedAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct)
        {
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
                    var dependencyModule = await ResolveInstalledModuleAsync(dependencyId, log, ct);
                    if (dependencyModule == null)
                    {
                        return false;
                    }

                    if (!await EnsureFirstRunCompletedCoreAsync(dependencyModule, log, ct, visitStack))
                    {
                        return false;
                    }

                    if (dependencyModule.Status.FirstRunCompleted)
                    {
                        continue;
                    }

                    log.Report($"[Deps] Running first-run setup for dependency module '{dependencyModule.Name}'...");
                    if (!await _runner.ExecuteFirstRunAsync(dependencyModule, log, ct, skipModuleDependencies: true))
                    {
                        log.Report($"✗ First-run setup failed for dependency module '{dependencyModule.Name}'.");
                        return false;
                    }

                    dependencyModule.Status.Installed = true;
                    dependencyModule.Status.FirstRunCompleted = true;
                    await _installer.SaveConfigAsync(dependencyModule);
                }
                finally
                {
                    visitStack.Remove(dependencyId);
                }
            }

            return true;
        }

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
