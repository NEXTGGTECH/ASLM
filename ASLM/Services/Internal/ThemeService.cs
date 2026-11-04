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
        // Fields

        /// <summary>
        /// Persisted application data that holds the selected appearance.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Store that resolves the user's saved custom themes by id.
        /// </summary>
        private readonly CustomThemesStore _customThemesStore;

        /// <summary>
        /// Logger for theme application failures, which never propagate to callers.
        /// </summary>
        private readonly ILogger<ThemeService> _logger;


        // Initialization

        /// <summary>
        /// Creates the theme service.
        /// </summary>
        /// <param name="appData">Application data that provides the persisted personalization settings.</param>
        /// <param name="customThemesStore">Store used to look up custom themes.</param>
        /// <param name="logger">Logger for failures while applying a theme.</param>
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

                // System appearance follows the OS preference and keeps tracking its changes.
                if (string.Equals(appearance, "System", StringComparison.Ordinal))
                {
                    ApplySystemTheme();
                    return;
                }

                // Custom appearance prefers the in-memory draft when it is the selected theme, otherwise the saved one.
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
                        // The selected custom theme no longer exists, so fall back to the dark built-in palette.
                        ApplyBuiltInTheme(isDark: true);
                    }

                    return;
                }

                // Remaining values pick a fixed built-in palette; anything other than Light is dark.
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
        /// Applies the built-in palette that matches the OS theme and re-applies it whenever the OS theme changes.
        /// </summary>
        private void ApplySystemTheme()
        {
            // Unsubscribe before subscribing so repeated calls never register the handler twice.
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
            // The event may arrive on a background thread, while resources must be written on the main thread.
            var isDark = e.RequestedTheme != AppTheme.Light;
            MainThread.BeginInvokeOnMainThread(() => ApplyBuiltInTheme(isDark));
        }

        /// <summary>
        /// Applies one built-in dark or light palette to application resources.
        /// </summary>
        private void ApplyBuiltInTheme(bool isDark)
        {
            // A fixed palette ends OS theme tracking; the app theme is forced to match it.
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
            // The theme's base appearance decides the app theme (platform controls); the palette supplies the colors.
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

            // Each color key may have a matching "<key>Brush" resource, which is rebuilt from the same color.
            foreach (var (key, color) in palette)
            {
                resources[key] = color;

                var brushKey = key + "Brush";
                if (resources.ContainsKey(brushKey))
                {
                    resources[brushKey] = new SolidColorBrush(color);
                }
            }

            // Cached tinted icons were rendered with the old colors, so drop them and tell subscribers to refresh.
            PackagedIconTintCache.Clear();
            PaletteApplied?.Invoke();
        }
    }
}
