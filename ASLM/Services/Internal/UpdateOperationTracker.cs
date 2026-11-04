// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Shares live update state and joins concurrent requests for the same app or engine target.
    /// </summary>
    internal sealed class UpdateOperationTracker
    {
        // Fields and events

        /// <summary>
        /// Minimum interval between two progress notifications raised for the same operation.
        /// </summary>
        private const long ProgressNotificationIntervalMilliseconds = 100;

        /// <summary>
        /// Guards the operation table and the mutable state of every <see cref="Operation"/>.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Running operations by target key; an entry exists only while the operation is in flight.
        /// </summary>
        private readonly Dictionary<string, Operation> _operations = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Notifies listeners when an operation starts, changes stage or progress, or finishes.
        /// </summary>
        internal event EventHandler? StateChanged;


        // State access

        /// <summary>
        /// Returns the latest immutable snapshot, including progress recorded while no view was open.
        /// </summary>
        internal UpdateOperationSnapshot? GetSnapshot(string key)
        {
            lock (_sync)
            {
                return _operations.GetValueOrDefault(key)?.Snapshot;
            }
        }


        // Shared execution

        /// <summary>
        /// Reserves one target or joins its existing task without canceling another caller's operation.
        /// </summary>
        internal Task<bool> RunAsync(string key, string version, Func<Operation, Task<bool>> action,
            CancellationToken ct = default)
        {
            // A caller that is already canceled must not reserve the target.
            if (ct.IsCancellationRequested)
            {
                return Task.FromCanceled<bool>(ct);
            }

            // Reserve the target, or join the operation that already owns it.
            Operation operation;
            lock (_sync)
            {
                // Automatic updates, settings, and notification actions share the same target reservation.
                if (_operations.TryGetValue(key, out var existing))
                {
                    return existing.Completion.Task.WaitAsync(ct);
                }

                operation = new Operation(this, key, version);
                _operations.Add(key, operation);
            }

            // The owner runs detached from the caller's token, so joined callers keep a live operation.
            _ = ExecuteAsync(operation, action);
            return operation.Completion.Task;
        }

        /// <summary>
        /// Publishes the reservation, executes the owner once, and removes state before completing waiters.
        /// </summary>
        private async Task ExecuteAsync(Operation operation, Func<Operation, Task<bool>> action)
        {
            var result = false;
            Exception? failure = null;

            // Announce the new reservation, then run the update pipeline.
            try
            {
                RaiseStateChanged();
                result = await action(operation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Preserve success, failure, and cancellation for every caller sharing this operation.
                failure = ex;
            }
            finally
            {
                // Release the target before completing waiters, so they observe a free target.
                lock (_sync)
                {
                    _operations.Remove(operation.Key);
                }

                RaiseStateChanged();
            }

            // Complete the shared task with the recorded outcome.
            if (failure is OperationCanceledException canceled)
            {
                operation.Completion.TrySetCanceled(canceled.CancellationToken);
            }
            else if (failure != null)
            {
                operation.Completion.TrySetException(failure);
            }
            else
            {
                operation.Completion.TrySetResult(result);
            }
        }


        // Phase and progress

        /// <summary>
        /// Changes the stage synchronously so queued download callbacks cannot overwrite application state.
        /// </summary>
        private void SetPhase(Operation operation, UpdateOperationPhase phase)
        {
            lock (_sync)
            {
                if (!IsCurrentOperation(operation) || operation.Snapshot.Phase == phase)
                {
                    return;
                }

                operation.Snapshot = operation.Snapshot with { Phase = phase, DownloadFraction = null };
            }

            RaiseStateChanged();
        }

        /// <summary>
        /// Retains every download sample while limiting listener notifications to ten per second.
        /// </summary>
        private void ReportProgress(Operation operation, DownloadProgress progress)
        {
            lock (_sync)
            {
                // Ignore callbacks from a finished operation or from the transfer after file replacement began.
                if (!IsCurrentOperation(operation) || operation.Snapshot.Phase != UpdateOperationPhase.Downloading)
                {
                    return;
                }

                // Store the sample unconditionally; an unknown total size leaves the fraction indeterminate.
                double? fraction = progress.TotalBytes > 0 && double.IsFinite(progress.Fraction)
                    ? Math.Clamp(progress.Fraction, 0, 1) : null;
                operation.Snapshot = operation.Snapshot with { DownloadFraction = fraction };

                // Throttle notifications, but always publish the final 100% sample.
                var now = Environment.TickCount64;
                if (fraction != 1 && now - operation.LastProgressNotification < ProgressNotificationIntervalMilliseconds)
                {
                    return;
                }

                operation.LastProgressNotification = now;
            }

            RaiseStateChanged();
        }

        /// <summary>
        /// Checks ownership under the state lock, rejecting late samples from an earlier reservation.
        /// </summary>
        private bool IsCurrentOperation(Operation operation)
        {
            return _operations.TryGetValue(operation.Key, out var current) && ReferenceEquals(current, operation);
        }

        /// <summary>
        /// Publishes changes without allowing a failing or detached UI listener to abort the updater.
        /// </summary>
        private void RaiseStateChanged()
        {
            foreach (EventHandler handler in StateChanged?.GetInvocationList() ?? [])
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }
            }
        }


        // Operation context

        /// <summary>
        /// Owns one target's task and allows its update pipeline to publish stages and transfer samples.
        /// </summary>
        internal sealed class Operation
        {
            // Fields and properties

            /// <summary>
            /// Tracker that owns this operation and serializes access to its state.
            /// </summary>
            private readonly UpdateOperationTracker _owner;

            /// <summary>
            /// Target key (application or engine) this operation is reserved for.
            /// </summary>
            internal string Key { get; }

            /// <summary>
            /// Task shared by the owner and every caller that joined this operation; carries the update outcome.
            /// </summary>
            internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>
            /// Latest immutable state (stage, version, download fraction); written only under the tracker lock.
            /// </summary>
            internal UpdateOperationSnapshot Snapshot { get; set; }

            /// <summary>
            /// Tick count of the last progress notification, used to throttle listener updates.
            /// </summary>
            internal long LastProgressNotification { get; set; }


            // Initialization

            /// <summary>
            /// Creates a reserved operation before any asynchronous update work starts.
            /// </summary>
            internal Operation(UpdateOperationTracker owner, string key, string version)
            {
                _owner = owner;
                Key = key;
                Snapshot = new(UpdateOperationPhase.Preparing, version);
            }


            // Pipeline reporting

            /// <summary>
            /// Publishes the update pipeline's current stage.
            /// </summary>
            internal void SetPhase(UpdateOperationPhase phase)
            {
                _owner.SetPhase(this, phase);
            }

            /// <summary>
            /// Captures progress synchronously before forwarding it to asynchronous notification or caller sinks.
            /// </summary>
            internal IProgress<DownloadProgress> WrapProgress(IProgress<DownloadProgress> inner)
            {
                return new DownloadProgressBridge(this, inner);
            }


            // Nested types

            /// <summary>
            /// Keeps shared progress ordered before the notification bridge posts callbacks to another thread.
            /// </summary>
            private sealed class DownloadProgressBridge : IProgress<DownloadProgress>
            {
                /// <summary>
                /// Operation whose shared snapshot receives every sample.
                /// </summary>
                private readonly Operation _operation;

                /// <summary>
                /// Caller or notification sink that receives the sample after it is recorded.
                /// </summary>
                private readonly IProgress<DownloadProgress> _inner;

                /// <summary>
                /// Creates a synchronous bridge for one reserved update operation.
                /// </summary>
                internal DownloadProgressBridge(Operation operation, IProgress<DownloadProgress> inner)
                {
                    _operation = operation;
                    _inner = inner;
                }

                /// <summary>
                /// Records the transfer sample before forwarding notification and caller progress.
                /// </summary>
                public void Report(DownloadProgress value)
                {
                    _operation._owner.ReportProgress(_operation, value);
                    _inner.Report(value);
                }
            }
        }
    }
}
