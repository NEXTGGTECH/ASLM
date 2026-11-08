// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Tests.TestSupport
{
    /// <summary>
    /// Runs every "UI thread" notification mutation inline on the calling thread so
    /// <see cref="NotificationCenter"/> behavior is testable without a MAUI application.
    /// </summary>
    internal sealed class InlineDispatcher : NotificationCenter.Dispatcher
    {
        /// <summary>
        /// Always reports the UI thread, so the notification center never takes its marshaling path.
        /// </summary>
        public override bool IsMainThread => true;

        /// <summary>
        /// Runs the action immediately on the calling thread instead of queueing it.
        /// </summary>
        public override void BeginInvoke(Action action) => action();

        /// <summary>
        /// Runs the action synchronously and returns an already completed task.
        /// </summary>
        public override Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs the function synchronously and returns its result in an already completed task.
        /// </summary>
        public override Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
    }
}
