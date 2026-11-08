// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Pages
{
    /// <summary>
    /// Platform part of the settings view: theme-aware account link icons, native scroll chrome
    /// and the native save picker for theme export.
    /// </summary>
    public partial class SettingsView
    {
        // Styling helpers

        /// <summary>
        /// Subscribes account-link icons to palette changes while the settings view is visible.
        /// </summary>
        private void AttachAccountLinkThemeHandlers()
        {
            ThemeService.PaletteApplied -= OnPaletteAppliedForAccountLinks;
            ThemeService.PaletteApplied += OnPaletteAppliedForAccountLinks;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChangedForAccountLinks;
                app.RequestedThemeChanged += OnRequestedThemeChangedForAccountLinks;
            }

            RefreshAccountLinkIconChrome();
        }

        /// <summary>
        /// Removes account-link theme handlers when the settings view leaves the visual tree.
        /// </summary>
        private void DetachAccountLinkThemeHandlers()
        {
            ThemeService.PaletteApplied -= OnPaletteAppliedForAccountLinks;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChangedForAccountLinks;
            }
        }

        /// <summary>
        /// Refreshes account-link icons after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForAccountLinks()
        {
            MainThread.BeginInvokeOnMainThread(RefreshAccountLinkIconChrome);
        }

        /// <summary>
        /// Refreshes account-link icons after the application appearance changes.
        /// </summary>
        private void OnRequestedThemeChangedForAccountLinks(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshAccountLinkIconChrome);
        }

        /// <summary>
        /// Applies the current primary-label color to every packaged account-link icon.
        /// </summary>
        private void RefreshAccountLinkIconChrome()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            var iconSource = PackagedIconTintCache.Get("icon_link.png", iconTint);
            BuiltInSettingsContainer.AslmAccountLink.Source = iconSource;
            BuiltInSettingsContainer.GitHubAccountLink.Source = iconSource;
            BuiltInSettingsContainer.OllamaAccountLink.Source = iconSource;
        }

        /// <summary>
        /// Selects the shared connect or disconnect style for an account action button.
        /// </summary>
        private static void ApplyAccountConnectionButtonState(Button button, bool isConnected)
        {
            var colorResource = isConnected ? "ActionRed" : "ActionBlue";
            button.Style = GetStyleResource(
                isConnected
                    ? "SettingsAccountDangerActionButtonStyle"
                    : "SettingsAccountActionButtonStyle");
            button.SetDynamicResource(Button.TextColorProperty, colorResource);
            button.SetDynamicResource(Button.BackgroundColorProperty, "BackgroundSecondary");
            button.SetDynamicResource(Button.BorderColorProperty, colorResource);
        }

        /// <summary>
        /// Removes native scrollbar chrome where the settings surface supplies stable navigation.
        /// </summary>
        private static void ApplyScrollViewChrome(ScrollView scrollView)
        {
            // Restyles the native scroll viewer on Windows; other platforms keep the default chrome.
            void ApplyPlatformStyle()
            {
#if WINDOWS
                if (scrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer viewer)
                {
                    var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    viewer.Background = transparentBrush;
                    viewer.BorderBrush = transparentBrush;
                    viewer.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    viewer.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                    viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                }
#endif
            }

            // The native handler can appear or change after construction, so restyle on every handler change
            // as well as now.
            scrollView.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }


#if WINDOWS
        // Native file dialogs

        /// <summary>
        /// Win32 call that returns the window the user is currently working in, used as a fallback picker owner.
        /// </summary>
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        /// <summary>
        /// Shows the Windows save picker for exporting a custom theme JSON file.
        /// Returns the chosen path, or null when no window handle is available or the user cancels.
        /// </summary>
        private static async Task<string?> PickExportThemeFilePathAsync(string suggestedFileName)
        {
            // Find the window handle that owns the picker: the main window, or the foreground window as a fallback.
            var native = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView;
            nint hwnd = 0;
            if (native is Microsoft.UI.Xaml.Window win)
            {
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
            }
            else if (native != null)
            {
                try
                {
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(native);
                }
                catch
                {
                    // The native object yielded no usable window handle, so use the foreground window.
                    hwnd = GetForegroundWindow();
                }
            }
            else
            {
                // No platform window is available yet, so use the foreground window.
                hwnd = GetForegroundWindow();
            }

            if (hwnd == 0)
            {
                return null;
            }

            // In a desktop app the WinRT picker must be initialized with the owner window handle before it is shown.
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "ASLM_theme" : suggestedFileName
            };

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            picker.FileTypeChoices.Add("ASLM theme (.json)", new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
#endif
    }
}
