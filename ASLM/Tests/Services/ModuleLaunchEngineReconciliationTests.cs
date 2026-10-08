// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using ASLM.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers engine preparation performed by the shared module launch pipeline.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleLaunchEngineReconciliationTests
{
    [Theory]
    [InlineData("run", ModuleLaunchStatus.Started)]
    [InlineData("empty", ModuleLaunchStatus.NoRunCommands)]
    [InlineData("invalid", ModuleLaunchStatus.Error)]
    [InlineData("canceled", ModuleLaunchStatus.Error)]
    public async Task Catalog_install_completion_launches_once_and_preserves_installation_on_launch_failure(
        string scenario, ModuleLaunchStatus expected)
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var directory = Path.Combine(layout.ModulesDir, "catalog-launch");
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, ModuleManifestDiscovery.ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, $$"""
        {
          "fileVersion": 2,
          "id": "catalog-launch",
          "name": "Catalog launch",
          "version": "1.0.0",
          "supportedPlatforms": [
            { "os": "{{PlatformInfo.OsKey}}", "arch": "{{PlatformInfo.ArchKey}}" }
          ]
        }
        """);

        var engines = new EngineInstaller();
        var reconciler = new ModuleEngineReconciler(engines);
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, reconciler);
        var coordinator = new ModuleLaunchCoordinator(installer, runner,
            new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
            installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
            NullLogger<UpdateManager>.Instance);
        var module = (await installer.LoadModuleConfig(manifestPath))!;
        module.Status.Installed = true;
        module.Status.FirstRunCompleted = true;
        if (scenario != "empty")
            module.Commands.Run.Add(new ModuleCommand
            {
                Name = "Test process",
                Exec = scenario == "invalid" ? "aslm-nonexistent-test-command"
                    : "cmd.exe /d /c ping -n 30 127.0.0.1"
            });
        // Exercise the final installation stage without network requests or changing real user modules.
        await installer.SaveConfigAsync(module);
        var log = new RecordingProgress();
        using var cts = new CancellationTokenSource();
        if (scenario == "canceled") cts.Cancel();
        try
        {
            if (scenario == "canceled")
            {
                var install = () => manager.InstallCatalogModuleAsync(module, log, ct: cts.Token);
                await install.Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                var result = await manager.InstallCatalogModuleAsync(module, log, ct: cts.Token);
                result.Status.Should().Be(expected);
                if (expected == ModuleLaunchStatus.Started)
                {
                    runner.GetRunningModuleSourcePaths().Should().Contain(manifestPath);
                    var repeated = await manager.InstallCatalogModuleAsync(module, log);
                    repeated.Status.Should().Be(ModuleLaunchStatus.AlreadyRunning);
                    log.Messages.Count(message => message.StartsWith("[Run]", StringComparison.Ordinal)).Should().Be(1);
                }
                else
                    runner.GetRunningModuleSourcePaths().Should().NotContain(manifestPath);
            }
            installer.Registry.ReadIds().Should().Contain(module.Id);
            (await installer.LoadModuleConfig(manifestPath))!.Status.FirstRunCompleted.Should().BeTrue();
            if (scenario == "canceled")
                runner.GetRunningModuleSourcePaths().Should().NotContain(manifestPath);
        }
        finally
        {
            await runner.StopModuleAsync(manifestPath);
        }
    }

    /// <summary>
    /// Verifies that launch installs a missing required engine before completing module setup.
    /// </summary>
    [Fact]
    public async Task Launch_installs_missing_required_module_engine_before_first_run()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);

        var moduleDir = Path.Combine(layout.ModulesDir, "launch-provider");
        var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
        var engineStateDir = Path.Combine(
            layout.Root,
            "Engines",
            "Modules",
            "launch-provider",
            "launch-vendor-runtime");
        ResetDirectory(engineStateDir);

        Directory.CreateDirectory(moduleDir);
        var runtimeDir = Path.Combine(engineStateDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        await File.WriteAllTextAsync(Path.Combine(runtimeDir, "vendor"), "runtime");
        await File.WriteAllTextAsync(manifestPath, BuildManifest());

        var engineInstaller = new EngineInstaller();
        var reconciler = new ModuleEngineReconciler(engineInstaller);
        using var runner = CreateRunner(engineInstaller);
        var moduleInstaller = new ModuleInstaller(runner, null!, reconciler);
        var coordinator = new ModuleLaunchCoordinator(
            moduleInstaller,
            runner,
            new ModuleStartThrottle(),
            NullLogger<ModuleLaunchCoordinator>.Instance);
        var log = new RecordingProgress();

        var result = await coordinator.LaunchOrEnsureRunningBySourcePathAsync(
            manifestPath,
            log,
            CancellationToken.None);

        result.Status.Should().Be(ModuleLaunchStatus.Started);
        result.EffectiveConfig.Should().NotBeNull();
        result.EffectiveConfig!.Status.Installed.Should().BeTrue();
        result.EffectiveConfig.Status.FirstRunCompleted.Should().BeTrue();
        result.EffectiveConfig.Status.Enabled.Should().BeTrue();

        engineInstaller.InvalidateCache();
        var engine = engineInstaller.FindAvailableEngine("launch-vendor-runtime");
        engine.Should().NotBeNull();
        engine!.Status.Installed.Should().BeTrue();
        engine.Status.InstalledManifestHash.Should().NotBeNullOrWhiteSpace();
        log.Messages.Should().Contain(message =>
            message.Contains("Installing required engine", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates the minimal runner graph needed for setup and a no-op run command.
    /// </summary>
    private static ModuleRunner CreateRunner(EngineInstaller engineInstaller)
    {
        var appData = new AppDataStore(NullLogger<AppDataStore>.Instance);
        return new ModuleRunner(
            engineInstaller,
            new ModuleEnvironmentResolver(engineInstaller),
            new PortRegistry(appData),
            null!,
            new ModuleConsoleStore(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new ModuleInteropHostState(),
            new EmptyServiceProvider(),
            NullLogger<ModuleRunner>.Instance);
    }

    /// <summary>
    /// Builds a portable v2 module manifest with one required embedded engine.
    /// </summary>
    private static string BuildManifest()
    {
        return $$"""
        {
          "fileVersion": 2,
          "id": "launch-provider",
          "name": "Launch Provider",
          "version": "1.0.0",
          "supportedPlatforms": [
            { "os": "{{PlatformInfo.OsKey}}", "arch": "{{PlatformInfo.ArchKey}}" }
          ],
          "dependencies": {
            "engines": [ { "id": "launch-vendor-runtime" } ]
          },
          "commands": {
            "run": [ { "name": "No-op run", "exec": "cmd.exe /d /c ping -n 2 127.0.0.1" } ]
          },
          "engines": [
            {
              "fileVersion": 2,
              "id": "launch-vendor-runtime",
              "name": "Launch Vendor Runtime",
              "version": "1.0.0",
              "supportedPlatforms": [
                {
                  "os": "{{PlatformInfo.OsKey}}",
                  "arch": "{{PlatformInfo.ArchKey}}",
                  "key": "{{PlatformInfo.PlatformKey}}"
                }
              ],
              "{{PlatformInfo.PlatformKey}}": {
                "executablePath": "runtime/vendor",
                "install": []
              }
            }
          ]
        }
        """;
    }

    /// <summary>
    /// Recreates a test directory so persisted engine state cannot affect the launch result.
    /// </summary>
    private static void ResetDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// Collects launch messages safely across progress callbacks and worker threads.
    /// </summary>
    private sealed class RecordingProgress : IProgress<string>
    {
        private readonly object _lock = new();
        private readonly List<string> _messages = [];

        /// <summary>
        /// Returns a stable snapshot of recorded launch messages.
        /// </summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_lock)
                {
                    return _messages.ToList();
                }
            }
        }

        /// <summary>
        /// Records one launch message for later assertions.
        /// </summary>
        public void Report(string value)
        {
            lock (_lock)
            {
                _messages.Add(value);
            }
        }
    }

    /// <summary>
    /// Supplies no optional services for a module without module dependencies.
    /// </summary>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        /// <summary>
        /// Returns no service because this fixture does not resolve optional dependencies.
        /// </summary>
        public object? GetService(Type serviceType) => null;
    }
}
