// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

public sealed class GitHubRateLimitNoticeTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ASLM-GitHubQuota-");
    private readonly Clock _clock = new();
    private string StatePath => Path.Combine(_directory.FullName, "limits.json");
    private long Reset => _clock.Now.AddHours(1).ToUnixTimeSeconds();
    private GitHubRateLimitStore Store() => new(NullLogger<GitHubRateLimitStore>.Instance, StatePath, _clock);
    public void Dispose() => _directory.Delete(recursive: true);

    [Theory]
    [InlineData(200)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Exhaustion_is_shown_once_per_window_even_after_restarting(int status)
    {
        var store = Store();
        using var response = Response((HttpStatusCode)status);
        store.UpdateFromResponse(response, authenticated: false);
        var notice = store.TryBeginExhaustionNotice();
        notice.Should().NotBeNull();
        store.TryBeginExhaustionNotice().Should().BeNull();
        await store.EndExhaustionNoticeAsync(notice!, acknowledged: true);
        store.UpdateFromResponse(response, authenticated: false);
        store.TryBeginExhaustionNotice().Should().BeNull();

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

    [Fact]
    public async Task Navigating_away_without_pressing_a_button_does_not_acknowledge_the_notice()
    {
        var store = Store();
        store.UpdateFromHeaders(60, 0, Reset);
        var first = store.TryBeginExhaustionNotice()!;
        await store.EndExhaustionNoticeAsync(first, acknowledged: false);
        var next = store.TryBeginExhaustionNotice();
        next.Should().NotBeNull();
        await store.EndExhaustionNoticeAsync(first, acknowledged: true);
        store.TryBeginExhaustionNotice().Should().BeNull("an obsolete view must not release another view's reservation");
        await store.EndExhaustionNoticeAsync(next!, acknowledged: false);
        store.TryBeginExhaustionNotice().Should().NotBeNull();
    }

    [Fact]
    public async Task Parallel_responses_reserve_only_one_dialog_and_out_of_order_responses_cannot_restore_budget()
    {
        var store = Store();
        var notices = new ConcurrentBag<GitHubRateLimitStore.ExhaustionNotice>();
        store.StateChanged += (_, _) => { if (store.TryBeginExhaustionNotice() is { } notice) notices.Add(notice); };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.UpdateFromHeaders(60, 0, Reset))));
        notices.Should().ContainSingle();
        store.UpdateFromHeaders(60, 20, Reset);
        store.GetSnapshot().Remaining.Should().Be(0);
        await store.EndExhaustionNoticeAsync(notices.Single(), acknowledged: true);
        store.UpdateFromHeaders(60, 0, Reset - 10);
        store.UpdateFromHeaders(60, 0, Reset + 10);
        notices.Should().ContainSingle("the acknowledged window has not reset yet");
    }

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

    [Fact]
    public async Task Connecting_changes_quota_scope_without_replaying_anonymous_responses()
    {
        var store = Store();
        var anonymousReset = Reset;
        store.UpdateFromHeaders(60, 0, anonymousReset);
        await store.EndExhaustionNoticeAsync(store.TryBeginExhaustionNotice()!, acknowledged: true);
        store.ApplyAuthenticatedLimitHint(true);
        store.TryBeginExhaustionNotice().Should().BeNull();
        store.UpdateFromHeaders(60, 0, anonymousReset, authenticated: false);
        store.GetSnapshot().Limit.Should().Be(5000);
        store.TryBeginExhaustionNotice().Should().BeNull();
        store.UpdateFromHeaders(5000, 0, anonymousReset - 100, authenticated: true);
        var accountNotice = store.TryBeginExhaustionNotice();
        accountNotice!.Authenticated.Should().BeTrue();
        await store.EndExhaustionNoticeAsync(accountNotice, acknowledged: true);
        store.ApplyAuthenticatedLimitHint(false);
        store.UpdateFromHeaders(60, 0, anonymousReset, authenticated: false);
        store.TryBeginExhaustionNotice().Should().BeNull("disconnecting does not undo the anonymous acknowledgment");
    }

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

    [Theory]
    [InlineData("releases")]
    [InlineData("branches")]
    [InlineData("artwork")]
    [InlineData("archive")]
    [InlineData("rate_limit")]
    public async Task All_GitHub_client_paths_report_quota_exhaustion_before_returning_an_HTTP_error(string operation)
    {
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
        if (operation == "rate_limit") await request();
        else await request.Should().ThrowAsync<HttpRequestException>();
        store.TryBeginExhaustionNotice().Should().NotBeNull();
    }

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

    private HttpResponseMessage Response(HttpStatusCode status, int remaining = 0, string resource = "core", int limit = 60)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("[]") };
        response.Headers.Add("X-RateLimit-Limit", limit.ToString());
        response.Headers.Add("X-RateLimit-Remaining", remaining.ToString());
        response.Headers.Add("X-RateLimit-Reset", Reset.ToString());
        response.Headers.Add("X-RateLimit-Resource", resource);
        return response;
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
