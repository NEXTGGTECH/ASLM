// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;

namespace ASLM.Services.Internal;

/// <summary>
/// Provides filesystem operations that must also work between a native Linux temp directory and another mount.
/// </summary>
internal static class LinuxFileOperations
{
    /// <summary>
    /// Moves a runtime while preserving symlinks and permissions, including moves across filesystem boundaries.
    /// </summary>
    internal static void MoveDirectory(string source, string destination)
    {
        // Coreutils handles EXDEV by copying metadata and links before removing the source. Directory.Move
        // cannot perform that fallback when, for example, /tmp and a portable installation use different mounts.
        var start = new ProcessStartInfo("/bin/mv")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-T");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the directory move.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"Could not move '{source}' to '{destination}': {error.Trim()}");
    }
}
