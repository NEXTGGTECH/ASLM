// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Dialogs
{
    /// <summary>
    /// Verifies the GitHub quota exhaustion notice: it is reserved once per rate-limit window across views,
    /// restarts, and parallel responses, triggers only for an exhausted hourly core quota, follows the switch between
    /// anonymous and account quota, and is raised by every GitHub client path. No network is used.
    /// </summary>
    public sealed class GitHubRateLimitNoticeTests : IDisposable
    {
        // Fields and helpers

        /// <summary>
        /// Temporary directory for the state file and downloads of one test; deleted on dispose.
        /// </summary>
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ASLM-GitHubQuota-");

        /// <summary>
        /// Controllable clock shared by all stores of one test.
        /// </summary>
        private readonly Clock _clock = new();

        /// <summary>
        /// Path of the rate-limit state file inside the temporary directory.
        /// </summary>
        private string StatePath => Path.Combine(_directory.FullName, "limits.json");

        /// <summary>
        /// Unix time of the quota window reset: one hour after the current fake time.
        /// </summary>
        private long Reset => _clock.Now.AddHours(1).ToUnixTimeSeconds();

        /// <summary>
        /// Creates a store on the test state file and clock; calling it again simulates an application restart.
        /// </summary>
        private GitHubRateLimitStore Store() => new(NullLogger<GitHubRateLimitStore>.Instance, StatePath, _clock);

        /// <summary>
        /// Deletes the temporary directory.
        /// </summary>
        public void Dispose() => _directory.Delete(recursive: true);


        // Notice reservation

        /// <summary>
        /// Verifies that an exhausted core quota (reported by a 200, 403, or 429 response) yields exactly one notice
        /// per window, that acknowledging it suppresses repeats including after a restart and an expired window, and
        /// that a new exhausted window produces a new notice.
        /// </summary>
        [Theory]
        [InlineData(200)]
        [InlineData(403)]
        [InlineData(429)]
        public async Task Exhaustion_is_shown_once_per_window_even_after_restarting(int status)
        {
            // Act and assert, reservation: the first caller gets the notice, a second caller gets none.
            var store = Store();
            using var response = Response((HttpStatusCode)status);
            store.UpdateFromResponse(response, authenticated: false);
            var notice = store.TryBeginExhaustionNotice();
            notice.Should().NotBeNull();
            store.TryBeginExhaustionNotice().Should().BeNull();

            // Act and assert, acknowledgment: the same exhausted window never produces another notice.
            await store.EndExhaustionNoticeAsync(notice!, acknowledged: true);
            store.UpdateFromResponse(response, authenticated: false);
            store.TryBeginExhaustionNotice().Should().BeNull();

            // Act and assert, restart: the acknowledgment survives through the persisted state, even after the
            // window has expired, and only a fresh exhausted window shows the notice again.
            var restarted = Store();
            await restarted.LoadAsync();
            restarted.TryBeginExhaustionNotice().Should().BeNull();
            _clock.Now = DateTimeOffset.FromUnixTimeSeconds(Reset).AddSeconds(1);
            restarted.TryBeginExhaustionNotice().Should().BeNull("an expired cached zero is not a new exhaustion");
            restarted.UpdateFromHeaders(60, 60, Reset);
            restarted.TryBeginExhaustionNotice().Should().BeNull();
            restarted.UpdateFromHeaders(60, 0, Reset);
            restarted.TryBeginExhaustionNotice().Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that a notice released without a button press is not acknowledged and can be reserved again,
        /// that ending an obsolete notice cannot release the reservation of a newer view, and that the acknowledgment
        /// of the current notice only counts when it is a button press.
        /// </summary>
        [Fact]
        public async Task Navigating_away_without_pressing_a_button_does_not_acknowledge_the_notice()
        {
            var store = Store();
            store.UpdateFromHeaders(60, 0, Reset);

            // Release the first reservation without a button: the notice becomes available again.
            var first = store.TryBeginExhaustionNotice()!;
            await store.EndExhaustionNoticeAsync(first, acknowledged: false);
            var next = store.TryBeginExhaustionNotice();
            next.Should().NotBeNull();

            // The obsolete first notice cannot acknowledge or release the reservation held by the next view.
            await store.EndExhaustionNoticeAsync(first, acknowledged: true);
            store.TryBeginExhaustionNotice().Should().BeNull("an obsolete view must not release another view's reservation");

            // Releasing the current reservation without a button again leaves the notice available.
            await store.EndExhaustionNoticeAsync(next!, acknowledged: false);
            store.TryBeginExhaustionNotice().Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that twenty parallel exhausted responses reserve only one notice, that an out-of-order response
        /// reporting remaining requests does not restore the budget, and that headers of the already acknowledged
        /// window (slightly older or newer reset time) do not raise a second notice.
        /// </summary>
        [Fact]
        public async Task Parallel_responses_reserve_only_one_dialog_and_out_of_order_responses_cannot_restore_budget()
        {
            // Arrange: every state change tries to reserve a notice, like views subscribed to the store.
            var store = Store();
            var notices = new ConcurrentBag<GitHubRateLimitStore.ExhaustionNotice>();
            store.StateChanged += (_, _) => { if (store.TryBeginExhaustionNotice() is { } notice) notices.Add(notice); };

            // Act and assert, parallel exhaustion: one reservation and a stale "20 remaining" response is ignored.
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.UpdateFromHeaders(60, 0, Reset))));
            notices.Should().ContainSingle();
            store.UpdateFromHeaders(60, 20, Reset);
            store.GetSnapshot().Remaining.Should().Be(0);

            // Act and assert, acknowledged window: later headers around the same reset time raise nothing.
            await store.EndExhaustionNoticeAsync(notices.Single(), acknowledged: true);
            store.UpdateFromHeaders(60, 0, Reset - 10);
            store.UpdateFromHeaders(60, 0, Reset + 10);
            notices.Should().ContainSingle("the acknowledged window has not reset yet");
        }


        // Notice triggers

        /// <summary>
        /// Verifies that responses that are not an exhausted hourly core quota do not trigger a notice: a 403 or 429
        /// with requests remaining (permission failure or secondary throttle) and exhausted search or GraphQL buckets.
        /// </summary>
        [Theory]
        [InlineData("core", 1, 403)]
        [InlineData("core", 1, 429)]
        [InlineData("search", 0, 403)]
        [InlineData("graphql", 0, 403)]
        public void Permission_failures_secondary_throttles_and_other_buckets_do_not_trigger_hourly_notice(
            string resource, int remaining, int status)
        {
            var store = Store();
            using var response = Response((HttpStatusCode)status, remaining, resource);
            store.UpdateFromResponse(response, authenticated: false);
            store.TryBeginExhaustionNotice().Should().BeNull();
        }

        /// <summary>
        /// Verifies that a reset time of -1, 0, or a value beyond the representable range is ignored and never leads
        /// to a notice.
        /// </summary>
        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(long.MaxValue)]
        public void Invalid_reset_headers_are_ignored(long reset)
        {
            var store = Store();
            store.UpdateFromHeaders(60, 0, reset);
            store.TryBeginExhaustionNotice().Should().BeNull();
        }

        /// <summary>
        /// Verifies that a response without rate-limit headers and an exhausted window whose reset time already
        /// passed produce no notice.
        /// </summary>
        [Fact]
        public void Missing_or_expired_quota_does_not_trigger_a_notice()
        {
            var store = Store();
            using var missing = new HttpResponseMessage(HttpStatusCode.Forbidden);
            store.UpdateFromResponse(missing, authenticated: false);
            store.TryBeginExhaustionNotice().Should().BeNull();
            store.UpdateFromHeaders(60, 0, _clock.Now.AddSeconds(-1).ToUnixTimeSeconds());
            store.TryBeginExhaustionNotice().Should().BeNull();
        }


        // Quota scope

        /// <summary>
        /// Verifies that connecting an account resets the known quota to the account default instead of keeping the
        /// anonymous exhaustion, that late anonymous responses are ignored afterward, that an exhausted account quota
        /// raises its own authenticated notice, and that disconnecting does not undo the earlier anonymous
        /// acknowledgment.
        /// </summary>
        [Fact]
        public async Task Connecting_changes_quota_scope_without_replaying_anonymous_responses()
        {
            // Arrange: an exhausted and acknowledged anonymous window.
            var store = Store();
            var anonymousReset = Reset;
            store.UpdateFromHeaders(60, 0, anonymousReset);
            await store.EndExhaustionNoticeAsync(store.TryBeginExhaustionNotice()!, acknowledged: true);

            // Act and assert, connecting: the quota switches to the account default and late anonymous responses
            // are ignored.
            store.ApplyAuthenticatedLimitHint(true);
            store.TryBeginExhaustionNotice().Should().BeNull();
            store.UpdateFromHeaders(60, 0, anonymousReset, authenticated: false);
            store.GetSnapshot().Limit.Should().Be(5000);
            store.TryBeginExhaustionNotice().Should().BeNull();

            // Act and assert, account exhaustion: an exhausted authenticated quota raises an authenticated notice.
            store.UpdateFromHeaders(5000, 0, anonymousReset - 100, authenticated: true);
            var accountNotice = store.TryBeginExhaustionNotice();
            accountNotice!.Authenticated.Should().BeTrue();
            await store.EndExhaustionNoticeAsync(accountNotice, acknowledged: true);

            // Act and assert, disconnecting: the anonymous window stays acknowledged.
            store.ApplyAuthenticatedLimitHint(false);
            store.UpdateFromHeaders(60, 0, anonymousReset, authenticated: false);
            store.TryBeginExhaustionNotice().Should().BeNull("disconnecting does not undo the anonymous acknowledgment");
        }


        // Subscribers

        /// <summary>
        /// Verifies that a state-change subscriber that throws neither breaks the quota update nor hides the change
        /// from other subscribers, and that the notice stays available.
        /// </summary>
        [Fact]
        public void A_failing_UI_subscriber_does_not_break_a_request_or_hide_the_notice_from_other_subscribers()
        {
            var store = Store();
            store.StateChanged += (_, _) => throw new InvalidOperationException("Test detached view");
            var notified = false;
            store.StateChanged += (_, _) => notified = true;
            store.UpdateFromHeaders(60, 0, Reset);
            notified.Should().BeTrue();
            store.TryBeginExhaustionNotice().Should().NotBeNull();
        }


        // Client integration

        /// <summary>
        /// Verifies that releases, branches, artwork, archive downloads, and the rate-limit refresh all record an
        /// exhausted quota from a 403 response before the HTTP error is thrown (the refresh itself does not throw).
        /// </summary>
        [Theory]
        [InlineData("releases")]
        [InlineData("branches")]
        [InlineData("artwork")]
        [InlineData("archive")]
        [InlineData("rate_limit")]
        public async Task All_GitHub_client_paths_report_quota_exhaustion_before_returning_an_HTTP_error(string operation)
        {
            // Arrange: a client whose every request answers 403 with exhausted rate-limit headers.
            var store = Store();
            using var http = new HttpClient(new Handler(_ => Response(HttpStatusCode.Forbidden)));
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            var account = new GitHubAccountStore(appData, store, NullLogger<GitHubAccountStore>.Instance);
            var client = new GitHubUpdateClient(store, account, http, new ModuleIconCache(_directory.FullName));
            Func<Task> request = operation switch
            {
                "releases" => () => client.GetReleasesAsync("test/repo", false),
                "branches" => () => client.GetBranchesAsync("test/repo"),
                "artwork" => () => client.GetModuleArtworkAsync("test/repo", "module"),
                "archive" => () => client.DownloadFileAsync("https://api.github.com/repos/test/repo/zipball/main", Path.Combine(_directory.FullName, "module.zip")),
                _ => () => client.RefreshRateLimitAsync()
            };

            // Act and assert: the operation fails with an HTTP error (the refresh swallows it), and either way the
            // exhausted quota has been recorded.
            if (operation == "rate_limit") await request();
            else await request.Should().ThrowAsync<HttpRequestException>();
            store.TryBeginExhaustionNotice().Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that when GitHub answers the startup token check with 403, 429, or 503, the stored account stays
        /// connected with its token and the authenticated quota notice is still raised.
        /// </summary>
        [Theory]
        [InlineData(403)]
        [InlineData(429)]
        [InlineData(503)]
        public async Task Startup_keeps_a_connected_account_when_verification_is_temporarily_unavailable(int status)
        {
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            appData.Data.GitHub.PersonalAccessToken = "test-token";
            appData.Data.GitHub.UserName = "test-user";
            var store = Store();
            using var http = new HttpClient(new Handler(_ => Response((HttpStatusCode)status, limit: 5000)));
            var account = new GitHubAccountStore(appData, store, NullLogger<GitHubAccountStore>.Instance, http);
            await account.InitializeAsync();
            account.GetState().IsConnected.Should().BeTrue();
            account.GetPersonalAccessToken().Should().Be("test-token");
            store.TryBeginExhaustionNotice()!.Authenticated.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a token rejected with 401 during the startup check is cleared, the account is reported as
        /// disconnected, and no quota notice is raised.
        /// </summary>
        [Fact]
        public async Task A_genuinely_invalid_token_is_still_cleared()
        {
            using var layout = new AslmFileSystemLayout();
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            appData.Data.GitHub.PersonalAccessToken = "invalid-test-token";
            var store = Store();
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var account = new GitHubAccountStore(appData, store, NullLogger<GitHubAccountStore>.Instance, http);
            await account.InitializeAsync();
            account.GetState().IsConnected.Should().BeFalse();
            account.GetPersonalAccessToken().Should().BeNull();
            store.TryBeginExhaustionNotice().Should().BeNull();
        }

        /// <summary>
        /// Verifies that downloading from a non-GitHub host sends no GitHub token and that the response's rate-limit
        /// headers, which describe a foreign service, do not touch the GitHub quota.
        /// </summary>
        [Fact]
        public async Task Foreign_download_headers_do_not_affect_GitHub_quota_and_receive_no_GitHub_token()
        {
            var store = Store();
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            appData.Data.GitHub.PersonalAccessToken = "test-token";
            using var http = new HttpClient(new Handler(request =>
            {
                request.Headers.Authorization.Should().BeNull();
                return Response(HttpStatusCode.OK);
            }));
            var account = new GitHubAccountStore(appData, store, NullLogger<GitHubAccountStore>.Instance);
            var client = new GitHubUpdateClient(store, account, http, new ModuleIconCache(_directory.FullName));
            await client.DownloadFileAsync("https://downloads.example/file", Path.Combine(_directory.FullName, "download"));
            store.TryBeginExhaustionNotice().Should().BeNull();
        }

        /// <summary>
        /// Verifies that the rate-limit endpoint's JSON body alone, without rate-limit response headers, can report
        /// an exhausted core quota and raise the notice.
        /// </summary>
        [Fact]
        public async Task Rate_limit_endpoint_body_can_report_exhaustion_without_response_headers()
        {
            var store = Store();
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    resources = new { core = new { limit = 60, remaining = 0, reset = Reset } }
                }))
            }));
            var account = new GitHubAccountStore(new AppDataStore(NullLogger<AppDataStore>.Instance),
                store, NullLogger<GitHubAccountStore>.Instance);
            var client = new GitHubUpdateClient(store, account, http, new ModuleIconCache(_directory.FullName));
            await client.RefreshRateLimitAsync();
            store.TryBeginExhaustionNotice().Should().NotBeNull();
        }

        /// <summary>
        /// Verifies that a request prepared by the client for a direct installer download carries the account
        /// token, and that tracking its response records the exhausted authenticated quota.
        /// </summary>
        [Fact]
        public void Direct_installer_requests_share_account_authorization_and_quota_tracking()
        {
            var store = Store();
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            appData.Data.GitHub.PersonalAccessToken = "test-token";
            var account = new GitHubAccountStore(appData, store, NullLogger<GitHubAccountStore>.Instance);
            using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected")));
            var client = new GitHubUpdateClient(store, account, http, new ModuleIconCache(_directory.FullName));
            store.ApplyAuthenticatedLimitHint(true);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/test/repo/zipball/main");
            client.PrepareApiRequest(request);
            request.Headers.Authorization!.Parameter.Should().Be("test-token");
            using var response = Response(HttpStatusCode.Forbidden, limit: 5000);
            client.TrackApiResponse(request, response);
            store.TryBeginExhaustionNotice()!.Authenticated.Should().BeTrue();
        }


        // Helpers

        /// <summary>
        /// Builds a response with the given status and rate-limit headers; by default an exhausted anonymous core
        /// quota (limit 60, none remaining) that resets in one hour.
        /// </summary>
        /// <param name="status">HTTP status of the response.</param>
        /// <param name="remaining">Value of <c>X-RateLimit-Remaining</c>.</param>
        /// <param name="resource">Value of <c>X-RateLimit-Resource</c>, the quota bucket.</param>
        /// <param name="limit">Value of <c>X-RateLimit-Limit</c>.</param>
        private HttpResponseMessage Response(HttpStatusCode status, int remaining = 0, string resource = "core", int limit = 60)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("[]") };
            response.Headers.Add("X-RateLimit-Limit", limit.ToString());
            response.Headers.Add("X-RateLimit-Remaining", remaining.ToString());
            response.Headers.Add("X-RateLimit-Reset", Reset.ToString());
            response.Headers.Add("X-RateLimit-Resource", resource);
            return response;
        }


        // Test doubles

        /// <summary>
        /// Clock that stays at a fixed moment until a test changes <see cref="Now"/>.
        /// </summary>
        private sealed class Clock : TimeProvider
        {
            /// <summary>
            /// Current fake time, starting at 2026-10-10 10:00 UTC.
            /// </summary>
            public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

            /// <summary>
            /// Returns <see cref="Now"/> instead of the system time.
            /// </summary>
            public override DateTimeOffset GetUtcNow() => Now;
        }

        /// <summary>
        /// Transport that answers every request through the supplied function, without network access.
        /// </summary>
        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            /// <summary>
            /// Returns the response produced by the test's function for the request.
            /// </summary>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(respond(request));
        }
    }
}
