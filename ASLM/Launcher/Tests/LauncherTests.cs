// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;

namespace ASLM.Launcher.Tests;

/// <summary>
/// Verifies launcher layout resolution, per-user bootstrapping from the payload archive,
/// argument forwarding, and the pending-update hand-off gates.
/// </summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _root;

    public LauncherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ASLM-Launcher-Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [Fact]
    public void Monolithic_layout_is_detected_by_the_app_executable_next_to_the_launcher()
    {
        var monolithic = Path.Combine(_root, "monolithic");
        Directory.CreateDirectory(Path.Combine(monolithic, "App"));
        File.WriteAllText(Path.Combine(monolithic, "App", AppPaths.ExecutableName), "app");
        Directory.CreateDirectory(Path.Combine(_root, "dual"));

        AppPaths.IsMonolithicLayout(monolithic).Should().BeTrue();
        AppPaths.IsMonolithicLayout(Path.Combine(_root, "dual")).Should().BeFalse();
    }

    [Fact]
    public void Payload_archive_name_follows_the_platform()
    {
        var expected = OperatingSystem.IsLinux() ? "aslm-base.tar.gz" : "aslm-base.zip";
        AppPaths.GetPayloadArchivePath(_root).Should().Be(Path.Combine(_root, expected));
    }

    [Fact]
    public void User_app_dir_readiness_requires_the_app_executable()
    {
        var userDir = Path.Combine(_root, "user");
        UserBootstrapper.IsUserAppDirReady(userDir).Should().BeFalse();

        Directory.CreateDirectory(Path.Combine(userDir, "App"));
        File.WriteAllText(Path.Combine(userDir, "App", AppPaths.ExecutableName), "app");
        if (OperatingSystem.IsLinux())
        {
            UserBootstrapper.IsUserAppDirReady(userDir).Should().BeFalse("linux also needs the runtime libraries");
        }
        else
        {
            UserBootstrapper.IsUserAppDirReady(userDir).Should().BeTrue();
        }
    }

    [Fact]
    public void Launcher_ref_round_trips_the_shared_launcher_path()
    {
        var userDir = Path.Combine(_root, "user");
        var launcherPath = Path.Combine(_root, "shared", AppPaths.ExecutableName);

        UserBootstrapper.WriteLauncherRef(launcherPath, userDir);

        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(userDir, "launcher-ref.json")));
        json.RootElement.GetProperty("launcherPath").GetString().Should().Be(launcherPath);
    }

    [Fact]
    public void Bootstrap_extracts_the_payload_but_never_a_second_launcher_or_unsafe_entries()
    {
        var shared = Path.Combine(_root, "shared");
        var user = Path.Combine(_root, "user");
        Directory.CreateDirectory(Path.Combine(shared, "App"));
        Directory.CreateDirectory(user);
        var archivePath = AppPaths.GetPayloadArchivePath(shared);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "App/ASLM.dll", "payload-app");
            WriteEntry(archive, "Patcher/patcher.dll", "patcher");
            // The root launcher must not be extracted a second time into the user dir.
            WriteEntry(archive, AppPaths.ExecutableName, "shared-launcher");
            // Escaping entries are skipped, not extracted.
            WriteEntry(archive, "../outside.txt", "escaped");
        }

        UserBootstrapper.TryBootstrap(shared, user, Path.Combine(_root, "Launcher.log")).Should().BeTrue();

        File.ReadAllText(Path.Combine(user, "App", "ASLM.dll")).Should().Be("payload-app");
        File.ReadAllText(Path.Combine(user, "Patcher", "patcher.dll")).Should().Be("patcher");
        File.Exists(Path.Combine(user, AppPaths.ExecutableName)).Should().BeFalse();
        File.Exists(Path.Combine(_root, "outside.txt")).Should().BeFalse();
    }

    [Fact]
    public void Bootstrap_failure_on_windows_removes_the_partial_user_directory()
    {
        if (OperatingSystem.IsLinux())
        {
            // Linux bootstrapping is transactional per folder and never deletes profile data.
            return;
        }

        var shared = Path.Combine(_root, "broken");
        Directory.CreateDirectory(shared);
        File.WriteAllText(AppPaths.GetPayloadArchivePath(shared), "not a zip at all");
        var user = Path.Combine(_root, "user");

        UserBootstrapper.TryBootstrap(shared, user, Path.Combine(_root, "Launcher.log")).Should().BeFalse();
        Directory.Exists(user).Should().BeFalse("a partial bootstrap must not look installed");
    }

    [Fact]
    public void Bootstrap_without_a_payload_archive_fails_without_touching_the_user_dir()
    {
        var shared = Path.Combine(_root, "empty-shared");
        var user = Path.Combine(_root, "untouched");
        Directory.CreateDirectory(shared);

        UserBootstrapper.TryBootstrap(shared, user, Path.Combine(_root, "Launcher.log")).Should().BeFalse();
        Directory.Exists(user).Should().BeFalse();
    }

    [Fact]
    public void Forwarded_arguments_drop_launcher_restart_pairs_and_keep_user_arguments()
    {
        var startInfo = new ProcessStartInfo("dotnet");
        var args = new[]
        {
            "--wait-process", "1234",
            "--wait-start", "638500000000000000",
            "--user-flag", "value with spaces",
            "--launcher"
        };

        Program.AppendForwardedArguments(startInfo, args);

        startInfo.ArgumentList.Should().Equal("--user-flag", "value with spaces", "--launcher");
    }

    [Fact]
    public async Task Wait_for_process_exit_honors_the_process_start_identity()
    {
        var logPath = Path.Combine(_root, "Launcher.log");

        // No restart arguments: a no-op.
        Program.WaitForRequestedProcessExit([], logPath);

        // Invalid ids are rejected outright.
        var act = () => Program.WaitForRequestedProcessExit(["--wait-process", "not-a-pid"], logPath);
        act.Should().Throw<ArgumentException>();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
            UseShellExecute = false
        });
        process.Should().NotBeNull();
        try
        {
            // A mismatched start time means the PID was reused: never wait for the wrong process.
            var staleTicks = process.StartTime.ToUniversalTime().Ticks - TimeSpan.FromDays(1).Ticks;
            Program.WaitForRequestedProcessExit(
                ["--wait-process", process.Id.ToString(), "--wait-start", staleTicks.ToString()],
                logPath);
            process.HasExited.Should().BeFalse("identity mismatch must return immediately");

            // A matching identity waits for the real exit.
            var matching = Task.Run(() => Program.WaitForRequestedProcessExit(
                ["--wait-process", process.Id.ToString(), "--wait-start",
                    process.StartTime.ToUniversalTime().Ticks.ToString()],
                logPath));
            process.Kill();
            await matching.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    [Fact]
    public void Pending_update_handoff_requires_a_patcher_executable()
    {
        var appRoot = Path.Combine(_root, "app");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(Path.Combine(appRoot, ".aslm-update"));
        File.WriteAllText(Path.Combine(appRoot, ".aslm-update", "pending.json"), "{}");
        var logPath = Path.Combine(_root, "Launcher.log");

        // No Patcher directory at all.
        Program.TryStartPendingPatcher(_root, appRoot, appRoot, [], logPath).Should().BeFalse();

        // A Patcher directory without a known executable also declines.
        Directory.CreateDirectory(Path.Combine(appRoot, "Patcher"));
        Program.TryStartPendingPatcher(_root, appRoot, appRoot, [], logPath).Should().BeFalse();
        File.ReadAllText(logPath).Should().Contain("patcher is missing");
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
