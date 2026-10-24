// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Services.Internal;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Verifies the real process infrastructure: snapshot capture of live processes and the
/// breakaway starter used for launcher restarts.
/// </summary>
public sealed class ProcessInfrastructureTests
{
    [Fact]
    public void Snapshot_contains_the_running_test_host_process()
    {
        var reader = new ProcessSnapshotReader();
        var snapshot = reader.GetSnapshot();

        using var current = Process.GetCurrentProcess();
        snapshot.Should().Contain(entry => entry.ProcessId == current.Id);
        // A real snapshot also observes the parent chain: at least one entry with a parent.
        snapshot.Should().Contain(entry => entry.ParentProcessId > 0);
    }

    [Fact]
    public async Task Breakaway_starter_launches_a_detached_process_that_runs_to_completion()
    {
        var marker = Path.Combine(Path.GetTempPath(), "ASLM-Tests", $"breakaway-{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);

        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var started = DetachedProcessStarter.TryStartBreakawayProcess(
            powerShell,
            Path.GetTempPath(),
            ["-NoProfile", "-NonInteractive", "-Command",
                $"Set-Content -Path '{marker}' -Value done"]);

        started.Should().BeTrue("a fresh process must launch outside any job of this host");
        await TestWait.UntilAsync(() => File.Exists(marker), timeout: TimeSpan.FromSeconds(15));
        (await File.ReadAllTextAsync(marker)).Trim().Should().Be("done");
    }
}
