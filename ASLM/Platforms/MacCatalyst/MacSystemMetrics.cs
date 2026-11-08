// Copyright NEXTGGTECH. Apache License 2.0.

using System.Runtime.InteropServices;
using System.Text;
using ASLM.Pages;

namespace ASLM
{
    /// <summary>
    /// Supplies the macOS counterparts of the Windows dashboard metrics:
    /// system CPU/memory (Mach), per-process disk I/O and socket counts (libproc),
    /// whole-disk activity (IOKit block storage statistics), and GPU utilization (IOAccelerator).
    /// </summary>
    internal static class MacSystemMetrics
    {
        // System CPU

        /// <summary>
        /// Mach <c>HOST_CPU_LOAD_INFO</c> flavor: makes <c>host_statistics</c> return cumulative CPU ticks.
        /// </summary>
        private const int HostCpuLoadInfoFlavor = 3;

        /// <summary>
        /// Mach <c>HOST_VM_INFO64</c> flavor: makes <c>host_statistics64</c> return virtual memory statistics.
        /// </summary>
        private const int HostVmInfo64Flavor = 4;

        /// <summary>
        /// Reads cumulative CPU ticks shaped like the Windows kernel counters:
        /// kernel includes idle so that <c>1 - idle / (kernel + user)</c> stays valid.
        /// </summary>
        /// <param name="idleTime">Receives the cumulative idle ticks.</param>
        /// <param name="kernelTime">Receives the cumulative system plus idle ticks.</param>
        /// <param name="userTime">Receives the cumulative user plus nice ticks.</param>
        /// <returns>True when the Mach call succeeded; otherwise false with all outputs zero.</returns>
        public static bool TryGetSystemCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
        {
            idleTime = 0;
            kernelTime = 0;
            userTime = 0;

            // The count is the number of 32-bit integers in host_cpu_load_info (HOST_CPU_LOAD_INFO_COUNT).
            var info = default(HostCpuLoadInfo);
            var count = 4u;
            if (host_statistics(mach_host_self(), HostCpuLoadInfoFlavor, ref info, ref count) != 0)
            {
                return false;
            }

            idleTime = info.Idle;
            kernelTime = (ulong)info.System + info.Idle;
            userTime = (ulong)info.User + info.Nice;
            return true;
        }


        // System memory

        /// <summary>
        /// Returns physical memory totals; available memory counts free plus inactive pages.
        /// </summary>
        public static HomeMemoryStatus GetMemoryStatus()
        {
            try
            {
                // Installed physical memory comes from the hw.memsize sysctl.
                var totalBytes = 0L;
                var length = (IntPtr)sizeof(long);
                if (sysctlbyname("hw.memsize", ref totalBytes, ref length, IntPtr.Zero, IntPtr.Zero) != 0 || totalBytes <= 0)
                {
                    return new HomeMemoryStatus();
                }

                // Page size falls back to 16 KiB (Apple silicon) when the host cannot report it.
                var host = mach_host_self();
                if (host_page_size(host, out var pageSize) != 0)
                {
                    pageSize = (UIntPtr)16384;
                }

                // The count is the structure size in 32-bit integers, as host_statistics64 expects.
                var statistics = default(VmStatistics64);
                var count = (uint)(Marshal.SizeOf<VmStatistics64>() / sizeof(int));
                if (host_statistics64(host, HostVmInfo64Flavor, ref statistics, ref count) != 0)
                {
                    return new HomeMemoryStatus { TotalPhysicalBytes = totalBytes };
                }

                // Free and inactive pages can be reclaimed immediately, so everything else counts as used.
                var availableBytes = (long)((statistics.FreeCount + (ulong)statistics.InactiveCount) * (ulong)pageSize);
                return new HomeMemoryStatus
                {
                    TotalPhysicalBytes = totalBytes,
                    UsedPhysicalBytes = Math.Max(0, totalBytes - availableBytes)
                };
            }
            catch
            {
                // Metrics are best effort: report an unknown memory state instead of failing the dashboard.
                return new HomeMemoryStatus();
            }
        }


