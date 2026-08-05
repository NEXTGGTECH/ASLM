// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using Microsoft.Extensions.Logging;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Applies a color palette to <see cref="Application.Current"/> resources so that all
    /// <c>DynamicResource</c> bindings update immediately when the active theme changes.
    /// </summary>
    public sealed class ThemeService
    {
        private readonly AppDataStore _appData;
        private readonly CustomThemesStore _customThemesStore;
        private readonly ILogger<ThemeService> _logger;

        // Initialization

        /// <summary>
        /// Creates the theme service.
        /// </summary>
        public ThemeService(
            AppDataStore appData,
            CustomThemesStore customThemesStore,
            ILogger<ThemeService> logger)
        {
            _appData = appData;
            _customThemesStore = customThemesStore;
            _logger = logger;
        }


        // Public API

        /// <summary>
        /// Applies the theme specified in persisted app data to the running application.
        /// Must be called on the main thread.
        /// </summary>
        public void ApplyFromSettings()
        {
            try
            {
                ApplyPersonalization(_appData.Data.Personalization, customDraft: null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply theme from settings.");
            }
        }

        /// <summary>
        /// Applies appearance from the supplied config (for example settings UI drafts), optionally
        /// using an in-memory <paramref name="customDraft"/> instead of the persisted custom theme.
        /// Must be called on the main thread.
        /// </summary>
        public void ApplyPersonalization(AppPersonalizationConfig config, CustomTheme? customDraft)
        {
            try
            {
                var appearance = AppPersonalizationConfig.NormalizeAppearance(config.Appearance);

                if (string.Equals(appearance, "System", StringComparison.Ordinal))
                {
                    ApplySystemTheme();
                    return;
                }

                if (string.Equals(appearance, "Custom", StringComparison.Ordinal))
                {
                    CustomTheme? theme = null;
                    if (customDraft != null &&
                        !string.IsNullOrWhiteSpace(config.CustomThemeId) &&
                        string.Equals(customDraft.Id, config.CustomThemeId, StringComparison.OrdinalIgnoreCase))
                    {
                        theme = customDraft;
                    }
                    else
                    {
                        theme = _customThemesStore.FindById(config.CustomThemeId);
                    }

                    if (theme != null)
                    {
                        ApplyCustomTheme(theme);
                    }
                    else
                    {
                        ApplyBuiltInTheme(isDark: true);
                    }

                    return;
                }

                if (string.Equals(appearance, "Light", StringComparison.Ordinal))
                {
                    ApplyBuiltInTheme(isDark: false);
                }
                else
                {
                    ApplyBuiltInTheme(isDark: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply personalization draft.");
            }
        }

        /// <summary>
        /// Previews a custom theme draft without persisting the changes.
        /// Must be called on the main thread.
        /// </summary>
        public void PreviewCustomTheme(CustomTheme draft)
        {
            try
            {
                ApplyCustomTheme(draft);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to preview custom theme.");
            }
        }

        /// <summary>
        /// Resolves the effective dark/light flag from the current system preference.
        /// </summary>
        public static bool IsSystemDark()
        {
            var theme = Application.Current?.RequestedTheme;
            return theme != AppTheme.Light;
        }


        // Theme application

        /// <summary>
        /// Applies the built-in palette and listens for OS theme changes.
        /// </summary>
        private void ApplySystemTheme()
        {
            if (Application.Current is { } app)
            {
                app.UserAppTheme = AppTheme.Unspecified;
                app.RequestedThemeChanged -= OnSystemThemeChanged;
                app.RequestedThemeChanged += OnSystemThemeChanged;
            }

            ApplyBuiltInTheme(isDark: IsSystemDark());
        }

        /// <summary>
        /// Unsubscribes from OS theme change notifications.
        /// </summary>
        private void StopTrackingSystemTheme()
        {
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnSystemThemeChanged;
            }
        }

        /// <summary>
        /// Reapplies the built-in palette when the OS appearance changes.
        /// </summary>
        private void OnSystemThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            var isDark = e.RequestedTheme != AppTheme.Light;
            MainThread.BeginInvokeOnMainThread(() => ApplyBuiltInTheme(isDark));
        }

        /// <summary>
        /// Applies one built-in dark or light palette to application resources.
        /// </summary>
        private void ApplyBuiltInTheme(bool isDark)
        {
            StopTrackingSystemTheme();
            if (Application.Current is { } app)
            {
                app.UserAppTheme = isDark ? AppTheme.Dark : AppTheme.Light;
            }

            var palette = isDark
                ? ThemePaletteResolver.BuildDarkPalette()
                : ThemePaletteResolver.BuildLightPalette();

            WritePaletteToResources(palette);
        }

        /// <summary>
        /// Applies one custom theme palette to application resources.
        /// </summary>
        private void ApplyCustomTheme(CustomTheme theme)
        {
            StopTrackingSystemTheme();
            var isDark = string.Equals(theme.BaseAppearance, "dark", StringComparison.OrdinalIgnoreCase);
            if (Application.Current is { } app)
            {
                app.UserAppTheme = isDark ? AppTheme.Dark : AppTheme.Light;
            }

            var palette = ThemePaletteResolver.BuildCustomPalette(theme);
            WritePaletteToResources(palette);
        }


        // Palette notifications

        /// <summary>
        /// Raised on the main thread after the application palette dictionary is rewritten.
        /// Icon tint behaviors read concrete <see cref="Color"/> values and must be refreshed explicitly.
        /// </summary>
        public static event Action? PaletteApplied;


        // Resource writing

        /// <summary>
        /// Overwrites every Color and SolidColorBrush key in Application.Resources
        /// that is present in the supplied palette dictionary.
        /// </summary>
        private static void WritePaletteToResources(Dictionary<string, Color> palette)
        {
            var app = Application.Current;
            if (app == null)
            {
                return;
            }

            var resources = app.Resources;

            foreach (var (key, color) in palette)
            {
                resources[key] = color;

                var brushKey = key + "Brush";
                if (resources.ContainsKey(brushKey))
                {
                    resources[brushKey] = new SolidColorBrush(color);
                }
            }

            PackagedIconTintCache.Clear();
            PaletteApplied?.Invoke();
        }
    }
}
