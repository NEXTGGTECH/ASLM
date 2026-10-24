// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ASLM.Patcher;

namespace ASLM.Installer.Tests.Patcher;

/// <summary>
/// Verifies that the GUI patcher runner applies pending updates correctly and that the shipped
/// CLI patcher executable produces the same result for the same update contract.
/// </summary>
public sealed class PatcherDualVersionTests
{
    /// <summary>
    /// Creates a monolithic ASLM root with a pending self-update and a cmd.exe launcher stub
    /// that records every restart into a marker file.
    /// </summary>
    private sealed class UpdateFixture : IDisposable
    {
        public readonly string Root;
        public readonly string LauncherMarker;

        public UpdateFixture(string kind = "app")
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "aslm-patcher-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);

            Root = Path.Combine(tempRoot, "root");
            LauncherMarker = Path.Combine(tempRoot, "launcher.marker");
            Directory.CreateDirectory(Path.Combine(Root, "App"));
            Directory.CreateDirectory(Path.Combine(Root, "Data", "App"));
            Directory.CreateDirectory(Path.Combine(Root, ".aslm-update", "staging", "v9.9.9", "App"));
            Directory.CreateDirectory(Path.Combine(Root, ".aslm-update", "staging", "v9.9.9", "Patcher"));

            File.WriteAllText(Path.Combine(Root, "App", "ASLM.dll"), "old-app");
            File.WriteAllText(Path.Combine(Root, "App", "legacy.txt"), "legacy-file");
            File.WriteAllText(Path.Combine(Root, "Data", "App", "ASLM_Data.json"),
                """{"user":{"name":"kept"},"updates":{"installedReleaseTag":"v1.0.0"}}""");
            File.WriteAllText(Path.Combine(Root, "Data", "App", "settings.json"), "data-file");
            File.WriteAllText(Path.Combine(Root, "settings.local"), "local-flags");

            File.WriteAllText(Path.Combine(Root, ".aslm-update", "staging", "v9.9.9", "App", "ASLM.dll"), "new-app");
            File.WriteAllText(Path.Combine(Root, ".aslm-update", "staging", "v9.9.9", "App", "added.txt"), "added");
            File.WriteAllText(Path.Combine(Root, ".aslm-update", "staging", "v9.9.9", "Patcher", "ASLM Patcher.exe"), "new-patcher");

            var pending = new
            {
                kind,
                version = "v9.9.9",
                stagingPath = Path.Combine(Root, ".aslm-update", "staging", "v9.9.9"),
                targetRoot = Root,
                backupPath = Path.Combine(Root, ".aslm-update", "backup", "20261024"),
                preserve = new[] { "Data/**", "*.local" }
            };
            File.WriteAllText(Path.Combine(Root, ".aslm-update", "pending.json"), JsonSerializer.Serialize(pending));
        }

        /// <summary>
        /// Patcher control arguments plus a forwarded command that makes the launcher stub leave a marker.
        /// </summary>
        public string[] ArgsForLauncherStub(string cmdPath) =>
        [
            "--root", Root,
            "--launcher", cmdPath,
            "--",
            "/d", "/c", $"echo restarted>{LauncherMarker}"
        ];

