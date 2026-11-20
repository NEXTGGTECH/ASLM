// Copyright NEXTGGTECH. Apache License 2.0.

using CoreGraphics;
using Foundation;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Handlers;
using UIKit;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Maps <see cref="ConsoleOutputView"/> to a native UITextView with bottom-pinned scrolling.
    /// </summary>
    public sealed class ConsoleOutputViewHandler : ViewHandler<ConsoleOutputView, UITextView>
    {
        // Fields and constants

        /// <summary>
        /// Palette key of the console background color.
        /// </summary>
        private const string ConsoleSurfaceColorKey = "BackgroundSecondary";

        /// <summary>
        /// Palette key of the console text color.
        /// </summary>
        private const string ConsoleTextColorKey = "LabelPrimary";

        /// <summary>
        /// Maps the scrollbar mode to the native text view.
        /// </summary>
        public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
            new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewHandler.ViewMapper)
            {
                [nameof(ConsoleOutputView.UseCustomScrollBar)] = static (handler, view) => handler.ApplyScrollChrome()
            };

        /// <summary>
        /// Watches native pointer gestures without recognizing a gesture or taking over selection.
        /// </summary>
        private SelectionPointerObserver? _selectionPointer;


        // Initialization

        /// <summary>
        /// Creates the handler instance for the native console host.
        /// </summary>
        public ConsoleOutputViewHandler() : base(Mapper)
        {
        }


        // Handler lifecycle

        /// <summary>
        /// Creates the native UITextView used to render console output.
        /// </summary>
        protected override UITextView CreatePlatformView()
        {
            // Read-only, selectable monospaced output.
            var textView = new UITextView
            {
                Editable = false,
                Selectable = true,
                Font = UIFont.GetMonospacedSystemFont(12f, UIFontWeight.Regular),
                TextContainerInset = new UIEdgeInsets(8, 10, 8, 10),
                ClipsToBounds = true
            };

            // Rounded corners and palette colors give the console its card appearance.
            textView.Layer.CornerRadius = 10;
            ApplyConsoleTheme(textView);
            return textView;
        }

        /// <summary>
        /// Hooks theme events and applies the initial virtual view state.
        /// </summary>
        protected override void ConnectHandler(UITextView platformView)
        {
            base.ConnectHandler(platformView);

            // Follow light/dark switches and custom palettes; unsubscribing first prevents duplicate handlers.
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;

            // Report scroll geometry to the shared scrollbar and let it drive the native scroll offset.
            ApplyConsoleTheme(platformView);
            _selectionPointer = new SelectionPointerObserver(VirtualView.SetSelectionGesture)
            {
                CancelsTouchesInView = false,
                DelaysTouchesBegan = false,
                DelaysTouchesEnded = false
            };
            platformView.AddGestureRecognizer(_selectionPointer);
            platformView.Scrolled += OnScrolled;
            VirtualView.SizeChanged += OnScrolled;
            VirtualView.ScrollToOffset = offset => platformView.SetContentOffset(new CGPoint(0, offset), false);
            ApplyScrollChrome();
            VirtualView.ConnectOutput(ApplyOutput);
        }

        /// <summary>
        /// Unhooks theme events before the native view is released.
        /// </summary>
        protected override void DisconnectHandler(UITextView platformView)
        {
            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;
            platformView.Scrolled -= OnScrolled;
            if (_selectionPointer is { } pointer)
            {
                platformView.RemoveGestureRecognizer(pointer);
                pointer.Dispose();
                _selectionPointer = null;
            }
            if (VirtualView != null)
            {
                VirtualView.SizeChanged -= OnScrolled;
                VirtualView.ScrollToOffset = null;
                VirtualView.DisconnectOutput();
            }

            base.DisconnectHandler(platformView);
        }


        // Output and viewport

        /// <summary>
        /// Edits the text storage in place and keeps the scroll position independent of the selection.
        /// </summary>
        private void ApplyOutput(ConsoleOutput output, ConsoleOutput? previous, bool sessionChanged)
        {
            var textView = PlatformView;
            var offset = (double)textView.ContentOffset.Y;
            var follow = sessionChanged || VirtualView.ShouldFollowOutput(offset, GetMaximumOffset(textView));
            var position = follow ? default : output.PreservePosition(previous, offset, GetLineTop);

            // Avoid replacing UITextView.Text during live output, which resets TextKit's selection.
            if (sessionChanged || previous == null || previous.Text.Length == 0)
            {
                textView.Text = output.Text;
                if (textView.SelectedRange.Length > 0) textView.SelectedRange = new NSRange(0, 0);
            }
            else
            {
                var storage = textView.TextStorage;
                storage.BeginEditing();
                try
                {
                    foreach (var edit in output.GetEdits(previous))
                        storage.Replace(new NSRange(edit.Start, edit.End - edit.Start), edit.Text);
                }
                finally
                {
                    storage.EndEditing();
                }
            }

            if (follow || position.CharacterIndex >= 0) textView.LayoutIfNeeded();
            var maximum = GetMaximumOffset(textView);
            var target = follow ? maximum : Math.Clamp(position.Resolve(GetLineTop), -textView.ContentInset.Top, maximum);
            if (Math.Abs((double)textView.ContentOffset.Y - target) >= 0.5)
                textView.SetContentOffset(new CGPoint(0, target), animated: false);
            PublishScrollMetrics();
        }

        /// <summary>
        /// Returns the vertical offset of the end of the native scroll view.
        /// </summary>
        private static double GetMaximumOffset(UITextView view) =>
            Math.Max(-view.ContentInset.Top, view.ContentSize.Height - view.Bounds.Height + view.ContentInset.Bottom);

        /// <summary>
        /// Gets a line's vertical position in the text view's scroll content.
        /// </summary>
        private double? GetLineTop(int index)
        {
            var textView = PlatformView;
            var position = textView.GetPosition(textView.BeginningOfDocument, index);
            if (position == null) return null;
            var rect = textView.GetCaretRectForPosition(position);
            return rect.Height > 0 ? rect.Y : null;
        }

        /// <summary>
        /// Reports the new scroll geometry when the native view scrolls or the virtual view is resized.
        /// </summary>
        private void OnScrolled(object? sender, EventArgs e) => PublishScrollMetrics();

        /// <summary>
        /// Sends viewport height, content height and scroll offset to the virtual view, but only while the
        /// shared custom scrollbar is in use.
        /// </summary>
        private void PublishScrollMetrics()
        {
            if (VirtualView?.UseCustomScrollBar == true && PlatformView is { } view)
            {
                VirtualView.SetScrollMetrics(view.Bounds.Height, view.ContentSize.Height, view.ContentOffset.Y);
            }
        }

        /// <summary>
        /// Hides the native vertical scroll indicator when the custom scrollbar is used and republishes the geometry.
        /// </summary>
        private void ApplyScrollChrome()
        {
            if (PlatformView == null) return;
            PlatformView.ShowsVerticalScrollIndicator = VirtualView?.UseCustomScrollBar != true;
            PublishScrollMetrics();
        }


        // Theme handling

        /// <summary>
        /// Reapplies console colors when the application theme changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            RefreshConsoleTheme();
        }

        /// <summary>
        /// Reapplies console colors when a custom palette is applied.
        /// </summary>
        private void OnPaletteApplied()
        {
            RefreshConsoleTheme();
        }

        /// <summary>
        /// Reapplies console colors on the UI thread.
        /// </summary>
        private void RefreshConsoleTheme()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (PlatformView != null)
                {
                    ApplyConsoleTheme(PlatformView);
                }
            });
        }

        /// <summary>
        /// Applies console surface and text colors from the active ASLM palette.
        /// </summary>
        private static void ApplyConsoleTheme(UITextView textView)
        {
            textView.BackgroundColor = ToUiColor(ResolveThemeColor(ConsoleSurfaceColorKey));
            textView.TextColor = ToUiColor(ResolveThemeColor(ConsoleTextColorKey));
        }

        /// <summary>
        /// Resolves a palette color from application resources, falling back to <see cref="ThemePaletteResolver"/>.
        /// </summary>
        private static Color ResolveThemeColor(string key)
        {
            if (Microsoft.Maui.Controls.Application.Current?.Resources.TryGetValue(key, out var value) == true &&
                value is Color resourceColor)
            {
                return resourceColor;
            }

            var isDark = Microsoft.Maui.Controls.Application.Current?.RequestedTheme != AppTheme.Light;
            var palette = isDark
                ? ThemePaletteResolver.BuildDarkPalette()
                : ThemePaletteResolver.BuildLightPalette();
            return palette.TryGetValue(key, out var paletteColor) ? paletteColor : Colors.Transparent;
        }

        /// <summary>
        /// Converts a MAUI color into the UIKit color type used by the native text view.
        /// </summary>
        private static UIColor ToUiColor(Color color) =>
            UIColor.FromRGBA(
                (nfloat)color.Red,
                (nfloat)color.Green,
                (nfloat)color.Blue,
                (nfloat)color.Alpha);

        // Native input

        /// <summary>
        /// Observes pointer activity before UITextView creates a nonempty selection range.
        /// </summary>
        private sealed class SelectionPointerObserver(Action<bool> setPressed) : UIGestureRecognizer
        {
            /// <summary>
            /// Keeps native touch and pointer delivery independent of this observer.
            /// </summary>
            public override bool CanPreventGestureRecognizer(UIGestureRecognizer preventedGestureRecognizer) => false;

            /// <summary>
            /// Keeps tracking the pointer through gestures owned by the native view.
            /// </summary>
            public override bool CanBePreventedByGestureRecognizer(UIGestureRecognizer preventingGestureRecognizer) => false;

            /// <summary>
            /// Pauses bottom-following at the start of a native pointer sequence.
            /// </summary>
            public override void TouchesBegan(NSSet touches, UIEvent evt) => setPressed(true);

            /// <summary>
            /// Ends observation without recognizing or claiming the sequence.
            /// </summary>
            public override void TouchesEnded(NSSet touches, UIEvent evt) => FinishTracking();

            /// <summary>
            /// Releases the guard when the system cancels the native sequence.
            /// </summary>
            public override void TouchesCancelled(NSSet touches, UIEvent evt) => FinishTracking();

            /// <summary>
            /// Releases the pointer guard after UIKit resets the observer.
            /// </summary>
            public override void Reset() => setPressed(false);

            /// <summary>
            /// Releases the guard before UIKit resets the unrecognized sequence.
            /// </summary>
            private void FinishTracking()
            {
                setPressed(false);
                State = UIGestureRecognizerState.Failed;
            }
        }

    }
}
