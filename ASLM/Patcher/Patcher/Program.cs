// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ASLM.Patcher;

/// <summary>
/// Carries one status update from the patcher runner to the UI.
/// </summary>
internal sealed record PatcherProgress(string Message);

/// <summary>
/// Applies a pending ASLM self-update while the main launcher is not running.
/// </summary>
internal static class PatcherRunner
{
    private const string PendingRelativePath = ".aslm-update/pending.json";
    private const string LogRelativePath = ".aslm-update/logs/Patcher.log";
    private static string LauncherExeName => OperatingSystem.IsWindows() ? "ASLM.exe" : "ASLM";
    private const string LauncherRefFileName = "launcher-ref.json";
    private const string WaitProcessArgument = "--wait-process";
    private const string WaitStartArgument = "--wait-start";
    private const string LauncherArgument = "--launcher";

    // Paths and runtime state

    private static string _logPath = string.Empty;
    private static IProgress<PatcherProgress>? _progress;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;


    // Process startup

    /// <summary>
    /// Runs the patch operation on a background thread.
    /// </summary>
    public static Task<int> RunAsync(string[] args, IProgress<PatcherProgress>? progress)
    {
        _progress = progress;
        return Task.Run(() => RunCore(args));
    }

    /// <summary>
    /// Loads the pending update, applies it, and restarts the launcher.
    /// </summary>
    private static int RunCore(string[] args)
    {
        var root = ResolveRoot(args);
        _logPath = Path.Combine(root, LogRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var mayRelaunch = false;

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
                StartLauncher(root, args);
                return 0;
            }

            // Lock the existing marker rather than creating a persistent lock file. A second launcher must not
            // apply or rename the same pending update while another helper owns it; deletion remains allowed.
            mayRelaunch = false;
            var sharing = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
            using var pendingStream = new FileStream(pendingPath, FileMode.Open, FileAccess.Read, sharing);
            mayRelaunch = true;
            var pending = LoadPendingUpdate(pendingStream);
            ValidatePendingUpdate(pending);

            // Resolve target, staging, backup, and preserve paths.
            var targetRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(pending.TargetRoot) ? root : pending.TargetRoot);
            var stagingPath = Path.GetFullPath(pending.StagingPath);
            var backupPath = Path.GetFullPath(string.IsNullOrWhiteSpace(pending.BackupPath)
                ? Path.Combine(root, ".aslm-update", "backup", DateTime.UtcNow.ToString("yyyyMMddHHmmss"))
                : pending.BackupPath);
            if (!Directory.Exists(stagingPath))
            {
                throw new DirectoryNotFoundException($"Staging path does not exist: {stagingPath}");
            }

            var preservePatterns = NormalizePreservePatterns(pending.Preserve);
            ValidateUpdatePaths(root, targetRoot, stagingPath, backupPath);

