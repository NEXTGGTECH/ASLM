// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ASLM.Models;

namespace ASLM.Tests.Services;

public sealed class GitHubUpdateClientTests : IDisposable
{
    private readonly DirectoryInfo _iconDirectory = Directory.CreateTempSubdirectory("ASLM-IconCache-");

    public void Dispose() => _iconDirectory.Delete(recursive: true);
    [Theory]
    [InlineData("release", "v1.0", 1)]
    [InlineData("pre-release", "v2.0-beta", 2)]
    public async Task Catalog_display_picker_and_install_target_share_the_default_channel(string channel, string expected, int count)
    {
        using var http = new HttpClient(new RepositoryHandler());
        var client = CreateClient(http);
        var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, client, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
        var module = new ModuleConfig { Id = "module", Version = "0.1", Source = new() { Type = "github", Repo = "owner/repo" } };
        UpdateManager.ApplyCatalogDefaults(module, false, channel);
        var version = await manager.GetModuleCatalogVersionAsync(module, false);
        var options = await manager.GetModuleReleaseCandidatesAsync(module);
        var install = await manager.ResolveModuleInstallCandidateAsync(module);
        version.Should().Be(expected);
        options.Should().HaveCount(count);
        install!.RemoteVersion.Should().Be(version);
        module.Version.Should().Be("0.1", "catalog presentation must not overwrite manifest version");
        module.Update.SelectedReleaseTag = "v1.0";
        (await manager.GetModuleCatalogVersionAsync(module, false)).Should().Be("v1.0");
        (await manager.GetModuleCatalogVersionAsync(module, true)).Should().Be("0.1");
    }

    [Fact]
    public async Task Artwork_reads_the_current_manifest_then_its_icon_from_the_default_branch_and_reuses_cache()
    {
        var handler = new RepositoryHandler();
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var first = await client.GetModuleArtworkAsync("owner/repo", "module");
        var second = await client.GetModuleArtworkAsync("owner/repo", "module");
        first.Icon.Should().Equal(RepositoryHandler.Icon);
        second.Icon.Should().Equal(first.Icon!);
        handler.Requests.Should().Equal(
            "https://api.github.com/repos/owner/repo/contents/ASLM_Module.json",
            "https://api.github.com/repos/owner/repo/contents/assets/new%20icon.png");
    }

