// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services;

/// <summary>
/// Captures module-setting contracts that the declarative settings rewrite must preserve.
/// </summary>
public sealed class SettingsContractCharacterizationTests
{
    /// <summary>
    /// Verifies that supported scalar types keep their established CLR representation.
    /// </summary>
    [Theory]
    [InlineData("bool", "true", true)]
    [InlineData("int", "42", 42)]
    [InlineData("long", "9223372036854775806", 9223372036854775806L)]
    [InlineData("number", "1.5", 1.5d)]
    [InlineData("string", "value", "value")]
    public void ParseSerializedValue_preserves_supported_scalar_contract(
        string type,
        string serialized,
        object expected)
    {
        var setting = new ModuleSetting { Type = type };

        var result = setting.ParseSerializedValue(serialized);

        result.Should().Be(expected);
    }

    /// <summary>
    /// Verifies invariant formatting used by command arguments and dirty-state comparisons.
    /// </summary>
    [Theory]
    [InlineData("bool", true, "true")]
    [InlineData("int", 42, "42")]
    [InlineData("number", 1.5d, "1.5")]
    [InlineData("string", "value", "value")]
    public void FormatValueForDisplay_preserves_command_contract(
        string type,
        object value,
        string expected)
    {
        var setting = new ModuleSetting { Type = type };

        var result = setting.FormatValueForDisplay(value);

        result.Should().Be(expected);
    }

    /// <summary>
    /// Verifies compatibility with the legacy key-prefix visibility convention.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldRenderSetting_preserves_legacy_prefixed_dependency(bool enabled, bool expected)
    {
        var controller = new ModuleSetting { Key = "provider", Type = "bool" };
        var child = new ModuleSetting { Key = "provider_url", Type = "string" };
        var settings = new[] { controller, child };

        var result = SettingsService.ShouldRenderSetting(
            child,
            settings,
            new Dictionary<string, object?> { [controller.Key] = enabled, [child.Key] = "http://localhost" });

        result.Should().Be(expected);
    }

    /// <summary>
    /// Verifies fail-open rendering when explicit dependency metadata references an unknown setting.
    /// </summary>
    [Fact]
    public void ShouldRenderSetting_keeps_invalid_explicit_dependency_visible()
    {
        var child = new ModuleSetting { Key = "url", Type = "string", DependsOn = "missing" };

        var result = SettingsService.ShouldRenderSetting(
            child,
            new[] { child },
            new Dictionary<string, object?> { [child.Key] = "http://localhost" });

        result.Should().BeTrue();
    }

    /// <summary>
    /// Verifies that host-managed types remain outside categories and dependency metadata.
    /// </summary>
    [Theory]
    [InlineData("engine")]
    [InlineData("path")]
    [InlineData("data")]
    [InlineData("models")]
    [InlineData("key-aslm")]
    [InlineData("key-gh")]
    public void Host_managed_types_remain_ineligible_for_settings_metadata(string type)
    {
        var setting = new ModuleSetting
        {
            Key = $"runtime-{type}",
            Type = type,
            Category = "advanced",
            DependsOn = "enabled"
        };

        SettingsService.IsSettingsMetadataEligible(setting).Should().BeFalse();
    }

    /// <summary>
    /// Verifies that host-only port, theme, and locale values never enter the editor surface.
    /// </summary>
    [Theory]
    [InlineData("port")]
    [InlineData("theme")]
    [InlineData("locale")]
    [InlineData("key-aslm")]
    [InlineData("key-gh")]
    public void Host_only_types_remain_hidden_from_settings_editor(string type)
    {
        SettingsService.ShouldDisplaySetting(new ModuleSetting { Type = type }).Should().BeFalse();
    }
}