            // Replace application files with backup and rollback support.
            var replacementStarted = false;
            try
            {
                Log($"Target root: {targetRoot}");
                Log($"Staging path: {stagingPath}");

                Directory.CreateDirectory(backupPath);

                Log("Creating backup of replaceable application files...");
                CopyDirectory(targetRoot, backupPath, relativePath => ShouldReplacePath(relativePath, preservePatterns));

                Log("Replacing application files...");
                replacementStarted = true;
                ClearDirectory(targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));
                CopyDirectory(stagingPath, targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));

                // Record the installed release and remove temporary update folders.
                // The data root holds ASLM_Data.json on every platform (targetRoot is the
                // .app bundle on macOS, where no data lives).
                PersistInstalledReleaseTag(root, pending.Version);
                File.Delete(pendingPath);
                TryDeleteDirectory(GetStagingDirectory(root, stagingPath));
                TryDeleteDirectory(backupPath);
                Log($"Update {pending.Version} applied successfully.");
                StartLauncher(root, args);
                return 0;
            }
            catch
            {
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
            // Never open another instance when waiting for the previous process failed.
            if (mayRelaunch)
            {
                MarkPendingUpdateFailed(root);
                StartLauncher(root, args);
            }
            return 1;
        }
        finally
        {
            TryDeleteShadowDirectory();
        }
    }


    // Pending update loading

    /// <summary>
    /// Waits for the previous ASLM process before replacing application files.
    /// </summary>
    private static void WaitForRequestedProcessExit(string[] args)
    {
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
    /// Loads the pending update manifest from disk.
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
        if (!string.Equals(pending.Kind, "app", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unsupported pending update kind: {pending.Kind}");
        }
        if (!Path.IsPathFullyQualified(pending.StagingPath))
            throw new InvalidOperationException("The staging path must be absolute.");
    }


    // Root resolution

    /// <summary>
    /// Resolves the ASLM root path either from command-line arguments or from the current executable directory.
    /// </summary>
    private static string ResolveRoot(string[] args)
    {
        for (var index = 0; index < args.Length - 1 && args[index] != "--"; index++)
        {
            if (string.Equals(args[index], "--root", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

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
        if (!pattern.Contains('*'))
        {
            return relativePath.Equals(pattern, PathComparison) ||
                   relativePath.StartsWith(pattern.TrimEnd('/') + "/", PathComparison);
        }

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
            var relative = CombineRelativePath(relativeDirectory, entry.Name);
            if (includeRelative?.Invoke(relative) == false)
            {
                continue;
            }

            var destination = ResolveChildPath(destRoot, relative);
            if (entry.LinkTarget is { } linkTarget)
            {
                // Recreate links, including dangling links, without traversing their targets during backup or apply.
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(destination, linkTarget);
                else File.CreateSymbolicLink(destination, linkTarget);
                continue;
            }

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
    /// Deletes every file inside one directory and removes empty subdirectories, optionally filtering by relative path.
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

            if (entry is DirectoryInfo directory)
            {
                ClearDirectoryContents(root, relative, deleteRelative);
                if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
            }
            else
            {
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
    private static void ValidateUpdatePaths(string root, string targetRoot, string stagingPath, string backupPath)
    {
        var stagingRoot = Path.Combine(root, ".aslm-update", "staging");
        var backupRoot = Path.Combine(root, ".aslm-update", "backup");
        if (!IsDescendant(stagingRoot, stagingPath) || !IsDescendant(backupRoot, backupPath) ||
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


    // Cleanup

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
    private static void TryDeleteShadowDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var name = Path.GetFileName(directory);
        var prefix = name.StartsWith("Patcher_", StringComparison.Ordinal) ? "Patcher_" : "ASLM-Patcher-";

        // Unix allows unlinking the running helper. Its parent and generated name must match our launch protocol.
        if (name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _) &&
            string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), PathComparison) &&
            new DirectoryInfo(directory).LinkTarget == null)
            TryDeleteDirectory(directory);
    }

    /// <summary>
    /// Renames the pending update file after a fatal error so the launcher does not re-enter the same failing patch loop.
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
    /// Appends one timestamped message to the patcher log.
    /// </summary>
    private static void Log(string message)
    {
        _progress?.Report(new PatcherProgress(message));

        try
        {
            File.AppendAllText(_logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging errors to avoid masking the primary failure.
        }
    }


    // Pending update payload

    /// <summary>
    /// Represents the pending ASLM self-update payload written by the main application.
    /// </summary>
    private sealed class PendingAppUpdate
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "app";

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("stagingPath")]
        public string StagingPath { get; set; } = string.Empty;

        [JsonPropertyName("targetRoot")]
        public string TargetRoot { get; set; } = string.Empty;

        [JsonPropertyName("backupPath")]
        public string BackupPath { get; set; } = string.Empty;

        [JsonPropertyName("preserve")]
        public List<string> Preserve { get; set; } = [];

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
        }
    }
}
