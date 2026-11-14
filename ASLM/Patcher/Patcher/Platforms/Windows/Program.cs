// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.UI.Dispatching;

namespace ASLM.Patcher.WinUI
{
    /// <summary>
    /// Starts the Windows patcher. It replaces the entry point that WinUI generates, so the elevation for the shared
    /// installation folder is decided before any window appears.
    /// </summary>
    public static class Program
    {
        // Process startup

        /// <summary>
        /// Hands the update to an elevated copy when the shared installation folder needs one; otherwise starts the
        /// patcher window, which applies the update itself.
        /// </summary>
        /// <returns>The exit code of the elevated copy, or the patch result that the window recorded.</returns>
        [STAThread]
        private static int Main(string[] args)
        {
            // This process shows no window while it waits; the elevated copy shows the progress.
            if (PatcherRunner.TryRunElevated(args) is { } exitCode)
            {
                return exitCode;
            }

            // Start the WinUI application the way the generated entry point does.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            // The patcher window records the patch result before it quits the application.
            return Environment.ExitCode;
        }
    }
}
