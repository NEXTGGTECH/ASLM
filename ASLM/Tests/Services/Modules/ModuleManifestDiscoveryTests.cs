// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// xUnit collection whose tests never run in parallel with other tests; the classes in it read and write the
    /// shared <c>Modules</c> directory of the test output root.
    /// </summary>
    [CollectionDefinition("ModuleManifestDiscovery", DisableParallelization = true)]
    public sealed class ModuleManifestDiscoveryCollection;

    /// <summary>
    /// Verifies discovery of installed module manifests and the path checks that guard it.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class ModuleManifestDiscoveryTests
    {
        // Manifest discovery

        /// <summary>
        /// Verifies that enumeration returns the manifest of a first-level module folder and ignores a manifest
        /// nested deeper below it.
        /// </summary>
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

        /// <summary>
        /// Verifies that only <c>Modules/{folder}/ASLM_Module.json</c> counts as an installed module manifest;
        /// a manifest in a nested folder and a file outside the modules root are rejected.
        /// </summary>
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

        /// <summary>
        /// Verifies that a manifest path inside the modules root is reported as contained, and the sibling
        /// application directory is not.
        /// </summary>
        [Fact]
        public void IsPathUnderDirectory_detects_paths_inside_modules_root()
        {
            using var layout = new AslmFileSystemLayout();
            var manifest = WriteManifest(layout.ModulesDir, "demo");

            ModuleManifestDiscovery.IsPathUnderDirectory(layout.ModulesDir, manifest).Should().BeTrue();
            ModuleManifestDiscovery.IsPathUnderDirectory(layout.ModulesDir, layout.AppDir).Should().BeFalse();
        }


        // Test helpers

        /// <summary>
        /// Deletes every module folder below the modules root so a test starts without leftovers of other tests.
        /// </summary>
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

        /// <summary>
        /// Writes a minimal valid module manifest into <c>{modulesRoot}/{moduleFolder}/{nestedSegments}</c>.
        /// </summary>
        /// <param name="modulesRoot">The modules root directory.</param>
        /// <param name="moduleFolder">Name of the first-level module folder.</param>
        /// <param name="nestedSegments">Optional sub-folder names below the module folder that receive the manifest.</param>
        /// <returns>The full path of the written manifest file.</returns>
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
}
