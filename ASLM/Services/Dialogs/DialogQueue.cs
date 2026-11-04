// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Dialogs
{
    /// <summary>
    /// Serializes independent requests on the owning window's UI thread.
    /// </summary>
    internal sealed class DialogQueue
    {
        // Queue state

        /// <summary>
        /// One waiting request: its session, the delegate that presents it, and the source that completes its caller.
        /// </summary>
        private sealed record Pending(DialogSession Session, Func<DialogSession, Task<DialogResult>> Present,
            TaskCompletionSource<DialogResult> Finished);

        /// <summary>
        /// Requests waiting for the presenter, in arrival order.
        /// </summary>
        private readonly Queue<Pending> _pending = new();

        /// <summary>
        /// Session of the dialog currently on screen, or null while the presenter is idle.
        /// </summary>
        private DialogSession? _active;

        /// <summary>
        /// Whether a pump loop is already running, so concurrent enqueues do not start a second one.
        /// </summary>
        private bool _pumping;


        // Queueing

        /// <summary>
        /// Adds a request and starts the pump unless another request already owns the presenter.
        /// </summary>
        /// <param name="session">Session that holds the request state and its completion.</param>
        /// <param name="present">Delegate that shows the session and returns its result once the dialog closes.</param>
        /// <returns>A task that completes with the dialog result after the request was presented or cancelled.</returns>
        internal Task<DialogResult> EnqueueAsync(DialogSession session, Func<DialogSession, Task<DialogResult>> present)
        {
            var item = new Pending(session, present, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending.Enqueue(item);
            _ = PumpAsync();
            return item.Finished.Task;
        }

        /// <summary>
        /// Cancels both the visible dialog and all queued requests when their host is detached.
        /// Queued sessions complete as cancelled, and the pump skips presenting them.
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
            // A running pump will pick up newly queued items itself.
            if (_pumping) return;
            _pumping = true;
            try
            {
                // Take requests one by one so at most one dialog is visible at a time.
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
}
