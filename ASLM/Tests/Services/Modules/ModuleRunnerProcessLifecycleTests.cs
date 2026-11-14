// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ASLM.Models;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies how the module runner starts, stops and cleans up real PowerShell child processes: reaping children
    /// of exited launchers, overlapping stop and restart, canceling a pending startup, rolling back a failed launch,
    /// and bringing their output into the consoles.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class ModuleRunnerProcessLifecycleTests
    {
        // Stopping running modules

        /// <summary>
        /// Verifies that stopping a module also ends a child that outlived its already exited launcher, frees the
        /// port that child listened on, clears the running state, and that the module can then be started and
        /// stopped again.
        /// </summary>
        [Fact]
        public async Task Stop_reaps_child_of_exited_launcher_releases_port_and_allows_relaunch()
        {
            // Arrange: a launcher command that starts a detached child. The child listens on a free loopback port
            // and records its process id and port in a file.
            using var layout = new AslmFileSystemLayout();
            var module = CreateModule(layout);
            var childInfo = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "child.txt");
            var childScript = $$"""
                $server = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
                $server.Start()
                [System.IO.File]::WriteAllText('{{childInfo}}', "$PID $($server.LocalEndpoint.Port)")
                Start-Sleep -Seconds 30
                """;
            var childScriptPath = WriteScript(module, childScript);
            module.Commands.Run.Add(new ModuleCommand
            {
                Name = "Launcher",
                Exec = PowerShell(module, $"Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile -NonInteractive -File \"{childScriptPath}\"' -RedirectStandardOutput child.out -RedirectStandardError child.err | Out-Null")
            });
            var console = new ModuleConsoleStore();
            using var runner = CreateRunner(console);
            Process? child = null;
            try
            {
                // Run the scenario twice to prove that the module can be relaunched after a stop.
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    // Act: start the module, then learn the launcher's pid and the child's pid and port.
                    File.Delete(childInfo);
                    var started = await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None);
                    started.Should().BeTrue(string.Join(Environment.NewLine, console.GetUnifiedModuleLines(module.SourcePath)));
                    var rootPid = console.GetSnapshot().Single().Sessions.Where(s => s.IsTrackedProcess)
                        .MaxBy(s => s.StartedUtc)!.ProcessId!.Value;
                    await WaitUntilAsync(() => File.Exists(childInfo) && new FileInfo(childInfo).Length > 0);
                    var values = (await File.ReadAllTextAsync(childInfo)).Split(' ');
                    child = Process.GetProcessById(int.Parse(values[0]));

                    // Retain identity even after termination.
                    _ = child.Handle;
                    var port = int.Parse(values[1]);

                    // Only the child is left once the launcher has exited.
                    await WaitUntilAsync(() => !IsAlive(rootPid));
                    child.HasExited.Should().BeFalse("the launcher exits before its server is stopped");

                    await runner.StopModuleAsync(module.SourcePath).WaitAsync(TimeSpan.FromSeconds(10));

                    // Assert: the child is gone, the module is no longer running and the child's port is free again.
                    child.HasExited.Should().BeTrue("stop must reap the child, not just the exited launcher");
                    runner.GetRunningModuleSourcePaths().Should().NotContain(module.SourcePath);
                    console.GetSnapshot().Single().Sessions.Should().OnlyContain(session => !session.IsRunning);
                    using var portProbe = new TcpListener(IPAddress.Loopback, port);
                    portProbe.Start();
                    child.Dispose();
                    child = null;
                }
            }
            finally
            {
                // Make sure a failed assertion cannot leave the child or any module process running.
                if (child != null)
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                    child.Dispose();
                }

                await runner.StopAllModulesAsync();
            }
        }

        /// <summary>
        /// Verifies that a stop issued immediately after a start, together with an overlapping restart request,
        /// leaves no old process behind and never stops a second module that is running at the same time.
        /// </summary>
        [Fact]
        public async Task Immediate_stop_and_restart_do_not_leave_processes_or_stop_another_module()
        {
            // Arrange: two modules with long-sleeping run commands; "other" is started first and must keep running.
            using var layout = new AslmFileSystemLayout();
            var module = CreateModule(layout);
            var other = CreateModule(layout);
            module.Commands.Run.Add(new ModuleCommand { Name = "Starting server", Exec = PowerShell(module, "Start-Sleep -Seconds 30") });
            other.Commands.Run.Add(new ModuleCommand { Name = "Other server", Exec = PowerShell(other, "Start-Sleep -Seconds 30") });
            var console = new ModuleConsoleStore();
            using var runner = CreateRunner(console);
            try
            {
                (await runner.ExecuteRunAsync(other, new SilentProgress(), CancellationToken.None)).Should().BeTrue();

                // Repeat start, stop and overlapping restart several times to expose timing races.
                for (var iteration = 0; iteration < 5; iteration++)
                {
                    (await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeTrue();
                    var session = console.GetSnapshot().Single(m => m.SourcePath == module.SourcePath)
                        .Sessions.Single(s => s.IsTrackedProcess && s.IsRunning);
                    using var root = Process.GetProcessById(session.ProcessId!.Value);
                    _ = root.Handle;
                    var stop = runner.StopModuleAsync(module.SourcePath);

                    // Even an overlapping direct run request must wait for the old run to be reaped.
                    var restart = runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None);
                    await stop.WaitAsync(TimeSpan.FromSeconds(10));
                    root.HasExited.Should().BeTrue();
                    (await restart).Should().BeTrue();

                    // After this stop only the untouched module is still running.
                    await runner.StopModuleAsync(module.SourcePath);
                    runner.GetRunningModuleSourcePaths().Should().Equal(other.SourcePath);
                }
            }
            finally { await runner.StopAllModulesAsync(); }
        }


        // Startup cancellation

        /// <summary>
        /// Verifies that stopping one module or all modules while the launch still waits for a settings command ends
        /// that command, makes the launch return false without starting any run command, and leaves the module able
        /// to start normally afterwards.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Stop_cancels_pending_startup_and_prevents_late_run_commands(bool stopAll)
        {
            // Arrange: a port setting whose set command records its pid and then blocks, which holds the launch in
            // its settings synchronization before any run command starts.
            using var layout = new AslmFileSystemLayout();
            var module = CreateModule(layout);
            var ready = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "settings.pid");
            module.Settings.Add(new ModuleSetting
            {
                Key = "port", Type = "port",
                SetExec = PowerShell(module, $"[System.IO.File]::WriteAllText('{ready}', [string]$PID); Start-Sleep -Seconds 30")
            });
            module.Commands.Run.Add(new ModuleCommand { Name = "Must not start", Exec = PowerShell(module, "Start-Sleep -Seconds 30") });
            var console = new ModuleConsoleStore();
            using var runner = CreateRunner(console);

            // Act: start the launch without awaiting it, wait until the blocking settings command is running, then
            // stop the module (or all modules).
            var launch = runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
                using var settings = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready)));
                _ = settings.Handle;
                await (stopAll ? runner.StopAllModulesAsync() : runner.StopModuleAsync(module.SourcePath))
                    .WaitAsync(TimeSpan.FromSeconds(10));

                // Assert: the launch was abandoned, the settings command ended and no run process was started.
                (await launch).Should().BeFalse();
                await settings.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                console.GetSnapshot().Single().Sessions.Should().NotContain(s => s.IsTrackedProcess);
                runner.GetRunningModuleSourcePaths().Should().BeEmpty();

                // Without the blocking setting the module starts normally, so the stop left no stale state behind.
                module.Settings.Clear();
                (await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeTrue();
            }
            finally { await runner.StopAllModulesAsync(); await launch; }
        }


        // Failed launch

        /// <summary>
        /// Verifies that when the second run command cannot start, the process already started for the first one is
        /// stopped, the launch returns false, and no module remains marked as running.
        /// </summary>
        [Fact]
        public async Task Failed_launch_after_first_command_creation_cleans_up_every_started_root()
        {
            // Arrange: a valid long-running command followed by a command with a non-existent executable.
            using var layout = new AslmFileSystemLayout();
            var module = CreateModule(layout);
            module.Commands.Run.Add(new ModuleCommand { Name = "Valid", Exec = PowerShell(module, "Start-Sleep -Seconds 30") });
            module.Commands.Run.Add(new ModuleCommand { Name = "Invalid", Exec = "aslm-nonexistent-lifecycle-test-command" });
            var console = new ModuleConsoleStore();
            using var runner = CreateRunner(console);
            try
            {
                // Act and assert: the launch fails, and the one process that did start is already gone.
                (await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeFalse();
                var sessions = console.GetSnapshot().Single().Sessions.Where(s => s.IsTrackedProcess).ToList();
                sessions.Should().ContainSingle();
                sessions.Should().OnlyContain(s => !s.IsRunning);
                IsAlive(sessions.Single().ProcessId!.Value).Should().BeFalse();
                runner.GetRunningModuleSourcePaths().Should().BeEmpty();
            }
            finally { await runner.StopAllModulesAsync(); }
        }


        // Console output

        /// <summary>
        /// Verifies that the output of a setup command reaches its console session as a terminal shows it (UTF-8 text
        /// decoded, the blank line kept, a progress line redrawn with carriage returns kept only in its final state)
        /// and that the merged console of the module receives the lines in the order the process wrote them.
        /// </summary>
        [Fact]
        public async Task Setup_output_reaches_the_consoles_in_order_and_as_a_terminal_shows_it()
        {
            // Arrange: a setup command that writes raw UTF-8 bytes, so the console code page of PowerShell does not
            // change them.
            using var layout = new AslmFileSystemLayout();
            var module = CreateModule(layout);
            module.Commands.FirstRun.Add(new ModuleCommand
            {
                Name = "Setup",
                Exec = PowerShell(module, """
                    $out = [Console]::OpenStandardOutput()
                    function Send([string]$text) { $bytes = [Text.Encoding]::UTF8.GetBytes($text); $out.Write($bytes, 0, $bytes.Length) }
                    for ($i = 1; $i -le 300; $i++) { Send "line $i`n" }
                    Send "`n"
                    Send "Привет`r`n"
                    Send "progress 10%`rprogress 50%`rprogress 100%`n"
                    """)
            });
            var console = new ModuleConsoleStore();
            using var runner = CreateRunner(console);

            // Act
            (await runner.ExecuteFirstRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeTrue();

            // Assert: the session holds every line once, with the redrawn line in its final state only.
            var session = console.GetSnapshot().Single().Sessions.Single(s => s.Stage == "Setup");
            var lines = console.GetSessionLines(module.SourcePath, session.Id);
            lines.Should().ContainInOrder("  line 1", "  line 300", "  ", "  Привет", "  progress 100%");
            lines.Should().NotContain(line => line.Contains("progress 10%") || line.Contains("progress 50%"));

            // The merged console lists the numbered lines in the order the process wrote them.
            var merged = console.GetUnifiedModuleLines(module.SourcePath);
            merged.Select(line => Regex.Match(line, @" line (\d+)$"))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups[1].Value))
                .Should().Equal(Enumerable.Range(1, 300));
            merged.Should().NotContain(line => line.Contains("progress 10%"));
        }


        // Test helpers

        /// <summary>
        /// Creates a module with a unique id whose manifest path lies in a freshly created folder below the modules
        /// directory; the manifest file itself is not written.
        /// </summary>
        private static ModuleConfig CreateModule(AslmFileSystemLayout layout)
        {
            var id = $"process-test-{Guid.NewGuid():N}";
            var path = Path.Combine(layout.ModulesDir, id, ModuleManifestDiscovery.ManifestFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new ModuleConfig { Id = id, Name = id, SourcePath = path };
        }

        /// <summary>
        /// Creates a runner with a real engine installer, environment resolver and port registry that reports to
        /// the given console store; the dependencies that these scenarios never reach are null.
        /// </summary>
        private static ModuleRunner CreateRunner(ModuleConsoleStore console)
        {
            var engines = new EngineInstaller();
            return new ModuleRunner(engines, new ModuleEnvironmentResolver(engines),
                new PortRegistry(new AppDataStore(NullLogger<AppDataStore>.Instance)), null!,
                console, new ProcessSnapshotReader(), null!, null!, null!, null!, null!,
                new ModuleInteropHostState(), new EmptyServiceProvider(), NullLogger<ModuleRunner>.Instance);
        }

        /// <summary>
        /// Writes a PowerShell script with a UTF-8 byte-order mark into the module folder and returns its path.
        /// </summary>
        private static string WriteScript(ModuleConfig module, string script)
        {
            var path = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, $"{Guid.NewGuid():N}.ps1");
            File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }

        /// <summary>
        /// Builds a command line that runs the given script text as a PowerShell script file.
        /// </summary>
        private static string PowerShell(ModuleConfig module, string script) =>
            $"powershell.exe -NoProfile -NonInteractive -File \"{WriteScript(module, script)}\"";

        /// <summary>
        /// Returns whether a process with the given id exists and has not exited.
        /// </summary>
        private static bool IsAlive(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }

        /// <summary>
        /// Polls the condition every 20 milliseconds and throws a cancellation exception after 10 seconds.
        /// </summary>
        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!predicate()) await Task.Delay(20, timeout.Token);
        }


        // Test doubles

        /// <summary>
        /// Progress reporter that discards every message.
        /// </summary>
        private sealed class SilentProgress : IProgress<string>
        {
            /// <summary>
            /// Ignores the reported message.
            /// </summary>
            public void Report(string value) { }
        }

        /// <summary>
        /// Service provider that resolves no services.
        /// </summary>
        private sealed class EmptyServiceProvider : IServiceProvider
        {
            /// <summary>
            /// Always returns null.
            /// </summary>
            public object? GetService(Type serviceType) => null;
        }
    }
}
