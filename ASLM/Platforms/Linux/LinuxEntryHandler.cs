// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Implements MAUI's WhileEditing clear-button policy instead of leaving a permanent GTK delete icon.
    /// </summary>
    public sealed class LinuxEntryHandler : EntryHandler
    {
        // Fields

        /// <summary>
        /// Focus controller attached to the entry while the handler is connected.
        /// </summary>
        private Gtk.EventControllerFocus? _focus;

        /// <summary>
        /// Whether the entry currently has keyboard focus, which the WhileEditing policy depends on.
        /// </summary>
        private bool _editing;


        // Mapping

        /// <summary>
        /// Refreshes clear-button visibility when text, focus policy or editability changes.
        /// </summary>
        static LinuxEntryHandler()
        {
            // The clear-button policy itself is replaced; other handler types keep the original mapping.
            Mapper.ModifyMapping(nameof(IEntry.ClearButtonVisibility), static (handler, entry, original) =>
            {
                if (handler is LinuxEntryHandler linux) linux.ApplyClearButton();
                else original?.Invoke(handler, entry);
            });

            // The icon depends on the text, editability and enabled state too, so those changes re-evaluate it.
            foreach (var property in new[] { nameof(IEntry.Text), nameof(IEntry.IsReadOnly), nameof(IView.IsEnabled) })
                Mapper.AppendToMapping(property, static (handler, _) =>
                {
                    if (handler is LinuxEntryHandler linux) linux.ApplyClearButton();
                });
        }


        // Native lifetime

        /// <summary>
        /// Tracks focus inside Gtk.Entry's text child and handles its secondary icon through GTK's public signals.
        /// </summary>
        protected override void ConnectHandler(Gtk.Entry platformView)
        {
            base.ConnectHandler(platformView);

            // Focus enter/leave on the entry tells when editing starts and ends; a release of the secondary
            // icon is the click on the clear button.
            _focus = Gtk.EventControllerFocus.New();
            _focus.OnEnter += OnFocusEntered;
            _focus.OnLeave += OnFocusLeft;
            platformView.AddController(_focus);
            platformView.OnIconRelease += OnClearIconReleased;
        }

        /// <summary>
        /// Releases callbacks before the entry is reused or removed from a page.
        /// </summary>
        protected override void DisconnectHandler(Gtk.Entry platformView)
        {
            platformView.OnIconRelease -= OnClearIconReleased;

            // Unhook the focus controller and detach it, so a reused entry does not keep a stale controller.
            if (_focus != null)
            {
                _focus.OnEnter -= OnFocusEntered;
                _focus.OnLeave -= OnFocusLeft;
                platformView.RemoveController(_focus);
                _focus.Dispose();
            }

            _focus = null;
            _editing = false;
            base.DisconnectHandler(platformView);
        }


        // Editing

        /// <summary>
        /// Shows the clear action only after the user actually focuses a nonempty editable entry.
        /// </summary>
        private void OnFocusEntered(Gtk.EventControllerFocus sender, EventArgs args)
        {
            _editing = true;
            ApplyClearButton();
        }

        /// <summary>
        /// Hides the clear action when focus leaves the entry, even if its text remains populated.
        /// </summary>
        private void OnFocusLeft(Gtk.EventControllerFocus sender, EventArgs args)
        {
            _editing = false;
            ApplyClearButton();
        }

        /// <summary>
        /// Clears through the normal native text-change path, preserving bindings and search filtering.
        /// </summary>
        private void OnClearIconReleased(Gtk.Entry sender, Gtk.Entry.IconReleaseSignalArgs args)
        {
            if (args.IconPos == Gtk.EntryIconPosition.Secondary && CanClear()) sender.SetText(string.Empty);
        }

        /// <summary>
        /// Resolves the MAUI visibility contract without exposing a no-op delete icon in empty search fields.
        /// </summary>
        private bool CanClear() => _editing && VirtualView.ClearButtonVisibility == ClearButtonVisibility.WhileEditing &&
            VirtualView.IsEnabled && !VirtualView.IsReadOnly && !string.IsNullOrEmpty(VirtualView.Text);

        /// <summary>
        /// Adds or removes the GTK icon rather than merely making a permanently allocated icon transparent.
        /// </summary>
        private void ApplyClearButton()
        {
            // Passing a null icon name removes the icon; the icon is also made (in)activatable and (in)sensitive in step.
            var visible = CanClear();
            PlatformView.SetIconFromIconName(Gtk.EntryIconPosition.Secondary, visible ? "edit-clear-symbolic" : null);
            PlatformView.SetIconActivatable(Gtk.EntryIconPosition.Secondary, visible);
            PlatformView.SetIconSensitive(Gtk.EntryIconPosition.Secondary, visible);
        }
    }
}
