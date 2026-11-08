// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using ASLM.Models;

namespace ASLM.Tests.Models
{
    /// <summary>
    /// Verifies how <see cref="EngineConfig"/> reads engine manifests and resolves per-platform install blocks:
    /// supported platform lists, v1 to v2 upgrade, legacy update metadata, and v2 serialization.
    /// </summary>
    public sealed class EngineConfigPlatformTests
    {
        // Helpers

        /// <summary>
        /// Deserializes an engine manifest with default serializer options; the type's own JSON converter does the
        /// version-aware reading.
        /// </summary>
        private static EngineConfig Deserialize(string json) =>
            JsonSerializer.Deserialize<EngineConfig>(json)!;


        // Platform resolution

        /// <summary>
        /// Verifies that only declared platforms with an install block are listed as supported, that block keys
        /// are canonicalized, and that listing them does not change the platform the engine resolved earlier.
        /// </summary>
        [Fact]
        public void Supported_platforms_require_an_install_block_and_do_not_mutate_the_active_engine()
        {
            var engine = Deserialize("""
                {
                  "fileVersion":2, "id":"custom-keys",
                  "supportedPlatforms":[
                    {"os":"win","arch":"x64","key":"windows-amd64"},
                    {"os":"macos","arch":"arm64","key":"macos-runtime"},
                    {"os":"linux","arch":"amd64","key":"missing"}
                  ],
                  "windows-amd64":{"executablePath":"tool.exe"},
                  "macos-runtime":{"executablePath":"tool"}
                }
                """);
            engine.ResolveForPlatform("windows", "amd64");
            engine.GetSupportedPlatforms().Select(platform => platform.Key)
                .Should().Equal("windows-amd64", "macos-arm64");
            engine.ActivePlatformKey.Should().Be("windows-amd64");
            engine.ExecutablePath.Should().Be("tool.exe");
            engine.IsSupportedOnCurrentPlatform.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that an install block for a platform that the explicit <c>supportedPlatforms</c> list does not
        /// name never makes the engine supported there and is not listed as supported.
        /// </summary>
        [Fact]
        public void An_install_block_cannot_bypass_an_explicit_supported_platform_list()
        {
            var engine = Deserialize("""
                {
                  "fileVersion":2, "id":"restricted",
                  "supportedPlatforms":[{"os":"windows","arch":"amd64"}],
                  "windows-amd64":{"executablePath":"tool.exe"},
                  "macos-arm64":{"executablePath":"tool"}
                }
                """);
            engine.ResolveForPlatform("macos", "arm64");
            engine.IsSupportedOnCurrentPlatform.Should().BeFalse();
            engine.GetSupportedPlatforms().Should().ContainSingle().Which.Key.Should().Be("windows-amd64");
        }

        /// <summary>
        /// Verifies that a v2 manifest exposes the executable path and install steps of the block that matches the
        /// resolved platform, and that resolving again for another architecture switches to that block.
        /// </summary>
        [Fact]
        public void V2_manifest_resolves_per_platform_blocks()
        {
            // Arrange: two platform blocks for the same OS with different executables and install URLs.
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

            // Act and assert, amd64: the amd64 block supplies the executable and install steps.
            config.ResolveForPlatform("windows", "amd64");
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.ExecutablePath.Should().Be("runtime/x64.exe");
            config.Install.Single().Url.Should().Be("amd64-url");

            // Act and assert, arm64: resolving again switches to the arm64 block.
            config.ResolveForPlatform("windows", "arm64");
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.ExecutablePath.Should().Be("runtime/arm64.exe");
            config.Install.Single().Url.Should().Be("arm64-url");
        }

        /// <summary>
        /// Verifies that resolving a platform missing from the supported list marks the engine unsupported and
        /// leaves no active platform, executable path, or install steps.
        /// </summary>
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


        // Legacy manifests

        /// <summary>
        /// Verifies that a v1 manifest with flat executable, requirements, and install fields is read as a single
        /// <c>windows-amd64</c> platform block with those values and keeps its disk space requirement.
        /// </summary>
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

        /// <summary>
        /// Verifies that a legacy per-platform <c>assetName</c> dictionary in the update block collapses to the
        /// asset name of the resolved platform.
        /// </summary>
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


        // Serialization

        /// <summary>
        /// Verifies that serializing a v1 manifest writes file version 2 with a supported platform list, and that
        /// reading the result back keeps the executable path and install status.
        /// </summary>
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
}
