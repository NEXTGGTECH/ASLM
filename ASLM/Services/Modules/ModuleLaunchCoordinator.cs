// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Describes the outcome of a module launch attempt orchestrated by <see cref="ModuleLaunchCoordinator"/>.
    /// </summary>
    public enum ModuleLaunchStatus
    {
        Started,
        AlreadyRunning,
        NotFound,
        NoRunCommands,
        FirstRunFailed,
        Error
    }

    /// <summary>
    /// Returns the result of one coordinated launch attempt.
    /// </summary>
    public sealed record ModuleLaunchResult(
        ModuleLaunchStatus Status,
        string? Message,
        ModuleConfig? EffectiveConfig);

    /// <summary>
    /// Runs the same launch sequence as the module dashboard card (first-run, persist enabled, start run commands).
    /// </summary>
    public sealed class ModuleLaunchCoordinator
    {
        private readonly ModuleInstaller _installer;
        private readonly ModuleRunner _runner;
        private readonly ModuleStartThrottle _startThrottle;
        private readonly ILogger<ModuleLaunchCoordinator> _logger;


        // Initialization

        /// <summary>
        /// Creates the coordinator.
        /// </summary>
        public ModuleLaunchCoordinator(
            ModuleInstaller installer,
            ModuleRunner runner,
            ModuleStartThrottle startThrottle,
            ILogger<ModuleLaunchCoordinator> logger)
        {
            _installer = installer;
            _runner = runner;
            _startThrottle = startThrottle;
            _logger = logger;
        }


        // Launch orchestration

        /// <summary>
        /// Resolves a module by stable id and starts it when it is not already running.
        /// </summary>
        public async Task<ModuleLaunchResult> LaunchOrEnsureRunningAsync(
            string moduleId,
            IProgress<string>? log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                return new ModuleLaunchResult(ModuleLaunchStatus.Error, "moduleId is required.", null);
            }

            var trimmedId = moduleId.Trim();
            List<ModuleConfig> matches;
            try
            {
                // Discover installed modules and match by stable id.
                var modules = await _installer.DiscoverModulesAsync();
                matches = modules
                    .Where(m => string.Equals(m.Id, trimmedId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.SourcePath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Module discovery failed while launching '{ModuleId}'.", trimmedId);
                return new ModuleLaunchResult(ModuleLaunchStatus.Error, ex.Message, null);
            }

            if (matches.Count == 0)
            {
                return new ModuleLaunchResult(ModuleLaunchStatus.NotFound, $"Module '{trimmedId}' was not found.", null);
            }

            if (matches.Count > 1)
            {
                _logger.LogWarning(
                    "Multiple installed modules share id '{ModuleId}'; using manifest at {SourcePath}.",
                    trimmedId,
                    matches[0].SourcePath);
            }

            return await LaunchOrEnsureRunningBySourcePathAsync(matches[0].SourcePath, log, ct);
        }

        /// <summary>
        /// Starts one installed module identified by its manifest path.
        /// </summary>
        public async Task<ModuleLaunchResult> LaunchOrEnsureRunningBySourcePathAsync(
            string moduleSourcePath,
            IProgress<string>? log,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(moduleSourcePath))
            {
                return new ModuleLaunchResult(ModuleLaunchStatus.Error, "moduleSourcePath is required.", null);
            }

            ModuleConfig? discovered;
            try
            {
                discovered = await _installer.LoadModuleConfig(moduleSourcePath.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load module manifest from {SourcePath}.", moduleSourcePath);
                return new ModuleLaunchResult(ModuleLaunchStatus.Error, ex.Message, null);
            }

            if (discovered == null)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.NotFound,
                    $"Manifest at '{moduleSourcePath}' could not be loaded.",
                    null);
            }

            await _startThrottle.WaitAsync(ct);
            try
            {
                return await LaunchOrEnsureRunningCoreAsync(discovered, log, ct);
            }
            finally
            {
                _startThrottle.Release();
            }
        }


        // Core launch

        /// <summary>
        /// Reloads the manifest, runs first-run when needed, enables the module, and starts run commands.
        /// </summary>
        private async Task<ModuleLaunchResult> LaunchOrEnsureRunningCoreAsync(
            ModuleConfig discovered,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var moduleLog = log ?? new Progress<string>(_ => { });

            // Reload the manifest from disk so launch uses the latest config.
            var fresh = await _installer.LoadModuleConfig(discovered.SourcePath);
            if (fresh == null)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.NotFound,
                    $"Manifest for '{discovered.Id}' could not be loaded.",
                    null);
            }

            if (fresh.Commands.Run.Count == 0)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.NoRunCommands,
                    "This module defines no run commands.",
                    fresh);
            }

            var runningPaths = _runner.GetRunningModuleSourcePaths();
            if (runningPaths.Contains(fresh.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                return new ModuleLaunchResult(ModuleLaunchStatus.AlreadyRunning, null, fresh);
            }

            if (!fresh.Status.FirstRunCompleted)
            {
                // Run setup once before the module can be enabled for normal operation.
                var setupSuccess = await Task.Run(
                    () => _runner.ExecuteFirstRunAsync(fresh, moduleLog, ct),
                    ct);

                if (!setupSuccess)
                {
                    return new ModuleLaunchResult(
                        ModuleLaunchStatus.FirstRunFailed,
                        "First-run setup did not complete successfully.",
                        fresh);
                }

                fresh.Status.Installed = true;
                fresh.Status.FirstRunCompleted = true;
                await _installer.SaveConfigAsync(fresh);
            }

            var dependencyFailure = await EnsureDependencyModulesRunningAsync(fresh, moduleLog, ct);
            if (dependencyFailure != null)
            {
                return dependencyFailure;
            }

            fresh.Status.Enabled = true;
            await _installer.SaveConfigAsync(fresh);

            // Start run commands on a background thread so the caller can return immediately.
            _ = Task.Run(
                () => _runner.ExecuteRunAsync(fresh, moduleLog, CancellationToken.None),
                CancellationToken.None);

            return new ModuleLaunchResult(ModuleLaunchStatus.Started, null, fresh);
        }

        /// <summary>
        /// Ensures every declared module dependency is running before the dependent module starts.
        /// </summary>
        private async Task<ModuleLaunchResult?> EnsureDependencyModulesRunningAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct)
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
                    return new ModuleLaunchResult(
                        ModuleLaunchStatus.Error,
                        $"Module '{module.Name}' declares a dependency on itself.",
                        module);
                }

                List<ModuleConfig> matches;
                try
                {
                    var modules = await _installer.DiscoverModulesAsync();
                    matches = modules
                        .Where(candidate => string.Equals(candidate.Id, dependencyId, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Module discovery failed while launching dependency '{ModuleId}'.", dependencyId);
                    return new ModuleLaunchResult(ModuleLaunchStatus.Error, ex.Message, module);
                }

                if (matches.Count == 0)
                {
                    return new ModuleLaunchResult(
                        ModuleLaunchStatus.NotFound,
                        $"Required module dependency '{dependencyId}' was not found.",
                        module);
                }

                if (matches.Count > 1)
                {
                    _logger.LogWarning(
                        "Multiple installed modules share dependency id '{ModuleId}'; using manifest at {SourcePath}.",
                        dependencyId,
                        matches[0].SourcePath);
                }

                log.Report($"[Deps] Ensuring dependency module '{matches[0].Name}' is running...");
                var result = await LaunchOrEnsureRunningCoreAsync(matches[0], log, ct);
                if (result.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning)
                {
                    continue;
                }

                var message = result.Message ?? $"Dependency module '{dependencyId}' could not be started.";
                return new ModuleLaunchResult(result.Status, message, module);
            }

            return null;
        }
    }
}
