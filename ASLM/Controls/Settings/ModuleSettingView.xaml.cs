// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Services.Internal;

namespace ASLM.Controls.Settings
{
    /// <summary>
    /// Renders one typed module setting editor entirely from XAML bindings.
    /// </summary>
    public partial class ModuleSettingView : ContentView
    {
        /// <summary>
        /// Creates the setting template declared in XAML.
        /// </summary>
        public ModuleSettingView()
        {
            InitializeComponent();
            foreach (var editor in this.GetVisualTreeDescendants().OfType<VisualElement>())
            {
                editor.Focused += OnEditorFocused;
                editor.Unfocused += OnEditorUnfocused;
            }
        }

        /// <summary>
        /// Defers external value notifications while the user owns the row's keyboard focus.
        /// </summary>
        private void OnEditorFocused(object? sender, FocusEventArgs e)
        {
            if (BindingContext is ModuleSettingItemViewModel item) item.IsEditing = true;
        }

        /// <summary>
        /// Resumes deferred refresh only after focus has left every editor within the row.
        /// </summary>
        private void OnEditorUnfocused(object? sender, FocusEventArgs e) => Dispatcher.Dispatch(() =>
        {
            if (this.GetVisualTreeDescendants().OfType<VisualElement>().Any(editor => editor.IsFocused)) return;
            if (BindingContext is ModuleSettingItemViewModel item) item.EndEditing();
        });
    }
}
