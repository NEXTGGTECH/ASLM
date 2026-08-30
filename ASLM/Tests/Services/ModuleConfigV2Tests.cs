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
    public void Missing_fileVersion_is_legacy_and_platform_agnostic()
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
