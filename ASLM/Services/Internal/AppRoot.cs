// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Resolves the ASLM data root directory that holds <c>Data/</c>, <c>Engines/</c>,
    /// <c>Models/</c>, and <c>Modules/</c>.
    /// </summary>
    /// <remarks>
    /// - Windows: the parent of the deployed <c>App/</c> folder (dual-location and monolithic layouts).
    /// - macOS: <c>~/Library/Application Support/ASLM</c>; the .app bundle stays read-only in /Applications.
    /// - Linux: <c>$XDG_DATA_HOME/ASLM</c>, falling back to <c>~/.local/share/ASLM</c>.
    /// - The <c>ASLM_ROOT</c> environment variable overrides platform defaults (development and tests).
    /// </remarks>
    public static class AppRoot
    {
        // Constants and cached state

        /// <summary>
        /// Name of the environment variable that overrides the platform default root.
        /// </summary>
        private const string EnvironmentOverrideVariable = "ASLM_ROOT";

        /// <summary>
        /// Name of the per-user application folder created below the macOS Application Support directory.
        /// </summary>
        private const string AppName = "ASLM";

        /// <summary>
        /// Lazily resolved root directory; replaced by <see cref="ResetForTests"/> to force a new resolution.
        /// </summary>
        private static Lazy<string> _directory = new(Resolve);


        // Root directory

        /// <summary>
        /// Gets the absolute ASLM data root directory for the current platform.
        /// </summary>
        public static string Directory => _directory.Value;

        /// <summary>
        /// Discards the cached root so the next <see cref="Directory"/> read honors a new
        /// <c>ASLM_ROOT</c> value. Test isolation only; production resolves once per process.
        /// </summary>
        internal static void ResetForTests() => _directory = new Lazy<string>(Resolve);

        /// <summary>
        /// Resolves the data root once per process.
        /// </summary>
        private static string Resolve()
        {
            // An absolute ASLM_ROOT wins over every platform default; relative values are ignored.
            var overridePath = Environment.GetEnvironmentVariable(EnvironmentOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathRooted(overridePath))
            {
                return Path.GetFullPath(overridePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            // macOS keeps its data in the user's Application Support folder because the app bundle is read-only.
            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                // SpecialFolder.LocalApplicationData maps to ~/Documents on Mac Catalyst,
                // so the Application Support path is built from the home directory instead.
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", AppName);
            }

#if LINUX
            // Linux follows the XDG base directory layout (see the remarks above).
            return LinuxAppDataSeeder.ResolveDataDirectory(Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
#else
            // Windows runs from the deployed App folder; the data root is its parent directory.
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            return System.IO.Directory.GetParent(appDir)?.FullName ?? appDir;
#endif
        }
    }
}
