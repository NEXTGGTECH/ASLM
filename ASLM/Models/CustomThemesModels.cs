// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Serialization;

namespace ASLM.Models
{
    // Custom theme models

    /// <summary>
    /// Root container for the custom themes persisted in <c>Data/App/ASLM_CustomThemes.json</c>.
    /// </summary>
    public class CustomThemesRoot
    {
        // Ordered list of user-defined themes.
        [JsonPropertyName("themes")]
        public List<CustomTheme> Themes { get; set; } = [];

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Themes ??= [];
            foreach (var theme in Themes)
            {
                theme.Normalize();
            }

            // Remove entries that cannot be identified or named.
            Themes.RemoveAll(t => string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name));
        }
    }

    /// <summary>
    /// Describes one named custom theme with a partial or complete color palette override.
    /// </summary>
    public class CustomTheme
    {
        // Unique identifier assigned when the theme is created.
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        // Display name shown in the personalization picker.
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        // Base palette used for keys absent from Colors; one of: "dark", "light".
        [JsonPropertyName("baseAppearance")]
        public string BaseAppearance { get; set; } = "dark";

        // Sparse map of palette key → hex color string (e.g. "#0A84FF" or "#FF0A84FF").
        [JsonPropertyName("colors")]
        public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Restores safe defaults after JSON deserialization.
        /// </summary>
        public void Normalize()
        {
            Id = (Id ?? string.Empty).Trim();
            Name = (Name ?? string.Empty).Trim();
            BaseAppearance = NormalizeBaseAppearance(BaseAppearance);
            Colors ??= new(StringComparer.OrdinalIgnoreCase);

            // Remove entries with blank keys or invalid hex values.
            var badKeys = Colors.Keys
                .Where(k => string.IsNullOrWhiteSpace(k) || !IsValidHex(Colors[k]))
                .ToList();
            foreach (var key in badKeys)
            {
                Colors.Remove(key);
            }

            ThemePaletteResolver.RemoveUnknownColorKeys(Colors);
        }

        /// <summary>
        /// Canonical base appearance string; defaults to "dark" for unknown values.
        /// </summary>
        public static string NormalizeBaseAppearance(string? value) =>
            string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";

        /// <summary>
        /// Returns true when the string is a well-formed 6- or 8-digit hex color prefixed with #.
        /// </summary>
        public static bool IsValidHex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var s = value.Trim();
            if (s.Length != 7 && s.Length != 9)
            {
                return false;
            }

            if (s[0] != '#')
            {
                return false;
            }

            return s[1..].All(c => Uri.IsHexDigit(c));
        }
    }
}
