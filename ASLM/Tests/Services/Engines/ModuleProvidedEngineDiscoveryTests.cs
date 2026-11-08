// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using ASLM.Models;
using System.Text.Json;

namespace ASLM.Tests.Services.Engines
{
    /// <summary>
    /// Verifies how the platform support of a module follows the engines it depends on, how engines embedded in
    /// module manifests are discovered, and how a changed embedded engine definition triggers a reinstall.
    /// The tests work on the module and engine folders of the shared test layout.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class ModuleProvidedEngineDiscoveryTests
    {
        // Platform support

        /// <summary>
        /// Verifies, for module manifest versions 1 and 2, that a module is listed but not selectable while its
        /// required engine supports only another platform, that the platform list shown for it is the intersection
        /// of module and engine platforms, and that rewriting the engine for this platform makes a reloaded module
        /// supported and available.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task Catalog_setup_and_info_use_engine_intersection_and_reload_changed_definitions(int version)
        {
            // Arrange: a module that depends on an engine of the same unique id, with both manifests on disk.
            using var layout = new AslmFileSystemLayout();
            var id = "platform-test-" + Guid.NewGuid().ToString("N");
            var moduleDir = Path.Combine(layout.ModulesDir, id);
            var engineDir = Path.Combine(layout.Root, "Engines", id);
            Directory.CreateDirectory(moduleDir);
            Directory.CreateDirectory(engineDir);
            var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
            var enginePath = Path.Combine(engineDir, "ASLM_Engine.json");
            var foreign = PlatformInfo.OsKey == "windows" ? "macos-arm64" : "windows-amd64";
            try
            {
                var module = new ModuleConfig
                {
                    FileVersion = version, Id = id,
                    SupportedPlatforms = version == 2
                        ? [SupportedPlatform.FromKey(PlatformInfo.PlatformKey), SupportedPlatform.FromKey(foreign)] : [],
                    Dependencies = new() { Engines = [new() { Id = id }] }
                };
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(module));
                await File.WriteAllTextAsync(enginePath, JsonSerializer.Serialize(TestEngine(id, foreign)));
                var installer = new ModuleInstaller(null!, null!, null!);

                // Initial setup lists the module but cannot select it on this host.
                var catalog = await installer.DiscoverModulesAsync();
                var discovered = catalog.Single(item => item.Id == id);
                discovered.IsSupportedOnCurrentPlatform.Should().BeFalse();
                discovered.EffectiveSupportedPlatforms.Should().ContainSingle().Which.Key.Should().Be(foreign);
                (await installer.DiscoverAvailableModulesAsync()).Should().NotContain(item => item.Id == id);

                // The info page shows the platforms that remain after intersecting module and engine.
                var fields = ASLM.Pages.ModuleInfo.CreateFields(discovered, catalog, []);
                fields.Single(field => field.Key == ASLM.Localization.LocalizationKeys.ModuleInfo_Platforms)
                    .Value.Should().Be(foreign.Replace("-", " "));

                // Changing the engine definition on disk is picked up when the module is loaded again.
                await File.WriteAllTextAsync(enginePath, JsonSerializer.Serialize(TestEngine(id, PlatformInfo.PlatformKey)));
                (await installer.LoadModuleConfig(manifestPath))!.IsSupportedOnCurrentPlatform.Should().BeTrue();
                (await installer.DiscoverAvailableModulesAsync()).Should().Contain(item => item.Id == id);
            }
            finally
            {
                Directory.Delete(moduleDir, recursive: true);
                Directory.Delete(engineDir, recursive: true);
            }
        }

