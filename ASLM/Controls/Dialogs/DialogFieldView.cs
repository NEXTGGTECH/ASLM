// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Controls.Settings;
using ASLM.Models;

namespace ASLM.Controls.Dialogs
{
    /// <summary>
    /// Renders a labeled typed input without owning form actions or persisting its values.
    /// </summary>
    internal sealed class DialogFieldView : ContentView, IDisposable
    {
        // Fields

        /// <summary>
        /// Caption shown above the input.
        /// </summary>
        private readonly Label _label = new();

        /// <summary>
        /// Validation message shown below the input; hidden while there is no error.
        /// </summary>
        private readonly Label _error = new();

        /// <summary>
        /// Native input control chosen for the field kind; it is the same instance as exactly one of the typed
        /// fields below.
        /// </summary>
        private readonly View _input;

        /// <summary>
        /// Single-line input used for text, password, integer and decimal fields, or null for other kinds.
        /// </summary>
        private readonly Entry? _entry;

        /// <summary>
        /// Multi-line input used for multiline fields, or null for other kinds.
        /// </summary>
        private readonly Editor? _editor;

        /// <summary>
        /// Drop-down used for choice fields, or null for other kinds.
        /// </summary>
        private readonly Picker? _picker;

        /// <summary>
        /// Switch used for boolean fields, or null for other kinds.
        /// </summary>
        private readonly Switch? _toggle;

        /// <summary>
        /// True while the view writes into native controls, so their change events do not echo back into the model.
        /// </summary>
        private bool _syncing;


        // Properties

        /// <summary>
        /// Dialog field model that this view edits.
        /// </summary>
        public DialogField Field { get; }


        // Initialization

        /// <summary>
        /// Creates the editor appropriate to the field type and connects bidirectional value updates.
        /// </summary>
        /// <param name="field">Field model to edit.</param>
        /// <param name="submit">Invoked when the user confirms a single-line input with Enter.</param>
        public DialogFieldView(DialogField field, Func<Task> submit)
        {
            // Label and error captions share the dialog text style; the error is hidden until validation fails.
            Field = field;
            _label.SetDynamicResource(StyleProperty, "DialogTextStyle");
            _error.SetDynamicResource(StyleProperty, "DialogTextStyle");
            _error.SetDynamicResource(Label.TextColorProperty, "ActionRed");
            _error.IsVisible = false;
            var layout = new VerticalStackLayout { Spacing = 4 };
            layout.Add(_label);

            // Use the same input chrome as settings while retaining each native editor's behavior.
            if (field.Kind == DialogFieldKind.Boolean)
            {
                _toggle = new Switch { HorizontalOptions = LayoutOptions.Start };
                _toggle.Toggled += (_, e) => { if (!_syncing) field.Value = e.Value; };
                _input = _toggle;
            }
            else if (field.Kind == DialogFieldKind.Choice)
            {
                // The picker stores the selected choice ID, never its translated caption.
                _picker = new Picker { FontSize = 14 };
                _picker.SetDynamicResource(Picker.TextColorProperty, "LabelPrimary");
                SettingsControlChrome.SetCompactPicker(_picker, true);
                _picker.SelectedIndexChanged += (_, _) =>
                {
                    if (!_syncing && _picker.SelectedIndex >= 0 && _picker.SelectedIndex < field.Choices.Count)
                        field.Value = field.Choices[_picker.SelectedIndex].Id;
                };
                _input = _picker;
            }
            else if (field.Kind == DialogFieldKind.Multiline)
            {
                _editor = new Editor { FontSize = 14, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80,
                    MaxLength = field.MaxLength };
                _editor.SetDynamicResource(Editor.TextColorProperty, "LabelPrimary");
                _editor.TextChanged += (_, e) => { if (!_syncing) field.Value = e.NewTextValue; };
                _input = _editor;
            }
            else
            {
                // Text, password and numeric kinds share one entry; numeric kinds only switch the keyboard.
                _entry = new Entry { FontSize = 14, IsPassword = field.Kind == DialogFieldKind.Password, MaxLength = field.MaxLength,
                    IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
                    Keyboard = field.Kind is DialogFieldKind.Integer or DialogFieldKind.Decimal ? Keyboard.Numeric : Keyboard.Text };
                _entry.SetDynamicResource(Entry.TextColorProperty, "LabelPrimary");
                _entry.SetDynamicResource(Entry.PlaceholderColorProperty, "LabelSecondary");
                SettingsControlChrome.SetFlatEntry(_entry, true);
                _entry.TextChanged += (_, e) => { if (!_syncing) field.Value = e.NewTextValue; };
                _entry.Completed += async (_, _) => await submit();
                _input = _entry;
            }

            // Switches already have their own chrome; text and choice inputs share a field border.
            if (field.Kind == DialogFieldKind.Boolean) layout.Add(_input);
            else layout.Add(new SettingsFieldView { FieldContent = _input });
            layout.Add(_error);
            Content = layout;

            // Keep the native input in sync when the model changes from outside this view.
            field.ValueChanged += OnValueChanged;
            field.PresentationChanged += OnPresentationChanged;
        }


