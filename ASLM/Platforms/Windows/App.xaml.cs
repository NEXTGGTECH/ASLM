// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.UI.Xaml;

namespace ASLM.WinUI
{
    // Windows application entry point

    /// <summary>
    /// Hosts the WinUI application wrapper for the MAUI app.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        // Initialization

        /// <summary>
        /// Creates the WinUI application instance.
        /// </summary>
        public App()
        {
            InitializeComponent();
        }


        // MAUI bootstrap

        /// <summary>
        /// Builds the shared MAUI application instance for the Windows host.
        /// </summary>
        protected override MauiApp CreateMauiApp()
        {
            return MauiProgram.CreateMauiApp();
        }
    }
}