        // Per-process disk I/O

        /// <summary>
        /// libproc <c>RUSAGE_INFO_V2</c> flavor: makes <c>proc_pid_rusage</c> fill a <c>rusage_info_v2</c> structure.
        /// </summary>
        private const int RusageInfoV2 = 2;

        /// <summary>
        /// Returns cumulative disk bytes read/written by one process.
        /// </summary>
        public static (ulong ReadBytes, ulong WriteBytes) GetProcessDiskIo(int processId)
        {
            try
            {
                var info = default(RUsageInfoV2);
                if (proc_pid_rusage(processId, RusageInfoV2, ref info) != 0)
                {
                    return (0, 0);
                }

                return (info.DiskIoBytesRead, info.DiskIoBytesWritten);
            }
            catch
            {
                // A process that exited or cannot be inspected reports no I/O.
                return (0, 0);
            }
        }


        // Per-process socket counts

        /// <summary>
        /// libproc <c>PROC_PIDLISTFDS</c> flavor: makes <c>proc_pidinfo</c> list the open file descriptors of a process.
        /// </summary>
        private const int ProcPidListFds = 1;

        /// <summary>
        /// libproc <c>PROX_FDTYPE_SOCKET</c>: descriptor type value that marks a socket.
        /// </summary>
        private const int ProxFdTypeSocket = 2;

        /// <summary>
        /// Size in bytes of one native <c>proc_fdinfo</c> entry: a 32-bit descriptor number followed by a 32-bit type.
        /// </summary>
        private const int FdInfoSize = 8;

        /// <summary>
        /// Counts open socket descriptors per tracked process (all address families).
        /// </summary>
        /// <param name="processIds">Ids of the processes to inspect.</param>
        /// <returns>Socket count by process id; processes without sockets or that cannot be inspected are omitted.</returns>
        public static Dictionary<int, int> CountProcessSockets(IReadOnlyCollection<int> processIds)
        {
            var counts = new Dictionary<int, int>();

            foreach (var processId in processIds)
            {
                try
                {
                    // A call without a buffer returns the byte size needed for the descriptor list.
                    var byteCount = proc_pidinfo(processId, ProcPidListFds, 0, null, 0);
                    if (byteCount <= 0)
                    {
                        continue;
                    }

                    // Leave headroom for descriptors opened between the two calls.
                    var buffer = new byte[byteCount + 32 * FdInfoSize];
                    byteCount = proc_pidinfo(processId, ProcPidListFds, 0, buffer, buffer.Length);
                    if (byteCount <= 0)
                    {
                        continue;
                    }

                    // Each entry stores the descriptor type at byte 4; count the ones marked as sockets.
                    var socketCount = 0;
                    for (var offset = 0; offset + FdInfoSize <= byteCount; offset += FdInfoSize)
                    {
                        if (BitConverter.ToUInt32(buffer, offset + 4) == ProxFdTypeSocket)
                        {
                            socketCount++;
                        }
                    }

                    if (socketCount > 0)
                    {
                        counts[processId] = socketCount;
                    }
                }
                catch
                {
                    // Skip processes that cannot be inspected.
                }
            }

            return counts;
        }


        // Whole-disk activity

        /// <summary>
        /// Guards <see cref="_lastDiskSample"/> against concurrent sampling.
        /// </summary>
        private static readonly object _diskSampleLock = new();

        /// <summary>
        /// Counters captured by the previous sampling call; rates are computed against it. Null before the first sample.
        /// </summary>
        private static DiskSample? _lastDiskSample;

