// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services;

public sealed class ReleaseTagOrderingTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", true)]
    [InlineData("1.0.0+build", "1.0.0", true)]
    [InlineData("1.0.0", "2.0.0", false)]
    [InlineData("1.0.0-alpha", "1.0.0", false)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", false)]
    public void AreEquivalentVersionReferences_matches_expected_pairs(
        string left,
        string right,
        bool expected)
    {
        ReleaseTagOrdering.AreEquivalentVersionReferences(left, right).Should().Be(expected);
    }

    [Theory]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.1.0", "1.0.9", 1)]
    [InlineData("1.0.0-rc.1", "1.0.0", 1)]
    public void ComparePrecedence_orders_tags(string left, string right, int expectedSign)
    {
        var cmp = ReleaseTagOrdering.ComparePrecedence(left, right);
        Math.Sign(cmp).Should().Be(expectedSign);
    }

    [Fact]
    public void CompareGitHubReleasesNewestFirst_uses_publish_date_as_tie_breaker()
    {
        var older = new GitHubReleaseInfo
        {
            TagName = "1.0.0",
            PublishedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var newer = new GitHubReleaseInfo
        {
            TagName = "1.0.0",
            PublishedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        ReleaseTagOrdering.CompareGitHubReleasesNewestFirst(older, newer).Should().BePositive();
        ReleaseTagOrdering.CompareGitHubReleasesNewestFirst(newer, older).Should().BeNegative();
    }

    [Fact]
    public void CompareGitHubReleasesNewestFirst_orders_by_semver_first()
    {
        var left = new GitHubReleaseInfo { TagName = "2.0.0" };
        var right = new GitHubReleaseInfo { TagName = "1.0.0" };

        ReleaseTagOrdering.CompareGitHubReleasesNewestFirst(left, right).Should().BeNegative();
    }
}
