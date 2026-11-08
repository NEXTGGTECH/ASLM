// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the equivalence check and the precedence ordering that <see cref="ReleaseTagOrdering"/> applies to
    /// release tags and GitHub release records.
    /// </summary>
    public sealed class ReleaseTagOrderingTests
    {
        // Equivalence

        /// <summary>
        /// Verifies which version references count as the same tag: a leading <c>v</c>, build metadata and the case of
        /// pre-release identifiers are ignored, while different versions, a pre-release identifier that is missing or
        /// different on the other side, and a different number of version parts are not.
        /// </summary>
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


        // Precedence

        /// <summary>
        /// Verifies that tags are ordered by their numeric version parts first (so 0.11.0.10 is newer than 0.11.0.9),
        /// that tags with equal numeric parts fall back to a text comparison of the whole tag (which ranks the tag with
        /// a pre-release suffix above the plain release), and that a leading <c>v</c> and build metadata do not affect
        /// the order.
        /// </summary>
        /// <param name="left">First tag to compare.</param>
        /// <param name="right">Second tag to compare.</param>
        /// <param name="expectedSign">Expected sign of the comparison: 1 when the first is newer, -1 when older, 0 when equal.</param>
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

        /// <summary>
        /// Verifies that a null or empty tag compares as equal to another missing tag.
        /// </summary>
        [Fact]
        public void ComparePrecedence_treats_missing_tags_as_equal()
        {
            ReleaseTagOrdering.ComparePrecedence(null, null).Should().Be(0);
            ReleaseTagOrdering.ComparePrecedence(null, "").Should().Be(0);
        }

        /// <summary>
        /// Verifies that tags without a parsable version are ordered by a text comparison of the tags.
        /// </summary>
        [Fact]
        public void ComparePrecedence_falls_back_to_string_order_for_non_version_tags()
        {
            // Both cores fail Version.TryParse, so ordinal string order decides.
            ReleaseTagOrdering.ComparePrecedence("nightly-zen", "nightly-alpha").Should().BePositive();
            ReleaseTagOrdering.ComparePrecedence("nightly-alpha", "nightly-zen").Should().BeNegative();
        }


        // GitHub release ordering

        /// <summary>
        /// Verifies that two releases with the same tag are ordered by publish date, newest first.
        /// </summary>
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

        /// <summary>
        /// Verifies that releases are ordered by tag version before any publish date is considered, so a higher version
        /// comes first.
        /// </summary>
        [Fact]
        public void CompareGitHubReleasesNewestFirst_orders_by_semver_first()
        {
            var left = new GitHubReleaseInfo { TagName = "2.0.0" };
            var right = new GitHubReleaseInfo { TagName = "1.0.0" };

            ReleaseTagOrdering.CompareGitHubReleasesNewestFirst(left, right).Should().BeNegative();
        }
    }
}
