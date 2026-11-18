// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Prepares download catalogs and module settings without tying their lifetime to any visible page.
    /// </summary>
    public sealed class DataPreparationService : IAsyncDisposable
    {
        // Dependencies and lifetime

        /// <summary>
        /// Serializes speculative I/O and shares its results with foreground requests.
        /// </summary>
        private readonly DataPreparationQueue _queue;

        /// <summary>
        /// Discovers committed module content and installation membership.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Loads categories, lists, and item descriptions through cached module bridges.
        /// </summary>
        private readonly DownloadCatalog _catalog;

        /// <summary>
        /// Resolves displayed setting values through the ordinary module command pipeline.
        /// </summary>
        private readonly SettingsService _settings;

        /// <summary>
        /// Resolves repository artwork and versions using the existing authenticated client.
        /// </summary>
        private readonly UpdateManager _updates;

        /// <summary>
        /// Cancels producers before the module runner is shut down.
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>
        /// Serializes reconciliation requests without holding an application thread while waiting.
        /// </summary>
        private readonly SemaphoreSlim _reconcile = new(1, 1);

        /// <summary>
        /// Protects the producer set and startup flag.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Tracks producers until completion so shutdown observes every outstanding operation.
        /// </summary>
        private readonly HashSet<Task> _producers = [];

        /// <summary>
        /// Identifies module paths already scheduled during discovery.
        /// </summary>
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);

        /// <summary>
        /// Prevents duplicate startup subscriptions.
        /// </summary>
        private bool _started;


        // Startup and reconciliation

        /// <summary>
        /// Creates a producer over existing services, without constructing UI controls in the background.
        /// </summary>
        public DataPreparationService(DataPreparationQueue queue, ModuleInstaller installer,
            DownloadCatalog catalog, SettingsService settings, UpdateManager updates)
        {
            _queue = queue;
            _installer = installer;
            _catalog = catalog;
            _settings = settings;
            _updates = updates;
        }

        /// <summary>
        /// Starts preparation after core startup; shell display and module autostart never wait for completion.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_started) return;
                _started = true;
                _installer.ModulesChanged += OnModulesChanged;
                _queue.Changed += OnDataChanged;
            }

            Track(() => ReconcileAsync());
        }

        /// <summary>
        /// Reconciles installation membership without treating ordinary configuration saves as content updates.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e) => Track(() => ReconcileAsync());

        /// <summary>
        /// Reloads only the module affected by maintenance, including updates that suppress ModulesChanged.
        /// </summary>
        private void OnDataChanged(object? sender, DataPreparationChangedEventArgs e) => Track(async () =>
        {
            if (e.Full) await _settings.DiscoverModulesAsync(_lifetime.Token).ConfigureAwait(false);
            var module = await _installer.LoadModuleConfig(e.SourcePath).ConfigureAwait(false);
            if (module != null && _installer.Registry.ReadIds().Contains(module.Id))
                await PrepareModuleAsync(module, e.Full).ConfigureAwait(false);
            else if (e.Full) _settings.ForgetPreparedModule(e.SourcePath);
        });

        /// <summary>
        /// Starts preparation only for newly discovered entries; individual requests share results with open pages.
        /// </summary>
        private async Task ReconcileAsync()
        {
            await _reconcile.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await _settings.DiscoverModulesAsync(_lifetime.Token).ConfigureAwait(false);
                var modules = await _installer.DiscoverModulesAsync().ConfigureAwait(false);
                var current = modules.Select(module => DataPreparationQueue.NormalizeOwner(module.SourcePath) +
                    ":" + module.Status.FirstRunCompleted)
                    .ToHashSet(StringComparer.Ordinal);
                _known.IntersectWith(current);
                foreach (var module in modules)
                {
                    if (_known.Add(DataPreparationQueue.NormalizeOwner(module.SourcePath) + ":" + module.Status.FirstRunCompleted))
                        Track(() => PrepareModuleAsync(module, full: true));
                }
            }
            finally { _reconcile.Release(); }
        }


        // Preparation steps

        /// <summary>
        /// Joins producers including their dynamically scheduled details without blocking a calling thread.
        /// </summary>
        internal async Task WhenIdleAsync(CancellationToken ct)
        {
            while (true)
            {
                Task[] tasks;
                lock (_sync)
                {
                    _producers.RemoveWhere(task => task.IsCompleted);
                    tasks = _producers.ToArray();
                }
                if (tasks.Length == 0) return;
                await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Loads one module's settings in order and schedules its bridge and artwork as independent queue steps.
        /// </summary>
        private async Task PrepareModuleAsync(ModuleConfig module, bool full)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token, _queue.GetModuleLifetime(module.SourcePath, valuesOnly: true));
            var ct = lifetime.Token;
            var registered = _installer.Registry.ReadIds().Contains(module.Id);
            if (!registered)
            {
                if (full)
                {
                    _updates.ApplyCatalogDefaults(module, installed: false);
                    await _updates.GetModuleCatalogArtworkAsync(module, ct).ConfigureAwait(false);
                    await _updates.GetModuleCatalogVersionAsync(module, false, ct).ConfigureAwait(false);
                }
                return;
            }

            if (full && module.Status.FirstRunCompleted && module.DownloadsBridge?.IsConfigured == true)
            {
                var contentLifetime = _queue.GetModuleLifetime(module.SourcePath);
                Track(() => PrepareDownloadsAsync(module, contentLifetime));
            }

            if (!SettingsService.IsModuleEligibleForSettings(module)) return;
            await _settings.CompleteModuleValuesAsync(module).WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads every returned list and detail, retaining provider ownership when categories merge across modules.
        /// </summary>
        private async Task PrepareDownloadsAsync(ModuleConfig module, CancellationToken ct)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, ct);
            ct = lifetime.Token;
            var snapshot = await _catalog.LoadModuleSnapshotAsync(module, ct).ConfigureAwait(false);
            foreach (var item in snapshot.Categories.SelectMany(category => category.Items))
            {
                ct.ThrowIfCancellationRequested();
                await _catalog.GetItemDetailAsync(item, preferCached: true, ct: ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Observes producers and isolates failures without dispatching work to the UI thread.
        /// </summary>
        private void Track(Func<Task> action)
        {
            lock (_sync)
            {
                if (_lifetime.IsCancellationRequested) return;
                var task = Task.Run(async () =>
                {
                    try { await _queue.PrepareAsync(action).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        // Maintenance or shutdown superseded this version of the module's preparation.
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Preparation] {ex}"); }
                });
                _producers.Add(task);
                _ = ForgetAsync(task);
            }
        }

        /// <summary>
        /// Removes completed producers without dropping exceptions or retaining finished module graphs.
        /// </summary>
        private async Task ForgetAsync(Task task)
        {
            await task.ConfigureAwait(false);
            lock (_sync) _producers.Remove(task);
        }

        /// <summary>
        /// Unsubscribes producers and joins them before the shared queue and module services stop.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _installer.ModulesChanged -= OnModulesChanged;
            _queue.Changed -= OnDataChanged;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Task[] tasks;
            lock (_sync) tasks = _producers.ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
