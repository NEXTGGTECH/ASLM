// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Styling helpers

        /// <summary>
        /// Selects the shared connect or disconnect style for an account action button.
        /// </summary>
        private static void ApplyAccountConnectionButtonState(Button button, bool isConnected)
        {
            button.Style = GetStyleResource(
                isConnected
                    ? "SettingsInlineDangerActionButtonStyle"
                    : "SettingsInlineActionButtonStyle");
        }

        /// <summary>
        /// Applies a quieter WinUI scrollbar treatment so the overlay keeps its minimalist look.
        /// </summary>
        private static void ApplyScrollViewChrome(ScrollView scrollView, bool isSidebar)
        {
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

                    if (!isSidebar)
                    {
                        viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                        viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
                    }

                    void Restyle()
                    {
                        StyleScrollBars(viewer, isSidebar);
                    }

                    viewer.Loaded -= OnViewerLoaded;
                    viewer.Loaded += OnViewerLoaded;
                    viewer.SizeChanged -= OnViewerSizeChanged;
                    viewer.SizeChanged += OnViewerSizeChanged;
                    viewer.DispatcherQueue.TryEnqueue(Restyle);

                    void OnViewerLoaded(object? sender, Microsoft.UI.Xaml.RoutedEventArgs e) => Restyle();
                    void OnViewerSizeChanged(object? sender, Microsoft.UI.Xaml.SizeChangedEventArgs e) => Restyle();
                }
#endif
            }

            scrollView.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }

#if WINDOWS
        /// <summary>
        /// Restyles WinUI scrollbars to sit tighter to the edge with lower visual weight.
        /// </summary>
        private static void StyleScrollBars(Microsoft.UI.Xaml.Controls.ScrollViewer viewer, bool isSidebar)
        {
            var thumbBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.ColorHelper.FromArgb(isSidebar ? (byte)54 : (byte)92, 255, 255, 255));
            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var hiddenOpacity = isSidebar ? (double?)null : 0;

            foreach (var scrollBar in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ScrollBar>(viewer))
            {
                if (!isSidebar)
                {
                    scrollBar.Opacity = 0;
                    scrollBar.Width = 0;
                    scrollBar.MinWidth = 0;
                    scrollBar.Height = 0;
                    scrollBar.MinHeight = 0;
                }

                if (scrollBar.Orientation == Microsoft.UI.Xaml.Controls.Orientation.Vertical)
                {
                    scrollBar.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right;
                    scrollBar.Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 4);
                    scrollBar.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    scrollBar.Background = transparentBrush;
                    scrollBar.Foreground = thumbBrush;
                    scrollBar.Opacity = hiddenOpacity ?? 0.18;
                }
                else
                {
                    scrollBar.Height = isSidebar ? 3 : 0;
                    scrollBar.MinHeight = isSidebar ? 3 : 0;
                    scrollBar.Background = transparentBrush;
                    scrollBar.Opacity = hiddenOpacity ?? 0.2;
                }

                foreach (var repeatButton in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.RepeatButton>(scrollBar))
                {
                    repeatButton.Background = transparentBrush;
                    repeatButton.BorderBrush = transparentBrush;
                    repeatButton.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    repeatButton.Opacity = 0;
                }

                foreach (var border in FindDescendants<Microsoft.UI.Xaml.Controls.Border>(scrollBar))
                {
                    border.Background = transparentBrush;
                    border.BorderBrush = transparentBrush;
                    border.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                }

                foreach (var thumb in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.Thumb>(scrollBar))
                {
                    thumb.Background = thumbBrush;
                    thumb.BorderBrush = transparentBrush;
                    thumb.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    thumb.MinWidth = isSidebar ? 6 : 6;
                    thumb.Width = isSidebar ? 6 : 6;
                    thumb.MinHeight = 18;
                    thumb.Opacity = isSidebar ? 0.38 : 0.55;
                }
            }
        }

        /// <summary>
        /// Enumerates descendants of the requested WinUI type.
        /// </summary>
        private static IEnumerable<T> FindDescendants<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
        {
            var queue = new Queue<Microsoft.UI.Xaml.DependencyObject>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);

                for (var index = 0; index < childCount; index++)
                {
                    var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, index);
                    if (child is T typed)
                    {
                        yield return typed;
                    }

                    queue.Enqueue(child);
                }
            }
        }
#endif


#if WINDOWS
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        /// <summary>
        /// Shows the Windows save picker for exporting a custom theme JSON file.
        /// </summary>
        private static async Task<string?> PickExportThemeFilePathAsync(string suggestedFileName)
        {
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
                    hwnd = GetForegroundWindow();
                }
            }
            else
            {
                hwnd = GetForegroundWindow();
            }

            if (hwnd == 0)
            {
                return null;
            }

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
