// Copyright NEXTGGTECH. Apache License 2.0.

using Foundation;

namespace ASLM
{
    /// <summary>
    /// Seeds the per-user ASLM data root from the templates bundled under <c>Resources/seed</c>.
    /// </summary>
    /// <remarks>
    /// On Windows the installer payload ships Data/, Engines/, Models/, and Modules/ next to the app.
    /// On macOS the .app bundle is the only artifact, so the first run copies the same template files
    /// into <see cref="AppRoot.Directory"/>. Existing files are never overwritten - engine and module
    /// state persisted by the user survives app updates.
    /// </remarks>
    public static class MacAppDataSeeder
    {
        private const string SeedFolderName = "seed";

        private static readonly string[] RootFolders = ["Data", "Engines", "Models", "Modules"];

        /// <summary>
        /// Creates the data root and copies missing template files from the app bundle.
        /// </summary>
        public static void EnsureSeeded()
        {
            var rootDir = AppRoot.Directory;

            foreach (var folder in RootFolders)
            {
                Directory.CreateDirectory(Path.Combine(rootDir, folder));
            }

            var resourcePath = NSBundle.MainBundle.ResourcePath;
            if (string.IsNullOrEmpty(resourcePath))
            {
                return;
            }

            var seedDir = Path.Combine(resourcePath, SeedFolderName);
            if (!Directory.Exists(seedDir))
            {
                return;
            }

            foreach (var sourceFile in Directory.EnumerateFiles(seedDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(seedDir, sourceFile);
                var targetFile = Path.Combine(rootDir, relativePath);

                if (File.Exists(targetFile))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                File.Copy(sourceFile, targetFile);
            }
        }
    }
}
