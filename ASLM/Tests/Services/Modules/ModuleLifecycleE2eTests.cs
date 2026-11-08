// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the real module lifecycle over the production installer, coordinator, runner and
    /// console store: archive install, first-run, launch, console capture, stop, removal,
    /// plus the stdio downloads-bridge protocol.
    /// </summary>
    public sealed class ModuleLifecycleE2eTests
    {
        // Module lifecycle

        /// <summary>
        /// Verifies the full path of a module: installing it from a zip served over HTTP (including its first-run
        /// command), launching it once and recognizing a second launch as already running, stopping its process
        /// tree (its console sessions then report as not running), and uninstalling it down to a retained
        /// not-installed manifest.
        /// </summary>
        [Fact]
        public async Task Install_first_run_launch_console_stop_and_uninstall_end_to_end()
        {
            // Arrange: a module folder and manifest whose first-run command writes a marker file.
            using var graph = new TestServiceGraph();
            var moduleId = $"e2e-{Guid.NewGuid():N}";
            var moduleDir = Path.Combine(graph.Root.ModulesDir, moduleId);
            Directory.CreateDirectory(moduleDir);
            var firstRunMarker = Path.Combine(moduleDir, "first-run.txt");
            var manifestJson = BuildManifestJson(moduleId, firstRunMarker);

            // Serve the module zipball from a real local HTTP server, exactly like a GitHub install.
            var zipPath = Path.Combine(graph.Root.Root, $"{moduleId}.zip");
            TestArchives.WriteZip(zipPath,
                new TestArchives.Entry($"src-{moduleId}/ASLM_Module.json", manifestJson),
                new TestArchives.Entry($"src-{moduleId}/tool.bin", "tool"));
            using var server = LoopbackHttpServer.Start();
            server.ServeBytes("module.zip", await File.ReadAllBytesAsync(zipPath));

            // 1. Install: download, extract, parse, install and run first-run commands.
            var installed = await graph.Installer.InstallFromUrlAsync(
                $"{server.BaseUri}module.zip", new Progress<string>(_ => { }));

            installed.Id.Should().Be(moduleId);
            installed.Status.Installed.Should().BeTrue();
            File.Exists(Path.Combine(moduleDir, "tool.bin")).Should().BeTrue("module files land in Modules/{id}");
            await TestWait.UntilAsync(() => File.Exists(firstRunMarker),
                because: "the first-run command must execute during installation");
            (await graph.Installer.DiscoverInstalledModulesAsync()).Should().Contain(m => m.Id == moduleId);

            // 2. Launch through the coordinator and observe the console session.
            var launch = await graph.Coordinator.LaunchOrEnsureRunningAsync(
                moduleId, new Progress<string>(_ => { }), CancellationToken.None);
            launch.Status.Should().Be(ModuleLaunchStatus.Started);
            await TestWait.UntilAsync(() => graph.Runner.GetRunningModuleSourcePaths().Count != 0,
                because: "the module process must register with the runner");
            var sourcePath = graph.Runner.GetRunningModuleSourcePaths().Single();
            sourcePath.Should().Be(Path.Combine(moduleDir, "ASLM_Module.json"));
            (await graph.Coordinator.LaunchOrEnsureRunningAsync(
                moduleId, new Progress<string>(_ => { }), CancellationToken.None)).Status
                .Should().Be(ModuleLaunchStatus.AlreadyRunning);

            // 3. Stop: the run command's process tree is reaped.
            await graph.Runner.StopModuleAsync(sourcePath).WaitAsync(TimeSpan.FromSeconds(15));
            graph.Runner.GetRunningModuleSourcePaths().Should().BeEmpty();
            var snapshot = graph.Consoles.GetSnapshot().Single(m => m.SourcePath == sourcePath);
            snapshot.Sessions.Should().OnlyContain(s => !s.IsRunning);

            // 4. Uninstall: the module's files leave the installation and the registry; only a reset manifest stays.
            var installedConfig = (await graph.Installer.DiscoverModulesAsync()).Single(m => m.Id == moduleId);
            var error = await graph.Installer.UninstallAsync(installedConfig);
            error.Should().BeNull("uninstall must succeed without diagnostics");

            // Uninstall keeps a reset manifest (for future reinstall) but removes the payload files.
            File.Exists(Path.Combine(moduleDir, "tool.bin")).Should().BeFalse("module payload files are removed");
            var reset = ModuleManifestParser.Parse(await File.ReadAllTextAsync(
                Path.Combine(moduleDir, "ASLM_Module.json")));
            reset.Status.Installed.Should().BeFalse("the retained manifest describes a not-installed module");
            (await graph.Installer.DiscoverInstalledModulesAsync()).Should().NotContain(m => m.Id == moduleId);
        }


        // Downloads bridge

        /// <summary>
        /// Verifies that a downloads bridge started as a real process receives the JSON request on standard input
        /// (operation and protocol version) and that its JSON answer on standard output is parsed into the response.
        /// </summary>
        [Fact]
        public async Task Downloads_bridge_round_trips_the_stdio_json_protocol()
        {
            // Arrange: a module folder for the bridge.
            using var graph = new TestServiceGraph();
            var moduleId = $"bridge-{Guid.NewGuid():N}";
            var moduleDir = Path.Combine(graph.Root.ModulesDir, moduleId);
            Directory.CreateDirectory(moduleDir);

            // A bridge that records the incoming request and answers with one category.
            var requestMarker = Path.Combine(moduleDir, "bridge-request.json");
            var bridgeScript = Path.Combine(moduleDir, "bridge.ps1");
            var bridgeJson = "{\"protocolVersion\":1,\"success\":true,\"categories\":[{\"id\":\"models\",\"title\":\"Models\",\"description\":\"d\",\"sortOrder\":1}]}";
            File.WriteAllText(bridgeScript, string.Join(Environment.NewLine,
                "$request = [Console]::In.ReadToEnd()",
                $"[System.IO.File]::WriteAllText('{requestMarker}', $request)",
                $"[Console]::Out.Write('{bridgeJson}')"), new UTF8Encoding(true));

            var module = ModuleConfigBuilder.Create(id: moduleId, configure: config =>
            {
                config.SourcePath = Path.Combine(moduleDir, "ASLM_Module.json");
                config.DownloadsBridge = new ModuleDownloadsBridge
                {
                    EntryPoint = $"powershell.exe -NoProfile -NonInteractive -File \"{bridgeScript}\""
                };
            });

            // Act: send a list_categories request through the bridge.
            var response = await graph.Bridge.InvokeAsync(module, new ModuleDownloadBridgeRequest
            {
                Operation = "list_categories"
            });

            // Assert: the script's category was parsed, and the request it recorded carries the operation and version.
            response.Success.Should().BeTrue(response.Error);
            response.Categories.Should().ContainSingle().Which.Id.Should().Be("models");
            var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(requestMarker));
            recorded.RootElement.GetProperty("operation").GetString().Should().Be("list_categories");
            recorded.RootElement.GetProperty("protocolVersion").GetInt32().Should().BeGreaterThan(0);
        }


        // Test helpers

        /// <summary>
        /// Builds the JSON manifest of a version 2 web module for the current platform: an empty install command
        /// list, a first-run command that writes a marker file, and a run command that sleeps for a minute.
        /// </summary>
        /// <param name="moduleId">Module id.</param>
        /// <param name="firstRunMarker">Path of the file that the first-run command writes.</param>
        private static string BuildManifestJson(string moduleId, string firstRunMarker) =>
            JsonSerializer.Serialize(new
            {
                fileVersion = 2,
                id = moduleId,
                name = "Lifecycle Module",
                description = "e2e",
                version = "1.0.0",
                author = "ASLM Tests",
                type = "web",
                supportedPlatforms = new[] { new { os = PlatformInfo.OsKey, arch = PlatformInfo.ArchKey } },
                commands = new
                {
                    install = Array.Empty<object>(),
                    firstRun = new[]
                    {
                        new
                        {
                            name = "First run",
                            exec = $"powershell.exe -NoProfile -NonInteractive -Command Set-Content -Path '{firstRunMarker}' -Value done"
                        }
                    },
                    run = new[]
                    {
                        new { name = "Run", exec = "powershell.exe -NoProfile -NonInteractive -Command Start-Sleep -Seconds 60" }
                    }
                },
                settings = Array.Empty<object>()
            });
    }
}
