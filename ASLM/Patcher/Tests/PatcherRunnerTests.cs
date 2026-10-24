// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Patcher;
using FluentAssertions;

namespace ASLM.Patcher.Tests;

/// <summary>
/// Applies real pending updates against a throwaway installation tree: replacement,
/// preserve rules, rollback, unsafe-path rejection, and the launcher restart contract.
/// </summary>
public sealed class PatcherRunnerTests : IDisposable
{
    private readonly string _root;
    private readonly string _markerScript;
    private readonly string _markerPath;
    private readonly List<string> _progress = [];

    public PatcherRunnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ASLM-Patcher-Tests", Guid.NewGuid().ToString("N"));
        _markerScript = Path.Combine(_root, "marker.ps1");
        _markerPath = Path.Combine(_root, "launched.txt");
        Directory.CreateDirectory(Path.Combine(_root, "Data", "App"));
        Directory.CreateDirectory(Path.Combine(_root, "Modules"));
        File.WriteAllText(Path.Combine(_root, "Data", "App", "ASLM_Data.json"),
            """{ "updates": { "checkEnabled": true }, "firstRunCompleted": true }""");
        File.WriteAllText(_markerScript,
            $"Add-Content -Path '{_markerPath}' -Value ($args -join ' ')");
    }

    public void Dispose()
    {
        TryDelete(_root);
    }

    private string TargetRoot => Path.Combine(_root, "target");

    private string LauncherArgs() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// Seeds an installation with one old app file, preserved user data, and a payload
    /// that replaces the app while shipping a brand-new file.
    /// </summary>
    private string SeedPendingUpdate(
        string[]? preserve = null,
        Action<string>? seedTarget = null,
        Action<string>? seedStaging = null,
        string kind = "app",
        string? stagingOverride = null,
        string? targetOverride = null)
    {
        var target = targetOverride ?? TargetRoot;
        Directory.CreateDirectory(Path.Combine(target, "App"));
        Directory.CreateDirectory(Path.Combine(target, "Data", "App"));
        Directory.CreateDirectory(Path.Combine(target, "Modules", "chat"));
        File.WriteAllText(Path.Combine(target, "App", "ASLM.dll"), "old-app");
        File.WriteAllText(Path.Combine(target, "App", "removed-in-update.bin"), "obsolete");
        File.WriteAllText(Path.Combine(target, "Data", "App", "ASLM_Data.json"),
            """{ "updates": { "checkEnabled": true }, "firstRunCompleted": true }""");
        File.WriteAllText(Path.Combine(target, "Modules", "chat", "user-state.json"), "{ }");
        seedTarget?.Invoke(target);

        var stagingRoot = Path.Combine(_root, ".aslm-update", "staging");
        var version = "9.9.9";
        var wrapper = Path.Combine(stagingRoot, version, "wrapper");
        Directory.CreateDirectory(Path.Combine(wrapper, "App"));
        File.WriteAllText(Path.Combine(wrapper, "App", "ASLM.dll"), "new-app");
        File.WriteAllText(Path.Combine(wrapper, "App", "added.bin"), "fresh");
        seedStaging?.Invoke(wrapper);

        var backup = Path.Combine(_root, ".aslm-update", "backup", "20260101000000");
        var pending = new
        {
            kind,
            version,
            stagingPath = stagingOverride ?? wrapper,
            targetRoot = target,
            backupPath = backup,
            preserve = preserve ?? new[] { "Data", "Modules" },
            createdUtc = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(Path.Combine(_root, ".aslm-update", "pending.json"),
            JsonSerializer.Serialize(pending));
        return version;
    }

    private Task<int> RunPatcher(params string[] extraArgs) =>
        PatcherRunner.RunAsync(
            ["--root", _root, "--launcher", LauncherArgs(), "--", .. extraArgs],
            new Progress<PatcherProgress>(update => _progress.Add(update.Message)));

    private async Task<string> WaitForLauncherMarkerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_markerPath))
            {
                return await File.ReadAllTextAsync(_markerPath);
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The launcher stub never started.");
    }

    [Fact]
    public async Task Applies_payload_preserves_user_data_and_restarts_the_launcher_with_forwarded_args()
    {
        SeedPendingUpdate();

        var exitCode = await RunPatcher("-File", _markerScript, "v9.9.9");

        exitCode.Should().Be(0);
        // Replaced and added application files.
        File.ReadAllText(Path.Combine(TargetRoot, "App", "ASLM.dll")).Should().Be("new-app");
        File.ReadAllText(Path.Combine(TargetRoot, "App", "added.bin")).Should().Be("fresh");
        File.Exists(Path.Combine(TargetRoot, "App", "removed-in-update.bin")).Should()
            .BeFalse("files absent from the payload are removed");
        // Preserved directories survive untouched.
        File.Exists(Path.Combine(TargetRoot, "Data", "App", "ASLM_Data.json")).Should().BeTrue();
        File.Exists(Path.Combine(TargetRoot, "Modules", "chat", "user-state.json")).Should().BeTrue();
        // The installed release tag is merged into the data root's updates block.
        var data = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_root, "Data", "App", "ASLM_Data.json")));
        data.RootElement.GetProperty("updates").GetProperty("installedReleaseTag").GetString()
            .Should().Be("9.9.9");
        data.RootElement.GetProperty("updates").GetProperty("checkEnabled").GetBoolean().Should().BeTrue();
        data.RootElement.GetProperty("firstRunCompleted").GetBoolean().Should().BeTrue();
        // Update bookkeeping is cleaned up (empty staging/backup parents may remain).
        File.Exists(Path.Combine(_root, ".aslm-update", "pending.json")).Should().BeFalse();
        Directory.GetDirectories(Path.Combine(_root, ".aslm-update", "staging")).Should().BeEmpty();
        Directory.GetDirectories(Path.Combine(_root, ".aslm-update", "backup")).Should().BeEmpty();
        File.Exists(Path.Combine(_root, ".aslm-update", "logs", "Patcher.log")).Should().BeTrue();
        // The launcher restart forwards only user arguments.
        (await WaitForLauncherMarkerAsync()).Should().Contain("v9.9.9").And.NotContain("--root");
    }

    [Fact]
    public async Task Preserve_patterns_support_directory_prefixes_and_per_segment_wildcards()
    {
        SeedPendingUpdate(
            preserve: ["Logs/*", "settings.json"],
            seedTarget: target =>
            {
                Directory.CreateDirectory(Path.Combine(target, "Logs"));
                File.WriteAllText(Path.Combine(target, "Logs", "run.log"), "log");
                File.WriteAllText(Path.Combine(target, "settings.json"), "keep");
                File.WriteAllText(Path.Combine(target, "dropped.txt"), "gone");
            });

        (await RunPatcher()).Should().Be(0);

        File.Exists(Path.Combine(TargetRoot, "Logs", "run.log")).Should().BeTrue();
        File.Exists(Path.Combine(TargetRoot, "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(TargetRoot, "dropped.txt")).Should().BeFalse();
        // The payload had no Data folder, but "Data" was not preserved this time.
        File.Exists(Path.Combine(TargetRoot, "Data", "App", "ASLM_Data.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_pending_update_the_launcher_still_restarts()
    {
        var exitCode = await RunPatcher("-File", _markerScript, "clean-start");

        exitCode.Should().Be(0);
        (await WaitForLauncherMarkerAsync()).Should().Contain("clean-start");
    }

    [Fact]
    public async Task Unsafe_update_paths_are_rejected_target_stays_intact_and_pending_is_marked_failed()
    {
        var outsideStaging = Path.Combine(Path.GetTempPath(), "ASLM-Patcher-Tests", Guid.NewGuid().ToString("N"), "evil");
        Directory.CreateDirectory(outsideStaging);
        SeedPendingUpdate(stagingOverride: outsideStaging);

        var exitCode = await RunPatcher();

        exitCode.Should().Be(1);
        File.ReadAllText(Path.Combine(TargetRoot, "App", "ASLM.dll")).Should().Be("old-app");
        Directory.GetFiles(Path.Combine(_root, ".aslm-update"))
            .Should().ContainSingle(path => Path.GetFileName(path).StartsWith("pending.json.failed-"));
    }

    [Fact]
    public async Task Unsupported_kind_and_relative_staging_are_rejected()
    {
        SeedPendingUpdate(kind: "module");
        (await RunPatcher()).Should().Be(1);

        Directory.Delete(TargetRoot, recursive: true);
        SeedPendingUpdate(stagingOverride: "relative/path");
        (await RunPatcher()).Should().Be(1);
    }

    [Theory]
    [InlineData("drive-root")]
    [InlineData("inside-update-dir")]
    [InlineData("overlapping-staging")]
    [InlineData("non-empty-backup")]
    public async Task Overlapping_or_degenerate_directories_are_rejected(string variant)
    {
        // Every variant seeds a normal installation and then swaps only the rejected path;
        // the drive-root variant must not seed files at the actual drive root.
        var target = variant switch
        {
            "drive-root" => Path.GetPathRoot(Path.GetTempPath())!,
            "inside-update-dir" => Path.Combine(_root, ".aslm-update", "sneaky"),
            "overlapping-staging" => Path.Combine(_root, ".aslm-update", "staging", "9.9.9", "wrapper", "App"),
            _ => TargetRoot
        };
        if (variant == "drive-root")
        {
            // Seed the payload and pending marker without touching the target root.
            var stagingRoot = Path.Combine(_root, ".aslm-update", "staging");
            var wrapper = Path.Combine(stagingRoot, "9.9.9", "wrapper");
            Directory.CreateDirectory(Path.Combine(wrapper, "App"));
            File.WriteAllText(Path.Combine(wrapper, "App", "ASLM.dll"), "new-app");
            File.WriteAllText(Path.Combine(_root, ".aslm-update", "pending.json"), JsonSerializer.Serialize(new
            {
                kind = "app",
                version = "9.9.9",
                stagingPath = wrapper,
                targetRoot = target,
                backupPath = Path.Combine(_root, ".aslm-update", "backup", "20260101000000"),
                preserve = new[] { "Data", "Modules" },
                createdUtc = DateTimeOffset.UtcNow.ToString("o")
            }));
        }
        else
        {
            SeedPendingUpdate(targetOverride: target);
            if (variant == "non-empty-backup")
            {
                Directory.CreateDirectory(Path.Combine(_root, ".aslm-update", "backup", "20260101000000"));
                File.WriteAllText(
                    Path.Combine(_root, ".aslm-update", "backup", "20260101000000", "stale.txt"), "old");
            }
        }

        var exitCode = await RunPatcher();

        exitCode.Should().Be(1);
        if (variant == "drive-root")
        {
            // Nothing was seeded and nothing may be created at the drive root itself.
            Directory.Exists(Path.Combine(target, "App")).Should().BeFalse();
        }
        else if (variant == "non-empty-backup")
        {
            File.ReadAllText(Path.Combine(TargetRoot, "App", "ASLM.dll")).Should().Be("old-app");
        }
        else
        {
            File.ReadAllText(Path.Combine(target, "App", "ASLM.dll")).Should().Be("old-app");
        }
    }

    [Fact]
    public async Task A_locked_file_fails_the_patch_before_any_replacement_and_marks_the_update_failed()
    {
        SeedPendingUpdate();
        var locked = Path.Combine(TargetRoot, "App", "ASLM.dll");
        using var held = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var exitCode = await RunPatcher();

        exitCode.Should().Be(1);
        held.Close();
        // The backup copy fails while the file is exclusively held, so nothing was replaced.
        File.ReadAllText(locked).Should().Be("old-app");
        Directory.GetFiles(Path.Combine(_root, ".aslm-update"))
            .Should().ContainSingle(path => Path.GetFileName(path).StartsWith("pending.json.failed-"));
    }

    [Fact]
    public async Task Launcher_resolution_falls_back_to_launcher_ref_json()
    {
        SeedPendingUpdate();
        var launcherDir = Path.Combine(_root, "launcher");
        Directory.CreateDirectory(launcherDir);
        File.WriteAllText(Path.Combine(_root, "launcher-ref.json"),
            JsonSerializer.Serialize(new { launcherPath = LauncherArgs() }));

        // No --launcher argument: tier 2 (launcher-ref.json) must resolve the stub.
        var exitCode = await PatcherRunner.RunAsync(
            ["--root", _root, "--", "-File", _markerScript, "via-ref"],
            new Progress<PatcherProgress>(_ => { }));

        exitCode.Should().Be(0);
        (await WaitForLauncherMarkerAsync()).Should().Contain("via-ref");
    }

    [Fact]
    public async Task Progress_messages_report_the_replacement_phases()
    {
        SeedPendingUpdate();

        await RunPatcher();

        _progress.Should().Contain(m => m.Contains("Creating backup"));
        _progress.Should().Contain(m => m.Contains("Replacing application files"));
        _progress.Should().Contain(m => m.Contains("applied successfully"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for the throwaway tree.
        }
    }
}
