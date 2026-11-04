// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Installer
{
    /// <summary>
    /// Hosts the installer wizard and configures the primary window.
    /// </summary>
    public partial class App : Application
    {
        // Application lifecycle

        /// <summary>
        /// Creates the installer application and loads its XAML resources.
        /// </summary>
        public App()
        {
            InitializeComponent();
        }


        // Window configuration

        /// <summary>
        /// Creates the primary installer window and pins its minimum and maximum size to a fixed 720x540,
        /// so it cannot be resized.
        /// </summary>
        protected override Window CreateWindow(IActivationState? activationState)
        {
            const int width = 720;
            const int height = 540;

            var window = new Window(new MainPage());
            window.Title = "ASLM Installer";

            window.Width = width;
            window.Height = height;

            window.MinimumWidth = width;
            window.MaximumWidth = width;
            window.MinimumHeight = height;
            window.MaximumHeight = height;

            return window;
        }
    }
}
