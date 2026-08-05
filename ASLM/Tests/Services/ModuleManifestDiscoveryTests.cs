// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

[CollectionDefinition("ModuleManifestDiscovery", DisableParallelization = true)]
public sealed class ModuleManifestDiscoveryCollection;

[Collection("ModuleManifestDiscovery")]
public sealed class ModuleManifestDiscoveryTests
{
    [Fact]
    public void EnumerateInstalledManifests_returns_only_root_manifests()
    {
        using var layout = new AslmFileSystemLayout();
        ResetModulesDirectory(layout.ModulesDir);
        WriteManifest(layout.ModulesDir, "root-module");
        WriteManifest(layout.ModulesDir, "nested-module", "vendor", "package");

        var manifests = ModuleManifestDiscovery
            .EnumerateInstalledManifests(layout.ModulesDir)
            .Select(Path.GetFullPath)
            .ToList();

        manifests.Should().HaveCount(1);
        manifests[0].Should().Be(Path.GetFullPath(
            Path.Combine(layout.ModulesDir, "root-module", ModuleManifestDiscovery.ManifestFileName)));
    }

    [Fact]
    public void IsInstalledModuleManifest_accepts_root_manifest_and_rejects_nested()
    {
        using var layout = new AslmFileSystemLayout();
        ResetModulesDirectory(layout.ModulesDir);
        var rootManifest = WriteManifest(layout.ModulesDir, "demo");
        var nestedManifest = WriteManifest(layout.ModulesDir, "demo", "App");

        ModuleManifestDiscovery.IsInstalledModuleManifest(layout.ModulesDir, rootManifest)
            .Should().BeTrue();
        ModuleManifestDiscovery.IsInstalledModuleManifest(layout.ModulesDir, nestedManifest)
            .Should().BeFalse();
        ModuleManifestDiscovery.IsInstalledModuleManifest(layout.ModulesDir, layout.AppDataFilePath)
            .Should().BeFalse();
    }

    [Fact]
    public void IsPathUnderDirectory_detects_paths_inside_modules_root()
    {
        using var layout = new AslmFileSystemLayout();
        var manifest = WriteManifest(layout.ModulesDir, "demo");

        ModuleManifestDiscovery.IsPathUnderDirectory(layout.ModulesDir, manifest).Should().BeTrue();
        ModuleManifestDiscovery.IsPathUnderDirectory(layout.ModulesDir, layout.AppDir).Should().BeFalse();
    }

    private static void ResetModulesDirectory(string modulesRoot)
    {
        if (!Directory.Exists(modulesRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(modulesRoot))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteManifest(string modulesRoot, string moduleFolder, params string[] nestedSegments)
    {
        var directory = Path.Combine(
            new[] { modulesRoot, moduleFolder }.Concat(nestedSegments).ToArray());
        Directory.CreateDirectory(directory);

        var manifestPath = Path.Combine(directory, ModuleManifestDiscovery.ManifestFileName);
        File.WriteAllText(
            manifestPath,
            """
            {
              "fileVersion": 1,
              "id": "test-module",
              "name": "Test Module",
              "version": "1.0.0"
            }
            """);

        return manifestPath;
    }
}
