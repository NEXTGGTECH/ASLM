// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ASLM.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Executes module setup, runtime, and settings commands and tracks their processes.
    /// A module run owns its root process and every captured descendant until a stop has verified that all of
    /// them ended; start and stop of one module are serialized by a per-module lifecycle gate.
    /// </summary>
    public class ModuleRunner : IDisposable
    {
        // Fields

        /// <summary>
        /// Resolves installed engines and their executables for engine-based commands and settings.
        /// </summary>
        private readonly EngineInstaller _engineInstaller;

        /// <summary>
        /// Creates per-module engine environments and resolves the executables that run inside them.
        /// </summary>
        private readonly ModuleEnvironmentResolver _environmentResolver;

        /// <summary>
        /// Registry that assigns the ports injected into module settings.
        /// </summary>
        private readonly PortRegistry _ports;

        /// <summary>
        /// Groups started processes under ASLM, so they end together with the application.
        /// </summary>
        private readonly ProcessTracker _processTracker;

        /// <summary>
        /// Receives console output and process sessions of every command.
        /// </summary>
        private readonly ModuleConsoleStore _consoleStore;

        /// <summary>
        /// Shared cache of process-table snapshots used to find descendant processes.
        /// </summary>
        private readonly ProcessSnapshotReader _processSnapshots;

        /// <summary>
        /// Builds the host theme JSON delivered to modules that declare a theme setting.
        /// </summary>
        private readonly ModuleThemePayloadBuilder _themePayloadBuilder;

        /// <summary>
        /// Builds the host locale JSON delivered to modules that declare a locale setting.
        /// </summary>
        private readonly ModuleLocalePayloadBuilder _localePayloadBuilder;

        /// <summary>
        /// Decides whether a module is trusted enough to receive host account keys.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// Source of the connected GitHub personal access token.
        /// </summary>
        private readonly GitHubAccountStore _githubAccountStore;

        /// <summary>
        /// Source of the connected ASLM account refresh token.
        /// </summary>
        private readonly SunriseService _sunriseService;

        /// <summary>
        /// Knows the URL and port of the module interop listener for modules that opted in.
        /// </summary>
        private readonly ModuleInteropHostState _interopHostState;

        /// <summary>
        /// Resolves services that are needed only occasionally, such as the module dependency service.
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Logger for command failures and process cleanup problems.
        /// </summary>
        private readonly ILogger<ModuleRunner> _logger;

        /// <summary>
        /// Limits how many module setting get/set commands run at the same time.
        /// </summary>
        private readonly SemaphoreSlim _settingCommandThrottle = new(4, 4);

        /// <summary>
        /// UTF-8 encoding without a byte order mark, used for payload files read by module commands.
        /// </summary>
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Whether the runner was disposed; new launches are rejected afterwards.
        /// </summary>
        private bool _disposed;


        // Running process state

        /// <summary>
        /// Owns root and descendant process handles until a module is fully stopped.
        /// </summary>
        private readonly ConcurrentDictionary<string, List<Process>> _runningProcesses = new();

        /// <summary>
        /// Module configs stored at launch time for modules with tracked processes, keyed by manifest path.
        /// </summary>
        private readonly ConcurrentDictionary<string, ModuleConfig> _runningModules = new();

        /// <summary>
        /// One gate per manifest path that serializes start and stop of that module.
        /// </summary>
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();

        /// <summary>
        /// Current run per manifest path: stop signal plus the tasks that belong to it.
        /// </summary>
        private readonly Dictionary<string, ModuleRun> _runs = new();

        /// <summary>
        /// Guards the run table and the tracked process lists, and makes process registration atomic with process creation.
        /// </summary>
        private readonly object _processLock = new();


        // Nested types

        /// <summary>
        /// State of one start of a module: its stop signal and the tasks that must end before it is stopped.
        /// </summary>
        private sealed class ModuleRun
        {
            /// <summary>
            /// Cancelled to stop the run; cancels pending command creation as well as existing processes.
            /// </summary>
            public CancellationTokenSource Stop { get; } = new();

            /// <summary>
            /// Tasks of the started run commands, completed when their processes exit.
            /// </summary>
            public List<Task> Commands { get; } = [];

            /// <summary>
            /// Tasks that poll descendant processes of the started run commands.
            /// </summary>
            public List<Task> Observers { get; } = [];
        }


        // Initialization

        /// <summary>
        /// Creates the module runner.
        /// </summary>
        /// <param name="engineInstaller">Service to resolve engine paths.</param>
        /// <param name="ports">Service to assign ports for settings resolution.</param>
        /// <param name="processTracker">Service that groups child processes under ASLM.</param>
        /// <param name="consoleStore">Service to report console output and process sessions.</param>
        /// <param name="processSnapshots">Service that shares cached process-table snapshots.</param>
        /// <param name="themePayloadBuilder">Builds host theme JSON for modules that declare a theme setting.</param>
        /// <param name="localePayloadBuilder">Builds host locale JSON for modules that declare a locale setting.</param>
        /// <param name="moduleTrustService">Resolves whether a module may receive host account keys.</param>
        /// <param name="githubAccountStore">Provides the connected GitHub personal access token.</param>
        /// <param name="sunriseService">Provides the connected ASLM account refresh token.</param>
        /// <param name="interopHostState">Tracks the module interop listener URL for opted-in modules.</param>
        /// <param name="serviceProvider">Resolves optional services such as <see cref="ModuleDependencyService"/>.</param>
        /// <param name="logger">Logger instance.</param>
        public ModuleRunner(
            EngineInstaller engineInstaller,
            ModuleEnvironmentResolver environmentResolver,
            PortRegistry ports,
            ProcessTracker processTracker,
            ModuleConsoleStore consoleStore,
            ProcessSnapshotReader processSnapshots,
            ModuleThemePayloadBuilder themePayloadBuilder,
            ModuleLocalePayloadBuilder localePayloadBuilder,
            ModuleTrustService moduleTrustService,
            GitHubAccountStore githubAccountStore,
            SunriseService sunriseService,
            ModuleInteropHostState interopHostState,
            IServiceProvider serviceProvider,
            ILogger<ModuleRunner> logger)
        {
            _engineInstaller = engineInstaller;
            _environmentResolver = environmentResolver;
            _ports = ports;
            _processTracker = processTracker;
            _consoleStore = consoleStore;
            _processSnapshots = processSnapshots ?? new ProcessSnapshotReader();
            _themePayloadBuilder = themePayloadBuilder;
            _localePayloadBuilder = localePayloadBuilder;
            _moduleTrustService = moduleTrustService;
            _githubAccountStore = githubAccountStore;
            _sunriseService = sunriseService;
            _interopHostState = interopHostState;
            _serviceProvider = serviceProvider;
            _logger = logger;
            _ports.PortsRedistributed += OnPortsRedistributed;
        }


        // Setup execution

        /// <summary>
        /// Executes first-run setup for a module: dependent modules, engine libraries, host-controlled settings
        /// and finally the declared first-run commands.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="log">Progress reporter for logging output.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="skipModuleDependencies">True to skip the first run of dependency modules because the caller already handles them.</param>
        /// <returns>True if all setup steps succeeded; otherwise, false.</returns>
        public async Task<bool> ExecuteFirstRunAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct,
            bool skipModuleDependencies = false)
        {
            // Hold a content lease while setup runs, register the module in the consoles and mirror all log
            // messages into its overview console.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            _consoleStore.EnsureModule(module);
            var moduleLog = CreateModuleLog(module, log);

            // Modules this one depends on must have completed their own first run before it.
            if (!skipModuleDependencies &&
                module.Dependencies.Modules.Count > 0)
            {
                var dependencyService = _serviceProvider.GetRequiredService<ModuleDependencyService>();
                if (!await dependencyService.EnsureFirstRunCompletedAsync(module, moduleLog, ct))
                {
                    return false;
                }
            }

            // 1. Install engine dependencies (libraries) first
            if (!await InstallDependenciesAsync(module, moduleLog, ct))
                return false;

            await SynchronizeDeclaredModuleSettingsAsync(module, moduleLog, ct);

            // 2. Run firstRun commands
            if (module.Commands.FirstRun.Count == 0)
            {
                moduleLog.Report("No first-run commands defined.");
                module.Status.Installed = true;
                module.Status.FirstRunCompleted = true;
                return true;
            }

            moduleLog.Report($"Running {module.Commands.FirstRun.Count} setup command(s)...");

            // Run the setup commands one after another; the first failure aborts the setup and leaves the
            // installed and first-run flags unchanged.
            foreach (var cmd in module.Commands.FirstRun)
            {
                if (ct.IsCancellationRequested) return false;

                moduleLog.Report($"[Setup] {cmd.Name}: {cmd.Description}");
                bool success = await RunCommandAsync(module, cmd, moduleLog, ct, trackProcess: false, sessionStage: "Setup");

                if (!success)
                {
                    moduleLog.Report($"✗ Setup failed at step: {cmd.Name}");
                    return false;
                }
            }

            // Every step succeeded: mark the module as installed with its first run completed.
            module.Status.Installed = true;
            module.Status.FirstRunCompleted = true;
            return true;
        }


        // Run execution

        /// <summary>
        /// Executes the long-running run commands for a module.
        /// Returns once every root process has been created, not when the processes exit.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="log">Progress reporter for logging output.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if commands were started successfully.</returns>
        public async Task<bool> ExecuteRunAsync(ModuleConfig module, IProgress<string> log, CancellationToken ct)
        {
            // Content leases must precede lifecycle gates: uninstall holds an exclusive
            // content lease while it calls StopModuleAsync.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            var gate = _lifecycleGates.GetOrAdd(module.SourcePath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Refuse to start on a disposed runner, register the module in the consoles and mirror the log.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _consoleStore.EnsureModule(module);
                var moduleLog = CreateModuleLog(module, log);
                if (module.Commands.Run.Count == 0)
                {
                    moduleLog.Report($"No run commands for {module.Name}.");
                    return true;
                }

                // A module that is already running (and not being stopped) is left untouched.
                lock (_processLock)
                {
                    CaptureDescendants(module.SourcePath);
                    if (_runs.TryGetValue(module.SourcePath, out var current) &&
                        !current.Stop.IsCancellationRequested &&
                        _runningProcesses.TryGetValue(module.SourcePath, out var processes) &&
                        processes.Any(IsProcessRunning))
                        return true;
                }

                // Release handles from a previous, naturally completed run before
                // replacing it. Failed stops keep their ownership information for retry.
                await StopModuleCoreAsync(module.SourcePath).ConfigureAwait(false);

                // Register the new run before any command starts, so a concurrent stop can always find and cancel it.
                // The startup token ends when the caller cancels or when the run is stopped.
                var run = new ModuleRun();
                lock (_processLock)
                    _runs.Add(module.SourcePath, run);
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Stop.Token);
                var startTasks = new List<Task<bool>>();

                try
                {
                    // Prepare ports and host-controlled settings, because commands read them at startup.
                    _ports.GetOrAssignPorts(module);
                    _ports.EnsurePortsAvailable(module.Id);
                    await SynchronizeDeclaredModuleSettingsAsync(module, moduleLog, startup.Token).ConfigureAwait(false);
                    moduleLog.Report($"Starting {module.Name}...");

                    // Start all run commands concurrently. Each command completes its start signal once its root
                    // process is tracked (or fails to start); the command task itself lives until the process exits.
                    foreach (var cmd in module.Commands.Run)
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        moduleLog.Report($"[Run] {cmd.Name}: {cmd.Description}");
                        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        startTasks.Add(started.Task);
                        run.Commands.Add(RunCommandAsync(module, cmd, moduleLog, startup.Token,
                            trackProcess: true, sessionStage: "Run", processStarted: started));
                    }

                    // The launch succeeds only when every root process was created.
                    var results = await Task.WhenAll(startTasks).WaitAsync(startup.Token).ConfigureAwait(false);
                    startup.Token.ThrowIfCancellationRequested();
                    if (results.All(started => started))
                        return true;
                    moduleLog.Report($"One or more run commands for {module.Name} failed to start.");
                }
                catch (OperationCanceledException) when (startup.IsCancellationRequested)
                {
                    // Stop cancels pending command creation as well as existing processes.
                }
                catch
                {
                    // Unexpected failure: tear down whatever was started so no orphan processes remain, then rethrow.
                    run.Stop.Cancel();
                    await Task.WhenAll(startTasks).ConfigureAwait(false);
                    await StopModuleCoreAsync(module.SourcePath).ConfigureAwait(false);
                    throw;
                }

                // A command failed to start or the launch was cancelled: stop the partial run, then report
                // cancellation to a cancelled caller and failure otherwise.
                run.Stop.Cancel();
                await Task.WhenAll(startTasks).ConfigureAwait(false);
                await StopModuleCoreAsync(module.SourcePath).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return false;
            }
            finally
            {
                gate.Release();
            }
        }


        // Settings synchronization

        /// <summary>
        /// Reconciles only host-controlled module settings before module commands start.
        /// A setting is written through its set command only when its current value differs from the resolved one.
        /// </summary>
        private async Task SynchronizeDeclaredModuleSettingsAsync(ModuleConfig module, IProgress<string> moduleLog, CancellationToken ct)
        {
            if (module.Settings == null || module.Settings.Count == 0)
            {
                return;
            }

            // Only settings that opt in to synchronization on launch and declare a set command take part.
            var settingsToSync = module.Settings
                .Where(static setting =>
                    setting.IsSynchronizedOnLaunch &&
                    !string.IsNullOrWhiteSpace(setting.SetExec))
                .ToList();

            if (settingsToSync.Count == 0)
            {
                return;
            }

            moduleLog.Report("Synchronizing host-controlled module settings...");

            // Resolve the value each setting should have; account keys of unverified modules are skipped.
            var targets = new List<(ModuleSetting Setting, string Target)>();
            foreach (var setting in settingsToSync)
            {
                // Account keys are resolved only for modules accepted by the trust service.
                if (setting.IsHostKey)
                {
                    if (!TryResolveHostKeyValue(module, setting, out var hostKeyValue))
                    {
                        moduleLog.Report($"[Sync] Skipping '{setting.Key}' for an unverified module.");
                        continue;
                    }

                    targets.Add((setting, hostKeyValue));
                    continue;
                }

                targets.Add((setting, ResolveSettingValue(module, setting)));
            }

            // Read the current values in parallel (the setting command throttle bounds the concurrency). A setting
            // without a get command is always written.
            var checkTasks = targets.Select(async t =>
            {
                if (string.IsNullOrEmpty(t.Setting.GetExec))
                {
                    return (t.Setting, t.Target, NeedsUpdate: true);
                }

                var current = await ExecuteSettingCommandAsync(module, t.Setting, isSet: false, newValue: null, ct);
                var upToDate = current != null &&
                               string.Equals(current.Trim(), t.Target.Trim(), StringComparison.OrdinalIgnoreCase);

                if (upToDate)
                {
                    moduleLog.Report($"[Sync] '{t.Setting.Key}' is already up to date ({current!.Trim()})");
                }

                return (t.Setting, t.Target, NeedsUpdate: !upToDate);
            });

            // Wait for all comparisons, then apply the outdated values one by one; a cancellation between writes
            // stops the synchronization.
            var results = await Task.WhenAll(checkTasks);

            foreach (var (setting, target, needsUpdate) in results)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (needsUpdate)
                {
                    moduleLog.Report($"[Sync] Applying '{setting.Key}' = {target}");
                    await ExecuteSettingCommandAsync(module, setting, isSet: true, newValue: target, ct);
                }
            }
        }


        // Properties

        /// <summary>
        /// Console store that receives the output and process sessions of every command run by this runner.
        /// </summary>
        internal ModuleConsoleStore ConsoleStore => _consoleStore;


        // Module stop

        /// <summary>
        /// Stops all tracked processes for one module.
        /// Throws when a process is still alive after the stop timeout.
        /// </summary>
        /// <param name="moduleSourcePath">The module's SourcePath (unique per instance).</param>
        public async Task StopModuleAsync(string moduleSourcePath)
        {
            using var activity = _consoleStore.BeginActivity(moduleSourcePath, ModuleActivity.Stopping);
            _consoleStore.AppendOverviewLine(moduleSourcePath, "Stopping module processes...");

            // Signal before waiting for the start gate, otherwise an unfinished
            // environment/settings command could keep stop waiting indefinitely.
            lock (_processLock)
            {
                if (_runs.TryGetValue(moduleSourcePath, out var run))
                    run.Stop.Cancel();
            }

            // Wait for any start or stop of the same module to finish, then terminate the processes under the gate.
            var gate = _lifecycleGates.GetOrAdd(moduleSourcePath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await StopModuleCoreAsync(moduleSourcePath).ConfigureAwait(false);
                _consoleStore.UpdateModuleEnabledState(moduleSourcePath, false);
                _consoleStore.AppendOverviewLine(moduleSourcePath, "Module processes stopped.");
            }
            finally
            {
                gate.Release();
            }
        }


        // Running module queries

        /// <summary>
        /// Returns the source paths of modules that currently have tracked running processes.
        /// Modules whose run is being stopped are not reported.
        /// </summary>
        public IReadOnlyList<string> GetRunningModuleSourcePaths()
        {
            lock (_processLock)
            {
                var runningPaths = new List<string>();

                foreach (var pair in _runningProcesses)
                {
                    // A launch request must wait for a stopping run and start afresh,
                    // not return AlreadyRunning for processes that are being killed.
                    if (_runs.TryGetValue(pair.Key, out var run) && run.Stop.IsCancellationRequested)
                        continue;

                    // When no tracked process is left running (the root may be a launcher that exited), look for
                    // surviving descendants that keep the module alive.
                    if (!pair.Value.Any(IsProcessRunning))
                        CaptureDescendants(pair.Key);

                    // A handle that can no longer be queried counts as not running.
                    var hasLiveProcess = pair.Value.Any(static process =>
                    {
                        try
                        {
                            return !process.HasExited;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (hasLiveProcess)
                    {
                        runningPaths.Add(pair.Key);
                    }
                }

                return runningPaths;
            }
        }

        /// <summary>
        /// Returns stable identifiers for modules that currently have tracked live processes.
        /// </summary>
        public IReadOnlyList<RunningModuleSnapshot> GetRunningModulesSnapshot()
        {
            lock (_processLock)
            {
                var results = new List<RunningModuleSnapshot>();

                // Report a module only when it has a live process and its launch-time config is still known.
                foreach (var pair in _runningProcesses)
                {
                    var hasLiveProcess = pair.Value.Any(static process =>
                    {
                        try
                        {
                            return !process.HasExited;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!hasLiveProcess)
                    {
                        continue;
                    }

                    if (_runningModules.TryGetValue(pair.Key, out var module))
                    {
                        results.Add(new RunningModuleSnapshot(module.Id, module.Name, module.SourcePath));
                    }
                }

                return results;
            }
        }

        /// <summary>
        /// Returns the module configurations for instances that currently have tracked live processes.
        /// The returned configs are the same snapshots stored at process launch time.
        /// </summary>
        public IReadOnlyList<ModuleConfig> GetRunningModuleConfigs()
        {
            lock (_processLock)
            {
                var results = new List<ModuleConfig>();

                // Same selection as the identifier snapshot, but returning the stored launch-time configs.
                foreach (var pair in _runningProcesses)
                {
                    var hasLiveProcess = pair.Value.Any(static process =>
                    {
                        try
                        {
                            return !process.HasExited;
                        }
                        catch
                        {
                            return false;
                        }
                    });

                    if (!hasLiveProcess)
                        continue;

                    if (_runningModules.TryGetValue(pair.Key, out var module))
                        results.Add(module);
                }

                return results;
            }
        }


        // Port redistribution

        /// <summary>
        /// Restarts currently running modules after the shared port map changes.
        /// The restart runs in the background; its failures are logged, not thrown to the event raiser.
        /// </summary>
        private void OnPortsRedistributed(object? sender, EventArgs e)
        {
            _ = RestartRunningModulesAfterPortRedistributionAsync();
        }

        /// <summary>
        /// Restarts live modules so their injected port settings match the redistributed port map.
        /// </summary>
        private async Task RestartRunningModulesAfterPortRedistributionAsync()
        {
            try
            {
                // Modules are restarted one at a time: stop (which verifies that the processes ended), a short pause,
                // then a background start. The module shows a Restarting activity until the start is scheduled.
                var modulesToRestart = GetRunningModuleSnapshots();
                foreach (var module in modulesToRestart)
                {
                    using var activity = _consoleStore.BeginActivity(module.SourcePath, ModuleActivity.Restarting);
                    _logger.LogInformation("Restarting module '{ModuleName}' after port redistribution.", module.Name);
                    await StopModuleAsync(module.SourcePath);
                    await Task.Delay(500);

                    var restartLog = new Progress<string>(message =>
                        Debug.WriteLine($"[Port Redistribution:{module.Name}] {message}"));
                    _ = Task.Run(() => ExecuteRunAsync(module, restartLog, CancellationToken.None));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restart modules after port redistribution.");
            }
        }

        /// <summary>
        /// Returns tracked module configs that still have live owned processes and declare run commands.
        /// </summary>
        private List<ModuleConfig> GetRunningModuleSnapshots()
        {
            lock (_processLock)
            {
                return _runningProcesses
                    .Where(static pair => pair.Value.Any(static process =>
                    {
                        try
                        {
                            return !process.HasExited;
                        }
                        catch
                        {
                            return false;
                        }
                    }))
                    .Select(pair => _runningModules.TryGetValue(pair.Key, out var module) ? module : null)
                    .OfType<ModuleConfig>()
                    .Where(static module => module.Commands.Run.Count > 0)
                    .ToList();
            }
        }


        // Global stop

        /// <summary>
        /// Stops every tracked module process.
        /// Modules are stopped in parallel; each one goes through the regular per-module stop.
        /// </summary>
        public async Task StopAllModulesAsync()
        {
            // Include modules that only have a pending run (no process yet) so their startup is cancelled too.
            string[] paths;
            lock (_processLock)
                paths = _runs.Keys.Union(_runningProcesses.Keys).ToArray();
            _logger.LogInformation("Stopping all module processes ({Count} modules)...", paths.Length);
            await Task.WhenAll(paths.Select(StopModuleAsync)).ConfigureAwait(false);
            _logger.LogInformation("All module processes stopped.");
        }


        // Settings propagation

        /// <summary>
        /// Injects resolved module settings into the process environment.
        /// Each setting becomes <c>ASLM_&lt;KEY&gt;</c>; host-managed types such as theme, locale and account
        /// keys are not exposed this way.
        /// </summary>
        private void InjectSettingsIntoEnvironment(ModuleConfig module, ProcessStartInfo psi)
        {
            if (module.Settings == null) return;

            // Host-managed values (theme, locale, account keys) are delivered through other channels, so the
            // process environment carries only the plain settings.
            foreach (var setting in module.Settings)
            {
                if (IsHostManagedSetting(setting.NormalizedType))
                {
                    continue;
                }

                var resolved = ResolveSettingValue(module, setting);
                var envKey = $"ASLM_{setting.Key.ToUpperInvariant()}";
                psi.Environment[envKey] = resolved;
            }

            // Also expose some useful module context values to child processes.
            psi.Environment["ASLM_MODULE_ID"] = module.Id;
            psi.Environment["ASLM_MODULE_DIR"] = Path.GetDirectoryName(module.SourcePath) ?? "";

            // Modules that opted in to interop learn where the host listener is, but only while it is running.
            if (module.ModuleInterop?.IsClientEnabled == true &&
                _interopHostState.TryGetListening(out var interopBaseUrl, out var interopPort))
            {
                psi.Environment["ASLM_MODULE_INTEROP_BASE_URL"] = interopBaseUrl;
                psi.Environment["ASLM_MODULE_INTEROP_PORT"] = interopPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Returns the effective setting value that ASLM resolves for a module, parsed to the type of the setting.
        /// </summary>
        public object? GetResolvedSettingValue(ModuleConfig module, ModuleSetting setting)
        {
            var resolved = ResolveSettingValue(module, setting);
            return setting.ParseSerializedValue(resolved);
        }

        /// <summary>
        /// Resolves the effective string value for one module setting.
        /// The result depends on the setting type: ports, engine availability, engine paths and host payloads are
        /// computed by ASLM, every other type uses the stored or default value.
        /// </summary>
        private string ResolveSettingValue(ModuleConfig module, ModuleSetting setting)
        {
            switch (setting.NormalizedType)
            {
                // Port: the port assigned by the registry; the stored or default value only when none is assigned.
                case "port":
                    var ports = _ports.GetOrAssignPorts(module);
                    if (ports.TryGetValue(setting.Key, out var assigned))
                        return assigned.ToString();

                    var rawPort = (setting.Value ?? setting.Default)?.ToString();
                    if (int.TryParse(rawPort, out var p))
                        return p.ToString();
                    return rawPort ?? string.Empty;

                // Engine: "true" when the engine is installed; for an unknown engine the stored or default flag.
                case "engine":
                    if (_engineInstaller.HasEngine(setting.Key))
                    {
                        return _engineInstaller.GetEngineConfig(setting.Key) != null ? "true" : "false";
                    }

                    var rawEngine = (setting.Value ?? setting.Default)?.ToString();
                    return rawEngine != null && rawEngine.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

                // Path: location of the engine executable unless the user supplied a custom value.
                case "path":
                    if (setting.UseCustomValue)
                    {
                        return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
                    }

                    // Returns the engine executable path, or empty string if not installed.
                    var pathEngineId = TrimEngineSettingSuffix(setting.Key);
                    var enginePath = _engineInstaller.GetEngineExecutablePath(pathEngineId);
                    return !string.IsNullOrEmpty(enginePath) ? enginePath.Replace('\\', '/') : "";

                // Data: per-engine data directory unless the user supplied a custom value.
                case "data":
                    if (setting.UseCustomValue)
                    {
                        return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
                    }

                    // Returns the engine data path: <root>/Data/<engineId>/.
                    var dataEngineId = TrimEngineSettingSuffix(setting.Key);
                    if (_engineInstaller.GetEngineConfig(dataEngineId) == null) return "";

                    var rootDir = GetRootDirectory();
                    return (Path.Combine(rootDir, "Data", dataEngineId) + Path.DirectorySeparatorChar).Replace('\\', '/');

                // Models: per-engine models directory unless the user supplied a custom value.
                case "models":
                    if (setting.UseCustomValue)
                    {
                        return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
                    }

                    // Returns the engine models path: <root>/Models/<engineId>/.
                    var modelsEngineId = TrimEngineSettingSuffix(setting.Key);
                    if (_engineInstaller.GetEngineConfig(modelsEngineId) == null) return "";

                    var modelsRootDir = GetRootDirectory();
                    return (Path.Combine(modelsRootDir, "Models", modelsEngineId) + Path.DirectorySeparatorChar).Replace('\\', '/');

                // Bool: normalized to the strings "true" and "false".
                case "bool":
                    var rawBool = (setting.Value ?? setting.Default)?.ToString();
                    return rawBool != null && rawBool.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

                // Host-generated payloads: the current theme and locale as JSON.
                case "theme":
                    return _themePayloadBuilder.BuildJson();

                case "locale":
                    return _localePayloadBuilder.BuildJson();

                // Account keys: the real token unless the module is unreviewed or no account is connected,
                // in which case the placeholder "None" is used.
                case "key-aslm":
                case "key-gh":
                    return TryResolveHostKeyValue(module, setting, out var hostKeyValue)
                        ? hostKeyValue
                        : "None";

                // Any other type uses the stored value, falling back to the default.
                default:
                    return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
            }
        }

        /// <summary>
        /// Resolves one host account key after verifying the requesting module.
        /// Returns false for unreviewed modules and unknown key types; <paramref name="value"/> is then <c>None</c>.
        /// When a reviewed module has no connected account, the method still returns true with the value <c>None</c>.
        /// </summary>
        private bool TryResolveHostKeyValue(
            ModuleConfig module,
            ModuleSetting setting,
            out string value)
        {
            // Unreviewed modules never receive account keys.
            value = "None";
            if (_moduleTrustService.Resolve(module) == ModuleTrustLevel.Unreviewed)
            {
                return false;
            }

            if (setting.NormalizedType == "key-gh")
            {
                var token = _githubAccountStore.GetPersonalAccessToken();
                value = string.IsNullOrWhiteSpace(token) ? "None" : token;
                return true;
            }

            if (setting.NormalizedType == "key-aslm")
            {
                value = _sunriseService.TryGetRefreshToken(out var refreshToken)
                    ? refreshToken
                    : "None";
                return true;
            }

            return false;
        }


        // Dependency install

        /// <summary>
        /// Installs engine-specific libraries required by a module.
        /// Engines without libraries or without a package manager are skipped; a missing engine or a failing
        /// install aborts with false.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="log">Progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if dependencies installed successfully.</returns>
        private async Task<bool> InstallDependenciesAsync(ModuleConfig module, IProgress<string> log, CancellationToken ct)
        {
            foreach (var engineDep in module.Dependencies.Engines)
            {
                // Skip engines without libraries; the others need an installed engine with a package manager.
                if (engineDep.Libraries.Count == 0)
                    continue;

                var engineConfig = _engineInstaller.GetEngineConfig(engineDep.Id);
                if (engineConfig == null)
                {
                    log.Report($"✗ Engine '{engineDep.Id}' not found or not installed.");
                    return false;
                }

                if (engineConfig.PackageManager == null &&
                    string.IsNullOrWhiteSpace(engineConfig.ModuleEnvironment?.PackageManagerCommand))
                {
                    log.Report($"⚠ Engine '{engineDep.Id}' has no packageManager defined, skipping library install.");
                    continue;
                }

                // Make sure the module's own environment exists, then build the package manager command line
                // (for example pip install into that environment).
                var environment = await _environmentResolver.EnsureEnvironmentAsync(module, engineConfig, log, ct);
                var exePath = _environmentResolver.ResolvePackageManagerExecutable(environment, engineConfig);
                var libs = string.Join(" ", engineDep.Libraries);
                var args = _environmentResolver.BuildPackageInstallArguments(environment, engineConfig, engineDep.Libraries);
                log.Report($"[Deps] Installing libraries for {engineDep.Id}: {libs}");

                // Run the package manager hidden in the module folder, with the environment variables of the engine.
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(module.SourcePath) ?? "",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureProcessForStreaming(psi);
                _environmentResolver.ApplyEnvironmentVariables(module, engineConfig, psi);

                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                {
                    log.Report($"✗ Failed to start package manager for {engineDep.Id}");
                    return false;
                }

                // Standard input is not used, so close it right away.
                process.StandardInput.Close();

                // Show the install in the module's consoles as an untracked process session.
                var sessionHandle = _consoleStore.StartProcessSession(
                    module,
                    new ModuleCommand
                    {
                        Name = $"Dependencies: {engineDep.Id}",
                        Description = $"Install libraries via {args}",
                        Exec = args
                    },
                    "Dependencies",
                    $"Exec: {Path.GetFileName(exePath)} {args}",
                    process,
                    isTrackedProcess: false);

                // Assign to the job object so dependency installs stay grouped under ASLM.
                _processTracker.AddProcess(process);

                // Read the output into the session and the log, and wait until the installer has exited and its
                // output has ended; a non-zero exit code aborts the setup.
                var output = ReadProcessOutputAsync(process, sessionHandle, log);
                await process.WaitForExitAsync(ct);
                await output.WaitAsync(ct);
                _consoleStore.CompleteProcessSession(sessionHandle, process.ExitCode);

                if (process.ExitCode != 0)
                {
                    log.Report($"✗ Library install failed with exit code {process.ExitCode}");
                    return false;
                }

                log.Report($"✓ Libraries installed for {engineDep.Id}");
            }

            return true;
        }


        // Command execution

        /// <summary>
        /// Executes one module command and waits for its process to exit.
        /// Failures are reported through <paramref name="log"/> and the return value instead of exceptions;
        /// for a tracked command the returned task lives as long as the process.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="cmd">The command to execute.</param>
        /// <param name="log">Progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="trackProcess">If true, the process is tracked for lifecycle management (long-running).</param>
        /// <param name="injectSettings">True to expose the module settings to the process as environment variables.</param>
        /// <param name="sessionStage">Stage label shown for the console session of the process.</param>
        /// <param name="processStarted">Optional signal completed after the root process is tracked.</param>
        /// <returns>True if the command executed successfully (exit code 0).</returns>
        public async Task<bool> RunCommandAsync(
            ModuleConfig module,
            ModuleCommand cmd,
            IProgress<string> log,
            CancellationToken ct,
            bool trackProcess = false,
            bool injectSettings = true,
            string sessionStage = "Command",
            TaskCompletionSource<bool>? processStarted = null)
        {
            // State shared with the finally block: which resources exist and must be released on every exit path.
            Process? process = null;
            ModuleConsoleSessionHandle? session = null;
            bool registered = false;
            int? exitCode = null;
            try
            {
                // The command runs in the module folder.
                var moduleDir = Path.GetDirectoryName(module.SourcePath);
                if (string.IsNullOrEmpty(moduleDir)) return false;

                string fileName;
                string arguments = string.Empty;
                EngineConfig? commandEngineConfig = null;

                // 1. Resolve Execution Strategy
                var execString = cmd.Exec ?? string.Empty;

                // Replace {key} placeholders with setting values
                if (module.Settings != null)
                {
                    foreach (var setting in module.Settings)
                    {
                        var resolved = ResolveSettingValue(module, setting);
                        execString = execString.Replace($"{{{setting.Key}}}", resolved);
                    }
                }

                if (!string.IsNullOrEmpty(cmd.Engine))
                {
                    // Run via Engine (e.g. Python)
                    commandEngineConfig = _engineInstaller.GetEngineConfig(cmd.Engine);
                    if (commandEngineConfig == null)
                    {
                        log.Report($"Error: Engine '{cmd.Engine}' not found or not installed.");
                        return false;
                    }

                    var environment = await _environmentResolver.EnsureEnvironmentAsync(module, commandEngineConfig, log, ct);
                    fileName = _environmentResolver.ResolveCommandExecutable(environment, commandEngineConfig);

                    // Combine engine + script/args
                    // cmd.Exec = "manage.py runserver"
                    // We need to ensure we run it in the module directory context
                    arguments = execString;
                }
                else
                {
                    // Run directly (e.g. valid executable on PATH)
                    // Properly parse the command string to handle quotes
                    var parts = SplitCommand(execString);
                    if (parts.Count == 0) return false;

                    fileName = parts[0];
                    arguments = parts.Count > 1 ? ParseArguments(parts.Skip(1)) : "";
                }

                // 2. Prepare Process
                // Hidden window and redirected streams, so output can be captured into the consoles.
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = moduleDir,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureProcessForStreaming(psi);
                if (commandEngineConfig != null)
                {
                    _environmentResolver.ApplyEnvironmentVariables(module, commandEngineConfig, psi);
                }

                // Apply settings as environment variables so the module can consume them dynamically
                if (injectSettings)
                {
                    InjectSettingsIntoEnvironment(module, psi);
                }

                var execMessage = $"Exec: {Path.GetFileName(fileName)} {arguments}";
                log.Report(execMessage);

                process = new Process { StartInfo = psi };

                // 3. Start the process
                // Register atomically with process creation, before console/UI events
                // can announce a running module. Stop cannot miss a late root.
                ModuleRun? run = null;
                lock (_processLock)
                {
                    // Refuse to start once the caller cancelled, the runner was disposed or the run is being stopped.
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (trackProcess && _runs.TryGetValue(module.SourcePath, out run))
                        run.Stop.Token.ThrowIfCancellationRequested();
                    if (!process.Start())
                    {
                        log.Report("Failed to start process.");
                        return false;
                    }

                    // Only tracked commands are registered for lifecycle management.
                    if (trackProcess)
                    {
                        // Keep the handle (including after root exit) until stop has
                        // reaped descendants. A PID alone is not process ownership.
                        _runningProcesses.GetOrAdd(module.SourcePath, _ => []).Add(process);
                        _runningModules[module.SourcePath] = module;
                        registered = true;

                        // Read Handle and StartTime now, while the process is certainly alive: descendant capture
                        // compares start times with this parent even after it has exited.
                        _ = process.Handle;
                        _ = process.StartTime;
                    }
                }

                // Standard input is not used, so close it right away.
                process.StandardInput.Close();

                // Open a console session for the process; its echo line is the executed command line.
                var sessionHandle = _consoleStore.StartProcessSession(
                    module,
                    cmd,
                    sessionStage,
                    execMessage,
                    process,
                    isTrackedProcess: trackProcess);
                session = sessionHandle;

                // Assign to the job object so launched processes stay grouped under ASLM.
                _processTracker?.AddProcess(process);

                // Tracked run commands get an observer that keeps listing the process's descendants in the console.
                if (run != null)
                {
                    run.Observers.Add(MonitorObservedProcessesAsync(module, sessionHandle, process.Id, run.Stop.Token));
                }

                // Start reading output, then tell the launcher that the root process exists.
                var output = ReadProcessOutputAsync(process, sessionHandle, log);
                processStarted?.TrySetResult(true);

                // A launch token controls setup and process creation only. Once a
                // tracked run command starts, module stop owns its lifetime.
                var lifetimeToken = trackProcess ? CancellationToken.None : ct;
                using var cancellationRegistration = lifetimeToken.Register(
                    static state => TerminateCanceledProcess((Process)state!),
                    process);

                // The command ends when its process has exited and its output has ended; children that inherited
                // the output keep it open, so their output still reaches the session.
                await process.WaitForExitAsync(lifetimeToken).ConfigureAwait(false);
                await output.WaitAsync(lifetimeToken).ConfigureAwait(false);
                exitCode = process.ExitCode;

                // A launcher may exit while its children keep running: capture them before the root is forgotten.
                if (registered)
                {
                    lock (_processLock)
                        CaptureDescendants(module.SourcePath);
                }

                if (exitCode == 0)
                    return true;
                log.Report($"Process exited with code {exitCode}");
                return false;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is reported as a failed command, not as an exception.
                log.Report("Operation canceled.");
                return false;
            }
            catch (Exception ex)
            {
                // Any other error is logged and reported as a failed command.
                log.Report($"Execution error: {ex.Message}");
                _logger.LogError(ex, "Command execution failed");
                return false;
            }
            finally
            {
                // Close the console session with the exit code (null when the process never ran to completion).
                if (session is { } handle)
                    _consoleStore.CompleteProcessSession(handle, exitCode);

                // Tracked process handles belong to StopModuleCoreAsync, not the
                // command task: disposing here races stop and loses exited parents.
                if (!registered)
                    process?.Dispose();

                // Every failure path must release launch callers waiting for the
                // process-start handshake. TrySet is harmless after success.
                processStarted?.TrySetResult(false);
            }
        }


        // Setting commands

        /// <summary>
        /// Executes a get or set command declared by one module setting.
        /// A set command receives the new value in place of the <c>{value}</c> token; JSON payloads of host-managed
        /// settings are passed through a temporary file whose path replaces the token.
        /// </summary>
        /// <param name="module">Module that owns the setting.</param>
        /// <param name="setting">Setting whose command is executed.</param>
        /// <param name="isSet">True to run the set command, false to run the get command.</param>
        /// <param name="newValue">Value passed to the set command; ignored for the get command.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The standard output of the command if successful, otherwise null.</returns>
        public async Task<string?> ExecuteSettingCommandAsync(ModuleConfig module, ModuleSetting setting, bool isSet, string? newValue, CancellationToken ct)
        {
            // Hold a content lease, then pick the command; a setting without that command has nothing to run.
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            var execStr = isSet ? setting.SetExec : setting.GetExec;
            if (string.IsNullOrEmpty(execStr)) return null;

            // Bound the number of setting commands that run at once.
            await _settingCommandThrottle.WaitAsync(ct);
            string? hostPayloadFile = null;
            try
            {
                // Substitute the {value} token with the new value, or with the path of a payload file for JSON values.
                if (isSet && newValue != null)
                {
                    if (UsesHostFilePayload(setting.NormalizedType))
                    {
                        // JSON payloads are written to a temp file so the child process argv stays reliable on Windows.
                        var safeId = SanitizeFileNameSegment(module.Id);
                        var prefix = GetHostPayloadFilePrefix(setting.NormalizedType);
                        hostPayloadFile = Path.Combine(Path.GetTempPath(), $"{prefix}_{safeId}_{Guid.NewGuid():N}.json");
                        await File.WriteAllTextAsync(hostPayloadFile, newValue, Utf8NoBom, ct).ConfigureAwait(false);

                        // Paths must be quoted for CreateProcess argv parsing when the profile or temp dir contains spaces.
                        execStr = execStr.Replace("{value}", QuoteWindowsArgument(hostPayloadFile));
                    }
                    else
                    {
                        execStr = execStr.Replace("{value}", newValue);
                    }
                }

                // Wrap the setting command as an ordinary module command so it runs through the same engine and
                // environment resolution as every other command.
                var cmd = new ModuleCommand
                {
                    Name = isSet ? $"Set {setting.Key}" : $"Get {setting.Key}",
                    Engine = setting.Engine,
                    Exec = execStr
                };

                // Collect the command output as the result; the "Exec:" echo line is not part of it. The output is
                // collected synchronously, so all of it is present once the command has finished.
                var outputBuilder = new StringBuilder();
                var lockObject = new object();
                var log = new InlineProgress<string>(msg =>
                {
                    if (string.IsNullOrEmpty(msg)) return;

                    // Capture the exact output, avoiding log prefixes and errors
                    var trimmed = msg.TrimStart();
                    if (!trimmed.StartsWith("Exec:"))
                    {
                        lock (lockObject)
                        {
                            outputBuilder.AppendLine(trimmed);
                        }
                    }
                });

                // Run the command untracked; module settings are injected into its environment only for set commands.
                bool success = await RunCommandAsync(
                    module,
                    cmd,
                    log,
                    ct,
                    trackProcess: false,
                    injectSettings: isSet,
                    sessionStage: "Settings");
                ct.ThrowIfCancellationRequested();
                if (!success)
                {
                    _logger.LogWarning(
                        "Module setting command failed (module {ModuleId}, setting {SettingKey}, set={IsSet}).",
                        module.Id,
                        setting.Key,
                        isSet);
                    return null;
                }

                // Get the last non-empty line (to ignore banners or headers)
                var lines = outputBuilder.ToString()
                    .Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToList();

                return lines.Count > 0 ? lines[^1] : string.Empty;
            }
            finally
            {
                if (hostPayloadFile != null)
                {
                    try
                    {
                        File.Delete(hostPayloadFile);
                    }
                    catch
                    {
                        // Best-effort cleanup of the host payload staging file.
                    }
                }

                _settingCommandThrottle.Release();
            }
        }


        // Command cancellation

        /// <summary>
        /// Terminates a short-lived command, including its child processes, when its caller cancels the operation.
        /// </summary>
        private static void TerminateCanceledProcess(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation is best-effort because the process may exit between the state check and kill call.
            }
        }


        // Host-managed settings

        /// <summary>
        /// Returns whether one setting type is owned by the host rather than module commands.
        /// </summary>
        private static bool IsHostManagedSetting(string normalizedType) =>
            string.Equals(normalizedType, "theme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "locale", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "key-aslm", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "key-gh", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns whether the setting value is delivered through a host-generated payload file.
        /// </summary>
        private static bool UsesHostFilePayload(string normalizedType) =>
            string.Equals(normalizedType, "theme", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedType, "locale", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the temp-file prefix used for one host-managed payload type.
        /// </summary>
        private static string GetHostPayloadFilePrefix(string normalizedType) =>
            string.Equals(normalizedType, "locale", StringComparison.OrdinalIgnoreCase)
                ? "aslm_locale"
                : "aslm_theme";


        // Console forwarding

        /// <summary>
        /// Mirrors module log messages into the shared consoles store.
        /// Every message goes to the caller's reporter and to the overview console of the module. The messages are
        /// forwarded synchronously on the reporting thread, so the overview keeps the order in which they arrived.
        /// </summary>
        private IProgress<string> CreateModuleLog(ModuleConfig module, IProgress<string> log)
        {
            return new InlineProgress<string>(message =>
            {
                log.Report(message);
                _consoleStore.AppendOverviewLine(module, message);
            });
        }


        // Output streaming

        /// <summary>
        /// Asks runtimes to write redirected output unbuffered and as UTF-8 where an environment variable controls it.
        /// Python buffers redirected output and writes it in the ANSI code page, so the Python variables are set for
        /// every command: a command can be a launcher script or a program that starts Python, and other programs
        /// ignore them. Node.js and Go write UTF-8 without holding output back, and output in the OEM code page of
        /// console programs (cmd.exe, PowerShell, .NET) is recognized by <see cref="ProcessOutputReader"/>.
        /// </summary>
        private static void ConfigureProcessForStreaming(ProcessStartInfo psi)
        {
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
        }

        /// <summary>
        /// Starts reading the output of a started process into its console session. Finished lines also go to the
        /// log; the unfinished last line is only shown in the session, where it is rewritten until it ends.
        /// </summary>
        /// <param name="process">Started process whose output and error streams are redirected.</param>
        /// <param name="session">Console session of the process.</param>
        /// <param name="log">Reporter that receives every finished line.</param>
        /// <returns>A task that completes once both output streams have ended.</returns>
        private Task ReadProcessOutputAsync(Process process, ModuleConsoleSessionHandle session, IProgress<string> log)
        {
            return ProcessOutputReader.ReadAsync(
                process,
                (stream, text) =>
                {
                    var line = $"  {text}";
                    log.Report(line);
                    _consoleStore.CompleteProcessLine(session, stream, line);
                },
                (stream, text) => _consoleStore.UpdateProcessLine(session, stream, $"  {text}"));
        }


        // Observed subprocesses

        /// <summary>
        /// Polls descendant processes of one tracked module process so externally spawned services stay visible.
        /// Runs until the run is stopped or the root process and all of its descendants are gone, then clears the
        /// observed sessions.
        /// </summary>
        private async Task MonitorObservedProcessesAsync(
            ModuleConfig module,
            ModuleConsoleSessionHandle ownerHandle,
            int rootProcessId,
            CancellationToken ct)
        {
            var emptyCycles = 0;

            // Poll once per second. The loop ends after three consecutive scans find neither descendants nor the
            // root, so a single empty scan does not end the monitor.
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Retain handles of new descendants first, then publish the current list to the consoles.
                    lock (_processLock)
                        CaptureDescendants(module.SourcePath);
                    var observedProcesses = GetDescendantProcesses(rootProcessId);
                    _consoleStore.SyncObservedProcesses(module, ownerHandle, observedProcesses);

                    if (observedProcesses.Count == 0 && !IsProcessAlive(rootProcessId))
                    {
                        emptyCycles++;
                        if (emptyCycles >= 3)
                        {
                            break;
                        }
                    }
                    else
                    {
                        emptyCycles = 0;
                    }
                }
                catch (Exception ex)
                {
                    // A failed scan must not end the monitor; the next cycle retries.
                    _logger.LogDebug(ex, "Observed process polling failed for root process {RootProcessId}.", rootProcessId);
                }

                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The run is stopping; leave the loop and clear the observed sessions below.
                    break;
                }
            }

            // Mark every remaining observed session as ended.
            _consoleStore.SyncObservedProcesses(module, ownerHandle, []);
        }

        /// <summary>
        /// Returns descendant processes of one root process.
        /// </summary>
        private List<ObservedProcessInfo> GetDescendantProcesses(int rootProcessId)
        {
            // Index a recent process-table snapshot by parent id (a cached snapshot up to 800 ms old is accepted).
            var snapshotEntries = _processSnapshots.GetSnapshot(TimeSpan.FromMilliseconds(800));
            var childrenByParent = snapshotEntries
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var results = new List<ObservedProcessInfo>();
            var queue = new Queue<int>();
            var visited = new HashSet<int> { rootProcessId };

            queue.Enqueue(rootProcessId);

            // Breadth-first walk down the process tree; the visited set guards against cycles from reused ids.
            while (queue.Count > 0)
            {
                var parentProcessId = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parentProcessId, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (!visited.Add(child.ProcessId))
                    {
                        continue;
                    }

                    results.Add(new ObservedProcessInfo
                    {
                        ProcessId = child.ProcessId,
                        ProcessName = ResolveObservedProcessName(child)
                    });
                    queue.Enqueue(child.ProcessId);
                }
            }

            return results;
        }

        /// <summary>
        /// Returns whether the process is still alive.
        /// </summary>
        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                // No process with that id exists (anymore), or it cannot be inspected: treat it as not alive.
                return false;
            }
        }

        /// <summary>
        /// Resolves a stable display name for one observed child process.
        /// </summary>
        private static string ResolveObservedProcessName(ProcessSnapshotEntry entry)
        {
            try
            {
                using var process = Process.GetProcessById(entry.ProcessId);
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                // Fall back to the process snapshot name.
            }

            var snapshotName = Path.GetFileNameWithoutExtension(entry.ExecutableName);
            return string.IsNullOrWhiteSpace(snapshotName)
                ? $"Process {entry.ProcessId}"
                : snapshotName;
        }


        // Process shutdown

        /// <summary>
        /// Captures and retains descendant handles while their ancestry is still known.
        /// Call with _processLock held. Exited parents remain anchors until cleanup.
        /// </summary>
        private void CaptureDescendants(string moduleSourcePath)
        {
            if (!_runningProcesses.TryGetValue(moduleSourcePath, out var processes) || processes.Count == 0)
                return;

            // Take a fresh process-table snapshot (no cached one) and remember when it was taken, so a process
            // created after the snapshot is never mistaken for a child found in it.
            var snapshotStartedUtc = DateTime.UtcNow;
            var childrenByParent = _processSnapshots.GetSnapshot(TimeSpan.Zero)
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var known = processes.Select(process => process.Id).ToHashSet();

            // Walk the list by index: every adopted child is appended and is itself searched for children later,
            // so the whole descendant tree is captured, however deep it is.
            for (var index = 0; index < processes.Count; index++)
            {
                var parent = processes[index];
                if (!childrenByParent.TryGetValue(parent.Id, out var children))
                    continue;
                foreach (var child in children)
                {
                    if (known.Contains(child.ProcessId))
                        continue;

                    // Open a handle first; the candidate is disposed in finally unless it is adopted.
                    Process? candidate = null;
                    try
                    {
                        candidate = Process.GetProcessById(child.ProcessId);
                        _ = candidate.Handle;

                        // An old parent PID may have been reused (especially on Unix).
                        // Only adopt children created within the known parent's lifetime.
                        if (candidate.StartTime.ToUniversalTime() > snapshotStartedUtc ||
                            candidate.StartTime < parent.StartTime ||
                            (parent.HasExited && candidate.StartTime > parent.ExitTime))
                            continue;
                        known.Add(candidate.Id);
                        processes.Add(candidate);

                        // ownership transferred to the module
                        candidate = null;
                    }
                    catch (ArgumentException)
                    {
                        // exited during the snapshot
                    }
                    catch (InvalidOperationException)
                    {
                        // exited during the snapshot
                    }
                    catch (System.ComponentModel.Win32Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not inspect descendant process {PID}.", child.ProcessId);
                    }
                    finally
                    {
                        candidate?.Dispose();
                    }
                }
            }
        }

        /// <summary>
        /// Returns whether the process is still running; a handle that can no longer be queried counts as exited.
        /// </summary>
        private static bool IsProcessRunning(Process process)
        {
            try { return !process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>
        /// Runs under the module's lifecycle gate. Do not remove ownership or report
        /// stopped until both command creation and every captured descendant have ended.
        /// </summary>
        private async Task StopModuleCoreAsync(string moduleSourcePath)
        {
            // Step 1: cancel the run so no new commands start, and wait for the observers to finish.
            ModuleRun? run;
            lock (_processLock)
            {
                _runs.TryGetValue(moduleSourcePath, out run);
                run?.Stop.Cancel();
            }

            if (run != null)
                await Task.WhenAll(run.Observers).ConfigureAwait(false);

            // Step 2: kill the process tree and wait until every owned process has ended.
            var processes = await TerminateModuleProcessesAsync(moduleSourcePath).ConfigureAwait(false);
            if (run != null)
            {
                await Task.WhenAll(run.Commands).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // A command finishing its output drain can discover descendants of
                // an exited launcher. Verify that final set before releasing ownership.
                processes = await TerminateModuleProcessesAsync(moduleSourcePath).ConfigureAwait(false);
            }

            // Step 3: only now drop ownership and release the handles.
            lock (_processLock)
            {
                _runningProcesses.TryRemove(moduleSourcePath, out _);
                _runningModules.TryRemove(moduleSourcePath, out _);
                _runs.Remove(moduleSourcePath);
                foreach (var process in processes)
                    process.Dispose();
                run?.Stop.Dispose();
            }
        }

        /// <summary>
        /// Kills every process owned by a module, descendants included, and returns the handles of the final set.
        /// Throws a <see cref="TimeoutException"/> when processes are still alive after ten seconds.
        /// </summary>
        private async Task<List<Process>> TerminateModuleProcessesAsync(string moduleSourcePath)
        {
            var timeout = Stopwatch.StartNew();
            List<Process> processes;

            // Repeat until a fresh scan finds no live process: killing a launcher can leave or spawn descendants,
            // so ownership is re-captured on every round.
            while (true)
            {
                lock (_processLock)
                {
                    CaptureDescendants(moduleSourcePath);
                    processes = _runningProcesses.TryGetValue(moduleSourcePath, out var owned) ? [.. owned] : [];
                }

                // Done when nothing is alive; give up after ten seconds, otherwise kill the remaining trees.
                var alive = processes.Where(IsProcessRunning).ToList();
                if (alive.Count == 0)
                    break;
                if (timeout.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"Module processes did not stop: {string.Join(", ", alive.Select(p => p.Id))}.");

                foreach (var process in alive)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (!IsProcessRunning(process))
                    {
                        // The process exited between the check and the kill, which is the desired result.
                    }
                    catch (Exception ex)
                    {
                        // Keep all handles on failure so a later stop can retry.
                        _logger.LogWarning(ex, "Could not terminate module process {PID}.", process.Id);
                    }
                }

                // WaitForExit on a launcher only waits for that launcher, not its
                // children. Re-snapshot after killing it and check every owned handle.
                await Task.Delay(25).ConfigureAwait(false);
            }

            return processes;
        }


        // Disposal

        /// <summary>
        /// Stops listening for port changes, cancels all runs and terminates the module processes.
        /// The processes are reaped synchronously; the remaining cleanup continues in the background.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ports.PortsRedistributed -= OnPortsRedistributed;

            // Signal every run to stop and remember which modules still own processes.
            string[] paths;
            lock (_processLock)
            {
                foreach (var run in _runs.Values)
                    run.Stop.Cancel();
                paths = _runningProcesses.Keys.ToArray();
            }

            // Reap the OS processes synchronously, without waiting on commands that
            // may still need the UI context. They can finish draining output later.
            foreach (var path in paths)
            {
                try { TerminateModuleProcessesAsync(path).GetAwaiter().GetResult(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not terminate module '{ModulePath}' during disposal.", path); }
            }

            // Do not dispose handles underneath commands still draining their output.
            // Shutdown normally awaits StopAllModulesAsync before disposing services.
            _ = StopOnDisposeAsync();
        }

        /// <summary>
        /// Runs the regular stop of all modules in the background after disposal; failures are only logged.
        /// </summary>
        private async Task StopOnDisposeAsync()
        {
            try { await StopAllModulesAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not stop module processes during disposal."); }
        }


        // Command parsing

        /// <summary>
        /// Splits a command string into arguments while respecting quotes.
        /// </summary>
        /// <param name="command">The command string to split.</param>
        /// <returns>A list of argument strings.</returns>
        private static List<string> SplitCommand(string command)
        {
            var args = new List<string>();
            var currentArg = new StringBuilder();
            var inQuotes = false;
            var quoteChar = '\0';

            foreach (var c in command)
            {
                // Inside quotes everything up to the matching quote belongs to the current token.
                if (inQuotes)
                {
                    if (c == quoteChar)
                    {
                        inQuotes = false;
                        quoteChar = '\0';
                    }
                    else
                    {
                        currentArg.Append(c);
                    }
                }
                else
                {
                    // Outside quotes whitespace ends a token and a single or double quote opens a quoted part.
                    if (char.IsWhiteSpace(c))
                    {
                        if (currentArg.Length > 0)
                        {
                            args.Add(currentArg.ToString());
                            currentArg.Clear();
                        }
                    }
                    else if (c == '"' || c == '\'')
                    {
                        inQuotes = true;
                        quoteChar = c;
                    }
                    else
                    {
                        currentArg.Append(c);
                    }
                }
            }

            // Flush the last token, which has no trailing whitespace after it.
            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args;
        }

        /// <summary>
        /// Rebuilds an argument string from parsed arguments.
        /// Arguments that contain a space are wrapped in double quotes.
        /// </summary>
        /// <param name="args">The list of arguments.</param>
        /// <returns>A single command-line argument string.</returns>
        private static string ParseArguments(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        /// <summary>
        /// Wraps one argument in double quotes for Windows process command lines (temp paths, user profiles with spaces).
        /// </summary>
        private static string QuoteWindowsArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            var escaped = argument.Replace("\"", "\\\"", StringComparison.Ordinal);
            return $"\"{escaped}\"";
        }


        // Name helpers

        /// <summary>
        /// Produces a filesystem-safe fragment from a module identifier for temp file names.
        /// Invalid file name characters become underscores; a blank identifier yields <c>module</c>.
        /// </summary>
        private static string SanitizeFileNameSegment(string moduleId)
        {
            if (string.IsNullOrWhiteSpace(moduleId))
            {
                return "module";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(moduleId.Length);
            foreach (var ch in moduleId)
            {
                builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }

            var trimmed = builder.ToString().Trim();
            return trimmed.Length > 0 ? trimmed : "module";
        }

        /// <summary>
        /// Removes known engine setting suffixes (<c>_path</c>, <c>_data</c>, <c>_models</c>) from a setting key,
        /// which yields the id of the engine the setting refers to.
        /// </summary>
        private static string TrimEngineSettingSuffix(string settingKey)
        {
            if (settingKey.EndsWith("_path", StringComparison.OrdinalIgnoreCase) ||
                settingKey.EndsWith("_data", StringComparison.OrdinalIgnoreCase))
            {
                return settingKey[..^5];
            }

            if (settingKey.EndsWith("_models", StringComparison.OrdinalIgnoreCase))
            {
                return settingKey[..^7];
            }

            return settingKey;
        }


        // Root path

        /// <summary>
        /// Returns the application root directory.
        /// </summary>
        private static string GetRootDirectory()
        {
            return AppRoot.Directory;
        }
    }
}
