// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies session-owned work, foreground promotion, and invalidation across module maintenance.
    /// </summary>
    public sealed class DataPreparationQueueTests
    {
        // Request ownership

        /// <summary>
        /// Verifies closing a consumer neither cancels its producer nor repeats its completed work.
        /// </summary>
        [Fact]
        public async Task Detached_consumer_does_not_cancel_or_repeat_the_load()
        {
            await using var queue = new DataPreparationQueue();
            var started = Signal();
            var release = Signal();
            var calls = 0;
            using var page = new CancellationTokenSource();
            var first = queue.GetAsync("module", "setting:test", async token =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task.WaitAsync(token);
                return 42;
            }, page.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            page.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

            // Reopening joins the original producer even before it finishes.
            var second = queue.GetAsync("module", "setting:test", _ => Task.FromResult(-1));
            release.SetResult();
            (await second.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
            (await queue.GetAsync("module", "setting:test", _ => Task.FromResult(-2))).Should().Be(42);
            calls.Should().Be(1);
        }

        /// <summary>
        /// Verifies a visible consumer starts queued work while an unrelated speculative operation is still pending.
        /// </summary>
        [Fact]
        public async Task Foreground_takes_pending_work_without_waiting_for_the_worker()
        {
            await using var queue = new DataPreparationQueue();
            var started = Signal();
            var release = Signal();
            var queued = Signal();
            var calls = 0;
            var first = queue.PrepareAsync(async () => await queue.GetAsync("slow", "list", async token =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return 0;
            }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = queue.PrepareAsync(async () =>
            {
                var result = queue.GetAsync("visible", "list", _ => Task.FromResult(Interlocked.Increment(ref calls)));
                queued.SetResult();
                await result;
            });
            await queued.Task;
            try
            {
                (await queue.GetAsync("visible", "list", _ => Task.FromResult(-1))
                    .WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(1);
                first.IsCompleted.Should().BeFalse();
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.TrySetResult(); }
            await first;
            calls.Should().Be(1);
        }

        /// <summary>
        /// Verifies speculative calls from different producers never execute simultaneously.
        /// </summary>
        [Fact]
        public async Task Speculative_producers_share_one_execution_lane()
        {
            await using var queue = new DataPreparationQueue();
            var started = Signal();
            var release = Signal();
            var secondStarted = false;
            var first = queue.PrepareAsync(async () => await queue.GetAsync("one", "list", async token =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return 1;
            }));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = queue.PrepareAsync(async () => await queue.GetAsync("two", "list", _ =>
            {
                secondStarted = true;
                return Task.FromResult(2);
            }));
            secondStarted.Should().BeFalse();
            release.SetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            secondStarted.Should().BeTrue();
        }

        /// <summary>
        /// Verifies failed requests remain retryable without suppressing the next speculative operation.
        /// </summary>
        [Fact]
        public async Task Failure_releases_the_lane_and_is_not_cached()
        {
            await using var queue = new DataPreparationQueue();
            await Assert.ThrowsAsync<IOException>(() => queue.PrepareAsync(async () =>
                await queue.GetAsync<int>("one", "list", _ => throw new IOException("unavailable"))));
            (await queue.GetAsync("one", "list", _ => Task.FromResult(7))).Should().Be(7);
            await queue.PrepareAsync(async () =>
                (await queue.GetAsync("two", "list", _ => Task.FromResult(8))).Should().Be(8));
        }


        /// <summary>
        /// Verifies opening queued settings moves all remaining reads ahead without restarting the active operation.
        /// </summary>
        [Fact]
        public async Task Settings_priority_preserves_active_work_and_promotes_remaining_section_steps()
        {
            await using var queue = new DataPreparationQueue();
            var started = Signal();
            var release = Signal();
            var order = new List<string>();
            var active = queue.PrepareAsync(async () => await queue.GetAsync("one", "setting:active", async token =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                order.Add("active");
                return 0;
            }));
            await started.Task;
            var other = queue.PrepareAsync(async () => await queue.GetAsync("one", "setting:later", _ =>
            {
                order.Add("other");
                return Task.FromResult(1);
            }));
            var selected = queue.PrepareAsync(async () =>
            {
                var first = queue.GetAsync("two", "setting:first", _ =>
                {
                    order.Add("selected first");
                    return Task.FromResult(2);
                });
                var second = queue.GetAsync("two", "setting:second", _ =>
                {
                    order.Add("selected second");
                    return Task.FromResult(3);
                });
                await Task.WhenAll(first, second);
            });
            queue.PrioritizeSettings("two");
            release.SetResult();
            await Task.WhenAll(active, other, selected).WaitAsync(TimeSpan.FromSeconds(5));

            order.Should().Equal("active", "selected first", "selected second", "other");
        }


        // Maintenance

        /// <summary>
        /// Verifies restarting a module invalidates its values while preserving metadata and other modules.
        /// </summary>
        [Fact]
        public async Task Values_only_mutation_preserves_bridge_data_and_other_modules()
        {
            await using var queue = new DataPreparationQueue();
            await queue.GetAsync("one", "setting:key", _ => Task.FromResult(1));
            await queue.GetAsync("one", "bridge:list", _ => Task.FromResult(2));
            await queue.GetAsync("two", "setting:key", _ => Task.FromResult(3));
            DataPreparationChangedEventArgs? changed = null;
            queue.Changed += (_, e) => changed = e;
            using (await queue.SuspendAsync("one", valuesOnly: true)) { }

            (await queue.GetAsync("one", "setting:key", _ => Task.FromResult(4))).Should().Be(4);
            (await queue.GetAsync("one", "bridge:list", _ => Task.FromResult(5))).Should().Be(2);
            (await queue.GetAsync("two", "setting:key", _ => Task.FromResult(6))).Should().Be(3);
            changed!.Full.Should().BeFalse();
        }

        /// <summary>
        /// Verifies a full update cancels obsolete work before replacing files and only publishes the new result.
        /// </summary>
        [Fact]
        public async Task Full_update_discards_running_old_results_and_waits_for_cleanup()
        {
            await using var queue = new DataPreparationQueue();
            var started = Signal();
            var cleaned = false;
            var obsolete = queue.GetAsync("module", "bridge:list", async token =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleaned = true; }
                return 1;
            });
            await started.Task;
            Task<int> current;
            using (await queue.SuspendAsync("module"))
            {
                cleaned.Should().BeTrue();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
                current = queue.GetAsync("module", "bridge:list", _ => Task.FromResult(2));
                current.IsCompleted.Should().BeFalse("the module is still being replaced");
            }
            (await current.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(2);
            (await queue.GetAsync("module", "bridge:list", _ => Task.FromResult(3))).Should().Be(2);
        }

        /// <summary>
        /// Verifies nested update and restart leases publish a single full invalidation after the outer mutation.
        /// </summary>
        [Fact]
        public async Task Nested_mutations_resume_only_after_the_outer_operation()
        {
            await using var queue = new DataPreparationQueue();
            var notifications = new List<bool>();
            queue.Changed += (_, e) => notifications.Add(e.Full);
            using (await queue.SuspendAsync("module"))
            {
                using (await queue.SuspendAsync("module", valuesOnly: true)) { }
                notifications.Should().BeEmpty();
            }
            notifications.Should().Equal(true);
        }

        /// <summary>
        /// Verifies restart cancels a complete value pass between getters while bridge preparation can continue.
        /// </summary>
        [Fact]
        public async Task Restart_cancels_value_generations_without_canceling_content_generations()
        {
            await using var queue = new DataPreparationQueue();
            var values = queue.GetModuleLifetime("module", valuesOnly: true);
            var content = queue.GetModuleLifetime("module");
            using (await queue.SuspendAsync("module", valuesOnly: true)) { }
            values.IsCancellationRequested.Should().BeTrue();
            content.IsCancellationRequested.Should().BeFalse();
            var nextValues = queue.GetModuleLifetime("module", valuesOnly: true);
            nextValues.IsCancellationRequested.Should().BeFalse();
            using (await queue.SuspendAsync("module")) { }
            content.IsCancellationRequested.Should().BeTrue();
            nextValues.IsCancellationRequested.Should().BeTrue();
        }

        /// <summary>
        /// Verifies shutdown cancels both a running command and speculative work that never started.
        /// </summary>
        [Fact]
        public async Task Shutdown_joins_running_and_queued_loads()
        {
            var queue = new DataPreparationQueue();
            var started = Signal();
            var running = queue.PrepareAsync(async () => await queue.GetAsync("one", "list", async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }));
            await started.Task;
            var pending = queue.PrepareAsync(async () => await queue.GetAsync("two", "list", _ => Task.FromResult(1)));
            await queue.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }


        // Schema changes

        /// <summary>
        /// Verifies schema replacement keeps compatible edits and drops removed or incompatible keys.
        /// </summary>
        [Fact]
        public void Schema_refresh_preserves_only_compatible_edits()
        {
            var module = ModuleConfigBuilder.Create();
            module.Settings =
            [
                new() { Key = "keep", Type = "string", Default = "old" },
                new() { Key = "removed", Type = "string", Default = "old" },
                new() { Key = "changed", Type = "string", Default = "old" }
            ];
            var session = new SettingsEditSession();
            session.ReplaceModules([module]);
            foreach (var draft in session.GetModule(module).Settings) draft.Value = "edited";
            var updated = ModuleConfigBuilder.Create();
            updated.Settings =
            [
                new() { Key = "keep", Type = "string", Default = "new" },
                new() { Key = "changed", Type = "bool", Default = false },
                new() { Key = "added", Type = "string", Default = "added" }
            ];
            session.UpdateModule(updated);
            var replacement = session.GetModule(updated);
            replacement.GetSetting("keep").Value.Should().Be("edited");
            replacement.GetSetting("keep").HasChanges.Should().BeTrue();
            replacement.GetSetting("changed").Value.Should().Be(false);
            replacement.Settings.Should().NotContain(draft => draft.Setting.Key == "removed");
            replacement.GetSetting("added").Value.Should().Be("added");
        }


        // Helpers

        /// <summary>
        /// Creates a deterministic asynchronous barrier without blocking a test thread.
        /// </summary>
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
