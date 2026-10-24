// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text.Json;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies response caching and the module-document filters of the GitHub client.
/// </summary>
public sealed class GitHubUpdateClientCachingTests
{
    private static GitHubUpdateClient CreateClient(StubHttpHandler http) => new(
        new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance,
            Path.Combine(Path.GetTempPath(), "ASLM-Tests", $"rl-{Guid.NewGuid():N}.json"), TimeProvider.System),
        new GitHubAccountStore(new AppDataStore(NullLogger<AppDataStore>.Instance),
            new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance,
                Path.Combine(Path.GetTempPath(), "ASLM-Tests", $"rl-{Guid.NewGuid():N}.json"), TimeProvider.System),
            NullLogger<GitHubAccountStore>.Instance, new HttpClient(new StubHttpHandler())),
        new HttpClient(http),
        new ModuleIconCache(Path.Combine(Path.GetTempPath(), "ASLM-Tests", $"icons-{Guid.NewGuid():N}"),
            TimeProvider.System, NullLogger<ModuleIconCache>.Instance));

    [Fact]
    public async Task Release_lists_are_cached_per_repository()
    {
        var http = new StubHttpHandler();
        // The GitHub client fetches releases with query parameters; match by prefix.
        http.OnJson("https://api.github.com/repos/owner/repo/releases*",
            """[{"tag_name":"v1.0","zipball_url":"https://api.github.com/zip"}]""");
        var client = CreateClient(http);

        var first = await client.GetReleasesAsync("owner/repo", includePrerelease: false);
        var second = await client.GetReleasesAsync("owner/repo", includePrerelease: false);

        first.Should().ContainSingle().Which.TagName.Should().Be("v1.0");
        second.Should().BeEquivalentTo(first);
        http.Requests.Should().HaveCount(1, "the second read must come from cache");
    }

    [Fact]
    public async Task Branch_lists_are_cached_per_repository()
    {
        var http = new StubHttpHandler();
        var branchesUrl = "https://api.github.com/repos/owner/repo/branches*";
        http.OnJson(branchesUrl, """[{"name":"main","commit":{"sha":"abc"}}]""");
        var client = CreateClient(http);

        var first = await client.GetBranchesAsync("owner/repo");
        var second = await client.GetBranchesAsync("owner/repo");

        second.Should().BeEquivalentTo(first);
        http.Requests.Count(request => request.Url.ToString().StartsWith(branchesUrl[..^1]))
            .Should().Be(1, "the second read must come from cache");
    }

    [Fact]
    public async Task A_cold_fetch_failure_surfaces_to_the_caller()
    {
        var http = new StubHttpHandler();
        var url = "https://api.github.com/repos/owner/repo/releases*";
        http.On(url, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server exploded")
        });
        var client = CreateClient(http);

        var act = async () => await client.GetReleasesAsync("owner/repo", includePrerelease: false);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("readme.markdown", true)]
    [InlineData("LICENSE.txt", true)]
    [InlineData("CHANGELOG", true)]
    [InlineData("readme.md.exe", false)]
    [InlineData("image.png", false)]
    [InlineData("docs/README.md", false)]
    [InlineData("..\\README.md", false)]
    [InlineData("unknown.md", false)]
    public void IsModuleDocumentName_accepts_only_conventional_root_documents(string name, bool expected)
    {
        GitHubUpdateClient.IsModuleDocumentName(name).Should().Be(expected);
    }

    [Fact]
    public void SelectModuleDocuments_filters_links_directories_sizes_and_mismatches()
    {
        var json = JsonSerializer.Serialize(new object?[]
        {
            // Symlink disguised with a friendly name.
            new { type = "file", name = "README.md", path = "README.md", size = 4, sha = "b", target = "evil.txt" },
            // Directory entry.
            new { type = "dir", name = "docs", path = "docs", size = 0 },
            // Nested path must match its name.
            new { type = "file", name = "LICENSE", path = "sub/LICENSE", size = 9, sha = "c" },
            // Oversized.
            new { type = "file", name = "SECURITY.md", path = "SECURITY.md", size = 2 * 1024 * 1024, sha = "d" },
            // Not a conventional document.
            new { type = "file", name = "notes.txt", path = "notes.txt", size = 3, sha = "e" }
        });

        var documents = GitHubUpdateClient.SelectModuleDocuments(JsonDocument.Parse(json).RootElement);

        documents.Should().BeEmpty();
    }

    [Fact]
    public void SelectModuleDocuments_orders_readme_first_and_keeps_license_variants()
    {
        var json = JsonSerializer.Serialize(new object?[]
        {
            new { type = "file", name = "LICENSE.txt", path = "LICENSE.txt", size = 5, sha = "a" },
            new { type = "file", name = "readme.md", path = "readme.md", size = 7, sha = "b" },
            new { type = "file", name = "CONTRIBUTING.md", path = "CONTRIBUTING.md", size = 3, sha = "c" }
        });

        var documents = GitHubUpdateClient.SelectModuleDocuments(JsonDocument.Parse(json).RootElement);

        documents.Select(d => d.Name).Should().Equal("readme.md", "LICENSE.txt", "CONTRIBUTING.md");
    }
}
