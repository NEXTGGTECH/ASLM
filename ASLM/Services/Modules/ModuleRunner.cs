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
    /// </summary>
    public class ModuleRunner : IDisposable
    {
        private readonly EngineInstaller _engineInstaller;
        private readonly ModuleEnvironmentResolver _environmentResolver;
        private readonly PortRegistry _ports;
        private readonly ProcessTracker _processTracker;
        private readonly ModuleConsoleStore _consoleStore;
        private readonly ProcessSnapshotReader _processSnapshots;
        private readonly ModuleThemePayloadBuilder _themePayloadBuilder;
        private readonly ModuleLocalePayloadBuilder _localePayloadBuilder;
        private readonly ModuleTrustService _moduleTrustService;
        private readonly GitHubAccountStore _githubAccountStore;
        private readonly SunriseService _sunriseService;
        private readonly ModuleInteropHostState _interopHostState;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ModuleRunner> _logger;
        private readonly SemaphoreSlim _settingCommandThrottle = new(4, 4);
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private bool _disposed;

        // Running processes

        /// <summary>
        /// Owns root and descendant process handles until a module is fully stopped.
        /// </summary>
        private readonly ConcurrentDictionary<string, List<Process>> _runningProcesses = new();
        private readonly ConcurrentDictionary<string, ModuleConfig> _runningModules = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleGates = new();
        private readonly Dictionary<string, ModuleRun> _runs = new();
        private readonly object _processLock = new();

        private sealed class ModuleRun
        {
            public CancellationTokenSource Stop { get; } = new();
            public List<Task> Commands { get; } = [];
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
        /// Executes first-run setup for a module.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="log">Progress reporter for logging output.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if all setup steps succeeded; otherwise, false.</returns>
        public async Task<bool> ExecuteFirstRunAsync(
            ModuleConfig module,
            IProgress<string> log,
            CancellationToken ct,
            bool skipModuleDependencies = false)
        {
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            _consoleStore.EnsureModule(module);
            var moduleLog = CreateModuleLog(module, log);

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

            module.Status.Installed = true;
            module.Status.FirstRunCompleted = true;
            return true;
        }

        // Run execution

        /// <summary>
        /// Executes the long-running run commands for a module.
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
                ObjectDisposedException.ThrowIf(_disposed, this);
                _consoleStore.EnsureModule(module);
                var moduleLog = CreateModuleLog(module, log);
                if (module.Commands.Run.Count == 0)
                {
                    moduleLog.Report($"No run commands for {module.Name}.");
                    return true;
                }
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
                var run = new ModuleRun();
                lock (_processLock)
                    _runs.Add(module.SourcePath, run);
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct, run.Stop.Token);
                var startTasks = new List<Task<bool>>();

                try
                {
                    _ports.GetOrAssignPorts(module);
                    _ports.EnsurePortsAvailable(module.Id);
                    await SynchronizeDeclaredModuleSettingsAsync(module, moduleLog, startup.Token).ConfigureAwait(false);
                    moduleLog.Report($"Starting {module.Name}...");

                    foreach (var cmd in module.Commands.Run)
                    {
                        startup.Token.ThrowIfCancellationRequested();
                        moduleLog.Report($"[Run] {cmd.Name}: {cmd.Description}");
                        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        startTasks.Add(started.Task);
                        run.Commands.Add(RunCommandAsync(module, cmd, moduleLog, startup.Token,
                            trackProcess: true, sessionStage: "Run", processStarted: started));
                    }

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
                    run.Stop.Cancel();
                    await Task.WhenAll(startTasks).ConfigureAwait(false);
                    await StopModuleCoreAsync(module.SourcePath).ConfigureAwait(false);
                    throw;
                }

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

        /// <summary>
        /// Reconciles only host-controlled module settings before module commands start.
        /// </summary>
        private async Task SynchronizeDeclaredModuleSettingsAsync(ModuleConfig module, IProgress<string> moduleLog, CancellationToken ct)
        {
            if (module.Settings == null || module.Settings.Count == 0)
            {
                return;
            }

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

        internal ModuleConsoleStore ConsoleStore => _consoleStore;

        // Module stop

        /// <summary>
        /// Stops all tracked processes for one module.
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

        /// <summary>
        /// Returns the source paths of modules that currently have tracked running processes.
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
                    if (!pair.Value.Any(IsProcessRunning))
                        CaptureDescendants(pair.Key);
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

        /// <summary>
        /// Restarts currently running modules after the shared port map changes.
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
        /// Returns tracked module configs that still have live owned processes.
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
        /// </summary>
        public async Task StopAllModulesAsync()
        {
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
        /// </summary>
        private void InjectSettingsIntoEnvironment(ModuleConfig module, ProcessStartInfo psi)
        {
            if (module.Settings == null) return;

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

            if (module.ModuleInterop?.IsClientEnabled == true &&
                _interopHostState.TryGetListening(out var interopBaseUrl, out var interopPort))
            {
                psi.Environment["ASLM_MODULE_INTEROP_BASE_URL"] = interopBaseUrl;
                psi.Environment["ASLM_MODULE_INTEROP_PORT"] = interopPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Returns the effective setting value that ASLM resolves for a module.
        /// </summary>
        public object? GetResolvedSettingValue(ModuleConfig module, ModuleSetting setting)
        {
            var resolved = ResolveSettingValue(module, setting);
            return setting.ParseSerializedValue(resolved);
        }

        /// <summary>
        /// Resolves the effective string value for one module setting.
        /// </summary>
        private string ResolveSettingValue(ModuleConfig module, ModuleSetting setting)
        {
            switch (setting.NormalizedType)
            {
                case "port":
                    var ports = _ports.GetOrAssignPorts(module);
                    if (ports.TryGetValue(setting.Key, out var assigned))
                        return assigned.ToString();
                    
                    var rawPort = (setting.Value ?? setting.Default)?.ToString();
                    if (int.TryParse(rawPort, out var p))
                        return p.ToString();
                    return rawPort ?? string.Empty;

                case "engine":
                    if (_engineInstaller.HasEngine(setting.Key))
                    {
                        return _engineInstaller.GetEngineConfig(setting.Key) != null ? "true" : "false";
                    }

                    var rawEngine = (setting.Value ?? setting.Default)?.ToString();
                    return rawEngine != null && rawEngine.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

                case "path":
                    if (setting.UseCustomValue)
                    {
                        return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
                    }

                    // Returns the engine executable path, or empty string if not installed.
                    var pathEngineId = TrimEngineSettingSuffix(setting.Key);
                    var enginePath = _engineInstaller.GetEngineExecutablePath(pathEngineId);
                    return !string.IsNullOrEmpty(enginePath) ? enginePath.Replace('\\', '/') : "";

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

                case "bool":
                    var rawBool = (setting.Value ?? setting.Default)?.ToString();
                    return rawBool != null && rawBool.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

                case "theme":
                    return _themePayloadBuilder.BuildJson();

                case "locale":
                    return _localePayloadBuilder.BuildJson();

                case "key-aslm":
                case "key-gh":
                    return TryResolveHostKeyValue(module, setting, out var hostKeyValue)
                        ? hostKeyValue
                        : "None";

                default:
                    return (setting.Value ?? setting.Default)?.ToString() ?? string.Empty;
            }
        }

        /// <summary>
        /// Resolves one host account key after verifying the requesting module.
        /// </summary>
        private bool TryResolveHostKeyValue(
            ModuleConfig module,
            ModuleSetting setting,
            out string value)
        {
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
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="log">Progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if dependencies installed successfully.</returns>
        private async Task<bool> InstallDependenciesAsync(ModuleConfig module, IProgress<string> log, CancellationToken ct)
        {
            foreach (var engineDep in module.Dependencies.Engines)
            {
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

                var environment = await _environmentResolver.EnsureEnvironmentAsync(module, engineConfig, log, ct);
                var exePath = _environmentResolver.ResolvePackageManagerExecutable(environment, engineConfig);
                var libs = string.Join(" ", engineDep.Libraries);
                var args = _environmentResolver.BuildPackageInstallArguments(environment, engineConfig, engineDep.Libraries);
                log.Report($"[Deps] Installing libraries for {engineDep.Id}: {libs}");

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
                ConfigureProcessForStreaming(psi, exePath, engineDep.Id);
                _environmentResolver.ApplyEnvironmentVariables(module, engineConfig, psi);

                using var process = new Process { StartInfo = psi };

                if (!process.Start())
                {
                    log.Report($"✗ Failed to start package manager for {engineDep.Id}");
                    return false;
                }
                process.StandardInput.Close();

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

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        var line = $"  {e.Data}";
                        log.Report(line);
                        _consoleStore.AppendProcessLine(sessionHandle, line);
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        var line = $"  {e.Data}";
                        log.Report(line);
                        _consoleStore.AppendProcessLine(sessionHandle, line);
                    }
                };

                // Assign to the job object so dependency installs stay grouped under ASLM.
                _processTracker.AddProcess(process);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(ct);
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
        /// Executes one module command.
        /// </summary>
        /// <param name="module">The module configuration.</param>
        /// <param name="cmd">The command to execute.</param>
        /// <param name="log">Progress reporter.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="trackProcess">If true, the process is tracked for lifecycle management (long-running).</param>
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
            Process? process = null;
            ModuleConsoleSessionHandle? session = null;
            bool registered = false;
            int? exitCode = null;
            try
            {
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
                ConfigureProcessForStreaming(psi, fileName, cmd.Engine);
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

                // Register atomically with process creation, before console/UI events
                // can announce a running module. Stop cannot miss a late root.
                ModuleRun? run = null;
                lock (_processLock)
                {
                    ct.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (trackProcess && _runs.TryGetValue(module.SourcePath, out run))
                        run.Stop.Token.ThrowIfCancellationRequested();
                    if (!process.Start())
                    {
                        log.Report("Failed to start process.");
                        return false;
                    }
                    if (trackProcess)
                    {
                        // Keep the handle (including after root exit) until stop has
                        // reaped descendants. A PID alone is not process ownership.
                        _runningProcesses.GetOrAdd(module.SourcePath, _ => []).Add(process);
                        _runningModules[module.SourcePath] = module;
                        registered = true;
                        _ = process.Handle;
                        _ = process.StartTime;
                    }
                }
                process.StandardInput.Close();

                var sessionHandle = _consoleStore.StartProcessSession(
                    module,
                    cmd,
                    sessionStage,
                    execMessage,
                    process,
                    isTrackedProcess: trackProcess);
                session = sessionHandle;

                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        var line = $"  {e.Data}";
                        log.Report(line);
                        _consoleStore.AppendProcessLine(sessionHandle, line);
                    }
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null)
                    {
                        var line = $"  {e.Data}";
                        log.Report(line);
                        _consoleStore.AppendProcessLine(sessionHandle, line);
                    }
                };

                // Assign to the job object so launched processes stay grouped under ASLM.
                _processTracker?.AddProcess(process);

                if (run != null)
                {
                    run.Observers.Add(MonitorObservedProcessesAsync(module, sessionHandle, process.Id, run.Stop.Token));
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                processStarted?.TrySetResult(true);

                // A launch token controls setup and process creation only. Once a
                // tracked run command starts, module stop owns its lifetime.
                var lifetimeToken = trackProcess ? CancellationToken.None : ct;
                using var cancellationRegistration = lifetimeToken.Register(
                    static state => TerminateCanceledProcess((Process)state!),
                    process);
                await process.WaitForExitAsync(lifetimeToken).ConfigureAwait(false);
                exitCode = process.ExitCode;
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
                log.Report("Operation canceled.");
                return false;
            }
            catch (Exception ex)
            {
                log.Report($"Execution error: {ex.Message}");
                _logger.LogError(ex, "Command execution failed");
                return false;
            }
            finally
            {
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
        /// </summary>
        /// <returns>The standard output of the command if successful, otherwise null.</returns>
        public async Task<string?> ExecuteSettingCommandAsync(ModuleConfig module, ModuleSetting setting, bool isSet, string? newValue, CancellationToken ct)
        {
            using var operation = await ModuleInstaller.BeginContentOperationAsync(ct, module).ConfigureAwait(false);
            var execStr = isSet ? setting.SetExec : setting.GetExec;
            if (string.IsNullOrEmpty(execStr)) return null;

            await _settingCommandThrottle.WaitAsync(ct);
            string? hostPayloadFile = null;
            try
            {
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

                var cmd = new ModuleCommand
                {
                    Name = isSet ? $"Set {setting.Key}" : $"Get {setting.Key}",
                    Engine = setting.Engine,
                    Exec = execStr
                };

                var outputBuilder = new StringBuilder();
                var lockObject = new object();
                var log = new Progress<string>(msg =>
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

        /// <summary>
        /// Terminates a short-lived command when its caller cancels the operation.
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
        /// </summary>
        private IProgress<string> CreateModuleLog(ModuleConfig module, IProgress<string> log)
        {
            return new Progress<string>(message =>
            {
                log.Report(message);
                _consoleStore.AppendOverviewLine(module, message);
            });
        }

        /// <summary>
        /// Tunes process startup so redirected console output is as complete and timely as possible.
        /// </summary>
        private static void ConfigureProcessForStreaming(ProcessStartInfo psi, string fileName, string? engineId)
        {
            if (!IsPythonProcess(fileName, engineId))
            {
                return;
            }

            if (!HasPythonUnbufferedFlag(psi.Arguments))
            {
                psi.Arguments = string.IsNullOrWhiteSpace(psi.Arguments)
                    ? "-u"
                    : $"-u {psi.Arguments}";
            }

            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
        }

        /// <summary>
        /// Returns whether the command is executed by a Python runtime.
        /// </summary>
        private static bool IsPythonProcess(string fileName, string? engineId)
        {
            if (!string.IsNullOrWhiteSpace(engineId) &&
                engineId.Contains("python", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var executableName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return false;
            }

            return executableName.StartsWith("python", StringComparison.OrdinalIgnoreCase) ||
                   executableName.StartsWith("py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns whether the Python command line already enables unbuffered output.
        /// </summary>
        private static bool HasPythonUnbufferedFlag(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return false;
            }

            var parts = SplitCommand(arguments);
            return parts.Any(part => string.Equals(part, "-u", StringComparison.OrdinalIgnoreCase));
        }

        // Observed subprocesses

        /// <summary>
        /// Polls descendant processes of one tracked module process so externally spawned services stay visible.
        /// </summary>
        private async Task MonitorObservedProcessesAsync(
            ModuleConfig module,
            ModuleConsoleSessionHandle ownerHandle,
            int rootProcessId,
            CancellationToken ct)
        {
            var emptyCycles = 0;

            while (!ct.IsCancellationRequested)
            {
                try
                {
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
                    _logger.LogDebug(ex, "Observed process polling failed for root process {RootProcessId}.", rootProcessId);
                }

                try
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _consoleStore.SyncObservedProcesses(module, ownerHandle, []);
        }

        /// <summary>
        /// Returns descendant processes of one root process.
        /// </summary>
        private List<ObservedProcessInfo> GetDescendantProcesses(int rootProcessId)
        {
            var snapshotEntries = _processSnapshots.GetSnapshot(TimeSpan.FromMilliseconds(800));
            var childrenByParent = snapshotEntries
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var results = new List<ObservedProcessInfo>();
            var queue = new Queue<int>();
            var visited = new HashSet<int> { rootProcessId };

            queue.Enqueue(rootProcessId);

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

            var snapshotStartedUtc = DateTime.UtcNow;
            var childrenByParent = _processSnapshots.GetSnapshot(TimeSpan.Zero)
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var known = processes.Select(process => process.Id).ToHashSet();
            for (var index = 0; index < processes.Count; index++)
            {
                var parent = processes[index];
                if (!childrenByParent.TryGetValue(parent.Id, out var children))
                    continue;
                foreach (var child in children)
                {
                    if (known.Contains(child.ProcessId))
                        continue;
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
                        candidate = null; // ownership transferred to the module
                    }
                    catch (ArgumentException) { /* exited during the snapshot */ }
                    catch (InvalidOperationException) { /* exited during the snapshot */ }
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
            ModuleRun? run;
            lock (_processLock)
            {
                _runs.TryGetValue(moduleSourcePath, out run);
                run?.Stop.Cancel();
            }
            if (run != null)
                await Task.WhenAll(run.Observers).ConfigureAwait(false);

            var processes = await TerminateModuleProcessesAsync(moduleSourcePath).ConfigureAwait(false);
            if (run != null)
            {
                await Task.WhenAll(run.Commands).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                // A command finishing its output drain can discover descendants of
                // an exited launcher. Verify that final set before releasing ownership.
                processes = await TerminateModuleProcessesAsync(moduleSourcePath).ConfigureAwait(false);
            }
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

        private async Task<List<Process>> TerminateModuleProcessesAsync(string moduleSourcePath)
        {
            var timeout = Stopwatch.StartNew();
            List<Process> processes;
            while (true)
            {
                lock (_processLock)
                {
                    CaptureDescendants(moduleSourcePath);
                    processes = _runningProcesses.TryGetValue(moduleSourcePath, out var owned) ? [.. owned] : [];
                }
                var alive = processes.Where(IsProcessRunning).ToList();
                if (alive.Count == 0)
                    break;
                if (timeout.Elapsed >= TimeSpan.FromSeconds(10))
                    throw new TimeoutException($"Module processes did not stop: {string.Join(", ", alive.Select(p => p.Id))}.");

                foreach (var process in alive)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (!IsProcessRunning(process)) { }
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

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ports.PortsRedistributed -= OnPortsRedistributed;

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

            if (currentArg.Length > 0)
            {
                args.Add(currentArg.ToString());
            }

            return args;
        }

        /// <summary>
        /// Rebuilds an argument string from parsed arguments.
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

        /// <summary>
        /// Produces a short filesystem-safe fragment from a module identifier for temp file names.
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
        /// Removes known engine setting suffixes from a setting key.
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
