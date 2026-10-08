// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;
using ASLM.Localization;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleConsoleStoreTests
{
    [Fact]
    public void Maintenance_output_is_isolated_retained_and_not_duplicated_by_nested_install_update()
    {
        var store = new ModuleConsoleStore();
        var module = ModuleConfigBuilder.Create();
        var other = ModuleConfigBuilder.Create(id: "other");
        other.SourcePath = module.SourcePath + ".other";
        var log = store.CreateMaintenanceLog(module, null, reset: true);
        var firstKey = store.GetMaintenanceSnapshot(module.SourcePath).SessionKey;
        log.Report("download");
        store.CreateMaintenanceLog(module, log, reset: true).Report("setup");
        store.CreateMaintenanceLog(other, null, reset: true).Report("other module");

        store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().Be($"download{Environment.NewLine}setup");
        store.GetMaintenanceSnapshot(other.SourcePath).Text.Should().Be("other module");
        store.CreateMaintenanceLog(module, null, reset: false).Report("finished");
        store.GetMaintenanceSnapshot(module.SourcePath).SessionKey.Should().Be(firstKey);
        store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().EndWith("finished");
        store.CreateMaintenanceLog(module, null, reset: true);
        store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().BeEmpty();
    }

    [Fact]
    public void Parent_activity_survives_nested_stop_and_out_of_order_disposal()
    {
        var store = new ModuleConsoleStore();
        var path = ModuleConfigBuilder.Create().SourcePath;
        var update = store.BeginActivity(path, ModuleActivity.Updating);
        var stop = store.BeginActivity(path, ModuleActivity.Stopping);
        store.GetMaintenanceSnapshot(path).Activity.Should().Be(ModuleActivity.Updating);
        update.Dispose();
        store.GetMaintenanceSnapshot(path).Activity.Should().Be(ModuleActivity.Stopping);
        update.Dispose();
        stop.Dispose();
        store.GetMaintenanceSnapshot(path).Activity.Should().BeNull();
    }

    [Fact]
    public void Maintenance_snapshot_tracks_real_process_completion()
    {
        var store = new ModuleConsoleStore();
        var module = ModuleConfigBuilder.Create();
        using var process = Process.GetCurrentProcess();
        var session = store.StartProcessSession(module, new ModuleCommand { Name = "Run" }, "Run", "process", process, true);
        store.GetMaintenanceSnapshot(module.SourcePath).IsRunning.Should().BeTrue();
        store.CompleteProcessSession(session, 0);
        store.GetMaintenanceSnapshot(module.SourcePath).IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Module_info_resolves_registry_process_and_transient_states()
    {
        ASLM.Pages.ModuleInfo.ResolveStateKey(false, false, null).Should().Be(LocalizationKeys.ModuleInfo_NotInstalled);
        ASLM.Pages.ModuleInfo.ResolveStateKey(true, false, null).Should().Be(LocalizationKeys.ModuleInfo_Installed);
        ASLM.Pages.ModuleInfo.ResolveStateKey(true, true, null).Should().Be(LocalizationKeys.Home_Metric_Running);
        ASLM.Pages.ModuleInfo.ResolveStateKey(true, true, ModuleActivity.Stopping).Should().Be(LocalizationKeys.Home_Module_Stopping);
        ASLM.Pages.ModuleInfo.ResolveStateKey(true, false, ModuleActivity.Restarting).Should().Be(LocalizationKeys.Modules_Restarting);
        ASLM.Pages.ModuleInfo.ResolveStateKey(true, true, ModuleActivity.Updating).Should().Be(LocalizationKeys.Modules_Updating);
        ASLM.Pages.ModuleInfo.ResolveStateKey(false, false, ModuleActivity.Installing).Should().Be(LocalizationKeys.SetupWizard_Installing);
    }

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
