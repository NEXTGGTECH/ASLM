// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;
using ASLM.Localization;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the shared module console store: maintenance logs and activities, process sessions with their
    /// rewritten unfinished lines and display limit, the overview buffer, module snapshots and the module status label
    /// derived from store state.
    /// </summary>
    public sealed class ModuleConsoleStoreTests
    {
        // Maintenance logs and activities

        /// <summary>
        /// Verifies that maintenance output is kept per module, that a nested step receiving the existing log does not
        /// duplicate lines, that a non-reset log continues the same session and that a reset starts an empty one.
        /// </summary>
        [Fact]
        public void Maintenance_output_is_isolated_retained_and_not_duplicated_by_nested_install_update()
        {
            // Two modules with distinct manifest paths, and the first maintenance session of the first module.
            var store = new ModuleConsoleStore();
            var module = ModuleConfigBuilder.Create();
            var other = ModuleConfigBuilder.Create(id: "other");
            other.SourcePath = module.SourcePath + ".other";
            var log = store.CreateMaintenanceLog(module, null, reset: true);
            var firstKey = store.GetMaintenanceSnapshot(module.SourcePath).SessionKey;

            // A nested step gets the existing log back, so "setup" is appended once; the other module has its own text.
            log.Report("download");
            store.CreateMaintenanceLog(module, log, reset: true).Report("setup");
            store.CreateMaintenanceLog(other, null, reset: true).Report("other module");

            store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().Be($"download{Environment.NewLine}setup");
            store.GetMaintenanceSnapshot(other.SourcePath).Text.Should().Be("other module");

            // Without reset the same session continues, so its key stays and the new line is appended.
            store.CreateMaintenanceLog(module, null, reset: false).Report("finished");
            store.GetMaintenanceSnapshot(module.SourcePath).SessionKey.Should().Be(firstKey);
            store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().EndWith("finished");

            // A reset replaces the session with an empty one.
            store.CreateMaintenanceLog(module, null, reset: true);
            store.GetMaintenanceSnapshot(module.SourcePath).Text.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that overlapping activities report the strongest one (update over stop), that disposing the
        /// update scope first leaves the stop activity visible, that disposing a scope twice is harmless and that no
        /// activity remains once all scopes are gone.
        /// </summary>
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

        /// <summary>
        /// Verifies that the maintenance snapshot reports a tracked process session as running until the session is
        /// completed.
        /// </summary>
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


        // Module status label

        /// <summary>
        /// Verifies that the module status label key follows the registration and running flags, and that any
        /// maintenance activity takes precedence over them.
        /// </summary>
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


        // Overview and process sessions

        /// <summary>
        /// Verifies that a line appended to a module's overview shows up in the unified overview of that module.
        /// </summary>
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

        /// <summary>
        /// Verifies that a started process session registers its module in the snapshot and that an appended line is
        /// readable from the session text.
        /// </summary>
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

        /// <summary>
        /// Verifies that completing a process session marks it as no longer running and records the exit code.
        /// </summary>
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

        /// <summary>
        /// Verifies that the unfinished line of a stream is rewritten in place until it is completed, independently of
        /// the lines of the other stream, and that blank lines are kept.
        /// </summary>
        [Fact]
        public void Unfinished_process_line_is_rewritten_in_place_per_stream_and_blank_lines_are_kept()
        {
            var store = new ModuleConsoleStore();
            var module = ModuleConfigBuilder.Create();
            using var process = Process.GetCurrentProcess();
            var handle = store.StartProcessSession(module, new ModuleCommand { Name = "Run" }, "Run", "run", process, true);

            store.UpdateProcessLine(handle, ProcessOutputStream.StandardError, "10%");
            store.CompleteProcessLine(handle, ProcessOutputStream.StandardOutput, "output line");
            store.UpdateProcessLine(handle, ProcessOutputStream.StandardError, "50%");
            store.CompleteProcessLine(handle, ProcessOutputStream.StandardError, "100%");
            store.AppendProcessLine(handle, "  ");
            store.CompleteProcessLine(handle, ProcessOutputStream.StandardError, "after progress");

            store.GetSessionLines(module.SourcePath, handle.SessionId)
                .Should().Equal("run", "100%", "output line", "  ", "after progress");
        }

        /// <summary>
        /// Verifies that a session with more buffered lines than the display limit shows the newest lines up to that
        /// limit, after a banner that says the output was trimmed.
        /// </summary>
        [Fact]
        public void Session_text_shows_the_newest_lines_up_to_the_display_limit()
        {
            var store = new ModuleConsoleStore();
            var module = ModuleConfigBuilder.Create();
            using var process = Process.GetCurrentProcess();
            var handle = store.StartProcessSession(module, new ModuleCommand { Name = "Run" }, "Run", "run", process, true);
            for (var index = 1; index <= 1200; index++)
            {
                store.AppendProcessLine(handle, $"line {index}");
            }

            var lines = store.GetSessionLines(module.SourcePath, handle.SessionId);

            lines.Should().HaveCount(ModuleConsoleStore.MaxDisplayedLines + 1);
            lines[0].Should().StartWith("[Console output trimmed");
            lines[1].Should().Be($"line {1201 - ModuleConsoleStore.MaxDisplayedLines}");
            lines[^1].Should().Be("line 1200");
        }


        // Module snapshots

        /// <summary>
        /// Verifies that the snapshot reports running maintenance activities, and that the maintenance log counts as a
        /// running session only while an installation or update writes to it.
        /// </summary>
        [Fact]
        public void Snapshot_reports_activity_and_runs_the_maintenance_log_only_during_installation_or_update()
        {
            var store = new ModuleConsoleStore();
            var module = ModuleConfigBuilder.Create();
            store.CreateMaintenanceLog(module, null, reset: true).Report("Downloading");

            // A stop is an activity, but does not write to the maintenance log.
            using (store.BeginActivity(module.SourcePath, ModuleActivity.Stopping))
            {
                var snapshot = store.GetSnapshot().Single();
                snapshot.HasActivity.Should().BeTrue();
                snapshot.Sessions.Single(session => session.Id == "maintenance").IsRunning.Should().BeFalse();
            }

            using (store.BeginActivity(module.SourcePath, ModuleActivity.Updating))
            {
                store.GetSnapshot().Single().Sessions.Single(session => session.Id == "maintenance")
                    .IsRunning.Should().BeTrue();
            }

            var finished = store.GetSnapshot().Single();
            finished.HasActivity.Should().BeFalse();
            finished.Sessions.Single(session => session.Id == "maintenance").IsRunning.Should().BeFalse();
        }
    }
}
