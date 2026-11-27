// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Downloads
{
    /// <summary>
    /// Verifies the sampling window, transfer boundaries and formatting of the shared speed estimator.
    /// </summary>
    public sealed class DownloadTransferSpeedEstimatorTests
    {
        /// <summary>
        /// Verifies that bursts are averaged by elapsed time and samples inside the minimum interval retain the rate.
        /// </summary>
        [Fact]
        public void Sample_averages_irregular_byte_counts_and_expires_old_measurements()
        {
            var clock = new FakeTimeProvider();
            var estimator = new DownloadTransferSpeedEstimator(clock);
            estimator.Sample(0).Should().BeNull();

            clock.Advance(TimeSpan.FromMilliseconds(150));
            estimator.Sample(45).Should().BeNull();

            clock.Advance(TimeSpan.FromMilliseconds(350));
            estimator.Sample(500).Should().Be(1000);

            clock.Advance(TimeSpan.FromMilliseconds(100));
            estimator.Sample(800).Should().Be(1000);

            clock.Advance(TimeSpan.FromMilliseconds(900));
            estimator.Sample(800).Should().BeApproximately(800.0 / 1.5, 0.01);

            clock.Advance(TimeSpan.FromSeconds(2));
            estimator.Sample(800).Should().Be(0);
        }

        /// <summary>
        /// Verifies that a resumed download measures only newly received bytes using the monotonic clock.
        /// </summary>
        [Fact]
        public void Reset_uses_the_resumed_byte_count_as_the_baseline()
        {
            var clock = new FakeTimeProvider();
            var estimator = new DownloadTransferSpeedEstimator(clock);
            estimator.Sample(0);
            clock.Advance(TimeSpan.FromSeconds(1));
            estimator.Sample(2000).Should().Be(2000);

            estimator.Reset();
            estimator.Sample(2000).Should().BeNull();
            clock.Advance(TimeSpan.FromSeconds(1));
            clock.Set(clock.GetUtcNow().AddHours(-1));
            estimator.Sample(2250).Should().Be(250);
        }

        /// <summary>
        /// Verifies that a reset counter starts a fresh window even inside the minimum measurement interval.
        /// </summary>
        [Fact]
        public void Sample_discards_speed_when_the_byte_counter_restarts()
        {
            var clock = new FakeTimeProvider();
            var estimator = new DownloadTransferSpeedEstimator(clock);
            estimator.Sample(0);
            clock.Advance(TimeSpan.FromSeconds(1));
            estimator.Sample(5000).Should().Be(5000);

            estimator.Sample(100).Should().BeNull();
            clock.Advance(TimeSpan.FromSeconds(1));
            estimator.Sample(400).Should().Be(300);
        }

        /// <summary>
        /// Verifies that progress labels share the same numeric rate and unit.
        /// </summary>
        [Fact]
        public void FormatSpeed_uses_binary_units_for_transfer_rates()
        {
            var expected = 2.5.ToString("F1", CultureInfo.CurrentCulture) + " KB/s";
            DownloadTransferSpeedEstimator.FormatSpeed(2560).Should().Be(expected);
            DownloadTransferSpeedEstimator.AppendSpeed("25%", 2560).Should().Be($"25% - {expected}");
            DownloadTransferSpeedEstimator.AppendSpeed("25%", null).Should().Be("25%");
        }
    }
}
