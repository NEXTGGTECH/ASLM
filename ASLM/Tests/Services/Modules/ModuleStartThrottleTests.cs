// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the shared limit on concurrent module launches.
    /// </summary>
    public sealed class ModuleStartThrottleTests
    {
        /// <summary>
        /// Verifies that six workers contending for the throttle never hold more than the default number of slots
        /// at the same time.
        /// </summary>
        [Fact]
        public async Task WaitAsync_limits_concurrent_acquires_to_default_max()
        {
            // Arrange: shared counters track how many workers currently hold a slot.
            var throttle = new ModuleStartThrottle();
            var acquired = 0;
            var maxObserved = 0;
            var gate = new object();

            // Act: every worker takes a slot, records the peak concurrency, holds the slot briefly and releases it.
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

            // Assert: the observed peak never exceeded the throttle capacity.
            maxObserved.Should().BeLessThanOrEqualTo(ModuleStartThrottle.DefaultMaxConcurrentStarts);
        }

        /// <summary>
        /// Verifies that a wait on an exhausted throttle with an already canceled token throws instead of blocking.
        /// </summary>
        [Fact]
        public async Task WaitAsync_respects_cancellation()
        {
            // Exhaust both default slots so that the next wait would have to block.
            var throttle = new ModuleStartThrottle();
            await throttle.WaitAsync();
            await throttle.WaitAsync();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = async () => await throttle.WaitAsync(cts.Token);
            await act.Should().ThrowAsync<TaskCanceledException>();

            // Return the slots taken above.
            throttle.Release();
            throttle.Release();
        }
    }
}
