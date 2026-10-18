// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Applies MAUI size constraints that the preview GTK Border handler omits from measurement.
/// </summary>
public sealed class LinuxBorderHandler : BorderHandler
{
    /// <summary>
    /// Measures content within explicit dimensions and preserves requested dialog bounds.
    /// </summary>
    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        // Star-sized children need the requested dimensions during measurement, not only during arrangement.
        var width = double.IsFinite(VirtualView.Width) ? Math.Min(widthConstraint, VirtualView.Width) : widthConstraint;
        var height = double.IsFinite(VirtualView.Height) ? Math.Min(heightConstraint, VirtualView.Height) : heightConstraint;
        var measured = base.GetDesiredSize(width, height);
        return new Size(
            LayoutManager.ResolveConstraints(widthConstraint, VirtualView.Width, measured.Width, VirtualView.MinimumWidth, VirtualView.MaximumWidth),
            LayoutManager.ResolveConstraints(heightConstraint, VirtualView.Height, measured.Height, VirtualView.MinimumHeight, VirtualView.MaximumHeight));
    }
}
