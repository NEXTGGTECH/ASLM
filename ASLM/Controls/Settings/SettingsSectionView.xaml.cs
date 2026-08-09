// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Provides the shared XAML-defined shell for one settings section.
    /// </summary>
    public partial class SettingsSectionView : Border
    {
        /// <summary>
        /// Identifies the optional section title bindable property.
        /// </summary>
        public static readonly BindableProperty TitleProperty = BindableProperty.Create(
            nameof(Title),
            typeof(string),
            typeof(SettingsSectionView),
            default(string),
            propertyChanged: OnTitleChanged);

        /// <summary>
        /// Identifies the optional section description bindable property.
        /// </summary>
        public static readonly BindableProperty DescriptionProperty = BindableProperty.Create(
            nameof(Description),
            typeof(string),
            typeof(SettingsSectionView),
            default(string),
            propertyChanged: OnDescriptionChanged);

        /// <summary>
        /// Identifies the settings rows hosted below the shared section header.
        /// </summary>
        public static readonly BindableProperty SectionContentProperty = BindableProperty.Create(
            nameof(SectionContent),
            typeof(View),
            typeof(SettingsSectionView));

        /// <summary>
        /// Creates a shared category shell whose metadata and content are supplied by its caller.
        /// </summary>
        public SettingsSectionView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Gets or sets the category title displayed above its settings.
        /// </summary>
        public string? Title
        {
            get => (string?)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        /// <summary>
        /// Gets or sets the optional category description.
        /// </summary>
        public string? Description
        {
            get => (string?)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>
        /// Gets or sets the settings rows rendered below the category metadata.
        /// </summary>
        public View? SectionContent
        {
            get => (View?)GetValue(SectionContentProperty);
            set => SetValue(SectionContentProperty, value);
        }

        /// <summary>
        /// Gets whether the shared title row should be visible.
        /// </summary>
        public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

        /// <summary>
        /// Gets whether the shared description row should be visible.
        /// </summary>
        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        /// <summary>
        /// Refreshes title visibility when category metadata changes.
        /// </summary>
        private static void OnTitleChanged(BindableObject bindable, object oldValue, object newValue) =>
            ((SettingsSectionView)bindable).OnPropertyChanged(nameof(HasTitle));

        /// <summary>
        /// Refreshes description visibility when category metadata changes.
        /// </summary>
        private static void OnDescriptionChanged(BindableObject bindable, object oldValue, object newValue) =>
            ((SettingsSectionView)bindable).OnPropertyChanged(nameof(HasDescription));
    }
}
