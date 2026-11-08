// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Independent queue lanes; operations of one lane run strictly one after another.
    /// </summary>
    public enum DownloadQueueKind
    {
        /// <summary>
        /// Lane for bridge resource downloads.
        /// </summary>
        Resources,

        /// <summary>
        /// Lane for module installations and updates.
        /// </summary>
        Modules,

        /// <summary>
        /// Lane for module removals.
        /// </summary>
        ModuleRemoval
    }

    /// <summary>
    /// Lifecycle phase of one queued operation.
    /// </summary>
    public enum DownloadOperationState
    {
        /// <summary>
        /// Waiting for the preceding operation of the same lane to finish.
        /// </summary>
        Queued,

        /// <summary>
        /// The operation action is executing.
        /// </summary>
        Running,

        /// <summary>
        /// A removal operation is executing.
        /// </summary>
        Removing
    }

    /// <summary>
    /// Independent session-only FIFO lanes for module installs, removals, and bridge resources.
    /// </summary>
    public sealed class DownloadQueue
    {
        // Queue state

        /// <summary>
        /// Guards <see cref="_pending"/>, <see cref="_tails"/> and the state of every entry.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Operations that are queued or running, by operation key.
        /// </summary>
        private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Completion signal of the most recently enqueued operation of each lane; the next operation waits for it.
        /// </summary>
        private readonly Dictionary<DownloadQueueKind, Task> _tails = [];


        // Nested types

        /// <summary>
        /// Bookkeeping record of one queued operation.
        /// </summary>
        private sealed class Entry(CancellationToken ct)
        {
            /// <summary>
            /// Cancellation source linked to the caller token; cancelling it aborts the operation.
            /// </summary>
            public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);

            /// <summary>
            /// Current phase of the operation.
            /// </summary>
            public DownloadOperationState State { get; set; } = DownloadOperationState.Queued;

            /// <summary>
            /// Task of the whole operation, including waiting for its turn.
            /// </summary>
            public Task Task { get; set; } = null!;
        }


        // Notifications

        /// <summary>
        /// Raised when an operation is added, starts running or finishes.
        /// </summary>
        public event EventHandler? StateChanged;


        // Queries and cancellation

        /// <summary>
        /// Returns whether an operation with this key is queued or running.
        /// </summary>
        public bool Contains(string key)
        {
            lock (_sync) return _pending.ContainsKey(key);
        }

        /// <summary>
        /// Returns the phase of the operation with this key, or null when it is not queued.
        /// </summary>
        public DownloadOperationState? GetState(string key)
        {
            lock (_sync) return _pending.TryGetValue(key, out var entry) ? entry.State : null;
        }

        /// <summary>
        /// Returns the task of the operation with this key, or null when it is not queued.
        /// </summary>
        internal Task? GetTask(string key)
        {
            lock (_sync) return _pending.GetValueOrDefault(key)?.Task;
        }

        /// <summary>
        /// Requests cancellation of the operation with this key; unknown keys are ignored.
        /// </summary>
        public void Cancel(string key)
        {
            Entry? entry;
            lock (_sync) entry = _pending.GetValueOrDefault(key);

            // Cancellation callbacks must not run under the queue lock.
            try { entry?.Cancellation.Cancel(); }
            catch (ObjectDisposedException)
            {
                // The job finished between lookup and cancellation.
            }
        }


        // Enqueueing

        /// <summary>
        /// Queues an operation at the end of its lane and returns the task of the whole run.
        /// An operation with the same key that is already pending is returned instead of queuing a second one.
        /// </summary>
        /// <param name="key">Unique operation key, for example a module id.</param>
        /// <param name="action">Work executed once all earlier operations of the lane have finished.</param>
        /// <param name="ct">Caller token linked to the operation's own cancellation source.</param>
        /// <param name="kind">Lane that serializes this operation.</param>
        /// <param name="removal">Whether the operation reports <see cref="DownloadOperationState.Removing"/> instead of running.</param>
        /// <param name="prepare">Optional step executed immediately, before the operation waits for its turn.</param>
        /// <param name="finish">Optional cleanup executed after the action; receives whether the operation was cancelled.</param>
        public Task<T> EnqueueAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default,
            DownloadQueueKind kind = DownloadQueueKind.Resources, bool removal = false,
            Func<CancellationToken, Task>? prepare = null, Func<bool, Task>? finish = null)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
            Task<T> task;
            lock (_sync)
            {
                // Join an operation that is already pending under the same key.
                if (_pending.TryGetValue(key, out var existing)) return (Task<T>)existing.Task;

                // Chain behind the previous operation of this lane and become the new tail.
                var previous = _tails.GetValueOrDefault(kind, Task.CompletedTask);
                var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _tails[kind] = released.Task;
                var entry = new Entry(ct);
                task = Task.Run(async () =>
                {
                    // Publish the entry before preparation can notify observers or complete the job.
                    lock (_sync) { }
                    var token = entry.Cancellation.Token;
                    try
                    {
                        // Prepare immediately, wait for the lane, and only then switch to the running state.
                        if (prepare != null) await prepare(token).ConfigureAwait(false);
                        await previous.WaitAsync(token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        lock (_sync) entry.State = removal ? DownloadOperationState.Removing : DownloadOperationState.Running;
                        RaiseStateChanged();
                        return await action(token).ConfigureAwait(false);
                    }
                    finally
                    {
                        try
                        {
                            if (finish != null)
                            {
                                // Dependency rollback is itself a module operation, never concurrent
                                // with the preceding module's installation.
                                await previous.ConfigureAwait(false);
                                await finish(token.IsCancellationRequested).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            // Unregister the entry and let the next operation of the lane proceed.
                            lock (_sync)
                            {
                                _pending.Remove(key);
                                entry.Cancellation.Dispose();
                            }

                            _ = ReleaseAfterAsync(previous, released);
                            RaiseStateChanged();
                        }
                    }
                });
                entry.Task = task;
                _pending.Add(key, entry);
            }

            RaiseStateChanged();
            return task;
        }


        // Lane and notification helpers

        /// <summary>
        /// Completes the lane tail signal only after the previous operation has finished, so the lane order
        /// holds even when this operation was cancelled before its turn.
        /// </summary>
        private static async Task ReleaseAfterAsync(Task previous, TaskCompletionSource released)
        {
            await previous.ConfigureAwait(false);
            released.SetResult();
        }

        /// <summary>
        /// Notifies subscribers that the queue changed; a failing subscriber never breaks the queue.
        /// </summary>
        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
