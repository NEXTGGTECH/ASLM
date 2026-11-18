// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Models
{
    /// <summary>
    /// Keeps repository discovery metadata in memory until the user installs the module.
    /// </summary>
    /// <param name="Repository">Canonical GitHub repository name.</param>
    /// <param name="DefaultBranch">Repository branch used when no explicit branch was declared.</param>
    /// <param name="ManifestJson">Already downloaded root manifest, reused for artwork.</param>
    internal sealed record ModuleRepositoryPreview(string Repository, string DefaultBranch, string ManifestJson);
}
