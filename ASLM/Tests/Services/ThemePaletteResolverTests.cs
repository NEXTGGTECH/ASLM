// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Maui.Graphics;

namespace ASLM.Tests.Services;

public sealed class ThemePaletteResolverTests
{
    [Theory]
    [InlineData("#FF0000", true)]
    [InlineData("#F00", true)]
    [InlineData("", false)]
    public void TryParseHex_parses_hex_strings(string hex, bool shouldSucceed)
    {
        var success = ThemePaletteResolver.TryParseHex(hex, out var color);
        success.Should().Be(shouldSucceed);
        if (shouldSucceed)
        {
            color.Red.Should().BeGreaterThan(0.9f);
        }
    }

    [Fact]
    public void ToHex_round_trips_six_digit_colors()
    {
        ThemePaletteResolver.TryParseHex("#0A84FF", out var color).Should().BeTrue();
        ThemePaletteResolver.ToHex(color).Should().EndWith("0A84FF");
    }

    [Fact]
    public void RemoveUnknownColorKeys_drops_stale_entries()
    {
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemBlue"] = "#0A84FF",
            ["NotARealPaletteKey"] = "#FFFFFF"
        };

        ThemePaletteResolver.RemoveUnknownColorKeys(colors);

        colors.Should().ContainKey("SystemBlue");
        colors.Should().NotContainKey("NotARealPaletteKey");
    }

    [Fact]
    public void BuildCustomPalette_applies_overrides_on_top_of_base()
    {
        var theme = new CustomTheme
        {
            Id = "custom-1",
            Name = "Custom",
            BaseAppearance = "dark",
            Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SystemBlue"] = "#123456"
            }
        };
        theme.Normalize();

        var palette = ThemePaletteResolver.BuildCustomPalette(theme);
        ThemePaletteResolver.ToHex(palette["SystemBlue"]).Should().EndWith("123456");
        palette.Should().ContainKey("BackgroundPrimary");
    }

    [Fact]
    public void PrefillCustomThemeFromBuiltIn_populates_missing_keys()
    {
        var theme = new CustomTheme
        {
            Id = "custom-2",
            Name = "Partial",
            BaseAppearance = "light",
            Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        theme.Normalize();

        ThemePaletteResolver.PrefillCustomThemeFromBuiltIn(theme);

        theme.Colors.Should().NotBeEmpty();
        theme.Colors.Should().ContainKey("SystemBlue");
    }

    [Fact]
    public void AllKeys_matches_built_in_palette_keys()
    {
        ThemePaletteResolver.AllKeys.Should().BeEquivalentTo(ThemePaletteResolver.BuildDarkPalette().Keys);
    }

    [Fact]
    public void SwatchContrastStroke_returns_high_contrast_stroke()
    {
        ThemePaletteResolver.TryParseHex("#000000", out var dark).Should().BeTrue();
        ThemePaletteResolver.TryParseHex("#FFFFFF", out var light).Should().BeTrue();

        ThemePaletteResolver.ToHex(ThemePaletteResolver.SwatchContrastStroke(dark)).Should().EndWith("9A9A9E");
        ThemePaletteResolver.ToHex(ThemePaletteResolver.SwatchContrastStroke(light)).Should().EndWith("FFFFFF");
    }
}
