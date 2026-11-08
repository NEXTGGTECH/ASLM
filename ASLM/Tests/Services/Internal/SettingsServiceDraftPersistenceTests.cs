// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies edit-session commit, manifest persistence, and deferred runtime application.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class SettingsServiceDraftPersistenceTests
    {
        // Draft loading

        /// <summary>
        /// Verifies runtime loading updates and accepts only the detached draft.
        /// </summary>
        [Fact]
        public void ApplyLoadedSettingsToDraft_does_not_mutate_manifest_value()
        {
            // Arrange: a module whose manifest value differs from the value that the runtime reports.
            var module = ModuleConfigBuilder.Create(configure: config =>
            {
                config.Settings =
                [
                    new ModuleSetting
                    {
                        Key = "runtime-value",
                        Name = "Runtime value",
                        Type = "string",
                        Default = "default-value",
                        Value = "manifest-value"
                    }
                ];
                config.Normalize();
            });
            var service = new SettingsService(null!, null!, CreateRunner());
            var moduleDraft = new ModuleSettingsDraft(module);

            // Act: apply the runtime value to the draft.
            service.ApplyLoadedSettingsToDraft(
                moduleDraft,
                [new LoadedSetting(module.Settings[0], "runtime-value")]);

            // Assert: only the draft holds the loaded value, and it is not counted as a pending change.
            module.Settings[0].Value.Should().Be("manifest-value");
            moduleDraft.GetSetting("runtime-value").Value.Should().Be("runtime-value");
            moduleDraft.HasChanges.Should().BeFalse();
        }


        // Saving

        /// <summary>
        /// Verifies that changed drafts persist together while a failed set command is reported as deferred.
        /// </summary>
        [Fact]
        public async Task SaveActiveModuleAsync_commits_drafts_and_reports_deferred_runtime_setting()
        {
            // Arrange: an installed module on disk with a plain setting and a setting whose set command cannot run.
            using var layout = new AslmFileSystemLayout();
            var moduleDir = Path.Combine(layout.ModulesDir, "settings-draft-persistence");
            var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
            Directory.CreateDirectory(moduleDir);

            var module = ModuleConfigBuilder.Create(
                id: "settings-draft-persistence",
                configure: config =>
                {
                    config.SourcePath = manifestPath;
                    config.Status.Installed = true;
                    config.Status.FirstRunCompleted = true;
                    config.Settings =
                    [
                        new ModuleSetting
                        {
                            Key = "plain",
                            Name = "Plain",
                            Type = "string",
                            Default = "plain-default",
                            Value = "plain-saved"
                        },
                        new ModuleSetting
                        {
                            Key = "runtime",
                            Name = "Runtime",
                            Type = "string",
                            Default = "runtime-default",
                            Value = "runtime-saved",
                            SetExec = "aslm-command-that-does-not-exist {value}"
                        }
                    ];
                    config.Normalize();
                });
            var runner = CreateRunner();
            var installer = new ModuleInstaller(runner, null!, null!);
            var service = new SettingsService(null!, installer, runner);
            await installer.SaveConfigAsync(module, raiseModulesChanged: false);
            var moduleDraft = new ModuleSettingsDraft(module);
            await service.LoadModuleDraftAsync(moduleDraft, reloadRuntimeValues: false);

            // Act: edit both settings in the draft and save the active module.
            moduleDraft.GetSetting("plain").Value = "plain-edited";
            moduleDraft.GetSetting("runtime").Value = "runtime-edited";

            var result = await service.SaveActiveModuleAsync(moduleDraft);

            // Assert: the module is reported as touched, the failing command as deferred, and the draft as committed.
            result.TouchedModules.Should().ContainSingle().Which.Should().BeSameAs(module);
            result.DeferredSettings.Should().ContainSingle().Which.Should().Contain("Runtime");
            moduleDraft.HasChanges.Should().BeFalse();

            // Both edited values, including the deferred one, are persisted in the manifest on disk.
            var persisted = await installer.LoadModuleConfig(manifestPath);
            persisted.Should().NotBeNull();
            persisted!.Settings.Single(setting => setting.Key == "plain").Value.Should().Be("plain-edited");
            persisted.Settings.Single(setting => setting.Key == "runtime").Value.Should().Be("runtime-edited");

            // Saving again without further edits touches nothing and defers nothing.
            var unchangedResult = await service.SaveActiveModuleAsync(moduleDraft);
            unchangedResult.TouchedModules.Should().BeEmpty();
            unchangedResult.DeferredSettings.Should().BeEmpty();
        }


        // Helpers

        /// <summary>
        /// Creates the minimal real runner needed to characterize failed setting-command execution.
        /// </summary>
        private static ModuleRunner CreateRunner()
        {
            var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            var ports = new PortRegistry(appData);
            return new ModuleRunner(
                null!,
                null!,
                ports,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                TestLoggerFactory.Create<ModuleRunner>());
        }
    }
}
