// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers engine preparation performed by the shared module launch pipeline.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleLaunchEngineReconciliationTests
{
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
            "run": [ { "name": "No-op run", "exec": "" } ]
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
