// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Captures and briefly caches the operating-system process table for dashboard and runner diagnostics.
    /// </summary>
    public sealed class ProcessSnapshotReader
    {
        // Cache state

        /// <summary>
        /// Maximum age of a cached snapshot when the caller does not pass one.
        /// </summary>
        private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Guards the cached snapshot so concurrent callers share one capture.
        /// </summary>
        private readonly object _cacheLock = new();

        /// <summary>
        /// Most recently captured process table; empty until the first capture.
        /// </summary>
        private IReadOnlyList<ProcessSnapshotEntry> _cachedSnapshot = [];

        /// <summary>
        /// UTC time associated with the cached snapshot, used to judge its age.
        /// </summary>
        private DateTimeOffset _cachedSnapshotUtc = DateTimeOffset.MinValue;


        // Snapshot access

        /// <summary>
        /// Returns a cached process snapshot when it is still fresh enough; otherwise captures a new one.
        /// </summary>
        /// <param name="maxAge">Oldest acceptable age of a cached snapshot, or null for the default of a few hundred milliseconds.</param>
        public IReadOnlyList<ProcessSnapshotEntry> GetSnapshot(TimeSpan? maxAge = null)
        {
            var effectiveMaxAge = maxAge ?? DefaultMaxAge;
            var now = DateTimeOffset.UtcNow;

            // Capture under the lock so parallel callers do not scan the process table twice.
            // An empty snapshot is never reused, so the next call captures again.
            lock (_cacheLock)
            {
                if (_cachedSnapshot.Count > 0 && now - _cachedSnapshotUtc <= effectiveMaxAge)
                {
                    return _cachedSnapshot;
                }

                _cachedSnapshot = CaptureSnapshot();
                _cachedSnapshotUtc = now;
                return _cachedSnapshot;
            }
        }


        // Snapshot capture

        /// <summary>
        /// Captures the current OS process table using the platform's native process source.
        /// </summary>
        private static IReadOnlyList<ProcessSnapshotEntry> CaptureSnapshot()
        {
#if WINDOWS
            // Windows takes a Toolhelp snapshot of all processes; a failed snapshot yields an empty table.
            var entries = new List<ProcessSnapshotEntry>();
            var snapshotHandle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
            if (snapshotHandle == InvalidHandleValue)
            {
                return entries;
            }

            try
            {
                // The entry structure must carry its own size before the first call.
                var entry = new ProcessEntry32
                {
                    dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
                };

                // Walk the snapshot: First returns the first process, Next advances until the list ends.
                if (Process32First(snapshotHandle, ref entry))
                {
                    do
                    {
                        entries.Add(new ProcessSnapshotEntry(
                            (int)entry.th32ProcessID,
                            (int)entry.th32ParentProcessID,
                            entry.szExeFile ?? string.Empty));
                    }
                    while (Process32Next(snapshotHandle, ref entry));
                }
            }
            finally
            {
                // The snapshot is a kernel handle and must always be released.
                CloseHandle(snapshotHandle);
            }

            return entries;
#elif MACCATALYST
            return MacProcessSnapshot.Capture();
#elif LINUX
            return LinuxProcessSnapshot.Capture();
#else
            return [];
#endif
        }


        // Win32 interop

        /// <summary>
        /// Toolhelp snapshot flag (<c>TH32CS_SNAPPROCESS</c>) that includes all processes of the system.
        /// </summary>
        private const uint Th32csSnapProcess = 0x00000002;

        /// <summary>
        /// Value returned by <c>CreateToolhelp32Snapshot</c> on failure (<c>INVALID_HANDLE_VALUE</c>).
        /// </summary>
        private static readonly IntPtr InvalidHandleValue = new(-1);

        /// <summary>
        /// Native <c>PROCESSENTRY32</c> structure describing one process of a Toolhelp snapshot.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            /// <summary>
            /// Size of the structure in bytes; the caller must set it before the first read.
            /// </summary>
            public uint dwSize;

            /// <summary>
            /// Reference count of the process; no longer used by Windows and always zero.
            /// </summary>
            public uint cntUsage;

            /// <summary>
            /// Identifier of the process.
            /// </summary>
            public uint th32ProcessID;

            /// <summary>
            /// Default heap identifier; no longer used by Windows and always zero.
            /// </summary>
            public IntPtr th32DefaultHeapID;

            /// <summary>
            /// Module identifier; no longer used by Windows and always zero.
            /// </summary>
            public uint th32ModuleID;

            /// <summary>
            /// Number of execution threads started by the process.
            /// </summary>
            public uint cntThreads;

            /// <summary>
            /// Identifier of the process that created this process.
            /// </summary>
            public uint th32ParentProcessID;

            /// <summary>
            /// Base priority of the threads this process creates.
            /// </summary>
            public int pcPriClassBase;

            /// <summary>
            /// Flags field that is no longer used by Windows and always zero.
            /// </summary>
            public uint dwFlags;

            /// <summary>
            /// Executable file name of the process, without a directory (at most 260 characters).
            /// </summary>
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        /// <summary>
        /// Takes a snapshot of the processes (and related data) of the system; the returned handle must be closed.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        /// <summary>
        /// Reads the first process entry of a snapshot; returns false when there is none.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        /// <summary>
        /// Reads the next process entry of a snapshot; returns false when the list is exhausted.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        /// <summary>
        /// Closes an open kernel object handle.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    /// <summary>
    /// Represents one raw process-table row with parent-child relationship data.
    /// </summary>
    /// <param name="ProcessId">Operating-system identifier of the process.</param>
    /// <param name="ParentProcessId">Identifier of the process that started it.</param>
    /// <param name="ExecutableName">Executable or command name as reported by the platform's process source, without a directory.</param>
    public sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ExecutableName);
}
