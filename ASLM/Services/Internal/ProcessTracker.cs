// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Groups ASLM and its child processes into one Windows Job Object.
    /// On other platforms it tracks child processes and kills their trees on dispose.
    /// </summary>
    public sealed class ProcessTracker : IDisposable
    {
        private readonly SafeFileHandle? _jobHandle;
        private readonly ILogger<ProcessTracker> _logger;
        private readonly object _trackedLock = new();
        private readonly List<Process> _trackedProcesses = [];
        private bool _disposed;

        // Initialization

        /// <summary>
        /// Creates the Windows Job Object and assigns the current process to it.
        /// </summary>
        public ProcessTracker(ILogger<ProcessTracker> logger)
        {
            _logger = logger;

            if (!OperatingSystem.IsWindows())
            {
                _logger.LogInformation("ProcessTracker: tracking child PIDs with kill-on-dispose (no Job Objects on this OS).");
                return;
            }

            _jobHandle = CreateJobObject(IntPtr.Zero, "ASLM_ProcessGroup");
            if (_jobHandle.IsInvalid)
            {
                _logger.LogError("Failed to create Job Object. Error: {Error}", Marshal.GetLastWin32Error());
                return;
            }

            // Configure the job so all attached processes die when ASLM closes.
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
                                 JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            var infoPtr = Marshal.AllocHGlobal(length);

            try
            {
                Marshal.StructureToPtr(info, infoPtr, false);
                if (!SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, infoPtr, (uint)length))
                {
                    _logger.LogError("Failed to set Job Object info. Error: {Error}", Marshal.GetLastWin32Error());
                    return;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(infoPtr);
            }

            // Attach the main ASLM process so child processes can stay grouped under it.
            if (!AssignProcessToJobObject(_jobHandle, Process.GetCurrentProcess().Handle))
            {
                _logger.LogWarning("Failed to assign current process to Job Object. Error: {Error}", Marshal.GetLastWin32Error());
            }
            else
            {
                _logger.LogInformation("ProcessTracker initialized - ASLM and child processes are grouped.");
            }
        }


        // Process assignment

        /// <summary>
        /// Adds one child process to the shared Job Object.
        /// </summary>
        public bool AddProcess(Process process)
        {
            if (_disposed)
            {
                return false;
            }

            if (!OperatingSystem.IsWindows())
            {
                lock (_trackedLock)
                {
                    _trackedProcesses.Add(process);
                }

                return true;
            }

            if (_jobHandle == null || _jobHandle.IsInvalid)
            {
                return false;
            }

            try
            {
                var result = AssignProcessToJobObject(_jobHandle, process.Handle);
                if (!result)
                {
                    _logger.LogWarning(
                        "Failed to assign process {PID} to Job Object. Error: {Error}",
                        process.Id,
                        Marshal.GetLastWin32Error());
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception assigning process to Job Object.");
                return false;
            }
        }


        // Disposal

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!OperatingSystem.IsWindows())
            {
                KillTrackedProcessTrees();
            }

            _jobHandle?.Dispose();
        }

        /// <summary>
        /// Kills every tracked child process tree that is still alive.
        /// </summary>
        private void KillTrackedProcessTrees()
        {
            List<Process> processes;
            lock (_trackedLock)
            {
                processes = [.. _trackedProcesses];
                _trackedProcesses.Clear();
            }

            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The owner may have disposed or stopped the process already.
                }
            }
        }


        // Win32 interop

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle hJob,
            JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

        // Job info types

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        // Job limits

        [Flags]
        private enum JOBOBJECTLIMIT : uint
        {
            JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800,
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
        }

        // Basic limit info

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public JOBOBJECTLIMIT LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public long Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        // I/O counters

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        // Extended limit info

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
