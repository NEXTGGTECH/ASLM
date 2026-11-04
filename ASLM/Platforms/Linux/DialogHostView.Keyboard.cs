// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Dialogs
{
    /// <summary>
    /// Linux part: GTK keyboard navigation inside the dialog and pointer blocking of the page behind it.
    /// </summary>
    public partial class DialogHostView
    {
        // Fields

        /// <summary>
        /// Native root widget of the dialog host that owns the key controller, or null while disconnected.
        /// </summary>
        private Gtk.Widget? _keyboardRoot;

        /// <summary>
        /// Capture-phase key controller attached to <see cref="_keyboardRoot"/>.
        /// </summary>
        private Gtk.EventControllerKey? _keyController;

        /// <summary>
        /// Native widget of the underlying page whose pointer input is blocked while a dialog is open.
        /// </summary>
        private Gtk.Widget? _blockedBackground;

        /// <summary>
        /// Widget that had keyboard focus before the dialog opened, restored when the dialog closes.
        /// </summary>
        private Gtk.Widget? _previousFocus;

        /// <summary>
        /// Original <c>can-target</c> value of the blocked background, restored when the dialog closes.
        /// </summary>
        private bool _backgroundCanTarget;


        // Keyboard controller

        /// <summary>
        /// Captures dialog keyboard navigation before GTK can move focus into the underlying page.
        /// </summary>
        partial void ConnectKeyboard()
        {
            // Reconnecting must not leave a second controller on the previous root.
            DisconnectKeyboard();
            if (Handler?.PlatformView is not Gtk.Widget root) return;
            _keyboardRoot = root;

            // The capture phase runs before the focused child sees the key, so Tab and Escape cannot leak
            // into the page behind the dialog.
            _keyController = Gtk.EventControllerKey.New();
            _keyController.SetPropagationPhase(Gtk.PropagationPhase.Capture);
            _keyController.OnKeyPressed += OnNativeKeyPressed;
            root.AddController(_keyController);
        }

        /// <summary>
        /// Releases the keyboard controller before its native view is replaced.
        /// </summary>
        partial void DisconnectKeyboard()
        {
            if (_keyController != null)
            {
                _keyController.OnKeyPressed -= OnNativeKeyPressed;
                _keyboardRoot?.RemoveController(_keyController);
            }

            _keyController = null;
            _keyboardRoot = null;
        }


        // Modal background

        /// <summary>
        /// Blocks pointer interaction without restyling the entire underlying settings tree.
        /// </summary>
        partial void UpdateModalPlatform(bool visible)
        {
            // Opening: remember focus and the original target flag, then stop the background from receiving pointer input.
            if (visible && _blockedBackground == null && _background?.Handler?.PlatformView is Gtk.Widget background)
            {
                _previousFocus = (background.GetRoot() as Gtk.Window)?.GetFocus();
                _backgroundCanTarget = background.GetCanTarget();
                _blockedBackground = background;
                background.SetCanTarget(false);
            }
            else if (!visible && _blockedBackground is { } previous)
            {
                // Closing: restore the background, and give focus back only if it still lives in the same window.
                previous.SetCanTarget(_backgroundCanTarget);
                _blockedBackground = null;
                if (_previousFocus?.GetRoot() == previous.GetRoot()) _previousFocus?.GrabFocus();
                _previousFocus = null;
            }
        }


        // Key handling

        /// <summary>
        /// Cycles focus within the dialog and respects the request's configured dismissal policy.
        /// </summary>
        private bool OnNativeKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
        {
            // Without an open dialog the key belongs to the rest of the application.
            if (!IsOpen || _keyboardRoot == null) return false;

            // Escape closes the dialog when the request is dismissible (Dismiss applies that policy).
            if (args.Keyval == Gdk.Constants.KEY_Escape)
            {
                Dismiss();
                return true;
            }

            // Tab and Shift+Tab: keep traversal inside the dialog subtree.
            if (args.Keyval is Gdk.Constants.KEY_Tab or Gdk.Constants.KEY_ISO_Left_Tab)
            {
                var backwards = (args.State & Gdk.ModifierType.ShiftMask) != 0;
                var direction = backwards ? Gtk.DirectionType.TabBackward : Gtk.DirectionType.TabForward;
                if (!_keyboardRoot.ChildFocus(direction))
                {
                    // Clearing the current focus lets GTK restart traversal at the opposite edge of this subtree.
                    (_keyboardRoot.GetRoot() as Gtk.Window)?.SetFocus(null);
                    _keyboardRoot.ChildFocus(direction);
                }

                return true;
            }

            // Enter submits the dialog's default action unless a focused native control consumes it.
            if (args.Keyval is Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter)
            {
                // Native buttons, selectors and multiline editors own Enter; other focused content submits the form.
                for (var focus = (_keyboardRoot.GetRoot() as Gtk.Window)?.GetFocus(); focus != null; focus = focus.GetParent())
                    if (focus is Gtk.Button or Gtk.DropDown or Gtk.TextView) return false;
                _ = InvokeDefaultAsync();
                return true;
            }

            return false;
        }
    }
}
