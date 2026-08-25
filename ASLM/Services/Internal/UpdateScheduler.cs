// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Runs periodic background update checks and retains actionable ASLM and Ollama results.
    /// </summary>
    public sealed class UpdateScheduler : IDisposable
    {
        private const string AslmTargetKind = "app";
        private const string AslmTargetId = "aslm";
        private const string OllamaTargetKind = "engine";
        private const string OllamaTargetId = "ollama-service";

        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan MinimumLoopDelay = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan BudgetExhaustedPadding = TimeSpan.FromSeconds(10);

        private readonly AppDataStore _appData;
        private readonly UpdateManager _updateManager;
        private readonly EngineInstaller _engineInstaller;
        private readonly GitHubRateLimitStore _rateLimitStore;
        private readonly ILogger<UpdateScheduler> _logger;

        private readonly Queue<ScheduledUpdateCheckItem> _pendingChecks = new();
        private readonly object _queueGate = new();
        private readonly object _stateGate = new();

        private CancellationTokenSource? _cts;
        private Task? _worker;

        /// <summary>
        /// Notifies open views when the available ASLM or Ollama update state changes.
        /// </summary>
        public event EventHandler? UpdateStateChanged;


        // Initialization

        /// <summary>
        /// Creates the background update scheduler.
        /// </summary>
        public UpdateScheduler(
            AppDataStore appData,
            UpdateManager updateManager,
            EngineInstaller engineInstaller,
            GitHubRateLimitStore rateLimitStore,
            ILogger<UpdateScheduler> logger)
        {
            _appData = appData;
            _updateManager = updateManager;
            _engineInstaller = engineInstaller;
            _rateLimitStore = rateLimitStore;
            _logger = logger;
        }


        // State access

        /// <summary>
        /// Returns the persisted ASLM update candidate when it has not already been prepared or installed.
        /// </summary>
        public UpdateCandidate? GetAvailableAppUpdate()
        {
            lock (_stateGate)
            {
                var persisted = _appData.Data.Updates.AvailableAppUpdate;
                if (persisted == null || _updateManager.HasPendingAppUpdate)
                {
                    return null;
                }

                var installed = _appData.Data.Updates.InstalledReleaseTag;
                var remote = persisted.ReleaseTag ?? persisted.RemoteVersion;
                if (!string.IsNullOrWhiteSpace(installed) &&
                    ReleaseTagOrdering.ComparePrecedence(remote, installed) <= 0)
                {
                    return null;
                }

                return persisted.ToCandidate();
            }
        }

        /// <summary>
        /// Returns the persisted Ollama update candidate with the current installed engine attached.
        /// </summary>
        public UpdateCandidate? GetAvailableOllamaUpdate()
        {
            PersistedUpdateCandidate? persisted;
            lock (_stateGate)
            {
                persisted = _appData.Data.Updates.AvailableOllamaUpdate;
            }

            if (persisted == null)
            {
                return null;
            }

            var engine = ResolveOllamaEngine();
            if (engine?.Status.Installed != true)
            {
                return null;
            }

            var candidate = persisted.ToCandidate(engine);
            return UpdateManager.IsEngineAlreadyAtInstallTarget(engine, candidate) ? null : candidate;
        }

        /// <summary>
        /// Clears one persisted candidate after its update has been applied or prepared.
        /// </summary>
        public async Task ClearAvailableCandidateAsync(string targetKind, string targetId)
        {
            var changed = ClearPersistedCandidate(targetKind, targetId);
            if (!changed)
            {
                return;
            }

            await _appData.SaveAsync();
            RaiseUpdateStateChanged();
        }


        // Startup

        /// <summary>
        /// Starts the background loop once.
        /// </summary>
        public void Start()
        {
            if (_worker != null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _worker = Task.Run(() => RunAsync(_cts.Token));
        }


        // Shutdown

        /// <summary>
        /// Stops the background loop.
        /// </summary>
        public async Task StopAsync()
        {
            if (_cts == null || _worker == null)
            {
                return;
            }

            _cts.Cancel();
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal scheduler shutdown path.
            }

            _cts.Dispose();
            _cts = null;
            _worker = null;
        }


        // Worker loop

        /// <summary>
        /// Runs the scheduler loop until the application shuts down.
        /// </summary>
        private async Task RunAsync(CancellationToken ct)
        {
            // Let startup I/O and account initialization finish before the first network request.
            await Task.Delay(StartupDelay, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var delay = await RunSchedulerPassAsync(ct);
                    await Task.Delay(ClampLoopDelay(delay), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scheduled update check failed.");
                    await Task.Delay(FailureRetryDelay, ct);
                }
            }
        }

        /// <summary>
        /// Executes one scheduler pass and returns the delay before the next pass.
        /// </summary>
        private async Task<TimeSpan> RunSchedulerPassAsync(CancellationToken ct)
        {
            _appData.Data.Updates.Normalize();
            var settings = _appData.Data.Updates;

            // ASLM and Ollama are checked every hour independently of the optional repository-check toggle.
            if (IsRequiredCheckDue(settings))
            {
                if (!_rateLimitStore.CanMakeAutoRequest())
                {
                    return _rateLimitStore.GetDelayUntilReset() + BudgetExhaustedPadding;
                }

                // Persist the window before network work so a crash cannot repeat checks inside the hour.
                lock (_stateGate)
                {
                    settings.LastAutoCheckUtc = DateTime.UtcNow.ToString("o");
                }

                await _appData.SaveAsync();
                await RunRequiredChecksAsync(ct);
                settings = _appData.Data.Updates;

                if (settings.CheckEnabled)
                {
                    await PopulateOptionalQueueAsync(ct);
                }
                else
                {
                    ClearOptionalQueue();
                }
            }
            else if (!settings.CheckEnabled)
            {
                ClearOptionalQueue();
            }

            // Optional module and non-Ollama engine checks remain controlled by Check for updates.
            if (GetPendingCheckCount() == 0)
            {
                return GetDelayUntilRequiredCheck(settings);
            }

            if (!_rateLimitStore.CanMakeAutoRequest())
            {
                return MinDelay(
                    _rateLimitStore.GetDelayUntilReset() + BudgetExhaustedPadding,
                    GetDelayUntilRequiredCheck(settings));
            }

            await ProcessNextOptionalItemAsync(ct);
            return MinDelay(
                _rateLimitStore.CalculateInterCheckDelay(),
                GetDelayUntilRequiredCheck(settings));
        }


        // Required checks

        /// <summary>
        /// Checks ASLM and Ollama, persists their results, and applies them when automatic updates are enabled.
        /// </summary>
        private async Task RunRequiredChecksAsync(CancellationToken ct)
        {
            var settings = _appData.Data.Updates;
            var publishNotifications = !settings.AutoUpdateEnabled;
            var appResult = await SafeCheckAppUpdateAsync(ct, publishNotifications);

            var ollamaEngine = ResolveOllamaEngine();
            var ollamaResult = ollamaEngine?.Status.Installed == true
                ? await SafeCheckEngineUpdateAsync(ollamaEngine, ct, publishNotifications)
                : UpdateCheckResult.CompletedWithoutUpdate;

            // Persist successful results while retaining older candidates when a network check failed.
            lock (_stateGate)
            {
                if (appResult.Completed)
                {
                    settings.AvailableAppUpdate = ToPersistedCandidate(appResult.Candidate);
                }

                if (ollamaResult.Completed)
                {
                    settings.AvailableOllamaUpdate = ToPersistedCandidate(ollamaResult.Candidate);
                }
            }

            await _appData.SaveAsync();
            RaiseUpdateStateChanged();

            if (!settings.AutoUpdateEnabled)
            {
                return;
            }

            // Automatic installs reuse the same candidates exposed by the cards and clear them only on success.
            var stateChanged = false;
            if (appResult.Candidate != null)
            {
                stateChanged |= await TryAutomaticallyPrepareAppUpdateAsync(appResult.Candidate, ct);
            }

            if (ollamaResult.Candidate != null)
            {
                stateChanged |= await TryAutomaticallyApplyEngineUpdateAsync(ollamaResult.Candidate, ct);
            }

            if (stateChanged)
            {
                await _appData.SaveAsync();
                RaiseUpdateStateChanged();
            }
        }

        /// <summary>
        /// Prepares an automatically discovered ASLM update and clears its candidate on success.
        /// </summary>
        private async Task<bool> TryAutomaticallyPrepareAppUpdateAsync(UpdateCandidate candidate, CancellationToken ct)
        {
            if (_updateManager.HasPendingAppUpdate)
            {
                return ClearPersistedCandidate(AslmTargetKind, AslmTargetId);
            }

            try
            {
                var log = new Progress<string>(message =>
                    _logger.LogInformation("[Updater] {Message}", message));
                var prepared = await _updateManager.PrepareAppUpdateAsync(candidate, log, null, false, ct);
                return prepared && ClearPersistedCandidate(AslmTargetKind, AslmTargetId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Automatic ASLM update preparation failed.");
                return false;
            }
        }

        /// <summary>
        /// Applies an automatically discovered Ollama update and clears its candidate on success.
        /// </summary>
        private async Task<bool> TryAutomaticallyApplyEngineUpdateAsync(UpdateCandidate candidate, CancellationToken ct)
        {
            try
            {
                var log = new Progress<string>(message =>
                    _logger.LogInformation("[Updater] {Message}", message));
                var applied = await _updateManager.ApplyEngineUpdateAsync(candidate, log, null, false, ct);
                return applied && ClearPersistedCandidate(OllamaTargetKind, OllamaTargetId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Automatic Ollama update failed.");
                return false;
            }
        }


        // Optional checks

        /// <summary>
        /// Discovers optional module and non-Ollama engine targets for sequential background checks.
        /// </summary>
        private async Task PopulateOptionalQueueAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var modules = await _updateManager.DiscoverInstalledModulesAsync();
            var engines = _engineInstaller.DiscoverEngines()
                .Where(engine =>
                    engine.Status.Installed &&
                    engine.Update != null &&
                    !string.IsNullOrWhiteSpace(engine.Update.Repo) &&
                    !string.Equals(engine.Id, OllamaTargetId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            lock (_queueGate)
            {
                _pendingChecks.Clear();
                foreach (var module in modules)
                {
                    _pendingChecks.Enqueue(ScheduledUpdateCheckItem.ForModule(module));
                }

                foreach (var engine in engines)
                {
                    _pendingChecks.Enqueue(ScheduledUpdateCheckItem.ForEngine(engine));
                }
            }
        }

        /// <summary>
        /// Runs one optional update check and applies its candidate when automatic updates are enabled.
        /// </summary>
        private async Task ProcessNextOptionalItemAsync(CancellationToken ct)
        {
            ScheduledUpdateCheckItem? item;
            lock (_queueGate)
            {
                item = _pendingChecks.Count > 0 ? _pendingChecks.Dequeue() : null;
            }

            if (item == null)
            {
                return;
            }

            var settings = _appData.Data.Updates;
            var publishNotifications = !settings.AutoUpdateEnabled;
            var result = item.Kind switch
            {
                ScheduledUpdateCheckItem.ModuleKind when item.Module != null =>
                    await SafeCheckModuleUpdateAsync(item.Module, ct, publishNotifications),
                ScheduledUpdateCheckItem.EngineKind when item.Engine != null =>
                    await SafeCheckEngineUpdateAsync(item.Engine, ct, publishNotifications),
                _ => UpdateCheckResult.CompletedWithoutUpdate
            };

            if (result.Candidate == null || !settings.AutoUpdateEnabled)
            {
                return;
            }

            var log = new Progress<string>(message =>
                _logger.LogInformation("[Updater] {Message}", message));
            await _updateManager.ApplyDiscoveredUpdatesAsync([result.Candidate], log, ct);
        }


        // Safe checks

        /// <summary>
        /// Checks ASLM without aborting the scheduler and distinguishes failures from an up-to-date result.
        /// </summary>
        private async Task<UpdateCheckResult> SafeCheckAppUpdateAsync(
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                var candidate = await _updateManager.CheckAppUpdateAsync(ct, publishNotifications, false);
                return new UpdateCheckResult(true, candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ASLM update check failed.");
                return UpdateCheckResult.Failed;
            }
        }

        /// <summary>
        /// Checks one module without aborting the scheduler and distinguishes failures from an up-to-date result.
        /// </summary>
        private async Task<UpdateCheckResult> SafeCheckModuleUpdateAsync(
            ModuleConfig module,
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                var candidate = await _updateManager.CheckModuleUpdateAsync(module, ct, publishNotifications, false);
                return new UpdateCheckResult(true, candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Module update check failed for {ModuleId}.", module.Id);
                return UpdateCheckResult.Failed;
            }
        }

        /// <summary>
        /// Checks one engine without aborting the scheduler and distinguishes failures from an up-to-date result.
        /// </summary>
        private async Task<UpdateCheckResult> SafeCheckEngineUpdateAsync(
            EngineConfig engine,
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                var candidate = await _updateManager.CheckEngineUpdateAsync(engine, ct, publishNotifications, false);
                return new UpdateCheckResult(true, candidate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Engine update check failed for {EngineId}.", engine.Id);
                return UpdateCheckResult.Failed;
            }
        }


        // State helpers

        /// <summary>
        /// Returns the managed Ollama engine manifest when it is present on disk.
        /// </summary>
        private EngineConfig? ResolveOllamaEngine()
        {
            return _engineInstaller.DiscoverEngines()
                .FirstOrDefault(engine =>
                    string.Equals(engine.Id, OllamaTargetId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Converts a discovered candidate into a compact persisted snapshot.
        /// </summary>
        private static PersistedUpdateCandidate? ToPersistedCandidate(UpdateCandidate? candidate) =>
            candidate == null ? null : PersistedUpdateCandidate.FromCandidate(candidate);

        /// <summary>
        /// Clears a persisted candidate without saving so related state can be committed together.
        /// </summary>
        private bool ClearPersistedCandidate(string targetKind, string targetId)
        {
            lock (_stateGate)
            {
                var settings = _appData.Data.Updates;
                if (string.Equals(targetKind, AslmTargetKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(targetId, AslmTargetId, StringComparison.OrdinalIgnoreCase) &&
                    settings.AvailableAppUpdate != null)
                {
                    settings.AvailableAppUpdate = null;
                    return true;
                }

                if (string.Equals(targetKind, OllamaTargetKind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(targetId, OllamaTargetId, StringComparison.OrdinalIgnoreCase) &&
                    settings.AvailableOllamaUpdate != null)
                {
                    settings.AvailableOllamaUpdate = null;
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Raises the scheduler state event without assuming a UI synchronization context.
        /// </summary>
        private void RaiseUpdateStateChanged() => UpdateStateChanged?.Invoke(this, EventArgs.Empty);


        // Queue helpers

        /// <summary>
        /// Returns the number of optional checks waiting to run.
        /// </summary>
        private int GetPendingCheckCount()
        {
            lock (_queueGate)
            {
                return _pendingChecks.Count;
            }
        }

        /// <summary>
        /// Drops optional checks when repository checking is disabled or a new interval begins.
        /// </summary>
        private void ClearOptionalQueue()
        {
            lock (_queueGate)
            {
                _pendingChecks.Clear();
            }
        }


        // Scheduling helpers

        /// <summary>
        /// Returns whether the hourly ASLM and Ollama check window has elapsed.
        /// </summary>
        internal static bool IsRequiredCheckDue(AppUpdateSettings settings) =>
            GetDelayUntilRequiredCheck(settings) <= TimeSpan.Zero;

        /// <summary>
        /// Returns the exact remaining delay until the next required hourly check.
        /// </summary>
        internal static TimeSpan GetDelayUntilRequiredCheck(AppUpdateSettings settings)
        {
            settings.Normalize();
            if (!DateTimeOffset.TryParse(settings.LastAutoCheckUtc, out var lastCheck))
            {
                return TimeSpan.Zero;
            }

            var delay = lastCheck.ToUniversalTime() + TimeSpan.FromHours(settings.AutoCheckPeriodHours) -
                        DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        /// <summary>
        /// Returns the shorter non-negative scheduler delay.
        /// </summary>
        private static TimeSpan MinDelay(TimeSpan first, TimeSpan second)
        {
            if (first <= TimeSpan.Zero || second <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return first <= second ? first : second;
        }

        /// <summary>
        /// Prevents tight scheduler loops when a calculated delay reaches zero.
        /// </summary>
        private static TimeSpan ClampLoopDelay(TimeSpan delay) =>
            delay < MinimumLoopDelay ? MinimumLoopDelay : delay;


        // Disposal

        /// <summary>
        /// Cancels the background worker when the scheduler is disposed by the host.
        /// </summary>
        public void Dispose()
        {
            if (_cts == null)
            {
                return;
            }

            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        /// <summary>
        /// Carries a safe check result while preserving the difference between failure and no update.
        /// </summary>
        private sealed record UpdateCheckResult(bool Completed, UpdateCandidate? Candidate)
        {
            public static UpdateCheckResult CompletedWithoutUpdate { get; } = new(true, null);
            public static UpdateCheckResult Failed { get; } = new(false, null);
        }

        /// <summary>
        /// Describes one queued optional update check.
        /// </summary>
        private sealed class ScheduledUpdateCheckItem
        {
            public const string ModuleKind = "module";
            public const string EngineKind = "engine";

            public string Kind { get; private init; } = string.Empty;
            public ModuleConfig? Module { get; private init; }
            public EngineConfig? Engine { get; private init; }

            /// <summary>
            /// Creates an optional module check item.
            /// </summary>
            public static ScheduledUpdateCheckItem ForModule(ModuleConfig module) =>
                new() { Kind = ModuleKind, Module = module };

            /// <summary>
            /// Creates an optional engine check item.
            /// </summary>
            public static ScheduledUpdateCheckItem ForEngine(EngineConfig engine) =>
                new() { Kind = EngineKind, Engine = engine };
        }
    }
}
