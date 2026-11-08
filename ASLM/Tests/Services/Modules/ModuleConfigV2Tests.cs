// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies parsing, validation, serialization and platform resolution of module manifests (file versions 1
    /// and 2).
    /// </summary>
    public sealed class ModuleConfigV2Tests
    {
        // Development flags

        /// <summary>
        /// Verifies that the beta and experimental flags are read independently from the manifest, are written back
        /// only when set (the experimental flag under the manifest key <c>is_experemental</c>) and are restored by
        /// parsing the written JSON again.
        /// </summary>
        [Theory]
        [InlineData(1, false, false)]
        [InlineData(1, true, false)]
        [InlineData(1, false, true)]
        [InlineData(1, true, true)]
        [InlineData(2, false, false)]
        [InlineData(2, true, false)]
        [InlineData(2, false, true)]
        [InlineData(2, true, true)]
        public void Development_flags_are_independent_and_survive_manifest_round_trip(int version, bool beta, bool experimental)
        {
            var config = ModuleManifestParser.Parse($$"""
                {
                  "fileVersion": {{version}},
                  "id": "development-flags",
                  "supportedPlatforms": [{"os": "windows", "arch": "amd64"}],
                  "is_beta": {{(beta ? "true" : "false")}},
                  "is_experemental": {{(experimental ? "true" : "false")}}
                }
                """);

            // Parsing exposes both flags as declared.
            config.IsBeta.Should().Be(beta);
            config.IsExperimental.Should().Be(experimental);

            // Serializing keeps a flag in the JSON only when it is set, and never under the correctly spelled name.
            var json = System.Text.Json.JsonSerializer.Serialize(config);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            document.RootElement.TryGetProperty("is_beta", out _).Should().Be(beta);
            document.RootElement.TryGetProperty("is_experemental", out _).Should().Be(experimental);
            document.RootElement.TryGetProperty("is_experimental", out _).Should().BeFalse();

            // The written JSON round-trips to the same flags.
            var reloaded = ModuleManifestParser.Parse(json);
            reloaded.IsBeta.Should().Be(beta);
            reloaded.IsExperimental.Should().Be(experimental);
        }

        /// <summary>
        /// Verifies that a manifest without the beta and experimental flags reports both as false.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Omitted_development_flags_default_to_false(int version)
        {
            var config = ModuleManifestParser.Parse($$"""
                {"fileVersion": {{version}}, "id": "stable",
                 "supportedPlatforms": [{"os": "windows", "arch": "amd64"}]}
                """);
            config.IsBeta.Should().BeFalse();
            config.IsExperimental.Should().BeFalse();
        }

        /// <summary>
        /// Verifies that a development flag given as a string, a number or null is rejected with a
        /// <see cref="System.Text.Json.JsonException"/>.
        /// </summary>
        [Theory]
        [InlineData("is_beta", "\"true\"")]
        [InlineData("is_beta", "1")]
        [InlineData("is_beta", "null")]
        [InlineData("is_experemental", "\"true\"")]
        [InlineData("is_experemental", "1")]
        [InlineData("is_experemental", "null")]
        public void Development_flags_require_json_booleans(string key, string value)
        {
            var parse = () => ModuleManifestParser.Parse($$"""{"id":"invalid", "{{key}}":{{value}}}""");
            parse.Should().Throw<System.Text.Json.JsonException>();
        }


        // Serialization

        /// <summary>
        /// Verifies the stable top-level order used when ASLM persists module manifests.
        /// </summary>
        [Fact]
        public void Serialization_preserves_classic_manifest_property_order()
        {
            // Arrange: a version 2 module with only a few properties set.
            var config = new ModuleConfig
            {
                FileVersion = 2,
                Id = "ordered",
                Name = "Ordered",
                HasPage = true,
                Icon = "icon.png",
                SidebarIcon = "sidebar.png",
                SupportedPlatforms =
                [
                    new SupportedPlatform
                    {
                        Os = "windows",
                        Arch = "amd64",
                        Key = "windows-amd64"
                    }
                ]
            };

            // Act: serialize and list the top-level property names in their written order.
            var json = System.Text.Json.JsonSerializer.Serialize(config);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

            // Assert: the names appear in the classic manifest order.
            properties.Should().ContainInOrder(
                "fileVersion",
                "id",
                "name",
                "description",
                "version",
                "author",
                "type",
                "category",
                "hasPage",
                "icon",
                "sidebarIcon",
                "source",
                "supportedPlatforms",
                "engines",
                "update",
                "dependencies",
                "commands",
                "settingCategories",
                "settings",
                "downloadsBridge",
                "moduleInterop",
                "status");
        }


        // Platform resolution

        /// <summary>
        /// Verifies that a manifest without platform declarations and engine dependencies defaults to file version 1
        /// and is supported on the current platform.
        /// </summary>
        [Fact]
        public void Missing_platforms_and_engine_dependencies_leave_the_module_unrestricted()
        {
            var config = ModuleManifestParser.Parse(
                """
                {
                  "id": "legacy",
                  "name": "Legacy"
                }
                """);

            config.FileVersion.Should().Be(1);
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        }

        /// <summary>
        /// Verifies that a module without its own platform list is limited to the platforms that all required
        /// engines share, and that this calculated result is not written back into the manifest.
        /// </summary>
        [Fact]
        public void Legacy_module_platforms_are_the_intersection_of_all_required_engines()
        {
            // Arrange: a module that requires two engines whose platform lists overlap only on windows-amd64.
            var config = ModuleManifestParser.Parse("""
                { "id": "legacy", "dependencies": { "engines": [{"id":"first"}, {"id":"second"}] } }
                """);
            var first = PlatformEngine("first", "windows-amd64", "macos-arm64");
            var second = PlatformEngine("second", "windows-x64", "linux-amd64");

            // Local function: resolves a required engine by id.
            EngineConfig? FindEngine(string id) => id == "first" ? first : second;

            // The shared platform is supported; any other host is not, and the declared list stays empty.
            config.ResolveForPlatform("windows", "amd64", FindEngine);
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.EffectiveSupportedPlatforms.Should().ContainSingle().Which.Key.Should().Be("windows-amd64");
            config.ResolveForPlatform("macos", "arm64", FindEngine);
            config.IsSupportedOnCurrentPlatform.Should().BeFalse();
            config.SupportedPlatforms.Should().BeEmpty();

            // Calculated restrictions must never overwrite a module's own declaration on save.
            var saved = System.Text.Json.JsonSerializer.Serialize(config);
            saved.Should().NotContain(nameof(ModuleConfig.EffectiveSupportedPlatforms));
            ModuleManifestParser.Parse(saved).SupportedPlatforms.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that a platform list declared by the module (written with the aliases <c>osx</c> and
        /// <c>aarch64</c>) is intersected with the engine platforms, so a host that only the engine supports is
        /// unsupported, and that the declared list keeps its original spelling, for both file versions.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void A_declared_module_platform_list_limits_the_engine_intersection_regardless_of_version(int version)
        {
            var config = ModuleManifestParser.Parse($$"""
                {
                  "fileVersion": {{version}}, "id": "restricted",
                  "supportedPlatforms": [{"os":"osx", "arch":"aarch64"}],
                  "dependencies": { "engines": [{"id":"engine"}] }
                }
                """);
            var engine = PlatformEngine("engine", "windows-amd64", "macos-arm64");

            config.ResolveForPlatform("windows", "amd64", _ => engine);
            config.IsSupportedOnCurrentPlatform.Should().BeFalse();
            config.EffectiveSupportedPlatforms.Should().ContainSingle().Which.Key.Should().Be("macos-arm64");
            config.ResolveForPlatform("macos", "arm64", _ => engine);
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.SupportedPlatforms.Single().Os.Should().Be("osx");
        }

        /// <summary>
        /// Verifies that a required engine with no platform metadata counts as Windows amd64 only, so a macOS arm64
        /// host is unsupported.
        /// </summary>
        [Fact]
        public void A_legacy_engine_without_platform_metadata_limits_the_module_to_windows_amd64()
        {
            var config = ModuleManifestParser.Parse("""
                {"id":"legacy", "dependencies":{"engines":[{"id":"legacy-engine"}]}}
                """);
            var engine = System.Text.Json.JsonSerializer.Deserialize<EngineConfig>("""
                {"fileVersion":1, "id":"legacy-engine", "executablePath":"runtime/tool.exe", "install":[]}
                """)!;
            config.ResolveForPlatform("macos", "arm64", _ => engine);
            config.IsSupportedOnCurrentPlatform.Should().BeFalse();
            config.EffectiveSupportedPlatforms.Should().ContainSingle().Which.Key.Should().Be("windows-amd64");
        }

        /// <summary>
        /// Verifies that a required engine that cannot be found, or that offers no matching platform, leaves the
        /// module without any supported platform.
        /// </summary>
        [Fact]
        public void Missing_or_incompatible_required_engine_leaves_no_supported_platforms()
        {
            var config = ModuleManifestParser.Parse("""
                {
                  "id":"module", "supportedPlatforms":[{"os":"windows", "arch":"amd64"}],
                  "dependencies":{"engines":[{"id":"required"}]}
                }
                """);
            config.ResolveForPlatform("windows", "amd64", _ => null);
            config.IsSupportedOnCurrentPlatform.Should().BeFalse();
            config.EffectiveSupportedPlatforms.Should().BeEmpty();

            config.ResolveForPlatform("windows", "amd64", _ => PlatformEngine("required", "macos-arm64"));
            config.IsSupportedOnCurrentPlatform.Should().BeFalse();
            config.EffectiveSupportedPlatforms.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that an engine embedded in the module but not listed as a dependency is never looked up and
        /// does not restrict the platforms.
        /// </summary>
        [Fact]
        public void Providing_an_unused_engine_does_not_make_it_a_required_dependency()
        {
            var config = new ModuleConfig { Engines = [PlatformEngine("optional", "windows-amd64")] };
            config.ResolveForPlatform("macos", "arm64", _ => throw new InvalidOperationException());
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.EffectiveSupportedPlatforms.Should().BeEmpty();
        }


        // Test helpers

        /// <summary>
        /// Creates a normalized engine that declares exactly the given platform keys.
        /// </summary>
        /// <param name="id">Engine id.</param>
        /// <param name="platforms">Platform keys such as <c>windows-amd64</c>.</param>
        private static EngineConfig PlatformEngine(string id, params string[] platforms)
        {
            var engine = new EngineConfig
            {
                Id = id,
                Platforms = platforms.ToDictionary(key => key, _ => new EnginePlatform())
            };
            engine.Normalize();
            return engine;
        }


        // Downloads bridge

        /// <summary>
        /// Verifies downloads bridge declarations survive version-aware manifest parsing.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void Downloads_bridge_is_preserved_for_supported_manifest_versions(int fileVersion)
        {
            // Arrange: a manifest with a downloads bridge; version 2 also needs a platform list.
            var supportedPlatforms = fileVersion == 2
                ? "\"supportedPlatforms\":[{\"os\":\"windows\",\"arch\":\"amd64\"}],"
                : string.Empty;
            var json = $$"""
                {
                  "fileVersion": {{fileVersion}},
                  "id": "bridge-module",
                  {{supportedPlatforms}}
                  "downloadsBridge": {
                    "protocolVersion": 1,
                    "engine": "python-runtime",
                    "entryPoint": "main.py downloads_bridge",
                    "operations": ["list_categories"],
                    "categories": [
                      { "id": "models", "title": "Models", "groupKey": "models" }
                    ]
                  }
                }
                """;

            // Act and assert: the parsed bridge is configured and keeps its operation and category.
            var config = ModuleManifestParser.Parse(json);

            config.DownloadsBridge.Should().NotBeNull();
            config.DownloadsBridge!.IsConfigured.Should().BeTrue();
            config.DownloadsBridge.Operations.Should().ContainSingle().Which.Should().Be("list_categories");
            config.DownloadsBridge.Categories.Should().ContainSingle().Which.Id.Should().Be("models");
        }


        // Manifest versions and validation

        /// <summary>
        /// Verifies that a version 2 manifest is supported on the current platform and exposes its setting
        /// categories, setting dependencies and embedded engines.
        /// </summary>
        [Fact]
        public void V2_manifest_resolves_platform_categories_dependencies_and_engines()
        {
            // Arrange and act: parse a manifest that declares a platform, setting categories, dependent settings and
            // an embedded engine.
            var config = ModuleManifestParser.Parse(
                """
                {
                  "fileVersion": 2,
                  "id": "demo",
                  "name": "Demo",
                  "supportedPlatforms": [
                    { "os": "windows", "arch": "x64" }
                  ],
                  "settingCategories": [
                    { "id": "network", "name": "Network" }
                  ],
                  "settings": [
                    { "key": "enabled", "type": "bool", "default": true, "category": "network" },
                    { "key": "url", "type": "string", "dependsOn": "enabled", "category": "network" }
                  ],
                  "engines": [
                    {
                      "fileVersion": 2,
                      "id": "vendor-runtime",
                      "supportedPlatforms": [
                        { "os": "windows", "arch": "amd64", "key": "windows-amd64" }
                      ],
                      "windows-amd64": { "executablePath": "runtime/vendor.exe", "install": [] }
                    }
                  ]
                }
                """);

            // Assert: the manifest content is available on the parsed module.
            config.IsSupportedOnCurrentPlatform.Should().BeTrue();
            config.SettingCategories.Should().ContainSingle().Which.Id.Should().Be("network");
            config.Settings.Single(setting => setting.Key == "url").DependsOn.Should().Be("enabled");
            config.Engines.Should().ContainSingle().Which.Id.Should().Be("vendor-runtime");
        }

        /// <summary>
        /// Verifies that a version 2 manifest without <c>supportedPlatforms</c> is rejected with an
        /// <see cref="InvalidDataException"/>.
        /// </summary>
        [Fact]
        public void V2_requires_supported_platforms()
        {
            var act = () => ModuleManifestParser.Parse(
                """
                { "fileVersion": 2, "id": "invalid" }
                """);

            act.Should().Throw<InvalidDataException>();
        }

        /// <summary>
        /// Verifies that a manifest with an unknown file version is rejected with a
        /// <see cref="NotSupportedException"/>.
        /// </summary>
        [Fact]
        public void Unknown_version_is_rejected()
        {
            var act = () => ModuleManifestParser.Parse(
                """
                { "fileVersion": 99, "id": "future" }
                """);

            act.Should().Throw<NotSupportedException>();
        }

        /// <summary>
        /// Verifies that a setting depending on a missing setting still parses and is reported as a validation
        /// warning.
        /// </summary>
        [Fact]
        public void Invalid_dependency_is_non_fatal_and_reported()
        {
            var config = ModuleManifestParser.Parse(
                """
                {
                  "fileVersion": 1,
                  "id": "warning",
                  "settings": [
                    { "key": "value", "type": "string", "dependsOn": "missing" }
                  ]
                }
                """);

            config.ValidationWarnings.Should().ContainSingle(message => message.Contains("missing"));
        }

        /// <summary>
        /// Verifies visible special settings receive the same metadata validation as standard settings.
        /// </summary>
        [Fact]
        public void Visible_special_setting_metadata_is_validated()
        {
            var config = ModuleManifestParser.Parse(
                """
                {
                  "fileVersion": 1,
                  "id": "special-metadata",
                  "settings": [
                    {
                      "key": "runtime-path",
                      "type": "path",
                      "category": "missing-category",
                      "dependsOn": "missing-setting"
                    }
                  ]
                }
                """);

            config.ValidationWarnings.Should().Contain(message => message.Contains("missing-category"));
            config.ValidationWarnings.Should().Contain(message => message.Contains("missing-setting"));
        }

        /// <summary>
        /// Verifies host account key settings stay outside user category and dependency validation.
        /// </summary>
        [Fact]
        public void Host_key_metadata_is_ignored_by_user_setting_validation()
        {
            var config = ModuleManifestParser.Parse(
                """
                {
                  "fileVersion": 1,
                  "id": "host-keys",
                  "settings": [
                    {
                      "key": "key-aslm",
                      "type": "key-aslm",
                      "category": "missing-category",
                      "dependsOn": "missing-setting"
                    },
                    {
                      "key": "key-gh",
                      "type": "key-gh"
                    }
                  ]
                }
                """);

            config.Settings.Should().OnlyContain(setting => setting.IsHostKey);
            config.ValidationWarnings.Should().BeEmpty();
        }
    }
}
