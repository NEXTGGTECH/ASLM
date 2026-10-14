// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers shared automatic/manual update state, concurrent requests, and observer lifetime boundaries.
/// </summary>
public sealed class UpdateOperationTrackerTests
{
    /// <summary>
    /// A view opening during a transfer restores its latest progress and does not treat extraction as downloading.
    /// </summary>
    [Fact]
    public async Task Snapshots_keep_progress_without_observers_and_distinguish_applying_from_downloading()
    {
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

    /// <summary>
    /// Settings and the scheduler cannot execute a second update pipeline for the same target.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_for_the_same_target_join_one_operation()
    {
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
            var duplicate = tracker.RunAsync("ENGINE:OLLAMA-SERVICE", "v2.0", _ =>
                throw new InvalidOperationException("Duplicate update executed"));
            executions.Should().Be(1);
            owner.IsCompleted.Should().BeFalse();
            duplicate.IsCompleted.Should().BeFalse();
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
    /// Canceling a joining caller does not cancel an automatic operation that another caller owns.
    /// </summary>
    [Fact]
    public async Task Canceling_a_waiter_does_not_cancel_the_owner()
    {
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
            var waiter = tracker.RunAsync("app:aslm", "v2.0", _ => throw new InvalidOperationException(),
                waiterCancellation.Token);
            waiterCancellation.Cancel();
            var wait = () => waiter.WaitAsync(TimeSpan.FromSeconds(5));
            await wait.Should().ThrowAsync<OperationCanceledException>();
            owner.IsCompleted.Should().BeFalse();
            tracker.GetSnapshot("app:aslm").Should().NotBeNull();
            release.SetResult();
            (await owner.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// ASLM and Ollama progress and reservations are isolated while both targets are active.
    /// </summary>
    [Fact]
    public async Task Different_targets_keep_independent_state_and_completion()
    {
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
            tracker.GetSnapshot("app:aslm")!.DownloadFraction.Should().Be(0.25);
            tracker.GetSnapshot("engine:ollama-service")!.Phase.Should().Be(UpdateOperationPhase.Applying);
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

    /// <summary>
    /// Failure and cancellation release the target before callers resume, allowing a later retry.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_owner_cancellation_clears_state_and_allows_retry(bool cancel)
    {
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
            var waiter = tracker.RunAsync("app:aslm", "v2.0", _ => throw new InvalidOperationException());
            if (cancel) cancellation.Cancel();
            else release.SetResult();

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
    /// A reported false result also releases the reservation and does not leave settings permanently busy.
    /// </summary>
    [Fact]
    public async Task Unsuccessful_result_does_not_leave_an_active_snapshot()
    {
        var tracker = new UpdateOperationTracker();
        (await tracker.RunAsync("app:aslm", "v2.0", _ => Task.FromResult(false))).Should().BeFalse();
        tracker.GetSnapshot("app:aslm").Should().BeNull();
        (await tracker.RunAsync("app:aslm", "v2.0", _ => Task.FromResult(true))).Should().BeTrue();
    }

    /// <summary>
    /// Transfers without a known length must not expose a fabricated percentage.
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
    /// Late callbacks from an earlier transfer cannot overwrite a newly started update for the same target.
    /// </summary>
    [Fact]
    public async Task Finished_operations_cannot_publish_progress_into_a_new_reservation()
    {
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
    /// A disposed or failing view cannot interrupt update work or suppress notifications to other views.
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
