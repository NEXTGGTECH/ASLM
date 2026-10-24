// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// The full self-update contract: the real <see cref="UpdateManager"/> stages a pending
/// update, then the real <c>aslm-patcher</c> executable applies it to a throwaway install.
/// </summary>
public sealed class SelfUpdateContractTests
{
    private static string PatcherExecutable => Path.Combine(
        GetRepoRoot(AppDomain.CurrentDomain.BaseDirectory),
        "ASLM", "Patcher", "Patcher-CLI", "bin", "Debug", "net10.0",
        OperatingSystem.IsWindows() ? "aslm-patcher.exe" : "aslm-patcher");

    private static string GetRepoRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ASLM.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task UpdateManager_stages_and_real_patcher_applies_the_self_update()
    {
        using var graph = new TestServiceGraph();
        // A realistic installation tree: the app under App/, user data under Data/.
        var appDir = Path.Combine(graph.Root.Root, "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "ASLM.dll"), "old-app");
        File.WriteAllText(Path.Combine(graph.Root.DataAppDir, "ASLM_Data.json"),
            """{ "updates": { "checkEnabled": true } }""");
        File.WriteAllText(Path.Combine(graph.Root.DataAppDir, "user.txt"), "user data");

        var config = new AppUpdateSourceConfig
        {
            Source = new ModuleSource { Type = "github", Repo = "NEXTGGTECH/ASLM" },
            Assets = new Dictionary<string, string> { ["windows-amd64"] = "ASLM-windows-amd64.zip" },
            Preserve = ["Data", "Engines", "Models", "Modules", ".aslm-update/logs"]
        };
        config.Normalize();
        graph.Root.WriteDataAppFile("ASLM_UpdateSource.json", JsonSerializer.Serialize(config,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var zipPath = Path.Combine(graph.Root.Root, "payload.zip");
        TestArchives.WriteZip(zipPath,
            new TestArchives.Entry("wrapper/App/ASLM.dll", "new-app"),
            new TestArchives.Entry("wrapper/App/Patcher/patcher.dll", "patcher"));
        var payload = await File.ReadAllBytesAsync(zipPath);
        graph.GitHubHttp.OnBytes("https://update.test/v42.0.0.zip", payload);

        var candidate = new UpdateCandidate
        {
            TargetKind = "app",
            TargetId = "app",
            Name = "ASLM",
            RemoteVersion = "42.0.0",
            DownloadUrl = "https://update.test/v42.0.0.zip"
        };
        // Stage through the app's real update pipeline into a real pending.json.
        (await graph.Updates.PrepareAppUpdateAsync(candidate)).Should().BeTrue();

        var pendingPath = Path.Combine(graph.Root.Root, ".aslm-update", "pending.json");
        File.Exists(pendingPath).Should().BeTrue("the app side of the contract published the marker");

        // Apply with the real, freshly built patcher executable using the launcher's
        // documented argument protocol (--root, --launcher, --wait-process, forwarded args).
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = PatcherExecutable,
            UseShellExecute = false,
            WorkingDirectory = graph.Root.Root
        };
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(graph.Root.Root);
        startInfo.ArgumentList.Add("--launcher");
        startInfo.ArgumentList.Add(powerShell);
        // A PID that no longer exists makes the wait a no-op; the patch proceeds.
        startInfo.ArgumentList.Add("--wait-process");
        startInfo.ArgumentList.Add("999999999");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("exit 0");

        using var patcher = Process.Start(startInfo);
        patcher.Should().NotBeNull("the patcher executable must build alongside the tests");
        await patcher!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));

        var logPath = Path.Combine(graph.Root.Root, ".aslm-update", "logs", "Patcher.log");
        var log = File.Exists(logPath) ? string.Join(Environment.NewLine, await File.ReadAllLinesAsync(logPath)) : "no patcher log";
        patcher.ExitCode.Should().Be(0, log);

        // The installed tree now carries the new payload.
        File.ReadAllText(Path.Combine(graph.Root.Root, "App", "ASLM.dll")).Should().Be("new-app");
        File.Exists(Path.Combine(graph.Root.Root, "App", "Patcher", "patcher.dll")).Should().BeTrue();
        // Preserved user data survived the replacement.
        File.ReadAllText(Path.Combine(graph.Root.DataAppDir, "user.txt")).Should().Be("user data");
        var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(graph.Root.DataAppDir, "ASLM_Data.json")));
        data.RootElement.GetProperty("updates").GetProperty("installedReleaseTag").GetString()
            .Should().Be("42.0.0");
        data.RootElement.GetProperty("updates").GetProperty("checkEnabled").GetBoolean().Should().BeTrue();
        File.Exists(pendingPath).Should().BeFalse();
    }
}
