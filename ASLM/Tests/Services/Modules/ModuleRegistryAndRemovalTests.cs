// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the installed-module registry, the removal planning, uninstall and reinstall of modules with their
    /// engines, and the rollback of canceled catalog installations.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class ModuleRegistryAndRemovalTests
    {
        // Installation state labels

        /// <summary>
        /// Verifies that the requester names appear in the long status label only for a queued or running dependency
        /// installation with a non-blank requester, never in the compact label, and that every other state keeps the
        /// compact label.
        /// </summary>
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


        // Required modules

        /// <summary>
        /// Verifies that a module counts as required only when its id equals a required id, ignoring case.
        /// </summary>
        [Theory]
        [InlineData("aslm-chat", true)]
        [InlineData("ASLM-CHAT", true)]
        [InlineData("aslm-code", false)]
        [InlineData("aslm-chat-example", false)]
        public void Required_modules_are_matched_by_exact_case_insensitive_id(string id, bool required)
        {
            ModuleRegistry.IsRequired(id).Should().Be(required);
        }

        /// <summary>
        /// Verifies that uninstalling a required module (in any id casing) returns without error but changes no
        /// manifest, registry entry, file or folder, raises no change event and leaves the content lock free.
        /// </summary>
        [Theory]
        [InlineData("aslm-chat")]
        [InlineData("ASLM-CHAT")]
        public async Task Uninstall_skips_required_modules_without_changing_content_or_registry(string id)
        {
            // Arrange: an installed required module, with its manifests and the registry captured for comparison.
            using var fixture = new InstalledFixture(id);
            var manifest = File.ReadAllText(fixture.Module.SourcePath);
            var engineManifest = File.ReadAllText(fixture.Engine.SourcePath);
            var registryPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Modules.json");
            var registry = File.ReadAllText(registryPath);
            var changes = 0;
            fixture.Installer.ModulesChanged += (_, _) => changes++;

            // Act: the uninstall is skipped without an error.
            (await fixture.Installer.UninstallAsync(fixture.Module)).Should().BeNull();

            // Assert: nothing on disk or in the registry changed.
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


        // Reinstall

        /// <summary>
        /// Verifies that a reinstall may clean even a required module: its payload and environment are removed and it
        /// leaves the registry, while installed consumers, the shared engine runtime and the models stay, and the
        /// catalog entry keeps the chosen update selection.
        /// </summary>
        [Theory]
        [InlineData("aslm-chat")]
        [InlineData("ASLM-CHAT")]
        [InlineData("optional-module")]
        public async Task Reinstall_cleans_owned_data_and_preserves_consumers_even_for_required_modules(string id)
        {
            // Arrange: an installed module, an installed consumer of it and of its engine, and a queued reinstall
            // of the module with a new update selection.
            using var fixture = new InstalledFixture(id);
            var consumer = fixture.AddModule("consumer");
            consumer.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
            consumer.Dependencies.Engines.Add(new() { Id = fixture.Engine.Id });
            fixture.Installer.SaveModuleConfig(consumer);
            var selected = UpdateManager.CreateReinstallConfig(fixture.Module,
                new() { Mode = "pre-release", SelectedReleaseTag = "v2-beta" });
            using var request = fixture.Installer.BeginCatalogInstallation(selected, [selected], reinstall: true);
            fixture.Installer.GetInstallationState(id)!.IsDependency.Should().BeFalse();
            fixture.Installer.GetInstallationState(id)!.Phase.Should().Be(DownloadOperationState.Queued);

            // Act: clear the module for reinstallation.
            await fixture.Installer.RemoveForReinstallAsync(selected, CancellationToken.None);

            // Assert: owned content is gone, shared content and consumers remain, and the catalog entry is intact.
            File.Exists(fixture.Payload).Should().BeFalse();
            Directory.Exists(fixture.Environment).Should().BeFalse();
            Directory.Exists(fixture.Runtime).Should().BeTrue();
            Directory.Exists(fixture.Models).Should().BeTrue();
            fixture.Installer.Registry.ReadIds().Should().Contain(consumer.Id).And.NotContain(id);
            var catalog = (await fixture.Installer.LoadModuleConfig(selected.SourcePath))!;
            catalog.Update.Mode.Should().Be("pre-release");
            catalog.Update.SelectedReleaseTag.Should().Be("v2-beta");
            catalog.Status.Installed.Should().BeFalse();
            (await fixture.Installer.DiscoverInstalledModulesAsync(includeInstalling: true)).Should().Contain(module => module.Id == id);
            (await fixture.Installer.DiscoverAvailableModulesAsync()).Should().Contain(module => module.Id == id);
        }


        // Registry file

        /// <summary>
        /// Verifies that a missing registry file is created once from the installed manifests only, and that a
        /// registry emptied later stays empty instead of being migrated again.
        /// </summary>
        [Fact]
        public void Registry_migrates_installed_manifests_once_and_respects_an_empty_registry()
        {
            // Arrange: one installed and one catalog-only module on disk, and no registry file yet.
            var root = Directory.CreateTempSubdirectory("ASLM-RegistryTest-").FullName;
            try
            {
                WriteModule(root, "installed", installed: true);
                WriteModule(root, "catalog", installed: false);

                // The first read derives the registry from the manifests.
                var registry = new ModuleRegistry(root);
                registry.ReadIds().Should().Equal("installed");

                // After the removal the empty registry file is authoritative, also for a new instance.
                registry.Remove("installed");
                new ModuleRegistry(root).ReadIds().Should().BeEmpty();
                JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "Data", "App", "ASLM_Modules.json")))
                    .Should().BeEmpty();
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        /// <summary>
        /// Verifies that registrations made concurrently through separate registry instances are all kept, and that
        /// a corrupt registry file makes reading fail with a <see cref="JsonException"/> without being overwritten.
        /// </summary>
        [Fact]
        public void Registry_keeps_concurrent_registrations_and_does_not_overwrite_corrupt_data()
        {
            var root = Directory.CreateTempSubdirectory("ASLM-RegistryTest-").FullName;
            try
            {
                // Twenty parallel registrations through separate instances all end up in the file.
                Parallel.For(0, 20, id => new ModuleRegistry(root).Add("module-" + id));
                var registry = new ModuleRegistry(root);
                registry.ReadIds().Should().HaveCount(20);

                // A damaged file is reported and left as it is.
                var path = Path.Combine(root, "Data", "App", "ASLM_Modules.json");
                File.WriteAllText(path, "broken registry");
                var read = () => registry.ReadIds();
                read.Should().Throw<JsonException>();
                File.ReadAllText(path).Should().Be("broken registry");
            }
            finally { Directory.Delete(root, recursive: true); }
        }


        // Removal planning

        /// <summary>
        /// Verifies that the removal plan keeps an engine and its folders when another module uses the engine only
        /// through a run command, and keeps the environment folder when another module's folder name normalizes to
        /// the same name.
        /// </summary>
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

        /// <summary>
        /// Verifies that the removal plan keeps the engine runtime and model store that another module uses through
        /// a models setting, and removes only the removed module's own environment folder.
        /// </summary>
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

        /// <summary>
        /// Verifies that custom paths of another module inside the engine runtime or the model store protect those
        /// folders from removal, that an environment folder prefix escaping the engine folder is rejected, and that
        /// a removal path equal to its parent root is rejected.
        /// </summary>
        [Fact]
        public void Removal_protects_custom_paths_and_rejects_an_environment_outside_its_engine()
        {
            // Arrange: another module with custom storage paths inside the shared engine runtime and model store.
            var root = Path.Combine(Path.GetTempPath(), "ASLM-RemovalPlan-" + Guid.NewGuid().ToString("N"));
            var target = MakeModule(root, "one");
            var other = MakeModule(root, "two");
            var engine = MakeEngine(root, "shared-engine");
            target.Dependencies.Engines.Add(new ModuleEngineDependency { Id = engine.Id });
            var runtime = Path.Combine(Path.GetDirectoryName(engine.SourcePath)!, "runtime");
            var models = Path.Combine(root, "Models", engine.Id);
            other.Settings.Add(new ModuleSetting { Type = "path", UseCustomValue = true, Value = Path.Combine(runtime, "tool.exe") });
            other.Settings.Add(new ModuleSetting { Type = "models", UseCustomValue = true, Value = Path.Combine(models, "nested") });

            // Neither the engine nor its runtime and models folders may be planned for removal.
            var plan = ModuleInstaller.BuildRemovalPlan(root, target, [other], [engine]);
            plan.Engines.Should().BeEmpty();
            plan.Directories.Should().NotContain(runtime).And.NotContain(models);

            // An environment folder name that climbs out of the engine folder, and a path equal to its root, are unsafe.
            engine.ModuleEnvironment!.DirectoryPrefix = "../../outside-";
            var invalid = () => ModuleInstaller.BuildRemovalPlan(root, target, [], [engine]);
            invalid.Should().Throw<InvalidOperationException>();
            var broad = () => ModuleInstaller.ValidateRemovalPath(root, root);
            broad.Should().Throw<InvalidOperationException>();
        }


        // Uninstall and reinstall persistence

        /// <summary>
        /// Verifies that uninstalling removes the module's payload, runtime, environment, models and ports and its
        /// registry entry, keeps its artwork and a not-installed manifest as the catalog card, resets the engine
        /// state and raises exactly one change event.
        /// </summary>
        [Fact]
        public async Task Uninstall_removes_only_owned_content_updates_registry_and_keeps_the_catalog_card()
        {
            // Arrange: an installed module with assigned ports, listed among the installed modules.
            using var fixture = new InstalledFixture();
            fixture.Ports.GetOrAssignPorts(fixture.Module);
            var changes = 0;
            fixture.Installer.ModulesChanged += (_, _) => changes++;
            (await fixture.Installer.DiscoverInstalledModulesAsync()).Should().Contain(module => module.Id == fixture.Module.Id);

            // Act: uninstall without errors.
            (await fixture.Installer.UninstallAsync(fixture.Module)).Should().BeNull();

            // Assert: registry, ports and owned files are gone, while artwork and the catalog card remain.
            fixture.Installer.Registry.ReadIds().Should().NotContain(fixture.Module.Id);
            fixture.Ports.TryGetAssignedPorts(fixture.Module.Id).Should().BeNull();
            var reloadedPorts = new PortRegistry(new AppDataStore(TestLoggerFactory.Create<AppDataStore>()));
            reloadedPorts.TryGetAssignedPorts(fixture.Module.Id).Should().BeNull();
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

        /// <summary>
        /// Verifies that when the registry file is held open so the final registry update fails, the uninstall throws
        /// an I/O error and restores the payload, folders, registry entry, ports, manifest and engine state.
        /// </summary>
        [Fact]
        public async Task Uninstall_rolls_back_files_and_engine_status_when_registry_commit_is_locked()
        {
            // Arrange: an installed module with ports, and the registry file held open for reading only.
            using var fixture = new InstalledFixture();
            var ports = fixture.Ports.GetOrAssignPorts(fixture.Module).ToDictionary(pair => pair.Key, pair => pair.Value);
            var registryPath = Path.Combine(AppRoot.Directory, "Data", "App", "ASLM_Modules.json");
            using var locked = new FileStream(registryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Act: the uninstall cannot write the registry and fails.
            var uninstall = () => fixture.Installer.UninstallAsync(fixture.Module);
            var error = await Record.ExceptionAsync(uninstall);
            Assert.True(error is IOException or UnauthorizedAccessException);

            // Assert: everything that was moved or reset has been restored.
            File.ReadAllText(fixture.Payload).Should().Be("installed content");
            Directory.Exists(fixture.Runtime).Should().BeTrue();
            Directory.Exists(fixture.Environment).Should().BeTrue();
            Directory.Exists(fixture.Models).Should().BeTrue();
            fixture.Installer.Registry.ReadIds().Should().Contain(fixture.Module.Id);
            fixture.Ports.TryGetAssignedPorts(fixture.Module.Id).Should().BeEquivalentTo(ports);
            (await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath))!.Status.Installed.Should().BeTrue();
            new EngineInstaller().FindAvailableEngine(fixture.Engine.Id)!.Status.Installed.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that after an uninstall a save of the stale in-memory module is rejected, also after the download
        /// preferences of the catalog card were saved, and that only saving newly downloaded content registers the
        /// module again as installed.
        /// </summary>
        [Fact]
        public async Task Reinstall_registers_new_content_but_a_stale_save_cannot_restore_removed_modules()
        {
            // The stale in-memory module cannot be saved once it has been uninstalled.
            using var fixture = new InstalledFixture();
            await fixture.Installer.UninstallAsync(fixture.Module);
            var staleSave = () => fixture.Installer.SaveConfigAsync(fixture.Module);
            await staleSave.Should().ThrowAsync<InvalidOperationException>();

            // Saving download preferences updates the catalog card without installing or registering the module.
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

            // Recording newly downloaded content registers the module as installed and lifts the guard.
            downloaded.Version = "2.0";
            File.WriteAllText(fixture.Payload, "new downloaded content");
            await fixture.Installer.SaveInstalledContentAsync(downloaded);

            fixture.Installer.Registry.ReadIds().Should().Contain(downloaded.Id);
            (await fixture.Installer.DiscoverAvailableModulesAsync()).Should().NotContain(item => item.Id == downloaded.Id);
            (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!.Status.InstalledVersion.Should().Be("2.0");
        }

        /// <summary>
        /// Verifies that when a change observer throws while newly downloaded content is recorded, the module is
        /// nevertheless registered and installed on disk, and later saves of its launch state still succeed.
        /// </summary>
        [Fact]
        public async Task Notification_failure_after_reinstall_does_not_block_future_launch_state_saves()
        {
            // Arrange: an uninstalled module with new content on disk and a change observer that always throws.
            using var fixture = new InstalledFixture();
            await fixture.Installer.UninstallAsync(fixture.Module);
            var downloaded = (await fixture.Installer.LoadModuleConfig(fixture.Module.SourcePath))!;
            File.WriteAllText(fixture.Payload, "reinstalled content");
            EventHandler brokenObserver = (_, _) => throw new InvalidOperationException("Observer failed");
            fixture.Installer.ModulesChanged += brokenObserver;

            // Act: recording the content surfaces the observer failure.
            try
            {
                await FluentActions.Awaiting(() => fixture.Installer.SaveInstalledContentAsync(downloaded))
                    .Should().ThrowAsync<InvalidOperationException>().WithMessage("Observer failed");
            }
            finally { fixture.Installer.ModulesChanged -= brokenObserver; }

            // Assert: the module is registered and installed anyway.
            fixture.Installer.Registry.ReadIds().Should().Contain(downloaded.Id);
            var fresh = (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!;
            fresh.Status.Installed.Should().BeTrue();
            fresh.Status.Enabled = true;

            // This is the persistence step shared by UI launches and module-to-module launch requests.
            await FluentActions.Awaiting(() => fixture.Installer.SaveConfigAsync(fresh)).Should().NotThrowAsync();
            (await fixture.Installer.LoadModuleConfig(downloaded.SourcePath))!.Status.Enabled.Should().BeTrue();
        }


        // Engine cache

        /// <summary>
        /// Verifies that lookups of an installed engine never report it as missing while another thread keeps
        /// invalidating the engine discovery cache.
        /// </summary>
        [Fact]
        public async Task Installed_engine_lookup_does_not_lose_engines_during_concurrent_cache_invalidation()
        {
            // Arrange: an installed engine that is known to be found, and a background loop that keeps clearing the
            // discovery cache until it is stopped.
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
                // Act: look the engine up in parallel while the invalidation runs, counting every miss.
                await started.Task;
                var missing = 0;
                await Task.Run(() => Parallel.For(0, 256, _ =>
                {
                    if (engines.GetEngineConfig(fixture.Engine.Id) == null) Interlocked.Increment(ref missing);
                    if (!engines.HasEngine(fixture.Engine.Id)) Interlocked.Increment(ref missing);
                }));

                // Assert: no lookup came back empty.
                missing.Should().Be(0, "invalidating a cache must not make an installed runtime appear missing");
            }
            finally { stop.Cancel(); await invalidator; }
        }


        // Uninstall guards

        /// <summary>
        /// Verifies that an uninstall fails and keeps the payload while an exclusive content operation (such as a
        /// download) is running, and also fails when a registered module has no manifest, so the dependencies of
        /// the remaining modules cannot be judged.
        /// </summary>
        [Fact]
        public async Task Uninstall_refuses_to_race_a_download_or_guess_missing_dependencies()
        {
            // An operation without modules is exclusive and blocks the uninstall.
            using var fixture = new InstalledFixture();
            using (ModuleInstaller.BeginContentOperation())
            {
                var busy = () => fixture.Installer.UninstallAsync(fixture.Module);
                await busy.Should().ThrowAsync<InvalidOperationException>();
                File.Exists(fixture.Payload).Should().BeTrue();
            }

            // A registry entry without a matching manifest makes the remaining dependencies unknowable.
            fixture.Module.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "missing" });
            fixture.Installer.Registry.Add("missing");
            var unknown = () => fixture.Installer.UninstallAsync(fixture.Module);
            await unknown.Should().ThrowAsync<InvalidOperationException>();
            File.Exists(fixture.Payload).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that uninstalling a module keeps the engine runtime and the models that another installed
        /// module uses, removes the uninstalled module's own environment and leaves the other module registered.
        /// </summary>
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

        /// <summary>
        /// Verifies that an uninstall throws and keeps the payload and registry entry while another installed module
        /// declares a dependency on the module.
        /// </summary>
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


        // Bridge target planning

        /// <summary>
        /// Verifies that the model folder of a downloads bridge target is removed with its module only when no other
        /// module has a bridge target inside it.
        /// </summary>
        [Fact]
        public void Removal_respects_bridge_model_targets_even_without_an_engine_dependency()
        {
            // Arrange: both modules download into Models; the second target lies inside the first target's folder.
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

            // With the other module present the folder is kept; without it, it is removed.
            ModuleInstaller.BuildRemovalPlan(root, target, [other], []).Directories.Should().BeEmpty();
            ModuleInstaller.BuildRemovalPlan(root, target, [], []).Directories
                .Should().Equal(Path.Combine(root, "Models", "shared-weights"));
        }


        // Content operation locking

        /// <summary>
        /// Verifies that content operations on unrelated modules do not block an uninstall, whereas an operation on a
        /// module that uses the same engine, or on a custom path inside the engine runtime, makes the uninstall fail.
        /// </summary>
        [Fact]
        public async Task Unrelated_content_operations_do_not_block_removal_but_shared_engines_and_paths_do()
        {
            // Arrange: an unrelated installed module whose content operation stays open throughout.
            using var fixture = new InstalledFixture();
            var unrelated = fixture.AddModule("unrelated");
            fixture.Installer.SaveModuleConfig(unrelated);
            using (ModuleInstaller.BeginContentOperation(unrelated))
            {
                // A second operation on a module of the same engine blocks the uninstall.
                using var sameEngine = ModuleInstaller.BeginContentOperation(new ModuleConfig
                {
                    Id = "active-engine-consumer",
                    Dependencies = new() { Engines = [new() { Id = fixture.Engine.Id }] }
                });
                await FluentActions.Awaiting(() => fixture.Installer.UninstallAsync(fixture.Module))
                    .Should().ThrowAsync<InvalidOperationException>();
            }

            // An operation on a custom path inside the engine runtime blocks it as well.
            var pathConsumer = new ModuleConfig { Id = "active-path-consumer" };
            pathConsumer.Settings.Add(new ModuleSetting
            {
                Type = "path", UseCustomValue = true, Value = Path.Combine(fixture.Runtime, "tool.exe")
            });
            using (ModuleInstaller.BeginContentOperation(pathConsumer))
                await FluentActions.Awaiting(() => fixture.Installer.UninstallAsync(fixture.Module))
                    .Should().ThrowAsync<InvalidOperationException>();

            // With only the unrelated operation open, the queued uninstall completes and removes the payload.
            using (ModuleInstaller.BeginContentOperation(unrelated))
            {
                (await fixture.Installer.UninstallQueuedAsync(fixture.Module, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
                File.Exists(fixture.Payload).Should().BeFalse();
            }
        }


        // Installation rollback

        /// <summary>
        /// Verifies that rolling back a canceled installation removes a dependency only when the installation
        /// installed it freshly: a preinstalled or a manually requested dependency stays with its files, and the
        /// local catalog manifest is retained in every case.
        /// </summary>
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(true, false, true)]
        [InlineData(false, true, true)]
        public async Task Cancel_rollback_removes_only_fresh_dependencies_not_preinstalled_or_manually_requested(
            bool preinstalled, bool manuallyRequested, bool shouldRemain)
        {
            // Arrange: an installation of "root", which depends on the installed fixture module. Unless it was
            // preinstalled, the installation counts as having installed that dependency freshly.
            using var fixture = new InstalledFixture();
            var root = fixture.AddModule("root");
            root.Status.Installed = false;
            root.Dependencies.Modules.Add(new() { Id = fixture.Module.Id });
            fixture.Installer.SaveModuleConfig(root);
            using var request = fixture.Installer.BeginCatalogInstallation(root, [fixture.Module, root]);
            if (!preinstalled) request.TrackNewContent(fixture.Module);
            if (manuallyRequested) fixture.Installer.RecordManualRequest(fixture.Module.Id);

            // Act: cancel the installation.
            await fixture.Installer.RollbackCatalogInstallationAsync(request, new Progress<string>());

            // Assert: the dependency and its files remain or are gone as expected.
            fixture.Installer.Registry.ReadIds().Contains(fixture.Module.Id).Should().Be(shouldRemain);
            File.Exists(fixture.Payload).Should().Be(shouldRemain);
            Directory.Exists(fixture.Runtime).Should().Be(shouldRemain);
            File.Exists(fixture.Module.SourcePath).Should().BeTrue("the local catalog manifest is retained");
        }

        /// <summary>
        /// Verifies that rolling back an installation whose root and dependency were both installed freshly removes
        /// both, which requires the root to go first because it depends on the dependency.
        /// </summary>
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

        /// <summary>
        /// Verifies that a freshly installed dependency shared by two pending installations survives the rollback of
        /// the first one and is removed only when the last installation that needs it is rolled back.
        /// </summary>
        [Fact]
        public async Task Shared_fresh_dependency_survives_first_cancel_and_is_cleaned_when_last_consumer_cancels()
        {
            // Arrange: two pending installations that both depend on the fixture module.
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

            // The first rollback keeps the dependency because the second installation still needs it.
            await fixture.Installer.RollbackCatalogInstallationAsync(firstRequest, new Progress<string>());
            File.Exists(fixture.Payload).Should().BeTrue();

            // After the first installation is released, rolling back the second one removes the dependency.
            firstRequest.Dispose();
            await fixture.Installer.RollbackCatalogInstallationAsync(secondRequest, new Progress<string>());

            fixture.Installer.Registry.ReadIds().Should().NotContain(fixture.Module.Id);
            File.Exists(fixture.Payload).Should().BeFalse();
        }

        /// <summary>
        /// Verifies that rolling back an installation keeps a freshly installed dependency, with its registry entry
        /// and files, when another installed module depends on it.
        /// </summary>
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


        // Test helpers

        /// <summary>
        /// Creates a module whose manifest path is <c>{root}/Modules/{id}/ASLM_Module.json</c>; nothing is written
        /// to disk.
        /// </summary>
        private static ModuleConfig MakeModule(string root, string id) => new()
        {
            Id = id,
            Name = id,
            Version = "1.0",
            SourcePath = Path.Combine(root, "Modules", id, ModuleManifestDiscovery.ManifestFileName)
        };

        /// <summary>
        /// Creates a module like <see cref="MakeModule"/> and writes its manifest to disk with the given installed
        /// flag.
        /// </summary>
        private static ModuleConfig WriteModule(string root, string id, bool installed)
        {
            var module = MakeModule(root, id);
            module.Status.Installed = installed;
            Directory.CreateDirectory(Path.GetDirectoryName(module.SourcePath)!);
            File.WriteAllText(module.SourcePath, JsonSerializer.Serialize(module));
            return module;
        }

        /// <summary>
        /// Creates an installed engine whose manifest path is <c>{root}/Engines/{id}/ASLM_Engine.json</c>, with a
        /// <c>runtime/tool.exe</c> executable and per-module environments in <c>venv-</c> folders; nothing is
        /// written to disk.
        /// </summary>
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


        // Test fixture

        /// <summary>
        /// An installed module on disk with its engine runtime, module environment, model store, artwork and payload
        /// file, registered through a module installer that uses its own port registry. Disposing it deletes the
        /// folders it created.
        /// </summary>
        private sealed class InstalledFixture : IDisposable
        {
            // State

            /// <summary>
            /// Folders created for the fixture, deleted on disposal.
            /// </summary>
            private readonly List<string> _directories = [];

            /// <summary>
            /// Installer under test, wired to <see cref="Ports"/>.
            /// </summary>
            public ModuleInstaller Installer { get; }

            /// <summary>
            /// Port registry that the installer releases module ports from.
            /// </summary>
            public PortRegistry Ports { get; }

            /// <summary>
            /// The installed module; it depends on <see cref="Engine"/>.
            /// </summary>
            public ModuleConfig Module { get; }

            /// <summary>
            /// The installed engine that <see cref="Module"/> uses; it has module environments.
            /// </summary>
            public EngineConfig Engine { get; }

            /// <summary>
            /// Runtime folder of the engine, holding a placeholder <c>tool.exe</c>.
            /// </summary>
            public string Runtime { get; }

            /// <summary>
            /// Environment folder that belongs to <see cref="Module"/> inside the engine folder.
            /// </summary>
            public string Environment { get; }

            /// <summary>
            /// Model store folder of the engine below the <c>Models</c> folder.
            /// </summary>
            public string Models { get; }

            /// <summary>
            /// Content file of the module (<c>main.py</c>) that an uninstall must remove.
            /// </summary>
            public string Payload { get; }


            // Initialization

            /// <summary>
            /// Creates the installed module, its engine and all their folders and files, then saves the module
            /// manifest through the installer so it is registered.
            /// </summary>
            /// <param name="moduleId">Id of the module; a unique generated id when null.</param>
            public InstalledFixture(string? moduleId = null)
            {
                // Start from an empty module registry and a port registry and installer that use it.
                using var layout = new AslmFileSystemLayout();
                File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), "[]");
                Ports = new PortRegistry(new AppDataStore(TestLoggerFactory.Create<AppDataStore>()));
                Installer = new ModuleInstaller(null!, null!, null!, ports: Ports);

                // Describe an installed module that depends on an installed engine.
                var id = "removal-test-" + Guid.NewGuid().ToString("N");
                Module = MakeModule(layout.Root, id);
                Module.Id = moduleId ?? id;
                Module.Status.Installed = true;
                Module.Icon = "icon.png";
                Engine = MakeEngine(layout.Root, id + "-engine");
                Module.Dependencies.Engines.Add(new ModuleEngineDependency { Id = Engine.Id });

                // Create the module, engine and model folders and remember them for cleanup.
                var moduleDirectory = Path.GetDirectoryName(Module.SourcePath)!;
                var engineDirectory = Path.GetDirectoryName(Engine.SourcePath)!;
                Models = Path.Combine(layout.Root, "Models", Engine.Id);
                _directories.AddRange([moduleDirectory, engineDirectory, Models]);
                foreach (var directory in _directories) Directory.CreateDirectory(directory);

                // Fill them: the engine runtime and manifest, the module environment, artwork and payload.
                Runtime = Path.Combine(engineDirectory, "runtime");
                Environment = ModuleEnvironmentResolver.GetEnvironmentDirectory(Module, Engine);
                Directory.CreateDirectory(Runtime);
                Directory.CreateDirectory(Environment);
                File.WriteAllText(Path.Combine(Runtime, "tool.exe"), "runtime");
                File.WriteAllText(Engine.SourcePath, JsonSerializer.Serialize(Engine));
                File.WriteAllText(Module.IconFullPath!, "artwork");
                Payload = Path.Combine(moduleDirectory, "main.py");
                File.WriteAllText(Payload, "installed content");

                // Saving the manifest through the installer also enters the module in the registry.
                Installer.SaveModuleConfig(Module);
            }


            // Lifecycle

            /// <summary>
            /// Deletes every folder the fixture created.
            /// </summary>
            public void Dispose()
            {
                foreach (var directory in _directories)
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }


            // Additional modules

            /// <summary>
            /// Creates an installed module with the id <c>{Module.Id}-{suffix}</c> and its (still empty) folder, and
            /// schedules the folder for cleanup. The manifest is not saved; callers save it when needed.
            /// </summary>
            /// <param name="suffix">Suffix that makes the module id unique.</param>
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
}
