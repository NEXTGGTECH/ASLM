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

        private const int HostCpuLoadInfoFlavor = 3;
        private const int HostVmInfo64Flavor = 4;

        /// <summary>
        /// Reads cumulative CPU ticks shaped like the Windows kernel counters:
        /// kernel includes idle so that <c>1 - idle / (kernel + user)</c> stays valid.
        /// </summary>
        public static bool TryGetSystemCpuTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime)
        {
            idleTime = 0;
            kernelTime = 0;
            userTime = 0;

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
                var totalBytes = 0L;
                var length = (IntPtr)sizeof(long);
                if (sysctlbyname("hw.memsize", ref totalBytes, ref length, IntPtr.Zero, IntPtr.Zero) != 0 || totalBytes <= 0)
                {
                    return new HomeMemoryStatus();
                }

                var host = mach_host_self();
                if (host_page_size(host, out var pageSize) != 0)
                {
                    pageSize = (UIntPtr)16384;
                }

                var statistics = default(VmStatistics64);
                var count = (uint)(Marshal.SizeOf<VmStatistics64>() / sizeof(int));
                if (host_statistics64(host, HostVmInfo64Flavor, ref statistics, ref count) != 0)
                {
                    return new HomeMemoryStatus { TotalPhysicalBytes = totalBytes };
                }

                var availableBytes = (long)((statistics.FreeCount + (ulong)statistics.InactiveCount) * (ulong)pageSize);
                return new HomeMemoryStatus
                {
                    TotalPhysicalBytes = totalBytes,
                    UsedPhysicalBytes = Math.Max(0, totalBytes - availableBytes)
                };
            }
            catch
            {
                return new HomeMemoryStatus();
            }
        }


        // Per-process disk I/O

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
                return (0, 0);
            }
        }


        // Per-process socket counts

        private const int ProcPidListFds = 1;
        private const int ProxFdTypeSocket = 2;
        private const int FdInfoSize = 8;

        /// <summary>
        /// Counts open socket descriptors per tracked process (all address families).
        /// </summary>
        public static Dictionary<int, int> CountProcessSockets(IReadOnlyCollection<int> processIds)
        {
            var counts = new Dictionary<int, int>();

            foreach (var processId in processIds)
            {
                try
                {
                    var byteCount = proc_pidinfo(processId, ProcPidListFds, 0, null, 0);
                    if (byteCount <= 0)
                    {
                        continue;
                    }

                    var buffer = new byte[byteCount + 32 * FdInfoSize];
                    byteCount = proc_pidinfo(processId, ProcPidListFds, 0, buffer, buffer.Length);
                    if (byteCount <= 0)
                    {
                        continue;
                    }

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

        private static readonly object _diskSampleLock = new();
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

                    var readDelta = current.BytesRead >= previous.BytesRead ? current.BytesRead - previous.BytesRead : 0;
                    var writeDelta = current.BytesWritten >= previous.BytesWritten ? current.BytesWritten - previous.BytesWritten : 0;
                    var busyDeltaNs = current.TotalTimeNs >= previous.TotalTimeNs ? current.TotalTimeNs - previous.TotalTimeNs : 0;

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
                return new HomeDiskStats();
            }
        }

        /// <summary>
        /// Sums the IOBlockStorageDriver statistics dictionaries across all drivers.
        /// </summary>
        private static DiskSample? CaptureDiskSample()
        {
            if (IOServiceGetMatchingServices(0, IOServiceMatching("IOBlockStorageDriver"), out var iterator) != 0)
            {
                return null;
            }

            var statisticsKey = CreateCfString("Statistics");
            var bytesReadKey = CreateCfString("Bytes (Read)");
            var bytesWrittenKey = CreateCfString("Bytes (Write)");
            var timeReadKey = CreateCfString("Total Time (Read)");
            var timeWriteKey = CreateCfString("Total Time (Write)");

            try
            {
                var sample = new DiskSample { TimestampUtc = DateTimeOffset.UtcNow };

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

        private sealed class DiskSample
        {
            public DateTimeOffset TimestampUtc { get; init; }
            public ulong BytesRead { get; set; }
            public ulong BytesWritten { get; set; }
            public ulong TotalTimeNs { get; set; }
        }


        // GPU utilization

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
                if (IOServiceGetMatchingServices(0, IOServiceMatching("IOAccelerator"), out var iterator) != 0)
                {
                    return EmptyGpuResult();
                }

                var statisticsKey = CreateCfString("PerformanceStatistics");
                var utilizationKey = CreateCfString("Device Utilization %");
                var modelKey = CreateCfString("model");

                try
                {
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

        private const uint CfStringEncodingUtf8 = 0x08000100;
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
            var buffer = new byte[256];
            if (CFStringGetCString(value, buffer, buffer.Length, CfStringEncodingUtf8))
            {
                var terminator = Array.IndexOf(buffer, (byte)0);
                return Encoding.UTF8.GetString(buffer, 0, terminator >= 0 ? terminator : buffer.Length).Trim();
            }

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

        private const string SystemLib = "/usr/lib/libSystem.dylib";
        private const string ProcLib = "/usr/lib/libproc.dylib";
        private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [StructLayout(LayoutKind.Sequential)]
        private struct HostCpuLoadInfo
        {
            public uint User;
            public uint System;
            public uint Idle;
            public uint Nice;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VmStatistics64
        {
            public uint FreeCount;
            public uint ActiveCount;
            public uint InactiveCount;
            public uint WireCount;
            public ulong ZeroFillCount;
            public ulong Reactivations;
            public ulong Pageins;
            public ulong Pageouts;
            public ulong Faults;
            public ulong CowFaults;
            public ulong Lookups;
            public ulong Hits;
            public ulong Purges;
            public uint PurgeableCount;
            public uint SpeculativeCount;
            public ulong Decompressions;
            public ulong Compressions;
            public ulong Swapins;
            public ulong Swapouts;
            public uint CompressorPageCount;
            public uint ThrottledCount;
            public uint ExternalPageCount;
            public uint InternalPageCount;
            public ulong TotalUncompressedPagesInCompressor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RUsageInfoV2
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Uuid;
            public ulong UserTime;
            public ulong SystemTime;
            public ulong PkgIdleWkups;
            public ulong InterruptWkups;
            public ulong Pageins;
            public ulong WiredSize;
            public ulong ResidentSize;
            public ulong PhysFootprint;
            public ulong ProcStartAbstime;
            public ulong ProcExitAbstime;
            public ulong ChildUserTime;
            public ulong ChildSystemTime;
            public ulong ChildPkgIdleWkups;
            public ulong ChildInterruptWkups;
            public ulong ChildPageins;
            public ulong ChildElapsedAbstime;
            public ulong DiskIoBytesRead;
            public ulong DiskIoBytesWritten;
        }

        [DllImport(SystemLib)]
        private static extern uint mach_host_self();

        [DllImport(SystemLib)]
        private static extern int host_statistics(uint host, int flavor, ref HostCpuLoadInfo info, ref uint count);

        [DllImport(SystemLib)]
        private static extern int host_statistics64(uint host, int flavor, ref VmStatistics64 info, ref uint count);

        [DllImport(SystemLib)]
        private static extern int host_page_size(uint host, out UIntPtr pageSize);

        [DllImport(SystemLib)]
        private static extern int sysctlbyname(string name, ref long value, ref IntPtr length, IntPtr newValue, IntPtr newLength);

        [DllImport(ProcLib)]
        private static extern int proc_pid_rusage(int pid, int flavor, ref RUsageInfoV2 buffer);

        [DllImport(ProcLib)]
        private static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[]? buffer, int buffersize);

        [DllImport(IOKitLib)]
        private static extern IntPtr IOServiceMatching(string name);

        [DllImport(IOKitLib)]
        private static extern int IOServiceGetMatchingServices(uint masterPort, IntPtr matching, out uint iterator);

        [DllImport(IOKitLib)]
        private static extern uint IOIteratorNext(uint iterator);

        [DllImport(IOKitLib)]
        private static extern int IOObjectRelease(uint handle);

        [DllImport(IOKitLib)]
        private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);

        [DllImport(IOKitLib, CharSet = CharSet.Ansi)]
        private static extern IntPtr IORegistryEntrySearchCFProperty(uint entry, string plane, IntPtr key, IntPtr allocator, uint options);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [DllImport(CoreFoundationLib)]
        private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);

        [DllImport(CoreFoundationLib)]
        private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, long bufferSize, uint encoding);

        [DllImport(CoreFoundationLib)]
        private static extern long CFDataGetLength(IntPtr data);

        [DllImport(CoreFoundationLib)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(CoreFoundationLib)]
        private static extern void CFRelease(IntPtr handle);
    }
}
