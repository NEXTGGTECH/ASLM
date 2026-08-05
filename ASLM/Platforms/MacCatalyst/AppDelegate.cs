// Copyright NEXTGGTECH. Apache License 2.0.

using Foundation;

namespace ASLM
{
    // macOS application delegate

    /// <summary>
    /// Hosts the Mac Catalyst application wrapper for the MAUI app.
    /// </summary>
    [Register("AppDelegate")]
    public class AppDelegate : MauiUIApplicationDelegate
    {
        /// <summary>
        /// Builds the shared MAUI application instance for the Mac Catalyst host.
        /// </summary>
        protected override MauiApp CreateMauiApp()
        {
            MacAppDataSeeder.EnsureSeeded();
            return MauiProgram.CreateMauiApp();
        }
    }
}
