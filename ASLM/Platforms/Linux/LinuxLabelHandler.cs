// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;
using Font = Microsoft.Maui.Font;

namespace ASLM.Services.Controls;

/// <summary>
/// Uses ASLM's packaged text font when a shared label style does not specify a family.
/// </summary>
public sealed class LinuxLabelHandler : LabelHandler
{
    // Font mapping

    /// <summary>
    /// Keeps explicit families authoritative and prevents settings captions from falling back to a desktop-theme font.
    /// </summary>
    static LinuxLabelHandler()
    {
        Mapper.ModifyMapping(nameof(ILabel.Font), static (handler, label, original) =>
        {
            if (handler is LinuxLabelHandler linux) linux.ApplyFont(label.Font);
            else original?.Invoke(handler, label);
        });
    }

    /// <summary>
    /// Resolves font aliases through the backend and resets weight/slant when a live label returns to regular text.
    /// </summary>
    private void ApplyFont(Font font)
    {
        var effective = string.IsNullOrWhiteSpace(font.Family)
            ? Font.OfSize("OpenSansRegular", font.Size, font.Weight, font.Slant, font.AutoScalingEnabled)
            : font;
        // BuildFontCss registers the packaged file with Pango. Preserve the original size and font attributes;
        // only the unspecified family changes, so settings and ordinary captions use the same fallback.
        var style = effective.Slant == FontSlant.Italic ? "italic" : effective.Slant == FontSlant.Oblique ? "oblique" : "normal";
        UpdateCss(PlatformView, BuildFontCss(effective) +
            FormattableString.Invariant($"font-weight: {(int)effective.Weight}; font-style: {style};"));
    }
}
