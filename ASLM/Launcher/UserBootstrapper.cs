// Copyright NEXTGGTECH. Apache License 2.0.

using System.Formats.Tar;
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
    private static string LauncherExeName => AppPaths.ExecutableName;
    private const string LauncherRefFileName = "launcher-ref.json";

    /// <summary>
    /// Returns true when the per-user application directory is already initialized.
    /// </summary>
    public static bool IsUserAppDirReady(string userAppDir)
    {
        if (!File.Exists(Path.Combine(userAppDir, "App", LauncherExeName))) return false;
        if (!OperatingSystem.IsLinux()) return true;

        // An interrupted bootstrap must not be mistaken for a usable self-contained installation.
        return File.Exists(Path.Combine(userAppDir, "App", "libcoreclr.so")) &&
               File.Exists(Path.Combine(userAppDir, "App", "libhostfxr.so")) &&
               File.Exists(Path.Combine(userAppDir, "App", "libhostpolicy.so")) &&
               File.Exists(Path.Combine(userAppDir, "Patcher", "aslm-patcher"));
    }

    /// <summary>
    /// Extracts the payload archive from the shared installation directory into the
    /// per-user application directory, skipping the root-level Launcher executable.
    /// </summary>
    /// <param name="sharedInstallDir">Directory that contains the Launcher and the platform's base archive.</param>
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
            if (OperatingSystem.IsLinux()) ExtractUnixPayload(archivePath, userAppDir);
            else ExtractPayload(archivePath, userAppDir, logPath);
            Log("Bootstrap extraction complete.", logPath);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Bootstrap failed: {ex.Message}", logPath);

            // Linux profiles may already contain installed modules and models; never remove them on repair failure.
            if (!OperatingSystem.IsLinux()) TryDeleteDirectory(userAppDir);
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
    /// Restores only program directories from the system tarball without overwriting the user's data directories.
    /// </summary>
    private static void ExtractUnixPayload(string archivePath, string userAppDir)
    {
        // Stage on the destination filesystem so the final directory renames cannot fail across mount points.
        var staging = Path.Combine(userAppDir, ".aslm-update", "bootstrap", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "payload");
        var replaced = new List<string>();
        var retainBackup = false;
        Directory.CreateDirectory(payload);
        try
        {
            using (var archive = File.OpenRead(archivePath))
            using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
                TarFile.ExtractToDirectory(gzip, payload, overwriteFiles: false);

            // The base archive carries application files and templates, not profile data or a second launcher.
            var folders = new[] { "App", "Patcher" };
            if (Directory.EnumerateFileSystemEntries(payload).Any(entry => !folders.Contains(Path.GetFileName(entry))) ||
                !IsUserAppDirReady(payload) ||
                folders.Any(folder => new DirectoryInfo(Path.Combine(payload, folder)).LinkTarget != null))
                throw new InvalidOperationException("The ASLM base archive is incomplete or has an unexpected layout.");

            foreach (var folder in folders)
            {
                var destination = Path.Combine(userAppDir, folder);
                var backup = Path.Combine(staging, "previous-" + folder);
                if (new DirectoryInfo(destination).LinkTarget != null)
                    throw new IOException($"Cannot restore ASLM over a directory link: {destination}");

                if (Directory.Exists(destination)) Directory.Move(destination, backup);
                // Record the old directory before attempting the second rename so this step is also rolled back on failure.
                replaced.Add(folder);
                Directory.Move(Path.Combine(payload, folder), destination);
            }
        }
        catch
        {
            // Roll back only App/ and Patcher/. Settings, engines, models and modules are never part of this transaction.
            foreach (var folder in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = Path.Combine(userAppDir, folder);
                    if (Directory.Exists(destination)) Directory.Move(destination, Path.Combine(staging, "failed-" + folder));
                    var backup = Path.Combine(staging, "previous-" + folder);
                    if (Directory.Exists(backup)) Directory.Move(backup, destination);
                }
                catch
                {
                    // Keep recoverable application files if a filesystem failure also prevents rollback.
                    retainBackup = true;
                }
            }
            throw;
        }
        finally
        {
            if (!retainBackup) TryDeleteDirectory(staging);
        }
    }

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
