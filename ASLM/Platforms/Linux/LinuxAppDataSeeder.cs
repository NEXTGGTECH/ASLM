// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Services.Internal;

/// <summary>
/// Initializes a writable data root from packaged templates without replacing user-owned files.
/// </summary>
internal static class LinuxAppDataSeeder
{
    private static readonly string[] RootFolders = ["Data", "Engines", "Models", "Modules"];

    // Data location

    /// <summary>
    /// Resolves the XDG data location without accepting relative paths or changing the working directory.
    /// </summary>
    internal static string ResolveDataDirectory(string? dataHome, string userProfile)
    {
        // The XDG specification requires absolute paths; an invalid override uses the standard fallback.
        var root = !string.IsNullOrWhiteSpace(dataHome) && Path.IsPathFullyQualified(dataHome)
            ? dataHome : Path.Combine(userProfile, ".local", "share");
        return Path.GetFullPath(Path.Combine(root, "ASLM"));
    }

    // Template seeding

    /// <summary>
    /// Copies missing template files and leaves persisted settings and installed content untouched.
    /// </summary>
    internal static void EnsureSeeded(string seedDirectory, string dataDirectory)
    {
        foreach (var folder in RootFolders)
        {
            var targetDirectory = Path.Combine(dataDirectory, folder);
            Directory.CreateDirectory(targetDirectory);
            var sourceDirectory = Path.Combine(seedDirectory, folder);
            if (!Directory.Exists(sourceDirectory)) continue;

            // Templates come only from the packaged seed tree; do not enumerate a developer's data root.
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, source));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target, overwrite: false); }
                catch (IOException) when (File.Exists(target))
                {
                    // Another application instance may have seeded this file after our existence check.
                }
            }
        }
    }
}
