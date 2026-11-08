// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the lane ordering, key de-duplication, cancellation and failure handling of
    /// <see cref="DownloadQueue"/>.
    /// </summary>
    public sealed class DownloadQueueTests
    {
        // Helpers

        /// <summary>
        /// Creates a manually completed signal whose continuations run asynchronously, so a test can hold a queue job
        /// at a known point and release it later.
        /// </summary>
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);


        // Ordering within a lane

        /// <summary>
        /// Verifies that jobs of one lane run strictly one after another in the order they were queued, that a job
        /// queued again under the same key (ignoring case) returns the already pending job instead of running twice,
        /// and that finished jobs leave the queue.
        /// </summary>
        [Fact]
        public async Task Jobs_in_one_lane_share_fifo_order_and_repeated_keys_share_one_job()
        {
            // Arrange: the first job starts and then stays blocked until it is released.
            var queue = new DownloadQueue();
            var started = Signal();
            var release = Signal();
            var order = new List<string>();
            var first = queue.EnqueueAsync("module:first", async _ =>
            {
                order.Add("first-start");
                started.SetResult();
                await release.Task;
                order.Add("first-end");
                return 1;
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Queue two more jobs behind the active one, plus a duplicate of the second job's key.
                var second = queue.EnqueueAsync("resource:second", _ =>
                {
                    order.Add("second");
                    return Task.FromResult("resource result");
                });
                var duplicate = queue.EnqueueAsync<string>("RESOURCE:SECOND", _ => throw new InvalidOperationException("Duplicate executed"));
                var third = queue.EnqueueAsync("module:third", _ =>
                {
                    order.Add("third");
                    return Task.FromResult(3);
                });

                // While the first job is blocked: the duplicate is the second job itself and nobody overtakes.
                duplicate.Should().BeSameAs(second);
                queue.Contains("module:first").Should().BeTrue();
                queue.Contains("resource:second").Should().BeTrue();
                second.IsCompleted.Should().BeFalse();
                third.IsCompleted.Should().BeFalse();

                // After the release: jobs run in queue order, return their results and are removed from the queue.
                release.SetResult();
                await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
                order.Should().Equal("first-start", "first-end", "second", "third");
                (await first).Should().Be(1);
                (await second).Should().Be("resource result");
                queue.Contains("module:first").Should().BeFalse();
                queue.Contains("resource:second").Should().BeFalse();
            }
            finally { release.TrySetResult(); }
        }

        /// <summary>
        /// Verifies that canceling a waiting job completes it as canceled right away without ever running it, that
        /// later jobs still wait for the active job, and that the canceled key can be queued again afterwards.
        /// </summary>
        [Fact]
        public async Task Canceling_a_waiting_job_is_immediate_but_does_not_let_later_jobs_overtake_the_active_one()
        {
            // Arrange: the first job is active and blocked.
            var queue = new DownloadQueue();
            var started = Signal();
            var release = Signal();
            using var cts = new CancellationTokenSource();
            var order = new List<string>();
            var first = queue.EnqueueAsync("first", async _ =>
            {
                started.SetResult();
                await release.Task;
                order.Add("first");
                return 1;
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Queue a job that must never run, then another job behind it, and cancel the first of the two.
                var canceled = queue.EnqueueAsync<int>("canceled", _ => throw new InvalidOperationException("Canceled job executed"), cts.Token);
                var third = queue.EnqueueAsync("third", _ =>
                {
                    order.Add("third");
                    return Task.FromResult(3);
                });
                cts.Cancel();
                var waitForCanceled = () => canceled.WaitAsync(TimeSpan.FromSeconds(5));
                await waitForCanceled.Should().ThrowAsync<OperationCanceledException>();

                // The canceled job is gone at once, while the active job and the later job are still waiting.
                queue.Contains("canceled").Should().BeFalse();
                first.IsCompleted.Should().BeFalse();
                third.IsCompleted.Should().BeFalse();

                // The freed key is accepted again and queues behind the later job.
                var replacement = queue.EnqueueAsync("canceled", _ =>
                {
                    order.Add("replacement");
                    return Task.FromResult(4);
                });

                // Releasing the active job lets the rest run in the order they were queued.
                release.SetResult();
                await Task.WhenAll(first, third, replacement).WaitAsync(TimeSpan.FromSeconds(5));
                order.Should().Equal("first", "third", "replacement");
            }
            finally { release.TrySetResult(); }
        }


        // Failure, lanes and cleanup

        /// <summary>
        /// Verifies that an active job that fails or is canceled still lets the next queued job run and complete, and
        /// that both jobs leave the queue.
        /// </summary>
        /// <param name="cancel">True to cancel the active job, false to make it throw.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Failure_or_cancellation_of_the_active_job_does_not_block_following_jobs(bool cancel)
        {
            // Arrange: an active job that waits for a failure signal or for cancellation.
            var queue = new DownloadQueue();
            var started = Signal();
            var fail = Signal();
            using var cts = new CancellationTokenSource();
            var first = queue.EnqueueAsync<int>("first", async token =>
            {
                started.SetResult();
                await fail.Task.WaitAsync(token);
                throw new InvalidOperationException("Expected failure");
            }, cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Act: queue a follower, then end the active job by cancellation or by failure.
                var next = queue.EnqueueAsync("next", _ => Task.FromResult(2));
                if (cancel) cts.Cancel();
                else fail.SetResult();

                // Assert: the active job reports the matching error, the follower still completes, the queue is empty.
                var wait = () => first.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancel) await wait.Should().ThrowAsync<OperationCanceledException>();
                else await wait.Should().ThrowAsync<InvalidOperationException>();
                (await next.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
                queue.Contains("first").Should().BeFalse();
                queue.Contains("next").Should().BeFalse();
            }
            finally { fail.TrySetResult(); }
        }

        /// <summary>
        /// Verifies that the resource, module and module-removal lanes run independently of each other, that a removal
        /// job reports the removing state, and that a second job of each lane waits behind the first job of its own
        /// lane only.
        /// </summary>
        [Fact]
        public async Task All_three_lanes_run_independently_and_preserve_their_own_fifo_order()
        {
            // Arrange: one blocked job per lane, all started at the same time.
            var queue = new DownloadQueue();
            var releases = Enum.GetValues<DownloadQueueKind>().ToDictionary(kind => kind, _ => Signal());
            var started = releases.Keys.ToDictionary(kind => kind, _ => Signal());
            var jobs = releases.Keys.ToDictionary(kind => kind, kind => queue.EnqueueAsync(kind + ":first", async _ =>
            {
                started[kind].SetResult();
                await releases[kind].Task;
                return 1;
            }, kind: kind, removal: kind == DownloadQueueKind.ModuleRemoval));
            try
            {
                // Every lane runs its first job at the same time; the removal lane reports the removing state.
                await Task.WhenAll(started.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
                queue.GetState("ModuleRemoval:first").Should().Be(DownloadOperationState.Removing);

                // A second job in each lane waits behind the first job of its own lane.
                var following = releases.Keys.ToDictionary(kind => kind,
                    kind => queue.EnqueueAsync(kind + ":second", _ => Task.FromResult(2), kind: kind));
                following.Values.Should().OnlyContain(task => !task.IsCompleted);

                // Releasing only the removal lane advances that lane and leaves the other lanes' first jobs running.
                releases[DownloadQueueKind.ModuleRemoval].SetResult();
                await following[DownloadQueueKind.ModuleRemoval].WaitAsync(TimeSpan.FromSeconds(5));
                jobs[DownloadQueueKind.Modules].IsCompleted.Should().BeFalse();
                jobs[DownloadQueueKind.Resources].IsCompleted.Should().BeFalse();

                // Releasing everything finishes all jobs.
                foreach (var signal in releases.Values) signal.TrySetResult();
                await Task.WhenAll(jobs.Values.Concat(following.Values)).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { foreach (var signal in releases.Values) signal.TrySetResult(); }
        }

        /// <summary>
        /// Verifies that a waiting job runs its prepare step immediately and still reports the queued state, and that
        /// canceling it runs its finish callback (told that it was canceled) before the next job of the lane starts.
        /// </summary>
        [Fact]
        public async Task Waiting_job_prepares_immediately_and_cancellation_finishes_cleanup_before_the_next_install()
        {
            // Arrange: an active module job, a waiting job with prepare and finish callbacks, and a last job.
            var queue = new DownloadQueue();
            var release = Signal();
            var prepared = Signal();
            var cleanupStarted = Signal();
            var cleanupRelease = Signal();
            var first = queue.EnqueueAsync("first", async _ => { await release.Task; return 1; }, kind: DownloadQueueKind.Modules);
            var canceled = queue.EnqueueAsync<int>("canceled", _ => throw new InvalidOperationException("Must not execute"),
                kind: DownloadQueueKind.Modules,
                prepare: _ => { prepared.SetResult(); return Task.CompletedTask; },
                finish: async wasCanceled =>
                {
                    wasCanceled.Should().BeTrue();
                    cleanupStarted.SetResult();
                    await cleanupRelease.Task;
                });
            var last = queue.EnqueueAsync("last", _ => Task.FromResult(3), kind: DownloadQueueKind.Modules);
            try
            {
                // The waiting job has already prepared although it is still queued.
                await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
                queue.GetState("canceled").Should().Be(DownloadOperationState.Queued);

                // Cancel it and let the active job finish: the cleanup starts, and the last job keeps waiting for it.
                queue.Cancel("canceled");
                release.SetResult();
                await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                last.IsCompleted.Should().BeFalse();

                // Once the cleanup is done the canceled job ends as canceled and the last job runs.
                cleanupRelease.SetResult();
                await FluentActions.Awaiting(() => canceled).Should().ThrowAsync<OperationCanceledException>();
                (await last.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(3);
                queue.Contains("canceled").Should().BeFalse();
                await first;
            }
            finally { release.TrySetResult(); cleanupRelease.TrySetResult(); }
        }
    }
}
