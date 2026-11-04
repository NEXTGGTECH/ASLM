// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using System.Text;

namespace ASLM
{
    /// <summary>
    /// Captures the macOS process table (pid, ppid, name) through libproc.
    /// </summary>
    internal static class MacProcessSnapshot
    {
        // Constants

        /// <summary>
        /// libproc flavor <c>PROC_PIDTBSDINFO</c>: makes <c>proc_pidinfo</c> fill a <c>proc_bsdinfo</c> structure.
        /// </summary>
        private const int ProcPidTbsdInfo = 3;

        /// <summary>
        /// Native <c>MAXCOMLEN</c>: size of the short command name buffer; the long name buffer is twice as large.
        /// </summary>
        private const int MaxComLen = 16;


        // Snapshot capture

        /// <summary>
        /// Returns one entry per live process, skipping processes that cannot be inspected.
        /// </summary>
        public static IReadOnlyList<ProcessSnapshotEntry> Capture()
        {
            var entries = new List<ProcessSnapshotEntry>();

            // First call with no buffer asks libproc how large the process id list is.
            var byteCount = proc_listallpids(null, 0);
            if (byteCount <= 0)
            {
                return entries;
            }

            // Leave headroom for processes spawned between the two calls.
            var pids = new int[byteCount / sizeof(int) + 64];
            byteCount = proc_listallpids(pids, pids.Length * sizeof(int));
            if (byteCount <= 0)
            {
                return entries;
            }

            var pidCount = byteCount / sizeof(int);
            var infoSize = Marshal.SizeOf<ProcBsdInfo>();

            // Query the BSD info of each pid; processes that exited meanwhile or deny access return a short result.
            for (var index = 0; index < pidCount; index++)
            {
                var pid = pids[index];
                if (pid <= 0)
                {
                    continue;
                }

                var written = proc_pidinfo(pid, ProcPidTbsdInfo, 0, out var info, infoSize);
                if (written < infoSize)
                {
                    continue;
                }

                var name = ReadProcessName(info);
                entries.Add(new ProcessSnapshotEntry(pid, unchecked((int)info.pbi_ppid), name));
            }

            return entries;
        }

        /// <summary>
        /// Returns the long process name when present, falling back to the 16-char comm field.
        /// </summary>
        private static string ReadProcessName(in ProcBsdInfo info)
        {
            var name = DecodeFixedString(info.pbi_name);
            return name.Length > 0 ? name : DecodeFixedString(info.pbi_comm);
        }

        /// <summary>
        /// Decodes one null-terminated fixed-size byte field.
        /// </summary>
        private static string DecodeFixedString(byte[] value)
        {
            var terminator = Array.IndexOf(value, (byte)0);
            var length = terminator >= 0 ? terminator : value.Length;
            return Encoding.UTF8.GetString(value, 0, length);
        }


        // libproc interop

        /// <summary>
        /// libproc <c>proc_listallpids</c>: fills the buffer with the ids of all processes. Passing a null buffer
        /// only queries the required size. Returns a value less than or equal to zero on failure.
        /// </summary>
        [DllImport("libproc", SetLastError = true)]
        private static extern int proc_listallpids(int[]? buffer, int buffersize);

        /// <summary>
        /// libproc <c>proc_pidinfo</c>: fills the buffer with the information selected by the flavor for one
        /// process. Returns the number of bytes written, or a value less than the buffer size when the process
        /// cannot be inspected.
        /// </summary>
        [DllImport("libproc", SetLastError = true)]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, out ProcBsdInfo buffer, int buffersize);

        /// <summary>
        /// Managed mirror of the native <c>proc_bsdinfo</c> structure returned for flavor <c>PROC_PIDTBSDINFO</c>.
        /// The field order and sizes must match the native layout.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcBsdInfo
        {
            /// <summary>
            /// Process flags such as the 64-bit and emulation markers.
            /// </summary>
            public uint pbi_flags;

            /// <summary>
            /// Process run state (idle, running, sleeping, stopped or zombie).
            /// </summary>
            public uint pbi_status;

            /// <summary>
            /// Exit status of the process once it has terminated.
            /// </summary>
            public uint pbi_xstatus;

            /// <summary>
            /// Process id.
            /// </summary>
            public uint pbi_pid;

            /// <summary>
            /// Process id of the parent process.
            /// </summary>
            public uint pbi_ppid;

            /// <summary>
            /// Effective user id.
            /// </summary>
            public uint pbi_uid;

            /// <summary>
            /// Effective group id.
            /// </summary>
            public uint pbi_gid;

            /// <summary>
            /// Real user id.
            /// </summary>
            public uint pbi_ruid;

            /// <summary>
            /// Real group id.
            /// </summary>
            public uint pbi_rgid;

            /// <summary>
            /// Saved user id.
            /// </summary>
            public uint pbi_svuid;

            /// <summary>
            /// Saved group id.
            /// </summary>
            public uint pbi_svgid;

            /// <summary>
            /// Reserved by the kernel; keeps the structure layout aligned.
            /// </summary>
            public uint rfu_1;

            /// <summary>
            /// Short command name in a <c>MAXCOMLEN</c>-byte null-terminated buffer, so longer names are truncated.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxComLen)]
            public byte[] pbi_comm;

            /// <summary>
            /// Longer process name (<c>2 * MAXCOMLEN</c> bytes, null-terminated); empty when the kernel has none.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2 * MaxComLen)]
            public byte[] pbi_name;

            /// <summary>
            /// Number of open files of the process.
            /// </summary>
            public uint pbi_nfiles;

            /// <summary>
            /// Process group id.
            /// </summary>
            public uint pbi_pgid;

            /// <summary>
            /// Job control counter: number of processes in the group whose parent is in another group of the same session.
            /// </summary>
            public uint pbi_pjobc;

            /// <summary>
            /// Device number of the controlling terminal.
            /// </summary>
            public uint e_tdev;

            /// <summary>
            /// Process group id of the controlling terminal's foreground group.
            /// </summary>
            public uint e_tpgid;

            /// <summary>
            /// Scheduling nice value of the process.
            /// </summary>
            public int pbi_nice;

            /// <summary>
            /// Process start time, whole seconds since the Unix epoch.
            /// </summary>
            public ulong pbi_start_tvsec;

            /// <summary>
            /// Process start time, microseconds part within the second.
            /// </summary>
            public ulong pbi_start_tvusec;
        }
    }
}
