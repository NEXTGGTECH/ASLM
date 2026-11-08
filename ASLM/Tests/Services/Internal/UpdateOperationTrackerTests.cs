// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Covers shared automatic/manual update state, concurrent requests, and observer lifetime boundaries.
    /// </summary>
    public sealed class UpdateOperationTrackerTests
    {
        // Progress snapshots

        /// <summary>
        /// Verifies that a view opening during a transfer restores its latest progress, that the snapshot does not
        /// treat extraction as downloading, and that the observed phases follow the operation from preparing to
        /// finished.
        /// </summary>
        [Fact]
        public async Task Snapshots_keep_progress_without_observers_and_distinguish_applying_from_downloading()
        {
            // Arrange: a running operation held open by a signal, observed by a listener that records the phases.
            var tracker = new UpdateOperationTracker();
            var release = Signal();
            UpdateOperationTracker.Operation context = null!;
            var phases = new List<UpdateOperationPhase?>();
            EventHandler listener = (_, _) => phases.Add(tracker.GetSnapshot("app:aslm")?.Phase);
            tracker.StateChanged += listener;
            var task = tracker.RunAsync("app:aslm", "v2.0", async operation =>
            {
                context = operation;
                await release.Task;
                return true;
            });
            try
            {
                // The operation starts in the preparing phase; switch it to downloading.
                var initial = tracker.GetSnapshot("app:aslm")!;
                initial.Phase.Should().Be(UpdateOperationPhase.Preparing);
                context.SetPhase(UpdateOperationPhase.Downloading);
                var sink = new RecordingProgress();
                var progress = context.WrapProgress(sink);

                // Closing settings detaches observers, but samples must still be available on reopening.
                tracker.StateChanged -= listener;
                progress.Report(new(0.35, 35, 100));
                progress.Report(new(0.7, 70, 100));
                tracker.GetSnapshot("app:aslm").Should().Be(new UpdateOperationSnapshot(
                    UpdateOperationPhase.Downloading, "v2.0", 0.7));
                initial.Phase.Should().Be(UpdateOperationPhase.Preparing);
                initial.DownloadFraction.Should().BeNull();
                sink.Samples.Should().HaveCount(2);

                // Extraction/application keeps the reservation, without displaying stale network percentages.
                tracker.StateChanged += listener;
                context.SetPhase(UpdateOperationPhase.Applying);
                progress.Report(new(1, 100, 100));
                tracker.GetSnapshot("app:aslm")!.Phase.Should().Be(UpdateOperationPhase.Applying);
                tracker.GetSnapshot("app:aslm")!.DownloadFraction.Should().BeNull();
                task.IsCompleted.Should().BeFalse();

                // Finishing the operation releases the reservation; the listener saw every phase in order.
                release.SetResult();
                (await task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
                tracker.GetSnapshot("app:aslm").Should().BeNull();
                phases.Should().ContainInOrder(UpdateOperationPhase.Preparing, UpdateOperationPhase.Downloading,
                    UpdateOperationPhase.Applying, null);
            }
            finally
            {
                release.TrySetResult();
            }
        }


        // Shared operations

        /// <summary>
        /// Verifies that settings and the scheduler cannot execute a second update pipeline for the same target: a
        /// second request (ignoring key case) joins the running operation and both callers get its result.
        /// </summary>
        [Fact]
        public async Task Concurrent_requests_for_the_same_target_join_one_operation()
        {
            // Arrange: an owner operation that stays running until released.
            var tracker = new UpdateOperationTracker();
            var release = Signal();
            var executions = 0;
            var owner = tracker.RunAsync("engine:ollama-service", "v2.0", async _ =>
            {
                executions++;
                await release.Task;
                return true;
            });
            try
            {
                // A second request for the same target does not execute its own pipeline.
                var duplicate = tracker.RunAsync("ENGINE:OLLAMA-SERVICE", "v2.0", _ =>
                    throw new InvalidOperationException("Duplicate update executed"));
                executions.Should().Be(1);
                owner.IsCompleted.Should().BeFalse();
                duplicate.IsCompleted.Should().BeFalse();

                // Both callers complete successfully together and the reservation is released.
                release.SetResult();
                var results = await Task.WhenAll(owner, duplicate).WaitAsync(TimeSpan.FromSeconds(5));
                results.Should().OnlyContain(success => success);
                tracker.GetSnapshot("engine:ollama-service").Should().BeNull();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>
        /// Verifies that canceling a joining caller does not cancel an automatic operation that another caller owns.
        /// </summary>
        [Fact]
        public async Task Canceling_a_waiter_does_not_cancel_the_owner()
        {
            // Arrange: an owner operation that stays running until released.
            var tracker = new UpdateOperationTracker();
            var release = Signal();
            using var waiterCancellation = new CancellationTokenSource();
            var owner = tracker.RunAsync("app:aslm", "v2.0", async _ =>
            {
                await release.Task;
                return true;
            });
            try
            {
                // A second caller joins the owner and then cancels: only that caller is canceled.
                var waiter = tracker.RunAsync("app:aslm", "v2.0", _ => throw new InvalidOperationException(),
                    waiterCancellation.Token);
                waiterCancellation.Cancel();
                var wait = () => waiter.WaitAsync(TimeSpan.FromSeconds(5));
                await wait.Should().ThrowAsync<OperationCanceledException>();
                owner.IsCompleted.Should().BeFalse();
                tracker.GetSnapshot("app:aslm").Should().NotBeNull();

                // The owner still completes successfully.
                release.SetResult();
                (await owner.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>
        /// Verifies that ASLM and Ollama progress and reservations are isolated while both targets are active, and
        /// that finishing one target leaves the other running.
        /// </summary>
        [Fact]
        public async Task Different_targets_keep_independent_state_and_completion()
        {
            // Arrange: an app operation that is downloading and an engine operation that is applying.
            var tracker = new UpdateOperationTracker();
            var appRelease = Signal();
            var engineRelease = Signal();
            var app = tracker.RunAsync("app:aslm", "v2.0", async operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Downloading);
                operation.WrapProgress(new RecordingProgress()).Report(new(0.25, 25, 100));
                await appRelease.Task;
                return true;
            });
            var engine = tracker.RunAsync("engine:ollama-service", "v3.0", async operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Applying);
                await engineRelease.Task;
                return true;
            });
            try
            {
                // Each target reports its own state.
                tracker.GetSnapshot("app:aslm")!.DownloadFraction.Should().Be(0.25);
                tracker.GetSnapshot("engine:ollama-service")!.Phase.Should().Be(UpdateOperationPhase.Applying);

                // Finishing the engine operation releases only the engine target.
                engineRelease.SetResult();
                await engine.WaitAsync(TimeSpan.FromSeconds(5));
                app.IsCompleted.Should().BeFalse();
                tracker.GetSnapshot("engine:ollama-service").Should().BeNull();
                tracker.GetSnapshot("app:aslm").Should().NotBeNull();
                appRelease.SetResult();
                await app.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                appRelease.TrySetResult();
                engineRelease.TrySetResult();
            }
        }


        // Completion and failure

        /// <summary>
        /// Verifies that a failing or canceled owner operation releases the target before callers resume, fails the
        /// joined caller the same way, and allows a later retry to succeed.
        /// </summary>
        /// <param name="cancel">True to cancel the owner, false to let it throw.</param>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Failure_or_owner_cancellation_clears_state_and_allows_retry(bool cancel)
        {
            // Arrange: an owner that fails once released or is canceled while it waits, plus a joined caller.
            var tracker = new UpdateOperationTracker();
            var release = Signal();
            using var cancellation = new CancellationTokenSource();
            var owner = tracker.RunAsync("app:aslm", "v2.0", async _ =>
            {
                await release.Task.WaitAsync(cancellation.Token);
                throw new InvalidOperationException("Expected failure");
            }, cancellation.Token);
            try
            {
                // End the owner by cancellation or by failure.
                var waiter = tracker.RunAsync("app:aslm", "v2.0", _ => throw new InvalidOperationException());
                if (cancel) cancellation.Cancel();
                else release.SetResult();

                // Both callers fail with the matching error, and the target is free for a successful retry.
                var wait = () => Task.WhenAll(owner, waiter).WaitAsync(TimeSpan.FromSeconds(5));
                if (cancel) await wait.Should().ThrowAsync<OperationCanceledException>();
                else await wait.Should().ThrowAsync<InvalidOperationException>().WithMessage("Expected failure");
                tracker.GetSnapshot("app:aslm").Should().BeNull();
                (await tracker.RunAsync("app:aslm", "v2.0", _ => Task.FromResult(true))).Should().BeTrue();
                tracker.GetSnapshot("app:aslm").Should().BeNull();
            }
            finally
            {
                release.TrySetResult();
            }
        }

        /// <summary>
        /// Verifies that an operation reporting a false result also releases the reservation, so settings are not
        /// left permanently busy and a later run can succeed.
        /// </summary>
        [Fact]
        public async Task Unsuccessful_result_does_not_leave_an_active_snapshot()
        {
            var tracker = new UpdateOperationTracker();
            (await tracker.RunAsync("app:aslm", "v2.0", _ => Task.FromResult(false))).Should().BeFalse();
            tracker.GetSnapshot("app:aslm").Should().BeNull();
            (await tracker.RunAsync("app:aslm", "v2.0", _ => Task.FromResult(true))).Should().BeTrue();
        }


        // Progress and observer safety

        /// <summary>
        /// Verifies that a transfer without a known total length does not expose a fabricated percentage in the
        /// snapshot.
        /// </summary>
        [Fact]
        public async Task Unknown_transfer_length_keeps_percentage_unavailable()
        {
            var tracker = new UpdateOperationTracker();
            await tracker.RunAsync("app:aslm", "v2.0", operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Downloading);
                operation.WrapProgress(new RecordingProgress()).Report(new(0.5, 50, 0));
                tracker.GetSnapshot("app:aslm")!.DownloadFraction.Should().BeNull();
                return Task.FromResult(true);
            });
        }

        /// <summary>
        /// Verifies that late progress callbacks from a finished transfer cannot overwrite a newly started update for
        /// the same target.
        /// </summary>
        [Fact]
        public async Task Finished_operations_cannot_publish_progress_into_a_new_reservation()
        {
            // Arrange: a first operation finishes and leaves its progress callback behind.
            var tracker = new UpdateOperationTracker();
            IProgress<DownloadProgress> oldProgress = null!;
            await tracker.RunAsync("app:aslm", "v2.0", operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Downloading);
                oldProgress = operation.WrapProgress(new RecordingProgress());
                return Task.FromResult(true);
            });

            // Reusing the target key is legitimate, but the old context no longer owns it.
            await tracker.RunAsync("app:aslm", "v3.0", operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Downloading);
                operation.WrapProgress(new RecordingProgress()).Report(new(0.25, 25, 100));
                oldProgress.Report(new(1, 100, 100));
                tracker.GetSnapshot("app:aslm").Should().Be(new UpdateOperationSnapshot(
                    UpdateOperationPhase.Downloading, "v3.0", 0.25));
                return Task.FromResult(true);
            });
        }

        /// <summary>
        /// Verifies that a disposed or failing view cannot interrupt update work or suppress the notifications to
        /// other views; the working observer receives one notification per state change.
        /// </summary>
        [Fact]
        public async Task Failing_observers_do_not_interrupt_operations_or_other_observers()
        {
            var tracker = new UpdateOperationTracker();
            var notifications = 0;
            tracker.StateChanged += (_, _) => throw new InvalidOperationException("Detached view");
            tracker.StateChanged += (_, _) => notifications++;
            (await tracker.RunAsync("app:aslm", "v2.0", operation =>
            {
                operation.SetPhase(UpdateOperationPhase.Applying);
                return Task.FromResult(true);
            })).Should().BeTrue();
            notifications.Should().Be(3);
            tracker.GetSnapshot("app:aslm").Should().BeNull();
        }


        // Test helpers

        /// <summary>
        /// Creates an asynchronous gate without sleeps or platform dispatcher dependencies.
        /// </summary>
        private static TaskCompletionSource Signal()
        {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>
        /// Records forwarded transfer samples independently of shared update snapshots.
        /// </summary>
        private sealed class RecordingProgress : IProgress<DownloadProgress>
        {
            /// <summary>
            /// All samples reported so far, in the order they arrived.
            /// </summary>
            internal List<DownloadProgress> Samples { get; } = [];

            /// <summary>
            /// Retains one sample to verify that tracking preserves existing progress forwarding.
            /// </summary>
            public void Report(DownloadProgress value)
            {
                Samples.Add(value);
            }
        }
    }
}
