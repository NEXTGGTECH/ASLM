// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleRegistryAndRemovalTests
{
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

        public InstalledFixture()
        {
            using var layout = new AslmFileSystemLayout();
            File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), "[]");
            var id = "removal-test-" + Guid.NewGuid().ToString("N");
            Module = MakeModule(layout.Root, id);
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
