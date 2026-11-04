// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Globalization;

/// <summary>
/// Launches the main ASLM application and logs startup errors.
/// </summary>
/// <remarks>
/// Two layouts are supported:
///
/// Monolithic (Debug):
///   The Launcher executable sits next to App\, Patcher\, Data\, etc. in one directory.
///   This layout is detected automatically and behaves exactly as before.
///
/// Dual-location (Release):
///   The Launcher sits in a shared installation directory alongside aslm-base.zip.
///   The application itself (App\, Patcher\, Data\, …) lives in the per-user
///   %LOCALAPPDATA%\ASLM directory. If that directory is not yet populated the
///   Launcher bootstraps it from aslm-base.zip before starting the application.
///
/// Linux installed builds use aslm-base.tar.gz to initialize App/ and Patcher/ in the XDG profile.
/// Portable Linux builds retain App/ beside the Launcher with a separate XDG data directory.
/// </remarks>
internal static class Program
{
    // Constants and names

    /// <summary>
    /// Name of the folder inside the application root that holds the main ASLM executable.
    /// </summary>
    private const string AppFolderName = "App";

    /// <summary>
    /// Platform-specific file name of the ASLM executable (the launcher and the application share it).
    /// </summary>
    private static string ExeName => AppPaths.ExecutableName;

    /// <summary>
    /// File name of the launcher diagnostic log.
    /// </summary>
    private const string LogFileName = "Launcher.log";

    /// <summary>
    /// Name of the folder inside the application root that holds the patcher helper.
    /// </summary>
    private const string PatcherFolderName = "Patcher";

    /// <summary>
    /// Candidate patcher executable names for the current platform, in order of preference.
    /// </summary>
    private static readonly IReadOnlyList<string> PatcherExeNames = OperatingSystem.IsLinux()
        ? ["aslm-patcher"]
        : ["ASLM Patcher.exe", "Patcher.exe"];

    /// <summary>
    /// Location of the pending update descriptor relative to the data root; its presence means that an update
    /// must be applied.
    /// </summary>
    private static readonly string PendingUpdateRelativePath = Path.Combine(".aslm-update", "pending.json");

    /// <summary>
    /// Argument naming the process id the launcher must wait for before it continues.
    /// </summary>
    private const string WaitProcessArgument = "--wait-process";

    /// <summary>
    /// Argument carrying the start time (UTC ticks) of the awaited process, used to detect a reused process id.
    /// </summary>
    private const string WaitStartArgument = "--wait-start";

    /// <summary>
    /// Patcher argument that passes the path of the launcher executable to restart after the update.
    /// </summary>
    private const string LauncherArgument = "--launcher";


    // Process startup

    /// <summary>
    /// Resolves the target path, forwards arguments and starts the main process.
    /// </summary>
    private static void Main(string[] args)
    {
        // Locate the shared install directory and the log. On Linux the log is kept in the per-user profile
        // instead of next to the launcher.
        var sharedInstallDir = AppPaths.GetSharedInstallDir();
        var logPath = OperatingSystem.IsLinux()
            ? Path.Combine(AppPaths.GetUserAppDir(), ".aslm-update", "logs", LogFileName)
            : Path.Combine(sharedInstallDir, LogFileName);

        try
        {
            // When the patcher restarted this launcher, let the previous ASLM process finish exiting first.
            WaitForRequestedProcessExit(args, logPath);

            // Decide where the application lives and make sure it exists (bootstrapping it if necessary).
            string appRoot;

            if (AppPaths.IsMonolithicLayout(sharedInstallDir))
            {
                // Debug / monolithic layout: everything lives next to the Launcher.
                appRoot = sharedInstallDir;
            }
            else
            {
                // Release / dual-location layout: application lives in the per-user directory.
                appRoot = AppPaths.GetUserAppDir();

                if (!UserBootstrapper.IsUserAppDirReady(appRoot))
                {
                    Log($"User application directory not found. Bootstrapping: {appRoot}", logPath);
                    if (!UserBootstrapper.TryBootstrap(sharedInstallDir, appRoot, logPath))
                    {
                        Log("Bootstrap failed. Cannot start ASLM.", logPath);
                        Environment.Exit(1);
                        return;
                    }
                }

                // Always refresh the launcher reference so the Patcher can restart us.
                var launcherExePath = Path.Combine(sharedInstallDir, ExeName);
                UserBootstrapper.WriteLauncherRef(launcherExePath, appRoot);
            }

            // A pending update takes over: the patcher replaces the application and restarts this launcher.
            // On Linux the writable data (and the pending descriptor) lives in the user profile even when
            // the application itself is portable.
            var dataRoot = OperatingSystem.IsLinux() ? AppPaths.GetUserAppDir() : appRoot;
            if (TryStartPendingPatcher(sharedInstallDir, appRoot, dataRoot, args, logPath))
            {
                return;
            }

            // No update to apply: start the application itself.
            StartApp(appRoot, args, logPath);
        }
        catch (Exception ex)
        {
            Log($"Critical Error: {ex.Message}\nStack Trace: {ex.StackTrace}", logPath);
            Environment.Exit(1);
        }
    }


