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
        // Fields and constants

        /// <summary>
        /// Name of the bundle resource folder that holds the template files.
        /// </summary>
        private const string SeedFolderName = "seed";

        /// <summary>
        /// Top-level data folders that always exist in the data root, even when the bundle has no template for them.
        /// </summary>
        private static readonly string[] RootFolders = ["Data", "Engines", "Models", "Modules"];


        // Seeding

        /// <summary>
        /// Creates the data root and copies missing template files from the app bundle.
        /// </summary>
        public static void EnsureSeeded()
        {
            var rootDir = AppRoot.Directory;

            // Create the standard folder layout so later code can rely on it.
            foreach (var folder in RootFolders)
            {
                Directory.CreateDirectory(Path.Combine(rootDir, folder));
            }

            // Locate the bundled templates; a bundle without a seed folder has nothing to copy.
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

            // Mirror the seed tree into the data root, skipping files that already exist so user state survives updates.
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
