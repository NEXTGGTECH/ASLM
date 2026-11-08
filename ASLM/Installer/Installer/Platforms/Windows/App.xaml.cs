// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.UI.Xaml;

namespace ASLM.Installer.WinUI
{
    /// <summary>
    /// Starts the MAUI installer on Windows.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        // Windows application lifecycle

        /// <summary>
        /// Creates the Windows MAUI application host and loads its XAML resources.
        /// </summary>
        public App()
        {
            InitializeComponent();
        }


        // Cross-platform host creation

        /// <summary>
        /// Connects the Windows shell to the shared MAUI app built by <see cref="MauiProgram.CreateMauiApp"/>.
        /// </summary>
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
}
