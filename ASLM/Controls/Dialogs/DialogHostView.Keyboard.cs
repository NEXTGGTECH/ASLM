// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Dialogs
{
    /// <summary>
    /// Platform-specific part of the dialog host that handles keyboard shortcuts and modal input isolation.
    /// </summary>
    public partial class DialogHostView
    {
#if WINDOWS
        // Windows fields

        /// <summary>
        /// Native root element that carries the key handler, or null while disconnected.
        /// </summary>
        private Microsoft.UI.Xaml.UIElement? _keyboardRoot;

        /// <summary>
        /// Native element of the page behind the dialog whose input is blocked, or null while no dialog is open.
        /// </summary>
        private Microsoft.UI.Xaml.UIElement? _blockedBackground;

        /// <summary>
        /// Native element that had focus before the dialog opened, restored when the dialog closes.
        /// </summary>
        private Microsoft.UI.Xaml.DependencyObject? _previousNativeFocus;

        /// <summary>
        /// Original hit-test state of the blocked background, restored when the dialog closes.
        /// </summary>
        private bool _backgroundWasHitTestVisible;


        // Windows keyboard and modal input

        /// <summary>
        /// Keeps keyboard navigation inside the dialog and listens for Escape and Enter.
        /// </summary>
        partial void ConnectKeyboard()
        {
            // Reconnecting must not leave a second handler on the previous root.
            DisconnectKeyboard();
            if (Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement element) return;
            _keyboardRoot = element;

            // Tab cycles inside the dialog; the handler also receives keys that child controls already handled.
            element.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle;
            element.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler(OnNativeKeyDown), true);
        }

        /// <summary>
        /// Removes the routed key handler before the native host is replaced or unloaded.
        /// </summary>
        partial void DisconnectKeyboard()
        {
            _keyboardRoot?.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent,
                new Microsoft.UI.Xaml.Input.KeyEventHandler(OnNativeKeyDown));
            _keyboardRoot = null;
        }

        /// <summary>
        /// Applies modal input isolation without propagating IsEnabled through the MAUI page tree.
        /// </summary>
        partial void UpdateModalPlatform(bool visible)
        {
            // Opening: remember the background's input state, then block pointer input and focus on it.
            if (visible)
            {
                if (_blockedBackground != null || _background?.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement background)
                    return;

                // Query focus directly instead of walking every hidden settings control.
                _previousNativeFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(background.XamlRoot)
                    as Microsoft.UI.Xaml.DependencyObject;
                _blockedBackground = background;
                _backgroundWasHitTestVisible = background.IsHitTestVisible;
                background.IsHitTestVisible = false;
                background.GettingFocus += OnBackgroundGettingFocus;
            }
            else if (_blockedBackground is { } background)
            {
                // Restore exactly the native input state captured on entry; do not overwrite bindings.
                background.GettingFocus -= OnBackgroundGettingFocus;
                background.IsHitTestVisible = _backgroundWasHitTestVisible;
                _blockedBackground = null;
                if (_previousNativeFocus is Microsoft.UI.Xaml.FrameworkElement { IsLoaded: true } previous &&
                    previous.XamlRoot == background.XamlRoot)
                    _ = Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(previous, Microsoft.UI.Xaml.FocusState.Programmatic);
                _previousNativeFocus = null;
            }
        }

        /// <summary>
        /// Rejects focus attempts into the background while allowing dialog picker popups to work.
        /// </summary>
        private void OnBackgroundGettingFocus(Microsoft.UI.Xaml.UIElement sender, Microsoft.UI.Xaml.Input.GettingFocusEventArgs e)
        {
            // Subscribe only on the background branch, not globally: other windows remain usable.
            if (IsOpen) e.TryCancel();
        }

        /// <summary>
        /// Handles dialog shortcuts without stealing Enter from buttons, pickers, or multiline inputs.
        /// </summary>
        private async void OnNativeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!IsOpen) return;
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Dismiss();
            }
            else if (e.Key == Windows.System.VirtualKey.Enter && !e.Handled &&
                     e.OriginalSource is not Microsoft.UI.Xaml.Controls.Primitives.ButtonBase &&
                     e.OriginalSource is not Microsoft.UI.Xaml.Controls.ComboBox &&
                     e.OriginalSource is not Microsoft.UI.Xaml.Controls.TextBox { AcceptsReturn: true })
            {
                e.Handled = true;
                await InvokeDefaultAsync();
            }
        }
#elif MACCATALYST
        // Mac Catalyst fields

        /// <summary>
        /// Native view of the page behind the dialog whose input is blocked, or null while no dialog is open.
        /// </summary>
        private UIKit.UIView? _blockedBackground;

        /// <summary>
        /// Native view that was the first responder before the dialog opened, restored when the dialog closes.
        /// </summary>
        private UIKit.UIView? _previousNativeFocus;

        /// <summary>
        /// Original user-interaction state of the blocked background, restored when the dialog closes.
        /// </summary>
        private bool _backgroundWasInteractive;

        /// <summary>
        /// Original accessibility-hidden state of the blocked background, restored when the dialog closes.
        /// </summary>
        private bool _backgroundAccessibilityHidden;


        // Mac Catalyst modal input

        /// <summary>
        /// Isolates UIKit input and accessibility at the native background root without restyling it.
        /// </summary>
        partial void UpdateModalPlatform(bool visible)
        {
            // Opening: capture the background's interaction state, then disable input and accessibility on it.
            if (visible && _blockedBackground == null && _background?.Handler?.PlatformView is UIKit.UIView background)
            {
                // Preserve the responder before the dialog takes focus, including native text editors.
                _previousNativeFocus = FindFirstResponder(background);
                _blockedBackground = background;
                _backgroundWasInteractive = background.UserInteractionEnabled;
                _backgroundAccessibilityHidden = background.AccessibilityElementsHidden;
                background.UserInteractionEnabled = false;
                background.AccessibilityElementsHidden = true;
            }
            else if (!visible && _blockedBackground is { } previousBackground)
            {
                // Closing: restore the captured state.
                previousBackground.UserInteractionEnabled = _backgroundWasInteractive;
                previousBackground.AccessibilityElementsHidden = _backgroundAccessibilityHidden;
                _blockedBackground = null;
            }

            // The dialog host itself becomes the modal accessibility scope and owns the keyboard responder.
            if (Handler?.PlatformView is UIKit.UIView view)
            {
                view.AccessibilityViewIsModal = visible;
                if (visible) view.BecomeFirstResponder();
                else view.ResignFirstResponder();
            }

            // A responder removed with its page must not regain focus after owner cancellation.
            if (!visible)
            {
                if (_previousNativeFocus?.Window != null) _previousNativeFocus.BecomeFirstResponder();
                _previousNativeFocus = null;
            }
        }

        /// <summary>
        /// Finds the current UIKit responder once when entering the modal input scope.
        /// </summary>
        private static UIKit.UIView? FindFirstResponder(UIKit.UIView view)
        {
            if (view.IsFirstResponder) return view;
            foreach (var child in view.Subviews)
                if (FindFirstResponder(child) is { } focused) return focused;
            return null;
        }
#endif
    }
}
