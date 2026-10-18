// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace ASLM.Services.Internal;

/// <summary>
/// Relaunches the Linux host only after the previous instance has stopped its services and processes.
/// </summary>
internal static class LinuxAppRelauncher
{
    private const string WaitArgument = "--aslm-wait-process";

    /// <summary>
    /// Starts a waiting instance without a shell, preserving paths containing spaces or non-ASCII characters.
    /// </summary>
    internal static void StartDetachedRelaunch()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("ASLM executable was not found.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };

        // Framework-dependent launches need the entry assembly; published apphosts do not.
        if (string.Equals(Path.GetFileName(executable), "dotnet", StringComparison.Ordinal))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        using var current = Process.GetCurrentProcess();
        start.ArgumentList.Add(WaitArgument);
        start.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        using var child = Process.Start(start) ?? throw new InvalidOperationException("ASLM could not be restarted.");
    }

    /// <summary>
    /// Consumes the internal restart handshake before GTK parses application arguments.
    /// </summary>
    internal static bool WaitForPreviousInstance(ref string[] args)
    {
        if (args.Length == 0 || args[0] != WaitArgument) return true;
        if (args.Length != 3 || !int.TryParse(args[1], out var pid) || pid <= 0 ||
            !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var started)) return false;
        args = [];
        try
        {
            // A PID may have been reused while the new process started; never wait for an unrelated process.
            using var previous = Process.GetProcessById(pid);
            if (previous.StartTime.ToUniversalTime().Ticks != started) return true;
            return previous.WaitForExit(60_000);
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
}
