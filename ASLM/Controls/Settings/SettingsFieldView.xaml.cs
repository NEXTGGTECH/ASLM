// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Hosts one editor inside the shared XAML-defined settings field border.
    /// </summary>
    public partial class SettingsFieldView : ContentView
    {
        // Bindable properties

        /// <summary>
        /// Identifies the bindable property that holds the editor rendered inside the field shell.
        /// </summary>
        public static readonly BindableProperty FieldContentProperty = BindableProperty.Create(
            nameof(FieldContent),
            typeof(View),
            typeof(SettingsFieldView));

        /// <summary>
        /// Identifies the bindable property that holds the padding around the hosted editor.
        /// </summary>
        public static readonly BindableProperty FieldPaddingProperty = BindableProperty.Create(
            nameof(FieldPadding),
            typeof(Thickness),
            typeof(SettingsFieldView),
            new Thickness(8, 0));


        // Initialization

        /// <summary>
        /// Creates a field shell with the standard input padding.
        /// </summary>
        public SettingsFieldView()
        {
            InitializeComponent();
        }


        // Properties

        /// <summary>
        /// Gets or sets the editor rendered inside the field shell.
        /// </summary>
        public View? FieldContent
        {
            get => (View?)GetValue(FieldContentProperty);
            set => SetValue(FieldContentProperty, value);
        }

        /// <summary>
        /// Gets or sets padding applied around the hosted editor.
        /// </summary>
        public Thickness FieldPadding
        {
            get => (Thickness)GetValue(FieldPaddingProperty);
            set => SetValue(FieldPaddingProperty, value);
        }
    }
}
