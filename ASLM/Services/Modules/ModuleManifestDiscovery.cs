// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Modules
{
    /// <summary>
    /// Locates installed module manifests under the <c>Modules</c> directory.
    /// </summary>
    public static class ModuleManifestDiscovery
    {
        // Manifest lookup

        /// <summary>
        /// The module manifest file name.
        /// </summary>
        public const string ManifestFileName = "ASLM_Module.json";

        /// <summary>
        /// Enumerates <c>Modules/{moduleFolder}/ASLM_Module.json</c> files.
        /// Manifests in nested subfolders are not returned. A missing or blank root yields nothing.
        /// </summary>
        public static IEnumerable<string> EnumerateInstalledManifests(string modulesRoot)
        {
            if (string.IsNullOrWhiteSpace(modulesRoot) || !Directory.Exists(modulesRoot))
            {
                yield break;
            }

            var normalizedRoot = Path.GetFullPath(modulesRoot);

            // Each first-level folder is one module; it counts only when it holds a manifest file.
            foreach (var moduleDir in Directory.EnumerateDirectories(normalizedRoot))
            {
                var manifestPath = Path.Combine(moduleDir, ManifestFileName);
                if (File.Exists(manifestPath))
                {
                    yield return manifestPath;
                }
            }
        }


        // Path checks

        /// <summary>
        /// Returns whether <paramref name="manifestPath"/> is the root manifest for one installed module:
        /// <c>Modules/{moduleFolder}/ASLM_Module.json</c>.
        /// </summary>
        public static bool IsInstalledModuleManifest(string modulesRoot, string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(modulesRoot) || string.IsNullOrWhiteSpace(manifestPath))
            {
                return false;
            }

            if (!IsPathUnderDirectory(modulesRoot, manifestPath))
            {
                return false;
            }

            // The file name must be the manifest name and the file must have a parent directory.
            var normalizedRoot = Path.GetFullPath(modulesRoot);
            var normalizedManifest = Path.GetFullPath(manifestPath);

            if (!string.Equals(Path.GetFileName(normalizedManifest), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var manifestDirectory = Path.GetDirectoryName(normalizedManifest);
            if (string.IsNullOrEmpty(manifestDirectory))
            {
                return false;
            }

            var moduleFolderRelative = Path.GetRelativePath(normalizedRoot, manifestDirectory);

            // Accept only manifests that live directly inside one first-level module folder.
            if (string.IsNullOrEmpty(moduleFolderRelative)
                || moduleFolderRelative is "." or ".."
                || moduleFolderRelative.Contains(Path.DirectorySeparatorChar)
                || moduleFolderRelative.Contains(Path.AltDirectorySeparatorChar)
                || Path.IsPathRooted(moduleFolderRelative)
                || moduleFolderRelative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Returns whether <paramref name="path"/> is located inside <paramref name="directory"/>.
        /// </summary>
        public static bool IsPathUnderDirectory(string directory, string path)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // The trailing separator keeps a sibling such as "Modules2" from matching "Modules".
            var normalizedDirectory = Path.GetFullPath(directory);
            if (!normalizedDirectory.EndsWith(Path.DirectorySeparatorChar))
            {
                normalizedDirectory += Path.DirectorySeparatorChar;
            }

            var normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }
    }
}
