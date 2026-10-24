// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the in-app notification lifecycle: publishing, download progress wiring,
/// dismissal, and the debounced persistence round-trip.
/// </summary>
public sealed class NotificationCenterTests : IDisposable
{
    private readonly UniqueAslmRoot _root = new("notifications");

    public void Dispose() => _root.Dispose();

    private NotificationCenter CreateCenter() => new(new InlineDispatcher());

    [Theory]
    [InlineData("Module", "ASLM-Chat", "module:aslm-chat")]
    [InlineData(" Engine ", " Ollama ", "engine:ollama")]
    public void BuildOperationKey_normalizes_source_parts(string kind, string id, string expected)
    {
        NotificationCenter.BuildOperationKey(kind, id).Should().Be(expected);
    }

    [Fact]
    public void PublishUpdateCandidate_adds_a_row_and_raises_the_toast_event()
    {
        var center = CreateCenter();
        AppNotification? toasted = null;
        center.NotificationPublished += (_, notification) => toasted = notification;

        center.PublishUpdateCandidate(new UpdateCandidate
        {
            TargetKind = "module",
            TargetId = "aslm-chat",
            Name = "ASLM Chat",
            RemoteVersion = "2.0"
        });

        center.Notifications.ToList().Should().ContainSingle();
        var row = center.Notifications.ToList().Single();
        row.Id.Should().Be("update:module:aslm-chat:2.0");
        row.Category.Should().Be(AppNotificationCategory.Updates);
        toasted.Should().NotBeNull().And.BeSameAs(row);
    }

    [Fact]
    public void PublishUpdateCandidate_deduplicates_the_same_candidate_version()
    {
        var center = CreateCenter();
        var candidate = new UpdateCandidate
        {
            TargetKind = "module", TargetId = "m", Name = "M", RemoteVersion = "2.0"
        };

        center.PublishUpdateCandidate(candidate);
        center.PublishUpdateCandidate(candidate);

        center.Notifications.ToList().Should().ContainSingle("the same version must not pile up rows");
    }

    [Fact]
    public async Task Download_flow_progresses_from_start_through_bytes_to_completion()
    {
        var center = CreateCenter();

        await center.StartDownloadAsync("resource:model", "Model", "Downloading model", "resource", "aslm-chat");
        var row = center.Notifications.ToList().Single(n => n.Id == "download:resource:model");
        row.IsInProgress.Should().BeTrue();
        var changes = 0;
        center.NotificationsChanged += (_, _) => changes++;

        center.ReportDownloadProgress("resource:model", new DownloadProgress(0.5, 512, 1024, "model"));
        row.IsInProgress.Should().BeTrue();
        row.StatusText.Should().NotBeNullOrWhiteSpace();

        center.CompleteDownload("resource:model", "Model installed");
        row.IsInProgress.Should().BeFalse();
        changes.Should().BeGreaterThan(0, "aggregate change events fire for the UI");
    }

    [Fact]
    public async Task Download_progress_for_unknown_operations_is_ignored()
    {
        var center = CreateCenter();

        center.ReportDownloadProgress("resource:ghost", new DownloadProgress(0.5, 1, 2, "ghost"));
        center.CompleteDownload("resource:ghost", "done");

        center.Notifications.ToList().Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FailDownload_marks_the_row_with_an_error_severity()
    {
        var center = CreateCenter();
        await center.StartDownloadAsync("resource:bad", "Bad", "Downloading bad", "resource", "aslm-chat");

        center.FailDownload("resource:bad", "Checksum failed");

        var row = center.Notifications.ToList().Single(n => n.Id == "download:resource:bad");
        row.IsInProgress.Should().BeFalse();
        row.Severity.Should().Be(AppNotificationSeverity.Error);
    }

    [Fact]
    public async Task Dismiss_removes_the_row_and_DismissAll_clears_the_list()
    {
        var center = CreateCenter();
        await center.StartDownloadAsync("resource:a", "A", "a", "resource", "aslm-chat");
        await center.StartDownloadAsync("resource:b", "B", "b", "resource", "aslm-chat");

        center.Dismiss(center.Notifications.Single(n => n.Id == "download:resource:a"));
        center.Notifications.ToList().Should().ContainSingle();

        center.DismissAll();
        center.Notifications.ToList().Should().BeEmpty();
    }

    [Fact]
    public async Task Notifications_persist_debounced_and_reload_on_the_next_session()
    {
        var center = CreateCenter();
        await center.InitializeAsync();
        center.PublishUpdateCandidate(new UpdateCandidate
        {
            TargetKind = "app", TargetId = "app", Name = "ASLM", RemoteVersion = "9.9.9"
        });
        await center.StartDownloadAsync("resource:model", "Model", "Downloading", "resource", "aslm-chat");
        center.CompleteDownload("resource:model", "done");

        var persisted = Path.Combine(_root.DataAppDir, "ASLM_Notifications.json");
        await TestWait.UntilAsync(() => File.Exists(persisted),
            because: "completed mutations schedule a debounced save");

        var reloaded = CreateCenter();
        await reloaded.InitializeAsync();

        reloaded.Notifications.ToList().Select(n => n.Id).Should()
            .Contain("update:app:app:9.9.9").And.Contain("download:resource:model");
    }

    [Fact]
    public async Task Persisted_history_is_capped_at_two_hundred_rows()
    {
        var center = CreateCenter();
        await center.InitializeAsync();
        for (var i = 0; i < 230; i++)
        {
            center.PublishUpdateCandidate(new UpdateCandidate
            {
                TargetKind = "module", TargetId = $"m-{i}", Name = $"M{i}", RemoteVersion = "1.0"
            });
        }
        center.Notifications.ToList().Should().HaveCount(200, "both the live list and the file are capped");

        var persisted = Path.Combine(_root.DataAppDir, "ASLM_Notifications.json");
        await TestWait.UntilAsync(() => File.Exists(persisted));
        await Task.Delay(1500);
        using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(persisted));

        document.RootElement.GetProperty("Notifications").GetArrayLength().Should()
            .BeLessThanOrEqualTo(200);
    }
}
