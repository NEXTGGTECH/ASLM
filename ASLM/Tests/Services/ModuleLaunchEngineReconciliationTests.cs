// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using ASLM.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers engine preparation performed by the shared module launch pipeline.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class ModuleLaunchEngineReconciliationTests
{
    [Fact]
    public async Task Module_installs_do_not_wait_for_the_bridge_download_queue()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var module = await WriteCatalogModuleAsync(layout, "queued-module");
        var engines = new EngineInstaller();
        var reconciler = new ModuleEngineReconciler(engines);
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, reconciler);
        var coordinator = new ModuleLaunchCoordinator(installer, runner,
            new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
        var queue = new DownloadQueue();
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
            installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
            NullLogger<UpdateManager>.Instance, queue);
        var downloads = new DownloadInstaller(installer, null!, engines, null!, null!, null!,
            NullLogger<DownloadInstaller>.Instance, queue);
        // Already prepared, no commands or network needed to verify scheduling through the real entry points.
        module.Status.Installed = module.Status.FirstRunCompleted = true;
        await installer.SaveConfigAsync(module);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseModules = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preceding = queue.EnqueueAsync("preceding", async _ => { await release.Task; return true; });
        var precedingModule = queue.EnqueueAsync("preceding-module", async _ => { await releaseModules.Task; return true; },
            kind: DownloadQueueKind.Modules);
        try
        {
            var moduleJob = manager.InstallCatalogModuleAsync(module, new RecordingProgress());
            var item = new DownloadCatalogItem { ResourceKey = "queued-resource" };
            var resourceJob = downloads.InstallAsync(item, null);
            manager.IsCatalogInstallPending(module.Id).Should().BeTrue();
            downloads.IsPending(item, null).Should().BeTrue();
            resourceJob.IsCompleted.Should().BeFalse();
            manager.InstallCatalogModuleAsync(module, new RecordingProgress()).Should().BeSameAs(moduleJob);
            downloads.InstallAsync(item, null).Should().BeSameAs(resourceJob);
            releaseModules.SetResult();
            (await moduleJob.WaitAsync(TimeSpan.FromSeconds(5))).Status.Should().Be(ModuleLaunchStatus.NoRunCommands);
            preceding.IsCompleted.Should().BeFalse();
            resourceJob.IsCompleted.Should().BeFalse();
            release.SetResult();
            await Task.WhenAll(preceding, precedingModule, moduleJob, resourceJob).WaitAsync(TimeSpan.FromSeconds(5));
            (await moduleJob).Status.Should().Be(ModuleLaunchStatus.NoRunCommands);
            (await resourceJob).Success.Should().BeFalse(); // no source, handled normally without blocking the queue
            manager.IsCatalogInstallPending(module.Id).Should().BeFalse();
            downloads.IsPending(item, null).Should().BeFalse();
        }
        finally { release.TrySetResult(); releaseModules.TrySetResult(); }
    }

    [Fact]
    public async Task Catalog_install_announces_all_pending_dependencies_before_downloading_and_clears_them_on_cancel()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var ready = await WriteCatalogModuleAsync(layout, "already-installed");
        var leaf = await WriteCatalogModuleAsync(layout, "pending-leaf", ready.Id);
        var dependency = await WriteCatalogModuleAsync(layout, "pending-dependency", leaf.Id);
        var root = await WriteCatalogModuleAsync(layout, "pending-root", dependency.Id);
        var pending = new[] { root, dependency, leaf };
        var engines = new EngineInstaller();
        var reconciler = new ModuleEngineReconciler(engines);
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, reconciler);
        var coordinator = new ModuleLaunchCoordinator(installer, runner,
            new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
            installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
            NullLogger<UpdateManager>.Instance, new DownloadQueue());
        ready.Status.Installed = ready.Status.FirstRunCompleted = true;
        await installer.SaveConfigAsync(ready);
        using var cts = new CancellationTokenSource();
        var announced = false;
        installer.ModulesChanged += (_, _) =>
        {
            if (!installer.IsInstalling(root.Id)) return;
            announced = true;
            foreach (var module in pending)
            {
                installer.IsInstalling(module.Id).Should().BeTrue();
                installer.GetInstallationState(module.Id)!.Phase.Should().Be(DownloadOperationState.Queued);
                installer.GetInstallationState(module.Id)!.IsDependency.Should().Be(module.Id != root.Id);
            }
            installer.IsInstalling(ready.Id).Should().BeFalse();
            installer.Registry.ReadIds().Should().Equal(ready.Id);
            // Cancel before candidate resolution: this test must never request a real repository.
            cts.Cancel();
        };

        var install = () => manager.InstallCatalogModuleAsync(root, new RecordingProgress(), ct: cts.Token);
        await install.Should().ThrowAsync<OperationCanceledException>();

        announced.Should().BeTrue();
        foreach (var module in pending)
        {
            installer.IsInstalling(module.Id).Should().BeFalse();
            runner.ConsoleStore.GetMaintenanceSnapshot(module.SourcePath).Activity.Should().BeNull();
        }
        (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should()
            .BeEquivalentTo(pending.Select(module => module.Id));
        (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
            .Should().Equal(ready.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_catalog_cards_survive_refresh_and_only_downloaded_modules_remain_after_the_operation(bool complete)
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var dependency = await WriteCatalogModuleAsync(layout, "pending-dependency");
        var root = await WriteCatalogModuleAsync(layout, "pending-root", dependency.Id);
        var engines = new EngineInstaller();
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
        var ids = new[] { root.Id, dependency.Id };
        var changes = 0;
        installer.ModulesChanged += (_, _) => changes++;
        using (installer.BeginCatalogInstallation(root, [dependency, root]))
        {
            changes.Should().Be(1);
            installer.Registry.ReadIds().Should().BeEmpty();
            (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should().BeEquivalentTo(ids);
            (await installer.DiscoverInstalledModulesAsync()).Should().BeEmpty();
            (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                .Should().BeEquivalentTo(ids);
            // The updater can temporarily move the original manifest out of the live module directory.
            var manifest = await File.ReadAllTextAsync(root.SourcePath);
            File.Delete(root.SourcePath);
            try
            {
                (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                    .Should().BeEquivalentTo(ids);
            }
            finally { await File.WriteAllTextAsync(root.SourcePath, manifest); }

            dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(dependency);
            if (complete)
            {
                root.Status.Installed = root.Status.FirstRunCompleted = true;
                await installer.SaveConfigAsync(root);
            }
            (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id).Should().BeEquivalentTo(ids);
            (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                .Should().BeEquivalentTo(ids);
            installer.IsInstalling(root.Id).Should().BeTrue();
            installer.IsInstalling(dependency.Id).Should().BeTrue();
        }

        changes.Should().Be(complete ? 4 : 3);
        installer.IsInstalling(root.Id).Should().BeFalse();
        installer.IsInstalling(dependency.Id).Should().BeFalse();
        runner.ConsoleStore.GetMaintenanceSnapshot(root.SourcePath).Activity.Should().BeNull();
        runner.ConsoleStore.GetMaintenanceSnapshot(dependency.SourcePath).Activity.Should().BeNull();
        var installed = complete ? ids : new[] { dependency.Id };
        installer.Registry.ReadIds().Should().BeEquivalentTo(installed);
        (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
            .Should().BeEquivalentTo(installed);
        (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id)
            .Should().BeEquivalentTo(complete ? Array.Empty<string>() : new[] { root.Id });
    }

    [Fact]
    public async Task Removal_queued_before_a_new_dependency_finishes_before_that_dependency_is_reinstalled()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var dependency = await WriteCatalogModuleAsync(layout, "remove-then-reinstall");
        var root = await WriteCatalogModuleAsync(layout, "new-consumer", dependency.Id);
        var engines = new EngineInstaller();
        var reconciler = new ModuleEngineReconciler(engines);
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, reconciler);
        var coordinator = new ModuleLaunchCoordinator(installer, runner,
            new ModuleStartThrottle(), NullLogger<ModuleLaunchCoordinator>.Instance);
        var queue = new DownloadQueue();
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance),
            installer, engines, runner, coordinator, null!, reconciler, null!, null!, null!,
            NullLogger<UpdateManager>.Instance, queue);
        dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
        await installer.SaveConfigAsync(dependency);
        var payload = Path.Combine(Path.GetDirectoryName(dependency.SourcePath)!, "payload.txt");
        await File.WriteAllTextAsync(payload, "installed dependency");
        var releaseRemoval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = queue.EnqueueAsync("removal-blocker", async _ => { await releaseRemoval.Task; return true; },
            kind: DownloadQueueKind.ModuleRemoval);
        using var cts = new CancellationTokenSource();
        var reinstallReached = false;
        installer.ModulesChanged += (_, _) =>
        {
            if (!installer.IsInstalling(root.Id)) return;
            prepared.TrySetResult();
            if (installer.GetInstallationState(dependency.Id)?.Phase != DownloadOperationState.Running) return;
            reinstallReached = true;
            installer.Registry.ReadIds().Should().NotContain(dependency.Id);
            File.Exists(payload).Should().BeFalse();
            // Verify entry into reinstallation, but never download from a real repository in this test.
            cts.Cancel();
        };
        Task? install = null;
        Task<string?>? removal = null;
        try
        {
            removal = manager.UninstallModuleAsync(dependency);
            installer.GetRemovalState(dependency.Id).Should().Be(DownloadOperationState.Queued);
            install = manager.InstallCatalogModuleAsync(root, new RecordingProgress(), ct: cts.Token);
            await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
            installer.GetInstallationState(dependency.Id)!.IsDependency.Should().BeTrue();
            (await installer.DiscoverAvailableModulesAsync()).Select(module => module.Id)
                .Should().BeEquivalentTo([root.Id, dependency.Id]);
            (await installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Select(module => module.Id)
                .Should().BeEquivalentTo([root.Id, dependency.Id]);
            install.IsCompleted.Should().BeFalse();
            releaseRemoval.SetResult();
            await removal.WaitAsync(TimeSpan.FromSeconds(5));
            await FluentActions.Awaiting(() => install.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<OperationCanceledException>();
            reinstallReached.Should().BeTrue();
            installer.GetRemovalState(dependency.Id).Should().BeNull();
        }
        finally
        {
            cts.Cancel();
            releaseRemoval.TrySetResult();
            await blocker;
            if (removal != null) await removal;
            if (install != null) try { await install; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Already_queued_install_reserves_dependencies_against_new_uninstall_requests()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var dependency = await WriteCatalogModuleAsync(layout, "reserved-dependency");
        var root = await WriteCatalogModuleAsync(layout, "queued-consumer", dependency.Id);
        var engines = new EngineInstaller();
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
        dependency.Status.Installed = dependency.Status.FirstRunCompleted = true;
        await installer.SaveConfigAsync(dependency);
        using var request = installer.BeginCatalogInstallation(root, [dependency, root]);
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance), installer,
            engines, runner, null!, null!, null!, null!, null!, null!, NullLogger<UpdateManager>.Instance, new DownloadQueue());

        await FluentActions.Awaiting(() => manager.UninstallModuleAsync(dependency))
            .Should().ThrowAsync<InvalidOperationException>();
        installer.Registry.ReadIds().Should().Contain(dependency.Id);
    }

    [Fact]
    public async Task Multiple_uninstalls_queue_and_complete_while_the_other_two_queues_are_busy()
    {
        using var layout = new AslmFileSystemLayout();
        ResetDirectory(layout.ModulesDir);
        var first = await WriteCatalogModuleAsync(layout, "remove-first");
        var second = await WriteCatalogModuleAsync(layout, "remove-second");
        var engines = new EngineInstaller();
        using var runner = CreateRunner(engines);
        var installer = new ModuleInstaller(runner, null!, new ModuleEngineReconciler(engines));
        var queue = new DownloadQueue();
        var manager = new UpdateManager(new AppDataStore(NullLogger<AppDataStore>.Instance), installer,
            engines, runner, null!, null!, null!, null!, null!, null!, NullLogger<UpdateManager>.Instance, queue);
        foreach (var module in new[] { first, second })
        {
            module.Status.Installed = module.Status.FirstRunCompleted = true;
            await installer.SaveConfigAsync(module);
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new[] { DownloadQueueKind.Resources, DownloadQueueKind.Modules }
            .Select(kind => queue.EnqueueAsync(kind.ToString(), async _ => { await release.Task; return true; }, kind: kind)).ToArray();
        using var content = ModuleInstaller.BeginContentOperation(new ModuleConfig { Id = "unrelated-install" });
        try
        {
            var firstRemoval = manager.UninstallModuleAsync(first);
            var secondRemoval = manager.UninstallModuleAsync(second);
            await Task.WhenAll(firstRemoval, secondRemoval).WaitAsync(TimeSpan.FromSeconds(5));
            installer.Registry.ReadIds().Should().NotContain(first.Id).And.NotContain(second.Id);
            installer.GetRemovalState(first.Id).Should().BeNull();
            installer.GetRemovalState(second.Id).Should().BeNull();
            jobs.Should().OnlyContain(job => !job.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(jobs); }
    }

    private static async Task<ModuleConfig> WriteCatalogModuleAsync(AslmFileSystemLayout layout, string id, params string[] dependencies)
    {
        var module = new ModuleConfig
        {
            FileVersion = 2, Id = id, Name = id, Version = "1.0.0",
            Source = new() { Type = "github", Repo = "test/catalog" },
            SupportedPlatforms = [SupportedPlatform.FromKey(PlatformInfo.PlatformKey)],
            SourcePath = Path.Combine(layout.ModulesDir, id, ModuleManifestDiscovery.ManifestFileName)
        };
        module.Dependencies.Modules.AddRange(dependencies.Select(dependency => new ModuleModuleDependency { Id = dependency }));
        Directory.CreateDirectory(Path.GetDirectoryName(module.SourcePath)!);
        await File.WriteAllTextAsync(module.SourcePath, JsonSerializer.Serialize(module));
        return module;
    }

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
            NullLogger<UpdateManager>.Instance, new DownloadQueue());
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
