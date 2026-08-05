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
        private readonly AppDataStore _appData;
        private readonly CustomThemesStore _customThemes;
        private readonly ILogger<ModuleThemePayloadBuilder> _logger;

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
        /// </summary>
        public string BuildJson()
        {
            try
            {
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
                    effectiveLightDark = "dark";
                    palette = ThemePaletteResolver.BuildDarkPalette();
                }

                var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, color) in palette)
                {
                    colors[key] = ThemePaletteResolver.ToHex(color);
                }

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
                return true;
            }
        }


        // Serialization DTO

        /// <summary>
        /// JSON shape delivered to modules through the theme setting integration.
        /// </summary>
        private sealed class ModuleHostThemePayloadDto
        {
            public string Appearance { get; set; } = "Dark";

            public string Theme { get; set; } = "dark";

            public string? CustomThemeId { get; set; }

            public string? CustomThemeName { get; set; }

            public Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
