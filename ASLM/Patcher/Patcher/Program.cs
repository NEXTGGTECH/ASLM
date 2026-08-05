// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
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
    private const string LauncherExeName = "ASLM.exe";
    private const string LauncherRefFileName = "launcher-ref.json";
    private const string WaitProcessArgument = "--wait-process";
    private const string LauncherArgument = "--launcher";

    // Paths and runtime state

    private static string _logPath = string.Empty;
    private static IProgress<PatcherProgress>? _progress;


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

        try
        {
            // Prepare the patcher log directory and wait for the launcher to exit.
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            Log("Patcher started.");
            WaitForRequestedProcessExit(args);

            // Stop when there is nothing left to apply.
            var pendingPath = Path.Combine(root, PendingRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(pendingPath))
            {
                Log("No pending update found.");
                StartLauncher(root, args);
                return 0;
            }

            // Read and validate the pending update manifest.
            var pending = LoadPendingUpdate(pendingPath);
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

            // Replace application files with backup and rollback support.
            try
            {
                Log($"Target root: {targetRoot}");
                Log($"Staging path: {stagingPath}");

                Directory.CreateDirectory(backupPath);

                Log("Creating backup of replaceable application files...");
                CopyDirectory(targetRoot, backupPath, relativePath => ShouldReplacePath(relativePath, preservePatterns));

                Log("Replacing application files...");
                ClearDirectory(targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));
                CopyDirectory(stagingPath, targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));

                // Record the installed release and remove temporary update folders.
                // The data root holds ASLM_Data.json on every platform (targetRoot is the
                // .app bundle on macOS, where no data lives).
                PersistInstalledReleaseTag(root, pending.Version);
                File.Delete(pendingPath);
                TryDeleteDirectory(Path.GetDirectoryName(stagingPath) ?? stagingPath);
                TryDeleteDirectory(backupPath);
                Log($"Update {pending.Version} applied successfully.");
                StartLauncher(root, args);
                return 0;
            }
            catch
            {
                Log("Patch failed. Attempting rollback...");

                // Restore files from the backup when replacement fails.
                if (Directory.Exists(backupPath))
                {
                    ClearDirectory(targetRoot, relativePath => ShouldReplacePath(relativePath, preservePatterns));
                    CopyDirectory(backupPath, targetRoot);
                }

                throw;
            }
        }
        catch (Exception ex)
        {
            // Keep the launcher usable after a fatal patcher error.
            Log("Fatal patcher error: " + ex);
            MarkPendingUpdateFailed(root);
            StartLauncher(root, args);
            return 1;
        }
    }


    // Pending update loading

    /// <summary>
    /// Waits for the previous ASLM process before replacing application files.
    /// </summary>
    private static void WaitForRequestedProcessExit(string[] args)
    {
        // Scan command-line arguments for the launcher process id to wait on.
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (!string.Equals(args[index], WaitProcessArgument, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(args[index + 1], out var processId))
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(processId);
                Log($"Waiting for ASLM process {processId} to exit...");
                if (!process.WaitForExit(30000))
                {
                    Log($"ASLM process {processId} did not exit within 30 seconds.");
                }
            }
            catch (ArgumentException)
            {
                // The process already exited.
            }
            catch (Exception ex)
            {
                Log("Failed while waiting for previous ASLM process: " + ex.Message);
            }

            return;
        }
    }

    /// <summary>
    /// Loads the pending update manifest from disk.
    /// </summary>
    private static PendingAppUpdate LoadPendingUpdate(string pendingPath)
    {
        var pending = JsonSerializer.Deserialize<PendingAppUpdate>(File.ReadAllText(pendingPath))
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
    }


    // Root resolution

    /// <summary>
    /// Resolves the ASLM root path either from command-line arguments or from the current executable directory.
    /// </summary>
    private static string ResolveRoot(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
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
        return normalized.Equals(".aslm-update", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(".aslm-update/", StringComparison.OrdinalIgnoreCase);
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
            return relativePath.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                   relativePath.StartsWith(pattern.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", "[^/]*", StringComparison.Ordinal) + "($|/.*)";
        return Regex.IsMatch(relativePath, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var relative = CombineRelativePath(relativeDirectory, Path.GetFileName(directory));
            if (includeRelative?.Invoke(relative) == false)
            {
                continue;
            }

            Directory.CreateDirectory(ResolveChildPath(destRoot, relative));
            CopyDirectoryContents(sourceRoot, destRoot, relative, includeRelative);
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var relative = CombineRelativePath(relativeDirectory, Path.GetFileName(file));
            if (includeRelative?.Invoke(relative) == false)
            {
                continue;
            }

            var destination = ResolveChildPath(destRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
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

        foreach (var file in Directory.EnumerateFiles(currentDirectory))
        {
            var relative = CombineRelativePath(relativeDirectory, Path.GetFileName(file));
            if (deleteRelative?.Invoke(relative) == false)
            {
                continue;
            }

            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (var subdir in Directory.EnumerateDirectories(currentDirectory))
        {
            var relative = CombineRelativePath(relativeDirectory, Path.GetFileName(subdir));
            if (deleteRelative?.Invoke(relative) == false || !Directory.Exists(subdir))
            {
                continue;
            }

            ClearDirectoryContents(root, relative, deleteRelative);
            if (!Directory.EnumerateFileSystemEntries(subdir).Any())
            {
                Directory.Delete(subdir);
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
        if (!EnsureTrailingSeparator(combined).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
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

            Process.Start(new ProcessStartInfo
            {
                FileName = launcherPath,
                WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? root,
                UseShellExecute = false
            });
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
        for (var i = 0; i < args.Length - 1; i++)
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
