// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Patcher;

/// <summary>
/// Hosts the patcher status window.
/// </summary>
public partial class App : Application
{
    // Application construction

    /// <summary>
    /// Creates the patcher application and opens the status page.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }


    // Window creation

    /// <summary>
    /// Creates a compact status window for the update flow.
    /// </summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        const int width = 720;
        const int height = 540;

        var window = new Window(new MainPage());
        window.Title = "ASLM Patcher";

        // Keep the patcher window at a fixed size so it reads as a lightweight status dialog.
        window.Width = width;
        window.Height = height;

        window.MinimumWidth = width;
        window.MaximumWidth = width;
        window.MinimumHeight = height;
        window.MaximumHeight = height;

        return window;
    }
}
