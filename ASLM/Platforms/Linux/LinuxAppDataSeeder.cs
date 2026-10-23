// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text.Json.Nodes;

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
                if (File.Exists(target))
                {
                    // Existing preview profiles predate the Linux update asset; retain their settings and source.
                    if (source == Path.Combine(seedDirectory, "Data", "App", "ASLM_UpdateSource.json"))
                        MergeUpdateAssets(source, target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target, overwrite: false); }
                catch (IOException) when (File.Exists(target))
                {
                    // Another application instance may have seeded this file after our existence check.
                }
            }
        }
    }

    /// <summary>
    /// Adds newly shipped update assets only when a profile still uses the same repository and has no mapping of its own.
    /// </summary>
    private static void MergeUpdateAssets(string source, string target)
    {
        try
        {
            var shipped = JsonNode.Parse(File.ReadAllText(source)) as JsonObject;
            var current = JsonNode.Parse(File.ReadAllText(target)) as JsonObject;
            if (shipped?["assets"] is not JsonObject assets || current == null ||
                !string.Equals(shipped["source"]?["repo"]?.GetValue<string>(), current["source"]?["repo"]?.GetValue<string>(),
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(shipped["source"]?["type"]?.GetValue<string>(), current["source"]?["type"]?.GetValue<string>(),
                    StringComparison.OrdinalIgnoreCase)) return;

            // Merge missing keys, not the whole document: custom assets, channels, preserve rules and unknown fields survive.
            var mappings = current["assets"] as JsonObject ?? new JsonObject();
            var changed = false;
            foreach (var asset in assets)
                if (!mappings.ContainsKey(asset.Key))
                {
                    mappings[asset.Key] = asset.Value?.DeepClone();
                    changed = true;
                }
            if (!changed) return;
            current["assets"] = mappings;
            File.WriteAllText(target, current.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // A malformed or read-only custom source must not prevent the application from starting.
            Console.Error.WriteLine($"[ASLM] Could not refresh update asset mappings: {ex.Message}");
        }
    }
}
