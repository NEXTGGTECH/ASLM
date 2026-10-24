// Copyright NEXTGGTECH. Apache License 2.0.


namespace ASLM.Tests.Services.Internal;

public sealed class ReleaseTagOrderingTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("v1.0.0", "1.0.0", true)]
    [InlineData("1.0.0+build", "1.0.0", true)]
    [InlineData("1.0.0", "2.0.0", false)]
    [InlineData("1.0.0-alpha", "1.0.0", false)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", false)]
    [InlineData("0.11.0.6+build.7", "v0.11.0.6", true)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("1.0.0-RC.1", "1.0.0-rc.1", true)]
    [InlineData("1.0.0-alpha", "1.0.0+alpha", false)]
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
    [InlineData("v0.11.0.10", "0.11.0.9", 1)]
    [InlineData("0.11.0.9", "0.11.0.10", -1)]
    [InlineData("1.0.0+b1", "1.0.0+b2", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0-beta", 1)]
    [InlineData("2026.10.24", "2026.9.30", 1)]
    public void ComparePrecedence_orders_tags(string left, string right, int expectedSign)
    {
        var cmp = ReleaseTagOrdering.ComparePrecedence(left, right);
        Math.Sign(cmp).Should().Be(expectedSign);
    }

    [Fact]
    public void ComparePrecedence_treats_missing_tags_as_equal()
    {
        ReleaseTagOrdering.ComparePrecedence(null, null).Should().Be(0);
        ReleaseTagOrdering.ComparePrecedence(null, "").Should().Be(0);
    }

    [Fact]
    public void ComparePrecedence_falls_back_to_string_order_for_non_version_tags()
    {
        // Both cores fail Version.TryParse, so ordinal string order decides.
        ReleaseTagOrdering.ComparePrecedence("nightly-zen", "nightly-alpha").Should().BePositive();
        ReleaseTagOrdering.ComparePrecedence("nightly-alpha", "nightly-zen").Should().BeNegative();
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
