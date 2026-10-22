// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace ASLM.Services.Internal;

/// <summary>
/// Connects MAUI measure invalidation to GTK's layout cycle when content changes without a window resize.
/// </summary>
internal static class LinuxLayoutManager
{
    private static readonly HashSet<GtkLayoutPanel> PendingRoots = [];
    private static bool _configured;
    private static bool _arranging;

    // Registration

    /// <summary>
    /// Completes the portable ViewHandler contract once for every GTK control, including nested layouts.
    /// </summary>
    internal static void Configure()
    {
        if (_configured) return;
        _configured = true;
        ViewHandler.ViewCommandMapper.AppendToMapping(nameof(IView.InvalidateMeasure),
            static (handler, _, _) => RequestLayout(handler.PlatformView as Gtk.Widget));
    }

    // Invalidation

    /// <summary>
    /// Coalesces a group of property changes into one root pass instead of laying out each changed label separately.
    /// </summary>
    internal static void RequestLayout(Gtk.Widget? widget)
    {
        if (_arranging) return;
        GtkLayoutPanel? root = null;
        for (var current = widget; current != null; current = current.GetParent())
            if (current is GtkLayoutPanel { CrossPlatformLayout: not null } panel) root = panel;
        if (root == null || !PendingRoots.Add(root)) return;

        // Use a one-shot UI callback. The backend's root driver invalidates while measuring; marking its
        // LayoutDirty flag from the same command would otherwise keep it measuring on every frame forever.
        GLib.Functions.IdleAdd(0, () =>
        {
            PendingRoots.Remove(root);
            ArrangeRoot(root);
            return false;
        });
    }

    /// <summary>
    /// Reflows the current managed tree against its allocated viewport, preserving the scrollable axis.
    /// </summary>
    private static void ArrangeRoot(GtkLayoutPanel panel)
    {
        if (panel.GetRoot() is not Gtk.Window || panel.CrossPlatformLayout is not IView view) return;
        var width = panel.GetAllocatedWidth();
        var height = panel.GetAllocatedHeight();
        ScrollOrientation? orientation = null;
        if (view.Parent is IScrollView scroll && panel.GetParent() is Gtk.Viewport viewport)
        {
            width = viewport.GetAllocatedWidth();
            height = viewport.GetAllocatedHeight();
            orientation = scroll.Orientation;
        }
        if (width <= 0 || height <= 0) return;

        _arranging = true;
        try
        {
            // Invalidate managed measurement too: native allocation alone cannot update Grid auto rows
            // or the natural width of a centered stack after changing the application language.
            if (view is VisualElement element) element.PlatformSizeChanged();
            var horizontal = orientation is ScrollOrientation.Horizontal or ScrollOrientation.Both;
            var vertical = orientation is ScrollOrientation.Vertical or ScrollOrientation.Both;
            var measured = panel.CrossPlatformMeasure(horizontal ? double.PositiveInfinity : width,
                vertical ? double.PositiveInfinity : height);
            panel.CrossPlatformArrange(new Rect(0, 0,
                horizontal ? Math.Max(width, measured.Width) : width,
                vertical ? Math.Max(height, measured.Height) : height));
            panel.QueueResize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LinuxLayout] Root layout failed: {ex}");
        }
        finally { _arranging = false; }
    }
}
