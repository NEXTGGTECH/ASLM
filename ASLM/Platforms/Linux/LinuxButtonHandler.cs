// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platform;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Renders MAUI button images and content layout, which the preview GTK button handler leaves unimplemented.
/// </summary>
public sealed class LinuxButtonHandler : ButtonHandler
{
    private Gtk.Box? _content;
    private Gtk.Label? _label;
    private Image? _image;
    private Gtk.Widget? _nativeImage;
    private Button.ButtonContentLayout.ImagePosition? _position;

    // Mapping

    /// <summary>
    /// Keeps the backend's click, color and font mappings while replacing its text-only content path.
    /// </summary>
    static LinuxButtonHandler()
    {
        Mapper.ModifyMapping(nameof(ITextButton.Text), static (handler, button, original) =>
        {
            if (handler is LinuxButtonHandler linux) linux.ApplyContent();
            else original?.Invoke(handler, button);
        });
        Mapper.ModifyMapping(nameof(IImageButton.Source), static (handler, button, original) =>
        {
            if (handler is LinuxButtonHandler linux) linux.ApplyContent();
            else original?.Invoke(handler, button);
        });
        Mapper.AppendToMapping(nameof(Button.ContentLayout), static (handler, _) =>
        {
            if (handler is LinuxButtonHandler linux) linux.ApplyContent();
        });
    }

    // Native content

    /// <summary>
    /// Updates text and image independently so localization cannot replace the icon with GTK's text-only label.
    /// </summary>
    private void ApplyContent()
    {
        if (VirtualView is not Button button || MauiContext == null) return;
        EnsureContent(button);
        var text = button.Text ?? string.Empty;
        _label!.SetText(text);
        _label.SetVisible(text.Length > 0);
        _image!.Source = button.ImageSource;
        _nativeImage!.SetVisible(button.ImageSource != null);

        // A bounded image uses the existing image handler for streams, packaged files and font sources.
        // Its bitmap dimensions must not inflate a sidebar button's natural minimum width or height.
        var layout = button.ContentLayout;
        var position = layout.Position;
        _content!.SetOrientation(position is Button.ButtonContentLayout.ImagePosition.Top or Button.ButtonContentLayout.ImagePosition.Bottom
            ? Gtk.Orientation.Vertical : Gtk.Orientation.Horizontal);
        _content.SetSpacing(text.Length > 0 && button.ImageSource != null ? (int)layout.Spacing : 0);
        if (_position != position)
        {
            for (var child = _content.GetFirstChild(); child != null; child = _content.GetFirstChild()) _content.Remove(child);
            var imageFirst = position is Button.ButtonContentLayout.ImagePosition.Left or Button.ButtonContentLayout.ImagePosition.Top;
            _content.Append(imageFirst ? _nativeImage : _label);
            _content.Append(imageFirst ? _label : _nativeImage);
            _position = position;
        }
        LinuxLayoutManager.RequestLayout(PlatformView);
    }

    /// <summary>
    /// Creates one reusable native image/text row instead of reloading the icon for every layout or label change.
    /// </summary>
    private void EnsureContent(Button button)
    {
        if (_content != null) return;
        _content = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _content.SetHalign(Gtk.Align.Center);
        _content.SetValign(Gtk.Align.Center);
        _label = Gtk.Label.New(string.Empty);
        _image = new Image { WidthRequest = 20, HeightRequest = 20, Aspect = Aspect.AspectFit, InputTransparent = true };
        button.AddLogicalChild(_image);
        _nativeImage = (Gtk.Widget)_image.ToPlatform(MauiContext!);
        _nativeImage.SetSizeRequest(20, 20);
        PlatformView.SetChild(_content);
    }

    /// <summary>
    /// Left-aligns the sidebar content without changing the layout of ordinary action buttons.
    /// </summary>
    internal void AlignContentStart() => _content?.SetHalign(Gtk.Align.Start);

    /// <summary>
    /// Cancels pending icon loads and removes the image's logical parent before a native button is released.
    /// </summary>
    protected override void DisconnectHandler(Gtk.Button platformView)
    {
        if (_image != null)
        {
            if (VirtualView is Button button) button.RemoveLogicalChild(_image);
            _image.Handler?.DisconnectHandler();
        }
        _image = null;
        _nativeImage = null;
        _content = null;
        _label = null;
        _position = null;
        base.DisconnectHandler(platformView);
    }
}
