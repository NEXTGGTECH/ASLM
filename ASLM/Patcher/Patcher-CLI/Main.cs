// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Patcher;

/// <summary>
/// Console entry point for the headless patcher helper used on macOS.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Runs the shared patch operation and returns its exit code.
    /// </summary>
    private static Task<int> Main(string[] args)
    {
        return PatcherRunner.RunAsync(args, progress: null);
    }
}
