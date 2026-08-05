// Copyright NEXTGGTECH. Apache License 2.0.

using System.IO.Compression;
using System.Text.Json;

/// <summary>
/// Bootstraps the per-user ASLM application directory for a user who does not yet have one.
/// </summary>
/// <remarks>
/// When a second (or any subsequent) user launches the shared Launcher, their per-user
/// application directory does not exist yet. This class extracts the payload archive that
/// was placed next to the Launcher during installation and sets up the user's own copy of
/// the application. The Launcher executable itself is intentionally excluded because it
/// already lives in the shared installation directory.
/// </remarks>
internal static class UserBootstrapper
{
    private const string LauncherExeName = "ASLM.exe";
    private const string LauncherRefFileName = "launcher-ref.json";

    /// <summary>
    /// Returns true when the per-user application directory is already initialized.
    /// </summary>
    public static bool IsUserAppDirReady(string userAppDir)
        => File.Exists(Path.Combine(userAppDir, "App", LauncherExeName));

    /// <summary>
    /// Extracts the payload archive from the shared installation directory into the
    /// per-user application directory, skipping the root-level Launcher executable.
    /// </summary>
    /// <param name="sharedInstallDir">Directory that contains the Launcher and aslm-base.zip.</param>
    /// <param name="userAppDir">Per-user target directory (created if absent).</param>
    /// <param name="logPath">Path to the Launcher log file for progress messages.</param>
    /// <returns>True when bootstrapping completed successfully.</returns>
    public static bool TryBootstrap(string sharedInstallDir, string userAppDir, string logPath)
    {
        var archivePath = AppPaths.GetPayloadArchivePath(sharedInstallDir);
        if (!File.Exists(archivePath))
        {
            Log($"Bootstrap skipped: payload archive not found at {archivePath}", logPath);
            return false;
        }

        Log($"Bootstrapping user application directory: {userAppDir}", logPath);

        try
        {
            Directory.CreateDirectory(userAppDir);
            ExtractPayload(archivePath, userAppDir, logPath);
            Log("Bootstrap extraction complete.", logPath);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Bootstrap failed: {ex.Message}", logPath);

            // Remove a potentially partial extraction so the next launch retries cleanly.
            TryDeleteDirectory(userAppDir);
            return false;
        }
    }

    /// <summary>
    /// Writes (or updates) the launcher reference file inside the per-user application directory
    /// so the Patcher can restart the correct Launcher after a self-update.
    /// </summary>
    /// <param name="launcherExePath">Absolute path to the Launcher executable.</param>
    /// <param name="userAppDir">Per-user application directory that receives the file.</param>
    public static void WriteLauncherRef(string launcherExePath, string userAppDir)
    {
        try
        {
            Directory.CreateDirectory(userAppDir);
            var refPath = Path.Combine(userAppDir, LauncherRefFileName);
            var json = JsonSerializer.Serialize(
                new { launcherPath = launcherExePath },
                new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(refPath, json);
        }
        catch
        {
            // Non-fatal: Patcher falls back to the --launcher argument or the root heuristic.
        }
    }


    // Extraction helpers

    /// <summary>
    /// Extracts archive entries into the target directory, skipping the root-level Launcher executable.
    /// </summary>
    private static void ExtractPayload(string archivePath, string targetDir, string logPath)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            // Skip directory-only entries (entries with no file name).
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // Skip the root-level Launcher executable — it lives in the shared install dir.
            if (IsRootLauncherEntry(entry.FullName))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!IsChildPath(targetDir, destinationPath))
            {
                Log($"Skipping unsafe archive entry: {entry.FullName}", logPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    /// <summary>
    /// Returns true when the archive entry represents the root-level Launcher executable
    /// (i.e. ASLM.exe at the top of the zip, not inside App\ or any other subdirectory).
    /// </summary>
    private static bool IsRootLauncherEntry(string entryFullName)
    {
        // Normalize separators so the check works regardless of how the zip was created.
        var normalized = entryFullName.Replace('\\', '/').Trim('/');

        // The entry must be exactly the launcher name with no directory component.
        return string.Equals(normalized, LauncherExeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="childPath"/> is located under <paramref name="parentPath"/>.
    /// </summary>
    private static bool IsChildPath(string parentPath, string childPath)
    {
        var normalizedParent = Path.GetFullPath(parentPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedChild = Path.GetFullPath(childPath).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes a directory tree on a best-effort basis.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Cleanup is best-effort only.
        }
    }

    /// <summary>
    /// Appends a timestamped message to the log file.
    /// </summary>
    private static void Log(string message, string logPath)
    {
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}
