// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the per-operation transfer speed sampling and formatting of
    /// <see cref="DownloadTransferSpeedEstimator"/>.
    /// </summary>
    public sealed class DownloadTransferSpeedEstimatorTests
    {
        /// <summary>
        /// Verifies that the first sample of an operation returns null, because it only sets the baseline, while the
        /// second sample returns a non-null text (empty here, because the two samples are too close in time to derive
        /// a speed).
        /// </summary>
        [Fact]
        public void Sample_returns_null_until_second_sample()
        {
            var estimator = new DownloadTransferSpeedEstimator();

            estimator.Sample("op-1", 0).Should().BeNull();
            estimator.Sample("op-1", 1024).Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that resetting an operation discards its samples, so the next sample is again a baseline that
        /// returns null.
        /// </summary>
        [Fact]
        public void Reset_clears_state_for_operation()
        {
            var estimator = new DownloadTransferSpeedEstimator();
            estimator.Sample("op-1", 0);
            estimator.Sample("op-1", 4096);

            estimator.Reset("op-1");

            estimator.Sample("op-1", 100).Should().BeNull();
        }

        /// <summary>
        /// Verifies that a measured speed is returned as a human-readable label of a number with a unit per second
        /// (B, KB, MB or GB), and that the measurement completes within two seconds.
        /// </summary>
        [Fact]
        public void Sample_formats_kilobytes_per_second()
        {
            var estimator = new DownloadTransferSpeedEstimator();
            var key = "download-" + Guid.NewGuid().ToString("N");
            var start = DateTimeOffset.UtcNow;

            estimator.Sample(key, 0);
            Thread.Sleep(120);
            var label = estimator.Sample(key, 256 * 1024);

            label.Should().NotBeNullOrWhiteSpace();
            label.Should().MatchRegex(@"\d+([.,]\d+)?\s+(B|KB|MB|GB)/s");
            (DateTimeOffset.UtcNow - start).Should().BeLessThan(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Verifies that a byte counter that moves backwards (a restarted transfer) yields a non-null result (no
        /// speed) instead of failing.
        /// </summary>
        [Fact]
        public void Sample_handles_negative_byte_delta()
        {
            var estimator = new DownloadTransferSpeedEstimator();
            var key = "neg-" + Guid.NewGuid().ToString("N");

            estimator.Sample(key, 10_000);
            var label = estimator.Sample(key, 100);

            label.Should().NotBeNull();
        }
    }
}
