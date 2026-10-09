// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal;

public enum DownloadQueueKind { Resources, Modules, ModuleRemoval }
public enum DownloadOperationState { Queued, Running, Removing }

/// <summary>Independent session-only FIFO lanes for module installs, removals, and bridge resources.</summary>
public sealed class DownloadQueue
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DownloadQueueKind, Task> _tails = [];

    private sealed class Entry(CancellationToken ct)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(ct);
        public DownloadOperationState State { get; set; } = DownloadOperationState.Queued;
        public Task Task { get; set; } = null!;
    }

    public event EventHandler? StateChanged;

    public bool Contains(string key)
    {
        lock (_sync) return _pending.ContainsKey(key);
    }

    public DownloadOperationState? GetState(string key)
    {
        lock (_sync) return _pending.TryGetValue(key, out var entry) ? entry.State : null;
    }

    internal Task? GetTask(string key)
    {
        lock (_sync) return _pending.GetValueOrDefault(key)?.Task;
    }

    public void Cancel(string key)
    {
        Entry? entry;
        lock (_sync) entry = _pending.GetValueOrDefault(key);
        // Cancellation callbacks must not run under the queue lock.
        try { entry?.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The job finished between lookup and cancellation. */ }
    }

    public Task<T> EnqueueAsync<T>(string key, Func<CancellationToken, Task<T>> action, CancellationToken ct = default,
        DownloadQueueKind kind = DownloadQueueKind.Resources, bool removal = false,
        Func<CancellationToken, Task>? prepare = null, Func<bool, Task>? finish = null)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        Task<T> task;
        lock (_sync)
        {
            if (_pending.TryGetValue(key, out var existing)) return (Task<T>)existing.Task;
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

    private static async Task ReleaseAfterAsync(Task previous, TaskCompletionSource released)
    {
        await previous.ConfigureAwait(false);
        released.SetResult();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
}
