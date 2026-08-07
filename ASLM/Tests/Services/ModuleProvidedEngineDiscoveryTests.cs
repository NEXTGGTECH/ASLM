// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleProvidedEngineDiscoveryTests
{
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