        /// <summary>
        /// Verifies that the platform check of a downloaded module manifest uses the engine embedded in the new
        /// manifest instead of the old installed definition, never falls back to the old definition when the
        /// embedded one is removed, and keeps a standalone engine of the same id authoritative in both directions.
        /// </summary>
        [Fact]
        public void Downloaded_manifest_uses_new_embedded_engine_but_preserves_standalone_precedence()
        {
            // Arrange: an installed module whose embedded engine supports the current platform.
            using var layout = new AslmFileSystemLayout();
            var id = "archive-test-" + Guid.NewGuid().ToString("N");
            var moduleDir = Path.Combine(layout.ModulesDir, id);
            var engineDir = Path.Combine(layout.Root, "Engines", id);
            Directory.CreateDirectory(moduleDir);
            var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
            var foreign = PlatformInfo.OsKey == "windows" ? "macos-arm64" : "windows-amd64";
            try
            {
                var current = new ModuleConfig
                {
                    FileVersion = 2, Id = id,
                    SupportedPlatforms = [SupportedPlatform.FromKey(PlatformInfo.PlatformKey)],
                    Dependencies = new() { Engines = [new() { Id = id }] },
                    Engines = [TestEngine(id, PlatformInfo.PlatformKey)]
                };
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(current));
                var installer = new ModuleInstaller(null!, null!, null!);

                // Act and assert, new embedded engine: the downloaded manifest embeds an engine for another
                // platform, which wins over the old installed definition.
                var downloaded = ModuleManifestParser.Parse(JsonSerializer.Serialize(current));
                downloaded.Engines = [TestEngine(id, foreign)];
                installer.ResolvePlatformSupport(downloaded);
                downloaded.IsSupportedOnCurrentPlatform.Should().BeFalse();
                downloaded.EffectiveSupportedPlatforms.Should().BeEmpty();

                // Removing an embedded definition must not fall back to the installed old definition.
                downloaded.Engines.Clear();
                installer.ResolvePlatformSupport(downloaded);
                downloaded.IsSupportedOnCurrentPlatform.Should().BeFalse();

                // A standalone engine for the current platform makes the module supported despite the incompatible
                // embedded one.
                Directory.CreateDirectory(engineDir);
                File.WriteAllText(Path.Combine(engineDir, "ASLM_Engine.json"),
                    JsonSerializer.Serialize(TestEngine(id, PlatformInfo.PlatformKey)));
                downloaded.Engines = [TestEngine(id, foreign)];
                installer.ResolvePlatformSupport(downloaded);
                downloaded.IsSupportedOnCurrentPlatform.Should().BeTrue();

                // Likewise, a compatible embedded definition cannot replace an incompatible standalone engine.
                File.WriteAllText(Path.Combine(engineDir, "ASLM_Engine.json"),
                    JsonSerializer.Serialize(TestEngine(id, foreign)));
                downloaded.Engines = [TestEngine(id, PlatformInfo.PlatformKey)];
                installer.ResolvePlatformSupport(downloaded);
                downloaded.IsSupportedOnCurrentPlatform.Should().BeFalse();
            }
            finally
            {
                Directory.Delete(moduleDir, recursive: true);
                if (Directory.Exists(engineDir)) Directory.Delete(engineDir, recursive: true);
            }
        }


        // Engine factory

        /// <summary>
        /// Builds a normalized engine with a single platform block for the given platform key.
        /// </summary>
        /// <param name="id">Engine id.</param>
        /// <param name="platformKey">Platform key of the only platform block, for example <c>windows-amd64</c>.</param>
        private static EngineConfig TestEngine(string id, string platformKey)
        {
            var engine = new EngineConfig
            {
                Id = id,
                Platforms = new() { [platformKey] = new() { ExecutablePath = "runtime/tool" } }
            };
            engine.Normalize();
            return engine;
        }


        // Discovery and reconciliation

        /// <summary>
        /// Verifies that discovery finds an engine embedded in a version 2 module manifest and marks it as
        /// module-provided, with the owner module, the module manifest as definition source, a state path below the
        /// engines folder of that module, and support for the current platform.
        /// </summary>
        [Fact]
        public void Discovery_reads_engine_manifests_from_module_v2()
        {
            // Arrange: a module folder whose manifest embeds the engine definition.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);
            var moduleDir = Path.Combine(layout.ModulesDir, "provider-module");
            Directory.CreateDirectory(moduleDir);
            File.WriteAllText(
                Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName),
                """
                {
                  "fileVersion": 2,
                  "id": "provider-module",
                  "name": "Provider",
                  "supportedPlatforms": [ { "os": "windows", "arch": "amd64" } ],
                  "dependencies": { "engines": [ { "id": "vendor-runtime" } ] },
                  "engines": [
                    {
                      "fileVersion": 2,
                      "id": "vendor-runtime",
                      "name": "Vendor Runtime",
                      "supportedPlatforms": [
                        { "os": "windows", "arch": "amd64", "key": "windows-amd64" }
                      ],
                      "windows-amd64": {
                        "executablePath": "runtime/vendor.exe",
                        "install": []
                      }
                    }
                  ]
                }
                """);

            // Act: discover engines, which also scans the module manifests.
            var installer = new EngineInstaller();
            var engine = installer.DiscoverEngines().Single(item => item.Id == "vendor-runtime");

            // Assert: ownership, source, state location and platform support come from the module.
            engine.IsModuleProvided.Should().BeTrue();
            engine.OwnerModuleId.Should().Be("provider-module");
            engine.DefinitionSourcePath.Should().EndWith("ASLM_Module.json");
            engine.SourcePath.Should().Contain(Path.Combine("Engines", "Modules", "provider-module", "vendor-runtime"));
            engine.IsSupportedOnCurrentPlatform.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an installed module-provided engine is reinstalled when its embedded definition changes,
        /// even if the module no longer lists it as a required dependency: the recorded manifest hash is replaced,
        /// the engine stays installed, and the log reports the manifest change.
        /// </summary>
        [Fact]
        public async Task Reconciliation_reinstalls_installed_engine_when_embedded_manifest_changes()
        {
            // Arrange: a clean modules folder and no leftover engine state for the provider module.
            using var layout = new AslmFileSystemLayout();
            ResetDirectory(layout.ModulesDir);

            var moduleDir = Path.Combine(layout.ModulesDir, "provider-module");
            var manifestPath = Path.Combine(moduleDir, ModuleManifestDiscovery.ManifestFileName);
            var engineStateDir = Path.Combine(
                layout.Root,
                "Engines",
                "Modules",
                "provider-module",
                "vendor-runtime");
            if (Directory.Exists(engineStateDir))
            {
                Directory.Delete(engineStateDir, recursive: true);
            }

            Directory.CreateDirectory(moduleDir);
            File.WriteAllText(manifestPath, BuildManifest("runtime/vendor.exe", isRequired: true));

            var installer = new EngineInstaller();
            var reconciler = new ModuleEngineReconciler(installer);
            var log = new RecordingProgress();
            var module = ModuleManifestParser.Parse(File.ReadAllText(manifestPath), manifestPath);

            // Act, first install: the required engine is installed and its manifest hash recorded.
            await reconciler.ReconcileRequiredEnginesAsync(module, log);

            // The install steps are empty, so provide the runtime file that discovery expects for an installed engine.
            var runtimeDir = Path.Combine(engineStateDir, "runtime");
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllText(Path.Combine(runtimeDir, "vendor.exe"), "runtime");

            installer.InvalidateCache();
            var installed = installer.FindAvailableEngine("vendor-runtime")!;
            var firstHash = installed.Status.InstalledManifestHash;
            firstHash.Should().NotBeNullOrWhiteSpace();

            // Act, definition change: the module embeds a different executable and no longer requires the engine.
            File.WriteAllText(manifestPath, BuildManifest("runtime/vendor-v2.exe", isRequired: false));
            module = ModuleManifestParser.Parse(File.ReadAllText(manifestPath), manifestPath);
            await reconciler.ReconcileRequiredEnginesAsync(module, log);

            // Assert: the engine was reinstalled with a new hash and the log names the manifest change.
            installer.InvalidateCache();
            var updated = installer.FindAvailableEngine("vendor-runtime")!;
            updated.Status.Installed.Should().BeTrue();
            updated.Status.InstalledManifestHash.Should().NotBe(firstHash);
            log.Messages.Should().Contain(message =>
                message.Contains("manifest changed", StringComparison.OrdinalIgnoreCase));
        }


        // Manifest and directory helpers

        /// <summary>
        /// Builds the version 2 manifest of the <c>provider-module</c> that embeds the <c>vendor-runtime</c> engine.
        /// </summary>
        /// <param name="executablePath">Executable path declared in the embedded engine's platform block.</param>
        /// <param name="isRequired">Whether the module lists the engine as a dependency.</param>
        private static string BuildManifest(string executablePath, bool isRequired)
        {
            // The dependency list names the embedded engine only when the module requires it.
            var engineDependencies = isRequired
                ? "[ { \"id\": \"vendor-runtime\" } ]"
                : "[]";

            // Interpolated raw string; only the dependency list and the executable path vary.
            return $$"""
            {
              "fileVersion": 2,
              "id": "provider-module",
              "name": "Provider",
              "supportedPlatforms": [ { "os": "windows", "arch": "amd64" } ],
              "dependencies": { "engines": {{engineDependencies}} },
              "engines": [
                {
                  "fileVersion": 2,
                  "id": "vendor-runtime",
                  "name": "Vendor Runtime",
                  "version": "1.0.0",
                  "supportedPlatforms": [
                    { "os": "windows", "arch": "amd64", "key": "windows-amd64" }
                  ],
                  "windows-amd64": {
                    "executablePath": "{{executablePath}}",
                    "install": []
                  }
                }
              ]
            }
            """;
        }

        /// <summary>
        /// Removes all subdirectories of the folder, or creates the folder when it is missing. Files directly
        /// inside the folder are kept.
        /// </summary>
        private static void ResetDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }


        // Test doubles

        /// <summary>
        /// Progress sink that keeps every reported message for later assertions.
        /// </summary>
        private sealed class RecordingProgress : IProgress<string>
        {
            /// <summary>
            /// Messages reported so far, in order.
            /// </summary>
            public List<string> Messages { get; } = [];

            /// <summary>
            /// Appends the reported message to <see cref="Messages"/>.
            /// </summary>
            public void Report(string value) => Messages.Add(value);
        }
    }
}
