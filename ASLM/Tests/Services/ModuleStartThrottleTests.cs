// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class ModuleStartThrottleTests
{
    [Fact]
    public async Task WaitAsync_limits_concurrent_acquires_to_default_max()
    {
        var throttle = new ModuleStartThrottle();
        var acquired = 0;
        var maxObserved = 0;
        var gate = new object();

        var workers = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            await throttle.WaitAsync();
            try
            {
                lock (gate)
                {
                    acquired++;
                    maxObserved = Math.Max(maxObserved, acquired);
                }

                await Task.Delay(50);
            }
            finally
            {
                lock (gate)
                {
                    acquired--;
                }

                throttle.Release();
            }
        })).ToArray();

        await Task.WhenAll(workers);

        maxObserved.Should().BeLessThanOrEqualTo(ModuleStartThrottle.DefaultMaxConcurrentStarts);
    }

    [Fact]
    public async Task WaitAsync_respects_cancellation()
    {
        var throttle = new ModuleStartThrottle();
        await throttle.WaitAsync();
        await throttle.WaitAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await throttle.WaitAsync(cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();

        throttle.Release();
        throttle.Release();
    }
}
