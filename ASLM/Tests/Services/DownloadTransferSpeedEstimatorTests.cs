// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class DownloadTransferSpeedEstimatorTests
{
    [Fact]
    public void Sample_returns_null_until_second_sample()
    {
        var estimator = new DownloadTransferSpeedEstimator();

        estimator.Sample("op-1", 0).Should().BeNull();
        estimator.Sample("op-1", 1024).Should().NotBeNull();
    }

    [Fact]
    public void Reset_clears_state_for_operation()
    {
        var estimator = new DownloadTransferSpeedEstimator();
        estimator.Sample("op-1", 0);
        estimator.Sample("op-1", 4096);

        estimator.Reset("op-1");

        estimator.Sample("op-1", 100).Should().BeNull();
    }

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
