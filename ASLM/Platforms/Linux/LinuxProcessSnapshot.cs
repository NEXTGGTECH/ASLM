// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;

namespace ASLM.Services.Internal;

/// <summary>
/// Reads parent-child process relationships from procfs without spawning shell commands.
/// </summary>
internal static class LinuxProcessSnapshot
{
    /// <summary>
    /// Captures visible Linux processes while tolerating processes exiting during enumeration.
    /// </summary>
    internal static IReadOnlyList<ProcessSnapshotEntry> Capture(string procRoot = "/proc")
    {
        var entries = new List<ProcessSnapshotEntry>();
        foreach (var directory in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            try
            {
                if (TryParse(File.ReadAllText(Path.Combine(directory, "stat")), out var entry)) entries.Add(entry!);
            }
            catch (IOException) { /* A process can exit before its stat file is read. */ }
            catch (UnauthorizedAccessException) { /* Procfs may hide processes belonging to other users. */ }
        }
        return entries;
    }

    /// <summary>
    /// Extracts PID, command and parent PID without splitting command names containing spaces or parentheses.
    /// </summary>
    internal static bool TryParse(string stat, out ProcessSnapshotEntry? entry)
    {
        entry = null;
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open <= 0 || close <= open || close + 2 >= stat.Length) return false;
        if (!int.TryParse(stat.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            return false;

        // Fields after comm begin with state and ppid. Later numeric fields are irrelevant to the snapshot.
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0].Length != 1 ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)) return false;
        entry = new ProcessSnapshotEntry(pid, parent, stat[(open + 1)..close]);
        return true;
    }
}