        /// <summary>
        /// Samples block-storage statistics and converts the deltas into busy percent and byte rates.
        /// </summary>
        public static HomeDiskStats SampleDiskStats()
        {
            try
            {
                var current = CaptureDiskSample();
                if (current == null)
                {
                    return new HomeDiskStats();
                }

                lock (_diskSampleLock)
                {
                    // Rates need two samples, so the first call only stores the baseline.
                    var previous = _lastDiskSample;
                    _lastDiskSample = current;

                    if (previous == null)
                    {
                        return new HomeDiskStats();
                    }

                    var elapsedSeconds = (current.TimestampUtc - previous.TimestampUtc).TotalSeconds;
                    if (elapsedSeconds <= 0)
                    {
                        return new HomeDiskStats();
                    }

                    // Counters can reset (for example when a drive is removed), so a decrease counts as no activity.
                    var readDelta = current.BytesRead >= previous.BytesRead ? current.BytesRead - previous.BytesRead : 0;
                    var writeDelta = current.BytesWritten >= previous.BytesWritten ? current.BytesWritten - previous.BytesWritten : 0;
                    var busyDeltaNs = current.TotalTimeNs >= previous.TotalTimeNs ? current.TotalTimeNs - previous.TotalTimeNs : 0;

                    // Busy percent is the share of the elapsed wall time spent on I/O, clamped to 0-100.
                    return new HomeDiskStats
                    {
                        BusyPercent = Math.Clamp(busyDeltaNs / (elapsedSeconds * 1_000_000_000d) * 100d, 0, 100),
                        ReadBytesPerSecond = readDelta / elapsedSeconds,
                        WriteBytesPerSecond = writeDelta / elapsedSeconds
                    };
                }
            }
            catch
            {
                // Metrics are best effort: report no disk activity instead of failing the dashboard.
                return new HomeDiskStats();
            }
        }

        /// <summary>
        /// Sums the IOBlockStorageDriver statistics dictionaries across all drivers.
        /// </summary>
        private static DiskSample? CaptureDiskSample()
        {
            // Enumerate all block storage drivers registered in IOKit; port 0 selects the default main port.
            if (IOServiceGetMatchingServices(0, IOServiceMatching("IOBlockStorageDriver"), out var iterator) != 0)
            {
                return null;
            }

            // CFString keys of the "Statistics" dictionary entries; all are released in the finally block.
            var statisticsKey = CreateCfString("Statistics");
            var bytesReadKey = CreateCfString("Bytes (Read)");
            var bytesWrittenKey = CreateCfString("Bytes (Write)");
            var timeReadKey = CreateCfString("Total Time (Read)");
            var timeWriteKey = CreateCfString("Total Time (Write)");

            try
            {
                var sample = new DiskSample { TimestampUtc = DateTimeOffset.UtcNow };

                // Add up the byte and time counters of every driver; each registry entry is released after use.
                uint entry;
                while ((entry = IOIteratorNext(iterator)) != 0)
                {
                    try
                    {
                        var statistics = IORegistryEntryCreateCFProperty(entry, statisticsKey, IntPtr.Zero, 0);
                        if (statistics == IntPtr.Zero)
                        {
                            continue;
                        }

                        try
                        {
                            sample.BytesRead += (ulong)ReadCfDictionaryNumber(statistics, bytesReadKey);
                            sample.BytesWritten += (ulong)ReadCfDictionaryNumber(statistics, bytesWrittenKey);
                            sample.TotalTimeNs += (ulong)ReadCfDictionaryNumber(statistics, timeReadKey);
                            sample.TotalTimeNs += (ulong)ReadCfDictionaryNumber(statistics, timeWriteKey);
                        }
                        finally
                        {
                            CFRelease(statistics);
                        }
                    }
                    finally
                    {
                        IOObjectRelease(entry);
                    }
                }

                return sample;
            }
            finally
            {
                CFRelease(statisticsKey);
                CFRelease(bytesReadKey);
                CFRelease(bytesWrittenKey);
                CFRelease(timeReadKey);
                CFRelease(timeWriteKey);
                IOObjectRelease(iterator);
            }
        }

        /// <summary>
        /// Cumulative block-storage counters summed over all drivers at one point in time.
        /// </summary>
        private sealed class DiskSample
        {
            /// <summary>
            /// UTC time when the counters were read.
            /// </summary>
            public DateTimeOffset TimestampUtc { get; init; }

