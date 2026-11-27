// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Owns one attempt's cancellation and serializes it with the start of irreversible work.
    /// </summary>
    internal sealed class OperationCancellation : IDisposable
    {
        /// <summary>
        /// Serializes cancellation, the commit boundary, and disposal.
        /// </summary>
        private readonly object _sync = new();

        /// <summary>
        /// Interrupts only the work performed by this attempt.
        /// </summary>
        private readonly CancellationTokenSource _source = new();

        /// <summary>
        /// Forwards the initiating caller's cancellation through the same boundary as the UI action.
        /// </summary>
        private readonly CancellationTokenRegistration _callerRegistration;

        /// <summary>
        /// Tracks cancellation callbacks so the source stays alive while they run.
        /// </summary>
        private Task _callbacks = Task.CompletedTask;

        /// <summary>
        /// Indicates that cancellation is closed because the attempt is committing or has ended.
        /// </summary>
        private bool _closed;

        /// <summary>
        /// Gets the token passed to the running operation.
        /// </summary>
        internal CancellationToken Token { get; }

        /// <summary>
        /// Gets whether this attempt can still accept a cancellation request.
        /// </summary>
        internal bool CanCancel
        {
            get { lock (_sync) return !_closed && !Token.IsCancellationRequested; }
        }

        /// <summary>
        /// Creates a cancellation source bound to the initiating caller for as long as cancellation is safe.
        /// </summary>
        internal OperationCancellation(CancellationToken caller)
        {
            Token = _source.Token;
            _callerRegistration = caller.UnsafeRegister(static state => ((OperationCancellation)state!).TryCancel(), this);
        }

        /// <summary>
        /// Accepts cancellation once and runs the callbacks off the requesting thread.
        /// </summary>
        internal bool TryCancel()
        {
            lock (_sync)
            {
                if (_closed || Token.IsCancellationRequested) return false;
                _callbacks = _source.CancelAsync();
                return true;
            }
        }

        /// <summary>
        /// Closes cancellation before committing, or throws if cancellation won the race.
        /// </summary>
        internal void BeginCommit()
        {
            lock (_sync)
            {
                Token.ThrowIfCancellationRequested();
                _closed = true;
            }
        }

        /// <summary>
        /// Releases the caller registration and the source once all cancellation callbacks have exited.
        /// </summary>
        public void Dispose()
        {
            Task callbacks;
            lock (_sync)
            {
                _closed = true;
                callbacks = _callbacks;
            }

            _callerRegistration.Dispose();
            if (callbacks.IsCompletedSuccessfully) _source.Dispose();
            else _ = DisposeAfterCallbacksAsync(callbacks);
        }

        /// <summary>
        /// Observes cancellation callback failures without surfacing them as operation failures.
        /// </summary>
        private async Task DisposeAfterCallbacksAsync(Task callbacks)
        {
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The cancellation request was already recorded and the attempt can finish cleanup.
                Debug.WriteLine(ex);
            }
            finally
            {
                _source.Dispose();
            }
        }
    }
}
