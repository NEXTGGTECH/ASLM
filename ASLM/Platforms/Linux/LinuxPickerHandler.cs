// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Preserves native selector input and closes its popup when the owning page is hidden or removed.
/// </summary>
public sealed class LinuxPickerHandler : PickerHandler
{
    private Gtk.DropDown? _native;

    // Mapping

    /// <summary>
    /// Replaces the backend's unsupported text-align CSS with GTK label alignment.
    /// </summary>
    static LinuxPickerHandler()
    {
        Mapper.ModifyMapping(nameof(IPicker.HorizontalTextAlignment), (handler, picker, original) =>
        {
            if (handler is LinuxPickerHandler linux) linux.ApplyAlignment(picker.HorizontalTextAlignment);
            else original?.Invoke(handler, picker);
        });
    }

    // Native lifetime

    /// <summary>
    /// Observes visibility while leaving row activation, scrolling and selection entirely with GTK.
    /// </summary>
    protected override void ConnectHandler(Gtk.DropDown platformView)
    {
        _native = platformView;
        platformView.OnUnmap += OnUnmapped;
        base.ConnectHandler(platformView);
    }

    /// <summary>
    /// Hides native popovers and disconnects callbacks before the selector is removed.
    /// </summary>
    protected override void DisconnectHandler(Gtk.DropDown platformView)
    {
        DismissPopover();
        platformView.OnUnmap -= OnUnmapped;
        _native = null;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Dismisses a popup when an ancestor is hidden or its page leaves the window.
    /// </summary>
    private void OnUnmapped(Gtk.Widget sender, EventArgs args) => DismissPopover();

    /// <summary>
    /// Closes only when leaving the control; GTK's closed signal resets the opener and completes native input handling.
    /// </summary>
    private void DismissPopover()
    {
        if (_native != null) ClosePopovers(_native);
    }

    /// <summary>
    /// Closes popovers through GTK's public widget tree without assuming a private child index or CSS name.
    /// </summary>
    private static void ClosePopovers(Gtk.Widget widget)
    {
        if (widget is Gtk.Popover popover)
        {
            popover.Popdown();
            return;
        }
        for (var child = widget.GetFirstChild(); child != null; child = child.GetNextSibling()) ClosePopovers(child);
    }

    /// <summary>
    /// Applies the shared text alignment to native display labels, which do not support CSS text-align.
    /// </summary>
    private void ApplyAlignment(TextAlignment alignment)
    {
        if (_native == null) return;
        AlignLabels(_native, alignment switch
        {
            TextAlignment.Center => .5f,
            TextAlignment.End => 1f,
            _ => 0f
        });
    }

    /// <summary>
    /// Updates GTK label alignment recursively after the native dropdown has created its display widget.
    /// </summary>
    private static void AlignLabels(Gtk.Widget widget, float alignment)
    {
        if (widget is Gtk.Label label) label.SetXalign(alignment);
        for (var child = widget.GetFirstChild(); child != null; child = child.GetNextSibling()) AlignLabels(child, alignment);
    }
}