            /// <summary>
            /// Cumulative bytes read, summed over all drivers.
            /// </summary>
            public ulong BytesRead { get; set; }

            /// <summary>
            /// Cumulative bytes written, summed over all drivers.
            /// </summary>
            public ulong BytesWritten { get; set; }

            /// <summary>
            /// Cumulative time, in nanoseconds, the drivers spent on read and write operations (both added together).
            /// </summary>
            public ulong TotalTimeNs { get; set; }
        }


        // GPU utilization

        /// <summary>
        /// Adapter names from the last query that found any, reused when a later query finds none.
        /// </summary>
        private static IReadOnlyList<string>? _cachedGpuAdapterNames;

        /// <summary>
        /// Reads per-adapter GPU utilization from IOAccelerator performance statistics.
        /// macOS exposes no public per-process GPU counters, so <c>ProcessByPid</c> stays empty.
        /// </summary>
        public static HomeGpuQueryResult QueryGpuUsage()
        {
            var samples = new List<HomeGpuEngineSample>();
            var adapterNames = new List<string>();

            try
            {
                // Enumerate all GPU accelerators registered in IOKit; port 0 selects the default main port.
                if (IOServiceGetMatchingServices(0, IOServiceMatching("IOAccelerator"), out var iterator) != 0)
                {
                    return EmptyGpuResult();
                }

                // CFString keys for the performance dictionary and the model property; released in the finally block.
                var statisticsKey = CreateCfString("PerformanceStatistics");
                var utilizationKey = CreateCfString("Device Utilization %");
                var modelKey = CreateCfString("model");

                try
                {
                    // One sample per accelerator: its name plus the device utilization from its statistics.
                    uint entry;
                    var adapterIndex = 0;
                    while ((entry = IOIteratorNext(iterator)) != 0)
                    {
                        try
                        {
                            adapterNames.Add(ResolveAdapterName(entry, modelKey, adapterIndex));

                            var statistics = IORegistryEntryCreateCFProperty(entry, statisticsKey, IntPtr.Zero, 0);
                            if (statistics != IntPtr.Zero)
                            {
                                try
                                {
                                    // macOS offers no per-process GPU attribution, so the device sample is not tied to a process id.
                                    var utilization = ReadCfDictionaryNumber(statistics, utilizationKey);
                                    samples.Add(new HomeGpuEngineSample
                                    {
                                        ProcessId = 0,
                                        AdapterIndex = adapterIndex,
                                        EngineId = "device",
                                        Utilization = Math.Clamp(utilization, 0, 100)
                                    });
                                }
                                finally
                                {
                                    CFRelease(statistics);
                                }
                            }

                            adapterIndex++;
                        }
                        finally
                        {
                            IOObjectRelease(entry);
                        }
                    }
                }
                finally
                {
                    CFRelease(statisticsKey);
                    CFRelease(utilizationKey);
                    CFRelease(modelKey);
                    IOObjectRelease(iterator);
                }

                // Remember the adapter names so a later query that finds none still reports them.
                if (adapterNames.Count > 0)
                {
                    _cachedGpuAdapterNames = adapterNames;
                }

                return new HomeGpuQueryResult
                {
                    ProcessByPid = new Dictionary<int, double>(),
                    AdapterNames = _cachedGpuAdapterNames ?? adapterNames,
                    Samples = samples
                };
            }
            catch
            {
                // Metrics are best effort: report no GPU data instead of failing the dashboard.
                return EmptyGpuResult();
            }
        }

        /// <summary>
        /// Returns an empty GPU result that keeps previously discovered adapter names.
        /// </summary>
        private static HomeGpuQueryResult EmptyGpuResult() => new()
        {
            ProcessByPid = new Dictionary<int, double>(),
            AdapterNames = _cachedGpuAdapterNames ?? [],
            Samples = []
        };

