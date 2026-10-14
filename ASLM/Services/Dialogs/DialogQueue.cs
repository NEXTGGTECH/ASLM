// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Serializes independent requests on the owning window's UI thread.
/// </summary>
internal sealed class DialogQueue
{
    private sealed record Pending(DialogSession Session, Func<DialogSession, Task<DialogResult>> Present,
        TaskCompletionSource<DialogResult> Finished);
    private readonly Queue<Pending> _pending = new();
    private DialogSession? _active;
    private bool _pumping;

    /// <summary>
    /// Adds a request and starts the pump unless another request already owns the presenter.
    /// </summary>
    internal Task<DialogResult> EnqueueAsync(DialogSession session, Func<DialogSession, Task<DialogResult>> present)
    {
        var item = new Pending(session, present, new(TaskCreationOptions.RunContinuationsAsynchronously));
        _pending.Enqueue(item);
        _ = PumpAsync();
        return item.Finished.Task;
    }

    /// <summary>
    /// Cancels both the visible dialog and all queued requests when their host is detached.
    /// </summary>
    internal void CancelAll()
    {
        _active?.Cancel();
        foreach (var item in _pending) item.Session.Cancel();
    }

    /// <summary>
    /// Presents requests sequentially and completes every waiter even if a presenter fails.
    /// </summary>
    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_pending.TryDequeue(out var item))
            {
                _active = item.Session;
                try
                {
                    // Cancelled waiters are completed without briefly opening a stale dialog.
                    var result = item.Session.Completion.IsCompleted
                        ? await item.Session.Completion : await item.Present(item.Session);
                    item.Finished.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    // A failed view must not leave its callback alive or stall subsequent dialogs.
                    item.Session.Cancel();
                    item.Finished.TrySetException(ex);
                }
                finally { _active = null; }
            }
        }
        finally { _pumping = false; }
    }
}
