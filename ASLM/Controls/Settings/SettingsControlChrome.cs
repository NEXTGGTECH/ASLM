// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Applies the minimal platform chrome required by XAML-defined settings editors.
    /// </summary>
    public static class SettingsControlChrome
    {
        /// <summary>
        /// Identifies whether an entry should remove its native border and background.
        /// </summary>
        public static readonly BindableProperty FlatEntryProperty = BindableProperty.CreateAttached(
            "FlatEntry",
            typeof(bool),
            typeof(SettingsControlChrome),
            false,
            propertyChanged: OnFlatEntryChanged);

        /// <summary>
        /// Identifies whether a picker should remove its native border and background.
        /// </summary>
        public static readonly BindableProperty CompactPickerProperty = BindableProperty.CreateAttached(
            "CompactPicker",
            typeof(bool),
            typeof(SettingsControlChrome),
            false,
            propertyChanged: OnCompactPickerChanged);

        /// <summary>
        /// Gets whether native entry chrome is disabled for one element.
        /// </summary>
        public static bool GetFlatEntry(BindableObject element) =>
            (bool)element.GetValue(FlatEntryProperty);

        /// <summary>
        /// Sets whether native entry chrome is disabled for one element.
        /// </summary>
        public static void SetFlatEntry(BindableObject element, bool value) =>
            element.SetValue(FlatEntryProperty, value);

        /// <summary>
        /// Gets whether native picker chrome is disabled for one element.
        /// </summary>
        public static bool GetCompactPicker(BindableObject element) =>
            (bool)element.GetValue(CompactPickerProperty);

        /// <summary>
        /// Sets whether native picker chrome is disabled for one element.
        /// </summary>
        public static void SetCompactPicker(BindableObject element, bool value) =>
            element.SetValue(CompactPickerProperty, value);

        /// <summary>
        /// Applies flat entry chrome immediately and after handler recreation.
        /// </summary>
        public static void ApplyFlatEntry(Entry entry)
        {
            entry.HandlerChanged -= OnEntryHandlerChanged;
            entry.HandlerChanged += OnEntryHandlerChanged;
            ApplyFlatEntryPlatform(entry);
        }

        /// <summary>
        /// Applies compact picker chrome immediately and after handler recreation.
        /// </summary>
        public static void ApplyCompactPicker(Picker picker)
        {
            picker.HandlerChanged -= OnPickerHandlerChanged;
            picker.HandlerChanged += OnPickerHandlerChanged;
            ApplyCompactPickerPlatform(picker);
        }

        /// <summary>
        /// Enables flat entry behavior when requested from XAML.
        /// </summary>
        private static void OnFlatEntryChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is Entry entry && newValue is true)
            {
                ApplyFlatEntry(entry);
            }
        }

        /// <summary>
        /// Enables compact picker behavior when requested from XAML.
        /// </summary>
        private static void OnCompactPickerChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is Picker picker && newValue is true)
            {
                ApplyCompactPicker(picker);
            }
        }

        /// <summary>
        /// Reapplies flat entry chrome after MAUI creates a platform handler.
        /// </summary>
        private static void OnEntryHandlerChanged(object? sender, EventArgs e)
        {
            if (sender is Entry entry)
            {
                ApplyFlatEntryPlatform(entry);
            }
        }

        /// <summary>
        /// Reapplies compact picker chrome after MAUI creates a platform handler.
        /// </summary>
        private static void OnPickerHandlerChanged(object? sender, EventArgs e)
        {
            if (sender is Picker picker)
            {
                ApplyCompactPickerPlatform(picker);
            }
        }

        /// <summary>
        /// Removes native entry chrome on platforms that draw an additional input shell.
        /// </summary>
        private static void ApplyFlatEntryPlatform(Entry entry)
        {
#if WINDOWS
            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

            switch (entry.Handler?.PlatformView)
            {
                case Microsoft.UI.Xaml.Controls.TextBox textBox:
                    textBox.Background = transparentBrush;
                    textBox.BorderBrush = transparentBrush;
                    textBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    textBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    textBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    textBox.UseSystemFocusVisuals = false;
                    break;
                case Microsoft.UI.Xaml.Controls.PasswordBox passwordBox:
                    passwordBox.Background = transparentBrush;
                    passwordBox.BorderBrush = transparentBrush;
                    passwordBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    passwordBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    passwordBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    passwordBox.UseSystemFocusVisuals = false;
                    break;
            }
#endif
        }

        /// <summary>
        /// Removes native picker chrome on platforms that draw an additional selector shell.
        /// </summary>
        private static void ApplyCompactPickerPlatform(Picker picker)
        {
#if WINDOWS
            if (picker.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ComboBox comboBox)
            {
                var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                comboBox.Background = transparentBrush;
                comboBox.BorderBrush = transparentBrush;
                comboBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                comboBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                comboBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                comboBox.UseSystemFocusVisuals = false;
            }
#endif
        }
    }
}
