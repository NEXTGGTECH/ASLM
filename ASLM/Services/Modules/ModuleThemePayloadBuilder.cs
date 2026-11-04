// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json;
using System.Text.Json.Serialization;
using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Builds a JSON snapshot of the host ASLM theme for modules that declare a <c>theme</c> setting.
    /// The snapshot is delivered through the standard <c>setExec</c> integration (typically via a temp file path).
    /// </summary>
    public sealed class ModuleThemePayloadBuilder
    {
        // Fields

        /// <summary>
        /// Application data store that supplies the user's appearance and custom theme selection.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Store of user-defined themes, used to resolve the selected custom theme.
        /// </summary>
        private readonly CustomThemesStore _customThemes;

        /// <summary>
        /// Logger for payload build failures.
        /// </summary>
        private readonly ILogger<ModuleThemePayloadBuilder> _logger;

        /// <summary>
        /// Compact camelCase JSON settings; null values are omitted.
        /// </summary>
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };


        // Initialization

        /// <summary>
        /// Creates the payload builder.
        /// </summary>
        public ModuleThemePayloadBuilder(
            AppDataStore appData,
            CustomThemesStore customThemes,
            ILogger<ModuleThemePayloadBuilder> logger)
        {
            _appData = appData;
            _customThemes = customThemes;
            _logger = logger;
        }


        // Payload build

        /// <summary>
        /// Serializes the active host theme (appearance + resolved palette) to a single-line JSON string.
        /// Falls back to a dark payload with an empty palette when the theme cannot be resolved or serialized.
        /// </summary>
        public string BuildJson()
        {
            try
            {
                // Read the stored appearance; unknown values are normalized to System.
                var personalization = _appData.Data.Personalization;
                var appearance = AppPersonalizationConfig.NormalizeAppearance(personalization.Appearance);
                string effectiveLightDark;
                string? customId = null;
                string? customName = null;

                Dictionary<string, Microsoft.Maui.Graphics.Color> palette;

                // Resolve the effective palette from system, custom, or built-in appearance.
                if (string.Equals(appearance, "System", StringComparison.Ordinal))
                {
                    var isDark = IsHostSystemDark();
                    effectiveLightDark = isDark ? "dark" : "light";
                    palette = isDark
                        ? ThemePaletteResolver.BuildDarkPalette()
                        : ThemePaletteResolver.BuildLightPalette();
                }
                else if (string.Equals(appearance, "Custom", StringComparison.Ordinal))
                {
                    // The selected custom theme decides light or dark through its base appearance;
                    // a missing or unknown custom theme falls back to the built-in dark palette.
                    var id = string.IsNullOrWhiteSpace(personalization.CustomThemeId)
                        ? null
                        : personalization.CustomThemeId.Trim();
                    var theme = id != null ? _customThemes.FindById(id) : null;
                    if (theme != null)
                    {
                        customId = id;
                        customName = theme.Name;
                        var isDark = string.Equals(theme.BaseAppearance, "dark", StringComparison.OrdinalIgnoreCase);
                        effectiveLightDark = isDark ? "dark" : "light";
                        palette = ThemePaletteResolver.BuildCustomPalette(theme);
                    }
                    else
                    {
                        effectiveLightDark = "dark";
                        palette = ThemePaletteResolver.BuildDarkPalette();
                    }
                }
                else if (string.Equals(appearance, "Light", StringComparison.Ordinal))
                {
                    effectiveLightDark = "light";
                    palette = ThemePaletteResolver.BuildLightPalette();
                }
                else
                {
                    // Dark appearance.
                    effectiveLightDark = "dark";
                    palette = ThemePaletteResolver.BuildDarkPalette();
                }

                // Convert the resolved palette to hex strings that modules can use directly.
                var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, color) in palette)
                {
                    colors[key] = ThemePaletteResolver.ToHex(color);
                }

                // Assemble the payload: the selected appearance, the effective light or dark mode and the colors.
                var dto = new ModuleHostThemePayloadDto
                {
                    Appearance = appearance,
                    Theme = effectiveLightDark,
                    CustomThemeId = customId,
                    CustomThemeName = customName,
                    Colors = colors
                };

                return JsonSerializer.Serialize(dto, JsonOptions);
            }
            catch (Exception ex)
            {
                // A module must always receive a valid theme payload, so a failure degrades to the dark theme.
                _logger.LogError(ex, "Failed to build module host theme payload.");
                return "{\"theme\":\"dark\",\"appearance\":\"Dark\",\"colors\":{}}";
            }
        }

        /// <summary>
        /// Resolves whether the host is currently in an effective dark mode when appearance is System.
        /// Mirrors <see cref="ThemeService.IsSystemDark"/>; falls back to dark when the MAUI app context is unavailable.
        /// </summary>
        private static bool IsHostSystemDark()
        {
            try
            {
                return ThemeService.IsSystemDark();
            }
            catch
            {
                // Without a MAUI app context the system theme is unknown; dark is the default.
                return true;
            }
        }


        // Serialization DTO

        /// <summary>
        /// JSON shape delivered to modules through the theme setting integration.
        /// </summary>
        private sealed class ModuleHostThemePayloadDto
        {
            /// <summary>
            /// Appearance selected by the user: <c>System</c>, <c>Light</c>, <c>Dark</c> or <c>Custom</c>.
            /// </summary>
            public string Appearance { get; set; } = "Dark";

            /// <summary>
            /// Effective mode after resolving the appearance: <c>light</c> or <c>dark</c>.
            /// </summary>
            public string Theme { get; set; } = "dark";

            /// <summary>
            /// Id of the active custom theme, or null when no custom theme applies.
            /// </summary>
            public string? CustomThemeId { get; set; }

            /// <summary>
            /// Display name of the active custom theme, or null when no custom theme applies.
            /// </summary>
            public string? CustomThemeName { get; set; }

            /// <summary>
            /// Palette keys mapped to <c>#AARRGGBB</c> hex color strings.
            /// </summary>
            public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
