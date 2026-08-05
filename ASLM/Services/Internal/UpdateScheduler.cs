// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Runs periodic background update checks according to user preferences.
    /// </summary>
    public sealed class UpdateScheduler : IDisposable
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan IdlePollDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan BudgetExhaustedPadding = TimeSpan.FromSeconds(10);

        private readonly AppDataStore _appData;
        private readonly UpdateManager _updateManager;
        private readonly EngineInstaller _engineInstaller;
        private readonly GitHubRateLimitStore _rateLimitStore;
        private readonly ILogger<UpdateScheduler> _logger;

        private readonly Queue<ScheduledUpdateCheckItem> _pendingChecks = new();
        private readonly object _queueGate = new();

        private CancellationTokenSource? _cts;
        private Task? _worker;


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
                // Normal shutdown path.
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
            // Give the app a short moment to finish startup I/O before beginning network work.
            await Task.Delay(StartupDelay, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var delay = await RunSchedulerPassAsync(ct);
                    await Task.Delay(delay, ct);
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
            if (!settings.CheckEnabled)
            {
                return IdlePollDelay;
            }

            if (GetPendingCheckCount() == 0 && IsDue(settings))
            {
                await PopulateQueueAsync(ct);
                settings.LastAutoCheckUtc = DateTime.UtcNow.ToString("o");
                await _appData.SaveAsync();
            }

            if (GetPendingCheckCount() == 0)
            {
                return GetSchedulerDelay(settings);
            }

            if (!_rateLimitStore.CanMakeAutoRequest())
            {
                return _rateLimitStore.GetDelayUntilReset() + BudgetExhaustedPadding;
            }

            await ProcessNextItemAsync(ct);
            return _rateLimitStore.CalculateInterCheckDelay();
        }

        /// <summary>
        /// Discovers ASLM and module update targets and enqueues them for sequential checking.
        /// </summary>
        private async Task PopulateQueueAsync(CancellationToken ct)
        {
            lock (_queueGate)
            {
                _pendingChecks.Clear();
                _pendingChecks.Enqueue(ScheduledUpdateCheckItem.ForApp());
            }

            var modules = await _updateManager.DiscoverInstalledModulesAsync();
            lock (_queueGate)
            {
                foreach (var module in modules)
                {
                    _pendingChecks.Enqueue(ScheduledUpdateCheckItem.ForModule(module));
                }
            }

            var engines = _engineInstaller.DiscoverEngines()
                .Where(engine =>
                    engine.Status.Installed &&
                    engine.Update != null &&
                    !string.IsNullOrWhiteSpace(engine.Update.Repo))
                .ToList();
            lock (_queueGate)
            {
                foreach (var engine in engines)
                {
                    _pendingChecks.Enqueue(ScheduledUpdateCheckItem.ForEngine(engine));
                }
            }
        }

        /// <summary>
        /// Runs one queued update check and applies automatic updates when enabled.
        /// </summary>
        private async Task ProcessNextItemAsync(CancellationToken ct)
        {
            ScheduledUpdateCheckItem? item;
            lock (_queueGate)
            {
                if (_pendingChecks.Count == 0)
                {
                    return;
                }

                item = _pendingChecks.Dequeue();
            }

            _appData.Data.Updates.Normalize();
            var settings = _appData.Data.Updates;
            var publishNotifications = !settings.AutoUpdateEnabled;

            UpdateCandidate? candidate = item.Kind switch
            {
                ScheduledUpdateCheckItem.AppKind => await SafeCheckAppUpdateAsync(ct, publishNotifications),
                ScheduledUpdateCheckItem.ModuleKind when item.Module != null =>
                    await SafeCheckModuleUpdateAsync(item.Module, ct, publishNotifications),
                ScheduledUpdateCheckItem.EngineKind when item.Engine != null =>
                    await SafeCheckEngineUpdateAsync(item.Engine, ct, publishNotifications),
                _ => null
            };

            if (candidate == null || !settings.AutoUpdateEnabled)
            {
                return;
            }

            var log = new Progress<string>(message =>
                _logger.LogInformation("[Updater] {Message}", message));
            await _updateManager.ApplyDiscoveredUpdatesAsync([candidate], log, ct);
        }

        /// <summary>
        /// Checks ASLM for updates without aborting the scheduler on failure.
        /// </summary>
        private async Task<UpdateCandidate?> SafeCheckAppUpdateAsync(
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                return await _updateManager.CheckAppUpdateAsync(ct, publishNotifications, isManualRequest: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "ASLM update check failed.");
                return null;
            }
        }

        /// <summary>
        /// Checks one module for updates without aborting the scheduler on failure.
        /// </summary>
        private async Task<UpdateCandidate?> SafeCheckModuleUpdateAsync(
            ModuleConfig module,
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                return await _updateManager.CheckModuleUpdateAsync(
                    module,
                    ct,
                    publishNotifications,
                    isManualRequest: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Module update check failed for {ModuleId}.", module.Id);
                return null;
            }
        }

        /// <summary>
        /// Checks one engine for updates without aborting the scheduler on failure.
        /// </summary>
        private async Task<UpdateCandidate?> SafeCheckEngineUpdateAsync(
            EngineConfig engine,
            CancellationToken ct,
            bool publishNotifications)
        {
            try
            {
                return await _updateManager.CheckEngineUpdateAsync(
                    engine,
                    ct,
                    publishNotifications,
                    isManualRequest: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Engine update check failed for {EngineId}.", engine.Id);
                return null;
            }
        }

        private int GetPendingCheckCount()
        {
            lock (_queueGate)
            {
                return _pendingChecks.Count;
            }
        }


        // Scheduling helpers

        /// <summary>
        /// Returns whether the configured automatic check window has elapsed.
        /// </summary>
        private static bool IsDue(AppUpdateSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.LastAutoCheckUtc))
            {
                return true;
            }

            if (!DateTimeOffset.TryParse(settings.LastAutoCheckUtc, out var lastCheck))
            {
                return true;
            }

            return DateTimeOffset.UtcNow - lastCheck >= GetSchedulerDelay(settings);
        }

        /// <summary>
        /// Converts the persisted interval into a bounded scheduler delay.
        /// </summary>
        private static TimeSpan GetSchedulerDelay(AppUpdateSettings settings)
        {
            settings.Normalize();
            return TimeSpan.FromHours(settings.AutoCheckPeriodHours);
        }


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
        /// Describes one queued automatic update check.
        /// </summary>
        private sealed class ScheduledUpdateCheckItem
        {
            public const string AppKind = "app";
            public const string ModuleKind = "module";
            public const string EngineKind = "engine";

            public string Kind { get; private init; } = string.Empty;
            public ModuleConfig? Module { get; private init; }
            public EngineConfig? Engine { get; private init; }

            public static ScheduledUpdateCheckItem ForApp() =>
                new() { Kind = AppKind };

            public static ScheduledUpdateCheckItem ForModule(ModuleConfig module) =>
                new() { Kind = ModuleKind, Module = module };

            public static ScheduledUpdateCheckItem ForEngine(EngineConfig engine) =>
                new() { Kind = EngineKind, Engine = engine };
        }
    }
}
