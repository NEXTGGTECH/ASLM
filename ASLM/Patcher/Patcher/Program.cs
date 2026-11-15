// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ASLM.Installer;
using Microsoft.Win32;

namespace ASLM.Patcher
{
    /// <summary>
    /// Carries one status update from the patcher runner to the UI.
    /// </summary>
    internal sealed record PatcherProgress(string Message);

    /// <summary>
    /// Applies a pending ASLM self-update while the main launcher is not running.
    /// </summary>
    internal static class PatcherRunner
    {
        // Constants

        /// <summary>
        /// Location of the pending update descriptor relative to the data root; slash-separated, converted when used.
        /// </summary>
        private const string PendingRelativePath = ".aslm-update/pending.json";

        /// <summary>
        /// Location of the patcher log relative to the data root; slash-separated, converted when used.
        /// </summary>
        private const string LogRelativePath = ".aslm-update/logs/Patcher.log";

        /// <summary>
        /// Platform-specific file name of the launcher executable, used by the legacy launcher lookup.
        /// </summary>
        private static string LauncherExeName => OperatingSystem.IsWindows() ? "ASLM.exe" : "ASLM";

        /// <summary>
        /// File name of the reference, written by the launcher, that stores the shared launcher path.
        /// </summary>
        private const string LauncherRefFileName = "launcher-ref.json";

        /// <summary>
        /// Argument naming the process id to wait for before files are replaced.
        /// </summary>
        private const string WaitProcessArgument = "--wait-process";

        /// <summary>
        /// Argument carrying the start time (UTC ticks) of the awaited process, used to detect a reused process id.
        /// </summary>
        private const string WaitStartArgument = "--wait-start";

        /// <summary>
        /// Argument that passes the path of the launcher executable to restart after the update.
        /// </summary>
        private const string LauncherArgument = "--launcher";

        /// <summary>
        /// Argument that marks the elevated copy this patcher starts for the shared installation folder; that copy
        /// leaves restarting the launcher to the patcher that waits for it.
        /// </summary>
        private const string ElevatedArgument = "--elevated";

        /// <summary>
        /// Exit code of a run that must not restart the launcher, because waiting for the previous process or locking
        /// the pending update failed.
        /// </summary>
        internal const int ExitCodeNoRelaunch = 2;

        /// <summary>
        /// Win32 error code that the shell reports when the user declines the UAC prompt (<c>ERROR_CANCELLED</c>).
        /// </summary>
        private const int ErrorCancelled = 1223;


        // Linux shared folder helper

        /// <summary>
        /// Argument that starts the shared folder helper: the copy of this patcher that pkexec starts with root rights
        /// on Linux. It only replaces the files of the shared installation folder and takes every path from the
        /// arguments below.
        /// </summary>
        private const string UpdateSharedArgument = "--update-shared";

        /// <summary>
        /// Helper argument naming the shared installation folder to update.
        /// </summary>
        private const string SharedDirArgument = "--shared-dir";

        /// <summary>
        /// Helper argument naming the directory that holds the new files of the update.
        /// </summary>
        private const string StagingArgument = "--staging";

        /// <summary>
        /// Helper argument naming the downloaded release archive that becomes the base archive for new users.
        /// </summary>
        private const string ArchiveArgument = "--archive";

        /// <summary>
        /// Helper argument carrying the four-part version that the install manifest records.
        /// </summary>
        private const string VersionArgument = "--version";

        /// <summary>
        /// Path of pkexec, which asks for the administrator password through the polkit agent of the desktop session
        /// and then starts a program with root rights.
        /// </summary>
        private const string PkexecPath = "/usr/bin/pkexec";

        /// <summary>
        /// Exit code of pkexec when the user dismissed the password dialog.
        /// </summary>
        private const int PkexecDismissedExitCode = 126;

        /// <summary>
        /// Exit code of pkexec when the user is not authorized, no polkit agent runs in the session, or pkexec failed.
        /// </summary>
        private const int PkexecNotAuthorizedExitCode = 127;


        // Shared installation layout

        /// <summary>
        /// File name of the install manifest in the shared installation folder of the dual-location layout.
        /// </summary>
        private const string SharedManifestFileName = "install-manifest.json";

        /// <summary>
        /// File name of the payload archive in the shared folder from which the launcher sets up new users.
        /// </summary>
        private const string SharedBaseArchiveFileName = "aslm-base.zip";

        /// <summary>
        /// Name under which a new payload archive is written before it replaces the old one, so a launcher that sets
        /// up a user never reads a partial archive; the uninstaller removes a leftover under this name.
        /// </summary>
        private const string SharedBaseArchiveTempFileName = "aslm-base.zip.tmp";

        /// <summary>
        /// Root-level payload files that belong only in the shared folder: the launcher and the uninstaller.
        /// </summary>
        private static readonly string[] SharedPayloadFileNames = [LauncherExeName, AppRegistration.UninstallerExeName];

        /// <summary>
        /// File name of the launcher in the Linux shared folder and at the root of the Linux release payload.
        /// </summary>
        private const string LinuxLauncherFileName = "ASLM";

        /// <summary>
        /// File name of the base archive in the Linux shared folder, from which the launcher sets up new users.
        /// </summary>
        private const string LinuxBaseArchiveFileName = "aslm-base.tar.gz";

        /// <summary>
        /// File name of the application menu icon in the Linux shared folder, which the desktop entry names.
        /// </summary>
        private const string LinuxIconFileName = "appicon.svg";

        /// <summary>
        /// Location of the application menu icon in the Linux release payload; slash-separated, converted when used.
        /// </summary>
        private const string LinuxIconPayloadPath = "App/hicolor/scalable/apps/appicon.svg";

        /// <summary>
        /// Name of the work folder inside the Linux shared folder that holds the new files and the backups while the
        /// helper replaces them. It lies on the same file system as the replaced files, so every replacement is a
        /// rename.
        /// </summary>
        private const string LinuxSharedWorkFolderName = ".aslm-shared-update";

        /// <summary>
        /// Unix mode of the launcher in the Linux shared folder, as the installer sets it (<c>755</c>).
        /// </summary>
        private const UnixFileMode LinuxExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute;

        /// <summary>
        /// Unix mode of the other files in the Linux shared folder, as the installer sets it (<c>644</c>).
        /// </summary>
        private const UnixFileMode LinuxRegularFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.OtherRead;


        // Runtime state and path comparison

        /// <summary>
        /// Full path of the patcher log file, set once the data root is known.
        /// </summary>
        private static string _logPath = string.Empty;

        /// <summary>
        /// Receiver of status messages for the UI, or null when the patcher runs headless.
        /// </summary>
        private static IProgress<PatcherProgress>? _progress;

        /// <summary>
        /// Writer that receives the log instead of the log file, or null. The shared folder helper writes nothing into
        /// the user's profile; its standard output goes into the log of the patcher that started it.
        /// </summary>
        private static TextWriter? _logWriter;

        /// <summary>
        /// Serializes log writes, because the output of the shared folder helper arrives on two reader threads.
        /// </summary>
        private static readonly object LogLock = new();

        /// <summary>
        /// Comparison for file system paths: case-insensitive on Windows, case-sensitive elsewhere.
        /// </summary>
        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


        // Update flow

        /// <summary>
        /// Runs the patch operation, or the Linux shared folder helper when the arguments ask for it, on a background
        /// thread.
        /// </summary>
        /// <param name="args">Command-line arguments: <c>--root</c>, <c>--launcher</c>, <c>--wait-process</c>,
        /// <c>--wait-start</c>, and after <c>--</c> the arguments forwarded to the restarted launcher; or
        /// <c>--update-shared</c> first, followed by the arguments of the shared folder helper.</param>
        /// <param name="progress">Receiver of status messages, or null to run without a UI.</param>
        /// <returns>0 when the update was applied or there was nothing to apply; 1 after a fatal error;
        /// <see cref="ExitCodeNoRelaunch"/> after a fatal error that must not restart the launcher. The helper returns
        /// 0 when it updated the shared folder and 1 otherwise.</returns>
        public static Task<int> RunAsync(string[] args, IProgress<PatcherProgress>? progress)
        {
            _progress = progress;
            var sharedHelper = args is [UpdateSharedArgument, ..];
            _logWriter = sharedHelper ? Console.Out : null;
            return Task.Run(() => sharedHelper ? RunSharedFolderHelper(args) : RunCore(args));
        }

