// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Dialogs;

public partial class DialogHostView
{
    private Gtk.Widget? _keyboardRoot;
    private Gtk.EventControllerKey? _keyController;
    private Gtk.Widget? _blockedBackground;
    private Gtk.Widget? _previousFocus;
    private bool _backgroundCanTarget;

    /// <summary>
    /// Captures dialog keyboard navigation before GTK can move focus into the underlying page.
    /// </summary>
    partial void ConnectKeyboard()
    {
        DisconnectKeyboard();
        if (Handler?.PlatformView is not Gtk.Widget root) return;
        _keyboardRoot = root;
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

    /// <summary>
    /// Blocks pointer interaction without restyling the entire underlying settings tree.
    /// </summary>
    partial void UpdateModalPlatform(bool visible)
    {
        if (visible && _blockedBackground == null && _background?.Handler?.PlatformView is Gtk.Widget background)
        {
            _previousFocus = (background.GetRoot() as Gtk.Window)?.GetFocus();
            _backgroundCanTarget = background.GetCanTarget();
            _blockedBackground = background;
            background.SetCanTarget(false);
        }
        else if (!visible && _blockedBackground is { } previous)
        {
            previous.SetCanTarget(_backgroundCanTarget);
            _blockedBackground = null;
            if (_previousFocus?.GetRoot() == previous.GetRoot()) _previousFocus?.GrabFocus();
            _previousFocus = null;
        }
    }

    /// <summary>
    /// Cycles focus within the dialog and respects the request's configured dismissal policy.
    /// </summary>
    private bool OnNativeKeyPressed(Gtk.EventControllerKey sender, Gtk.EventControllerKey.KeyPressedSignalArgs args)
    {
        if (!IsOpen || _keyboardRoot == null) return false;
        if (args.Keyval == Gdk.Constants.KEY_Escape)
        {
            Dismiss();
            return true;
        }
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
