// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;

namespace ASLM.Services.Internal;

/// <summary>
/// Captures and briefly caches the operating-system process table for dashboard and runner diagnostics.
/// </summary>
public sealed class ProcessSnapshotReader
{
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMilliseconds(250);

    private readonly object _cacheLock = new();
    private IReadOnlyList<ProcessSnapshotEntry> _cachedSnapshot = [];
    private DateTimeOffset _cachedSnapshotUtc = DateTimeOffset.MinValue;

    /// <summary>
    /// Returns a cached process snapshot when it is still fresh enough; otherwise captures a new one.
    /// </summary>
    public IReadOnlyList<ProcessSnapshotEntry> GetSnapshot(TimeSpan? maxAge = null)
    {
        var effectiveMaxAge = maxAge ?? DefaultMaxAge;
        var now = DateTimeOffset.UtcNow;

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
    /// Captures the current OS process table, or an empty list on non-Windows platforms.
    /// </summary>
    private static IReadOnlyList<ProcessSnapshotEntry> CaptureSnapshot()
    {
#if WINDOWS
        var entries = new List<ProcessSnapshotEntry>();
        var snapshotHandle = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshotHandle == InvalidHandleValue)
        {
            return entries;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                dwSize = (uint)Marshal.SizeOf<ProcessEntry32>()
            };

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
            CloseHandle(snapshotHandle);
        }

        return entries;
#elif MACCATALYST
        return MacProcessSnapshot.Capture();
#else
        return [];
#endif
    }


    // Win32 interop

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Represents one raw process-table row with parent-child relationship data.
/// </summary>
public sealed record ProcessSnapshotEntry(int ProcessId, int ParentProcessId, string ExecutableName);