        /// <summary>
        /// Resolves a display name for one accelerator, walking up the registry for a model property.
        /// </summary>
        private static string ResolveAdapterName(uint entry, IntPtr modelKey, int adapterIndex)
        {
            // kIORegistryIterateRecursively (1) | kIORegistryIterateParents (2): search the entry, its children and its parents.
            const uint IterateRecursivelyAndParents = 3;

            var model = IORegistryEntrySearchCFProperty(entry, "IOService", modelKey, IntPtr.Zero, IterateRecursivelyAndParents);
            if (model != IntPtr.Zero)
            {
                try
                {
                    var name = ReadCfStringOrData(model);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                finally
                {
                    CFRelease(model);
                }
            }

            return $"GPU {adapterIndex}";
        }


        // CoreFoundation helpers

        /// <summary>
        /// CoreFoundation <c>kCFStringEncodingUTF8</c> encoding identifier.
        /// </summary>
        private const uint CfStringEncodingUtf8 = 0x08000100;

        /// <summary>
        /// CoreFoundation <c>kCFNumberSInt64Type</c>: requests the number as a signed 64-bit integer.
        /// </summary>
        private const int CfNumberSInt64Type = 4;

        /// <summary>
        /// Creates a CFString the caller must release.
        /// </summary>
        private static IntPtr CreateCfString(string value) =>
            CFStringCreateWithCString(IntPtr.Zero, value, CfStringEncodingUtf8);

        /// <summary>
        /// Reads one numeric dictionary value, returning zero when absent.
        /// </summary>
        private static long ReadCfDictionaryNumber(IntPtr dictionary, IntPtr key)
        {
            var number = CFDictionaryGetValue(dictionary, key);
            if (number == IntPtr.Zero)
            {
                return 0;
            }

            return CFNumberGetValue(number, CfNumberSInt64Type, out var value) ? value : 0;
        }

        /// <summary>
        /// Decodes a CFString or CFData property into a managed string.
        /// </summary>
        private static string ReadCfStringOrData(IntPtr value)
        {
            // A CFString converts directly into a UTF-8 buffer of up to 256 bytes.
            var buffer = new byte[256];
            if (CFStringGetCString(value, buffer, buffer.Length, CfStringEncodingUtf8))
            {
                var terminator = Array.IndexOf(buffer, (byte)0);
                return Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
            }

            // Otherwise the value is CFData (some GPU drivers store the model as null-terminated bytes).
            var dataLength = (int)CFDataGetLength(value);
            if (dataLength <= 0 || dataLength > 256)
            {
                return string.Empty;
            }

            var dataPointer = CFDataGetBytePtr(value);
            if (dataPointer == IntPtr.Zero)
            {
                return string.Empty;
            }

            var bytes = new byte[dataLength];
            Marshal.Copy(dataPointer, bytes, 0, dataLength);
            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : dataLength).Trim();
        }


        // Native interop

        /// <summary>
        /// Path of the system library that exports the Mach host calls and <c>sysctlbyname</c>.
        /// </summary>
        private const string SystemLib = "/usr/lib/libSystem.dylib";

        /// <summary>
        /// Path of the process information library (libproc).
        /// </summary>
        private const string ProcLib = "/usr/lib/libproc.dylib";

        /// <summary>
        /// Path of the IOKit framework binary used to query the I/O registry.
        /// </summary>
        private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";

        /// <summary>
        /// Path of the CoreFoundation framework binary used to read registry property values.
        /// </summary>
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        /// <summary>
        /// Managed mirror of the Mach <c>host_cpu_load_info</c> structure: cumulative CPU ticks per state,
        /// in the native <c>cpu_ticks</c> order (user, system, idle, nice).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HostCpuLoadInfo
        {
            /// <summary>
            /// Ticks spent running user-space code, excluding nice time.
            /// </summary>
            public uint User;

            /// <summary>
            /// Ticks spent running kernel code.
            /// </summary>
            public uint System;

            /// <summary>
            /// Ticks the CPUs were idle.
            /// </summary>
            public uint Idle;

            /// <summary>
            /// Ticks spent running user-space code of processes with a positive nice value (lowered priority).
            /// </summary>
            public uint Nice;
        }

