// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Runs local WebView readiness checks independently of module data preparation.
    /// </summary>
    internal sealed class ModulePagePreparationQueue : IAsyncDisposable
    {
        // Dependencies and state

        /// <summary>
        /// Paces retries and successive background checks when a listener refuses connections immediately.
        /// </summary>
        private static readonly TimeSpan ModulePageCheckInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Sends the same local GET whether the page is visible or being prepared for later.
        /// </summary>
        private static readonly HttpClient ModulePageHttp = new(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        }) { Timeout = TimeSpan.FromSeconds(5) };

        /// <summary>
        /// Identifies live module launches and announces changes to their processes.
        /// </summary>
        private readonly ModuleRunner _runner;

        /// <summary>
        /// Supplies the assigned endpoint of each module page.
        /// </summary>
        private readonly PortRegistry _ports;

        /// <summary>
        /// Protects readiness results, pending work and the current visible page.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Cancels all checks and retry delays at application shutdown.
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>
        /// Wakes the worker when background checks become available.
        /// </summary>
        private readonly SemaphoreSlim _signal = new(0, 1);

        /// <summary>
        /// Runs speculative HTTP requests one at a time.
        /// </summary>
        private readonly Task _worker;

        /// <summary>
        /// Contains the current check and its result for each running module.
        /// </summary>
        private readonly Dictionary<CancellationToken, ModulePageCheck> _modulePageChecks = [];

        /// <summary>
        /// Keeps unresolved background checks in round-robin order.
        /// </summary>
        private readonly LinkedList<ModulePageCheck> _pendingModulePages = new();

        /// <summary>
        /// Tracks active HTTP requests until their completion, including requests for superseded endpoints.
        /// </summary>
        private readonly HashSet<Task<bool>> _modulePageAttempts = [];

        /// <summary>
        /// Gives the page in the shared WebView exclusive control of its retry cadence.
        /// </summary>
        private ModulePageTarget? _visibleModulePage;

        /// <summary>
        /// Indicates when a visible consumer should stop waiting for this queue.
        /// </summary>
        internal bool IsStopping => _lifetime.IsCancellationRequested;


        // Lifecycle and module discovery

        /// <summary>
        /// Observes module lifetimes and starts an asynchronous worker for the current app session.
        /// </summary>
        public ModulePagePreparationQueue(ModuleRunner runner, PortRegistry ports)
        {
            _runner = runner;
            _ports = ports;
            _runner.ConsoleStore.ModulePageStateChanged += OnModulePagesChanged;
            _ports.PortsRedistributed += OnModulePagesChanged;
            _worker = Task.Run(ConsumeAsync);
        }

        /// <summary>
        /// Refreshes the checks after a process or port change.
        /// </summary>
        private void OnModulePagesChanged(object? sender, EventArgs e) => RefreshModulePageChecks();

        /// <summary>
        /// Reconciles checks with running modules and their assigned addresses.
        /// </summary>
        private void RefreshModulePageChecks()
        {
            if (IsStopping) return;
            PruneModulePageChecks();
            foreach (var module in _runner.GetRunningModuleConfigs())
                if (module.HasPage && module.Status.Enabled) RefreshModulePageCheck(module);
        }

        /// <summary>
        /// Registers the same endpoint for an opening page and for a module that started in the background.
        /// </summary>
        internal ModulePageTarget? RefreshModulePageCheck(ModuleConfig module)
        {
            if (_runner.GetModulePageRun(module.SourcePath) is not { } run ||
                _ports.TryGetModulePageUrl(module) is not { } url)
                return null;

            // A new endpoint replaces the previous check for the same launch.
            var target = new ModulePageTarget(run, url);
            lock (_sync)
            {
                if (IsStopping || run.IsCancellationRequested) return null;
                if (FindModulePageCheck(target) != null) return target;

                RemoveModulePageCheck(run);
                var check = new ModulePageCheck(module, target);
                _modulePageChecks.Add(run, check);
                EnqueueModulePageCheck(check);
            }
            return target;
        }

        /// <summary>
        /// Verifies that a probe or navigation still points to the same running server.
        /// </summary>
        internal bool IsModulePageCurrent(ModuleConfig module, ModulePageTarget target) =>
            _runner.GetModulePageRun(module.SourcePath) == target.Run &&
            _ports.TryGetModulePageUrl(module) == target.Url;


        /// <summary>
        /// Retires checks for exits and endpoint changes after a process or port notification.
        /// </summary>
        private void PruneModulePageChecks()
        {
            ModulePageCheck[] checks;
            lock (_sync) checks = _modulePageChecks.Values.ToArray();
            foreach (var check in checks)
            {
                if (IsModulePageCurrent(check.Module, check.Target)) continue;
                lock (_sync)
                    if (_modulePageChecks.GetValueOrDefault(check.Target.Run) == check)
                        RemoveModulePageCheck(check.Target.Run);
            }
        }


        // Page activation

        /// <summary>
        /// Takes the visible page out of speculative rotation and returns the previous page to the tail if needed.
        /// </summary>
        internal void SetVisibleModulePage(ModulePageTarget? target)
        {
            lock (_sync)
            {
                if (_visibleModulePage == target) return;
                var previous = _visibleModulePage;
                _visibleModulePage = target;
                if (previous is { } oldPage && FindModulePageCheck(oldPage) is { } oldCheck)
                    EnqueueModulePageCheck(oldCheck);
                if (target is { } newPage && FindModulePageCheck(newPage) is { } newCheck)
                    DequeueModulePageCheck(newCheck);
            }
        }

        /// <summary>
        /// Returns whether the current run has already answered the local HTTP request.
        /// </summary>
        internal bool IsModulePageReady(ModulePageTarget target)
        {
            lock (_sync) return FindModulePageCheck(target)?.Ready == true;
        }

        /// <summary>
        /// Waits on demand with the WebView's retry cadence, joining a request already in flight for this run.
        /// </summary>
        internal async Task<bool> WaitForModulePageAsync(ModulePageTarget target, CancellationToken token)
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            token = lifetime.Token;
            while (true)
            {
                Task<bool> attempt;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (FindModulePageCheck(target) is not { } check) return false;
                    if (check.Ready) return true;
                    DequeueModulePageCheck(check);
                    attempt = check.Attempt ?? StartModulePageCheck(check);
                }

                if (await attempt.WaitAsync(token).ConfigureAwait(false)) return true;
                await Task.Delay(ModulePageCheckInterval, token).ConfigureAwait(false);
            }
        }


        // Background scheduling

        /// <summary>
        /// Looks up a check only when its run and URL still match. Callers hold the queue lock.
        /// </summary>
        private ModulePageCheck? FindModulePageCheck(ModulePageTarget target) =>
            !target.Run.IsCancellationRequested && _modulePageChecks.TryGetValue(target.Run, out var check) &&
            check.Target == target ? check : null;

        /// <summary>
        /// Retires a check so a late HTTP result cannot belong to the next run. Callers hold the queue lock.
        /// </summary>
        private void RemoveModulePageCheck(CancellationToken run)
        {
            if (!_modulePageChecks.Remove(run, out var check)) return;
            DequeueModulePageCheck(check);
            if (check.Cancellation != null) _ = check.Cancellation.CancelAsync();
        }

        /// <summary>
        /// Appends an idle unresolved page behind every already waiting page. Callers hold the queue lock.
        /// </summary>
        private void EnqueueModulePageCheck(ModulePageCheck check)
        {
            if (_lifetime.IsCancellationRequested || check.Ready || check.Attempt != null || check.Node != null ||
                check.Target == _visibleModulePage)
                return;
            check.Node = _pendingModulePages.AddLast(check);
            if (_signal.CurrentCount == 0) _signal.Release();
        }

        /// <summary>
        /// Removes the check's pending slot without interrupting an in-flight request.
        /// </summary>
        private void DequeueModulePageCheck(ModulePageCheck check)
        {
            if (check.Node == null) return;
            _pendingModulePages.Remove(check.Node);
            check.Node = null;
        }

        /// <summary>
        /// Processes the background rotation with a short pause after each completed attempt.
        /// </summary>
        private async Task ConsumeAsync()
        {
            var token = _lifetime.Token;
            try
            {
                RefreshModulePageChecks();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    Task<bool>? attempt = null;
                    lock (_sync)
                        if (_pendingModulePages.First?.Value is { } check)
                        {
                            DequeueModulePageCheck(check);
                            attempt = StartModulePageCheck(check);
                        }

                    // Only the background rotation waits here; visible pages can start or share an attempt directly.
                    if (attempt == null)
                    {
                        await _signal.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }

                    await attempt.ConfigureAwait(false);
                    await Task.Delay(ModulePageCheckInterval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Shutdown cancels the idle wait, cooldown and all active checks.
            }
        }

        /// <summary>
        /// Publishes a single shared attempt before dispatching it away from the caller's thread.
        /// </summary>
        private Task<bool> StartModulePageCheck(ModulePageCheck check)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, check.Target.Run);
            check.Cancellation = cancellation;
            var attempt = Task.Run(() => CompleteModulePageCheckAsync(check, cancellation));
            check.Attempt = attempt;
            _modulePageAttempts.Add(attempt);
            return attempt;
        }

        /// <summary>
        /// Attempts one HTTP request and moves unresolved inactive pages to the end of the rotation.
        /// </summary>
        private async Task<bool> CompleteModulePageCheckAsync(ModulePageCheck check,
            CancellationTokenSource cancellation)
        {
            var token = cancellation.Token;
            var current = false;
            var hasResponse = false;
            try
            {
                // The process or its port may have changed before dispatch or while HTTP was in flight.
                current = !token.IsCancellationRequested && IsModulePageCurrent(check.Module, check.Target);
                if (current)
                {
                    hasResponse = await ProbeModulePageAsync(check.Target.Url, token).ConfigureAwait(false);
                    current = IsModulePageCurrent(check.Module, check.Target);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Maintenance and shutdown retire the request without marking the page ready.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModulePage] {ex}");
                current = false;
            }

            // Publish results only for the entry that still owns this target.
            lock (_sync)
            {
                _modulePageAttempts.Remove(check.Attempt!);
                check.Attempt = null;
                check.Cancellation = null;
                var usable = current && !token.IsCancellationRequested && FindModulePageCheck(check.Target) == check;
                cancellation.Dispose();
                if (usable)
                {
                    check.Ready |= hasResponse;
                    EnqueueModulePageCheck(check);
                    return check.Ready;
                }

                if (_modulePageChecks.GetValueOrDefault(check.Target.Run) == check)
                    RemoveModulePageCheck(check.Target.Run);
                return false;
            }
        }


        /// <summary>
        /// Treats any HTTP response as readiness without reading the response body or following redirects.
        /// </summary>
        private static async Task<bool> ProbeModulePageAsync(string url, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.CacheControl = new() { NoCache = true, NoStore = true };
                using var response = await ModulePageHttp.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                return true;
            }
            catch (HttpRequestException)
            {
                // The listener may not have opened yet.
                return false;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // A timeout allows another attempt while the page's request is still wanted.
                return false;
            }
        }


        // Shutdown and checked pages

        /// <summary>
        /// Stops observing modules and joins outstanding page requests before the runner is released.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _runner.ConsoleStore.ModulePageStateChanged -= OnModulePagesChanged;
            _ports.PortsRedistributed -= OnModulePagesChanged;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false);

            Task<bool>[] attempts;
            lock (_sync) attempts = _modulePageAttempts.ToArray();
            await Task.WhenAll(attempts).ConfigureAwait(false);
        }

        /// <summary>
        /// Holds the readiness result and at most one HTTP request for a running module page.
        /// </summary>
        private sealed class ModulePageCheck(ModuleConfig module, ModulePageTarget target)
        {
            /// <summary>
            /// Supplies the manifest path and page port used to validate the endpoint.
            /// </summary>
            public ModuleConfig Module { get; } = module;

            /// <summary>
            /// Identifies the exact run and address the request may prepare.
            /// </summary>
            public ModulePageTarget Target { get; } = target;

            /// <summary>
            /// Keeps the HTTP result available for the lifetime of this run and address.
            /// </summary>
            public bool Ready { get; set; }

            /// <summary>
            /// Lets page navigation join a background request for the same run.
            /// </summary>
            public Task<bool>? Attempt { get; set; }

            /// <summary>
            /// Cancels the HTTP request when the run is retired.
            /// </summary>
            public CancellationTokenSource? Cancellation { get; set; }

            /// <summary>
            /// Locates the check in the background rotation for immediate promotion or removal.
            /// </summary>
            public LinkedListNode<ModulePageCheck>? Node { get; set; }
        }
    }

    /// <summary>
    /// Identifies a module page by its unique launch lifetime and URL.
    /// </summary>
    internal readonly record struct ModulePageTarget(CancellationToken Run, string Url);
}
