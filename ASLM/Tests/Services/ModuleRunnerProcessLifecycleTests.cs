// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ASLM.Models;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleRunnerProcessLifecycleTests
{
    [Fact]
    public async Task Stop_reaps_child_of_exited_launcher_releases_port_and_allows_relaunch()
    {
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
            for (var iteration = 0; iteration < 2; iteration++)
            {
                File.Delete(childInfo);
                var started = await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None);
                started.Should().BeTrue(string.Join(Environment.NewLine, console.GetUnifiedModuleLines(module.SourcePath)));
                var rootPid = console.GetSnapshot().Single().Sessions.Where(s => s.IsTrackedProcess)
                    .MaxBy(s => s.StartedUtc)!.ProcessId!.Value;
                await WaitUntilAsync(() => File.Exists(childInfo) && new FileInfo(childInfo).Length > 0);
                var values = (await File.ReadAllTextAsync(childInfo)).Split(' ');
                child = Process.GetProcessById(int.Parse(values[0]));
                _ = child.Handle; // retain identity even after termination
                var port = int.Parse(values[1]);
                await WaitUntilAsync(() => !IsAlive(rootPid));
                child.HasExited.Should().BeFalse("the launcher exits before its server is stopped");

                await runner.StopModuleAsync(module.SourcePath).WaitAsync(TimeSpan.FromSeconds(10));

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
            if (child != null)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
                child.Dispose();
            }
            await runner.StopAllModulesAsync();
        }
    }

    [Fact]
    public async Task Immediate_stop_and_restart_do_not_leave_processes_or_stop_another_module()
    {
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
                await runner.StopModuleAsync(module.SourcePath);
                runner.GetRunningModuleSourcePaths().Should().Equal(other.SourcePath);
            }
        }
        finally { await runner.StopAllModulesAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_cancels_pending_startup_and_prevents_late_run_commands(bool stopAll)
    {
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
        var launch = runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => File.Exists(ready) && new FileInfo(ready).Length > 0);
            using var settings = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(ready)));
            _ = settings.Handle;
            await (stopAll ? runner.StopAllModulesAsync() : runner.StopModuleAsync(module.SourcePath))
                .WaitAsync(TimeSpan.FromSeconds(10));
            (await launch).Should().BeFalse();
            await settings.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            console.GetSnapshot().Single().Sessions.Should().NotContain(s => s.IsTrackedProcess);
            runner.GetRunningModuleSourcePaths().Should().BeEmpty();

            module.Settings.Clear();
            (await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeTrue();
        }
        finally { await runner.StopAllModulesAsync(); await launch; }
    }

    [Fact]
    public async Task Failed_launch_after_first_command_creation_cleans_up_every_started_root()
    {
        using var layout = new AslmFileSystemLayout();
        var module = CreateModule(layout);
        module.Commands.Run.Add(new ModuleCommand { Name = "Valid", Exec = PowerShell(module, "Start-Sleep -Seconds 30") });
        module.Commands.Run.Add(new ModuleCommand { Name = "Invalid", Exec = "aslm-nonexistent-lifecycle-test-command" });
        var console = new ModuleConsoleStore();
        using var runner = CreateRunner(console);
        try
        {
            (await runner.ExecuteRunAsync(module, new SilentProgress(), CancellationToken.None)).Should().BeFalse();
            var sessions = console.GetSnapshot().Single().Sessions.Where(s => s.IsTrackedProcess).ToList();
            sessions.Should().ContainSingle();
            sessions.Should().OnlyContain(s => !s.IsRunning);
            IsAlive(sessions.Single().ProcessId!.Value).Should().BeFalse();
            runner.GetRunningModuleSourcePaths().Should().BeEmpty();
        }
        finally { await runner.StopAllModulesAsync(); }
    }

    private static ModuleConfig CreateModule(AslmFileSystemLayout layout)
    {
        var id = $"process-test-{Guid.NewGuid():N}";
        var path = Path.Combine(layout.ModulesDir, id, ModuleManifestDiscovery.ManifestFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new ModuleConfig { Id = id, Name = id, SourcePath = path };
    }

    private static ModuleRunner CreateRunner(ModuleConsoleStore console)
    {
        var engines = new EngineInstaller();
        return new ModuleRunner(engines, new ModuleEnvironmentResolver(engines),
            new PortRegistry(new AppDataStore(NullLogger<AppDataStore>.Instance)), null!,
            console, new ProcessSnapshotReader(), null!, null!, null!, null!, null!,
            new ModuleInteropHostState(), new EmptyServiceProvider(), NullLogger<ModuleRunner>.Instance);
    }

    private static string WriteScript(ModuleConfig module, string script)
    {
        var path = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, $"{Guid.NewGuid():N}.ps1");
        File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string PowerShell(ModuleConfig module, string script) =>
        $"powershell.exe -NoProfile -NonInteractive -File \"{WriteScript(module, script)}\"";

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class SilentProgress : IProgress<string>
    {
        public void Report(string value) { }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
