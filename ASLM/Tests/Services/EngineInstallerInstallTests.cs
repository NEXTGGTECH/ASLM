// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies filesystem behavior of declarative engine installation actions.
/// </summary>
public sealed class EngineInstallerInstallTests
{
    /// <summary>
    /// Ensures a first install can move a runtime into a module-provided engine directory.
    /// </summary>
    [Fact]
    public async Task InstallAsync_creates_missing_engine_directory_before_move()
    {
        // Isolate the state and temporary paths so the regression test cannot affect real engines.
        using var layout = new AslmFileSystemLayout();
        var uniqueId = $"move-runtime-{Guid.NewGuid():N}";
        var ownerId = $"move-provider-{Guid.NewGuid():N}";
        var engineDir = Path.Combine(layout.Root, "Engines", "Modules", ownerId, uniqueId);
        var statePath = Path.Combine(engineDir, "ASLM_Engine.json");
        var tempDir = Path.Combine(Path.GetTempPath(), "ASLM", uniqueId);
        var payloadDir = Path.Combine(tempDir, "payload");
        var payloadFile = Path.Combine(payloadDir, "runtime.bin");
        Directory.CreateDirectory(payloadDir);
        await File.WriteAllTextAsync(payloadFile, "runtime");

        // Reproduce a first-time module-provided engine whose state directory does not exist.
        var platformKey = PlatformInfo.PlatformKey;
        var config = new EngineConfig
        {
            FileVersion = 2,
            Id = uniqueId,
            Name = "Move Runtime",
            Version = "1.0.0",
            Type = "runtime",
            SourcePath = statePath,
            SupportedPlatforms =
            [
                new SupportedPlatform
                {
                    Os = PlatformInfo.OsKey,
                    Arch = PlatformInfo.ArchKey,
                    Key = platformKey
                }
            ],
            Platforms = new Dictionary<string, EnginePlatform>(StringComparer.OrdinalIgnoreCase)
            {
                [platformKey] = new EnginePlatform
                {
                    ExecutablePath = "runtime/runtime.bin",
                    Install =
                    [
                        new InstallStep
                        {
                            Action = "move",
                            Source = "{temp}/payload",
                            Dest = "{engineDir}/runtime"
                        }
                    ]
                }
            }
        };

        try
        {
            // Execute the production move pipeline and verify both runtime and state were persisted.
            await new EngineInstaller().InstallAsync(config, new Progress<string>());

            File.Exists(Path.Combine(engineDir, "runtime", "runtime.bin")).Should().BeTrue();
            File.Exists(statePath).Should().BeTrue();
        }
        finally
        {
            // Remove only the unique directories created by this test.
            if (Directory.Exists(Path.Combine(layout.Root, "Engines", "Modules", ownerId)))
            {
                Directory.Delete(Path.Combine(layout.Root, "Engines", "Modules", ownerId), recursive: true);
            }

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
