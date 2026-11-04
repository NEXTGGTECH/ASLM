// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Controls;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Resolves MAUI palette colors used when tinting packaged icons.
    /// </summary>
    internal static class IconTintHelper
    {
        // Palette lookup

        /// <summary>
        /// Returns the <see cref="Color"/> stored under <paramref name="resourceKey"/>, or white when the key is missing.
        /// </summary>
        /// <param name="resourceKey">Key of a palette color in the application resource dictionary.</param>
        /// <returns>The palette color, or white when there is no application, no such key, or the value is not a color.</returns>
        internal static Color ResolvePaletteColor(string resourceKey)
        {
            // Palette entries live in the application resources and change with the active theme.
            if (Application.Current?.Resources.TryGetValue(resourceKey, out var v) == true && v is Color c)
            {
                return c;
            }

            return Colors.White;
        }
    }
}
