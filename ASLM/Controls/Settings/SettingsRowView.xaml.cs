// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Renders one standard title, description, and trailing editor row from XAML.
    /// </summary>
    public partial class SettingsRowView : ContentView
    {
        /// <summary>
        /// Identifies the row title bindable property.
        /// </summary>
        public static readonly BindableProperty TitleProperty = BindableProperty.Create(
            nameof(Title),
            typeof(string),
            typeof(SettingsRowView),
            string.Empty);

        /// <summary>
        /// Identifies the row description bindable property.
        /// </summary>
        public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
            nameof(Description),
            typeof(string),
            typeof(SettingsRowView),
            string.Empty,
            propertyChanged: OnDescriptionChanged);

        /// <summary>
        /// Identifies the trailing editor bindable property.
        /// </summary>
        public static readonly BindableProperty TrailingContentProperty = BindableProperty.Create(
            nameof(TrailingContent),
            typeof(View),
            typeof(SettingsRowView));

        /// <summary>
        /// Identifies whether the description label should be rendered.
        /// </summary>
        public static readonly BindableProperty HasDescriptionProperty = BindableProperty.Create(
            nameof(HasDescription),
            typeof(bool),
            typeof(SettingsRowView),
            false);

        /// <summary>
        /// Creates an empty settings row populated through bindable properties.
        /// </summary>
        public SettingsRowView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Gets or sets the primary row title.
        /// </summary>
        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>
        /// Gets or sets the optional secondary description.
        /// </summary>
        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// Gets or sets the editor or action displayed on the right.
        /// </summary>
        public View? TrailingContent
        {
            get => (View?)GetValue(TrailingContentProperty);
            set => SetValue(TrailingContentProperty, value);
        }

        /// <summary>
        /// Gets whether the optional description label is visible.
        /// </summary>
        public bool HasDescription
        {
            get => (bool)GetValue(HasDescriptionProperty);
            private set => SetValue(HasDescriptionProperty, value);
        }

        /// <summary>
        /// Updates description visibility when localized text changes.
        /// </summary>
        private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue)
        {
            ((SettingsRowView)bindable).HasDescription = !string.IsNullOrWhiteSpace(newValue as string);
        }
    }
}