        public void Dispose()
        {
            TryDelete(Path.GetDirectoryName(Root)!);
        }
    }

    /// <summary>
    /// Asserts the exact post-update state both patcher versions must produce.
    /// </summary>
    private static void AssertUpdateApplied(UpdateFixture fixture)
    {
        var root = fixture.Root;
        File.ReadAllText(Path.Combine(root, "App", "ASLM.dll")).Should().Be("new-app");
        File.ReadAllText(Path.Combine(root, "App", "added.txt")).Should().Be("added");
        File.Exists(Path.Combine(root, "App", "legacy.txt")).Should().BeFalse("replaced files are removed");
        File.ReadAllText(Path.Combine(root, "Patcher", "ASLM Patcher.exe")).Should().Be("new-patcher");

        // Data is preserved and the installed release tag is merged into the existing settings.
        var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Data", "App", "ASLM_Data.json")));
        data.RootElement.GetProperty("updates").GetProperty("installedReleaseTag").GetString().Should().Be("v9.9.9");
        data.RootElement.GetProperty("user").GetProperty("name").GetString().Should().Be("kept");
        File.ReadAllText(Path.Combine(root, "Data", "App", "settings.json")).Should().Be("data-file");
        File.ReadAllText(Path.Combine(root, "settings.local")).Should().Be("local-flags");

        File.Exists(Path.Combine(root, ".aslm-update", "pending.json")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, ".aslm-update", "staging", "v9.9.9")).Should().BeFalse();
        Directory.Exists(Path.Combine(root, ".aslm-update", "backup", "20261024")).Should().BeFalse();
        File.Exists(fixture.LauncherMarker).Should().BeTrue("the launcher must be restarted after the update");
    }

    /// <summary>
    /// Waits briefly for the launcher stub to write its marker: both patchers restart it
    /// fire-and-forget, so the file appears shortly after the patcher exits.
    /// </summary>
    private static void WaitForLauncherRestart(UpdateFixture fixture)
    {
        for (var attempt = 0; attempt < 50 && !File.Exists(fixture.LauncherMarker); attempt++)
        {
            Thread.Sleep(100);
        }
    }

    [Fact]
    public async Task Gui_patcher_applies_the_pending_update()
    {
        using var fixture = new UpdateFixture();
        var messages = new List<string>();

        var exitCode = await PatcherRunner.RunAsync(
            fixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")),
            new Progress<PatcherProgress>(message => messages.Add(message.Message)));

        exitCode.Should().Be(0);
        messages.Should().Contain(message => message.Contains("applied successfully"));
        WaitForLauncherRestart(fixture);
        AssertUpdateApplied(fixture);
    }

    [Fact]
    public void Cli_patcher_applies_the_same_pending_update()
    {
        using var fixture = new UpdateFixture();

        var exitCode = RunCliPatcher(fixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")));

        exitCode.Should().Be(0);
        WaitForLauncherRestart(fixture);
        AssertUpdateApplied(fixture);
    }

    [Fact]
    public async Task Both_patchers_reject_unsupported_update_kinds_the_same_way()
    {
        using var guiFixture = new UpdateFixture(kind: "module");
        var guiExit = await PatcherRunner.RunAsync(
            guiFixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")), null);
        guiExit.Should().Be(1);
        AssertPendingMarkedFailed(guiFixture.Root);

        using var cliFixture = new UpdateFixture(kind: "module");
        var cliExit = RunCliPatcher(cliFixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        cliExit.Should().Be(1);
        AssertPendingMarkedFailed(cliFixture.Root);
    }

    [Fact]
    public async Task Gui_patcher_without_a_pending_update_just_restarts_the_launcher()
    {
        using var fixture = new UpdateFixture();
        File.Delete(Path.Combine(fixture.Root, ".aslm-update", "pending.json"));

        var exitCode = await PatcherRunner.RunAsync(
            fixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")), null);

        exitCode.Should().Be(0);
        WaitForLauncherRestart(fixture);
        File.Exists(fixture.LauncherMarker).Should().BeTrue();
        Directory.GetFiles(Path.Combine(fixture.Root, ".aslm-update"), "pending.json.failed-*").Should().BeEmpty();
    }

    [Theory]
    [InlineData("drive-root")]
    [InlineData("inside-update-dir")]
    [InlineData("staging-equals-target")]
    [InlineData("non-empty-backup")]
    public async Task Gui_patcher_rejects_unsafe_update_paths_without_touching_the_installation(string mode)
    {
        using var fixture = new UpdateFixture();
        var pendingPath = Path.Combine(fixture.Root, ".aslm-update", "pending.json");
        var pending = JsonNode.Parse(File.ReadAllText(pendingPath))!.AsObject();

        switch (mode)
        {
            case "drive-root":
                pending["targetRoot"] = Path.GetPathRoot(Path.GetFullPath(fixture.Root))!;
                break;
            case "inside-update-dir":
                pending["targetRoot"] = Path.Combine(fixture.Root, ".aslm-update", "inside");
                break;
            case "staging-equals-target":
                pending["stagingPath"] = fixture.Root;
                break;
            case "non-empty-backup":
                Directory.CreateDirectory(Path.Combine(fixture.Root, ".aslm-update", "backup", "20261024"));
                File.WriteAllText(Path.Combine(fixture.Root, ".aslm-update", "backup", "20261024", "stale.txt"), "stale");
                break;
        }
        File.WriteAllText(pendingPath, pending.ToJsonString());

        var exitCode = await PatcherRunner.RunAsync(
            fixture.ArgsForLauncherStub(Path.Combine(Environment.SystemDirectory, "cmd.exe")), null);

        exitCode.Should().Be(1);
        AssertPendingMarkedFailed(fixture.Root);
        File.ReadAllText(Path.Combine(fixture.Root, "App", "ASLM.dll")).Should().Be("old-app",
            "unsafe updates must fail before any file is replaced");
    }

    /// <summary>
    /// Asserts the pending update was renamed so the launcher cannot re-enter the failing loop.
    /// </summary>
    private static void AssertPendingMarkedFailed(string root)
    {
        Directory.GetFiles(Path.Combine(root, ".aslm-update"), "pending.json.failed-*").Should().NotBeEmpty();
        File.Exists(Path.Combine(root, ".aslm-update", "pending.json")).Should().BeFalse();
    }

    /// <summary>
    /// Runs the real aslm-patcher executable built by the Patcher-CLI project.
    /// </summary>
    private static int RunCliPatcher(string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindCliPatcherExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the CLI patcher.");
        if (!process.WaitForExit(120_000))
        {
            process.Kill();
            throw new TimeoutException("The CLI patcher did not exit within two minutes.");
        }
        return process.ExitCode;
    }

    /// <summary>
    /// Locates the built CLI patcher by walking up from the test output to the solution root.
    /// </summary>
    private static string FindCliPatcherExecutable()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "ASLM.slnx")))
            {
                continue;
            }

            var patcher = Path.Combine(directory.FullName, "ASLM", "Patcher", "Patcher-CLI", "bin", "Debug", "net10.0", "aslm-patcher.exe");
            if (!File.Exists(patcher))
            {
                throw new InvalidOperationException($"The CLI patcher was not built: {patcher}");
            }
            return patcher;
        }

        throw new InvalidOperationException("ASLM.slnx was not found above the test output directory.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
