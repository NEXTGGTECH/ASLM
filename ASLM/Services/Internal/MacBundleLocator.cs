// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Locates the .app bundle that hosts the running executable on macOS.
    /// </summary>
    public static class MacBundleLocator
    {
        /// <summary>
        /// Returns the bundle root directory (the path ending in .app), or null when
        /// not running on macOS or not running from inside a bundle.
        /// </summary>
        public static string? FindBundleRoot()
        {
            if (!OperatingSystem.IsMacCatalyst() && !OperatingSystem.IsMacOS())
            {
                return null;
            }

            var directory = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            while (directory != null)
            {
                if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
