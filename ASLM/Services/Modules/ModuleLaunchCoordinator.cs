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
        /// <summary>
        /// The run commands were started successfully.
        /// </summary>
        Started,

        /// <summary>
        /// A process of this module is already running, so nothing was started.
        /// </summary>
        AlreadyRunning,

        /// <summary>
        /// The module or its manifest could not be found or loaded.
        /// </summary>
        NotFound,

        /// <summary>
        /// The manifest defines no run commands, so there is nothing to start.
        /// </summary>
        NoRunCommands,

        /// <summary>
        /// The one-time first-run setup did not complete, so the module was not started.
        /// </summary>
        FirstRunFailed,

        /// <summary>
        /// The launch failed for any other reason; the result message holds the details.
        /// </summary>
        Error
    }

    /// <summary>
    /// Returns the result of one coordinated launch attempt.
    /// </summary>
    /// <param name="Status">Outcome of the launch attempt.</param>
    /// <param name="Message">Human-readable failure or status detail, or null on success.</param>
    /// <param name="EffectiveConfig">The freshly loaded manifest the launch worked with, or null when none could be loaded.</param>
    public sealed record ModuleLaunchResult(
        ModuleLaunchStatus Status,
        string? Message,
        ModuleConfig? EffectiveConfig);

    /// <summary>
    /// Runs the same launch sequence as the module dashboard card (first-run, persist enabled, start run commands).
    /// </summary>
    public sealed class ModuleLaunchCoordinator
    {
        // Fields

        /// <summary>
        /// Installer used to discover, load and save module manifests and to reconcile required engines.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Runner that executes first-run setup and starts the module run commands.
        /// </summary>
        private readonly ModuleRunner _runner;

        /// <summary>
        /// Shared limit on concurrent module starts; one slot is held for the whole launch.
        /// </summary>
        private readonly ModuleStartThrottle _startThrottle;

        /// <summary>
        /// Logger for discovery, load and launch failures.
        /// </summary>
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
        /// Discovery failures and unknown ids are reported through the result instead of exceptions.
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
                var modules = await _installer.DiscoverInstalledModulesAsync();
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

            // Duplicate ids are tolerated: matches are ordered by manifest path, so the first one is stable.
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
        /// Holds a content operation and one start-throttle slot for the whole launch, including dependencies.
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

            // Load the manifest; a load exception becomes an Error result and a missing manifest a NotFound result.
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

            // Run the launch inside a content operation (registers the module as in use, so a removal cannot run
            // underneath it, and waits while a conflicting removal is in progress) and take one throttle slot. The slot is released in finally only when it was actually acquired, so a
            // cancellation while waiting does not release a slot that was never taken.
            var launchSlotAcquired = false;
            try
            {
                using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, discovered).ConfigureAwait(false);
                await _startThrottle.WaitAsync(ct);
                launchSlotAcquired = true;
                return await LaunchOrEnsureRunningCoreAsync(discovered, log, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Module launch was canceled for manifest {SourcePath}.",
                    discovered.SourcePath);
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.Error,
                    "Module launch was canceled.",
                    discovered);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Module launch failed for manifest {SourcePath}.",
                    discovered.SourcePath);
                return new ModuleLaunchResult(ModuleLaunchStatus.Error, ex.Message, discovered);
            }
            finally
            {
                if (launchSlotAcquired)
                {
                    _startThrottle.Release();
                }
            }
        }


        // Core launch

        /// <summary>
        /// Reloads the manifest, runs first-run when needed, enables the module, and starts run commands.
        /// The caller owns the start-throttle slot; dependency launches reuse it instead of acquiring another one.
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

            // Reject modules that cannot run here or have nothing to start.
            if (!fresh.IsSupportedOnCurrentPlatform)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.Error,
                    $"Module '{fresh.Name}' does not support {PlatformInfo.PlatformKey}.",
                    fresh);
            }

            if (fresh.Commands.Run.Count == 0)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.NoRunCommands,
                    "This module defines no run commands.",
                    fresh);
            }

            // A module that is already running is a success for callers that only need it to be up.
            var runningPaths = _runner.GetRunningModuleSourcePaths();
            if (runningPaths.Contains(fresh.SourcePath, StringComparer.OrdinalIgnoreCase))
            {
                return new ModuleLaunchResult(ModuleLaunchStatus.AlreadyRunning, null, fresh);
            }

            // Required runtimes must be ready before setup resolves libraries, settings, or command engines.
            await _installer.ReconcileRequiredEnginesAsync(
                fresh,
                moduleLog,
                downloadProgress: null,
                ct: ct);

            var completedFirstRunDuringLaunch = false;
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
                completedFirstRunDuringLaunch = true;

                // Persist completed setup without rebuilding module cards while launch still owns the old view model.
                await _installer.SaveConfigAsync(fresh, raiseModulesChanged: false);
            }

            // Start the modules this one depends on; any dependency failure aborts this launch.
            var dependencyFailure = await EnsureDependencyModulesRunningAsync(fresh, moduleLog, ct);
            if (dependencyFailure != null)
            {
                // Publish the persisted setup state once when a dependency prevents normal startup.
                if (completedFirstRunDuringLaunch)
                {
                    await _installer.SaveConfigAsync(fresh);
                }

                return dependencyFailure;
            }

            // Persist the enabled state before the run commands start.
            fresh.Status.Enabled = true;
            await _installer.SaveConfigAsync(fresh);

            // Keep the launch in Starting state through settings synchronization
            // and until every root run process has actually been created.
            var runStarted = await _runner.ExecuteRunAsync(fresh, moduleLog, ct);
            if (!runStarted)
            {
                return new ModuleLaunchResult(
                    ModuleLaunchStatus.Error,
                    $"One or more run commands for '{fresh.Name}' failed to start.",
                    fresh);
            }

            return new ModuleLaunchResult(ModuleLaunchStatus.Started, null, fresh);
        }

        /// <summary>
        /// Ensures every declared module dependency is running before the dependent module starts.
        /// Returns null when all dependencies are running, otherwise the failure result for the dependent module.
        /// </summary>
        private async Task<ModuleLaunchResult?> EnsureDependencyModulesRunningAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct)
        {
            foreach (var dependency in module.Dependencies.Modules)
            {
                // Skip entries without an id.
                var dependencyId = dependency.Id?.Trim();
                if (string.IsNullOrEmpty(dependencyId))
                {
                    continue;
                }

                // A module that depends on itself could never start, so fail instead of recursing.
                if (string.Equals(dependencyId, module.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return new ModuleLaunchResult(
                        ModuleLaunchStatus.Error,
                        $"Module '{module.Name}' declares a dependency on itself.",
                        module);
                }

                // Find the dependency manifest by id; duplicates resolve to the first manifest path.
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

                // Launch the dependency through the core path: the throttle slot of the outer launch is already held,
                // so acquiring another one could deadlock when all slots are taken by waiting launches.
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