        /// <summary>
        /// Loads the pending update, applies it, and restarts the launcher. On Windows and Linux the shared
        /// installation folder of the dual-location layout is updated too, when the update is a stable release of a
        /// newer global or sub version than the folder records: on Windows when this process has administrator
        /// rights, on Linux through pkexec after the administrator password is entered.
        /// </summary>
        private static int RunCore(string[] args)
        {
            // Resolve the data root and the log. After an error the launcher is restarted only when waiting for
            // the previous process succeeded (see mayRelaunch). An elevated copy leaves every restart to the patcher
            // that started it, so ASLM never runs with administrator rights.
            var root = ResolveRoot(args);
            _logPath = Path.Combine(root, LogRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var mayRelaunch = false;
            var restartsLauncher = !IsElevatedCopy(args);

            try
            {
                // Prepare the patcher log directory and wait for the launcher to exit.
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                Log("Patcher started.");
                WaitForRequestedProcessExit(args);
                mayRelaunch = true;

                // Stop when there is nothing left to apply.
                var pendingPath = Path.Combine(root, PendingRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(pendingPath))
                {
                    Log("No pending update found.");
                    if (restartsLauncher)
                    {
                        StartLauncher(root, args);
                    }

                    return 0;
                }

                // Lock the existing marker rather than creating a persistent lock file. A second launcher must not
                // apply or rename the same pending update while another helper owns it; deletion remains allowed.
                // A helper that fails to get the lock must not start another launcher, hence mayRelaunch is off.
                mayRelaunch = false;
                var sharing = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
                using var pendingStream = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, sharing);
                mayRelaunch = true;

                // Read the update description and reject unsupported or relative-path updates before touching files.
                var pending = LoadPendingUpdate(pendingStream);
                ValidatePendingUpdate(pending);

                // Resolve target, staging, backup, archive, and preserve paths.
                var targetRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(pending.TargetRoot) ? root : pending.TargetRoot);
                var stagingPath = Path.GetFullPath(pending.StagingPath);
                var backupPath = Path.GetFullPath(string.IsNullOrWhiteSpace(pending.BackupPath)
                    ? Path.Combine(root, ".aslm-update", "backup", DateTime.UtcNow.ToString("yyyyMMddHHmmss"))
                    : pending.BackupPath);
                var archivePath = string.IsNullOrWhiteSpace(pending.ArchivePath)
                    ? null
                    : Path.GetFullPath(pending.ArchivePath);
                if (!Directory.Exists(stagingPath))
                {
                    throw new DirectoryNotFoundException($"Staging path does not exist: {stagingPath}");
                }

                // Normalize the preserve rules and make sure the paths cannot damage anything outside the update.
                var preservePatterns = NormalizePreservePatterns(pending.Preserve);
                ValidateUpdatePaths(root, targetRoot, stagingPath, backupPath, archivePath);

                // On Windows and Linux the launcher may live in a separate shared installation folder. Its files are
                // updated in a step of their own below, and the per-user copy never receives them.
                var sharedInstallDir = ResolveSharedInstallDir(root, targetRoot, args);

                // Selects the staged entries that the per-user copy receives.
                bool ShouldCopyPath(string relativePath) =>
                    ShouldReplacePath(relativePath, preservePatterns) &&
                    (sharedInstallDir is null || !IsSharedPayloadFile(relativePath));

                // Replace application files with backup and rollback support.
                // Order: back up the replaceable files, clear them, then copy the staged files in. Preserved
                // paths (user data) are skipped by every step through the same filter.
                var replacementStarted = false;
                try
                {
                    Log($"Target root: {targetRoot}");
                    Log($"Staging path: {stagingPath}");

                    // Back up everything that will be replaced; this runs before anything is modified.
                    Directory.CreateDirectory(backupPath);

                    Log("Creating backup of replaceable application files...");
                    CopyDirectory(targetRoot, backupPath, relativePath => ShouldReplacePath(relativePath, preservePatterns));

                    // From here the installation is modified, so a failure requires a rollback.
                    Log("Replacing application files...");
                    replacementStarted = true;
                    ClearDirectory(targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));
                    CopyDirectory(stagingPath, targetRoot, ShouldCopyPath);

                    // The shared folder follows while the staged files still exist; that step never undoes the
                    // per-user update, because each user's copy works on its own.
                    if (sharedInstallDir is not null)
                    {
                        UpdateSharedInstallationIfNeeded(
                            sharedInstallDir, pending.Version, stagingPath, archivePath, backupPath, root);
                    }

                    // Record the installed release and remove temporary update folders.
                    // The data root holds ASLM_Data.json on every platform (targetRoot is the
                    // .app bundle on macOS, where no data lives).
                    PersistInstalledReleaseTag(root, pending.Version);
                    File.Delete(pendingPath);
                    TryDeleteDirectory(GetStagingDirectory(root, stagingPath));
                    TryDeleteDirectory(backupPath);
                    Log($"Update {pending.Version} applied successfully.");
                    if (restartsLauncher)
                    {
                        StartLauncher(root, args);
                    }

                    return 0;
                }
                catch
                {
                    // Restore the previous application files, then rethrow so the outer handler reports the failure.
                    // A failed backup is incomplete: leave the original installation intact instead of restoring it.
                    if (replacementStarted)
                    {
                        Log("Patch failed. Attempting rollback...");
                        ClearDirectory(targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));
                        CopyDirectory(backupPath, targetRoot);
                        Log("Previous application files restored.");
                    }

