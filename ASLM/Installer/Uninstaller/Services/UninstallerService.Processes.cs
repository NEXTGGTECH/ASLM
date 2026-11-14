// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ASLM.Installer.Uninstaller
{
    /// <summary>
    /// Process part of the uninstaller service: finds running programs that hold files of the installation and
    /// closes them on request.
    /// </summary>
    public sealed partial class UninstallerService
    {
        // Process constants

        /// <summary>
        /// Access right that is enough to read the executable path of a process of another user or integrity level
        /// (<c>PROCESS_QUERY_LIMITED_INFORMATION</c>).
        /// </summary>
        private const uint ProcessQueryLimitedInformation = 0x1000;

        /// <summary>
        /// Time a terminated process gets to finish exiting before the next one is handled.
        /// </summary>
        private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(5);


        // Running processes

        /// <summary>
        /// Returns the processes whose executable lies in a folder that the plan removes, including the patchers that
        /// the Launcher runs from temporary copies.
        /// </summary>
        /// <param name="plan">Plan returned by <see cref="CreatePlan"/>.</param>
        /// <returns>The blocking processes ordered by name; empty when nothing holds the installation.</returns>
        public IReadOnlyList<BlockingProcess> FindBlockingProcesses(UninstallPlan plan)
        {
            var roots = GetProcessRoots(plan);
            var blocking = new List<BlockingProcess>();

            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    // The uninstaller itself may run from a removed folder; it stays out of the list.
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    // The roots are normalized, so the executable path is brought into the same form first.
                    var executablePath = TryGetExecutablePath(process.Id);
                    if (executablePath is null)
                    {
                        continue;
                    }

                    executablePath = NormalizePath(executablePath);
                    if (roots.Any(root => IsSameOrChildPath(root, executablePath)))
                    {
                        blocking.Add(new BlockingProcess(process.Id, Path.GetFileName(executablePath), executablePath));
                    }
                }
            }

            return blocking
                .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.Id)
                .ToList();
        }

        /// <summary>
        /// Asks the processes to close, waits for the grace period, and terminates the ones that still run together
        /// with their child processes. Processes of other users may refuse to stop, so the caller checks again with
        /// <see cref="FindBlockingProcesses"/>.
        /// </summary>
        /// <param name="processes">Processes returned by <see cref="FindBlockingProcesses"/>.</param>
        /// <param name="gracePeriod">Time all processes together get to close on their own.</param>
        public void CloseProcesses(IReadOnlyList<BlockingProcess> processes, TimeSpan gracePeriod)
        {
            var opened = new List<Process>();
            try
            {
                // Ask every process to close first so ASLM can save its state; a reused id is skipped.
                foreach (var blocking in processes)
                {
                    var process = TryOpenSameProcess(blocking);
                    if (process is null)
                    {
                        continue;
                    }

                    opened.Add(process);
                    TryRun(() => process.CloseMainWindow());
                }

                // Wait for all of them within one shared grace period.
                var deadline = DateTime.UtcNow + gracePeriod;
                foreach (var process in opened)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero)
                    {
                        TryRun(() => process.WaitForExit(remaining));
                    }
                }

                // Terminate the rest together with their children, such as the module runtimes ASLM started.
                foreach (var process in opened)
                {
                    TryRun(() =>
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(TerminationWait);
                        }
                    });
                }
            }
            finally
            {
                foreach (var process in opened)
                {
                    process.Dispose();
                }
            }
        }


        // Process helpers

        /// <summary>
        /// Returns the folders whose programs block the removal: the shared folder when it is removed, every per-user
        /// folder, and the temporary patcher copies of the affected users.
        /// </summary>
        private static List<string> GetProcessRoots(UninstallPlan plan)
        {
            var roots = new List<string>();
            if (plan.Scope == UninstallScope.AllUsers)
            {
                roots.Add(plan.Installation.SharedInstallPath);
            }

            foreach (var user in plan.Users)
            {
                if (user.UserAppPath is not null)
                {
                    roots.Add(user.UserAppPath);
                }

                // A patcher that runs from a temporary copy writes into the per-user folder that is being removed.
                try
                {
                    if (Directory.Exists(user.TempPath))
                    {
                        roots.AddRange(Directory
                            .EnumerateDirectories(user.TempPath, PatcherCopyPrefix + "*")
                            .Where(directory => IsAslmTemporaryFolder(Path.GetFileName(directory))));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable temporary folder holds no patcher this uninstaller could find or stop.
                }
            }

            return roots;
        }

        /// <summary>
        /// Opens a process by id when it still runs the same executable, so a reused id never closes a stranger.
        /// </summary>
        /// <returns>The opened process, or null when it exited or the id now belongs to another program.</returns>
        private static Process? TryOpenSameProcess(BlockingProcess blocking)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(blocking.Id);
            }
            catch (ArgumentException)
            {
                // The process already exited.
                return null;
            }

            if (IsSamePath(TryGetExecutablePath(blocking.Id), blocking.ExecutablePath))
            {
                return process;
            }

            process.Dispose();
            return null;
        }

        /// <summary>
        /// Runs one operation on a process and ignores the errors of a process that exited or denies access; the
        /// follow-up check reports what still runs.
        /// </summary>
        private static void TryRun(Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // The process exited meanwhile or belongs to a user this process may not control.
            }
        }

        /// <summary>
        /// Reads the full executable path of a process.
        /// </summary>
        /// <returns>The executable path, or null when the process exited or Windows denies access.</returns>
        private static string? TryGetExecutablePath(int processId)
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, (uint)processId);
            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = new StringBuilder(MaxExtendedPathLength);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, flags: 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : null;
        }


        // Process interop

        /// <summary>
        /// Opens an existing process with the requested access rights; the handle is invalid when that fails.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        /// <summary>
        /// Reads the full Win32 path of the executable of a process (Unicode variant of the kernel32
        /// <c>QueryFullProcessImageName</c>).
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            SafeProcessHandle process,
            uint flags,
            StringBuilder exeName,
            ref int size);
    }
}
