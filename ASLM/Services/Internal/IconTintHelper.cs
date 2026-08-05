// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Controls;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Resolves MAUI palette colors used when tinting packaged module icons.
    /// </summary>
    internal static class IconTintHelper
    {
        // Palette lookup

        /// <summary>
        /// Returns the <see cref="Color"/> stored under <paramref name="resourceKey"/>, or white when the key is missing.
        /// </summary>
        internal static Color ResolvePaletteColor(string resourceKey)
        {
            if (Application.Current?.Resources.TryGetValue(resourceKey, out var v) == true && v is Color c)
            {
                return c;
            }

            return Colors.White;
        }
    }
}