        /// <summary>
        /// Managed mirror of the Mach <c>vm_statistics64</c> structure returned for <c>HOST_VM_INFO64</c>.
        /// Fields named <c>*Count</c> are page counts; the field order and sizes must match the native layout.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct VmStatistics64
        {
            /// <summary>
            /// Number of free pages.
            /// </summary>
            public uint FreeCount;

            /// <summary>
            /// Number of pages currently in use and recently referenced.
            /// </summary>
            public uint ActiveCount;

            /// <summary>
            /// Number of pages that are in memory but have not been referenced recently and can be reclaimed.
            /// </summary>
            public uint InactiveCount;

            /// <summary>
            /// Number of pages wired in memory that cannot be paged out.
            /// </summary>
            public uint WireCount;

            /// <summary>
            /// Number of zero-fill pages created on demand.
            /// </summary>
            public ulong ZeroFillCount;

            /// <summary>
            /// Number of pages reactivated, moved from the inactive back to the active list.
            /// </summary>
            public ulong Reactivations;

            /// <summary>
            /// Number of pages paged in from disk.
            /// </summary>
            public ulong Pageins;

            /// <summary>
            /// Number of pages paged out to disk.
            /// </summary>
            public ulong Pageouts;

            /// <summary>
            /// Number of page faults.
            /// </summary>
            public ulong Faults;

            /// <summary>
            /// Number of copy-on-write faults.
            /// </summary>
            public ulong CowFaults;

            /// <summary>
            /// Number of object cache lookups.
            /// </summary>
            public ulong Lookups;

            /// <summary>
            /// Number of object cache lookups that found the page.
            /// </summary>
            public ulong Hits;

            /// <summary>
            /// Number of purgeable pages that were reclaimed.
            /// </summary>
            public ulong Purges;

            /// <summary>
            /// Number of pages marked purgeable that the system may discard under memory pressure.
            /// </summary>
            public uint PurgeableCount;

            /// <summary>
            /// Number of speculative pages, read ahead from disk but not yet requested; the kernel also counts
            /// them in <see cref="FreeCount"/>.
            /// </summary>
            public uint SpeculativeCount;

            /// <summary>
            /// Number of pages decompressed from the compressor.
            /// </summary>
            public ulong Decompressions;

            /// <summary>
            /// Number of pages compressed by the compressor.
            /// </summary>
            public ulong Compressions;

            /// <summary>
            /// Number of pages swapped in through compressor segments.
            /// </summary>
            public ulong Swapins;

            /// <summary>
            /// Number of pages swapped out through compressor segments.
            /// </summary>
            public ulong Swapouts;

            /// <summary>
            /// Number of pages the compressed pager uses to hold all compressed data.
            /// </summary>
            public uint CompressorPageCount;

            /// <summary>
            /// Number of pages on the throttled queue, held back from the active and inactive lists.
            /// </summary>
            public uint ThrottledCount;

            /// <summary>
            /// Number of file-backed pages.
            /// </summary>
            public uint ExternalPageCount;

            /// <summary>
            /// Number of anonymous (non file-backed) pages.
            /// </summary>
            public uint InternalPageCount;

            /// <summary>
            /// Number of pages, counted uncompressed, whose data is held within the compressor.
            /// </summary>
            public ulong TotalUncompressedPagesInCompressor;
        }

