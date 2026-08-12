// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Displays an adaptive native tooltip for optional settings help text.
    /// </summary>
    public partial class SettingsInfoButton : ContentView
    {
        private const double MaximumTooltipWidth = 400;

        /// <summary>
        /// Identifies the help text displayed while the pointer rests on the icon.
        /// </summary>
        public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
            nameof(Description),
            typeof(string),
            typeof(SettingsInfoButton),
            string.Empty,
            propertyChanged: OnDescriptionChanged);

        /// <summary>
        /// Creates the info icon and connects its platform tooltip lifecycle.
        /// </summary>
        public SettingsInfoButton()
        {
            InitializeComponent();
            InfoIconButton.HandlerChanged += OnInfoIconHandlerChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Gets or sets the description presented by the tooltip.
        /// </summary>
        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// Gets whether a non-empty description requires an info icon.
        /// </summary>
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        /// <summary>
        /// Refreshes visibility and native tooltip content after description changes.
        /// </summary>
        private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
        {
            var infoButton = (SettingsInfoButton)bindable;
            ToolTipProperties.SetText(infoButton.InfoIconButton, infoButton.Description);
            infoButton.OnPropertyChanged(nameof(HasDescription));
            infoButton.Dispatcher.Dispatch(infoButton.ApplyNativeTooltip);
        }

        /// <summary>
        /// Applies tooltip constraints after the platform button becomes available.
        /// </summary>
        private void OnInfoIconHandlerChanged(object? sender, EventArgs e)
        {
            ApplyNativeTooltip();
        }

        /// <summary>
        /// Subscribes the icon to palette changes while it is in the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
                app.RequestedThemeChanged += OnRequestedThemeChanged;
            }

            RefreshIconChrome();
            ApplyNativeTooltip();
        }

        /// <summary>
        /// Removes palette subscriptions when the icon leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            ThemeService.PaletteApplied -= OnPaletteApplied;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnRequestedThemeChanged;
            }
        }

        /// <summary>
        /// Refreshes the icon after a custom palette is applied.
        /// </summary>
        private void OnPaletteApplied()
        {
            MainThread.BeginInvokeOnMainThread(RefreshIconChrome);
        }

        /// <summary>
        /// Refreshes the icon after the application appearance changes.
        /// </summary>
        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshIconChrome);
        }

        /// <summary>
        /// Applies the primary-label palette color to the packaged info icon.
        /// </summary>
        private void RefreshIconChrome()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            InfoIconButton.Source = PackagedIconTintCache.Get("icon_question.png", iconTint);
        }

        /// <summary>
        /// Limits desktop tooltip width while leaving placement to the native window manager.
        /// </summary>
        private void ApplyNativeTooltip()
        {
#if WINDOWS
            if (InfoIconButton.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement platformButton)
            {
                return;
            }

            if (!HasDescription)
            {
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(platformButton, null);
                return;
            }

            var content = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                Text = Description,
                TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                MaxWidth = MaximumTooltipWidth
            };
            var tooltip = new Microsoft.UI.Xaml.Controls.ToolTip
            {
                Content = content,
                MaxWidth = MaximumTooltipWidth
            };
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(platformButton, tooltip);
#endif
        }
    }
}
