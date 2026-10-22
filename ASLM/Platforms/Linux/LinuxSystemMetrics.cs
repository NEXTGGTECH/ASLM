// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;

namespace ASLM.Services.Internal;

/// <summary>
/// Reads Linux CPU, memory and I/O counters without depending on Windows performance providers.
/// </summary>
internal static class LinuxSystemMetrics
{
    private static readonly object DiskGate = new();
    private static (DateTimeOffset Time, ulong Read, ulong Write, ulong Busy)? _lastDisk;

    /// <summary>
    /// Converts procfs CPU fields to the idle-inclusive kernel counters expected by the shared sampler.
    /// </summary>
    internal static bool TryGetSystemCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        try
        {
            var fields = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 || fields[0] != "cpu") return false;
            idle = Parse(fields[4]) + Parse(fields[5]);
            user = Parse(fields[1]) + Parse(fields[2]);
            kernel = idle + Parse(fields[3]) + Parse(fields[6]) + Parse(fields[7]) + Parse(fields[8]);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Uses MemAvailable so reclaimable file cache is not incorrectly reported as unavailable memory.
    /// </summary>
    internal static HomeMemoryStatus GetMemoryStatus()
    {
        var fields = ReadCounters("/proc/meminfo");
        var total = fields.GetValueOrDefault("MemTotal") * 1024;
        var available = fields.GetValueOrDefault("MemAvailable", fields.GetValueOrDefault("MemFree")) * 1024;
        return new HomeMemoryStatus
        {
            TotalPhysicalBytes = (long)total,
            UsedPhysicalBytes = (long)(total - Math.Min(total, available))
        };
    }

    /// <summary>
    /// Reads actual storage I/O rather than counting bytes satisfied from the process's page cache.
    /// </summary>
    internal static (ulong Read, ulong Write) GetProcessDiskIo(int processId)
    {
        var fields = ReadCounters($"/proc/{processId}/io");
        return (fields.GetValueOrDefault("read_bytes"), fields.GetValueOrDefault("write_bytes"));
    }

    /// <summary>
    /// Samples whole-device disk counters without counting partitions or loop-backed files a second time.
    /// </summary>
    internal static HomeDiskStats SampleDiskStats()
    {
        lock (DiskGate)
        {
            ulong read = 0, write = 0, busy = 0;
            try
            {
                foreach (var line in File.ReadLines("/proc/diskstats"))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 14) continue;
                    var device = fields[2];
                    if (device.StartsWith("loop", StringComparison.Ordinal) || device.StartsWith("ram", StringComparison.Ordinal) ||
                        device.StartsWith("dm-", StringComparison.Ordinal) || !Directory.Exists($"/sys/block/{device}")) continue;
                    read += Parse(fields[5]) * 512;
                    write += Parse(fields[9]) * 512;
                    busy += Parse(fields[12]);
                }
            }
            catch (IOException) { _lastDisk = null; return new HomeDiskStats(); }
            catch (UnauthorizedAccessException) { _lastDisk = null; return new HomeDiskStats(); }

            var now = DateTimeOffset.UtcNow;
            var previous = _lastDisk;
            _lastDisk = (now, read, write, busy);
            if (previous is not { } last || (now - last.Time).TotalSeconds is not > 0) return new HomeDiskStats();
            var seconds = (now - last.Time).TotalSeconds;
            return new HomeDiskStats
            {
                ReadBytesPerSecond = (read >= last.Read ? read - last.Read : 0) / seconds,
                WriteBytesPerSecond = (write >= last.Write ? write - last.Write : 0) / seconds,
                BusyPercent = Math.Clamp((busy >= last.Busy ? busy - last.Busy : 0) / (seconds * 10), 0, 100)
            };
        }
    }

    /// <summary>
    /// Counts visible TCP/UDP sockets by matching process descriptors against procfs network inodes.
    /// </summary>
    internal static Dictionary<int, int> CountProcessSockets(IEnumerable<int> processIds)
    {
        var sockets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var protocol in new[] { "tcp", "tcp6", "udp", "udp6" })
        {
            try
            {
                foreach (var line in File.ReadLines($"/proc/net/{protocol}").Skip(1))
                {
                    var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 9) sockets.Add($"socket:[{fields[9]}]");
                }
            }
            catch (IOException) { /* A network namespace may not expose every protocol. */ }
            catch (UnauthorizedAccessException) { /* Restricted procfs mounts may hide network tables. */ }
        }

        var result = new Dictionary<int, int>();
        foreach (var pid in processIds)
        {
            try
            {
                var owned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles($"/proc/{pid}/fd"))
                {
                    var target = new FileInfo(file).LinkTarget;
                    if (target != null && sockets.Contains(target)) owned.Add(target);
                }
                result[pid] = owned.Count;
            }
            catch (IOException) { /* Processes and descriptors can exit while being sampled. */ }
            catch (UnauthorizedAccessException) { /* Only visible descriptors are included. */ }
        }
        return result;
    }

    /// <summary>
    /// Reads named unsigned counters from procfs, tolerating process exit during a sample.
    /// </summary>
    private static Dictionary<string, ulong> ReadCounters(string path)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var fields = line.Split([' ', ':', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1) result[fields[0]] = Parse(fields[1]);
            }
        }
        catch (IOException) { /* Missing procfs entries are unavailable samples. */ }
        catch (UnauthorizedAccessException) { /* Respect process and filesystem access restrictions. */ }
        return result;
    }

    /// <summary>
    /// Parses kernel counters independently of the application's selected locale.
    /// </summary>
    private static ulong Parse(string value) => ulong.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
}