        /// <summary>
        /// Managed mirror of the native <c>rusage_info_v2</c> structure returned for <c>RUSAGE_INFO_V2</c>.
        /// Time fields use Mach absolute time units; size fields are in bytes.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct RUsageInfoV2
        {
            /// <summary>
            /// UUID of the process's main executable image (16 bytes).
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Uuid;

            /// <summary>
            /// CPU time spent in user mode.
            /// </summary>
            public ulong UserTime;

            /// <summary>
            /// CPU time spent in kernel mode.
            /// </summary>
            public ulong SystemTime;

            /// <summary>
            /// Number of package idle wakeups caused by the process.
            /// </summary>
            public ulong PkgIdleWkups;

            /// <summary>
            /// Number of interrupt wakeups caused by the process.
            /// </summary>
            public ulong InterruptWkups;

            /// <summary>
            /// Number of pages paged in.
            /// </summary>
            public ulong Pageins;

            /// <summary>
            /// Wired memory size in bytes.
            /// </summary>
            public ulong WiredSize;

            /// <summary>
            /// Resident memory size in bytes.
            /// </summary>
            public ulong ResidentSize;

            /// <summary>
            /// Physical memory footprint in bytes.
            /// </summary>
            public ulong PhysFootprint;

            /// <summary>
            /// Process start time as Mach absolute time.
            /// </summary>
            public ulong ProcStartAbstime;

            /// <summary>
            /// Process exit time as Mach absolute time; zero while the process is alive.
            /// </summary>
            public ulong ProcExitAbstime;

            /// <summary>
            /// User-mode CPU time of terminated child processes.
            /// </summary>
            public ulong ChildUserTime;

            /// <summary>
            /// Kernel-mode CPU time of terminated child processes.
            /// </summary>
            public ulong ChildSystemTime;

            /// <summary>
            /// Package idle wakeups caused by terminated child processes.
            /// </summary>
            public ulong ChildPkgIdleWkups;

            /// <summary>
            /// Interrupt wakeups caused by terminated child processes.
            /// </summary>
            public ulong ChildInterruptWkups;

            /// <summary>
            /// Pages paged in by terminated child processes.
            /// </summary>
            public ulong ChildPageins;

            /// <summary>
            /// Elapsed time of terminated child processes as Mach absolute time.
            /// </summary>
            public ulong ChildElapsedAbstime;

            /// <summary>
            /// Total bytes the process has read from storage.
            /// </summary>
            public ulong DiskIoBytesRead;

            /// <summary>
            /// Total bytes the process has written to storage.
            /// </summary>
            public ulong DiskIoBytesWritten;
        }

        /// <summary>
        /// Mach <c>mach_host_self</c>: returns the send right (port name) of the current host.
        /// </summary>
        [DllImport(SystemLib)]
        private static extern uint mach_host_self();

        /// <summary>
        /// Mach <c>host_statistics</c>: fills the CPU load structure selected by the flavor; the count is the
        /// buffer size in 32-bit integers on input. Returns 0 (<c>KERN_SUCCESS</c>) on success.
        /// </summary>
        [DllImport(SystemLib)]
        private static extern int host_statistics(uint host, int flavor, ref HostCpuLoadInfo info, ref uint count);

        /// <summary>
        /// Mach <c>host_statistics64</c>: fills the 64-bit virtual memory statistics selected by the flavor; the
        /// count is the buffer size in 32-bit integers on input. Returns 0 (<c>KERN_SUCCESS</c>) on success.
        /// </summary>
        [DllImport(SystemLib)]
        private static extern int host_statistics64(uint host, int flavor, ref VmStatistics64 info, ref uint count);

        /// <summary>
        /// Mach <c>host_page_size</c>: returns the virtual memory page size of the host in bytes.
        /// Returns 0 (<c>KERN_SUCCESS</c>) on success.
        /// </summary>
        [DllImport(SystemLib)]
        private static extern int host_page_size(uint host, out UIntPtr pageSize);

        /// <summary>
        /// BSD <c>sysctlbyname</c>: reads the kernel value with the given name into the value buffer; the length
        /// holds the buffer size on input and the written size on output. Returns 0 on success.
        /// </summary>
        [DllImport(SystemLib)]
        private static extern int sysctlbyname(string name, ref long value, ref IntPtr length, IntPtr newValue, IntPtr newLength);

