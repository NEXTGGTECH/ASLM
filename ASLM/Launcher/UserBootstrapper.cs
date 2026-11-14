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
/// was placed next to the Launcher during installation or by a later self-update and sets up
/// the user's own copy of the application. The Launcher and the uninstaller are intentionally
/// excluded because they already live in the shared installation directory.
/// </remarks>
internal static class UserBootstrapper
{
    // Names

    /// <summary>
    /// File name of the launcher executable, which is never copied from the payload into a user directory.
    /// </summary>
    private static string LauncherExeName => AppPaths.ExecutableName;

    /// <summary>
    /// File name of the Windows uninstaller, which belongs in the shared installation directory like the launcher.
    /// </summary>
    private const string UninstallerExeName = "uninstall.exe";

    /// <summary>
    /// File name of the reference that tells the patcher where the shared launcher is.
    /// </summary>
    private const string LauncherRefFileName = "launcher-ref.json";


    // Bootstrapping

    /// <summary>
    /// Returns true when the per-user application directory is already initialized.
    /// </summary>
    public static bool IsUserAppDirReady(string userAppDir)
    {
        // The application executable is the minimum sign of an initialized directory.
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
        // Without the installer's payload archive there is nothing to copy from.
        var archivePath = AppPaths.GetPayloadArchivePath(sharedInstallDir);
        if (!File.Exists(archivePath))
        {
            Log($"Bootstrap skipped: payload archive not found at {archivePath}", logPath);
            return false;
        }

        Log($"Bootstrapping user application directory: {userAppDir}", logPath);

        try
        {
            // Linux restores program folders transactionally from a tarball; other platforms extract a zip.
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
            // Elsewhere the directory is created by this bootstrap, so the partial result is removed.
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
            // Store only the launcher path as compact JSON; the patcher reads it back when restarting.
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
            // Unpack the tarball into the staging area only; the live directories are untouched so far.
            using (var archive = File.OpenRead(archivePath))
            using (var gzip = new GZipStream(archive, CompressionMode.Decompress))
                TarFile.ExtractToDirectory(gzip, payload, overwriteFiles: false);

            // The base archive carries application files and templates, not profile data or a second launcher.
            var folders = new[] { "App", "Patcher" };
            if (Directory.EnumerateFileSystemEntries(payload).Any(entry => !folders.Contains(Path.GetFileName(entry))) ||
                !IsUserAppDirReady(payload) ||
                folders.Any(folder => new DirectoryInfo(Path.Combine(payload, folder)).LinkTarget != null))
                throw new InvalidOperationException("The ASLM base archive is incomplete or has an unexpected layout.");

            // Swap each program folder in: move the current one aside as a backup, then move the new one in.
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
                    // Set the half-installed folder aside and put the previous one back.
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
            // Drop the staging area unless it still holds the only copy of the previous installation.
            if (!retainBackup) TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Extracts archive entries into the target directory, skipping the root-level shared files. The installer's
    /// archive keeps its files at the root, while the release archive that self-updates place next to the Launcher
    /// wraps them in a single folder, which is collapsed.
    /// </summary>
    private static void ExtractPayload(string archivePath, string targetDir, string logPath)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var wrapperPrefix = GetWrapperPrefix(archive);

        foreach (var entry in archive.Entries)
        {
            // Skip directory-only entries (entries with no file name).
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            // Paths are relative to the payload root, which lies below the wrapper folder when there is one.
            var relativePath = entry.FullName.Replace('\\', '/').TrimStart('/')[wrapperPrefix.Length..];

            // Skip the root-level shared files — they live in the shared install dir.
            if (IsRootSharedEntry(relativePath))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(targetDir, relativePath));
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
    /// Returns the wrapper folder prefix, such as <c>ASLM-windows-amd64/</c>, when every entry lies below one
    /// top-level folder and no file sits at the root; otherwise returns an empty string.
    /// </summary>
    private static string GetWrapperPrefix(ZipArchive archive)
    {
        string? wrapperFolder = null;
        foreach (var entry in archive.Entries)
        {
            // A file at the root means the payload is not wrapped.
            var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
            var separator = normalized.IndexOf('/');
            if (separator < 0)
            {
                return string.Empty;
            }

            // A second top-level folder means the payload is not wrapped either.
            var topFolder = normalized[..separator];
            if (wrapperFolder is null)
            {
                wrapperFolder = topFolder;
            }
            else if (!string.Equals(wrapperFolder, topFolder, StringComparison.Ordinal))
            {
                return string.Empty;
            }
        }

        return wrapperFolder is null ? string.Empty : wrapperFolder + "/";
    }

    /// <summary>
    /// Returns true when a payload-relative path represents a root-level shared file: the Launcher executable or the
    /// uninstaller at the top of the payload, not inside App\ or any other subdirectory.
    /// </summary>
    private static bool IsRootSharedEntry(string relativePath)
    {
        // The entry must be exactly a shared file name with no directory component.
        var normalized = relativePath.Trim('/');
        return string.Equals(normalized, LauncherExeName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, UninstallerExeName, StringComparison.OrdinalIgnoreCase);
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


    // Cleanup and logging

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
