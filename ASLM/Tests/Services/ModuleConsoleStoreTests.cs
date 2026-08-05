// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleConsoleStoreTests
{
    [Fact]
    public void AppendOverviewLine_appears_in_unified_overview()
    {
        var store = new ModuleConsoleStore();
        var module = ModuleConfigBuilder.Create();

        store.EnsureModule(module);
        store.AppendOverviewLine(module, "hello overview");

        var lines = store.GetUnifiedOverviewLines([module.SourcePath]);
        lines.Should().Contain(line => line.Contains("hello overview", StringComparison.Ordinal));
    }

    [Fact]
    public void StartProcessSession_and_AppendProcessLine_capture_output()
    {
        var store = new ModuleConsoleStore();
        var module = ModuleConfigBuilder.Create();
        using var process = Process.GetCurrentProcess();
        var command = new ModuleCommand { Name = "Install", Description = "Install dependencies" };

        var handle = store.StartProcessSession(
            module,
            command,
            stage: "Install",
            commandLine: "pip install package",
            process,
            isTrackedProcess: true);

        store.AppendProcessLine(handle, "Collecting package");

        var snapshot = store.GetSnapshot();
        snapshot.Should().ContainSingle(m => m.SourcePath == module.SourcePath);
        store.GetSessionText(module.SourcePath, handle.SessionId)
            .Should()
            .Contain("Collecting package");
    }

    [Fact]
    public void CompleteProcessSession_marks_session_not_running()
    {
        var store = new ModuleConsoleStore();
        var module = ModuleConfigBuilder.Create();
        using var process = Process.GetCurrentProcess();
        var command = new ModuleCommand { Name = "Run" };

        var handle = store.StartProcessSession(
            module,
            command,
            stage: "Run",
            commandLine: "run",
            process,
            isTrackedProcess: true);

        store.CompleteProcessSession(handle, exitCode: 0);

        var session = store.GetSnapshot()
            .Single()
            .Sessions
            .Single(s => s.Id == handle.SessionId);

        session.IsRunning.Should().BeFalse();
        session.ExitCode.Should().Be(0);
    }
}
