// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Controls;

namespace ASLM.Pages;

public partial class AppShellPage
{
    /// <summary>
    /// Aligns the image/text row of GTK sidebar buttons to the same leading edge as Windows.
    /// </summary>
    partial void ApplyPlatformButtonAlignment(Button button)
    {
        if (button.Handler is LinuxButtonHandler handler) handler.AlignContentStart();
    }
}
