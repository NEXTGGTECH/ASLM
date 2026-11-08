// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Maui.Graphics;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies hex color parsing, custom palette construction and the swatch stroke color of
    /// <see cref="ThemePaletteResolver"/>.
    /// </summary>
    public sealed class ThemePaletteResolverTests
    {
        // Hex colors

        /// <summary>
        /// Verifies that six-digit and three-digit hex strings parse into a red color, while an empty string does not
        /// parse.
        /// </summary>
        /// <param name="hex">Color text to parse.</param>
        /// <param name="shouldSucceed">Whether the text is expected to parse.</param>
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

        /// <summary>
        /// Verifies that a parsed six-digit color is formatted back to the same hex digits.
        /// </summary>
        [Fact]
        public void ToHex_round_trips_six_digit_colors()
        {
            ThemePaletteResolver.TryParseHex("#0A84FF", out var color).Should().BeTrue();
            ThemePaletteResolver.ToHex(color).Should().EndWith("0A84FF");
        }


        // Palette construction

        /// <summary>
        /// Verifies that color keys outside the built-in palette are removed while known keys are kept.
        /// </summary>
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

        /// <summary>
        /// Verifies that a custom theme's color overrides replace the matching colors of its base palette while the
        /// remaining base colors are kept.
        /// </summary>
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

        /// <summary>
        /// Verifies that prefilling an empty custom theme fills its colors from the built-in palette of its base
        /// appearance.
        /// </summary>
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

        /// <summary>
        /// Verifies that the list of all palette keys has the same keys as the built-in dark palette.
        /// </summary>
        [Fact]
        public void AllKeys_matches_built_in_palette_keys()
        {
            ThemePaletteResolver.AllKeys.Should().BeEquivalentTo(ThemePaletteResolver.BuildDarkPalette().Keys);
        }


        // Swatch rendering

        /// <summary>
        /// Verifies that a black swatch gets the mid-gray ring and a white swatch gets a (translucent) white ring.
        /// </summary>
        [Fact]
        public void SwatchContrastStroke_returns_high_contrast_stroke()
        {
            ThemePaletteResolver.TryParseHex("#000000", out var dark).Should().BeTrue();
            ThemePaletteResolver.TryParseHex("#FFFFFF", out var light).Should().BeTrue();

            ThemePaletteResolver.ToHex(ThemePaletteResolver.SwatchContrastStroke(dark)).Should().EndWith("9A9A9E");
            ThemePaletteResolver.ToHex(ThemePaletteResolver.SwatchContrastStroke(light)).Should().EndWith("FFFFFF");
        }
    }
}
