// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;
using ASLM.Models;
using System.Text.Json;

namespace ASLM.Tests.Services;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleProvidedEngineDiscoveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Catalog_setup_and_info_use_engine_intersection_and_reload_changed_definitions(int version)
    {
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
            var fields = ASLM.Pages.ModuleInfo.CreateFields(discovered, catalog, []);
            fields.Single(field => field.Key == ASLM.Localization.LocalizationKeys.ModuleInfo_Platforms)
                .Value.Should().Be(foreign.Replace("-", " "));

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

    [Fact]
    public void Downloaded_manifest_uses_new_embedded_engine_but_preserves_standalone_precedence()
    {
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
            var downloaded = ModuleManifestParser.Parse(JsonSerializer.Serialize(current));
            downloaded.Engines = [TestEngine(id, foreign)];
            installer.ResolvePlatformSupport(downloaded);
            downloaded.IsSupportedOnCurrentPlatform.Should().BeFalse();
            downloaded.EffectiveSupportedPlatforms.Should().BeEmpty();

            // Removing an embedded definition must not fall back to the installed old definition.
            downloaded.Engines.Clear();
            installer.ResolvePlatformSupport(downloaded);
            downloaded.IsSupportedOnCurrentPlatform.Should().BeFalse();

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

    [Fact]
    public void Discovery_reads_engine_manifests_from_module_v2()
    {
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

        var installer = new EngineInstaller();
        var engine = installer.DiscoverEngines().Single(item => item.Id == "vendor-runtime");

        engine.IsModuleProvided.Should().BeTrue();
        engine.OwnerModuleId.Should().Be("provider-module");
        engine.DefinitionSourcePath.Should().EndWith("ASLM_Module.json");
        engine.SourcePath.Should().Contain(Path.Combine("Engines", "Modules", "provider-module", "vendor-runtime"));
        engine.IsSupportedOnCurrentPlatform.Should().BeTrue();
    }

    [Fact]
    public async Task Reconciliation_reinstalls_installed_engine_when_embedded_manifest_changes()
    {
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

        await reconciler.ReconcileRequiredEnginesAsync(module, log);

        var runtimeDir = Path.Combine(engineStateDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "vendor.exe"), "runtime");

        installer.InvalidateCache();
        var installed = installer.FindAvailableEngine("vendor-runtime")!;
        var firstHash = installed.Status.InstalledManifestHash;
        firstHash.Should().NotBeNullOrWhiteSpace();

        File.WriteAllText(manifestPath, BuildManifest("runtime/vendor-v2.exe", isRequired: false));
        module = ModuleManifestParser.Parse(File.ReadAllText(manifestPath), manifestPath);
        await reconciler.ReconcileRequiredEnginesAsync(module, log);

        installer.InvalidateCache();
        var updated = installer.FindAvailableEngine("vendor-runtime")!;
        updated.Status.Installed.Should().BeTrue();
        updated.Status.InstalledManifestHash.Should().NotBe(firstHash);
        log.Messages.Should().Contain(message =>
            message.Contains("manifest changed", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildManifest(string executablePath, bool isRequired)
    {
        var engineDependencies = isRequired
            ? "[ { \"id\": \"vendor-runtime\" } ]"
            : "[]";

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

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];

        public void Report(string value) => Messages.Add(value);
    }
}
