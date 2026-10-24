// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the module artwork disk cache: request coalescing, expiry, size bounds, and
/// survival across cache instances.
/// </summary>
public sealed class ModuleIconCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ASLM-Tests", $"icons-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ModuleIconCache CreateCache(FakeTimeProvider? clock = null) =>
        new(_directory, clock, NullLogger<ModuleIconCache>.Instance);

    [Fact]
    public async Task Icons_download_once_and_are_served_from_disk_afterwards()
    {
        var cache = CreateCache();
        var downloads = 0;

        var first = await cache.GetOrDownloadAsync("module-a", _ =>
        {
            downloads++;
            return Task.FromResult<ModuleIconCache.DownloadedIcon?>(new("icon"u8.ToArray(), ".png"));
        });
        var second = await cache.GetOrDownloadAsync("module-a", _ =>
            throw new InvalidOperationException("the cached icon must not download again"));

        downloads.Should().Be(1);
        second.Should().Equal(first!);

        // A fresh instance reads the same entry from the index on disk.
        var reloaded = CreateCache();
        var third = await reloaded.GetOrDownloadAsync("module-a", _ =>
            throw new InvalidOperationException("the persisted icon must survive restarts"));
        third.Should().Equal(first!);
    }

    [Fact]
    public async Task Expired_entries_download_again()
    {
        var clock = new FakeTimeProvider();
        var cache = CreateCache(clock);
        var downloads = 0;
        ModuleIconCache.DownloadedIcon Produce() =>
            new(System.Text.Encoding.UTF8.GetBytes($"icon-{downloads}"), ".png");

        await cache.GetOrDownloadAsync("module-b", _ =>
        {
            downloads++;
            return Task.FromResult<ModuleIconCache.DownloadedIcon?>(Produce());
        });
        clock.Advance(TimeSpan.FromDays(30));
        await cache.GetOrDownloadAsync("module-b", _ =>
        {
            downloads++;
            return Task.FromResult<ModuleIconCache.DownloadedIcon?>(Produce());
        });

        downloads.Should().Be(2, "a month-old entry must be considered stale");
    }

    [Fact]
    public async Task Oversized_icons_are_returned_but_not_cached()
    {
        var cache = CreateCache();
        var oversized = new byte[9 * 1024 * 1024];

        var result = await cache.GetOrDownloadAsync("module-big", _ =>
            Task.FromResult<ModuleIconCache.DownloadedIcon?>(new(oversized, ".png")));

        result.Should().Equal(oversized);
        if (Directory.Exists(_directory))
        {
            Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
                .Should().NotContain(f => new FileInfo(f).Length > 1024, "no oversized payload is persisted");
        }
    }

    [Fact]
    public async Task Failed_downloads_return_null_without_poisoning_the_cache()
    {
        var cache = CreateCache();

        var result = await cache.GetOrDownloadAsync("module-ghost", _ =>
            Task.FromResult<ModuleIconCache.DownloadedIcon?>(null));

        result.Should().BeNull();
        // The next request may still try again.
        var retry = await cache.GetOrDownloadAsync("module-ghost", _ =>
            Task.FromResult<ModuleIconCache.DownloadedIcon?>(new("ok"u8.ToArray(), ".png")));
        retry.Should().Equal("ok"u8.ToArray());
    }
}
