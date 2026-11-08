// Copyright NEXTGGTECH. Apache License 2.0.

using Foundation;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace ASLM.Controls.Dialogs
{
    /// <summary>
    /// Integrates dialog dismissal with the UIKit responder chain while form inputs have focus.
    /// </summary>
    public sealed class DialogHostHandler : ContentViewHandler
    {
        /// <summary>
        /// Creates the native layout host with a reference to the dialog's dismissal policy.
        /// </summary>
        protected override Microsoft.Maui.Platform.ContentView CreatePlatformView() => new DialogPlatformView
        {
            CrossPlatformLayout = VirtualView,
            Dialog = (DialogHostView)VirtualView
        };

        /// <summary>
        /// Provides an Escape command only while its dialog is open.
        /// </summary>
        private sealed class DialogPlatformView : Microsoft.Maui.Platform.ContentView
        {
            // State

            /// <summary>
            /// Cross-platform dialog host that owns the open state and the dismissal policy.
            /// </summary>
            public DialogHostView? Dialog { get; init; }

            /// <summary>
            /// Key command bound to the Escape character (U+001B) with no modifiers; it invokes <c>dismissDialog:</c>.
            /// </summary>
            private readonly UIKeyCommand _escape = UIKeyCommand.Create(new NSString("\u001b"), 0, new Selector("dismissDialog:"));


            // Responder chain

            /// <summary>
            /// Lets the view take keyboard focus only while a dialog is open, so Escape reaches it.
            /// </summary>
            public override bool CanBecomeFirstResponder => Dialog?.IsOpen == true;

            /// <summary>
            /// Exposes the Escape command to UIKit only while a dialog is open.
            /// </summary>
            public override UIKeyCommand[] KeyCommands => Dialog?.IsOpen == true ? [_escape] : [];

            /// <summary>
            /// Forwards Escape to the shared session so button-only dialogs cannot be dismissed.
            /// </summary>
            [Export("dismissDialog:")]
            private void DismissDialog(UIKeyCommand command) => Dialog?.Dismiss();


            // Disposal

            /// <summary>
            /// Releases the native keyboard command when the platform handler is disposed.
            /// </summary>
            protected override void Dispose(bool disposing)
            {
                if (disposing) _escape.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
