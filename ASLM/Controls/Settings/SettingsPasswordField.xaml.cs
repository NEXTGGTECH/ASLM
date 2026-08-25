// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Provides a XAML-defined password editor with a visibility action.
    /// </summary>
    public partial class SettingsPasswordField : ContentView
    {
        private const string PasswordIconHidden = "icon_password_off.png";
        private const string PasswordIconVisible = "icon_password_on.png";

        /// <summary>
        /// Identifies the two-way password text bindable property.
        /// </summary>
        public static readonly BindableProperty TextProperty = BindableProperty.Create(
            nameof(Text),
            typeof(string),
            typeof(SettingsPasswordField),
            string.Empty,
            BindingMode.TwoWay);

        /// <summary>
        /// Creates the password field with hidden text.
        /// </summary>
        public SettingsPasswordField()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Gets or sets the password text displayed by the editor.
        /// </summary>
        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>
        /// Gets the underlying entry for legacy event integration during migration.
        /// </summary>
        public Entry Input => PasswordEntry;

        /// <summary>
        /// Toggles password visibility and updates the action icon.
        /// </summary>
        private void OnToggleTapped(object? sender, TappedEventArgs e)
        {
            PasswordEntry.IsPassword = !PasswordEntry.IsPassword;
            ToggleIcon.Source = PasswordEntry.IsPassword
                ? PasswordIconHidden
                : PasswordIconVisible;
        }
    }
}
