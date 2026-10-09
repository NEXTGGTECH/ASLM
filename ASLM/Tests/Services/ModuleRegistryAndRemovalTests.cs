// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleRegistryAndRemovalTests
{
    [Theory]
    [InlineData(DownloadOperationState.Queued, true, "ASLM Code", true)]
    [InlineData(DownloadOperationState.Running, true, "ASLM Code, Example", true)]
    [InlineData(DownloadOperationState.Removing, true, "ASLM Code", false)]
    [InlineData(DownloadOperationState.Running, false, "ASLM Code", false)]
    [InlineData(DownloadOperationState.Queued, true, "", false)]
    public void Download_dependency_status_includes_requester_only_for_dependency_installation(
        DownloadOperationState phase, bool dependency, string requester, bool includesRequester)
    {
        var state = new ModuleInstaller.InstallationState(phase, dependency, requester);
        if (includesRequester)
        {
            state.LabelWithRequester.Should().Contain(requester).And.NotContain("{0}");
            state.Label.Should().NotContain(requester, "dashboard statuses stay compact");
        }
        else state.LabelWithRequester.Should().Be(state.Label);
    }

    [Theory]
    [InlineData("aslm-chat", true)]
    [InlineData("ASLM-CHAT", true)]
    [InlineData("aslm-code", false)]
    [InlineData("aslm-chat-example", false)]
    public void Required_modules_are_matched_by_exact_case_insensitive_id(string id, bool required)
    {
        ModuleRegistry.IsRequired(id).Should().Be(required);
    }

    [Theory]
    [InlineData("aslm-chat")]
    [InlineData("ASLM-CHAT")]
    public async Task Uninstall_skips_required_modules_without_changing_content_or_registry(string id)
    {
        using var fixture = new InstalledFixture(id);
        var manifest = File.ReadAllText(fixture.Module.SourcePath);
        var engineManifest = File.ReadAllText(fixture.Engine.SourcePath);
        var registryPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Modules.json");
        var registry = File.ReadAllText(registryPath);
        var changes = 0;
        fixture.Installer.ModulesChanged += (_, _) => changes++;

        (await fixture.Installer.UninstallAsync(fixture.Module)).Should().BeNull();

        File.ReadAllText(fixture.Module.SourcePath).Should().Be(manifest);
        File.ReadAllText(fixture.Engine.SourcePath).Should().Be(engineManifest);
        File.ReadAllText(registryPath).Should().Be(registry);
        File.ReadAllText(fixture.Payload).Should().Be("installed content");
        Directory.Exists(fixture.Runtime).Should().BeTrue();
        Directory.Exists(fixture.Environment).Should().BeTrue();
        Directory.Exists(fixture.Models).Should().BeTrue();
        changes.Should().Be(0);
        // A rejected uninstall must not hold the global content-operation lock.
        using var operation = ModuleInstaller.BeginContentOperation();
    }

    [Fact]
    public void Registry_migrates_installed_manifests_once_and_respects_an_empty_registry()
    {
        var root = Directory.CreateTempSubdirectory("ASLM-RegistryTest-").FullName;
        try
        {
            WriteModule(root, "installed", installed: true);
            WriteModule(root, "catalog", installed: false);
            var registry = new ModuleRegistry(root);
            registry.ReadIds().Should().Equal("installed");
            registry.Remove("installed");
            new ModuleRegistry(root).ReadIds().Should().BeEmpty();
            JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "Data", "App", "ASLM_Modules.json")))
                .Should().BeEmpty();
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Registry_keeps_concurrent_registrations_and_does_not_overwrite_corrupt_data()
    {
        var root = Directory.CreateTempSubdirectory("ASLM-RegistryTest-").FullName;
        try
        {
            Parallel.For(0, 20, id => new ModuleRegistry(root).Add("module-" + id));
            var registry = new ModuleRegistry(root);
            registry.ReadIds().Should().HaveCount(20);
            var path = Path.Combine(root, "Data", "App", "ASLM_Modules.json");
            File.WriteAllText(path, "broken registry");
            var read = () => registry.ReadIds();
            read.Should().Throw<JsonException>();
            File.ReadAllText(path).Should().Be("broken registry");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Removal_keeps_shared_engines_models_and_colliding_environment_names()
    {
        var root = Path.Combine(Path.GetTempPath(), "ASLM-RemovalPlan-" + Guid.NewGuid().ToString("N"));
        var target = MakeModule(root, "example_one");
        var other = MakeModule(root, "example-one");
        var engine = MakeEngine(root, "shared-engine");
        target.Dependencies.Engines.Add(new ModuleEngineDependency { Id = engine.Id });
        // Commands are consumers even when the manifest omitted dependencies.engines.
        other.Commands.Run.Add(new ModuleCommand { Engine = engine.Id });

        var plan = ModuleInstaller.BuildRemovalPlan(root, target, [other], [engine]);

        plan.Engines.Should().BeEmpty();
        plan.Directories.Should().BeEmpty();
    }

    [Fact]
    public void Removal_keeps_shared_stores_but_removes_the_modules_own_environment()
    {
        var root = Path.Combine(Path.GetTempPath(), "ASLM-RemovalPlan-" + Guid.NewGuid().ToString("N"));
        var target = MakeModule(root, "one");
        var other = MakeModule(root, "two");
        var engine = MakeEngine(root, "shared-engine");
        target.Dependencies.Engines.Add(new ModuleEngineDependency { Id = engine.Id });
        other.Settings.Add(new ModuleSetting { Key = engine.Id + "_models", Type = "models" });

        var plan = ModuleInstaller.BuildRemovalPlan(root, target, [other], [engine]);

        plan.Engines.Should().BeEmpty();
        plan.Directories.Should().Equal(ModuleEnvironmentResolver.GetEnvironmentDirectory(target, engine));
    }

    [Fact]
    public void Removal_protects_custom_paths_and_rejects_an_environment_outside_its_engine()
    {
        var root = Path.Combine(Path.GetTempPath(), "ASLM-RemovalPlan-" + Guid.NewGuid().ToString("N"));
        var target = MakeModule(root, "one");
        var other = MakeModule(root, "two");
        var engine = MakeEngine(root, "shared-engine");
        target.Dependencies.Engines.Add(new ModuleEngineDependency { Id = engine.Id });
        var runtime = Path.Combine(Path.GetDirectoryName(engine.SourcePath)!, "runtime");
        var models = Path.Combine(root, "Models", engine.Id);
        other.Settings.Add(new ModuleSetting { Type = "path", UseCustomValue = true, Value = Path.Combine(runtime, "tool.exe") });
        other.Settings.Add(new ModuleSetting { Type = "models", UseCustomValue = true, Value = Path.Combine(models, "nested") });
        var plan = ModuleInstaller.BuildRemovalPlan(root, target, [other], [engine]);
        plan.Engines.Should().BeEmpty();
        plan.Directories.Should().NotContain(runtime).And.NotContain(models);

        engine.ModuleEnvironment!.DirectoryPrefix = "../../outside-";
        var invalid = () => ModuleInstaller.BuildRemovalPlan(root, target, [], [engine]);
        invalid.Should().Throw<InvalidOperationException>();
        var broad = () => ModuleInstaller.ValidateRemovalPath(root, root);
        broad.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Uninstall_removes_only_owned_content_updates_registry_and_keeps_the_catalog_card()
    {
        using var fixture = new InstalledFixture();
        var changes = 0;
        fixture.Installer.ModulesChanged += (_, _) => changes++;
        (await fixture.Installer.DiscoverInstalledModulesAsync()).Should().Contain(module => module.Id == fixture.Module.Id);

        (await fixture.Installer.UninstallAsync(fixture.Module)).Should().BeNull();

        fixture.Installer.Registry.ReadIds().Should().NotContain(fixture.Module.Id);
        File.Exists(fixture.Payload).Should().BeFalse();
        Directory.Exists(fixture.Runtime).Should().BeFalse();
        Directory.Exists(fixture.Environment).Should().BeFalse();
        Directory.Exists(fixture.Models).Should().BeFalse();
        File.ReadAllText(fixture.Module.IconFullPath!).Should().Be("artwork");
        var catalog = await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath);
        catalog!.Status.Installed.Should().BeFalse();
        (await fixture.Installer.DiscoverAvailableModulesAsync()).Should().Contain(module => module.Id == fixture.Module.Id);
        (await fixture.Installer.DiscoverInstalledModulesAsync()).Should().NotContain(module => module.Id == fixture.Module.Id);
        new EngineInstaller().FindAvailableEngine(fixture.Engine.Id)!.Status.Installed.Should().BeFalse();
        changes.Should().Be(1);
    }

    [Fact]
    public async Task Uninstall_rolls_back_files_and_engine_status_when_registry_commit_is_locked()
    {
        using var fixture = new InstalledFixture();
        var registryPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Modules.json");
        using var locked = new FileStream(registryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var uninstall = () => fixture.Installer.UninstallAsync(fixture.Module);
        var error = await Record.ExceptionAsync(uninstall);
        Assert.True(error is IOException or UnauthorizedAccessException);

        File.ReadAllText(fixture.Payload).Should().Be("installed content");
        Directory.Exists(fixture.Runtime).Should().BeTrue();
        Directory.Exists(fixture.Environment).Should().BeTrue();
        Directory.Exists(fixture.Models).Should().BeTrue();
        fixture.Installer.Registry.ReadIds().Should().Contain(fixture.Module.Id);
        (await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath))!.Status.Installed.Should().BeTrue();
        new EngineInstaller().FindAvailableEngine(fixture.Engine.Id)!.Status.Installed.Should().BeTrue();
    }

    [Fact]
    public async Task Reinstall_registers_new_content_but_a_stale_save_cannot_restore_removed_modules()
    {
        using var fixture = new InstalledFixture();
        await fixture.Installer.UninstallAsync(fixture.Module);
        var staleSave = () => fixture.Installer.SaveConfigAsync(fixture.Module);
        await staleSave.Should().ThrowAsync<InvalidOperationException>();

        var downloaded = (await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath))!;
        downloaded.Update.Mode = "branch";
        downloaded.Update.Branch = "preview";
        fixture.Installer.SaveModuleUpdatePreferences(downloaded);
        var configured = (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!;
        configured.Update.Mode.Should().Be("branch");
        configured.Update.Branch.Should().Be("preview");
        configured.Status.Installed.Should().BeFalse();
        fixture.Installer.Registry.ReadIds().Should().NotContain(downloaded.Id);
        await staleSave.Should().ThrowAsync<InvalidOperationException>();

        downloaded.Version = "2.0";
        File.WriteAllText(fixture.Payload, "new downloaded content");
        await fixture.Installer.SaveInstalledContentAsync(downloaded);

        fixture.Installer.Registry.ReadIds().Should().Contain(downloaded.Id);
        (await fixture.Installer.DiscoverAvailableModulesAsync()).Should().NotContain(item => item.Id == downloaded.Id);
        (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!.Status.InstalledVersion.Should().Be("2.0");
    }

    [Fact]
    public async Task Notification_failure_after_reinstall_does_not_block_future_launch_state_saves()
    {
        using var fixture = new InstalledFixture();
        await fixture.Installer.UninstallAsync(fixture.Module);
        var downloaded = (await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath))!;
        File.WriteAllText(fixture.Payload, "reinstalled content");
        EventHandler brokenObserver = (_, _) => throw new InvalidOperationException("Observer failed");
        fixture.Installer.ModulesChanged += brokenObserver;
        try
        {
            await FluentActions.Awaiting(() => fixture.Installer.SaveInstalledContentAsync(downloaded))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("Observer failed");
        }
        finally { fixture.Installer.ModulesChanged -= brokenObserver; }

        fixture.Installer.Registry.ReadIds().Should().Contain(downloaded.Id);
        var fresh = (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!;
        fresh.Status.Installed.Should().BeTrue();
        fresh.Status.Enabled = true;
        // This is the persistence step shared by UI launches and module-to-module launch requests.
        await FluentActions.Awaiting(() => fixture.Installer.SaveConfigAsync(fresh)).Should().NotThrowAsync();
        (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!.Status.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Installed_engine_lookup_does_not_lose_engines_during_concurrent_cache_invalidation()
    {
        using var fixture = new InstalledFixture();
        var engines = new EngineInstaller();
        engines.GetEngineConfig(fixture.Engine.Id).Should().NotBeNull();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidator = Task.Run(() =>
        {
            started.SetResult();
            while (!stop.IsCancellationRequested)
            {
                engines.InvalidateCache();
                Thread.Yield();
            }
        });
        try
        {
            await started.Task;
            var missing = 0;
            await Task.Run(() => Parallel.For(0, 256, _ =>
            {
                if (engines.GetEngineConfig(fixture.Engine.Id) == null) Interlocked.Increment(ref missing);
                if (!engines.HasEngine(fixture.Engine.Id)) Interlocked.Increment(ref missing);
            }));
            missing.Should().Be(0, "invalidating a cache must not make an installed runtime appear missing");
        }
        finally { stop.Cancel(); await invalidator; }
    }

    [Fact]
    public async Task Uninstall_refuses_to_race_a_download_or_guess_missing_dependencies()
    {
        using var fixture = new InstalledFixture();
        using (ModuleInstaller.BeginContentOperation())
        {
            var busy = () => fixture.Installer.UninstallAsync(fixture.Module);
            await busy.Should().ThrowAsync<InvalidOperationException>();
            File.Exists(fixture.Payload).Should().BeTrue();
        }
        fixture.Module.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "missing" });
        fixture.Installer.Registry.Add("missing");
        var unknown = () => fixture.Installer.UninstallAsync(fixture.Module);
        await unknown.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(fixture.Payload).Should().BeTrue();
    }

    [Fact]
    public async Task Uninstall_keeps_another_modules_shared_runtime_and_models_on_disk()
    {
        using var fixture = new InstalledFixture();
        var other = fixture.AddModule("consumer");
        other.Dependencies.Engines.Add(new ModuleEngineDependency { Id = fixture.Engine.Id });
        fixture.Installer.SaveModuleConfig(other);
        await fixture.Installer.UninstallAsync(fixture.Module);
        Directory.Exists(fixture.Runtime).Should().BeTrue();
        Directory.Exists(fixture.Models).Should().BeTrue();
        Directory.Exists(fixture.Environment).Should().BeFalse();
        fixture.Installer.Registry.ReadIds().Should().Contain(other.Id).And.NotContain(fixture.Module.Id);
    }

    [Fact]
    public async Task Uninstall_blocks_removal_of_a_module_required_by_another_installed_module()
    {
        using var fixture = new InstalledFixture();
        var other = fixture.AddModule("dependent");
        other.Dependencies.Modules.Add(new ModuleModuleDependency { Id = fixture.Module.Id });
        fixture.Installer.SaveModuleConfig(other);
        var remove = () => fixture.Installer.UninstallAsync(fixture.Module);
        await remove.Should().ThrowAsync<InvalidOperationException>();
        File.ReadAllText(fixture.Payload).Should().Be("installed content");
        fixture.Installer.Registry.ReadIds().Should().Contain(fixture.Module.Id);
    }

    [Fact]
    public void Removal_respects_bridge_model_targets_even_without_an_engine_dependency()
    {
        var root = Path.Combine(Path.GetTempPath(), "ASLM-RemovalPlan-" + Guid.NewGuid().ToString("N"));
        var target = MakeModule(root, "provider");
        var other = MakeModule(root, "consumer");
        target.DownloadsBridge = new ModuleDownloadsBridge
        {
            Targets = new() { ["weights"] = new ModuleDownloadBridgeTarget { Root = "Models", Relative = "shared-weights" } }
        };
        other.DownloadsBridge = new ModuleDownloadsBridge
        {
            Targets = new() { ["weights"] = new ModuleDownloadBridgeTarget { Root = "Models", Relative = "shared-weights/subset" } }
        };
        ModuleInstaller.BuildRemovalPlan(root, target, [other], []).Directories.Should().BeEmpty();
        ModuleInstaller.BuildRemovalPlan(root, target, [], []).Directories
            .Should().Equal(Path.Combine(root, "Models", "shared-weights"));
    }

    [Fact]
    public async Task Unrelated_content_operations_do_not_block_removal_but_shared_engines_and_paths_do()
    {
        using var fixture = new InstalledFixture();
        var unrelated = fixture.AddModule("unrelated");
        fixture.Installer.SaveModuleConfig(unrelated);
        using (ModuleInstaller.BeginContentOperation(unrelated))
        {
            using var sameEngine = ModuleInstaller.BeginContentOperation(new ModuleConfig
            {
                Id = "active-engine-consumer",
                Dependencies = new() { Engines = [new() { Id = fixture.Engine.Id }] }
            });
            await FluentActions.Awaiting(() => fixture.Installer.UninstallAsync(fixture.Module))
                .Should().ThrowAsync<InvalidOperationException>();
        }
        var pathConsumer = new ModuleConfig { Id = "active-path-consumer" };
        pathConsumer.Settings.Add(new ModuleSetting
        {
            Type = "path", UseCustomValue = true, Value = Path.Combine(fixture.Runtime, "tool.exe")
        });
        using (ModuleInstaller.BeginContentOperation(pathConsumer))
            await FluentActions.Awaiting(() => fixture.Installer.UninstallAsync(fixture.Module))
                .Should().ThrowAsync<InvalidOperationException>();
        using (ModuleInstaller.BeginContentOperation(unrelated))
        {
            (await fixture.Installer.UninstallQueuedAsync(fixture.Module, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
            File.Exists(fixture.Payload).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Cancel_rollback_removes_only_fresh_dependencies_not_preinstalled_or_manually_requested(
        bool preinstalled, bool manuallyRequested, bool shouldRemain)
    {
        using var fixture = new InstalledFixture();
        var root = fixture.AddModule("root");
        root.Status.Installed = false;
        root.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
        fixture.Installer.SaveModuleConfig(root);
        using var request = fixture.Installer.BeginCatalogInstallation(root, [fixture.Module, root]);
        if (!preinstalled) request.TrackNewContent(fixture.Module);
        if (manuallyRequested) fixture.Installer.RecordManualRequest(fixture.Module.Id);

        await fixture.Installer.RollbackCatalogInstallationAsync(request, new Progress<string>());

        fixture.Installer.Registry.ReadIds().Contains(fixture.Module.Id).Should().Be(shouldRemain);
        File.Exists(fixture.Payload).Should().Be(shouldRemain);
        Directory.Exists(fixture.Runtime).Should().Be(shouldRemain);
        File.Exists(fixture.Module.SourcePath).Should().BeTrue("the local catalog manifest is retained");
    }

    [Fact]
    public async Task Cancel_removes_the_fresh_root_before_removing_its_fresh_dependencies()
    {
        using var fixture = new InstalledFixture();
        var root = fixture.AddModule("root");
        root.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
        fixture.Installer.SaveModuleConfig(root);
        using var request = fixture.Installer.BeginCatalogInstallation(root, [fixture.Module, root]);
        request.TrackNewContent(root);
        request.TrackNewContent(fixture.Module);

        await fixture.Installer.RollbackCatalogInstallationAsync(request, new Progress<string>());

        fixture.Installer.Registry.ReadIds().Should().NotContain(root.Id).And.NotContain(fixture.Module.Id);
        File.Exists(fixture.Payload).Should().BeFalse();
    }

    [Fact]
    public async Task Shared_fresh_dependency_survives_first_cancel_and_is_cleaned_when_last_consumer_cancels()
    {
        using var fixture = new InstalledFixture();
        var first = fixture.AddModule("first");
        var second = fixture.AddModule("second");
        foreach (var root in new[] { first, second })
        {
            root.Status.Installed = false;
            root.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
            fixture.Installer.SaveModuleConfig(root);
        }
        using var firstRequest = fixture.Installer.BeginCatalogInstallation(first, [fixture.Module, first]);
        firstRequest.TrackNewContent(fixture.Module);
        using var secondRequest = fixture.Installer.BeginCatalogInstallation(second, [fixture.Module, second]);

        await fixture.Installer.RollbackCatalogInstallationAsync(firstRequest, new Progress<string>());
        File.Exists(fixture.Payload).Should().BeTrue();
        firstRequest.Dispose();
        await fixture.Installer.RollbackCatalogInstallationAsync(secondRequest, new Progress<string>());

        fixture.Installer.Registry.ReadIds().Should().NotContain(fixture.Module.Id);
        File.Exists(fixture.Payload).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_keeps_a_dependency_required_by_another_installed_module()
    {
        using var fixture = new InstalledFixture();
        var root = fixture.AddModule("root");
        root.Status.Installed = false;
        root.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
        fixture.Installer.SaveModuleConfig(root);
        var installedConsumer = fixture.AddModule("consumer");
        installedConsumer.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
        fixture.Installer.SaveModuleConfig(installedConsumer);
        using var request = fixture.Installer.BeginCatalogInstallation(root, [fixture.Module, root]);
        request.TrackNewContent(fixture.Module);

        await fixture.Installer.RollbackCatalogInstallationAsync(request, new Progress<string>());

        fixture.Installer.Registry.ReadIds().Should().Contain(fixture.Module.Id);
        File.Exists(fixture.Payload).Should().BeTrue();
    }

    private static ModuleConfig MakeModule(string root, string id) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0",
        SourcePath = Path.Combine(root, "Modules", id, ModuleManifestDiscovery.ManifestFileName)
    };

    private static ModuleConfig WriteModule(string root, string id, bool installed)
    {
        var module = MakeModule(root, id);
        module.Status.Installed = installed;
        Directory.CreateDirectory(Path.GetDirectoryName(module.SourcePath)!);
        File.WriteAllText(module.SourcePath, JsonSerializer.Serialize(module));
        return module;
    }

    private static EngineConfig MakeEngine(string root, string id) => new()
    {
        FileVersion = 1,
        Id = id,
        Name = id,
        SourcePath = Path.Combine(root, "Engines", id, "ASLM_Engine.json"),
        ExecutablePath = "runtime/tool.exe",
        ModuleEnvironment = new EngineModuleEnvironment { Enabled = true, DirectoryPrefix = "venv-" },
        Status = new EngineStatus { Installed = true }
    };

    private sealed class InstalledFixture : IDisposable
    {
        private readonly List<string> _directories = [];
        public ModuleInstaller Installer { get; } = new(null!, null!, null!);
        public ModuleConfig Module { get; }
        public EngineConfig Engine { get; }
        public string Runtime { get; }
        public string Environment { get; }
        public string Models { get; }
        public string Payload { get; }

        public InstalledFixture(string? moduleId = null)
        {
            using var layout = new AslmFileSystemLayout();
            File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), "[]");
            var id = "removal-test-" + Guid.NewGuid().ToString("N");
            Module = MakeModule(layout.Root, id);
            Module.Id = moduleId ?? id;
            Module.Status.Installed = true;
            Module.Icon = "icon.png";
            Engine = MakeEngine(layout.Root, id + "-engine");
            Module.Dependencies.Engines.Add(new ModuleEngineDependency { Id = Engine.Id });
            var moduleDirectory = Path.GetDirectoryName(Module.SourcePath)!;
            var engineDirectory = Path.GetDirectoryName(Engine.SourcePath)!;
            Models = Path.Combine(layout.Root, "Models", Engine.Id);
            _directories.AddRange([moduleDirectory, engineDirectory, Models]);
            foreach (var directory in _directories) Directory.CreateDirectory(directory);
            Runtime = Path.Combine(engineDirectory, "runtime");
            Environment = ModuleEnvironmentResolver.GetEnvironmentDirectory(Module, Engine);
            Directory.CreateDirectory(Runtime);
            Directory.CreateDirectory(Environment);
            File.WriteAllText(Path.Combine(Runtime, "tool.exe"), "runtime");
            File.WriteAllText(Engine.SourcePath, JsonSerializer.Serialize(Engine));
            File.WriteAllText(Module.IconFullPath!, "artwork");
            Payload = Path.Combine(moduleDirectory, "main.py");
            File.WriteAllText(Payload, "installed content");
            Installer.SaveModuleConfig(Module);
        }

        public void Dispose()
        {
            foreach (var directory in _directories)
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        public ModuleConfig AddModule(string suffix)
        {
            var module = MakeModule(AppRoot.Directory, Module.Id + "-" + suffix);
            module.Status.Installed = true;
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            _directories.Add(directory);
            Directory.CreateDirectory(directory);
            return module;
        }
    }
}
