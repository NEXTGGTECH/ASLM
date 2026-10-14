// Copyright NEXTGGTECH. Apache License 2.0.

using Foundation;
using Microsoft.Maui.Handlers;
using ObjCRuntime;
using UIKit;

namespace ASLM.Controls.Dialogs;

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
        public DialogHostView? Dialog { get; init; }
        private readonly UIKeyCommand _escape = UIKeyCommand.Create(new NSString("\u001b"), 0, new Selector("dismissDialog:"));
        public override bool CanBecomeFirstResponder => Dialog?.IsOpen == true;
        public override UIKeyCommand[] KeyCommands => Dialog?.IsOpen == true ? [_escape] : [];

        /// <summary>
        /// Forwards Escape to the shared session so button-only dialogs cannot be dismissed.
        /// </summary>
        [Export("dismissDialog:")]
        private void DismissDialog(UIKeyCommand command) => Dialog?.Dismiss();

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
