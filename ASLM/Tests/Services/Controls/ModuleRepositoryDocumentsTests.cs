// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Controls.Downloads;
using ASLM.Services.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Controls
{
    /// <summary>
    /// Verifies the root-only documentation policy and bounded, cached GitHub requests without live network calls.
    /// </summary>
    public sealed class ModuleRepositoryDocumentsTests
    {
        // Navigation policy

        /// <summary>
        /// Verifies that the embedded document view allows the data URI that WebView2 announces for the current
        /// HTML before it publishes <c>about:blank</c> as its source, and allows <c>about:blank</c> with or without
        /// a fragment.
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
        /// Verifies that permitting the current document grants no general access: missing or empty URLs, other or
        /// malformed data documents, script and file schemes, web links, and other <c>about:</c> addresses are all
        /// refused.
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
        /// Verifies that the data URI of a previously shown document is refused once another document is selected or
        /// the preview is cleared, so a late navigation cannot replace the newly selected file.
        /// </summary>
        [Fact]
        public void Rejects_a_previous_document_after_selection_changes_or_the_preview_is_cleared()
        {
            const string previous = "<h1>README</h1>";
            var url = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(previous));

            RepositoryDocumentView.IsDocumentNavigation(url, "<h1>LICENSE</h1>").Should().BeFalse();
            RepositoryDocumentView.IsDocumentNavigation(url, null).Should().BeFalse();
        }


        // Document name policy

        /// <summary>
        /// Verifies that the conventional documentation base names are accepted in any casing, without an extension
        /// or with a Markdown or text extension.
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
        /// Verifies that the name policy is an exact allowlist: unlisted names, extra name parts, nested or
        /// parent-relative paths, other extensions, stream or encoded names, stray dots or spaces, and empty names
        /// are all rejected.
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


        // Listing and loading

        /// <summary>
        /// Verifies that the directory listing keeps only regular, non-empty, size-bounded root files with allowed
        /// names, skipping directories, links, nested paths, oversized and empty files, and returns them README
        /// first with the license and notice files after it.
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
        /// Verifies that listing a repository sends a single directory request, that loading one document sends one
        /// request for rendered HTML with its own media type, and that repeating either call is answered from the
        /// cache without another request.
        /// </summary>
        [Fact]
        public async Task Loads_only_selected_documents_from_the_default_branch_and_caches_results()
        {
            // Arrange: a fake transport answering the directory listing and the rendered document.
            using var fixture = new Fixture((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("contents/")
                    ? """[{"name":"ReadMe.MD","path":"ReadMe.MD","type":"file","size":30,"sha":"abc"}]"""
                    : "<h1>Module documentation</h1>")
            }));

            // Act and assert, listing: two calls produce one directory request with the metadata media type.
            var documents = await fixture.Client.GetModuleDocumentsAsync("owner/repo");
            await fixture.Client.GetModuleDocumentsAsync("owner/repo");
            fixture.Requests.Should().ContainSingle();
            fixture.Requests[0].Url.Should().Be("https://api.github.com/repos/owner/repo/contents/");
            fixture.Requests[0].MediaType.Should().Be("application/vnd.github+json");

            // Act and assert, content: two calls produce one more request for the file as rendered HTML.
            (await fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", documents[0])).Should().Contain("<h1>");
            await fixture.Client.GetModuleDocumentHtmlAsync("owner/repo", documents[0]);
            fixture.Requests.Should().HaveCount(2);
            fixture.Requests[1].Url.Should().EndWith("/contents/ReadMe.MD");
            fixture.Requests[1].MediaType.Should().Be("application/vnd.github.html+json");
        }

        /// <summary>
        /// Verifies that loading a document with a disallowed name throws <see cref="ArgumentException"/> before any
        /// request is sent, even when the name never came from a directory listing.
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
        /// Verifies that a failed document request (HTTP 403) throws <see cref="HttpRequestException"/> without
        /// caching the failure, so an immediate retry sends a second request and returns its content.
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
        /// Verifies that cancelling a listing request that is stuck on the network ends it with an
        /// <see cref="OperationCanceledException"/> and that a later lookup for the same repository then completes
        /// instead of waiting on the cancelled one.
        /// </summary>
        [Fact]
        public async Task Cancellation_does_not_block_a_later_lookup()
        {
            // Arrange: the first request signals that it started and then waits until its token is cancelled.
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

            // Act: cancel the first lookup once its request is in flight.
            using var cts = new CancellationTokenSource();
            var first = fixture.Client.GetModuleDocumentsAsync("owner/repo", cts.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await first);

            // Assert: a new lookup is not blocked by the cancelled one and returns the (empty) listing.
            (await fixture.Client.GetModuleDocumentsAsync("owner/repo").WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a 1 MiB rendered document is rejected with <see cref="InvalidDataException"/> both when its
        /// length is declared and when the server streams it without a <c>Content-Length</c> header.
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


        // Rendering

        /// <summary>
        /// Verifies that the presentation wrapper keeps the rendered markup, adds a content security policy that
        /// forbids scripts, base addresses, and form submission, applies the host text and background colors, and
        /// sets a system font stack.
        /// </summary>
        [Fact]
        public void Wraps_passive_document_content_with_the_host_palette()
        {
            var html = RepositoryDocumentRenderer.Build("<h1>Readme</h1><table><tr><td>Value</td></tr></table>",
                "#eeeeee", "#111111", "#0088ff", "#333333");

            html.Should().Contain("<h1>Readme</h1>").And.Contain("<table>");
            html.Should().Contain("default-src 'none'").And.Contain("base-uri 'none'").And.Contain("form-action 'none'");
            html.Should().Contain("color:#eeeeee").And.Contain("background:#111111");
            html.Should().Contain("system-ui,");
            html.Should().NotContain("script-src 'unsafe-inline'");
        }


        // Test doubles

        /// <summary>
        /// Owns a mocked HTTP client and the isolated artwork directory required by the shared GitHub client.
        /// </summary>
        private sealed class Fixture : IDisposable
        {
            /// <summary>
            /// Temporary directory used as the icon cache of the GitHub client; deleted on dispose.
            /// </summary>
            private readonly DirectoryInfo _icons = Directory.CreateTempSubdirectory("ASLM-DocumentTests-");

            /// <summary>
            /// HTTP client whose transport is the fake handler.
            /// </summary>
            private readonly HttpClient _http;

            /// <summary>
            /// URL and Accept media type of every request the client sent, in order.
            /// </summary>
            public List<(string Url, string? MediaType)> Requests { get; } = [];

            /// <summary>
            /// GitHub client under test, wired to the fake transport.
            /// </summary>
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
}
