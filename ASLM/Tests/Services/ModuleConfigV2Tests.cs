// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services;

public sealed class ModuleConfigV2Tests
{
    /// <summary>
    /// Verifies the stable top-level order used when ASLM persists module manifests.
    /// </summary>
    [Fact]
    public void Serialization_preserves_classic_manifest_property_order()
    {
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

        var json = System.Text.Json.JsonSerializer.Serialize(config);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();

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

    [Fact]
    public void Legacy_module_platforms_are_the_intersection_of_all_required_engines()
    {
        var config = ModuleManifestParser.Parse("""
            { "id": "legacy", "dependencies": { "engines": [{"id":"first"}, {"id":"second"}] } }
            """);
        var first = PlatformEngine("first", "windows-amd64", "macos-arm64");
        var second = PlatformEngine("second", "windows-x64", "linux-amd64");
        EngineConfig? FindEngine(string id) => id == "first" ? first : second;

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

    [Fact]
    public void Providing_an_unused_engine_does_not_make_it_a_required_dependency()
    {
        var config = new ModuleConfig { Engines = [PlatformEngine("optional", "windows-amd64")] };
        config.ResolveForPlatform("macos", "arm64", _ => throw new InvalidOperationException());
        config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        config.EffectiveSupportedPlatforms.Should().BeEmpty();
    }

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

    /// <summary>
    /// Verifies downloads bridge declarations survive version-aware manifest parsing.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Downloads_bridge_is_preserved_for_supported_manifest_versions(int fileVersion)
    {
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

        var config = ModuleManifestParser.Parse(json);

        config.DownloadsBridge.Should().NotBeNull();
        config.DownloadsBridge!.IsConfigured.Should().BeTrue();
        config.DownloadsBridge.Operations.Should().ContainSingle().Which.Should().Be("list_categories");
        config.DownloadsBridge.Categories.Should().ContainSingle().Which.Id.Should().Be("models");
    }

    [Fact]
    public void V2_manifest_resolves_platform_categories_dependencies_and_engines()
    {
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

        config.IsSupportedOnCurrentPlatform.Should().BeTrue();
        config.SettingCategories.Should().ContainSingle().Which.Id.Should().Be("network");
        config.Settings.Single(setting => setting.Key == "url").DependsOn.Should().Be("enabled");
        config.Engines.Should().ContainSingle().Which.Id.Should().Be("vendor-runtime");
    }

    [Fact]
    public void V2_requires_supported_platforms()
    {
        var act = () => ModuleManifestParser.Parse(
            """
            { "fileVersion": 2, "id": "invalid" }
            """);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Unknown_version_is_rejected()
    {
        var act = () => ModuleManifestParser.Parse(
            """
            { "fileVersion": 99, "id": "future" }
            """);

        act.Should().Throw<NotSupportedException>();
    }

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
