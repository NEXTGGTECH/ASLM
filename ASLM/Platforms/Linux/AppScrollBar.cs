// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls;

public sealed partial class AppScrollBar
{
    /// <summary>
    /// Hides GTK scrollbars while retaining a scrollable viewport on the configured axes.
    /// </summary>
    partial void ApplyPlatformChrome()
    {
        if (_connected is not ScrollView { Handler.PlatformView: Gtk.ScrolledWindow viewer } scroll) return;

        // External retains scrollable content; Never would constrain it to the viewport in GTK.
        viewer.SetPolicy(scroll.Orientation is ScrollOrientation.Horizontal or ScrollOrientation.Both
                ? Gtk.PolicyType.External : Gtk.PolicyType.Never,
            scroll.Orientation is ScrollOrientation.Vertical or ScrollOrientation.Both
                ? Gtk.PolicyType.External : Gtk.PolicyType.Never);
    }
}
