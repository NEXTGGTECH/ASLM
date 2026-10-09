// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services;

public sealed class DownloadQueueTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Jobs_in_one_lane_share_fifo_order_and_repeated_keys_share_one_job()
    {
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
            duplicate.Should().BeSameAs(second);
            queue.Contains("module:first").Should().BeTrue();
            queue.Contains("resource:second").Should().BeTrue();
            second.IsCompleted.Should().BeFalse();
            third.IsCompleted.Should().BeFalse();

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

    [Fact]
    public async Task Canceling_a_waiting_job_is_immediate_but_does_not_let_later_jobs_overtake_the_active_one()
    {
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
            var canceled = queue.EnqueueAsync<int>("canceled", _ => throw new InvalidOperationException("Canceled job executed"), cts.Token);
            var third = queue.EnqueueAsync("third", _ =>
            {
                order.Add("third");
                return Task.FromResult(3);
            });
            cts.Cancel();
            var waitForCanceled = () => canceled.WaitAsync(TimeSpan.FromSeconds(5));
            await waitForCanceled.Should().ThrowAsync<OperationCanceledException>();
            queue.Contains("canceled").Should().BeFalse();
            first.IsCompleted.Should().BeFalse();
            third.IsCompleted.Should().BeFalse();
            var replacement = queue.EnqueueAsync("canceled", _ =>
            {
                order.Add("replacement");
                return Task.FromResult(4);
            });
            release.SetResult();
            await Task.WhenAll(first, third, replacement).WaitAsync(TimeSpan.FromSeconds(5));
            order.Should().Equal("first", "third", "replacement");
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_of_the_active_job_does_not_block_following_jobs(bool cancel)
    {
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
            var next = queue.EnqueueAsync("next", _ => Task.FromResult(2));
            if (cancel) cts.Cancel();
            else fail.SetResult();
            var wait = () => first.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel) await wait.Should().ThrowAsync<OperationCanceledException>();
            else await wait.Should().ThrowAsync<InvalidOperationException>();
            (await next.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
            queue.Contains("first").Should().BeFalse();
            queue.Contains("next").Should().BeFalse();
        }
        finally { fail.TrySetResult(); }
    }

    [Fact]
    public async Task All_three_lanes_run_independently_and_preserve_their_own_fifo_order()
    {
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
            await Task.WhenAll(started.Values.Select(signal => signal.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            queue.GetState("ModuleRemoval:first").Should().Be(DownloadOperationState.Removing);
            var following = releases.Keys.ToDictionary(kind => kind,
                kind => queue.EnqueueAsync(kind + ":second", _ => Task.FromResult(2), kind: kind));
            following.Values.Should().OnlyContain(task => !task.IsCompleted);
            releases[DownloadQueueKind.ModuleRemoval].SetResult();
            await following[DownloadQueueKind.ModuleRemoval].WaitAsync(TimeSpan.FromSeconds(5));
            jobs[DownloadQueueKind.Modules].IsCompleted.Should().BeFalse();
            jobs[DownloadQueueKind.Resources].IsCompleted.Should().BeFalse();
            foreach (var signal in releases.Values) signal.TrySetResult();
            await Task.WhenAll(jobs.Values.Concat(following.Values)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { foreach (var signal in releases.Values) signal.TrySetResult(); }
    }

    [Fact]
    public async Task Waiting_job_prepares_immediately_and_cancellation_finishes_cleanup_before_the_next_install()
    {
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
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queue.GetState("canceled").Should().Be(DownloadOperationState.Queued);
            queue.Cancel("canceled");
            release.SetResult();
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            last.IsCompleted.Should().BeFalse();
            cleanupRelease.SetResult();
            await FluentActions.Awaiting(() => canceled).Should().ThrowAsync<OperationCanceledException>();
            (await last.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(3);
            queue.Contains("canceled").Should().BeFalse();
            await first;
        }
        finally { release.TrySetResult(); cleanupRelease.TrySetResult(); }
    }
}