                    throw;
                }
            }
            catch (Exception ex)
            {
                // Keep the launcher usable after a fatal patcher error.
                Log("Fatal patcher error: " + ex);

                // Never open another instance when waiting for the previous process failed; the exit code tells a
                // waiting patcher the same. The failed descriptor is renamed so the restarted launcher does not retry
                // the same update forever.
                if (!mayRelaunch)
                {
                    return ExitCodeNoRelaunch;
                }

                MarkPendingUpdateFailed(root);
                if (restartsLauncher)
                {
                    StartLauncher(root, args);
                }

                return 1;
            }
            finally
            {
                // The temporary copy this helper runs from is no longer needed, whatever the outcome.
                TryDeleteShadowDirectory(root);
            }
        }


        // Process wait and pending update loading

        /// <summary>
        /// Waits for the previous ASLM process before replacing application files.
        /// </summary>
        private static void WaitForRequestedProcessExit(string[] args)
        {
            // Only patcher control arguments (before "--") are inspected; later ones belong to ASLM.
            var separator = Array.IndexOf(args, "--");
            var internalArgs = separator < 0 ? args : args[..separator];
            var index = Array.IndexOf(internalArgs, WaitProcessArgument);
            if (index < 0) return;
            if (index + 1 >= internalArgs.Length || !int.TryParse(internalArgs[index + 1], out var processId) || processId <= 0)
                throw new ArgumentException("Invalid restart process id.");

            // Match process identity as well as PID when the caller provides the start time.
            long? started = null;
            var startIndex = Array.IndexOf(internalArgs, WaitStartArgument);
            if (startIndex >= 0)
            {
                if (startIndex + 1 >= internalArgs.Length || !long.TryParse(internalArgs[startIndex + 1],
                        NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                    throw new ArgumentException("Invalid restart process start time.");
                started = ticks;
            }

            // Wait up to a minute; a different start time means the id now belongs to another process,
            // and a process that is already gone needs no waiting.
            try
            {
                using var process = Process.GetProcessById(processId);
                if (started.HasValue && process.StartTime.ToUniversalTime().Ticks != started.Value) return;
                Log($"Waiting for ASLM process {processId} to exit...");
                if (!process.WaitForExit(60000))
                    throw new TimeoutException($"ASLM process {processId} did not exit within 60 seconds.");
            }
            catch (ArgumentException) { /* The process already exited. */ }
            catch (InvalidOperationException) { /* The process exited before its start time could be read. */ }
        }

        /// <summary>
        /// Reads the pending update descriptor from the already opened (and locked) file stream.
        /// </summary>
        private static PendingAppUpdate LoadPendingUpdate(Stream pendingStream)
        {
            var pending = JsonSerializer.Deserialize<PendingAppUpdate>(pendingStream)
                ?? new PendingAppUpdate();
            pending.Normalize();
            return pending;
        }

        /// <summary>
        /// Validates the pending update payload before any filesystem changes begin.
        /// </summary>
        private static void ValidatePendingUpdate(PendingAppUpdate pending)
        {
            // Only full application updates are supported by this patcher.
            if (!string.Equals(pending.Kind, "app", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsupported pending update kind: {pending.Kind}");
            }

            // A relative staging path would depend on this helper's working directory.
            if (!Path.IsPathFullyQualified(pending.StagingPath))
                throw new InvalidOperationException("The staging path must be absolute.");
        }


        // Root resolution

        /// <summary>
        /// Resolves the ASLM root path either from command-line arguments or from the current executable directory.
        /// </summary>
        private static string ResolveRoot(string[] args)
        {
            // An explicit --root (given before the "--" separator) wins.
            for (var index = 0; index < args.Length - 1 && args[index] != "--"; index++)
            {
                if (string.Equals(args[index], "--root", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(args[index + 1]);
                }
            }

            // Otherwise the patcher is assumed to run from the data root itself.
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }


        // Internal path filtering

        /// <summary>
        /// Returns whether the relative path points into the updater's own working directory.
        /// </summary>
        private static bool IsInternalUpdatePath(string relativePath)
        {
            // Compare in the slash-only form regardless of the separator the caller used.
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            return normalized.Equals(".aslm-update", PathComparison) ||
                   normalized.StartsWith(".aslm-update/", PathComparison);
        }


        // Preserve handling

        /// <summary>
        /// Normalizes preserved path patterns once before replacement begins.
        /// </summary>
        private static List<string> NormalizePreservePatterns(IEnumerable<string> preservePatterns)
        {
            // Drop empty entries and any pattern that could point outside the root ("..", ".", empty).
            return preservePatterns
                .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
                .Select(pattern => NormalizeRelativePattern(pattern))
                .Where(pattern => pattern.Length > 0 && pattern != "." && !pattern.Contains("..", StringComparison.Ordinal))
                .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Normalizes one preserved pattern into the slash-only relative format used by the patcher.
        /// </summary>
        private static string NormalizeRelativePattern(string pattern)
        {
            return pattern.Trim().Replace('\\', '/').Trim('/');
        }

        /// <summary>
        /// Returns whether a relative path should be replaced by the new application payload.
        /// </summary>
        private static bool ShouldReplacePath(string relativePath, IReadOnlyCollection<string> preservePatterns)
        {
            return !IsInternalUpdatePath(relativePath) && !IsPreservedPath(relativePath, preservePatterns);
        }

        /// <summary>
        /// Returns whether the relative path belongs to a preserved file or directory.
        /// </summary>
        private static bool IsPreservedPath(string relativePath, IReadOnlyCollection<string> preservePatterns)
        {
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            return preservePatterns.Any(pattern => IsPatternMatch(pattern, normalized));
        }

        /// <summary>
        /// Returns whether a preserved pattern matches the requested relative path.
        /// </summary>
        private static bool IsPatternMatch(string pattern, string relativePath)
        {
            // A plain pattern matches the exact path or anything below that directory.
            if (!pattern.Contains('*'))
            {
                return relativePath.Equals(pattern, PathComparison) ||
                       relativePath.StartsWith(pattern.TrimEnd('/') + "/", PathComparison);
            }

            // A wildcard matches within one path segment ("*" never crosses a "/"), plus everything below a match.
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal) + "($|/.*)";
            var options = RegexOptions.CultureInvariant;
            if (OperatingSystem.IsWindows()) options |= RegexOptions.IgnoreCase;
            return Regex.IsMatch(relativePath, regexPattern, options);
        }


        // Copy helpers

        /// <summary>
        /// Recursively copies one directory into another, optionally filtering entries by relative path.
        /// </summary>
        private static void CopyDirectory(
            string sourceDir,
            string destDir,
            Func<string, bool>? includeRelative = null)
        {
            Directory.CreateDirectory(destDir);
            var sourceRoot = Path.GetFullPath(sourceDir);
            CopyDirectoryContents(sourceRoot, destDir, string.Empty, includeRelative);
        }

        /// <summary>
        /// Copies directory contents recursively while pruning filtered directories before descent.
        /// </summary>
        private static void CopyDirectoryContents(
            string sourceRoot,
            string destRoot,
            string relativeDirectory,
            Func<string, bool>? includeRelative)
        {
            var sourceDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? sourceRoot
                : ResolveChildPath(sourceRoot, relativeDirectory);

            foreach (var entry in new DirectoryInfo(sourceDirectory).EnumerateFileSystemInfos())
            {
                // Skip filtered entries (preserved data, the updater's own folder) without descending into them.
                var relative = CombineRelativePath(relativeDirectory, entry.Name);
                if (includeRelative?.Invoke(relative) == false)
                {
                    continue;
                }

                var destination = ResolveChildPath(destRoot, relative);
                if (entry.LinkTarget is { } linkTarget)
                {
                    // Recreate links, including dangling links, without traversing their targets during backup or
                    // apply.
                    if (entry is DirectoryInfo) Directory.CreateSymbolicLink(destination, linkTarget);
                    else File.CreateSymbolicLink(destination, linkTarget);
                    continue;
                }

                // Copy directories recursively and files one by one, overwriting existing files.
                if (entry is DirectoryInfo)
                {
                    Directory.CreateDirectory(destination);
                    CopyDirectoryContents(sourceRoot, destRoot, relative, includeRelative);
                }
                else File.Copy(entry.FullName, destination, overwrite: true);

                // Preserve executable bits on files and searchable directory permissions on Unix.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(entry.FullName));
            }
        }


        // Delete helpers

        /// <summary>
        /// Deletes every file inside one directory and removes empty subdirectories, optionally filtering by relative
        /// path.
        /// </summary>
        private static void ClearDirectory(string directory, Func<string, bool>? deleteRelative = null)
        {
            Directory.CreateDirectory(directory);
            ClearDirectoryContents(Path.GetFullPath(directory), string.Empty, deleteRelative);
        }

        /// <summary>
        /// Deletes directory contents recursively while pruning preserved directories before descent.
        /// </summary>
        private static void ClearDirectoryContents(
            string root,
            string relativeDirectory,
            Func<string, bool>? deleteRelative)
        {
            var currentDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                ? root
                : ResolveChildPath(root, relativeDirectory);

            foreach (var entry in new DirectoryInfo(currentDirectory).EnumerateFileSystemInfos())
            {
                // Leave preserved entries untouched and do not descend into them.
                var relative = CombineRelativePath(relativeDirectory, entry.Name);
                if (deleteRelative?.Invoke(relative) == false)
                {
                    continue;
                }

                // Unlink directory links instead of recursively deleting files outside the installation.
                if (entry.LinkTarget != null)
                {
                    entry.Delete();
                    continue;
                }

                // Empty a directory first and remove it only if nothing preserved remains inside.
                if (entry is DirectoryInfo directory)
                {
                    ClearDirectoryContents(root, relative, deleteRelative);
                    if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
                }
                else
                {
                    // Read-only files cannot be deleted on Windows until the attribute is cleared.
                    if (OperatingSystem.IsWindows()) entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }
            }
        }


        // Path helpers

        /// <summary>
        /// Combines relative path segments using the platform separator.
        /// </summary>
        private static string CombineRelativePath(string parent, string child)
        {
            return string.IsNullOrWhiteSpace(parent)
                ? child
                : Path.Combine(parent, child);
        }

        /// <summary>
        /// Resolves one child path and rejects directory traversal outside the requested root.
        /// </summary>
        private static string ResolveChildPath(string rootPath, string relativePath)
        {
            // Compare with trailing separators so "/a/bc" is not mistaken for a child of "/a/b".
            var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
            var combined = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!EnsureTrailingSeparator(combined).StartsWith(normalizedRoot, PathComparison))
            {
                throw new InvalidOperationException($"Path '{relativePath}' escapes root '{rootPath}'.");
            }

            return combined;
        }

        /// <summary>
        /// Ensures directory paths always end with a separator for secure prefix checks.
        /// </summary>
        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Rejects overlapping work directories and limits updater cleanup to its own staging and backup trees.
        /// </summary>
        /// <param name="root">Data root that holds the updater folders.</param>
        /// <param name="targetRoot">Directory whose files the update replaces.</param>
        /// <param name="stagingPath">Directory that holds the new files.</param>
        /// <param name="backupPath">Directory that receives the backup of the replaced files.</param>
        /// <param name="archivePath">Downloaded update archive, or null when the update names none.</param>
        private static void ValidateUpdatePaths(
            string root,
            string targetRoot,
            string stagingPath,
            string backupPath,
            string? archivePath)
        {
            // Staging, the archive and the backup must live in the updater's own folders, and the target must not
            // overlap them, the filesystem root or the updater folder; otherwise clearing the target could destroy
            // them.
            var stagingRoot = Path.Combine(root, ".aslm-update", "staging");
            var backupRoot = Path.Combine(root, ".aslm-update", "backup");
            if (!IsDescendant(stagingRoot, stagingPath) || !IsDescendant(backupRoot, backupPath) ||
                (archivePath is not null && !IsDescendant(stagingRoot, archivePath)) ||
                string.Equals(Path.TrimEndingDirectorySeparator(targetRoot), Path.GetPathRoot(targetRoot), PathComparison) ||
                IsDescendant(Path.Combine(root, ".aslm-update"), targetRoot) ||
                IsDescendant(stagingPath, targetRoot) || IsDescendant(backupPath, targetRoot) ||
                string.Equals(targetRoot, stagingPath, PathComparison) || string.Equals(targetRoot, backupPath, PathComparison))
                throw new InvalidOperationException("Unsafe application update paths.");

            // A separate profile nested inside the target would not be covered by Windows-style Data/ preserve rules.
            if (IsDescendant(targetRoot, root))
                throw new InvalidOperationException("The application update target contains the user profile.");

            // A reused backup must never mix files from an older installation with the current snapshot.
            if (Directory.Exists(backupPath) && Directory.EnumerateFileSystemEntries(backupPath).Any())
                throw new InvalidOperationException("The backup directory is not empty.");
        }

        /// <summary>
        /// Compares complete path components using the target platform's case rules.
        /// </summary>
        private static bool IsDescendant(string root, string path)
        {
            return Path.GetFullPath(path).StartsWith(EnsureTrailingSeparator(Path.GetFullPath(root)), PathComparison);
        }

        /// <summary>
        /// Finds the version-specific staging directory, including both extracted files and the downloaded archive.
        /// </summary>
        private static string GetStagingDirectory(string root, string stagingPath)
        {
            var stagingRoot = Path.Combine(root, ".aslm-update", "staging");
            var relative = Path.GetRelativePath(stagingRoot, stagingPath);
            return ResolveChildPath(stagingRoot, relative.Split(Path.DirectorySeparatorChar)[0]);
        }


        // Launcher restart

        /// <summary>
        /// Restarts the Launcher after patching or after a fatal error.
        /// </summary>
        /// <remarks>
        /// Resolution order for the Launcher path:
        /// 1. The <c>--launcher</c> command-line argument passed by the Launcher itself.
        /// 2. The <c>launcher-ref.json</c> file written by the Launcher in the application root.
        /// 3. Fallback: <c>ASLM.exe</c> adjacent to the application root (legacy / monolithic layout).
        /// </remarks>
        private static void StartLauncher(string root, string[] args)
        {
            try
            {
                // Find the launcher to restart; nothing is started when it cannot be located.
                var launcherPath = ResolveLauncherPath(root, args);
                if (string.IsNullOrWhiteSpace(launcherPath))
                {
                    Log("Launcher not found after patch: <unresolved>");
                    return;
                }

                // macOS hands over a .app bundle path; relaunch it through launch services.
                if (launcherPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(launcherPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/usr/bin/open",
                        ArgumentList = { "-n", launcherPath },
                        UseShellExecute = false
                    });
                    return;
                }

                if (!File.Exists(launcherPath))
                {
                    Log("Launcher not found after patch: " + launcherPath);
                    return;
                }

                // Start the launcher as a normal process from its own directory.
                var startInfo = new ProcessStartInfo
                {
                    FileName = launcherPath,
                    WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? root,
                    UseShellExecute = false
                };

                // Only arguments after the separator belong to ASLM; patcher control arguments stay private.
                var separator = Array.IndexOf(args, "--");
                if (separator >= 0)
                    foreach (var argument in args[(separator + 1)..]) startInfo.ArgumentList.Add(argument);
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                // A failed restart must not turn a finished update into a crash; the user can start ASLM manually.
                Log("Failed to restart launcher: " + ex);
            }
        }

        /// <summary>
        /// Resolves the Launcher executable path using a three-tier strategy.
        /// </summary>
        private static string? ResolveLauncherPath(string root, string[] args)
        {
            // Tier 1: explicit --launcher argument provided by the Launcher process.
            for (var i = 0; i < args.Length - 1 && args[i] != "--"; i++)
            {
                if (string.Equals(args[i], LauncherArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            // Tier 2: launcher-ref.json written to the application root directory.
            var launcherRefPath = Path.Combine(root, LauncherRefFileName);
            if (File.Exists(launcherRefPath))
            {
                try
                {
                    var json = File.ReadAllText(launcherRefPath);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("launcherPath", out var element))
                    {
                        var path = element.GetString();
                        if (!string.IsNullOrWhiteSpace(path))
                        {
                            return path;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("Failed to read launcher-ref.json: " + ex.Message);
                }
            }

            // Tier 3: legacy / monolithic layout — ASLM.exe lives next to the application root.
            return Path.Combine(root, LauncherExeName);
        }


        // Shared installation folder

        /// <summary>
        /// Returns the shared installation folder of the dual-location layout on Windows and Linux: the folder of the
        /// launcher that started the patcher, when it differs from the folder the update replaces and is an ASLM
        /// installation folder (see <see cref="IsSharedInstallDir"/>).
        /// </summary>
        /// <returns>The shared folder, or null for portable, monolithic and macOS installations.</returns>
        internal static string? ResolveSharedInstallDir(string root, string targetRoot, string[] args)
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return null;
            }

            return ResolveSharedInstallDir(root, targetRoot, args, linuxLayout: OperatingSystem.IsLinux());
        }

        /// <summary>
        /// Returns the shared installation folder of the dual-location layout for the given platform layout.
        /// </summary>
        /// <param name="root">Data root of the patcher.</param>
        /// <param name="targetRoot">Directory whose files the update replaces.</param>
        /// <param name="args">Command-line arguments of the patcher, which name the launcher.</param>
        /// <param name="linuxLayout">True for the Linux layout, which also accepts a folder without an install
        /// manifest.</param>
        /// <returns>The shared folder, or null when the launcher does not live in a separate ASLM installation
        /// folder.</returns>
        internal static string? ResolveSharedInstallDir(string root, string targetRoot, string[] args, bool linuxLayout)
        {
            var launcherPath = ResolveLauncherPath(root, args);
            var sharedDir = string.IsNullOrWhiteSpace(launcherPath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(launcherPath));
            return sharedDir is null || IsSamePath(sharedDir, targetRoot) || IsSamePath(sharedDir, root) ||
                   !IsSharedInstallDir(sharedDir, linuxLayout)
                ? null
                : sharedDir;
        }

        /// <summary>
        /// Returns whether a folder is an ASLM shared installation folder: it holds an install manifest that the
        /// installer wrote for exactly this folder. The first Linux installers wrote no manifest, so in the Linux
        /// layout a folder without one also counts when it holds the launcher and the base archive; its version is
        /// unknown then.
        /// </summary>
        /// <param name="sharedDir">Folder to check.</param>
        /// <param name="linuxLayout">True for the Linux layout.</param>
        internal static bool IsSharedInstallDir(string sharedDir, bool linuxLayout)
        {
            try
            {
                var manifestPath = Path.Combine(sharedDir, SharedManifestFileName);
                if (!File.Exists(manifestPath))
                {
                    return linuxLayout &&
                           File.Exists(Path.Combine(sharedDir, LinuxLauncherFileName)) &&
                           File.Exists(Path.Combine(sharedDir, LinuxBaseArchiveFileName));
                }

                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
                return ReadString(manifest, "app") == "ASLM" &&
                       IsSamePath(ReadString(manifest, "sharedInstallPath"), sharedDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log("Failed to read the shared install manifest: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Returns whether the shared folder takes the update. Only stable releases update it, and only when their
        /// global or sub version (the first two parts) is newer than the version in the install manifest, or when that
        /// version is unknown: the first installers recorded none or a placeholder. A folder whose update was declined
        /// keeps its old version, so every later update asks again until the folder is updated. Updates within the
        /// same global and sub version, prereleases and older releases leave the folder as it is.
        /// </summary>
        /// <param name="sharedInstallDir">Shared installation folder.</param>
        /// <param name="releaseTag">Release tag of the update, such as <c>v0.12.0.5</c>.</param>
        /// <param name="version">Four-part version of the release, which the manifest records after the update; empty
        /// when the tag names no stable release.</param>
        internal static bool IsSharedUpdateNeeded(string sharedInstallDir, string releaseTag, out string version)
        {
            version = string.Empty;
            if (!TryParseStableReleaseVersion(releaseTag, out var releaseVersion))
            {
                return false;
            }

            version = releaseVersion.ToString();
            var installedVersion = ReadSharedManifestVersion(sharedInstallDir);
            return installedVersion is null ||
                   releaseVersion.Major > installedVersion.Major ||
                   (releaseVersion.Major == installedVersion.Major && releaseVersion.Minor > installedVersion.Minor);
        }

        /// <summary>
        /// Reads the four-part version of a stable release tag such as <c>v0.12.0.5</c>; older three-part tags get 0
        /// as the last part. A tag with a suffix, such as <c>v0.12.0.5-rc1</c>, names a prerelease.
        /// </summary>
        /// <returns>True when the tag names a stable release.</returns>
        internal static bool TryParseStableReleaseVersion(string releaseTag, [NotNullWhen(true)] out Version? version)
        {
            version = null;
            var text = releaseTag.Trim();
            if (text.StartsWith('v') || text.StartsWith('V'))
            {
                text = text[1..];
            }

            // Version.TryParse alone would also accept two parts, signs and surrounding whitespace.
            var parts = text.Split('.');
            if (parts.Length is not (3 or 4) ||
                parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)) ||
                !Version.TryParse(text, out var parsed))
            {
                return false;
            }

            version = parsed.Revision < 0 ? new Version(parsed.Major, parsed.Minor, parsed.Build, 0) : parsed;
            return true;
        }

        /// <summary>
        /// Updates the shared folder when it takes the update (see <see cref="IsSharedUpdateNeeded"/>): on Windows
        /// when this process has administrator rights, on Linux through the shared folder helper with root rights.
        /// Without the rights the folder stays as it is and the next update asks again.
        /// </summary>
        /// <param name="sharedInstallDir">Shared installation folder.</param>
        /// <param name="releaseTag">Release tag of the update.</param>
        /// <param name="stagingPath">Directory that holds the new files.</param>
        /// <param name="archivePath">Downloaded update archive, or null when the pending update names none.</param>
        /// <param name="backupPath">Backup folder of the per-user update.</param>
        /// <param name="userAppDir">Per-user application folder.</param>
        private static void UpdateSharedInstallationIfNeeded(
            string sharedInstallDir,
            string releaseTag,
            string stagingPath,
            string? archivePath,
            string backupPath,
            string userAppDir)
        {
            if (!IsSharedUpdateNeeded(sharedInstallDir, releaseTag, out var version))
            {
                Log($"The shared installation folder keeps its files: {releaseTag} is not a stable release of a newer " +
                    "global or sub version.");
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                // A fresh backup folder next to the per-user backup keeps both apart, so neither rollback mixes them.
                UpdateWindowsSharedInstallationIfElevated(new SharedInstallationUpdate(
                    sharedInstallDir,
                    stagingPath,
                    archivePath,
                    $"{backupPath}-shared-{Guid.NewGuid():N}",
                    version,
                    userAppDir));
            }
            else if (OperatingSystem.IsLinux())
            {
                // New users are set up from the release archive, so without it the folder cannot be updated.
                if (archivePath is null)
                {
                    Log("The pending update names no release archive, so the shared installation folder keeps its files.");
                    return;
                }

                UpdateLinuxSharedInstallationAsRoot(
                    new LinuxSharedInstallationUpdate(sharedInstallDir, stagingPath, archivePath, version));
            }
        }

        /// <summary>
        /// Updates the Windows shared folder when this process has administrator rights; without them the folder stays
        /// as it is and the next update tries again.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void UpdateWindowsSharedInstallationIfElevated(SharedInstallationUpdate update)
        {
            if (!IsElevated())
            {
                Log("The shared installation folder needs administrator rights; the next update tries again.");
                return;
            }

            UpdateSharedInstallation(update, () => AppRegistration.OpenUninstallRoot(writable: true));
        }

        /// <summary>
        /// Replaces the files of the shared installation folder with those of the update: the launcher, the
        /// uninstaller, the payload archive for new users, and the version in the install manifest; then keeps the
        /// entry in the installed applications current. A failure restores the previous files from the backup.
        /// </summary>
        /// <param name="update">Folders, archive and version of the update.</param>
        /// <param name="openUninstallRoot">Opens the writable list of installed applications.</param>
        /// <returns>True when the shared folder was updated.</returns>
        [SupportedOSPlatform("windows")]
        internal static bool UpdateSharedInstallation(SharedInstallationUpdate update, Func<RegistryKey?> openUninstallRoot)
        {
            var sharedDir = update.SharedInstallDir;
            var backups = new List<SharedFileBackup>();
            try
            {
                Log($"Updating the shared installation folder: {sharedDir}");
                Directory.CreateDirectory(update.BackupPath);

                // The launcher and, when the update carries it, the uninstaller.
                foreach (var fileName in SharedPayloadFileNames)
                {
                    var source = Path.Combine(update.StagingPath, fileName);
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    var target = Path.Combine(sharedDir, fileName);
                    BackupSharedFile(target, update.BackupPath, backups);
                    File.Copy(source, target, overwrite: true);
                }

                // The payload archive goes in under a temporary name first and then replaces the old one at once.
                if (update.ArchivePath is { } archivePath && File.Exists(archivePath))
                {
                    var target = Path.Combine(sharedDir, SharedBaseArchiveFileName);
                    var temporary = Path.Combine(sharedDir, SharedBaseArchiveTempFileName);
                    File.Copy(archivePath, temporary, overwrite: true);
                    BackupSharedFile(target, update.BackupPath, backups);
                    File.Move(temporary, target, overwrite: true);
                }

                // The manifest records the version that the next update compares with.
                var manifestPath = Path.Combine(sharedDir, SharedManifestFileName);
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                    ?? throw new InvalidDataException("The shared install manifest is not a JSON object.");
                BackupSharedFile(manifestPath, update.BackupPath, backups);
                manifest["version"] = update.Version;
                File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                // Put the previous files back; the backup stays when that fails, because it holds their only copy.
                Log("Failed to update the shared installation folder: " + ex);
                TryDeleteFile(Path.Combine(sharedDir, SharedBaseArchiveTempFileName));
                if (RestoreSharedFiles(backups))
                {
                    TryDeleteDirectory(update.BackupPath);
                }
                else
                {
                    Log($"The previous shared files are kept in {update.BackupPath}.");
                }

                return false;
            }

            TryDeleteDirectory(update.BackupPath);
            UpdateRegistration(update, openUninstallRoot);
            Log($"Shared installation folder updated to {update.Version}.");
            return true;
        }

        /// <summary>
        /// Keeps the entry in the installed applications current. An installation made before the installer wrote
        /// such an entry gets one now, which makes its uninstaller reachable from the Windows settings. A failure only
        /// leaves the shown version stale, so it is logged.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static void UpdateRegistration(SharedInstallationUpdate update, Func<RegistryKey?> openUninstallRoot)
        {
            try
            {
                using var uninstallRoot = openUninstallRoot();
                if (uninstallRoot is null)
                {
                    return;
                }

                // An entry is written only when its uninstaller exists, because the entry starts it.
                var installLocation = AppRegistration.ReadInstallLocation(uninstallRoot);
                if (installLocation is null)
                {
                    if (File.Exists(Path.Combine(update.SharedInstallDir, AppRegistration.UninstallerExeName)))
                    {
                        AppRegistration.Register(
                            uninstallRoot,
                            update.SharedInstallDir,
                            update.Version,
                            AppRegistration.EstimateSizeKb(update.SharedInstallDir, update.UserAppDir));
                    }

                    return;
                }

                // An entry of another folder belongs to another installation and stays as it is.
                if (IsSamePath(installLocation, update.SharedInstallDir))
                {
                    AppRegistration.SetVersion(uninstallRoot, update.Version);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                Log("Failed to update the entry in the installed applications: " + ex.Message);
            }
        }

        /// <summary>
        /// Copies a shared file into the backup folder before it is replaced, and records how to undo the change: a
        /// file that did not exist is deleted again.
        /// </summary>
        private static void BackupSharedFile(string target, string backupPath, List<SharedFileBackup> backups)
        {
            if (!File.Exists(target))
            {
                backups.Add(new SharedFileBackup(target, null));
                return;
            }

            var backup = Path.Combine(backupPath, Path.GetFileName(target));
            File.Copy(target, backup, overwrite: true);
            backups.Add(new SharedFileBackup(target, backup));
        }

        /// <summary>
        /// Undoes the recorded changes of the shared folder in reverse order.
        /// </summary>
        /// <returns>True when every file was restored.</returns>
        private static bool RestoreSharedFiles(List<SharedFileBackup> backups)
        {
            var restored = true;
            foreach (var backup in backups.AsEnumerable().Reverse())
            {
                try
                {
                    if (backup.BackupPath is null)
                    {
                        File.Delete(backup.TargetPath);
                    }
                    else
                    {
                        File.Copy(backup.BackupPath, backup.TargetPath, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Failed to restore {backup.TargetPath}: {ex.Message}");
                    restored = false;
                }
            }

            return restored;
        }

        /// <summary>
        /// Returns whether a relative path names a root-level file that belongs only in the shared folder.
        /// </summary>
        private static bool IsSharedPayloadFile(string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/').Trim('/');
            return SharedPayloadFileNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads the four-part version recorded in the shared install manifest.
        /// </summary>
        /// <returns>The version, or null when the manifest is unreadable or records no four-part version.</returns>
        private static Version? ReadSharedManifestVersion(string sharedInstallDir)
        {
            try
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sharedInstallDir, SharedManifestFileName)))
                    as JsonObject;
                return Version.TryParse(ReadString(manifest, "version"), out var version) &&
                       version.Build >= 0 && version.Revision >= 0
                    ? version
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // An unreadable manifest gives no version; updating the folder then reports the actual problem.
                return null;
            }
        }

        /// <summary>
        /// Returns a string property of a JSON object, or null when it is missing or not a string.
        /// </summary>
        private static string? ReadString(JsonObject? json, string propertyName)
        {
            return json?[propertyName] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }


        // Linux shared installation

        /// <summary>
        /// Hands the update of the Linux shared folder to the shared folder helper: a copy of this patcher that pkexec
        /// starts with root rights once the administrator password is entered in the dialog of the desktop session.
        /// A patcher that already runs with root rights updates the folder itself. This process waits for the helper,
        /// so the launcher restarts only afterwards. A dismissed dialog, missing rights or a failed helper leave the
        /// folder as it is, and nothing here undoes the per-user update.
        /// </summary>
        [SupportedOSPlatform("linux")]
        private static void UpdateLinuxSharedInstallationAsRoot(LinuxSharedInstallationUpdate update)
        {
            if (Environment.IsPrivilegedProcess)
            {
                UpdateLinuxSharedInstallation(update);
                return;
            }

            // Without pkexec there is no way to enter the administrator password, so only the per-user copy changes.
            var patcherPath = Environment.ProcessPath;
            if (!File.Exists(PkexecPath) || string.IsNullOrEmpty(patcherPath))
            {
                Log("pkexec is not available, so the shared installation folder keeps its files; the next update " +
                    "tries again.");
                return;
            }

            try
            {
                Log("The shared installation folder needs administrator rights. Asking for the administrator password...");
                var exitCode = RunSharedFolderHelperProcess(
                    BuildSharedFolderHelperStartInfo(PkexecPath, patcherPath, update));
                if (exitCode == PkexecDismissedExitCode)
                {
                    Log("The administrator password dialog was dismissed. The shared installation folder keeps its " +
                        "files; the next update asks again.");
                }
                else if (exitCode == PkexecNotAuthorizedExitCode)
                {
                    Log("Administrator rights were not granted, or no polkit authentication agent runs in this " +
                        "session. The shared installation folder keeps its files; the next update asks again.");
                }
                else if (exitCode != 0)
                {
                    Log($"The shared folder helper failed with exit code {exitCode}; the shared installation folder " +
                        "keeps its previous files.");
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                Log("Failed to start the shared folder helper: " + ex.Message);
            }
        }

        /// <summary>
        /// Builds the command that starts the shared folder helper through pkexec: the patcher executable with the
        /// helper arguments. pkexec gives the helper a clean environment, so every path travels as an argument.
        /// </summary>
        /// <param name="pkexecPath">Program that starts the helper with root rights.</param>
        /// <param name="patcherPath">Executable of this patcher.</param>
        /// <param name="update">Folders, archive and version of the update.</param>
        internal static ProcessStartInfo BuildSharedFolderHelperStartInfo(
            string pkexecPath,
            string patcherPath,
            LinuxSharedInstallationUpdate update)
        {
            var startInfo = new ProcessStartInfo { FileName = pkexecPath, UseShellExecute = false };
            string[] arguments =
            [
                patcherPath,
                UpdateSharedArgument,
                SharedDirArgument, update.SharedInstallDir,
                StagingArgument, update.StagingPath,
                ArchiveArgument, update.ArchivePath,
                VersionArgument, update.Version
            ];
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        /// <summary>
        /// Starts the shared folder helper, writes its output into this patcher's log, and waits for it to exit.
        /// </summary>
        /// <returns>The exit code of the started process.</returns>
        internal static int RunSharedFolderHelperProcess(ProcessStartInfo startInfo)
        {
            // Both streams are read while the helper runs, so a full pipe never blocks it.
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            using var process = new Process { StartInfo = startInfo };
            process.OutputDataReceived += (_, line) => LogHelperOutput(line.Data);
            process.ErrorDataReceived += (_, line) => LogHelperOutput(line.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Waiting without a timeout also waits until both streams are read to their end.
            process.WaitForExit();
            return process.ExitCode;
        }

        /// <summary>
        /// Writes one output line of the shared folder helper, indented, into this patcher's log.
        /// </summary>
        private static void LogHelperOutput(string? line)
        {
            if (!string.IsNullOrEmpty(line))
            {
                Log("  " + line);
            }
        }

        /// <summary>
        /// Runs the shared folder helper, the copy of this patcher that pkexec started with root rights: it replaces
        /// the files of the Linux shared installation folder and logs to its standard output, which the patcher that
        /// started it records. It never reads the pending update, which that patcher keeps locked, writes nothing into
        /// the user's profile and starts nothing afterwards.
        /// </summary>
        /// <returns>0 when the shared folder was updated; 1 when the arguments are invalid or the update
        /// failed.</returns>
        private static int RunSharedFolderHelper(string[] args)
        {
            try
            {
                return UpdateLinuxSharedInstallation(ParseSharedFolderHelperArguments(args)) ? 0 : 1;
            }
            catch (ArgumentException ex)
            {
                Log("Invalid shared folder helper arguments: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Reads the arguments of the shared folder helper: an ASLM shared installation folder, the absolute paths of
        /// the staged files and of the release archive, and a four-part version.
        /// </summary>
        /// <exception cref="ArgumentException">An argument is missing or invalid.</exception>
        internal static LinuxSharedInstallationUpdate ParseSharedFolderHelperArguments(string[] args)
        {
            // Returns the value that follows a helper argument.
            string ReadValue(string name)
            {
                var index = Array.IndexOf(args, name);
                if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    throw new ArgumentException($"The value of {name} is missing.");
                }

                return args[index + 1];
            }

            // Returns the full form of a path argument, which must be absolute.
            string ReadPath(string name)
            {
                var value = ReadValue(name);
                return Path.IsPathFullyQualified(value)
                    ? Path.GetFullPath(value)
                    : throw new ArgumentException($"The value of {name} must be an absolute path.");
            }

            var update = new LinuxSharedInstallationUpdate(
                ReadPath(SharedDirArgument),
                ReadPath(StagingArgument),
                ReadPath(ArchiveArgument),
                ReadValue(VersionArgument));
            if (!Version.TryParse(update.Version, out var version) || version.Revision < 0)
            {
                throw new ArgumentException($"The version {update.Version} does not have four parts.");
            }

            if (!IsSharedInstallDir(update.SharedInstallDir, linuxLayout: true))
            {
                throw new ArgumentException($"{update.SharedInstallDir} is not an ASLM shared installation folder.");
            }

            return update;
        }

        /// <summary>
        /// Replaces the files of the Linux shared installation folder with those of the update: the launcher, the
        /// base archive from which the launcher sets up new users, the application menu icon, and the version in the
        /// install manifest. The new files are written into a work folder inside the shared folder and then renamed
        /// over the old ones, so a launcher that starts meanwhile always finds complete files. The launcher goes
        /// first, because only the new launcher reads the release archive that becomes the base archive; the manifest
        /// goes last, so an interrupted run leaves the old version recorded and the next update repeats it. A failure
        /// restores the previous files.
        /// </summary>
        /// <param name="update">Folders, archive and version of the update.</param>
        /// <returns>True when the shared folder was updated.</returns>
        internal static bool UpdateLinuxSharedInstallation(LinuxSharedInstallationUpdate update)
        {
            var sharedDir = update.SharedInstallDir;
            var workDir = Path.Combine(sharedDir, LinuxSharedWorkFolderName);
            var backupDir = Path.Combine(workDir, "backup");
            var replaced = new List<SharedFileBackup>();
            try
            {
                // A work folder that an interrupted run left behind holds nothing the folder still needs: every file
                // it replaced exists in the shared folder, and this run replaces all of them again.
                Log($"Updating the shared installation folder: {sharedDir}");
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, recursive: true);
                }

                Directory.CreateDirectory(backupDir);

                // Write the new files into the work folder, in the order in which they replace the old ones.
                var fileNames = new List<string>();
                StageLinuxSharedFile(
                    Path.Combine(update.StagingPath, LinuxLauncherFileName),
                    workDir,
                    LinuxLauncherFileName,
                    LinuxExecutableMode,
                    fileNames);
                StageLinuxSharedFile(update.ArchivePath, workDir, LinuxBaseArchiveFileName, LinuxRegularFileMode, fileNames);
                var icon = Path.Combine(update.StagingPath, LinuxIconPayloadPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(icon))
                {
                    StageLinuxSharedFile(icon, workDir, LinuxIconFileName, LinuxRegularFileMode, fileNames);
                }

                WriteLinuxSharedManifest(update, workDir);
                fileNames.Add(SharedManifestFileName);

                // Rename each new file over the old one; the old one stays in the backup folder until the end.
                foreach (var fileName in fileNames)
                {
                    ReplaceLinuxSharedFile(
                        Path.Combine(workDir, fileName),
                        Path.Combine(sharedDir, fileName),
                        Path.Combine(backupDir, fileName),
                        replaced);
                }
            }
            catch (Exception ex)
            {
                // Put the previous files back; the work folder stays when that fails, because it holds their only copy.
                Log("Failed to update the shared installation folder: " + ex);
                if (RestoreLinuxSharedFiles(replaced))
                {
                    TryDeleteDirectory(workDir);
                }
                else
                {
                    Log($"The previous shared files are kept in {backupDir}.");
                }

                return false;
            }

            TryDeleteDirectory(workDir);
            Log($"Shared installation folder updated to {update.Version}.");
            return true;
        }

        /// <summary>
        /// Copies one new file into the work folder with the given Unix mode and adds it to the files to replace.
        /// </summary>
        private static void StageLinuxSharedFile(
            string source,
            string workDir,
            string fileName,
            UnixFileMode mode,
            List<string> fileNames)
        {
            var staged = Path.Combine(workDir, fileName);
            File.Copy(source, staged);
            SetLinuxSharedFileMode(staged, mode);
            fileNames.Add(fileName);
        }

        /// <summary>
        /// Writes the new install manifest into the work folder: the current manifest, or a new one for a folder that
        /// an installer without a manifest set up, with the version of the update.
        /// </summary>
        private static void WriteLinuxSharedManifest(LinuxSharedInstallationUpdate update, string workDir)
        {
            var currentPath = Path.Combine(update.SharedInstallDir, SharedManifestFileName);
            var manifest = File.Exists(currentPath)
                ? JsonNode.Parse(File.ReadAllText(currentPath)) as JsonObject
                    ?? throw new InvalidDataException("The shared install manifest is not a JSON object.")
                : new JsonObject { ["app"] = "ASLM", ["sharedInstallPath"] = update.SharedInstallDir };
            manifest["version"] = update.Version;

            var staged = Path.Combine(workDir, SharedManifestFileName);
            File.WriteAllText(staged, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            SetLinuxSharedFileMode(staged, LinuxRegularFileMode);
        }

        /// <summary>
        /// Sets the Unix mode of a file for the shared folder; on Windows, which has no Unix modes, the file keeps its
        /// default access.
        /// </summary>
        private static void SetLinuxSharedFileMode(string path, UnixFileMode mode)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, mode);
            }
        }

        /// <summary>
        /// Renames a new file over a file of the shared folder and records how to undo that. File.Replace keeps the
        /// previous file under the backup name (on Linux as a hard link, so nothing is copied) and renames the new
        /// file over it at once; a file that did not exist is simply moved in.
        /// </summary>
        private static void ReplaceLinuxSharedFile(
            string source,
            string target,
            string backup,
            List<SharedFileBackup> replaced)
        {
            if (File.Exists(target))
            {
                File.Replace(source, target, backup, ignoreMetadataErrors: true);
                replaced.Add(new SharedFileBackup(target, backup));
            }
            else
            {
                File.Move(source, target);
                replaced.Add(new SharedFileBackup(target, null));
            }
        }

        /// <summary>
        /// Undoes the recorded replacements in reverse order: each backup is renamed back over the new file, and a file
        /// that did not exist before is deleted.
        /// </summary>
        /// <returns>True when every file was restored.</returns>
        private static bool RestoreLinuxSharedFiles(List<SharedFileBackup> replaced)
        {
            var restored = true;
            foreach (var backup in replaced.AsEnumerable().Reverse())
            {
                try
                {
                    if (backup.BackupPath is null)
                    {
                        File.Delete(backup.TargetPath);
                    }
                    else
                    {
                        File.Move(backup.BackupPath, backup.TargetPath, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log($"Failed to restore {backup.TargetPath}: {ex.Message}");
                    restored = false;
                }
            }

            return restored;
        }


        // Elevation

        /// <summary>
        /// Hands the update to an elevated copy of this patcher when the shared installation folder takes the pending
        /// update (see <see cref="IsSharedUpdateNeeded"/>) and this process lacks administrator rights. This process
        /// then waits for the copy and restarts the launcher itself, so ASLM starts without administrator rights and
        /// as the user who started it.
        /// </summary>
        /// <param name="args">Command-line arguments of this patcher.</param>
        /// <returns>The exit code of the elevated copy, or null when this process applies the update itself because
        /// no elevation is needed or the user declined it; then only the per-user copy is updated.</returns>
        [SupportedOSPlatform("windows")]
        public static int? TryRunElevated(string[] args)
        {
            if (IsElevatedCopy(args) || IsElevated())
            {
                return null;
            }

            // Only a pending update that an existing shared folder takes needs the rights.
            var root = ResolveRoot(args);
            _logPath = Path.Combine(root, LogRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!RequiresSharedUpdate(root, args))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                Log("The shared installation folder needs administrator rights. Starting the elevated patcher...");
                using var elevatedCopy = Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    Arguments = BuildElevatedArguments(args),
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                if (elevatedCopy is null)
                {
                    return null;
                }

                // The elevated copy never restarts the launcher; it happens here, without administrator rights.
                elevatedCopy.WaitForExit();
                if (elevatedCopy.ExitCode != ExitCodeNoRelaunch)
                {
                    StartLauncher(root, args);
                }

                return elevatedCopy.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                Log("Administrator rights were not granted. Only the per-user copy is updated; the next update asks " +
                    "again.");
                return null;
            }
        }

        /// <summary>
        /// Returns whether an existing shared installation folder takes the pending update (see
        /// <see cref="IsSharedUpdateNeeded"/>). A pending update that cannot be read counts as not needing it; the
        /// regular run then reports the problem.
        /// </summary>
        internal static bool RequiresSharedUpdate(string root, string[] args)
        {
            try
            {
                var pendingPath = Path.Combine(root, PendingRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(pendingPath))
                {
                    return false;
                }

                PendingAppUpdate pending;
                using (var stream = File.OpenRead(pendingPath))
                {
                    pending = LoadPendingUpdate(stream);
                }

                if (!string.Equals(pending.Kind, "app", StringComparison.OrdinalIgnoreCase) ||
                    !Path.IsPathFullyQualified(pending.StagingPath))
                {
                    return false;
                }

                var targetRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(pending.TargetRoot) ? root : pending.TargetRoot);
                var sharedInstallDir = ResolveSharedInstallDir(root, targetRoot, args);
                return sharedInstallDir is not null && IsSharedUpdateNeeded(sharedInstallDir, pending.Version, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Returns whether this process is the elevated copy that another patcher started; only control arguments
        /// before <c>--</c> count.
        /// </summary>
        private static bool IsElevatedCopy(string[] args)
        {
            var separator = Array.IndexOf(args, "--");
            var controlArgs = separator < 0 ? args : args[..separator];
            return controlArgs.Contains(ElevatedArgument, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the command line of the elevated copy: the same arguments with the elevated marker added to the
        /// control arguments before <c>--</c>.
        /// </summary>
        internal static string BuildElevatedArguments(string[] args)
        {
            var separator = Array.IndexOf(args, "--");
            var controlArgs = separator < 0 ? args : args[..separator];
            var forwardedArgs = separator < 0 ? Array.Empty<string>() : args[separator..];
            string[] arguments = [.. controlArgs, ElevatedArgument, .. forwardedArgs];
            return string.Join(" ", arguments.Select(QuoteArgument));
        }

        /// <summary>
        /// Quotes one argument with the Windows command-line rules, so the started process reads it back unchanged.
        /// </summary>
        internal static string QuoteArgument(string value)
        {
            // An empty argument still occupies a token; text without whitespace or quotes needs no quoting.
            if (value.Length == 0)
            {
                return "\"\"";
            }

            if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            {
                return value;
            }

            // Backslashes are literal unless they precede a quote, so they are written once the next character is
            // known: before a quote they are doubled and the quote is escaped.
            var builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            var backslashCount = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashCount++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashCount * 2 + 1);
                    builder.Append('"');
                    backslashCount = 0;
                    continue;
                }

                builder.Append('\\', backslashCount);
                backslashCount = 0;
                builder.Append(character);
            }

            // Trailing backslashes would escape the closing quote, so they are doubled.
            builder.Append('\\', backslashCount * 2);
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Returns whether this process runs with administrator rights.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Returns whether two paths name the same location, using the platform's case rules.
        /// </summary>
        private static bool IsSamePath(string? left, string? right)
        {
            return !string.IsNullOrWhiteSpace(left) &&
                   !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                       Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                       PathComparison);
        }


        // Cleanup

        /// <summary>
        /// Deletes one file on a best-effort basis.
        /// </summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file does no harm; the next update overwrites it and the uninstaller removes it.
            }
        }

        /// <summary>
        /// Deletes one directory on a best-effort basis.
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
                // Best effort cleanup only.
            }
        }

        /// <summary>
        /// Removes the Unix helper's temporary shadow copy without ever deleting an installed helper directory.
        /// </summary>
        private static void TryDeleteShadowDirectory(string root)
        {
            // Only Unix systems allow removing the directory of a running helper; on Windows nothing is deleted here.
            if (OperatingSystem.IsWindows()) return;

            // Recognize the copy names created by the launcher ("Patcher_") and by the macOS relauncher
            // ("ASLM-Patcher-").
            var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var name = Path.GetFileName(directory);
            if (IsLinuxHelperDirectory(directory, root))
            {
                TryDeleteDirectory(directory);
                return;
            }
            var prefix = name.StartsWith("Patcher_", StringComparison.Ordinal) ? "Patcher_" : "ASLM-Patcher-";

            // Unix allows unlinking the running helper. Its parent and generated name must match our launch protocol.
            if (name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _) &&
                string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), PathComparison) &&
                new DirectoryInfo(directory).LinkTarget == null)
                TryDeleteDirectory(directory);
        }

        /// <summary>
        /// Recognizes only a generated Linux helper directly below this profile's protected update directory.
        /// </summary>
        internal static bool IsLinuxHelperDirectory(string directory, string root) =>
            Guid.TryParseExact(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), "N", out _) &&
            string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)),
                Path.Combine(Path.GetFullPath(root), ".aslm-update", "helpers"), PathComparison) &&
            new DirectoryInfo(directory).LinkTarget == null;

        /// <summary>
        /// Renames the pending update file after a fatal error so the launcher does not re-enter the same failing patch
        /// loop.
        /// </summary>
        private static void MarkPendingUpdateFailed(string root)
        {
            try
            {
                var pendingPath = Path.Combine(root, PendingRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(pendingPath))
                {
                    return;
                }

                // Keep the failed descriptor with a timestamp for diagnosis instead of deleting it.
                var failedPath = pendingPath + ".failed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                File.Move(pendingPath, failedPath, overwrite: true);
                Log("Moved failed pending update to: " + failedPath);
            }
            catch (Exception ex)
            {
                Log("Failed to mark pending update as failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Writes the installed GitHub release tag into the preserved application settings after a successful patch.
        /// </summary>
        private static void PersistInstalledReleaseTag(string root, string releaseTag)
        {
            if (string.IsNullOrWhiteSpace(releaseTag))
            {
                return;
            }

            // The settings file is preserved by the update, so it still exists at its usual location.
            var appDataPath = Path.Combine(root, "Data", "App", "ASLM_Data.json");
            if (!File.Exists(appDataPath))
            {
                Log("ASLM_Data.json was not found while persisting the installed release tag.");
                return;
            }

            // Merge the installed tag into the persisted updates section.
            var rootNode = JsonNode.Parse(File.ReadAllText(appDataPath)) as JsonObject ?? new JsonObject();
            var updatesNode = rootNode["updates"] as JsonObject ?? new JsonObject();

            updatesNode["installedReleaseTag"] = releaseTag.Trim();
            rootNode["updates"] = updatesNode;

            File.WriteAllText(
                appDataPath,
                rootNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log($"Persisted installed release tag: {releaseTag}");
        }


        // Logging

        /// <summary>
        /// Appends one timestamped message to the patcher log, or writes it without a timestamp to the log writer of
        /// the shared folder helper, whose starting patcher adds its own.
        /// </summary>
        private static void Log(string message)
        {
            // Show the message in the UI first, then write it to the log.
            _progress?.Report(new PatcherProgress(message));

            lock (LogLock)
            {
                try
                {
                    if (_logWriter is not null)
                    {
                        _logWriter.WriteLine(message);
                        _logWriter.Flush();
                    }
                    else
                    {
                        File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
                    }
                }
                catch
                {
                    // Ignore logging errors to avoid masking the primary failure.
                }
            }
        }


        // Pending update payload

        /// <summary>
        /// Represents the pending ASLM self-update payload written by the main application.
        /// </summary>
        private sealed class PendingAppUpdate
        {
            /// <summary>
            /// Kind of update; only <c>app</c> is supported.
            /// </summary>
            [JsonPropertyName("kind")]
            public string Kind { get; set; } = "app";

            /// <summary>
            /// Release tag of the update, recorded as the installed release after a successful patch.
            /// </summary>
            [JsonPropertyName("version")]
            public string Version { get; set; } = string.Empty;

            /// <summary>
            /// Absolute path of the directory that holds the new application files.
            /// </summary>
            [JsonPropertyName("stagingPath")]
            public string StagingPath { get; set; } = string.Empty;

            /// <summary>
            /// Directory whose files are replaced; the data root is used when it is empty.
            /// </summary>
            [JsonPropertyName("targetRoot")]
            public string TargetRoot { get; set; } = string.Empty;

            /// <summary>
            /// Directory that receives the backup of the replaced files; a timestamped folder under the updater's
            /// backup folder is used when it is empty.
            /// </summary>
            [JsonPropertyName("backupPath")]
            public string BackupPath { get; set; } = string.Empty;

            /// <summary>
            /// Relative paths or per-segment wildcard patterns (such as user data) that the update must not touch.
            /// </summary>
            [JsonPropertyName("preserve")]
            public List<string> Preserve { get; set; } = [];

            /// <summary>
            /// Downloaded update archive below the staging folder, which becomes the payload archive of the shared
            /// installation folder on Windows and Linux; empty in descriptors of older versions.
            /// </summary>
            [JsonPropertyName("archivePath")]
            public string ArchivePath { get; set; } = string.Empty;

            /// <summary>
            /// Normalizes persisted string and collection values after deserialization.
            /// </summary>
            public void Normalize()
            {
                Kind = string.IsNullOrWhiteSpace(Kind) ? "app" : Kind.Trim();
                Version ??= string.Empty;
                StagingPath ??= string.Empty;
                TargetRoot ??= string.Empty;
                BackupPath ??= string.Empty;
                Preserve ??= [];
                ArchivePath ??= string.Empty;
            }
        }


        // Shared installation records

        /// <summary>
        /// Describes one update of the shared installation folder.
        /// </summary>
        /// <param name="SharedInstallDir">Shared folder that holds the launcher, the uninstaller and the payload
        /// archive.</param>
        /// <param name="StagingPath">Directory that holds the new files of the update.</param>
        /// <param name="ArchivePath">Downloaded update archive that becomes the payload archive, or null.</param>
        /// <param name="BackupPath">Fresh directory that receives the previous shared files.</param>
        /// <param name="Version">Four-part version of the release, which the manifest and the entry in the installed
        /// applications record.</param>
        /// <param name="UserAppDir">Per-user folder whose program folders count towards the registered size.</param>
        internal sealed record SharedInstallationUpdate(
            string SharedInstallDir,
            string StagingPath,
            string? ArchivePath,
            string BackupPath,
            string Version,
            string UserAppDir);

        /// <summary>
        /// Describes one update of the Linux shared installation folder, which the shared folder helper applies.
        /// </summary>
        /// <param name="SharedInstallDir">Shared folder that holds the launcher, the base archive, the icon and the
        /// install manifest.</param>
        /// <param name="StagingPath">Directory that holds the new files of the update.</param>
        /// <param name="ArchivePath">Downloaded release archive, which becomes the base archive.</param>
        /// <param name="Version">Four-part version of the release, which the install manifest records.</param>
        internal sealed record LinuxSharedInstallationUpdate(
            string SharedInstallDir,
            string StagingPath,
            string ArchivePath,
            string Version);

        /// <summary>
        /// Records how to undo the replacement of one shared file.
        /// </summary>
        /// <param name="TargetPath">Replaced file in the shared folder.</param>
        /// <param name="BackupPath">Copy of the previous file, or null when the file did not exist before.</param>
        private sealed record SharedFileBackup(string TargetPath, string? BackupPath);
    }
}