    // Application startup

    /// <summary>
    /// Starts the main ASLM application from the resolved application root directory.
    /// </summary>
    private static void StartApp(string appRoot, string[] args, string logPath)
    {
        // Resolve the executable and stop with a logged error when the installation is incomplete.
        var targetPath = Path.Combine(appRoot, AppFolderName, ExeName);

        if (!File.Exists(targetPath))
        {
            Log("Error: Target executable not found at " + targetPath, logPath);
            Environment.Exit(1);
            return;
        }

        // Run from the App folder; on Linux tell the application where this launcher is so it can restart it.
        var startInfo = new ProcessStartInfo
        {
            FileName = targetPath,
            WorkingDirectory = Path.Combine(appRoot, AppFolderName),
            UseShellExecute = false
        };
        if (OperatingSystem.IsLinux())
            startInfo.Environment["ASLM_LAUNCHER_PATH"] = Path.Combine(AppPaths.GetSharedInstallDir(), ExeName);

        // Pass the user's arguments through without the launcher-only restart arguments.
        AppendForwardedArguments(startInfo, args);

        var process = Process.Start(startInfo);
        if (process == null)
        {
            Log("Error: Failed to start the process (Process.Start returned null).", logPath);
            Environment.Exit(1);
        }
    }


    // Patcher startup

