// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls
{
    /// <summary>
    /// Linux part: hides GTK's native scrollbars so the shared scrollbar is the only one shown.
    /// </summary>
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
}
