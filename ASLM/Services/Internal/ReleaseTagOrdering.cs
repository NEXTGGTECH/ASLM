// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Normalizes and orders GitHub-style release tags consistently for ASLM and module updates.
    /// </summary>
    public static class ReleaseTagOrdering
    {
        // Version equivalence

        /// <summary>
        /// Returns whether two local or remote version references point at the same GitHub tag identity.
        /// </summary>
        public static bool AreEquivalentVersionReferences(string left, string right)
        {
            var leftNormalized = NormalizeVersionReference(left);
            var rightNormalized = NormalizeVersionReference(right);

            // Stable numeric versions should compare semantically so 1.0 and 1.0.0 are treated as the same release.
            if (!leftNormalized.Contains('-', StringComparison.Ordinal) &&
                !rightNormalized.Contains('-', StringComparison.Ordinal) &&
                Version.TryParse(leftNormalized, out var leftVersion) &&
                Version.TryParse(rightNormalized, out var rightVersion))
            {
                return leftVersion == rightVersion;
            }

            return string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
        }


        // Tag precedence

        /// <summary>
        /// Compares two release tags; positive when <paramref name="leftTag"/> is strictly newer than <paramref name="rightTag"/>.
        /// </summary>
        public static int ComparePrecedence(string? leftTag, string? rightTag)
        {
            if (AreEquivalentVersionReferences(leftTag ?? string.Empty, rightTag ?? string.Empty))
            {
                return 0;
            }

            var left = NormalizeVersionReference(leftTag ?? string.Empty);
            var right = NormalizeVersionReference(rightTag ?? string.Empty);

            var leftCore = ExtractSemverNumericCore(left);
            var rightCore = ExtractSemverNumericCore(right);

            if (Version.TryParse(leftCore, out var leftVersion) && Version.TryParse(rightCore, out var rightVersion))
            {
                var coreCompare = leftVersion.CompareTo(rightVersion);
                if (coreCompare != 0)
                {
                    return coreCompare;
                }
            }
            else
            {
                var coreString = string.Compare(leftCore, rightCore, StringComparison.OrdinalIgnoreCase);
                if (coreString != 0)
                {
                    return coreString;
                }
            }

            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Orders two GitHub release records so the semantically newest tag appears first, using publish dates as a tie-breaker.
        /// </summary>
        public static int CompareGitHubReleasesNewestFirst(GitHubReleaseInfo left, GitHubReleaseInfo right)
        {
            var versionCmp = -ComparePrecedence(left.TagName, right.TagName);
            if (versionCmp != 0)
            {
                return versionCmp;
            }

            var leftStamp = left.PublishedAt ?? left.CreatedAt ?? DateTimeOffset.MinValue;
            var rightStamp = right.PublishedAt ?? right.CreatedAt ?? DateTimeOffset.MinValue;
            return rightStamp.CompareTo(leftStamp);
        }


        // Normalization helpers

        /// <summary>
        /// Normalizes a version or release tag while preserving pre-release identifiers and removing build metadata.
        /// </summary>
        private static string NormalizeVersionReference(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            {
                normalized = normalized[1..];
            }

            var buildMetadataIndex = normalized.IndexOf('+');
            return buildMetadataIndex > 0 ? normalized[..buildMetadataIndex] : normalized;
        }

        /// <summary>
        /// Returns the dotted numeric portion used for <see cref="Version"/> comparisons before any pre-release suffix.
        /// </summary>
        private static string ExtractSemverNumericCore(string normalizedReference)
        {
            var dash = normalizedReference.IndexOf('-', StringComparison.Ordinal);
            return dash > 0 ? normalizedReference[..dash] : normalizedReference;
        }
    }
}
