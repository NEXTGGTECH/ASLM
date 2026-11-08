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
        // Fields

        /// <summary>
        /// Handle of the Windows Job Object; null when no job was created (non-Windows systems and test isolation).
        /// </summary>
        private readonly SafeFileHandle? _jobHandle;

        /// <summary>
        /// Logger for job setup and assignment results.
        /// </summary>
        private readonly ILogger<ProcessTracker> _logger;

        /// <summary>
        /// Guards <see cref="_trackedProcesses"/>.
        /// </summary>
        private readonly object _trackedLock = new();

        /// <summary>
        /// Child processes tracked outside Windows, where there are no Job Objects; killed on disposal.
        /// </summary>
        private readonly List<Process> _trackedProcesses = [];

        /// <summary>
        /// Whether the tracker was already disposed; later calls become no-ops.
        /// </summary>
        private bool _disposed;


        // Initialization

        /// <summary>
        /// Creates the Windows Job Object and assigns the current process to it.
        /// </summary>
        /// <param name="logger">Logger for job setup and assignment results.</param>
        public ProcessTracker(ILogger<ProcessTracker> logger) : this(logger, trackCurrentProcess: true)
        {
        }

        /// <summary>
        /// Creates the tracker; when <paramref name="trackCurrentProcess"/> is false no Job Object
        /// is created and disposal is inert (test isolation - a kill-on-close job would take the
        /// test host down with it).
        /// </summary>
        /// <param name="logger">Logger for job setup and assignment results.</param>
        /// <param name="trackCurrentProcess">False to skip all setup and create an inert tracker.</param>
        internal ProcessTracker(ILogger<ProcessTracker> logger, bool trackCurrentProcess)
        {
            _logger = logger;

            // Test isolation: no job, so disposal cannot kill the test host.
            if (!trackCurrentProcess)
            {
                return;
            }

            // Only Windows has Job Objects; elsewhere children are tracked individually in AddProcess.
            if (!OperatingSystem.IsWindows())
            {
                _logger.LogInformation("ProcessTracker: tracking child PIDs with kill-on-dispose (no Job Objects on this OS).");
                return;
            }

            // Create the named job that will group ASLM and its children.
            _jobHandle = CreateJobObject(IntPtr.Zero, "ASLM_ProcessGroup");
            if (_jobHandle.IsInvalid)
            {
                _logger.LogError("Failed to create Job Object. Error: {Error}", Marshal.GetLastWin32Error());
                return;
            }

            // Configure the job so all attached processes die when ASLM closes (kill-on-close ends them once the last
            // job handle is closed). Breakaway stays allowed so a process can still opt out with
            // CREATE_BREAKAWAY_FROM_JOB, as DetachedProcessStarter does for the self-update restart.
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
                                 JOBOBJECTLIMIT.JOB_OBJECT_LIMIT_BREAKAWAY_OK
                }
            };

            // The native call takes a pointer to the structure, so it is copied into unmanaged memory
            // that is freed again right after the call.
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
        /// Adds one child process to the shared Job Object on Windows, or to the list of processes
        /// killed on disposal on other systems.
        /// </summary>
        /// <param name="process">Started child process to track.</param>
        /// <returns>True when the process is now tracked; false when disposed, no job is available or assignment failed.</returns>
        public bool AddProcess(Process process)
        {
            if (_disposed)
            {
                return false;
            }

            // Without Job Objects the process is remembered and its tree is killed on disposal.
            if (!OperatingSystem.IsWindows())
            {
                lock (_trackedLock)
                {
                    _trackedProcesses.Add(process);
                }

                return true;
            }

            // The job could not be created (or the tracker is inert), so there is nothing to attach to.
            if (_jobHandle == null || _jobHandle.IsInvalid)
            {
                return false;
            }

            // Attach the process to the job; failures are logged and reported to the caller as false.
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

        /// <summary>
        /// Ends tracking: kills the tracked process trees on non-Windows systems and closes the job handle,
        /// which on Windows terminates every process still in the job because of the kill-on-close limit.
        /// </summary>
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
            // Take the list out under the lock, then kill outside it.
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

        /// <summary>
        /// Creates (or opens, when the name exists) a named Job Object and returns its handle.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string lpName);

        /// <summary>
        /// Applies limit settings of the given information class to a Job Object.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle hJob,
            JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo,
            uint cbJobObjectInfoLength);

        /// <summary>
        /// Places a process into a Job Object so the job's limits apply to it.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);


        // Win32 job types

        /// <summary>
        /// Information classes accepted by <see cref="SetInformationJobObject"/>.
        /// </summary>
        private enum JobObjectInfoType
        {
            /// <summary>
            /// Extended limit information (<c>JobObjectExtendedLimitInformation</c>, value 9).
            /// </summary>
            ExtendedLimitInformation = 9
        }

        /// <summary>
        /// Job limit flags used in <see cref="JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags"/>.
        /// </summary>
        [Flags]
        private enum JOBOBJECTLIMIT : uint
        {
            /// <summary>
            /// Lets a process in the job leave it by being created with <c>CREATE_BREAKAWAY_FROM_JOB</c>.
            /// </summary>
            JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800,

            /// <summary>
            /// Terminates all processes of the job when the last handle to the job is closed.
            /// </summary>
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000
        }

        /// <summary>
        /// Native <c>JOBOBJECT_BASIC_LIMIT_INFORMATION</c> structure. Only <c>LimitFlags</c> is set here;
        /// the other fields matter only when their corresponding limit flag is enabled.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            /// <summary>
            /// Per-process user-mode time limit in 100-nanosecond ticks.
            /// </summary>
            public long PerProcessUserTimeLimit;

            /// <summary>
            /// User-mode time limit for all processes of the job together, in 100-nanosecond ticks.
            /// </summary>
            public long PerJobUserTimeLimit;

            /// <summary>
            /// Enabled limits; also decides which of the other fields are honored.
            /// </summary>
            public JOBOBJECTLIMIT LimitFlags;

            /// <summary>
            /// Minimum working set size of each process.
            /// </summary>
            public UIntPtr MinimumWorkingSetSize;

            /// <summary>
            /// Maximum working set size of each process.
            /// </summary>
            public UIntPtr MaximumWorkingSetSize;

            /// <summary>
            /// Maximum number of processes that may be active in the job at the same time.
            /// </summary>
            public uint ActiveProcessLimit;

            /// <summary>
            /// Processor affinity mask applied to the processes of the job.
            /// </summary>
            public long Affinity;

            /// <summary>
            /// Priority class applied to the processes of the job.
            /// </summary>
            public uint PriorityClass;

            /// <summary>
            /// Scheduling class of the job, which sets how long its threads run per time slice.
            /// </summary>
            public uint SchedulingClass;
        }

        /// <summary>
        /// Native <c>IO_COUNTERS</c> structure: I/O accounting numbers that Windows reports for a job.
        /// It only fills the layout of <see cref="JOBOBJECT_EXTENDED_LIMIT_INFORMATION"/> here.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            /// <summary>
            /// Number of read operations.
            /// </summary>
            public ulong ReadOperationCount;

            /// <summary>
            /// Number of write operations.
            /// </summary>
            public ulong WriteOperationCount;

            /// <summary>
            /// Number of I/O operations that are neither reads nor writes.
            /// </summary>
            public ulong OtherOperationCount;

            /// <summary>
            /// Number of bytes read.
            /// </summary>
            public ulong ReadTransferCount;

            /// <summary>
            /// Number of bytes written.
            /// </summary>
            public ulong WriteTransferCount;

            /// <summary>
            /// Number of bytes transferred by operations that are neither reads nor writes.
            /// </summary>
            public ulong OtherTransferCount;
        }

        /// <summary>
        /// Native <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> structure passed to <see cref="SetInformationJobObject"/>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            /// <summary>
            /// Basic limits of the job, including the kill-on-close and breakaway flags.
            /// </summary>
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;

            /// <summary>
            /// Reserved I/O accounting block of the native structure.
            /// </summary>
            public IO_COUNTERS IoInfo;

            /// <summary>
            /// Memory limit per process; honored only with the matching limit flag.
            /// </summary>
            public UIntPtr ProcessMemoryLimit;

            /// <summary>
            /// Memory limit for the whole job; honored only with the matching limit flag.
            /// </summary>
            public UIntPtr JobMemoryLimit;

            /// <summary>
            /// Largest memory use of any single process in the job so far; filled in by Windows.
            /// </summary>
            public UIntPtr PeakProcessMemoryUsed;

            /// <summary>
            /// Largest memory use of the whole job so far; filled in by Windows.
            /// </summary>
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
