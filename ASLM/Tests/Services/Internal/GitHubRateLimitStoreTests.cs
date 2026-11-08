// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the GitHub quota planning store: window snapshots, the automatic-request budget,
    /// inter-check spacing, and exhaustion-notice reservations.
    /// </summary>
    public sealed class GitHubRateLimitStoreTests : IDisposable
    {
        // Fixture state

        /// <summary>
        /// Isolated ASLM root that holds the persisted rate-limit file of the test.
        /// </summary>
        private readonly UniqueAslmRoot _root = new("rate-limit");

        /// <summary>
        /// Path of the rate-limit file that every store created by the test reads and writes.
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Controllable clock handed to the stores. The store reads it only for the exhaustion-notice logic; the delay
        /// and budget calculations use the real system clock.
        /// </summary>
        private readonly FakeTimeProvider _clock = new();

        /// <summary>
        /// Points the rate-limit file into the isolated root.
        /// </summary>
        public GitHubRateLimitStoreTests()
        {
            _filePath = Path.Combine(_root.DataAppDir, "ASLM_GitHubRateLimit.json");
        }

        /// <summary>
        /// Removes the isolated root together with the rate-limit file.
        /// </summary>
        public void Dispose() => _root.Dispose();

        /// <summary>
        /// Creates a store bound to the test file and the fake clock; stores created from the same file share the
        /// persisted data.
        /// </summary>
        private GitHubRateLimitStore CreateStore() =>
            new(NullLogger<GitHubRateLimitStore>.Instance, _filePath, _clock);


        // Window snapshot

        /// <summary>
        /// Verifies that header values update the in-memory window snapshot and are written to the rate-limit file.
        /// </summary>
        [Fact]
        public void UpdateFromHeaders_persists_the_window_snapshot_to_disk()
        {
            var store = CreateStore();
            var reset = DateTimeOffset.UtcNow.AddHours(1);

            store.UpdateFromHeaders(60, 42, reset.ToUnixTimeSeconds());

            var snapshot = store.GetSnapshot();
            snapshot.Limit.Should().Be(60);
            snapshot.Remaining.Should().Be(42);
            File.Exists(_filePath).Should().BeTrue();
            File.ReadAllText(_filePath).Should().Contain("\"knownLimit\": 60").And.Contain("42");
        }

        /// <summary>
        /// Verifies that a header set from an older window and header sets with an invalid limit or a remaining count
        /// above the limit are ignored, so the known snapshot is kept.
        /// </summary>
        [Fact]
        public void UpdateFromHeaders_ignores_stale_and_invalid_values()
        {
            var store = CreateStore();
            var later = DateTimeOffset.UtcNow.AddHours(1);
            store.UpdateFromHeaders(60, 42, later.ToUnixTimeSeconds());

            // A response from an older window (earlier reset) must not roll knowledge back.
            store.UpdateFromHeaders(60, 59, later.AddMinutes(-30).ToUnixTimeSeconds());
            store.GetSnapshot().Remaining.Should().Be(42);

            // Structurally invalid headers are dropped entirely.
            store.UpdateFromHeaders(0, 5, later.ToUnixTimeSeconds());
            store.UpdateFromHeaders(60, 61, later.ToUnixTimeSeconds());
            store.GetSnapshot().Limit.Should().Be(60);
            store.GetSnapshot().Remaining.Should().Be(42);
        }


        // Automatic request budget

        /// <summary>
        /// Verifies that half of the window limit is available for automatic requests, that only automatic requests
        /// of the current window use that budget, and that a new window restores it.
        /// </summary>
        [Fact]
        public void Auto_request_budget_counts_only_auto_requests_in_the_current_window()
        {
            var store = CreateStore();
            var reset = DateTimeOffset.UtcNow.AddHours(1);

            // Budget = limit / 2 = 5 auto requests.
            store.UpdateFromHeaders(10, 10, reset.ToUnixTimeSeconds());

            store.CanMakeAutoRequest().Should().BeTrue();

            for (var i = 0; i < 4; i++)
            {
                store.RecordRequest("https://api.github.com/repos/o/r/releases", "releases", "auto", 200);
            }

            store.CanMakeAutoRequest().Should().BeTrue("four of five requests are spent");
            store.RecordRequest("https://api.github.com/repos/o/r/releases", "releases", "auto", 200);

            store.CanMakeAutoRequest().Should().BeFalse("the auto budget is exhausted");
            store.GetAutoRequestsRemaining().Should().Be(0);

            // Manual requests never consume the automatic budget.
            store.RecordRequest("https://api.github.com/repos/o/r/releases", "releases", "manual", 200);
            store.GetAutoRequestsRemaining().Should().Be(0);

            // A new window (later reset) resets the counted window even with old records present.
            store.UpdateFromHeaders(10, 10, reset.AddHours(1).ToUnixTimeSeconds());
            store.CanMakeAutoRequest().Should().BeTrue("a fresh window restores the budget");
        }


        // Check spacing

        /// <summary>
        /// Verifies that the delay until the window reset is zero without a known window and otherwise matches the
        /// time left, and that the delay between automatic checks spreads the remaining budget over that time.
        /// </summary>
        [Fact]
        public void GetDelayUntilReset_and_inter_check_delay_follow_the_window_and_budget()
        {
            var store = CreateStore();
            store.GetDelayUntilReset().Should().Be(TimeSpan.Zero, "no window known yet");

            var reset = DateTimeOffset.UtcNow.AddMinutes(50);
            store.UpdateFromHeaders(10, 10, reset.ToUnixTimeSeconds());
            store.GetDelayUntilReset().Should().BeCloseTo(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(5));

            // Budget of 10 with 50 minutes left spaces checks evenly: 50min / 5 requests = ~10min.
            var delay = store.CalculateInterCheckDelay();
            delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(11));
        }

        /// <summary>
        /// Verifies that the delay between automatic checks is the 30 minute maximum when the budget is spent and the
        /// 5 second minimum when the window has already reset.
        /// </summary>
        [Fact]
        public void CalculateInterCheckDelay_pins_to_bounds_when_budget_or_window_is_extreme()
        {
            var store = CreateStore();
            var reset = DateTimeOffset.UtcNow.AddHours(1);
            store.UpdateFromHeaders(10, 10, reset.ToUnixTimeSeconds());
            for (var i = 0; i < 5; i++)
            {
                store.RecordRequest("https://api.github.com/ops", "releases", "auto", 200);
            }

            // Exhausted budget forces the maximum delay.
            store.CalculateInterCheckDelay().Should().Be(TimeSpan.FromMinutes(30));

            var fresh = CreateStore();

            // A window that already reset forces the minimum delay.
            fresh.UpdateFromHeaders(10, 10, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
            fresh.CalculateInterCheckDelay().Should().Be(TimeSpan.FromSeconds(5));
        }


        // Exhaustion notices

        /// <summary>
        /// Verifies that only one exhaustion notice can be reserved at a time, that closing it without acknowledgement
        /// allows another one, that an acknowledged window never warns again, and that a later window warns again.
        /// </summary>
        [Fact]
        public async Task Exhaustion_notice_is_reserved_once_until_acknowledged_and_deduped_per_window()
        {
            var store = CreateStore();
            var reset = _clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
            store.UpdateFromHeaders(60, 0, reset);

            var first = store.TryBeginExhaustionNotice();
            first.Should().NotBeNull("quota is exhausted with a future reset");
            store.TryBeginExhaustionNotice().Should().BeNull("a dialog is already reserved");

            // Closing without acknowledging releases the reservation but keeps the window pending.
            await store.EndExhaustionNoticeAsync(first!, acknowledged: false);
            var second = store.TryBeginExhaustionNotice();
            second.Should().NotBeNull("unacknowledged windows may warn again");

            // Acknowledging records the window so it never warns again.
            await store.EndExhaustionNoticeAsync(second!, acknowledged: true);
            store.TryBeginExhaustionNotice().Should().BeNull("the acknowledged window is done");

            // A later window warns again once the acknowledged window has passed.
            _clock.Advance(TimeSpan.FromHours(3));
            var futureReset = _clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds();
            store.UpdateFromHeaders(60, 0, futureReset);
            store.TryBeginExhaustionNotice().Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that no exhaustion notice is offered without rate-limit data, while quota remains, or when the
        /// exhausted window has already reset.
        /// </summary>
        [Fact]
        public void Exhaustion_notice_requires_an_exhausted_window_with_a_future_reset()
        {
            var store = CreateStore();

            // No data at all.
            store.TryBeginExhaustionNotice().Should().BeNull();

            // Remaining quota - nothing to complain about.
            var reset = _clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
            store.UpdateFromHeaders(60, 12, reset);
            store.TryBeginExhaustionNotice().Should().BeNull();

            // Already-reset window - warning would be pointless.
            var exhausted = CreateStore();
            exhausted.UpdateFromHeaders(60, 0, _clock.GetUtcNow().AddHours(-1).ToUnixTimeSeconds());
            exhausted.TryBeginExhaustionNotice().Should().BeNull();
        }


        // Persistence

        /// <summary>
        /// Verifies that a window snapshot written by one store is loaded unchanged, including the remaining time
        /// until reset, by a second store that uses the same file.
        /// </summary>
        [Fact]
        public async Task Snapshot_round_trips_through_the_persisted_file()
        {
            var reset = DateTimeOffset.UtcNow.AddHours(1);
            CreateStore().UpdateFromHeaders(5000, 4321, reset.ToUnixTimeSeconds());

            var reloaded = CreateStore();
            await reloaded.LoadAsync();
            reloaded.GetSnapshot().Limit.Should().Be(5000);
            reloaded.GetSnapshot().Remaining.Should().Be(4321);
            reloaded.GetDelayUntilReset().Should().BeCloseTo(
                reset - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }
    }
}
