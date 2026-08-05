// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleEnvironmentResolverTests
{
    [Fact]
    public void HasModuleEnvironment_returns_true_when_enabled()
    {
        var engine = new EngineConfig
        {
            ModuleEnvironment = new EngineModuleEnvironment { Enabled = true }
        };

        ModuleEnvironmentResolver.HasModuleEnvironment(engine).Should().BeTrue();
    }

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

    private static void CreateDiscoveredEngine(string root, string executableFileName)
    {
        var engineDir = Path.Combine(root, "Engines", "test-engine");
        Directory.CreateDirectory(engineDir);
        var executablePath = Path.Combine(engineDir, executableFileName);
        File.WriteAllText(executablePath, string.Empty);
        var runtimeDir = Path.Combine(engineDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "placeholder.txt"), string.Empty);

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

        var manifestPath = Path.Combine(engineDir, "ASLM_Engine.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        _ = manifestPath;
    }
}
