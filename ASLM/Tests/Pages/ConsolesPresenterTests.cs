// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using ASLM.Models;
using ASLM.Pages;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Pages
{
    /// <summary>
    /// Verifies which modules and consoles the consoles page lists: stopped modules keep their console history, busy
    /// modules are listed, and the maintenance log is listed while the preference hides finished processes.
    /// </summary>
    public sealed class ConsolesPresenterTests
    {
        // Module list

        /// <summary>
        /// Verifies that a disabled module with console output stays listed, while a disabled module without output
        /// and without a running activity is hidden.
        /// </summary>
        [Fact]
        public void Stopped_module_with_console_output_stays_listed()
        {
            var store = new ModuleConsoleStore();
            var stopped = CreateModule("stopped");
            var idle = CreateModule("idle");
            store.EnsureModules([stopped, idle]);
            store.AppendOverviewLine(stopped, "Module processes stopped.");

            var state = CreatePresenter(store, showCompletedProcesses: false).BuildState(store.GetSnapshot());

            state.Modules.Select(module => module.SourcePath).Should().Equal(stopped.SourcePath);
        }

        /// <summary>
        /// Verifies that a module with a running maintenance activity is listed before it has any console output.
        /// </summary>
        [Fact]
        public void Module_with_running_activity_is_listed_before_it_has_output()
        {
            var store = new ModuleConsoleStore();
            var installing = CreateModule("installing");
            store.EnsureModule(installing);
            using var activity = store.BeginActivity(installing.SourcePath, ModuleActivity.Installing);

            var state = CreatePresenter(store, showCompletedProcesses: false).BuildState(store.GetSnapshot());

            state.Modules.Select(module => module.SourcePath).Should().Equal(installing.SourcePath);
        }


        // Console list

        /// <summary>
        /// Verifies that the maintenance log with output is listed after the merged console even when finished
        /// processes are hidden, and that finished processes are listed once the preference shows them.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Maintenance_log_is_listed_regardless_of_the_finished_process_preference(bool showCompletedProcesses)
        {
            var store = new ModuleConsoleStore();
            var module = CreateModule("module");
            store.EnsureModule(module);
            store.CreateMaintenanceLog(module, null, reset: true).Report("Downloading");
            using var process = Process.GetCurrentProcess();
            var setup = store.StartProcessSession(module, new ModuleCommand { Name = "Setup" }, "Setup", "setup", process, false);
            store.CompleteProcessSession(setup, 0);

            var state = CreatePresenter(store, showCompletedProcesses).BuildState(store.GetSnapshot());

            string[] expected = showCompletedProcesses
                ? ["__module_unified__", "maintenance", setup.SessionId]
                : ["__module_unified__", "maintenance"];
            state.Sessions.Select(session => session.SessionSourceId).Should().BeEquivalentTo(expected);
            state.Sessions[0].SessionSourceId.Should().Be("__module_unified__");
        }


        // Test helpers

        /// <summary>
        /// Creates a disabled module with its own manifest path.
        /// </summary>
        private static ModuleConfig CreateModule(string id)
        {
            var module = ModuleConfigBuilder.Create(id: id, name: id);
            module.Status.Enabled = false;
            return module;
        }

        /// <summary>
        /// Creates a presenter over the store that lists individual consoles and shows or hides finished processes.
        /// </summary>
        private static ConsolesPresenter CreatePresenter(ModuleConsoleStore store, bool showCompletedProcesses)
        {
            var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
            appData.Data.Consoles.ShowIndividualModuleConsoles = true;
            appData.Data.Consoles.ShowCompletedProcesses = showCompletedProcesses;
            return new ConsolesPresenter(new DiscardingConsolesView(), null!, store, appData);
        }


        // Test doubles

        /// <summary>
        /// Consoles view that ignores every rendered state; the tests read the state from the presenter directly.
        /// </summary>
        private sealed class DiscardingConsolesView : IConsolesView
        {
            /// <summary>
            /// Ignores the rendered state.
            /// </summary>
            public void Render(ConsolesDashboardState state) { }
        }
    }
}
