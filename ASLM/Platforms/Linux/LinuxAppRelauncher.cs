// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Relaunches the Linux host only after the previous instance has stopped its services and processes.
    /// </summary>
    internal static class LinuxAppRelauncher
    {
        // Constants

        /// <summary>
        /// Command-line switch of the internal restart handshake: the new instance waits for the previous process
        /// (given by PID and start time) to exit before it continues. It is used when no launcher performs the wait.
        /// </summary>
        private const string WaitArgument = "--aslm-wait-process";


        // Installation layout

        /// <summary>
        /// Gets the packaged installation root independently of the writable XDG profile.
        /// </summary>
        internal static string InstallDirectory => Directory.GetParent(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!.FullName;

        /// <summary>
        /// Gets whether the current installation has a launcher and CLI helper available for self-updates.
        /// </summary>
        internal static bool SupportsSelfUpdate => ResolveLauncherPath() != null &&
            File.Exists(Path.Combine(InstallDirectory, "Patcher", "aslm-patcher"));

        /// <summary>
        /// Resolves either the portable launcher or the shared launcher recorded during profile initialization.
        /// </summary>
        private static string? ResolveLauncherPath()
        {
            // The running launcher identifies itself; direct portable runs still prefer their adjacent executable.
            var inherited = Environment.GetEnvironmentVariable("ASLM_LAUNCHER_PATH");
            if (!string.IsNullOrEmpty(inherited) && Path.IsPathFullyQualified(inherited) && File.Exists(inherited))
                return inherited;
            var adjacent = Path.Combine(InstallDirectory, "ASLM");
            if (File.Exists(adjacent)) return adjacent;

            // Fall back to the launcher path that profile initialization recorded for shared installations.
            try
            {
                using var reference = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppRoot.Directory, "launcher-ref.json")));
                var launcher = reference.RootElement.GetProperty("launcherPath").GetString();
                if (!string.IsNullOrEmpty(launcher) && Path.IsPathFullyQualified(launcher) && File.Exists(launcher))
                    return launcher;
            }
            catch
            {
                // Missing or stale references are expected in development runs and after removal of the shared installation.
            }

            return null;
        }


        // Update preservation

        /// <summary>
        /// Keeps the shared launcher reference without installing a second, portable launcher into the user profile.
        /// </summary>
        internal static List<string> GetUpdatePreservePaths(IEnumerable<string> configuredPaths)
        {
            var launcher = ResolveLauncherPath();
            if (launcher == null || string.Equals(Path.GetDirectoryName(launcher), InstallDirectory, StringComparison.Ordinal))
                return configuredPaths.ToList();

            // Portable archives contain a root launcher; installed builds continue using the system copy after updates.
            return configuredPaths.Concat(["ASLM", "launcher-ref.json"]).Distinct(StringComparer.Ordinal).ToList();
        }


        // Relaunch

        /// <summary>
        /// Starts a waiting instance without a shell, preserving paths containing spaces or non-ASCII characters.
        /// </summary>
        internal static void StartDetachedRelaunch()
        {
            // Prefer the launcher; otherwise restart the current executable directly.
            var launcher = ResolveLauncherPath();
            var hasLauncher = launcher != null;
            var executable = launcher ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("ASLM executable was not found.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = hasLauncher ? Path.GetDirectoryName(launcher)! : AppContext.BaseDirectory
            };

            // Development runs without a packaged launcher retain their direct, framework-dependent restart path.
            if (!hasLauncher && string.Equals(Path.GetFileName(executable), "dotnet", StringComparison.Ordinal))
                start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);

            // Pass this process's PID and start time so the new instance waits for exactly this process to exit
            // (the start time protects against PID reuse, see WaitForPreviousInstance).
            using var current = Process.GetCurrentProcess();
            start.ArgumentList.Add(hasLauncher ? "--wait-process" : WaitArgument);
            start.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
            if (hasLauncher) start.ArgumentList.Add("--wait-start");
            start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            using var child = Process.Start(start) ?? throw new InvalidOperationException("ASLM could not be restarted.");
        }

        /// <summary>
        /// Hands a staged update to the launcher before a direct App/ASLM launch constructs GTK or starts modules.
        /// </summary>
        internal static bool TryHandOffPendingUpdate()
        {
            // The launcher already attempted the gate. Do not loop if its helper could not start and it opened this app instead.
            if (string.Equals(Environment.GetEnvironmentVariable("ASLM_LAUNCHER_PATH"),
                    ResolveLauncherPath(), StringComparison.Ordinal)) return false;
            if (!SupportsSelfUpdate || !File.Exists(Path.Combine(AppRoot.Directory, ".aslm-update", "pending.json")))
                return false;

            try
            {
                StartDetachedRelaunch();
                return true;
            }
            catch (Exception ex)
            {
                // A missing or non-executable helper must not prevent a user from opening the current application.
                Console.Error.WriteLine($"[ASLM] Could not start the pending update: {ex.Message}");
                return false;
            }
        }


        // Update validation

        /// <summary>
        /// Fails before downloading when the current user cannot replace the installed application.
        /// </summary>
        internal static void EnsureUpdateTargetWritable()
        {
            // Probe the program directory in either layout; never elevate the application or patcher for an update.
            using var probe = new FileStream(Path.Combine(InstallDirectory, ".aslm-update-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }

        /// <summary>
        /// Rejects incomplete release archives before their paths can be handed to the external patcher.
        /// </summary>
        internal static void ValidateUpdatePayload(string payloadDirectory)
        {
            // All three executables are one update unit; an App-only archive would remove the updater on restart.
            var executables = new[] { "ASLM", "App/ASLM", "Patcher/aslm-patcher" };
            foreach (var relative in executables.Concat(["App/libcoreclr.so", "App/libhostfxr.so", "App/libhostpolicy.so"]))
                if (!File.Exists(Path.Combine(payloadDirectory, relative)))
                    throw new InvalidOperationException($"The ASLM update archive is incomplete: {relative}");

            // Existence is not enough on Unix: a package that lost its executable bits cannot relaunch after replacement.
            if (!OperatingSystem.IsLinux()) return;
            foreach (var relative in executables)
                if ((File.GetUnixFileMode(Path.Combine(payloadDirectory, relative)) &
                     (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    throw new InvalidOperationException($"The ASLM update executable has no execute permission: {relative}");
        }


        // Restart handshake

        /// <summary>
        /// Consumes the internal restart handshake before GTK parses application arguments.
        /// Returns true when startup may continue, and false when the handshake is malformed or the previous
        /// instance did not exit in time.
        /// </summary>
        internal static bool WaitForPreviousInstance(ref string[] args)
        {
            // Normal start: no handshake argument, nothing to wait for.
            if (args.Length == 0 || args[0] != WaitArgument) return true;

            // Reject a malformed handshake, then clear the arguments so GTK does not see the internal switch.
            if (args.Length != 3 || !int.TryParse(args[1], out var pid) || pid <= 0 ||
                !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return false;
            args = [];
            try
            {
                // A PID may have been reused while the new process started; never wait for an unrelated process.
                using var previous = Process.GetProcessById(pid);
                if (previous.StartTime.ToUniversalTime().Ticks != started) return true;
                return previous.WaitForExit(60_000);
            }
            catch (ArgumentException)
            {
                // No process with this PID exists any more, so the previous instance is already gone.
                return true;
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
                return true;
            }
        }
    }
}
