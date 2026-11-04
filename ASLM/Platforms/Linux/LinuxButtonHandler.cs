// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platform;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Renders MAUI button images and content layout, which the preview GTK button handler leaves unimplemented.
    /// </summary>
    public sealed class LinuxButtonHandler : ButtonHandler
    {
        // Fields

        /// <summary>
        /// Row that holds the image and the label inside the native button, created on first use.
        /// </summary>
        private Gtk.Box? _content;

        /// <summary>
        /// Native label showing the button text.
        /// </summary>
        private Gtk.Label? _label;

        /// <summary>
        /// MAUI image control that loads the button icon through the shared image handler.
        /// </summary>
        private Image? _image;

        /// <summary>
        /// Native widget created for <see cref="_image"/> and placed into <see cref="_content"/>.
        /// </summary>
        private Gtk.Widget? _nativeImage;

        /// <summary>
        /// Image position the children of <see cref="_content"/> were last ordered for, or null before the first layout.
        /// </summary>
        private Button.ButtonContentLayout.ImagePosition? _position;


        // Mapping

        /// <summary>
        /// Keeps the backend's click, color and font mappings while replacing its text-only content path.
        /// </summary>
        static LinuxButtonHandler()
        {
            // Text, image source and content layout all rebuild the shared image/text row. The original mapping
            // still runs for any other handler type, because the mapper is shared with the base ButtonHandler.
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

            // Show the label and the image only when they have content, so an empty one takes no space.
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

            // GTK boxes order children by insertion, so a changed image position re-appends both children.
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

            // Centered row that replaces the button's default text-only child.
            _content = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            _content.SetHalign(Gtk.Align.Center);
            _content.SetValign(Gtk.Align.Center);
            _label = Gtk.Label.New(string.Empty);

            // The icon is registered as a logical child so it has the button as its MAUI parent (removed again on
            // disconnect); the fixed 20x20 size keeps the bitmap from inflating the button's natural size.
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


        // Lifecycle

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

            // Drop all references so a reconnected handler rebuilds its content row from scratch.
            _image = null;
            _nativeImage = null;
            _content = null;
            _label = null;
            _position = null;
            base.DisconnectHandler(platformView);
        }
    }
}
