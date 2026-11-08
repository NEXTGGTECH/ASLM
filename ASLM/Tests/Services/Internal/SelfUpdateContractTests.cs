// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the full self-update contract: the real <see cref="UpdateManager"/> stages a pending
    /// update, then the real <c>aslm-patcher</c> executable applies it to a throwaway install.
    /// </summary>
    public sealed class SelfUpdateContractTests
    {
        // Patcher location

        /// <summary>
        /// Path of the patcher executable that the build of the patcher CLI project (Debug configuration) places in
        /// the repository.
        /// </summary>
        private static string PatcherExecutable => Path.Combine(
            GetRepoRoot(AppDomain.CurrentDomain.BaseDirectory),
            "ASLM", "Patcher", "Patcher-CLI", "bin", "Debug", "net10.0",
            OperatingSystem.IsWindows() ? "aslm-patcher.exe" : "aslm-patcher");

        /// <summary>
        /// Finds the repository root by walking up from the given directory until the solution file is found.
        /// </summary>
        /// <param name="start">Directory from which the search starts.</param>
        /// <returns>The full path of the repository root.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no parent directory contains the solution file.</exception>
        private static string GetRepoRoot(string start)
        {
            var directory = new DirectoryInfo(start);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "ASLM.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }


        // Self-update contract

        /// <summary>
        /// Verifies the complete self-update hand-off: the update manager downloads and stages an app update
        /// (publishing a pending marker), and the real patcher executable, started with the launcher's argument
        /// protocol, replaces the application files, keeps the preserved user data, records the installed version and
        /// removes the marker.
        /// </summary>
        [Fact]
        public async Task UpdateManager_stages_and_real_patcher_applies_the_self_update()
        {
            // Arrange: a throwaway installation with an old application file and user data to preserve.
            using var graph = new TestServiceGraph();

            // A realistic installation tree: the app under App/, user data under Data/.
            var appDir = Path.Combine(graph.Root.Root, "App");
            Directory.CreateDirectory(appDir);
            File.WriteAllText(Path.Combine(appDir, "ASLM.dll"), "old-app");
            File.WriteAllText(Path.Combine(graph.Root.DataAppDir, "ASLM_Data.json"),
                """{ "updates": { "checkEnabled": true } }""");
            File.WriteAllText(Path.Combine(graph.Root.DataAppDir, "user.txt"), "user data");

            // The update source declares the asset name and the folders that an update must preserve.
            var config = new AppUpdateSourceConfig
            {
                Source = new ModuleSource { Type = "github", Repo = "NEXTGGTECH/ASLM" },
                Assets = new Dictionary<string, string> { ["windows-amd64"] = "ASLM-windows-amd64.zip" },
                Preserve = ["Data", "Engines", "Models", "Modules", ".aslm-update/logs"]
            };
            config.Normalize();
            graph.Root.WriteDataAppFile("ASLM_UpdateSource.json", JsonSerializer.Serialize(config,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

            // The update archive that the fake download server returns.
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

            // Act, app side: stage through the app's real update pipeline into a real pending.json.
            (await graph.Updates.PrepareAppUpdateAsync(candidate)).Should().BeTrue();

            var pendingPath = Path.Combine(graph.Root.Root, ".aslm-update", "pending.json");
            File.Exists(pendingPath).Should().BeTrue("the app side of the contract published the marker");

            // Act, patcher side: apply with the real, freshly built patcher executable using the launcher's
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

            // Run the patcher to completion; on failure its log is attached to the assertion message.
            using var patcher = Process.Start(startInfo);
            patcher.Should().NotBeNull("the patcher executable must build alongside the tests");
            await patcher!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));

            var logPath = Path.Combine(graph.Root.Root, ".aslm-update", "logs", "Patcher.log");
            var log = File.Exists(logPath) ? string.Join(Environment.NewLine, await File.ReadAllLinesAsync(logPath)) : "no patcher log";
            patcher.ExitCode.Should().Be(0, log);

            // Assert: the installed tree now carries the new payload.
            File.ReadAllText(Path.Combine(graph.Root.Root, "App", "ASLM.dll")).Should().Be("new-app");
            File.Exists(Path.Combine(graph.Root.Root, "App", "Patcher", "patcher.dll")).Should().BeTrue();

            // Preserved user data survived the replacement.
            File.ReadAllText(Path.Combine(graph.Root.DataAppDir, "user.txt")).Should().Be("user data");
            var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(graph.Root.DataAppDir, "ASLM_Data.json")));
            data.RootElement.GetProperty("updates").GetProperty("installedReleaseTag").GetString()
                .Should().Be("42.0.0");
            data.RootElement.GetProperty("updates").GetProperty("checkEnabled").GetBoolean().Should().BeTrue();

            // The pending marker is consumed once the update was applied.
            File.Exists(pendingPath).Should().BeFalse();
        }
    }
}
