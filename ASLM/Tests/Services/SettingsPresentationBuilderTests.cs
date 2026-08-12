// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies deterministic module section construction before XAML rendering.
/// </summary>
public sealed class SettingsPresentationBuilderTests
{
    /// <summary>
    /// Verifies uncategorized settings lead declared categories while preserving manifest order.
    /// </summary>
    [Fact]
    public void BuildModuleSections_places_one_default_group_before_manifest_categories()
    {
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "general", Name = "General" },
                new ModuleSettingCategory { Id = "advanced", Name = "Advanced" }
            ];
            config.Settings =
            [
                CreateSetting("runtime_path", "path", "ignored"),
                CreateSetting("general-value", "string", "general"),
                CreateSetting("advanced-value", "string", "advanced"),
                CreateSetting("unknown-value", "string", "missing"),
                CreateSetting("plain-value", "string", null),
                CreateSetting("host-locale", "locale", null)
            ];
            config.Normalize();
        });

        var sections = SettingsPresentationBuilder.BuildModuleSections(new ModuleSettingsDraft(module));

        sections.Select(static section => section.Kind).Should().Equal(
            ModuleSettingsSectionKind.Uncategorized,
            ModuleSettingsSectionKind.ManifestCategory,
            ModuleSettingsSectionKind.ManifestCategory);
        sections[0].Title.Should().BeNull();
        sections[0].Settings.Select(static draft => draft.Setting.Key).Should().Equal(
            "runtime_path",
            "unknown-value",
            "plain-value");
        sections[1].Title.Should().Be("General");
        sections[1].Settings.Select(static draft => draft.Setting.Key).Should().Equal("general-value");
        sections[2].Title.Should().Be("Advanced");
        sections[2].Settings.Select(static draft => draft.Setting.Key).Should().Equal("advanced-value");
    }

    /// <summary>
    /// Verifies dependency visibility is resolved before sections reach the renderer.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void BuildModuleSections_applies_detached_dependency_state(bool enabled, bool expectedChild)
    {
        var module = ModuleConfigBuilder.Create(configure: config =>
        {
            config.SettingCategories =
            [
                new ModuleSettingCategory { Id = "feature", Name = "Feature" }
            ];
            config.Settings =
            [
                CreateSetting("feature-enabled", "bool", "feature", false),
                CreateSetting("feature-value", "string", "feature", "value", "feature-enabled")
            ];
            config.Normalize();
        });
        var moduleDraft = new ModuleSettingsDraft(module);
        moduleDraft.GetSetting("feature-enabled").Value = enabled;

        var sections = SettingsPresentationBuilder.BuildModuleSections(moduleDraft);
        var renderedKeys = sections
            .SelectMany(static section => section.Settings)
            .Select(static draft => draft.Setting.Key)
            .ToList();

        renderedKeys.Should().Contain("feature-enabled");
        renderedKeys.Contains("feature-value").Should().Be(expectedChild);
        module.Settings.Single(static setting => setting.Key == "feature-enabled").Value.Should().Be(false);
    }

    /// <summary>
    /// Creates one setting definition with optional category, value, and explicit dependency metadata.
    /// </summary>
    private static ModuleSetting CreateSetting(
        string key,
        string type,
        string? category,
        object? value = null,
        string? dependsOn = null) =>
        new()
        {
            Key = key,
            Name = key,
            Type = type,
            Category = category,
            Default = value,
            Value = value,
            DependsOn = dependsOn
        };
}
