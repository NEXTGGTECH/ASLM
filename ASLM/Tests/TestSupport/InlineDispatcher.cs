// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Tests.TestSupport;

/// <summary>
/// Runs every "UI thread" notification mutation inline on the calling thread so
/// <see cref="NotificationCenter"/> behavior is testable without a MAUI application.
/// </summary>
internal sealed class InlineDispatcher : NotificationCenter.Dispatcher
{
    /// <inheritdoc />
    public override bool IsMainThread => true;

    /// <inheritdoc />
    public override void BeginInvoke(Action action) => action();

    /// <inheritdoc />
    public override Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<T> InvokeAsync<T>(Func<T> work) => Task.FromResult(work());
}