        // Model change handlers

        /// <summary>
        /// Refreshes a dependent selector after its options, label, or loading state change.
        /// </summary>
        private void OnPresentationChanged(object? sender, EventArgs e)
        {
            ApplyLocalization();
            ShowError(Field.PresentationError?.Resolve());
        }

        /// <summary>
        /// Synchronizes an externally changed value without resolving all labels on every keystroke.
        /// </summary>
        private void OnValueChanged(object? sender, EventArgs e)
        {
            SyncValue();
            ShowError(null);
        }


        // Presentation

        /// <summary>
        /// Focuses the actual input rather than its decorative field shell.
        /// </summary>
        public void FocusInput() => (_entry as View ?? _editor ?? _picker as View ?? _toggle)?.Focus();

        /// <summary>
        /// Updates labels and choice captions when the language or dependent field presentation changes.
        /// </summary>
        public void ApplyLocalization()
        {
            _syncing = true;
            try
            {
                // Resolve the caption, enabled state and placeholders from the model's localized texts.
                _label.Text = Field.Label.Resolve();
                _input.IsEnabled = Field.IsEnabled;
                if (_entry != null) _entry.Placeholder = Field.Placeholder?.Resolve();
                if (_editor != null) _editor.Placeholder = Field.Placeholder?.Resolve();
                if (_picker != null)
                {
                    // Rebuild native picker items only when translated captions actually differ.
                    var labels = Field.Choices.Select(c => c.Label.Resolve()).ToArray();
                    if (!_picker.Items.SequenceEqual(labels))
                    {
                        _picker.Items.Clear();
                        foreach (var label in labels) _picker.Items.Add(label);
                    }
                }

                // Screen readers announce the caption for the otherwise unlabeled native input.
                SemanticProperties.SetDescription(_input, _label.Text);
            }
            finally { _syncing = false; }

            // Items may have been rebuilt, so reselect the model value afterwards.
            SyncValue();
        }

        /// <summary>
        /// Updates the validation message without rebuilding editor state or picker options.
        /// </summary>
        public void ShowError(string? error)
        {
            _error.Text = error;
            _error.IsVisible = !string.IsNullOrEmpty(error);
        }

        /// <summary>
        /// Copies the model value to its native editor without emitting a second model change.
        /// </summary>
        private void SyncValue()
        {
            _syncing = true;
            try
            {
                var text = Field.Value?.ToString() ?? string.Empty;
                if (_entry != null && _entry.Text != text) _entry.Text = text;
                if (_editor != null && _editor.Text != text) _editor.Text = text;
                if (_toggle != null) _toggle.IsToggled = Field.Value is true;
                if (_picker != null)
                {
                    // Resolve by stable ID; translated text must never become a persisted value.
                    var index = -1;
                    for (var i = 0; i < Field.Choices.Count; i++)
                        if (Field.Choices[i].Id == text) { index = i; break; }
                    if (_picker.SelectedIndex != index) _picker.SelectedIndex = index;
                }
            }
            finally { _syncing = false; }
        }


        // Lifecycle

        /// <summary>
        /// Releases the model subscription when the dialog discards its input controls.
        /// </summary>
        public void Dispose()
        {
            Field.ValueChanged -= OnValueChanged;
            Field.PresentationChanged -= OnPresentationChanged;
        }
    }
}
