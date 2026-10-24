// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the app self-update staging contract consumed by the external patcher:
/// download, extraction, and the atomic <c>.aslm-update/pending.json</c> hand-off.
/// </summary>
public sealed class UpdateManagerAppUpdateTests
{
    [Fact]
    public async Task PrepareAppUpdateAsync_stages_payload_and_writes_pending_contract()
    {
        using var graph = new TestServiceGraph();
        SeedUpdateSource(graph, preserve: ["Data", "Modules"]);
        var candidate = BuildCandidate(graph, "9.9.9", BuildPayloadZip(graph, "ASLM.dll", "updated"));

        var prepared = await graph.Updates.PrepareAppUpdateAsync(candidate);

        prepared.Should().BeTrue();
        graph.Updates.HasPendingAppUpdate.Should().BeTrue();

        var pendingPath = Path.Combine(graph.Root.Root, ".aslm-update", "pending.json");
        File.Exists(pendingPath).Should().BeTrue();
        File.Exists(pendingPath + ".tmp").Should().BeFalse("the marker must be published atomically");

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(pendingPath));
        var root = document.RootElement;
        root.GetProperty("kind").GetString().Should().Be("app");
        root.GetProperty("version").GetString().Should().Be("9.9.9");
        var stagingPath = root.GetProperty("stagingPath").GetString()!;
        // The zip unpacks into a single wrapper folder, which must be collapsed away.
        Path.GetFileName(stagingPath).Should().Be("wrapper");
        root.GetProperty("targetRoot").GetString().Should().Be(graph.Root.Root);
        root.GetProperty("backupPath").GetString()!.Should().Contain(".aslm-update");
        root.GetProperty("preserve").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("Data", "Modules");
        DateTimeOffset.TryParse(root.GetProperty("createdUtc").GetString(), out _).Should().BeTrue();

        Directory.Exists(stagingPath).Should().BeTrue();
        File.ReadAllText(Path.Combine(stagingPath, "ASLM.dll")).Should().Be("updated");

        // The transfer completed through the notification center, not just silently.
        DownloadNotification(graph).StatusText.Should().Contain("prepared");
    }

    [Fact]
    public async Task PrepareAppUpdateAsync_returns_early_when_an_update_is_already_pending()
    {
        using var graph = new TestServiceGraph();
        SeedUpdateSource(graph);
        var candidate = BuildCandidate(graph, "9.9.9", BuildPayloadZip(graph, "ASLM.dll", "first"));
        (await graph.Updates.PrepareAppUpdateAsync(candidate)).Should().BeTrue();
        var requestsAfterFirst = graph.GitHubHttp.Requests.Count;

        // No HTTP route is registered for the second payload: an early return must not download it.
        var second = BuildCandidate(graph, "10.0.0", url: "https://update.test/second.zip");

        (await graph.Updates.PrepareAppUpdateAsync(second)).Should().BeTrue();

        graph.GitHubHttp.Requests.Count.Should().Be(requestsAfterFirst,
            "an already-pending update must satisfy new prepare requests without another download");
    }

    [Fact]
    public async Task PrepareAppUpdateAsync_without_configured_source_fails_and_publishes_no_pending_file()
    {
        using var graph = new TestServiceGraph();
        var candidate = new UpdateCandidate
        {
            TargetKind = "app",
            TargetId = "app",
            Name = "ASLM",
            RemoteVersion = "9.9.9",
            DownloadUrl = "https://update.test/never.zip"
        };

        var prepared = await graph.Updates.PrepareAppUpdateAsync(candidate);

        prepared.Should().BeFalse("no shipped update source means the update cannot stage");
        graph.Updates.HasPendingAppUpdate.Should().BeFalse();
        DownloadNotification(graph).StatusText
            .Should().Contain("not configured");
    }

    [Fact]
    public async Task PrepareAppUpdateAsync_with_corrupt_archive_cleans_staging_and_publishes_no_pending_file()
    {
        using var graph = new TestServiceGraph();
        SeedUpdateSource(graph);
        // Serve bytes that are not a zip at all.
        graph.GitHubHttp.OnBytes("https://update.test/corrupt.zip", "not a zip"u8.ToArray());
        var candidate = BuildCandidate(graph, "9.9.9", url: "https://update.test/corrupt.zip");

        var act = async () => await graph.Updates.PrepareAppUpdateAsync(candidate);

        await act.Should().ThrowAsync<Exception>();
        graph.Updates.HasPendingAppUpdate.Should().BeFalse();

        var stagingRoot = Path.Combine(graph.Root.Root, ".aslm-update", "staging");
        // The failed version directory is removed; an empty staging parent may remain.
        if (Directory.Exists(stagingRoot))
        {
            Directory.GetDirectories(stagingRoot).Should().BeEmpty("a failed package must not linger on disk");
        }
        DownloadNotification(graph).StatusText
            .Should().Contain("failed");
    }

    private static AppNotification DownloadNotification(TestServiceGraph graph) =>
        graph.Notifications.Notifications.ToList().Single(n => n.Id == "download:app-update:app");

    private static void SeedUpdateSource(TestServiceGraph graph, params string[] preserve)
    {
        var config = new AppUpdateSourceConfig
        {
            Source = new ModuleSource { Type = "github", Repo = "NEXTGGTECH/ASLM" },
            Assets = new Dictionary<string, string> { ["windows-amd64"] = "ASLM-windows-amd64.zip" },
            Preserve = preserve.ToList()
        };
        config.Normalize();
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        graph.Root.WriteDataAppFile("ASLM_UpdateSource.json", json);
    }

    private static string BuildPayloadZip(TestServiceGraph graph, string fileName, string content)
    {
        var url = $"https://update.test/{Guid.NewGuid():N}.zip";
        var zipPath = Path.Combine(graph.Root.Root, $"{Guid.NewGuid():N}.zip");
        TestArchives.WriteZip(zipPath, new TestArchives.Entry($"wrapper/{fileName}", content));
        graph.GitHubHttp.OnBytes(url, File.ReadAllBytes(zipPath));
        return url;
    }

    private static UpdateCandidate BuildCandidate(TestServiceGraph graph, string version, string? url = null) =>
        new()
        {
            TargetKind = "app",
            TargetId = "app",
            Name = "ASLM",
            RemoteVersion = version,
            DownloadUrl = url ?? BuildPayloadZip(graph, "ASLM.dll", "payload")
        };
}
