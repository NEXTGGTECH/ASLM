// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies how per-module engine environments are detected, resolved from the engine manifest and applied to
    /// child process start info.
    /// </summary>
    public sealed class ModuleEnvironmentResolverTests
    {
        // Module environment

        /// <summary>
        /// Verifies that an engine whose module environment is enabled is reported as having one.
        /// </summary>
        [Fact]
        public void HasModuleEnvironment_returns_true_when_enabled()
        {
            var engine = new EngineConfig
            {
                ModuleEnvironment = new EngineModuleEnvironment { Enabled = true }
            };

            ModuleEnvironmentResolver.HasModuleEnvironment(engine).Should().BeTrue();
        }

        /// <summary>
        /// Verifies that the resolved environment directory is named after the engine's prefix and the module id, and
        /// that the <c>{environmentDir}</c> token in an environment variable expands to that directory.
        /// </summary>
        [Fact]
        public void ResolveEnvironment_builds_directory_under_engine_root()
        {
            using var layout = new AslmFileSystemLayout();
            CreateDiscoveredEngine(layout.Root, "tool.exe");
            var installer = new EngineInstaller();
            var engine = installer.GetEngineConfig("test-engine");
            engine.Should().NotBeNull();

            var module = ModuleConfigBuilder.Create(id: "demo-module");
            var resolver = new ModuleEnvironmentResolver(installer);
            var resolution = resolver.ResolveEnvironment(module, engine!);

            resolution.DirectoryPath.Should().Contain("venv-demo-module");
            resolution.EnvironmentVariables["ASLM_TEST"].Should().Be(resolution.DirectoryPath);
        }

        /// <summary>
        /// Verifies that applying the environment copies the declared variables into the start info and adds the
        /// <c>ASLM_ENGINE_ENV_DIR</c> variable.
        /// </summary>
        [Fact]
        public void ApplyEnvironmentVariables_writes_values_to_process_start_info()
        {
            using var layout = new AslmFileSystemLayout();
            CreateDiscoveredEngine(layout.Root, "tool.exe");
            var installer = new EngineInstaller();
            var engine = installer.GetEngineConfig("test-engine");
            engine.Should().NotBeNull();
            var module = ModuleConfigBuilder.Create(id: "env-module");

            var psi = new ProcessStartInfo { FileName = "cmd.exe" };
            var resolver = new ModuleEnvironmentResolver(installer);

            resolver.ApplyEnvironmentVariables(module, engine!, psi);

            psi.Environment["CUSTOM_FLAG"].Should().Be("enabled");
            psi.Environment["ASLM_ENGINE_ENV_DIR"].Should().NotBeNullOrWhiteSpace();
        }


        // Test helpers

        /// <summary>
        /// Writes an installed engine named <c>test-engine</c> under <c>{root}/Engines</c>: a placeholder executable,
        /// a non-empty runtime folder (so it is not reset as missing) and a manifest with an enabled module
        /// environment that sets <c>ASLM_TEST</c> and <c>CUSTOM_FLAG</c>.
        /// </summary>
        /// <param name="root">ASLM root directory that holds the <c>Engines</c> folder.</param>
        /// <param name="executableFileName">File name of the engine executable and of the environment executable.</param>
        private static void CreateDiscoveredEngine(string root, string executableFileName)
        {
            // Engine folder with a placeholder executable and a non-empty runtime folder.
            var engineDir = Path.Combine(root, "Engines", "test-engine");
            Directory.CreateDirectory(engineDir);
            var executablePath = Path.Combine(engineDir, executableFileName);
            File.WriteAllText(executablePath, string.Empty);
            var runtimeDir = Path.Combine(engineDir, "runtime");
            Directory.CreateDirectory(runtimeDir);
            File.WriteAllText(Path.Combine(runtimeDir, "placeholder.txt"), string.Empty);

            // Manifest of an installed engine whose module environment lives in "venv-{module}" folders.
            var manifest = new EngineConfig
            {
                FileVersion = 1,
                Id = "test-engine",
                Name = "Test Engine",
                Version = "1.0.0",
                ExecutablePath = executableFileName,
                Status = new EngineStatus { Installed = true },
                ModuleEnvironment = new EngineModuleEnvironment
                {
                    Enabled = true,
                    DirectoryPrefix = "venv-",
                    ExecutablePath = executableFileName,
                    Environment = new Dictionary<string, string>
                    {
                        ["ASLM_TEST"] = "{environmentDir}",
                        ["CUSTOM_FLAG"] = "enabled"
                    }
                }
            };
            manifest.Normalize();

            // Persist the manifest where engine discovery looks for it.
            var manifestPath = Path.Combine(engineDir, "ASLM_Engine.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            _ = manifestPath;
        }
    }
}
