// Copyright NEXTGGTECH. Apache License 2.0.

using UIKit;

namespace ASLM
{
    // macOS application entry point

    /// <summary>
    /// Hosts the Mac Catalyst entry point for the MAUI app.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Applies a staged self-update when one is pending, then starts the UI application.
        /// </summary>
        private static void Main(string[] args)
        {
            if (MacPendingUpdateGate.TryHandOffToPatcher())
            {
                return;
            }

            UIApplication.Main(args, null, typeof(AppDelegate));
        }
    }
}