        /// <summary>
        /// libproc <c>proc_pid_rusage</c>: fills the resource usage structure selected by the flavor for one
        /// process. Returns 0 on success.
        /// </summary>
        [DllImport(ProcLib)]
        private static extern int proc_pid_rusage(int pid, int flavor, ref RUsageInfoV2 buffer);

        /// <summary>
        /// libproc <c>proc_pidinfo</c>: writes the information selected by the flavor for one process into the
        /// buffer. With a null buffer it returns the required size in bytes; otherwise the number of bytes written,
        /// or a value less than or equal to zero on failure.
        /// </summary>
        [DllImport(ProcLib)]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int buffersize);

        /// <summary>
        /// IOKit <c>IOServiceMatching</c>: creates a matching dictionary for the named I/O service class.
        /// The dictionary is consumed by <c>IOServiceGetMatchingServices</c>.
        /// </summary>
        [DllImport(IOKitLib)]
        private static extern IntPtr IOServiceMatching(string name);

        /// <summary>
        /// IOKit <c>IOServiceGetMatchingServices</c>: returns an iterator over the registry entries that match
        /// the dictionary. The main port 0 selects the default. Returns 0 (<c>KERN_SUCCESS</c>) on success.
        /// </summary>
        [DllImport(IOKitLib)]
        private static extern int IOServiceGetMatchingServices(uint masterPort, IntPtr matching, out uint iterator);

        /// <summary>
        /// IOKit <c>IOIteratorNext</c>: returns the next registry entry of the iterator, or 0 at the end.
        /// The caller must release each returned entry.
        /// </summary>
        [DllImport(IOKitLib)]
        private static extern uint IOIteratorNext(uint iterator);

        /// <summary>
        /// IOKit <c>IOObjectRelease</c>: releases an iterator or registry entry obtained from IOKit.
        /// </summary>
        [DllImport(IOKitLib)]
        private static extern int IOObjectRelease(uint handle);

        /// <summary>
        /// IOKit <c>IORegistryEntryCreateCFProperty</c>: returns a retained CoreFoundation copy of one property
        /// of a registry entry, or a null pointer when the property does not exist. The caller must release it.
        /// </summary>
        [DllImport(IOKitLib)]
        private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

        /// <summary>
        /// IOKit <c>IORegistryEntrySearchCFProperty</c>: searches a registry entry (and, depending on the options,
        /// its children or parents) in the named plane for a property and returns a retained copy, or a null
        /// pointer when none is found. The caller must release it.
        /// </summary>
        [DllImport(IOKitLib, CharSet = CharSet.Ansi)]
        private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, string plane, IntPtr key, IntPtr allocator, uint options);

        /// <summary>
        /// CoreFoundation <c>CFStringCreateWithCString</c>: creates a CFString from a C string in the given
        /// encoding. The caller must release the result.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        /// <summary>
        /// CoreFoundation <c>CFDictionaryGetValue</c>: returns the value stored for a key, or a null pointer when
        /// absent. The result is not retained, so it must not be released.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        /// <summary>
        /// CoreFoundation <c>CFNumberGetValue</c>: converts a CFNumber into the requested numeric type.
        /// Returns whether the conversion was lossless.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

        /// <summary>
        /// CoreFoundation <c>CFStringGetCString</c>: copies a CFString into a null-terminated buffer in the given
        /// encoding. Returns false when the string cannot be converted or does not fit into the buffer.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, long bufferSize, uint encoding);

        /// <summary>
        /// CoreFoundation <c>CFDataGetLength</c>: returns the number of bytes in a CFData object.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern long CFDataGetLength(IntPtr data);

        /// <summary>
        /// CoreFoundation <c>CFDataGetBytePtr</c>: returns a read-only pointer to the bytes of a CFData object.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        /// <summary>
        /// CoreFoundation <c>CFRelease</c>: releases one reference to a CoreFoundation object.
        /// </summary>
        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr handle);
    }
}
