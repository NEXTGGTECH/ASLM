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
    /// - The <c>ASLM_ROOT</c> environment variable overrides both (development and tests).
    /// </remarks>
    public static class AppRoot
    {
        private const string EnvironmentOverrideVariable = "ASLM_ROOT";
        private const string AppName = "ASLM";

        private static readonly Lazy<string> _directory = new(Resolve);

        /// <summary>
        /// Gets the absolute ASLM data root directory for the current platform.
        /// </summary>
        public static string Directory => _directory.Value;

        /// <summary>
        /// Resolves the data root once per process.
        /// </summary>
        private static string Resolve()
        {
            var overridePath = Environment.GetEnvironmentVariable(EnvironmentOverrideVariable);
            if (!string.IsNullOrWhiteSpace(overridePath) && Path.IsPathRooted(overridePath))
            {
                return Path.GetFullPath(overridePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
            {
                // SpecialFolder.LocalApplicationData maps to ~/Documents on Mac Catalyst,
                // so the Application Support path is built from the home directory instead.
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", AppName);
            }

            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            return System.IO.Directory.GetParent(appDir)?.FullName ?? appDir;
        }
    }
}
