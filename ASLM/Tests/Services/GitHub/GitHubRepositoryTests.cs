// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.GitHub
{
    /// <summary>
    /// Verifies repository input and portable destination validation without network requests.
    /// </summary>
    public sealed class GitHubRepositoryTests
    {
        /// <summary>
        /// Verifies normalization of exact repository names and HTTPS clone links.
        /// </summary>
        [Theory]
        [InlineData("owner/repo")]
        [InlineData(" owner/repo/ ")]
        [InlineData("owner/repo.git")]
        [InlineData("https://github.com/owner/repo")]
        [InlineData("https://github.com/owner/repo.git/")]
        public void Exact_repository_inputs_are_normalized(string query)
        {
            GitHubRepository.TryParse(query, out var repo).Should().BeTrue();
            repo.Should().Be("owner/repo");
        }

        /// <summary>
        /// Verifies that other hosts, credentials, fragments, nested pages and normalized traversal are rejected.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("owner")]
        [InlineData("owner/../repo")]
        [InlineData("owner/repo/issues")]
        [InlineData("http://github.com/owner/repo")]
        [InlineData("https://github.com.evil.test/owner/repo")]
        [InlineData("https://token@github.com/owner/repo")]
        [InlineData("https://github.com/owner/repo?token=value")]
        [InlineData("https://github.com/owner/repo#readme")]
        [InlineData("https://github.com/owner/repo/tree/main")]
        [InlineData("https://github.com/ignored/../owner/repo")]
        [InlineData("https://github.com/owner/%2E%2E/repo")]
        public void Non_repository_inputs_are_rejected(string query)
        {
            GitHubRepository.TryParse(query, out _).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that remote ids cannot escape Modules or use reserved Windows folder names on another OS.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("..")]
        [InlineData("../outside")]
        [InlineData("nested/module")]
        [InlineData("nested\\module")]
        [InlineData("C:module")]
        [InlineData("module.")]
        [InlineData("module ")]
        [InlineData("CON")]
        [InlineData("nul.txt")]
        [InlineData("LPT1")]
        public void Unsafe_module_destinations_are_rejected(string id)
        {
            using var root = new UniqueAslmRoot();
            var resolve = () => UpdateManager.GetRepositoryModuleManifestPath(id);
            resolve.Should().Throw<InvalidDataException>();
            Directory.EnumerateFileSystemEntries(root.ModulesDir).Should().BeEmpty();
        }
    }
}
