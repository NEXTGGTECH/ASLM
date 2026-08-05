// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;

namespace ASLM.Tests.Services;

public sealed class EngineConfigPlatformTests
{
    private static EngineConfig Deserialize(string json) =>
        JsonSerializer.Deserialize<EngineConfig>(json)!;

    [Fact]
    public void V2_manifest_resolves_per_platform_blocks()
    {
        var config = Deserialize(
            """
            {
                "fileVersion": 2,
                "id": "demo",
                "type": "runtime",
                "supportedPlatforms": [
                    { "os": "windows", "arch": "amd64", "key": "windows-amd64" },
                    { "os": "windows", "arch": "arm64", "key": "windows-arm64" }
                ],
                "windows-amd64": {
                    "executablePath": "runtime/x64.exe",
                    "install": [ { "action": "download", "url": "amd64-url" } ]
                },
                "windows-arm64": {
                    "executablePath": "runtime/arm64.exe",
                    "install": [ { "action": "download", "url": "arm64-url" } ]
                },
                "status": { "installed": false }
            }
            """);

        config.Platforms.Should().HaveCount(2);

        config.ResolveForPlatform("windows", "amd64");
        config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        config.ExecutablePath.Should().Be("runtime/x64.exe");
        config.Install.Single().Url.Should().Be("amd64-url");

        config.ResolveForPlatform("windows", "arm64");
        config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        config.ExecutablePath.Should().Be("runtime/arm64.exe");
        config.Install.Single().Url.Should().Be("arm64-url");
    }

    [Fact]
    public void Unsupported_platform_marks_engine_unavailable()
    {
        var config = Deserialize(
            """
            {
                "fileVersion": 2,
                "id": "demo",
                "supportedPlatforms": [ { "os": "windows", "arch": "amd64", "key": "windows-amd64" } ],
                "windows-amd64": { "executablePath": "x.exe", "install": [] }
            }
            """);

        config.ResolveForPlatform("macos", "arm64");

        config.IsSupportedOnCurrentPlatform.Should().BeFalse();
        config.ActivePlatform.Should().BeNull();
        config.ExecutablePath.Should().BeEmpty();
        config.Install.Should().BeEmpty();
    }

    [Fact]
    public void Legacy_v1_flat_manifest_is_lifted_into_amd64_block()
    {
        var config = Deserialize(
            """
            {
                "fileVersion": 1,
                "id": "legacy",
                "type": "runtime",
                "executablePath": "runtime/python.exe",
                "requirements": { "os": "windows", "arch": "x64", "diskSpaceMb": 256 },
                "install": [ { "action": "download", "url": "legacy-url" } ],
                "status": { "installed": false }
            }
            """);

        config.SupportedPlatforms.Should().ContainSingle().Which.Key.Should().Be("windows-amd64");

        config.ResolveForPlatform("windows", "amd64");
        config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        config.ExecutablePath.Should().Be("runtime/python.exe");
        config.Install.Single().Url.Should().Be("legacy-url");
        config.Requirements!.DiskSpaceMb.Should().Be(256);
    }

    [Fact]
    public void Legacy_update_assetName_dictionary_collapses_to_platform_string()
    {
        var config = Deserialize(
            """
            {
                "fileVersion": 1,
                "id": "ollama-service",
                "requirements": { "os": "windows", "arch": "amd64" },
                "install": [],
                "update": { "repo": "ollama/ollama", "assetName": { "windows-x64": "ollama-windows-amd64.zip" } },
                "status": { "installed": false }
            }
            """);

        config.ResolveForPlatform("windows", "amd64");

        config.Update!.Repo.Should().Be("ollama/ollama");
        config.Update.AssetName.Should().Be("ollama-windows-amd64.zip");
    }

    [Fact]
    public void Serialization_always_writes_v2_and_round_trips()
    {
        var config = Deserialize(
            """
            {
                "fileVersion": 1,
                "id": "legacy",
                "executablePath": "tool.exe",
                "install": [ { "action": "download", "url": "u" } ],
                "status": { "installed": true }
            }
            """);

        var serialized = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        serialized.Should().Contain("\"fileVersion\": 2");
        serialized.Should().Contain("supportedPlatforms");
        serialized.Should().Contain("windows-amd64");

        var round = Deserialize(serialized);
        round.FileVersion.Should().Be(2);
        round.ResolveForPlatform("windows", "amd64");
        round.ExecutablePath.Should().Be("tool.exe");
        round.Status.Installed.Should().BeTrue();
    }
}
