// Copyright NEXTGGTECH. Apache License 2.0.

#if WINDOWS
using System.ComponentModel;
using Microsoft.Maui.Platform;
using WinUIComboBox = Microsoft.UI.Xaml.Controls.ComboBox;
using WinUISolidColorBrush = Microsoft.UI.Xaml.Media.SolidColorBrush;
#endif

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Applies shared styling to XAML-defined settings controls and selectors.
    /// </summary>
    public static class SettingsControlChrome
    {
#if WINDOWS
        /// <summary>
        /// Identifies the brush owned by the picker so theme updates never mutate a shared WinUI brush.
        /// </summary>
        private const string PickerItemBrushKey = "AslmPickerItemForeground";


#endif
        // Attached properties

        /// <summary>
        /// Identifies the attached property that, when true, removes the native border and background of an entry.
        /// </summary>
        public static readonly BindableProperty FlatEntryProperty = BindableProperty.CreateAttached(
            "FlatEntry",
            typeof(bool),
            typeof(SettingsControlChrome),
            false,
            propertyChanged: OnFlatEntryChanged);

        /// <summary>
        /// Identifies the attached property that, when true, removes the native border and background of a picker.
        /// </summary>
        public static readonly BindableProperty CompactPickerProperty = BindableProperty.CreateAttached(
            "CompactPicker",
            typeof(bool),
            typeof(SettingsControlChrome),
            false,
            propertyChanged: OnCompactPickerChanged);

        /// <summary>
        /// Lets selector states switch the text resource key while keeping the text itself dynamically bound.
        /// </summary>
        public static readonly BindableProperty TextColorResourceProperty = BindableProperty.CreateAttached(
            "TextColorResource", typeof(string), typeof(SettingsControlChrome), null,
            propertyChanged: OnTextColorResourceChanged);

        /// <summary>
        /// Lets picker states switch the title resource key while keeping the title dynamically bound.
        /// </summary>
        public static readonly BindableProperty TitleColorResourceProperty = BindableProperty.CreateAttached(
            "TitleColorResource", typeof(string), typeof(SettingsControlChrome), null,
            propertyChanged: OnTitleColorResourceChanged);

        /// <summary>
        /// Lets selector states switch the border resource key while keeping the stroke dynamically bound.
        /// </summary>
        public static readonly BindableProperty StrokeResourceProperty = BindableProperty.CreateAttached(
            "StrokeResource", typeof(string), typeof(SettingsControlChrome), null,
            propertyChanged: OnStrokeResourceChanged);


        // Property accessors

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
        /// Gets the palette key currently used for the selector text.
        /// </summary>
        public static string? GetTextColorResource(BindableObject element) => (string?)element.GetValue(TextColorResourceProperty);

        /// <summary>
        /// Changes the palette key used for the selector text.
        /// </summary>
        public static void SetTextColorResource(BindableObject element, string? value) =>
            element.SetValue(TextColorResourceProperty, value);

        /// <summary>
        /// Gets the palette key currently used for the picker title.
        /// </summary>
        public static string? GetTitleColorResource(BindableObject element) =>
            (string?)element.GetValue(TitleColorResourceProperty);

        /// <summary>
        /// Changes the palette key used for the picker title.
        /// </summary>
        public static void SetTitleColorResource(BindableObject element, string? value) =>
            element.SetValue(TitleColorResourceProperty, value);

        /// <summary>
        /// Gets the palette key currently used for a selector border.
        /// </summary>
        public static string? GetStrokeResource(BindableObject element) => (string?)element.GetValue(StrokeResourceProperty);

        /// <summary>
        /// Changes the palette key used for a selector border.
        /// </summary>
        public static void SetStrokeResource(BindableObject element, string? value) => element.SetValue(StrokeResourceProperty, value);


        // Chrome application

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
#if WINDOWS
            picker.PropertyChanged -= OnPickerTextColorChanged;
            picker.PropertyChanged += OnPickerTextColorChanged;
#endif
            ApplyCompactPickerPlatform(picker);
        }


        // Change handlers

        /// <summary>
        /// Rebinds text when entering or leaving an active state so the restored color follows later theme changes.
        /// </summary>
        private static void OnTextColorResourceChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (newValue is not string key) return;
            if (bindable is Label label) label.SetDynamicResource(Label.TextColorProperty, key);
            if (bindable is Button button) button.SetDynamicResource(Button.TextColorProperty, key);
            if (bindable is Picker picker) picker.SetDynamicResource(Picker.TextColorProperty, key);
        }

        /// <summary>
        /// Rebinds the title when the picker is enabled or disabled.
        /// </summary>
        private static void OnTitleColorResourceChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is Picker picker && newValue is string key) picker.SetDynamicResource(Picker.TitleColorProperty, key);
        }

        /// <summary>
        /// Rebinds the border to the resource corresponding to its current selection state.
        /// </summary>
        private static void OnStrokeResourceChanged(BindableObject bindable, object oldValue, object newValue)
        {
            if (bindable is Border border && newValue is string key) border.SetDynamicResource(Border.StrokeProperty, key);
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


        // Platform chrome

        /// <summary>
        /// Removes native entry chrome on platforms that draw an additional input shell.
        /// </summary>
        private static void ApplyFlatEntryPlatform(Entry entry)
        {
#if WINDOWS
            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

            // WinUI draws single-line and password inputs with different native controls; flatten whichever is used.
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
            if (picker.Handler?.PlatformView is WinUIComboBox comboBox)
            {
                var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                comboBox.Background = transparentBrush;
                comboBox.BorderBrush = transparentBrush;
                comboBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                comboBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                comboBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                comboBox.UseSystemFocusVisuals = false;
                ApplyPickerItemTextColor(picker, comboBox);
            }
#endif
        }

#if WINDOWS
        /// <summary>
        /// Updates popup rows when the picker's existing text resource changes.
        /// </summary>
        private static void OnPickerTextColorChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == Picker.TextColorProperty.PropertyName &&
                sender is Picker { Handler.PlatformView: WinUIComboBox comboBox } picker)
                ApplyPickerItemTextColor(picker, comboBox);
        }

        /// <summary>
        /// Shares a foreground brush between native row states so even an open dropdown follows the text color.
        /// </summary>
        private static void ApplyPickerItemTextColor(Picker picker, WinUIComboBox comboBox)
        {
            if (picker.TextColor is not { } textColor) return;
            var color = textColor.ToWindowsColor();
            if (comboBox.Resources.TryGetValue(PickerItemBrushKey, out var resource) &&
                resource is WinUISolidColorBrush brush)
            {
                brush.Color = color;
                return;
            }

            brush = new WinUISolidColorBrush(color);
            comboBox.Resources[PickerItemBrushKey] = brush;
            comboBox.Resources["ComboBoxDropDownForeground"] = brush;
            comboBox.Resources["ComboBoxItemForeground"] = brush;
            comboBox.Resources["ComboBoxItemForegroundPointerOver"] = brush;
            comboBox.Resources["ComboBoxItemForegroundPressed"] = brush;
            comboBox.Resources["ComboBoxItemForegroundSelected"] = brush;
            comboBox.Resources["ComboBoxItemForegroundSelectedUnfocused"] = brush;
            comboBox.Resources["ComboBoxItemForegroundSelectedPointerOver"] = brush;
            comboBox.Resources["ComboBoxItemForegroundSelectedPressed"] = brush;
        }
#endif
    }
}