    [Fact]
    public async Task Artwork_cache_survives_a_new_client_and_checks_that_the_file_still_exists()
    {
        using var firstHttp = new HttpClient(new RepositoryHandler());
        await CreateClient(firstHttp).GetModuleArtworkAsync("owner/repo", "module");
        var handler = new RepositoryHandler();
        using var nextHttp = new HttpClient(handler);
        var nextClient = CreateClient(nextHttp);
        (await nextClient.GetModuleArtworkAsync("owner/repo", "module")).Icon.Should().Equal(RepositoryHandler.Icon);
        handler.Requests.Should().BeEmpty();

        using var index = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json")));
        index.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        var entry = index.RootElement[0];
        entry.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo("moduleId", "downloadedAt", "fileName");
        entry.GetProperty("moduleId").GetString().Should().Be("module");
        entry.GetProperty("downloadedAt").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);
        var fileName = entry.GetProperty("fileName").GetString()!;
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(fileName), "N", out _).Should().BeTrue();
        File.Delete(Path.Combine(_iconDirectory.FullName, fileName));
        (await nextClient.GetModuleArtworkAsync("owner/repo", "module")).Icon.Should().Equal(RepositoryHandler.Icon);
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Icon_expires_after_exactly_seven_days_without_refreshing_its_age_on_reads()
    {
        var clock = new CacheClock();
        var cache = new ModuleIconCache(_iconDirectory.FullName, clock);
        var downloads = 0;
        Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken _) =>
            Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([(byte)++downloads], ".png"));
        await cache.GetOrDownloadAsync("module", Download);
        var original = ReadIconIndex().Single();
        clock.Now += TimeSpan.FromDays(6);
        (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([1]);
        ReadIconIndex().Single().DownloadedAt.Should().Be(original.DownloadedAt);
        clock.Now += TimeSpan.FromDays(1);
        (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([2]);
        File.Exists(Path.Combine(_iconDirectory.FullName, original.FileName)).Should().BeFalse();
        ReadIconIndex().Single().FileName.Should().NotBe(original.FileName);
    }

    [Fact]
    public async Task Cleanup_and_writes_enforce_the_256_icon_limit_by_download_time()
    {
        var clock = new CacheClock();
        var entries = new List<ModuleIconCache.Entry>();
        for (var i = 0; i < 257; i++)
        {
            var name = Guid.NewGuid().ToString("N") + ".png";
            entries.Add(new("module-" + i, clock.Now.AddMinutes(i - 257), name));
            await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, name), [1]);
        }
        await WriteIconIndex(entries);
        var cache = new ModuleIconCache(_iconDirectory.FullName, clock);
        await cache.PruneAsync();
        ReadIconIndex().Should().HaveCount(256).And.NotContain(entry => entry.ModuleId == "module-0");
        File.Exists(Path.Combine(_iconDirectory.FullName, entries[0].FileName)).Should().BeFalse();
        await cache.GetOrDownloadAsync("new", _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([2], ".png")));
        ReadIconIndex().Should().HaveCount(256).And.NotContain(entry => entry.ModuleId == "module-1");
        _iconDirectory.GetFiles("*.png").Should().HaveCount(256);
        ReadIconIndex().Select(entry => entry.FileName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Cleanup_removes_expired_missing_and_orphan_icons_but_never_follows_index_paths()
    {
        var clock = new CacheClock();
        var cacheDirectory = Path.Combine(_iconDirectory.FullName, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var protectedFile = Path.Combine(_iconDirectory.FullName, "protected.png");
        await File.WriteAllBytesAsync(protectedFile, [9]);
        var expired = Guid.NewGuid().ToString("N") + ".png";
        var orphan = Guid.NewGuid().ToString("N") + ".png";
        await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, expired), [1]);
        await File.WriteAllBytesAsync(Path.Combine(cacheDirectory, orphan), [1]);
        await File.WriteAllTextAsync(Path.Combine(cacheDirectory, "index.json"), JsonSerializer.Serialize(new ModuleIconCache.Entry[]
        {
            new("expired", clock.Now - TimeSpan.FromDays(7), expired),
            new("missing", clock.Now, Guid.NewGuid().ToString("N") + ".png"),
            new("unsafe", clock.Now, "../protected.png")
        }));
        await new ModuleIconCache(cacheDirectory, clock).PruneAsync();
        Directory.GetFiles(cacheDirectory).Select(Path.GetFileName).Should().Equal("index.json");
        (await File.ReadAllTextAsync(Path.Combine(cacheDirectory, "index.json"))).Trim().Should().Be("[]");
        (await File.ReadAllBytesAsync(protectedFile)).Should().Equal([9]);
    }

    [Fact]
    public async Task Concurrent_requests_for_one_id_download_once_and_canceled_downloads_leave_no_record()
    {
        var cache = new ModuleIconCache(_iconDirectory.FullName);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new([1], ".png");
        }
        var first = cache.GetOrDownloadAsync("module", Download);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.GetOrDownloadAsync("module", Download);
        release.SetResult();
        await Task.WhenAll(first, second);
        calls.Should().Be(1);
        using var canceled = new CancellationTokenSource();
        Func<Task> canceledDownload = () => cache.GetOrDownloadAsync("canceled", ct =>
        {
            canceled.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ModuleIconCache.DownloadedIcon?>(null);
        }, canceled.Token);
        await canceledDownload.Should().ThrowAsync<OperationCanceledException>();
        ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("module");
    }

    [Fact]
    public async Task Corrupt_index_is_rebuilt_and_unwritable_cache_does_not_discard_downloaded_artwork()
    {
        await File.WriteAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json"), "[broken");
        var cache = new ModuleIconCache(_iconDirectory.FullName);
        Task<ModuleIconCache.DownloadedIcon?> Download(CancellationToken _) =>
            Task.FromResult<ModuleIconCache.DownloadedIcon?>(new([3], ".png"));
        (await cache.GetOrDownloadAsync("module", Download)).Should().Equal([3]);
        ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("module");
        var blockedDirectory = Path.Combine(_iconDirectory.FullName, "not-a-directory");
        await File.WriteAllTextAsync(blockedDirectory, "keep");
        (await new ModuleIconCache(blockedDirectory).GetOrDownloadAsync("module", Download)).Should().Equal([3]);
        (await File.ReadAllTextAsync(blockedDirectory)).Should().Be("keep");
    }

    private List<ModuleIconCache.Entry> ReadIconIndex() => JsonSerializer.Deserialize<List<ModuleIconCache.Entry>>(
        File.ReadAllText(Path.Combine(_iconDirectory.FullName, "index.json")))!;

    private Task WriteIconIndex(List<ModuleIconCache.Entry> entries) =>
        File.WriteAllTextAsync(Path.Combine(_iconDirectory.FullName, "index.json"), JsonSerializer.Serialize(entries));

    private sealed class CacheClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("../icon.png")]
    [InlineData("/icon.png")]
    [InlineData("https://elsewhere/icon.png")]
    [InlineData("C:/icon.png")]
    public void Remote_icon_paths_cannot_escape_the_repository(string path)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { id = "module", icon = path }));
        var read = () => GitHubUpdateClient.ReadModuleIconPath(json.RootElement, "module");
        read.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Missing_remote_icon_does_not_prevent_resolving_the_selected_release()
    {
        using var http = new HttpClient(new RepositoryHandler(failIcon: true));
        var client = CreateClient(http);
        var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, client, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
        var module = new ModuleConfig { Id = "module", Source = new() { Type = "github", Repo = "owner/repo" } };
        (await manager.GetModuleCatalogVersionAsync(module, false)).Should().Be("v1.0");
        (await manager.GetModuleCatalogArtworkAsync(module)).Should().BeNull();
    }

    [Fact]
    public async Task Cached_artwork_is_available_while_release_requests_are_still_pending()
    {
        var cache = new ModuleIconCache(_iconDirectory.FullName);
        foreach (var id in new[] { "module", "other" })
            await cache.GetOrDownloadAsync(id, _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new(RepositoryHandler.Icon, ".png")));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
        {
            request.RequestUri!.AbsolutePath.Should().EndWith("/releases", "cached icons must not contact the repository");
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        using var http = new HttpClient(handler);
        var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, CreateClient(http), null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
        var modules = new[] { CreateModule("module", "owner/repo"), CreateModule("other", "owner/other") };
        var versions = modules.Select(module => manager.GetModuleCatalogVersionAsync(module, false, timeout.Token)).ToArray();
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            var icons = await Task.WhenAll(modules.Select(module => manager.GetModuleCatalogArtworkAsync(module, timeout.Token)))
                .WaitAsync(timeout.Token);
            foreach (var icon in icons) icon!.Icon.Should().Equal(RepositoryHandler.Icon);
            versions.Should().OnlyContain(version => !version.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(versions); }
        (await Task.WhenAll(versions)).Should().OnlyContain(version => version == "v1.0");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_slow_icon_does_not_block_versions_or_other_cached_icons_and_cancellation_does_not_cache_it()
    {
        await new ModuleIconCache(_iconDirectory.FullName).GetOrDownloadAsync("other",
            _ => Task.FromResult<ModuleIconCache.DownloadedIcon?>(new(RepositoryHandler.Icon, ".png")));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".png"))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        });
        using var http = new HttpClient(handler);
        var manager = new UpdateManager(null!, null!, null!, null!, null!, null!, null!, null!, CreateClient(http), null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());
        var slow = CreateModule("module", "owner/repo");
        var cached = CreateModule("other", "owner/other");
        var icon = manager.GetModuleCatalogArtworkAsync(slow, canceled.Token);
        try
        {
            await entered.Task.WaitAsync(timeout.Token);
            (await manager.GetModuleCatalogVersionAsync(slow, false, timeout.Token)).Should().Be("v1.0");
            (await manager.GetModuleCatalogArtworkAsync(cached, timeout.Token))!.Icon.Should().Equal(RepositoryHandler.Icon);
            icon.IsCompleted.Should().BeFalse();
        }
        finally
        {
            canceled.Cancel();
            var wait = async () => await icon;
            await wait.Should().ThrowAsync<OperationCanceledException>();
        }
        ReadIconIndex().Should().ContainSingle().Which.ModuleId.Should().Be("other");
    }

    [Fact]
    public async Task Metadata_requests_only_wait_for_the_same_repository_and_endpoint()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new RepositoryHandler(beforeResponse: async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/repos/owner/repo/releases")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        });
        using var http = new HttpClient(handler);
        var client = CreateClient(http);
        var first = client.GetReleasesAsync("owner/repo", false, ct: timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var duplicate = client.GetReleasesAsync("OWNER/REPO", true, ct: timeout.Token);
        try
        {
            (await client.GetReleasesAsync("owner/other", false, ct: timeout.Token)).Should().ContainSingle();
            (await client.GetBranchesAsync("owner/repo", ct: timeout.Token)).Should().BeEmpty();
            first.IsCompleted.Should().BeFalse();
            duplicate.IsCompleted.Should().BeFalse();
            using var canceled = new CancellationTokenSource();
            var waiting = client.GetReleasesAsync("owner/repo", false, ct: canceled.Token);
            canceled.Cancel();
            var wait = async () => await waiting;
            await wait.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { release.TrySetResult(); await Task.WhenAll(first, duplicate); }
        (await first).Should().ContainSingle();
        (await duplicate).Should().HaveCount(2);
        handler.Requests.Should().HaveCount(3, "same-repository requests must still share one fetch");
    }

    [Fact]
    public async Task A_cache_hit_only_reads_its_icon_without_rewriting_the_index_or_pruning_other_files()
    {
        var clock = new CacheClock();
        var valid = Guid.NewGuid().ToString("N") + ".png";
        var expired = Guid.NewGuid().ToString("N") + ".png";
        await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, valid), [1]);
        await File.WriteAllBytesAsync(Path.Combine(_iconDirectory.FullName, expired), [2]);
        await WriteIconIndex([new("module", clock.Now, valid), new("old", clock.Now - TimeSpan.FromDays(8), expired)]);
        var index = Path.Combine(_iconDirectory.FullName, "index.json");
        var before = await File.ReadAllTextAsync(index);
        // Reading should still work when the index cannot be replaced or written.
        using (var readOnly = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var bytes = await new ModuleIconCache(_iconDirectory.FullName, clock).GetOrDownloadAsync("module",
                _ => throw new InvalidOperationException("A cache hit must not download anything."));
            bytes.Should().Equal([1]);
        }
        (await File.ReadAllTextAsync(index)).Should().Be(before);
        File.Exists(Path.Combine(_iconDirectory.FullName, expired)).Should().BeTrue();
        await new ModuleIconCache(_iconDirectory.FullName, clock).PruneAsync();
        File.Exists(Path.Combine(_iconDirectory.FullName, expired)).Should().BeFalse();
    }

    private static ModuleConfig CreateModule(string id, string repo) =>
        new() { Id = id, Version = "0.1", Source = new() { Type = "github", Repo = repo } };

    private sealed class RepositoryHandler(bool failIcon = false,
        Func<HttpRequestMessage, CancellationToken, Task>? beforeResponse = null) : HttpMessageHandler
    {
        public static readonly byte[] Icon = [137, 80, 78, 71];
        public ConcurrentQueue<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request.RequestUri!.AbsoluteUri);
            if (beforeResponse != null) await beforeResponse(request, cancellationToken);
            HttpContent content;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/repos/owner/repo/releases":
                case "/repos/owner/other/releases":
                    content = new StringContent("""
                        [{"tag_name":"v1.0","prerelease":false,"zipball_url":"https://example.test/v1.zip"},
                         {"tag_name":"v2.0-beta","prerelease":true,"zipball_url":"https://example.test/v2.zip"}]
                        """);
                    break;
                case "/repos/owner/repo/branches":
                    content = new StringContent("[]");
                    break;
                case "/repos/owner/repo/contents/ASLM_Module.json":
                    request.Headers.Accept.Should().Contain(value => value.MediaType == "application/vnd.github.raw+json");
                    content = new StringContent("""{"id":"module","icon":"assets/new icon.png"}""");
                    break;
                case "/repos/owner/repo/contents/assets/new%20icon.png":
                    if (failIcon) return new HttpResponseMessage(HttpStatusCode.NotFound);
                    content = new ByteArrayContent(Icon);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected test request: " + request.RequestUri.AbsoluteUri);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
    [Fact]
    public async Task GetReleasesAsync_returns_empty_for_blank_repo()
    {
        var client = CreateClient();

        var releases = await client.GetReleasesAsync("  ", includePrerelease: true);

        releases.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBranchesAsync_returns_empty_for_blank_repo()
    {
        var client = CreateClient();

        var branches = await client.GetBranchesAsync(string.Empty);

        branches.Should().BeEmpty();
    }

    [Fact]
    public async Task GetLatestReleaseAsync_returns_null_for_blank_repo()
    {
        var client = CreateClient();

        var release = await client.GetLatestReleaseAsync(string.Empty, includePrerelease: false);

        release.Should().BeNull();
    }

    private GitHubUpdateClient CreateClient(HttpClient? http = null)
    {
        var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
        var rateLimitStore = new GitHubRateLimitStore(NullLogger<GitHubRateLimitStore>.Instance);
        var accountStore = new GitHubAccountStore(
            appData,
            rateLimitStore,
            NullLogger<GitHubAccountStore>.Instance);
        var icons = new ModuleIconCache(_iconDirectory.FullName);
        return http == null ? new GitHubUpdateClient(rateLimitStore, accountStore, icons)
            : new GitHubUpdateClient(rateLimitStore, accountStore, http, icons);
    }
}
