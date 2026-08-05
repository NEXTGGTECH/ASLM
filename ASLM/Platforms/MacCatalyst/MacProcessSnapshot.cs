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
        private const int ProcPidTbsdInfo = 3;
        private const int MaxComLen = 16;

        /// <summary>
        /// Returns one entry per live process, skipping processes that cannot be inspected.
        /// </summary>
        public static IReadOnlyList<ProcessSnapshotEntry> Capture()
        {
            var entries = new List<ProcessSnapshotEntry>();

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

        [DllImport("libproc", SetLastError = true)]
        private static extern int proc_listallpids(int[]? buffer, int buffersize);

        [DllImport("libproc", SetLastError = true)]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, out ProcBsdInfo buffer, int buffersize);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcBsdInfo
        {
            public uint pbi_flags;
            public uint pbi_status;
            public uint pbi_xstatus;
            public uint pbi_pid;
            public uint pbi_ppid;
            public uint pbi_uid;
            public uint pbi_gid;
            public uint pbi_ruid;
            public uint pbi_rgid;
            public uint pbi_svuid;
            public uint pbi_svgid;
            public uint rfu_1;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxComLen)]
            public byte[] pbi_comm;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2 * MaxComLen)]
            public byte[] pbi_name;

            public uint pbi_nfiles;
            public uint pbi_pgid;
            public uint pbi_pjobc;
            public uint e_tdev;
            public uint e_tpgid;
            public int pbi_nice;
            public ulong pbi_start_tvsec;
            public ulong pbi_start_tvusec;
        }
    }
}
