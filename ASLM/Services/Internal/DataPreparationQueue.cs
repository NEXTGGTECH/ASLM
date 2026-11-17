// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Owns session data loads, serializes speculative work, and lets visible consumers take queued work immediately.
    /// </summary>
    public sealed class DataPreparationQueue : IAsyncDisposable
    {
        // State

        /// <summary>
        /// Protects entries, pending work, and maintenance reservations without holding a lock across awaits.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Identifies speculative calls throughout their asynchronous execution context.
        /// </summary>
        private readonly AsyncLocal<bool> _background = new();

        /// <summary>
        /// Shares successful results and in-flight requests by module and operation.
        /// </summary>
        private readonly Dictionary<(string Owner, string Key), Entry> _entries = [];

        /// <summary>
        /// Contains speculative operations that have not started yet.
        /// </summary>
        private readonly LinkedList<Entry> _pending = new();

        /// <summary>
        /// Identifies settings whose queued reads take precedence without changing foreground download requests.
        /// </summary>
        private string? _settingsPriorityOwner;

        /// <summary>
        /// Gets the settings section whose remaining steps should run first.
        /// </summary>
        internal string? SettingsPriorityOwner
        {
            get { lock (_sync) return _settingsPriorityOwner; }
        }

        /// <summary>
        /// Changes settings priority without cancelling the active request or discarding other sections' progress.
        /// </summary>
        internal void PrioritizeSettings(string? owner)
        {
            lock (_sync) _settingsPriorityOwner = owner == null ? null : NormalizeOwner(owner);
        }

        /// <summary>
        /// Counts nested maintenance reservations for each module.
        /// </summary>
        private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);

        /// <summary>
        /// Cancels composite producers when their module changes between individual read operations.
        /// </summary>
        private readonly Dictionary<(string Owner, bool Values), CancellationTokenSource> _generations = [];

        /// <summary>
        /// Cancels all active loads when the application shuts down.
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>
        /// Signals the single speculative consumer when work becomes available.
        /// </summary>
        private readonly SemaphoreSlim _signal = new(0);

        /// <summary>
        /// Owns the speculative consumer until shutdown.
        /// </summary>
        private readonly Task _worker;

        /// <summary>
        /// Notifies consumers after module data becomes invalid or maintenance ends.
        /// </summary>
        public event EventHandler<DataPreparationChangedEventArgs>? Changed;


        /// <summary>
        /// Gets the application lifetime for composite loads that must survive detached views.
        /// </summary>
        internal CancellationToken Lifetime => _lifetime.Token;

        /// <summary>
        /// Gets whether the calling producer is performing speculative work rather than serving a visible page.
        /// </summary>
        internal bool IsBackground => _background.Value;


        // Initialization and requests

        /// <summary>
        /// Starts one asynchronous speculative consumer without reserving a thread while it is idle.
        /// </summary>
        public DataPreparationQueue() => _worker = Task.Run(ConsumeAsync);

        /// <summary>
        /// Runs a producer whose individual requests enter the speculative queue.
        /// </summary>
        public async Task PrepareAsync(Func<Task> action)
        {
            _background.Value = true;
            try { await action().ConfigureAwait(false); }
            finally { _background.Value = false; }
        }

        /// <summary>
        /// Returns a shared result; caller cancellation only detaches that caller and never cancels the load.
        /// Foreground requests remove pending work from the queue and start it without waiting for other modules.
        /// </summary>
        public async Task<T> GetAsync<T>(string owner, string key, Func<CancellationToken, Task<T>> load,
            CancellationToken ct = default, TimeSpan? lifetime = null)
        {
            owner = NormalizeOwner(owner);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Entry? start = null;
                Task? resume = null;
                Entry? entry = null;
                lock (_sync)
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    if (_reservations.TryGetValue(owner, out var reservation))
                        resume = reservation.Released.Task;
                    else
                    {
                        var identity = (owner, key);
                        if (_entries.TryGetValue(identity, out entry) && entry.Completion.Task.IsCompletedSuccessfully &&
                            lifetime is { } maxAge && DateTimeOffset.UtcNow - entry.CompletedAt > maxAge)
                        {
                            _entries.Remove(identity);
                            entry = null;
                        }

                        if (entry == null)
                        {
                            entry = new Entry(owner, key, async token => await load(token).ConfigureAwait(false),
                                _lifetime.Token);
                            _entries.Add(identity, entry);
                            if (_background.Value)
                            {
                                entry.Node = _pending.AddLast(entry);
                                _signal.Release();
                            }
                        }

                        if (!_background.Value && !entry.Started)
                        {
                            if (entry.Node != null) _pending.Remove(entry.Node);
                            entry.Node = null;
                            entry.Started = true;
                            start = entry;
                        }
                    }
                }

                if (resume != null)
                {
                    await resume.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                if (start != null) _ = Task.Run(() => ExecuteAsync(start));
                return (T)(await entry!.Completion.Task.WaitAsync(ct).ConfigureAwait(false))!;
            }
        }


        // Maintenance coordination

        /// <summary>
        /// Gets a module generation token so a composite producer cannot enqueue obsolete work after maintenance.
        /// </summary>
        internal CancellationToken GetModuleLifetime(string owner, bool valuesOnly = false)
        {
            owner = NormalizeOwner(owner);
            lock (_sync)
            {
                if (_reservations.ContainsKey(owner)) return new CancellationToken(canceled: true);
                if (!_generations.TryGetValue((owner, valuesOnly), out var generation))
                    _generations.Add((owner, valuesOnly), generation =
                        CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
                return generation.Token;
            }
        }

        /// <summary>
        /// Cancels obsolete reads before a module mutation and prevents new reads until the returned lease ends.
        /// Values-only mutations preserve bridge and repository metadata.
        /// </summary>
        public async Task<IDisposable> SuspendAsync(string owner, bool valuesOnly = false)
        {
            owner = NormalizeOwner(owner);
            List<Entry> obsolete;
            List<CancellationTokenSource> generations;
            lock (_sync)
            {
                var generationKeys = _generations.Keys.Where(key => key.Owner == owner &&
                    (!valuesOnly || key.Values)).ToArray();
                generations = generationKeys.Select(key => _generations[key]).ToList();
                foreach (var key in generationKeys) _generations.Remove(key);
                if (!_reservations.TryGetValue(owner, out var reservation))
                    _reservations.Add(owner, reservation = new Reservation());
                reservation.Count++;
                reservation.Full |= !valuesOnly;
                obsolete = _entries.Values.Where(entry => entry.Owner == owner &&
                    (!valuesOnly || entry.Key.StartsWith("setting:", StringComparison.Ordinal))).ToList();
                foreach (var entry in obsolete)
                {
                    _entries.Remove((entry.Owner, entry.Key));
                    if (entry.Node != null) _pending.Remove(entry.Node);
                    entry.Node = null;
                }
            }

            // Cancellation may terminate child processes, so it always runs away from the UI thread and queue lock.
            await Task.Run(async () =>
            {
                foreach (var generation in generations)
                {
                    await generation.CancelAsync().ConfigureAwait(false);
                    generation.Dispose();
                }
                foreach (var entry in obsolete)
                {
                    try { await entry.Cancellation.CancelAsync().ConfigureAwait(false); }
                    catch (ObjectDisposedException)
                    {
                        // Completed requests release their cancellation registrations before cache invalidation.
                    }
                    if (!entry.Started) entry.Completion.TrySetCanceled();
                }

                foreach (var entry in obsolete)
                {
                    try { await entry.Completion.Task.ConfigureAwait(false); }
                    catch (Exception)
                    {
                        // Obsolete results are deliberately discarded before mutation.
                    }
                    if (!entry.Started) entry.Cancellation.Dispose();
                }
            }).ConfigureAwait(false);
            return new Lease(() => Resume(owner));
        }

        /// <summary>
        /// Releases one reservation and publishes the strongest invalidation after nested mutations finish.
        /// </summary>
        private void Resume(string owner)
        {
            Reservation reservation;
            lock (_sync)
            {
                reservation = _reservations[owner];
                if (--reservation.Count != 0) return;
                _reservations.Remove(owner);
                reservation.Released.TrySetResult();
            }

            foreach (EventHandler<DataPreparationChangedEventArgs> handler in Changed?.GetInvocationList() ?? [])
            {
                try { handler(this, new(owner, reservation.Full)); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
        }

        /// <summary>
        /// Normalizes module identities consistently with the platform filesystem.
        /// </summary>
        internal static string NormalizeOwner(string owner) => OperatingSystem.IsWindows() ||
            OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? owner.ToUpperInvariant() : owner;


        // Consumer and shutdown

        /// <summary>
        /// Executes speculative operations one at a time; visible consumers may independently promote pending work.
        /// </summary>
        private async Task ConsumeAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    Entry? entry;
                    lock (_sync)
                    {
                        var node = _pending.First;
                        if (_settingsPriorityOwner != null)
                            for (var candidate = _pending.First; candidate != null; candidate = candidate.Next)
                                if (candidate.Value.Owner == _settingsPriorityOwner &&
                                    candidate.Value.Key.StartsWith("setting:", StringComparison.Ordinal))
                                {
                                    node = candidate;
                                    break;
                                }
                        entry = node?.Value;
                        if (entry == null) continue;
                        _pending.Remove(node!);
                        entry.Node = null;
                        entry.Started = true;
                    }

                    await ExecuteAsync(entry).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Shutdown ends the idle consumer as well as any active request.
            }
        }

        /// <summary>
        /// Publishes one result or removes a failed entry so a later request can retry it.
        /// </summary>
        private async Task ExecuteAsync(Entry entry)
        {
            try
            {
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                var value = await entry.Load!(entry.Cancellation.Token).ConfigureAwait(false);
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                entry.CompletedAt = DateTimeOffset.UtcNow;
                entry.Completion.TrySetResult(value);
            }
            catch (Exception ex)
            {
                lock (_sync)
                    if (_entries.GetValueOrDefault((entry.Owner, entry.Key)) == entry)
                        _entries.Remove((entry.Owner, entry.Key));
                if (ex is OperationCanceledException) entry.Completion.TrySetCanceled();
                else
                {
                    entry.Completion.TrySetException(ex);

                    // A closing page may have detached the last observer before the producer failed.
                    _ = entry.Completion.Task.Exception;
                }
            }
            finally
            {
                entry.Load = null;
                entry.Cancellation.Dispose();
            }
        }

        /// <summary>
        /// Cancels loads and joins the consumer and foreground work before module services are disposed.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Entry[] entries;
            lock (_sync)
            {
                entries = _entries.Values.ToArray();
                foreach (var entry in entries.Where(entry => !entry.Started)) entry.Completion.TrySetCanceled();
                foreach (var reservation in _reservations.Values) reservation.Released.TrySetCanceled();
            }

            await _worker.ConfigureAwait(false);
            foreach (var entry in entries)
            {
                try { await entry.Completion.Task.ConfigureAwait(false); }
                catch (Exception)
                {
                    // Shutdown observes failures from canceled module commands.
                }
                entry.Cancellation.Dispose();
            }
        }


        // Request records

        /// <summary>
        /// Owns a request independently of any page or individual caller.
        /// </summary>
        private sealed class Entry(string owner, string key, Func<CancellationToken, Task<object?>> load,
            CancellationToken lifetime)
        {
            /// <summary>
            /// Identifies the module instance that supplied the data.
            /// </summary>
            public string Owner { get; } = owner;

            /// <summary>
            /// Distinguishes bridge queries, settings, and repository metadata.
            /// </summary>
            public string Key { get; } = key;

            /// <summary>
            /// Runs the underlying I/O independently of caller cancellation.
            /// </summary>
            public Func<CancellationToken, Task<object?>>? Load { get; set; } = load;

            /// <summary>
            /// Cancels work on invalidation or application shutdown.
            /// </summary>
            public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

            /// <summary>
            /// Shares the result with every consumer without inline continuation execution.
            /// </summary>
            public TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// References the pending node so foreground promotion can remove it immediately.
            /// </summary>
            public LinkedListNode<Entry>? Node { get; set; }

            /// <summary>
            /// Indicates that execution has been claimed by a consumer.
            /// </summary>
            public bool Started { get; set; }

            /// <summary>
            /// Records successful completion for metadata expiration.
            /// </summary>
            public DateTimeOffset CompletedAt { get; set; }
        }

        /// <summary>
        /// Counts nested mutations and wakes waiting readers only after all mutations finish.
        /// </summary>
        private sealed class Reservation
        {
            /// <summary>
            /// Counts outstanding maintenance leases.
            /// </summary>
            public int Count { get; set; }

            /// <summary>
            /// Records whether content or only values changed.
            /// </summary>
            public bool Full { get; set; }

            /// <summary>
            /// Releases readers without executing their continuations under the queue lock.
            /// </summary>
            public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Releases a maintenance reservation exactly once.
        /// </summary>
        private sealed class Lease(Action release) : IDisposable
        {
            /// <summary>
            /// Holds the callback until the first disposal.
            /// </summary>
            private Action? _release = release;

            /// <summary>
            /// Resumes readers after the owning operation completes.
            /// </summary>
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    /// <summary>
    /// Identifies the module and the scope of data invalidated by a completed mutation.
    /// </summary>
    public sealed class DataPreparationChangedEventArgs(string sourcePath, bool full) : EventArgs
    {
        /// <summary>
        /// Gets the normalized manifest path of the affected module.
        /// </summary>
        public string SourcePath { get; } = sourcePath;

        /// <summary>
        /// Gets whether bridge data and setting definitions also need reloading.
        /// </summary>
        public bool Full { get; } = full;
    }
}
