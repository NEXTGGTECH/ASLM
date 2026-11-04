// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Renders one standard title, contextual help icon, and trailing editor row from XAML.
    /// </summary>
    public partial class SettingsRowView : ContentView
    {
        // Bindable properties

        /// <summary>
        /// Identifies the bindable property that holds the primary row title.
        /// </summary>
        public static readonly BindableProperty TitleProperty = BindableProperty.Create(
            nameof(Title),
            typeof(string),
            typeof(SettingsRowView),
            string.Empty);

        /// <summary>
        /// Identifies the bindable property that holds the optional secondary description.
        /// </summary>
        public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
            nameof(Description),
            typeof(string),
            typeof(SettingsRowView),
            string.Empty);

        /// <summary>
        /// Identifies the bindable property that holds the editor or action shown at the trailing edge.
        /// </summary>
        public static readonly BindableProperty TrailingContentProperty = BindableProperty.Create(
            nameof(TrailingContent),
            typeof(View),
            typeof(SettingsRowView));


        // Initialization

        /// <summary>
        /// Creates an empty settings row populated through bindable properties.
        /// </summary>
        public SettingsRowView()
        {
            InitializeComponent();
        }


        // Properties

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
    }
}
