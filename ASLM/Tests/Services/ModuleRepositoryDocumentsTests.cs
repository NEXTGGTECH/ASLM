// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Controls.Downloads;
using ASLM.Services.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies the root-only documentation policy and bounded, cached GitHub requests without live network calls.
/// </summary>
public sealed class ModuleRepositoryDocumentsTests
{
    /// <summary>
    /// WebView2 announces NavigateToString through an encoded data URI before publishing about:blank as its source.
    /// </summary>
    [Fact]
    public void Allows_the_current_inline_document_before_its_about_blank_navigation_completes()
    {
        const string html = "<!doctype html><html><body><h1>Документы ✓</h1></body></html>";
        var url = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html));

        // MAUI forwards Uri.AbsoluteUri from the native NavigationStarting event.
        RepositoryDocumentView.IsDocumentNavigation(new Uri(url).AbsoluteUri, html).Should().BeTrue();
        RepositoryDocumentView.IsDocumentNavigation("about:blank", html).Should().BeTrue();
        RepositoryDocumentView.IsDocumentNavigation("about:blank#license", html).Should().BeTrue();
    }

    /// <summary>
    /// Permitting the current document must not grant general access to data, script, file or external navigation.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("data:text/html;charset=utf-8;base64,PGgxPk90aGVyPC9oMT4=")]
    [InlineData("data:text/html;charset=utf-8;base64,not-base64")]
    [InlineData("data:text/html,<h1>Other</h1>")]
    [InlineData("data:text/plain;charset=utf-8;base64,PGgxPk90aGVyPC9oMT4=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/private.txt")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("about:blank-other")]
    public void Keeps_unrelated_navigations_out_of_the_embedded_document(string? url) =>
        RepositoryDocumentView.IsDocumentNavigation(url, "<h1>Current document</h1>").Should().BeFalse();

    /// <summary>
    /// A late navigation for a previous selection cannot replace the newly selected file.
    /// </summary>
    [Fact]
    public void Rejects_a_previous_document_after_selection_changes_or_the_preview_is_cleared()
    {
        const string previous = "<h1>README</h1>";
        var url = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(previous));

        RepositoryDocumentView.IsDocumentNavigation(url, "<h1>LICENSE</h1>").Should().BeFalse();
        RepositoryDocumentView.IsDocumentNavigation(url, null).Should().BeFalse();
    }

    /// <summary>
    /// Conventional filenames accept mixed casing and the supported text extensions.
    /// </summary>
    [Theory]
    [InlineData("README")]
    [InlineData("ReadMe.Md")]
    [InlineData("readme.markdown")]
    [InlineData("LICENSE")]
    [InlineData("licence.TXT")]
    [InlineData("Notice.md")]
    [InlineData("COPYING")]
    [InlineData("copyright.md")]
    [InlineData("AUTHORS.txt")]
    [InlineData("CONTRIBUTORS")]
    [InlineData("CHANGELOG.md")]
    [InlineData("History")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("code_of_conduct.MD")]
    [InlineData("SECURITY.md")]
    [InlineData("SUPPORT")]
    public void Accepts_standard_root_document_names(string name) =>
        GitHubUpdateClient.IsModuleDocumentName(name).Should().BeTrue();

    /// <summary>
    /// The policy is an exact allowlist, not a scan for Markdown, filename prefixes or nested documentation.
    /// </summary>
    [Theory]
    [InlineData("AGENTS.md")]
    [InlineData("CLAUDE.md")]
    [InlineData("PLAN.md")]
    [InlineData("README-generated.md")]
    [InlineData("README.ru.md")]
    [InlineData("docs/README.md")]
    [InlineData(".github/SECURITY.md")]
    [InlineData("../LICENSE")]
    [InlineData("docs\\NOTICE.md")]
    [InlineData("README.md.bak")]
    [InlineData("README.exe")]
    [InlineData("LICENSE.html")]
    [InlineData("README.svg")]
    [InlineData(".README")]
    [InlineData("README.md:stream")]
    [InlineData("README%2emd")]
    [InlineData("README.")]
    [InlineData(" README.md")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_nonstandard_files_and_paths(string? name) =>
        GitHubUpdateClient.IsModuleDocumentName(name).Should().BeFalse();

    /// <summary>
    /// Listing ignores directory entries, symlinks, oversized files and misleading display names.
    /// </summary>
    [Fact]
    public void Selects_only_nonempty_regular_root_files_in_stable_order()
    {
        using var json = JsonDocument.Parse("""
            [
              {"name":"NOTICE","path":"NOTICE","type":"file","size":20,"sha":"notice"},
              {"name":"ReadMe.MD","path":"ReadMe.MD","type":"file","size":20,"sha":"readme"},
              {"name":"LICENSE.txt","path":"LICENSE.txt","type":"file","size":20,"sha":"license"},
              {"name":"README","path":"docs/README","type":"file","size":20},
              {"name":"AUTHORS","path":"AUTHORS","type":"symlink","size":20},
              {"name":"CONTRIBUTORS","path":"CONTRIBUTORS","type":"file","target":"elsewhere","size":20},
              {"name":"CONTRIBUTING","path":"CONTRIBUTING","type":"dir","size":20},
              {"name":"SECURITY.md","path":"SECURITY.md","type":"file","size":1048577},
              {"name":"CHANGELOG","path":"CHANGELOG","type":"file","size":0},
              {"name":"AGENTS.md","path":"AGENTS.md","type":"file","size":20}
            ]
            """);

        var selected = GitHubUpdateClient.SelectModuleDocuments(json.RootElement);

        selected.Select(document => document.Name).Should().Equal("ReadMe.MD", "LICENSE.txt", "NOTICE");
        selected[0].Sha.Should().Be("readme");
    }

    /// <summary>
    /// Directory discovery does not download files and repeated selection reuses rendered content.
    /// </summary>
    [Fact]
    public async Task Loads_only_selected_documents_from_the_default_branch_and_caches_results()
    {
        using var fixture = new Fixture((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("contents/")
                ? """[{"name":"ReadMe.MD","path":"ReadMe.MD","type":"file","size":30,"sha":"abc"}]"""
                : "<h1>Module documentation</h1>")
        }));

        var documents = await fixture.Client.GetModuleDocumentsAsync("owner/repo");
        await fixture.Client.GetModuleDocumentsAsync("owner/repo");
        fixture.Requests.Should().ContainSingle();
        fixture.Requests[0].Url.Should().Be("https://api.github.com/repos/owner/repo/contents/");
        fixture.Requests[0].MediaType.Should().Be("application/vnd.github+json");

        (await fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", documents[0])).Should().Contain("<h1>");
        await fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", documents[0]);
        fixture.Requests.Should().HaveCount(2);
        fixture.Requests[1].Url.Should().EndWith("/contents/ReadMe.MD");
        fixture.Requests[1].MediaType.Should().Be("application/vnd.github.html+json");
    }

    /// <summary>
    /// Content lookup enforces the same name policy even when no directory selection was made.
    /// </summary>
    [Fact]
    public async Task Rejects_a_disallowed_document_before_sending_any_request()
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Unexpected request"));
        var load = () => fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", new("AGENTS.md", "abc"));

        await load.Should().ThrowAsync<ArgumentException>();
        fixture.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// Errors do not poison an empty cache; a retry can recover without reopening the application.
    /// </summary>
    [Fact]
    public async Task Recovers_after_a_failed_document_request()
    {
        var attempts = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<p>Recovered</p>") }));
        var document = new ModuleRepositoryDocument("README.md", "abc");
        var load = () => fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", document);

        await load.Should().ThrowAsync<HttpRequestException>();
        (await load()).Should().Be("<p>Recovered</p>");
        fixture.Requests.Should().HaveCount(2);
    }

    /// <summary>
    /// Closing a view cancels its network request and releases the coalescing gate for the next view.
    /// </summary>
    [Fact]
    public async Task Cancellation_does_not_block_a_later_lookup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        using var fixture = new Fixture(async (_, token) =>
        {
            if (++attempts == 1)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var cts = new CancellationTokenSource();
        var first = fixture.Client.GetModuleDocumentsAsync("owner/repo", cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);

        (await fixture.Client.GetModuleDocumentsAsync("owner/repo").WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
    }

    /// <summary>
    /// HTML size limits also apply when the server omits Content-Length.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_oversized_content_with_or_without_declared_length(bool chunked)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = chunked ? new ChunkedContent(new string('x', 1024 * 1024)) : new StringContent(new string('x', 1024 * 1024))
        }));
        var load = () => fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", new("README.md", "abc"));

        await load.Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>
    /// The presentation wrapper preserves rendered markup while disabling scripts, frames and form submission.
    /// </summary>
    [Fact]
    public void Wraps_passive_document_content_with_the_host_palette()
    {
        var html = RepositoryDocumentRenderer.Build("<h1>Readme</h1><table><tr><td>Value</td></tr></table>",
            "#eeeeee", "#111111", "#0088ff", "#333333");

        html.Should().Contain("<h1>Readme</h1>").And.Contain("<table>");
        html.Should().Contain("default-src 'none'").And.Contain("base-uri 'none'").And.Contain("form-action 'none'");
        html.Should().Contain("color:#eeeeee").And.Contain("background:#111111");
        html.Should().NotContain("script-src 'unsafe-inline'");
    }

    /// <summary>
    /// Owns a mocked HTTP client and the isolated artwork directory required by the shared GitHub client.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _icons = Directory.CreateTempSubdirectory("ASLM-DocumentTests-");
        private readonly HttpClient _http;
        public List<(string Url, string? MediaType)> Requests { get; } = [];
        public GitHubUpdateClient Client { get; }

        /// <summary>
        /// Records the requested route/media type without exposing account tokens in test output.
        /// </summary>
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            _http = new HttpClient(new Handler((request, token) =>
            {
                Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Accept.Single().MediaType));
                return respond(request, token);
            }));
            var data = new AppDataStore(NullLogger<AppDataStore>.Instance);
            var limits = new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance);
            var account = new GitHubAccountStore(data, limits, NullLogger<GitHubAccountStore>.Instance);
            Client = new GitHubUpdateClient(limits, account, _http, new ModuleIconCache(_icons.FullName));
        }

        /// <summary>
        /// Releases only resources created by this fixture.
        /// </summary>
        public void Dispose()
        {
            _http.Dispose();
            _icons.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Substitutes deterministic repository responses for the real GitHub transport.
    /// </summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        /// <summary>
        /// Dispatches the request to the individual test's response factory.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    /// <summary>
    /// Simulates a streaming response that cannot declare its length in advance.
    /// </summary>
    private sealed class ChunkedContent(string text) : HttpContent
    {
        /// <summary>
        /// Writes the test payload to the response stream.
        /// </summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        /// <summary>
        /// Deliberately omits Content-Length so the streaming byte limit is exercised.
        /// </summary>
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