    /// <summary>
    /// Starts the external patcher from a temporary shadow copy when a self-update is pending.
    /// </summary>
    /// <param name="sharedInstallDir">The shared installation directory (contains the Launcher).</param>
    /// <param name="appRoot">The application root containing App/ and Patcher/.</param>
    /// <param name="dataRoot">The writable profile containing the pending update.</param>
    /// <param name="args">Arguments forwarded after the update.</param>
    /// <param name="logPath">The launcher diagnostic log.</param>
    /// <returns>True when the patcher was started and this launcher must exit; false when the application should
    /// start normally.</returns>
    internal static bool TryStartPendingPatcher(string sharedInstallDir, string appRoot, string dataRoot,
        string[] args, string logPath)
    {
        // Nothing to do unless an update was staged and left a pending descriptor.
        var pendingPath = Path.Combine(dataRoot, PendingUpdateRelativePath);
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        // Without a patcher the update cannot be applied; the application then starts as it is.
        var patcherDir = Path.Combine(appRoot, PatcherFolderName);
        var patcherExeName = ResolvePatcherExecutableName(patcherDir);
        if (string.IsNullOrWhiteSpace(patcherExeName))
        {
            Log(
                "Pending update found, but patcher is missing. Checked: " +
                string.Join(", ", PatcherExeNames.Select(name => Path.Combine(patcherDir, name))),
                logPath);
            return false;
        }

        string? shadowDir = null;
        try
        {
            // Run the patcher from a temporary copy so it can overwrite the Patcher folder it was started from.
            shadowDir = Path.Combine(Path.GetTempPath(), "Patcher_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(shadowDir);

            CopyDirectory(patcherDir, shadowDir);

            var shadowPatcher = Path.Combine(shadowDir, patcherExeName);
            var launcherExePath = Path.Combine(sharedInstallDir, ExeName);

            // Tell the patcher which profile to update and which launcher to restart afterwards.
            var startInfo = new ProcessStartInfo
            {
                FileName = shadowPatcher,
                WorkingDirectory = shadowDir,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(dataRoot);
            startInfo.ArgumentList.Add(LauncherArgument);
            startInfo.ArgumentList.Add(launcherExePath);

            // The helper must outlive this launcher before replacing its executable as part of the payload.
            // The start time guards against waiting for another process that reuses this process id.
            using var current = Process.GetCurrentProcess();
            startInfo.ArgumentList.Add(WaitProcessArgument);
            startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(WaitStartArgument);
            startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--");

            // Everything after "--" is handed back to the restarted launcher unchanged.
            AppendForwardedArguments(startInfo, args);

            // Start the helper; this launcher then exits so its own files can be replaced.
            if (Process.Start(startInfo) == null)
            {
                throw new InvalidOperationException("Failed to start patcher (Process.Start returned null).");
            }

            Log("Pending update detected. Handed control to patcher.", logPath);
            return true;
        }
        catch (Exception ex)
        {
            // No helper owns the shadow copy when process creation failed.
            if (shadowDir != null)
            {
                try { Directory.Delete(shadowDir, recursive: true); }
                catch { /* Preserve the original startup error when temporary cleanup fails. */ }
            }

            // Fall back to a normal application start instead of blocking the user on a failed update hand-off.
            Log("Failed to start patcher: " + ex, logPath);
            return false;
        }
    }


    // Process wait

    /// <summary>
    /// Waits for the previous ASLM process to exit before applying a pending update.
    /// </summary>
    internal static void WaitForRequestedProcessExit(string[] args, string logPath)
    {
        // Nothing to wait for unless the launcher was restarted with a process id.
        var index = Array.IndexOf(args, WaitProcessArgument);
        if (index < 0) return;
        if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out var processId) || processId <= 0)
            throw new ArgumentException("Invalid restart process id.");

        // A restarted process must never wait for an unrelated process that reused the original PID.
        long? started = null;
        var startIndex = Array.IndexOf(args, WaitStartArgument);
        if (startIndex >= 0)
        {
            if (startIndex + 1 >= args.Length || !long.TryParse(args[startIndex + 1],
                    NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                throw new ArgumentException("Invalid restart process start time.");
            started = ticks;
        }

        // Wait up to a minute; a different start time means the process id now belongs to someone else,
        // and a process that is already gone needs no waiting.
        try
        {
            using var process = Process.GetProcessById(processId);
            if (started.HasValue && process.StartTime.ToUniversalTime().Ticks != started.Value) return;
            Log($"Waiting for ASLM process {processId} to exit before restart.", logPath);
            if (!process.WaitForExit(60000))
                throw new TimeoutException($"ASLM process {processId} did not exit within 60 seconds.");
        }
        catch (ArgumentException) { /* The process already exited. */ }
        catch (InvalidOperationException) { /* The process exited before its start time could be read. */ }
    }


    // Argument forwarding

    /// <summary>
    /// Forwards user arguments to ASLM while removing launcher-only restart arguments.
    /// </summary>
    internal static void AppendForwardedArguments(ProcessStartInfo startInfo, string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            // Skip each restart argument together with its value.
            if (string.Equals(args[index], WaitProcessArgument, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args[index], WaitStartArgument, StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            startInfo.ArgumentList.Add(args[index]);
        }
    }


    // Patcher helpers

    /// <summary>
    /// Returns the available patcher executable name from the known candidates.
    /// </summary>
    private static string? ResolvePatcherExecutableName(string patcherDir)
    {
        foreach (var candidate in PatcherExeNames)
        {
            if (File.Exists(Path.Combine(patcherDir, candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the complete patcher folder into a temporary shadow directory before replacement starts.
    /// </summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        // Recreate the folder structure first, including empty folders.
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destDir, relative));
        }

        // Copy the files, preserving Unix permissions so the copied patcher stays executable.
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(file));
        }
    }


    // Logging

    /// <summary>
    /// Appends a timestamped message to the launcher log.
    /// </summary>
    private static void Log(string message, string logPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, logEntry);
        }
        catch
        {
            // Ignore logging failures to avoid blocking the launcher.
        }
    }
}
