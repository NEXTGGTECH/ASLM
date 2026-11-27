// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Owns ASLM's Windows Job Object and tracks child processes separately when they cannot use it.
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
        /// Serializes process creation and registration with disposal.
        /// </summary>
        private readonly object _trackedLock = new();

        /// <summary>
        /// Child processes that rely on explicit termination when the tracker is disposed.
        /// </summary>
        private readonly List<Process> _trackedProcesses = [];

        /// <summary>
        /// Enables process ownership outside test hosts.
        /// </summary>
        private readonly bool _trackProcesses;

        /// <summary>
        /// Whether explicit assignment to the job is available for child processes.
        /// </summary>
        private readonly bool _jobReady;

        /// <summary>
        /// Whether the tracker was already disposed; later calls become no-ops.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Gets whether direct children inherit ASLM's kill-on-close job and a launcher must break away.
        /// </summary>
        internal bool OwnsCurrentProcess { get; }


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
            _trackProcesses = trackCurrentProcess;

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

            // An unnamed job gives every instance its own handle lifetime and process group.
            _jobHandle = CreateJobObject(IntPtr.Zero, null);
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

            // Attach ASLM so direct children, including WebView2, inherit its group.
            _jobReady = true;
            using var currentProcess = Process.GetCurrentProcess();
            OwnsCurrentProcess = AssignProcessToJobObject(_jobHandle, currentProcess.Handle);
            if (!OwnsCurrentProcess)
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
        /// Starts and registers a child before disposal can take the process group away.
        /// </summary>
        internal bool StartProcess(Process process)
        {
            lock (_trackedLock)
            {
                if (_disposed || !process.Start()) return false;
                if (_trackProcesses) TrackProcess(process);
                return true;
            }
        }

        /// <summary>
        /// Groups a direct child or retains a handle for fallback termination. Callers hold the tracker lock.
        /// </summary>
        private void TrackProcess(Process process)
        {
            if (!OperatingSystem.IsWindows())
            {
                _trackedProcesses.Add(process);
                return;
            }

            if (OwnsCurrentProcess) return;

            // When ASLM could not join, try assigning the child itself before falling back to an owned handle.
            try
            {
                if (_jobReady && _jobHandle != null)
                {
                    if (AssignProcessToJobObject(_jobHandle, process.Handle)) return;
                    _logger.LogWarning(
                        "Failed to assign process {PID} to Job Object. Error: {Error}",
                        process.Id,
                        Marshal.GetLastWin32Error());
                }

                // The caller may dispose its Process wrapper before shutdown, so keep a separate live handle.
                for (var i = _trackedProcesses.Count - 1; i >= 0; i--)
                {
                    if (!_trackedProcesses[i].HasExited) continue;
                    _trackedProcesses[i].Dispose();
                    _trackedProcesses.RemoveAt(i);
                }

                if (process.HasExited) return;
                var tracked = Process.GetProcessById(process.Id);
                try
                {
                    _ = tracked.Handle;
                    if (tracked.StartTime == process.StartTime)
                    {
                        _trackedProcesses.Add(tracked);
                        return;
                    }
                }
                catch
                {
                    tracked.Dispose();
                    throw;
                }

                // The original process exited and its PID was reused before the handle could be opened.
                tracked.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not retain the child process for shutdown.");
            }
        }


        // Disposal

        /// <summary>
        /// Closes ungrouped process trees first, then releases the kill-on-close job.
        /// </summary>
        public void Dispose()
        {
            List<Process> processes;
            lock (_trackedLock)
            {
                if (_disposed) return;
                _disposed = true;
                processes = [.. _trackedProcesses];
                _trackedProcesses.Clear();
            }

            // The native termination request does not wait for commands or redirected output to finish.
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
                finally
                {
                    if (OperatingSystem.IsWindows()) process.Dispose();
                }
            }

            _jobHandle?.Dispose();
        }


        // Win32 interop

        /// <summary>
        /// Creates a Job Object and returns its handle.
        /// </summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

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
