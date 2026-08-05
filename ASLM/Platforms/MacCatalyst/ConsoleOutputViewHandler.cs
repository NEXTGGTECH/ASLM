// Copyright NEXTGGTECH. Apache License 2.0.

using CoreGraphics;
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

        private const string ConsoleSurfaceColorKey = "BackgroundSecondary";
        private const string ConsoleTextColorKey = "LabelPrimary";
        private const double BottomStickinessThreshold = 48;

        public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
            new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewHandler.ViewMapper)
            {
                [nameof(ConsoleOutputView.Text)] = static (handler, view) => handler.ApplyText(view.Text),
                [nameof(ConsoleOutputView.SessionKey)] = static (handler, view) => handler.ApplySessionKey(view.SessionKey)
            };

        private string _lastSessionKey = string.Empty;
        private bool _forceScrollToEnd = true;


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
            var textView = new UITextView
            {
                Editable = false,
                Selectable = true,
                Font = UIFont.GetMonospacedSystemFont(12f, UIFontWeight.Regular),
                TextContainerInset = new UIEdgeInsets(8, 10, 8, 10),
                ClipsToBounds = true
            };

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

            if (Microsoft.Maui.Controls.Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;

            ApplyConsoleTheme(platformView);
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
            base.DisconnectHandler(platformView);
        }


        // Property application

        /// <summary>
        /// Replaces the console text and keeps the viewport pinned to the newest output.
        /// </summary>
        private void ApplyText(string text)
        {
            if (PlatformView is not { } textView)
            {
                return;
            }

            var pinToBottom = _forceScrollToEnd || IsNearBottom(textView);
            textView.Text = text ?? string.Empty;

            if (pinToBottom)
            {
                ScrollToEnd(textView);
            }
        }

        /// <summary>
        /// Resets scroll pinning when a different console session is selected.
        /// </summary>
        private void ApplySessionKey(string sessionKey)
        {
            var normalized = sessionKey ?? string.Empty;
            if (string.Equals(_lastSessionKey, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _lastSessionKey = normalized;
            _forceScrollToEnd = true;

            if (PlatformView is { } textView)
            {
                ScrollToEnd(textView);
            }
        }


        // Scrolling

        /// <summary>
        /// Returns whether the viewport currently sits close enough to the bottom to stay pinned.
        /// </summary>
        private static bool IsNearBottom(UITextView textView)
        {
            var visibleBottom = textView.ContentOffset.Y + textView.Bounds.Height;
            return visibleBottom >= textView.ContentSize.Height - BottomStickinessThreshold;
        }

        /// <summary>
        /// Scrolls to the end of the content after the pending layout pass completes.
        /// </summary>
        private void ScrollToEnd(UITextView textView)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                textView.LayoutIfNeeded();
                var target = textView.ContentSize.Height - textView.Bounds.Height + textView.ContentInset.Bottom;
                textView.SetContentOffset(new CGPoint(0, Math.Max(0, target)), animated: false);
                _forceScrollToEnd = false;
            });
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
    }
}
