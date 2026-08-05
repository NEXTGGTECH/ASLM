// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Maui.Graphics;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Builds the full resolved color palette for a given theme mode.
    /// <para>
    /// Resolution order for custom themes:
    /// 1. Full base palette (Dark or Light according to <see cref="CustomTheme.BaseAppearance"/>).
    /// 2. Any colors explicitly defined in the custom theme's <c>Colors</c> dictionary
    ///    override the base values — absent keys fall back to the base palette.
    /// </para>
    /// </summary>
    public static class ThemePaletteResolver
    {
        private static readonly Lazy<HashSet<string>> KnownColorKeys = new(
            () => new HashSet<string>(BuildDarkPalette().Keys, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Drops palette entries that are not part of the canonical key set (stale keys from older builds).
        /// </summary>
        public static void RemoveUnknownColorKeys(IDictionary<string, string> colors)
        {
            var known = KnownColorKeys.Value;
            foreach (var key in colors.Keys.ToList())
            {
                if (!known.Contains(key))
                {
                    colors.Remove(key);
                }
            }
        }

        // Dark palette

        /// <summary>
        /// Returns the complete dark-mode palette.
        /// Key names mirror the resource keys defined in <c>Resources/Styles/Colors.xaml</c>.
        /// </summary>
        public static Dictionary<string, Color> BuildDarkPalette() => new(StringComparer.OrdinalIgnoreCase)
        {
            // System palette
            ["SystemBlue"]   = C("#0A84FF"),
            ["SystemGreen"]  = C("#32D74B"),
            ["SystemIndigo"] = C("#5E5CE6"),
            ["SystemOrange"] = C("#FF9F0A"),
            ["SystemPink"]   = C("#FF375F"),
            ["SystemPurple"] = C("#BF5AF2"),
            ["SystemRed"]    = C("#FF453A"),
            ["SystemTeal"]   = C("#64D2FF"),
            ["SystemYellow"] = C("#FFD60A"),
            ["SystemMint"]   = C("#66D4CF"),

            ["SystemGray"]  = C("#8E8E93"),
            ["SystemGray2"] = C("#636366"),
            ["SystemGray3"] = C("#48484A"),
            ["SystemGray4"] = C("#3A3A3C"),
            ["SystemGray5"] = C("#2C2C2E"),
            ["SystemGray6"] = C("#1C1C1E"),

            // Backgrounds
            ["BackgroundPrimary"]   = C("#000000"),
            ["BackgroundSecondary"] = C("#1C1C1E"),
            ["BackgroundTertiary"]  = C("#2C2C2E"),

            // Labels
            ["LabelPrimary"]    = C("#FFFFFF"),
            ["LabelSecondary"]  = C("#99EBEBF5"),
            ["LabelTertiary"]   = C("#4DEBEBF5"),
            ["LabelQuaternary"] = C("#2EEBEBF5"),

            // Utility
            ["Separator"]         = C("#38383A"),
            ["PlaceholderText"]   = C("#4DEBEBF5"),
            ["LinkColor"]         = C("#0984FF"),
            ["SystemBlueOverlay"] = C("#200A84FF"),

            ["White"] = C("#FFFFFF"),
            ["Black"] = C("#000000"),

            // Action colors
            ["ActionRed"]   = C("#FF453A"),
            ["ActionBlue"]  = C("#0A84FF"),
            ["ActionGreen"] = C("#32D74B"),

            ["OverlayBackground"] = C("#40000000"),

            ["BackgroundErrorOverlay"] = C("#4C1F24"),
        };


        // Light palette

        /// <summary>
        /// Returns the complete light-mode palette.
        /// </summary>
        public static Dictionary<string, Color> BuildLightPalette() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemBlue"]   = C("#007AFF"),
            ["SystemGreen"]  = C("#34C759"),
            ["SystemIndigo"] = C("#5856D6"),
            ["SystemOrange"] = C("#FF9500"),
            ["SystemPink"]   = C("#FF2D55"),
            ["SystemPurple"] = C("#AF52DE"),
            ["SystemRed"]    = C("#FF3B30"),
            ["SystemTeal"]   = C("#5AC8FA"),
            ["SystemYellow"] = C("#FFCC00"),
            ["SystemMint"]   = C("#00C7BE"),

            ["SystemGray"]  = C("#8E8E93"),
            ["SystemGray2"] = C("#AEAEB2"),
            ["SystemGray3"] = C("#C7C7CC"),
            ["SystemGray4"] = C("#D1D1D6"),
            ["SystemGray5"] = C("#E5E5EA"),
            ["SystemGray6"] = C("#F2F2F7"),

            ["BackgroundPrimary"]   = C("#F2F2F7"),
            ["BackgroundSecondary"] = C("#FFFFFF"),
            ["BackgroundTertiary"]  = C("#E5E5EA"),

            ["LabelPrimary"]    = C("#000000"),
            ["LabelSecondary"]  = C("#636366"),
            ["LabelTertiary"]   = C("#8E8E93"),
            ["LabelQuaternary"] = C("#AEAEB2"),

            ["Separator"]         = C("#B0B0B6"),
            ["PlaceholderText"]   = C("#8E8E93"),
            ["LinkColor"]         = C("#007AFF"),
            ["SystemBlueOverlay"] = C("#20007AFF"),

            ["White"] = C("#FFFFFF"),
            ["Black"] = C("#000000"),

            ["ActionRed"]   = C("#FF3B30"),
            ["ActionBlue"]  = C("#007AFF"),
            ["ActionGreen"] = C("#34C759"),

            ["OverlayBackground"] = C("#59000000"),

            ["BackgroundErrorOverlay"] = C("#FFEBEC"),
        };


        // Custom palette

        /// <summary>
        /// Fills <paramref name="theme"/>.Colors with every palette key from the chosen built-in base.
        /// </summary>
        public static void PrefillCustomThemeFromBuiltIn(CustomTheme theme)
        {
            theme.Normalize();
            var isDark = string.Equals(theme.BaseAppearance, "dark", StringComparison.OrdinalIgnoreCase);
            var palette = isDark ? BuildDarkPalette() : BuildLightPalette();

            theme.Colors.Clear();
            foreach (var (key, color) in palette)
            {
                theme.Colors[key] = ToHex(color);
            }
        }

        /// <summary>
        /// Builds the resolved palette for a custom theme. Missing or invalid JSON keys fall back to
        /// the built-in palette for <see cref="CustomTheme.BaseAppearance"/>, then explicit theme overrides.
        /// </summary>
        public static Dictionary<string, Color> BuildCustomPalette(CustomTheme theme)
        {
            var palette = string.Equals(theme.BaseAppearance, "light", StringComparison.OrdinalIgnoreCase)
                ? BuildLightPalette()
                : BuildDarkPalette();

            foreach (var (key, hex) in theme.Colors)
            {
                if (string.IsNullOrWhiteSpace(key) || !CustomTheme.IsValidHex(hex))
                {
                    continue;
                }

                if (TryParseHex(hex, out var color) && palette.ContainsKey(key))
                {
                    palette[key] = color;
                }
            }

            return palette;
        }


        // All known palette keys

        /// <summary>
        /// Returns the ordered list of every palette key known to the theme system.
        /// Used by the custom theme editor to display all editable slots.
        /// </summary>
        public static IReadOnlyList<string> AllKeys { get; } = BuildDarkPalette().Keys.ToList().AsReadOnly();

        /// <summary>
        /// Stroke for small color swatches: subtle ring that stays visible on both light and dark fills.
        /// </summary>
        public static Color SwatchContrastStroke(Color fill)
        {
            static double SrgbChannel(double c) =>
                c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

            var r = SrgbChannel(fill.Red);
            var g = SrgbChannel(fill.Green);
            var b = SrgbChannel(fill.Blue);
            var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            return luminance > 0.52
                ? Color.FromArgb("#D9FFFFFF")
                : Color.FromArgb("#FF9A9A9E");
        }


        // Helpers

        /// <summary>
        /// Parses a 6- or 8-digit hex string to a MAUI Color.
        /// </summary>
        public static bool TryParseHex(string hex, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(hex))
            {
                return false;
            }

            try
            {
                color = Color.FromArgb(hex.Trim());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Converts a MAUI Color to a canonical 8-digit hex string (#AARRGGBB).
        /// </summary>
        public static string ToHex(Color color) =>
            $"#{(int)(color.Alpha * 255):X2}{(int)(color.Red * 255):X2}{(int)(color.Green * 255):X2}{(int)(color.Blue * 255):X2}";

        /// <summary>
        /// Parses one palette hex literal into a MAUI color.
        /// </summary>
        private static Color C(string hex) => Color.FromArgb(hex);
    }
}
