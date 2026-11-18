// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    // Setting editor

    /// <summary>
    /// Identifies the XAML editor displayed for one module setting.
    /// </summary>
    public enum ModuleSettingEditorKind
    {
        /// <summary>
        /// Read-only indicator of whether an ASLM-managed engine is installed.
        /// </summary>
        EngineStatus,

        /// <summary>
        /// On/off toggle.
        /// </summary>
        Boolean,

        /// <summary>
        /// Host-managed value that is shown automatically unless the user switches to a custom value.
        /// </summary>
        Managed,

        /// <summary>
        /// Picker over a fixed list of allowed values.
        /// </summary>
        Choice,

        /// <summary>
        /// Entry for integer and floating-point numbers.
        /// </summary>
        Numeric,

        /// <summary>
        /// Entry that hides its content.
        /// </summary>
        Password,

        /// <summary>
        /// Plain single-value text entry.
        /// </summary>
        Text
    }

    /// <summary>
    /// Exposes one detached module setting as typed bindable editor state.
    /// </summary>
    public sealed class ModuleSettingItemViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Callback that tells the owning page that an editor changed its draft.
        /// </summary>
        private readonly Action _valueChanged;

        /// <summary>
        /// Wakes deferred value work after focus leaves without re-evaluating every dependency on focus changes.
        /// </summary>
        private readonly Action _editingEnded;


        /// <summary>
        /// Backing field of <see cref="TextValue"/>.
        /// </summary>
        private string _textValue;

        /// <summary>
        /// Backing field of <see cref="SelectedValue"/>.
        /// </summary>
        private string? _selectedValue;

        /// <summary>
        /// Backing field of <see cref="Options"/>.
        /// </summary>
        private IReadOnlyList<string> _options;

        /// <summary>
        /// Backing field of <see cref="BooleanValue"/>.
        /// </summary>
        private bool _booleanValue;

        /// <summary>
        /// Backing field of <see cref="UseCustomValue"/>.
        /// </summary>
        private bool _useCustomValue;

        /// <summary>
        /// Backing field of <see cref="IsVisible"/>.
        /// </summary>
        private bool _isVisible;

        /// <summary>
        /// Defers first-time native measurement until this prepared editor is revealed by the incremental renderer.
        /// </summary>
        private bool _isPresented = true;

        /// <summary>
        /// Text of the user's custom value, shown again when a managed setting switches back from automatic mode.
        /// </summary>
        private string _customTextValue;


        // Initialization

        /// <summary>
        /// Creates typed editor state from one detached setting draft.
        /// </summary>
        /// <param name="draft">Draft that the editor reads and updates.</param>
        /// <param name="engineInstalledText">Localized text shown when an engine is installed.</param>
        /// <param name="engineNotInstalledText">Localized text shown when an engine is not installed.</param>
        /// <param name="valueChanged">Callback invoked after the editor changed the draft.</param>
        /// <param name="editingEnded">Optional callback that resumes deferred values after focus leaves.</param>
        public ModuleSettingItemViewModel(
            ModuleSettingDraft draft,
            string engineInstalledText,
            string engineNotInstalledText,
            Action valueChanged,
            Action? editingEnded = null)
        {
            // The editor kind comes first because the initial display state depends on it.
            Draft = draft;
            _valueChanged = valueChanged;
            _editingEnded = editingEnded ?? valueChanged;
            EngineInstalledText = engineInstalledText;
            EngineNotInstalledText = engineNotInstalledText;
            EditorKind = ResolveEditorKind(draft);
            _options = BuildOptions(draft);
            _useCustomValue = draft.UseCustomValue;
            _booleanValue = ResolveBooleanValue(draft.EffectiveValue, draft.Setting);
            _customTextValue = draft.Setting.FormatValueForDisplay(draft.Value);
            _textValue = ResolveDisplayedText();
            _selectedValue = ResolveSelectedValue(draft, _options);
            _isVisible = draft.IsVisible;
        }


        // Setting metadata

        /// <summary>
        /// Gets the detached draft updated by editor bindings.
        /// </summary>
        public ModuleSettingDraft Draft { get; private set; }

        /// <summary>
        /// Gets the editor kind consumed by XAML visibility properties.
        /// </summary>
        public ModuleSettingEditorKind EditorKind { get; private set; }

        /// <summary>
        /// Gets the setting title from the module manifest.
        /// </summary>
        public string Title => Draft.Setting.Name;

        /// <summary>
        /// Gets the trimmed setting description from the module manifest.
        /// </summary>
        public string Description => SettingsService.BuildSettingDescription(Draft.Setting);

        /// <summary>
        /// Gets choice values including a persisted value absent from current metadata.
        /// </summary>
        public IReadOnlyList<string> Options
        {
            get => _options;
            private set => SetProperty(ref _options, value);
        }

        /// <summary>
        /// Gets localized installed text for read-only engine state.
        /// </summary>
        public string EngineInstalledText { get; }

        /// <summary>
        /// Gets localized unavailable text for read-only engine state.
        /// </summary>
        public string EngineNotInstalledText { get; }

        /// <summary>
        /// Gets whether dependency rules currently render this setting.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible && _isPresented;
            private set => SetProperty(ref _isVisible, value);
        }


        // Editor template state

        /// <summary>
        /// Gets whether the engine status reports an installed runtime.
        /// </summary>
        public bool IsEngineInstalled => _booleanValue;

        /// <summary>
        /// Gets whether the engine status reports a missing runtime.
        /// </summary>
        public bool IsEngineMissing => !_booleanValue;

        /// <summary>
        /// Gets whether the read-only engine status template is active.
        /// </summary>
        public bool IsEngineStatus => EditorKind == ModuleSettingEditorKind.EngineStatus;

        /// <summary>
        /// Gets whether the boolean toggle template is active.
        /// </summary>
        public bool IsBooleanEditor => EditorKind == ModuleSettingEditorKind.Boolean;

        /// <summary>
        /// Gets whether the host-managed custom-value template is active.
        /// </summary>
        public bool IsManagedEditor => EditorKind == ModuleSettingEditorKind.Managed;

        /// <summary>
        /// Gets whether the choice picker template is active.
        /// </summary>
        public bool IsChoiceEditor => EditorKind == ModuleSettingEditorKind.Choice;

        /// <summary>
        /// Gets whether the numeric entry template is active.
        /// </summary>
        public bool IsNumericEditor => EditorKind == ModuleSettingEditorKind.Numeric;

        /// <summary>
        /// Gets whether the password entry template is active.
        /// </summary>
        public bool IsPasswordEditor => EditorKind == ModuleSettingEditorKind.Password;

        /// <summary>
        /// Gets whether the plain text entry template is active.
        /// </summary>
        public bool IsTextEditor => EditorKind == ModuleSettingEditorKind.Text;

        /// <summary>
        /// Gets whether the managed entry must remain read-only in automatic mode.
        /// </summary>
        public bool IsManagedReadOnly => IsManagedEditor && !UseCustomValue;

        /// <summary>
        /// Gets whether a managed text editor must hide password content.
        /// </summary>
        public bool IsManagedPassword =>
            IsManagedEditor && Draft.Setting.NormalizedType == "password";

        /// <summary>
        /// Gets whether the common value-editor block is active.
        /// </summary>
        public bool IsValueEditor => !IsEngineStatus && !IsBooleanEditor;

        /// <summary>
        /// Gets the visual opacity applied to automatic managed values.
        /// </summary>
        public double ManagedEditorOpacity => IsManagedReadOnly ? 0.72 : 1.0;


        // Editable values

        /// <summary>
        /// Gets or sets the boolean value committed to the detached draft.
        /// </summary>
        public bool BooleanValue
        {
            get => _booleanValue;
            set
            {
                if (!SetProperty(ref _booleanValue, value))
                {
                    return;
                }

                // Commit to the draft, refresh the computed engine flags and notify the page.
                Draft.Value = value;
                RaisePropertyChanged(nameof(IsEngineInstalled));
                RaisePropertyChanged(nameof(IsEngineMissing));
                _valueChanged();
            }
        }

        /// <summary>
        /// Gets or sets the formatted text value committed to the detached draft.
        /// </summary>
        public string TextValue
        {
            get => _textValue;
            set
            {
                value ??= string.Empty;
                if (!SetProperty(ref _textValue, value))
                {
                    return;
                }

                // Text typed into an automatic managed entry is only a display; it never reaches the draft.
                if (!IsManagedEditor || UseCustomValue)
                {
                    _customTextValue = value;
                    Draft.Value = Draft.Setting.NormalizeUserValue(value);
                    _valueChanged();
                }
            }
        }

        /// <summary>
        /// Gets or sets the selected choice committed to the detached draft.
        /// </summary>
        public string? SelectedValue
        {
            get => _selectedValue;
            set
            {
                if (!SetProperty(ref _selectedValue, value) || value == null)
                {
                    return;
                }

                Draft.Value = Draft.Setting.NormalizeUserValue(value);
                _valueChanged();
            }
        }

        /// <summary>
        /// Gets or sets whether a managed setting uses its custom draft value.
        /// </summary>
        public bool UseCustomValue
        {
            get => _useCustomValue;
            set
            {
                if (!SetProperty(ref _useCustomValue, value))
                {
                    return;
                }

                // Custom mode shows the user's own text; automatic mode shows the host-resolved value.
                Draft.UseCustomValue = value;
                if (value)
                {
                    SetProperty(ref _textValue, _customTextValue, nameof(TextValue));
                }
                else
                {
                    SetProperty(ref _textValue, ResolveDisplayedText(), nameof(TextValue));
                }

                RaisePropertyChanged(nameof(IsManagedReadOnly));
                RaisePropertyChanged(nameof(ManagedEditorOpacity));
                _valueChanged();
            }
        }


        // Refreshing from the draft

        /// <summary>
        /// Refreshes dependency visibility without recreating the editor view model.
        /// </summary>
        public void RefreshVisibility() => IsVisible = Draft.IsVisible;

        /// <summary>
        /// Copies accepted or reset draft values into existing bound editor properties.
        /// </summary>
        public void RefreshFromDraft()
        {
            var kind = ResolveEditorKind(Draft);
            if (kind != EditorKind)
            {
                EditorKind = kind;
                foreach (var property in new[] { nameof(EditorKind), nameof(IsEngineStatus), nameof(IsBooleanEditor),
                    nameof(IsManagedEditor), nameof(IsChoiceEditor), nameof(IsNumericEditor), nameof(IsPasswordEditor),
                    nameof(IsTextEditor), nameof(IsValueEditor) }) RaisePropertyChanged(property);
            }

            // Choice lists can change with the draft, so rebuild them before reselecting the value.
            if (IsChoiceEditor)
            {
                var options = BuildOptions(Draft);
                if (!Options.SequenceEqual(options, StringComparer.Ordinal))
                {
                    Options = options;
                }

                SetProperty(ref _selectedValue, ResolveSelectedValue(Draft, Options), nameof(SelectedValue));
            }

            _customTextValue = Draft.Setting.FormatValueForDisplay(Draft.Value);
            SetProperty(ref _useCustomValue, Draft.UseCustomValue, nameof(UseCustomValue));
            SetProperty(
                ref _booleanValue,
                ResolveBooleanValue(Draft.EffectiveValue, Draft.Setting),
                nameof(BooleanValue));
            SetProperty(ref _textValue, ResolveDisplayedText(), nameof(TextValue));

            if (IsEngineStatus)
            {
                RaisePropertyChanged(nameof(IsEngineInstalled));
                RaisePropertyChanged(nameof(IsEngineMissing));
            }

            if (IsManagedEditor)
            {
                RaisePropertyChanged(nameof(IsManagedReadOnly));
                RaisePropertyChanged(nameof(ManagedEditorOpacity));
            }

            RefreshVisibility();
        }


        /// <summary>
        /// Gets whether an editor owns keyboard focus and should retain its text selection during runtime refresh.
        /// </summary>
        internal bool IsEditing { get; set; }

        /// <summary>
        /// Releases a deferred runtime refresh when focus leaves the editor.
        /// </summary>
        internal void EndEditing()
        {
            IsEditing = false;
            _editingEnded();
        }

        /// <summary>
        /// Updates compatible metadata while retaining the bound row and unchanged editor values.
        /// </summary>
        internal void Rebind(ModuleSettingDraft draft)
        {
            Draft = draft;
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(Description));
            if (!IsEditing) RefreshFromDraft();
        }

        /// <summary>
        /// Gets dependency visibility independently from whether the prepared row has been shown yet.
        /// </summary>
        internal bool IsLogicallyVisible => _isVisible;

        /// <summary>
        /// Gets whether the original native template may participate in layout.
        /// </summary>
        internal bool IsPresented => _isPresented;

        /// <summary>
        /// Reveals one prepared editor without attaching a complete unmeasured tree in one UI turn.
        /// </summary>
        internal void SetPresented(bool presented)
        {
            if (_isPresented == presented) return;
            _isPresented = presented;
            RaisePropertyChanged(nameof(IsVisible));
        }

        // Editor resolution

        /// <summary>
        /// Selects the editor kind that preserves the existing module settings contract.
        /// </summary>
        private static ModuleSettingEditorKind ResolveEditorKind(ModuleSettingDraft draft)
        {
            // The first matching rule wins: engine status, boolean, managed, choice, then by value type.
            var setting = draft.Setting;
            if (draft.IsReadOnly && setting.NormalizedType == "engine")
            {
                return ModuleSettingEditorKind.EngineStatus;
            }

            if (setting.NormalizedType is "bool" or "engine")
            {
                return ModuleSettingEditorKind.Boolean;
            }

            if (setting.IsAutomaticallyManaged)
            {
                return ModuleSettingEditorKind.Managed;
            }

            if (SettingsService.IsActiveEngineSelector(setting) || setting.AllowedValues is { Count: > 0 })
            {
                return ModuleSettingEditorKind.Choice;
            }

            return setting.NormalizedType switch
            {
                "int" or "integer" or "long" or "float" or "double" or "number" =>
                    ModuleSettingEditorKind.Numeric,
                "password" => ModuleSettingEditorKind.Password,
                _ => ModuleSettingEditorKind.Text
            };
        }

        /// <summary>
        /// Materializes picker options and preserves an existing value missing from allowed metadata.
        /// </summary>
        private static IReadOnlyList<string> BuildOptions(ModuleSettingDraft draft)
        {
            if (draft.Setting.AllowedValues is not { Count: > 0 } allowedValues)
            {
                return [];
            }

            var options = allowedValues.ToList();
            var currentValue = draft.Setting.FormatValueForDisplay(draft.EffectiveValue);
            if (!string.IsNullOrWhiteSpace(currentValue) &&
                options.All(option => !string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase)))
            {
                options.Insert(0, currentValue);
            }

            return options;
        }

        /// <summary>
        /// Resolves the initial picker selection from effective and default values.
        /// </summary>
        private static string? ResolveSelectedValue(
            ModuleSettingDraft draft,
            IReadOnlyList<string> options)
        {
            var currentValue = draft.Setting.FormatValueForDisplay(draft.EffectiveValue);
            if (string.IsNullOrWhiteSpace(currentValue))
            {
                currentValue = draft.Setting.FormatValueForDisplay(draft.Setting.Default);
            }

            return options.FirstOrDefault(option =>
                       string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase))
                   ?? options.FirstOrDefault();
        }

        /// <summary>
        /// Resolves boolean values from normalized or serialized manifest representations.
        /// </summary>
        private static bool ResolveBooleanValue(object? value, ModuleSetting setting) =>
            value is bool booleanValue
                ? booleanValue
                : bool.TryParse(setting.FormatValueForDisplay(value), out var parsed) && parsed;

        /// <summary>
        /// Resolves the text shown for custom or host-managed automatic mode.
        /// </summary>
        private string ResolveDisplayedText() =>
            Draft.Setting.FormatValueForDisplay(
                IsManagedEditor && !UseCustomValue
                    ? Draft.AutomaticValue ?? Draft.EffectiveValue
                    : Draft.Value);
    }


    // Settings section

    /// <summary>
    /// Exposes one module settings group and its dependency-driven visibility.
    /// </summary>
    public sealed class ModuleSettingsSectionViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Callback into the owning module page that handles a navigation request for this section.
        /// </summary>
        private readonly Action<ModuleSettingsSectionViewModel> _select;

        /// <summary>
        /// Backing field of <see cref="IsActive"/>.
        /// </summary>
        private bool _isActive;

        /// <summary>
        /// Backing field of <see cref="IsVisible"/>.
        /// </summary>
        private bool _isVisible;


        // Initialization

        /// <summary>
        /// Creates a bindable section from one presentation descriptor.
        /// </summary>
        /// <param name="section">Presentation descriptor that provides the layout role, title and description.</param>
        /// <param name="settings">Editor view models shown in the section.</param>
        /// <param name="navigationTitle">Title displayed in the module section navigation.</param>
        /// <param name="select">Callback invoked when the user asks to navigate to this section.</param>
        public ModuleSettingsSectionViewModel(
            ModuleSettingsSectionPresentation section,
            IEnumerable<ModuleSettingItemViewModel> settings,
            string navigationTitle,
            Action<ModuleSettingsSectionViewModel> select)
        {
            Key = section.Kind == ModuleSettingsSectionKind.Uncategorized ? "uncategorized" :
                "category:" + section.Settings.FirstOrDefault()?.Setting.Category;
            Kind = section.Kind;
            Title = section.Title;
            Description = section.Description;
            NavigationTitle = navigationTitle;
            _select = select;
            Settings = new ObservableCollection<ModuleSettingItemViewModel>(settings);
            _isVisible = Settings.Any(static item => item.IsLogicallyVisible);
            SelectCommand = new Command(Select, () => IsVisible);
        }


        // Section content

        /// <summary>
        /// Gets the original manifest identity independently from category title or row movement.
        /// </summary>
        internal string Key { get; }

        /// <summary>
        /// Gets the section layout role.
        /// </summary>
        public ModuleSettingsSectionKind Kind { get; }

        /// <summary>
        /// Gets the optional section title.
        /// </summary>
        public string? Title { get; private set; }

        /// <summary>
        /// Gets the optional section description.
        /// </summary>
        public string? Description { get; private set; }

        /// <summary>
        /// Gets the title displayed in the module section navigation.
        /// </summary>
        public string NavigationTitle { get; private set; }

        /// <summary>
        /// Gets setting rows in manifest declaration order.
        /// </summary>
        public ObservableCollection<ModuleSettingItemViewModel> Settings { get; }


        // Navigation state

        /// <summary>
        /// Gets the command that requests scrolling to this section.
        /// </summary>
        public Command SelectCommand { get; }

        /// <summary>
        /// Gets whether this section is selected in the module navigation.
        /// </summary>
        public bool IsActive
        {
            get => _isActive;
            private set => SetProperty(ref _isActive, value);
        }

        /// <summary>
        /// Gets whether dependency rules leave at least one row visible.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            private set => SetProperty(ref _isVisible, value);
        }


        // State changes

        /// <summary>
        /// Refreshes section metadata without replacing its card or section navigation command.
        /// </summary>
        internal void Rebind(ModuleSettingsSectionPresentation section, string navigationTitle)
        {
            Title = section.Title;
            Description = section.Description;
            NavigationTitle = navigationTitle;
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(NavigationTitle));
        }

        /// <summary>
        /// Refreshes the section after item visibility changes.
        /// </summary>
        public void RefreshVisibility()
        {
            // The select command can only run while the section is visible, so its availability follows visibility.
            var wasVisible = IsVisible;
            IsVisible = Settings.Any(static item => item.IsLogicallyVisible);
            if (wasVisible != IsVisible)
            {
                SelectCommand.ChangeCanExecute();
            }
        }

        /// <summary>
        /// Updates the selection state consumed by the navigation template.
        /// </summary>
        public void SetActive(bool isActive) => IsActive = isActive;

        /// <summary>
        /// Forwards a valid navigation request to the owning module page.
        /// </summary>
        private void Select()
        {
            if (IsVisible)
            {
                _select(this);
            }
        }
    }


    // Settings page

    /// <summary>
    /// Owns bindable module sections and refreshes dependency visibility in place.
    /// </summary>
    public sealed class ModuleSettingsPageViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Callback that refreshes the page's save actions after any editor changed a draft.
        /// </summary>
        private readonly Action _valueChanged;

        /// <summary>
        /// Callback that asks the settings page to reveal a section chosen in the navigation.
        /// </summary>
        private readonly Action<ModuleSettingsSectionViewModel> _sectionSelected;

        /// <summary>
        /// Draft whose settings are currently shown, or null before the first load.
        /// </summary>
        private ModuleSettingsDraft? _moduleDraft;

        /// <summary>
        /// Section highlighted in the module navigation, or null when none is visible.
        /// </summary>
        private ModuleSettingsSectionViewModel? _activeSection;

        /// <summary>
        /// Backing field of <see cref="HasSettings"/>.
        /// </summary>
        private bool _hasSettings;

        /// <summary>
        /// Backing field of <see cref="HasSectionNavigation"/>.
        /// </summary>
        private bool _hasSectionNavigation;

        /// <summary>
        /// Backing field of <see cref="IsLoading"/>.
        /// </summary>
        private bool _isLoading;

        /// <summary>
        /// Backing field of <see cref="IsFullyLoaded"/>.
        /// </summary>
        private bool _isFullyLoaded;

        /// <summary>
        /// Task of the running or last incremental load, shared with callers that request the same load again.
        /// </summary>
        private Task _incrementalLoadTask = Task.CompletedTask;


        // Initialization

        /// <summary>
        /// Creates the module page model with a callback used to refresh save actions.
        /// </summary>
        /// <param name="valueChanged">Callback invoked after any editor changed its draft.</param>
        /// <param name="sectionSelected">Optional callback invoked when the user chooses a section.</param>
        public ModuleSettingsPageViewModel(
            Action valueChanged,
            Action<ModuleSettingsSectionViewModel>? sectionSelected = null)
        {
            _valueChanged = valueChanged;
            _sectionSelected = sectionSelected ?? (static _ => { });
        }


        // Page state

        /// <summary>
        /// Gets render-ready module sections.
        /// </summary>
        public ObservableCollection<ModuleSettingsSectionViewModel> Sections { get; } = new();

        /// <summary>
        /// Gets whether at least one displayable setting exists.
        /// </summary>
        public bool HasSettings
        {
            get => _hasSettings;
            private set => SetProperty(ref _hasSettings, value);
        }

        /// <summary>
        /// Gets whether more than one currently visible section requires navigation.
        /// </summary>
        public bool HasSectionNavigation
        {
            get => _hasSectionNavigation;
            private set => SetProperty(ref _hasSectionNavigation, value);
        }

        /// <summary>
        /// Gets whether editor rows are currently being materialized between UI frames.
        /// </summary>
        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        /// <summary>
        /// Gets whether every editor row for the current draft has been materialized.
        /// </summary>
        public bool IsFullyLoaded
        {
            get => _isFullyLoaded;
            private set => SetProperty(ref _isFullyLoaded, value);
        }


        // Loading

        /// <summary>
        /// Loads one module draft and creates stable editor view models for all dependency states.
        /// </summary>
        /// <param name="moduleDraft">Draft whose settings are shown.</param>
        /// <param name="engineInstalledText">Localized text shown when an engine is installed.</param>
        /// <param name="engineNotInstalledText">Localized text shown when an engine is not installed.</param>
        public void Load(
            ModuleSettingsDraft moduleDraft,
            string engineInstalledText,
            string engineNotInstalledText)
        {
            // The same draft is already shown, so only its values are refreshed.
            if (ReferenceEquals(_moduleDraft, moduleDraft))
            {
                RefreshFromDraft();
                return;
            }

            // Build every section and row, including dependency-hidden ones, so visibility changes never rebuild controls.
            _moduleDraft = moduleDraft;
            Sections.Clear();

            var sections = SettingsPresentationBuilder.BuildModuleSections(
                moduleDraft,
                includeDependencyHiddenSettings: true);
            foreach (var section in sections)
            {
                var items = section.Settings.Select(draft => new ModuleSettingItemViewModel(
                    draft,
                    engineInstalledText,
                    engineNotInstalledText,
                    OnItemValueChanged,
                    _valueChanged));
                Sections.Add(new ModuleSettingsSectionViewModel(
                    section,
                    items,
                    ResolveNavigationTitle(moduleDraft.Module, section),
                    OnSectionSelected));
            }

            HasSettings = Sections.Count > 0;
            RefreshVisibility();
            IsLoading = false;
            IsFullyLoaded = true;
        }

        /// <summary>
        /// Materializes one module page incrementally so large manifests never monopolize the UI thread.
        /// </summary>
        public Task LoadIncrementallyAsync(
            ModuleSettingsDraft moduleDraft,
            string engineInstalledText,
            string engineNotInstalledText,
            CancellationToken cancellationToken)
        {
            // Reuse a finished or running load of the same draft instead of starting another one.
            if (ReferenceEquals(_moduleDraft, moduleDraft) && IsFullyLoaded)
            {
                return Task.CompletedTask;
            }

            if (ReferenceEquals(_moduleDraft, moduleDraft) && IsLoading)
            {
                return _incrementalLoadTask;
            }

            _incrementalLoadTask = LoadIncrementallyCoreAsync(
                moduleDraft,
                engineInstalledText,
                engineNotInstalledText,
                cancellationToken);
            return _incrementalLoadTask;
        }

        /// <summary>
        /// Adds sections and rows in manifest order while yielding after every rendered setting.
        /// </summary>
        private async Task LoadIncrementallyCoreAsync(
            ModuleSettingsDraft moduleDraft,
            string engineInstalledText,
            string engineNotInstalledText,
            CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sections = await Task.Run(() => SettingsPresentationBuilder.BuildModuleSections(
                    moduleDraft, includeDependencyHiddenSettings: true), cancellationToken);
                BeginReconcile(moduleDraft, sections, engineInstalledText, engineNotInstalledText);
                while (HasRenderSteps || HasUnrevealedRows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RenderNext();
                    await Task.Delay(1, cancellationToken);
                }
            }
            finally { IsLoading = false; }
        }


        // Incremental reconciliation

        /// <summary>
        /// Retains the cursor of a partially reconciled schema across priority changes and navigation.
        /// </summary>
        private IEnumerator<bool>? _renderSteps;

        /// <summary>
        /// Retains prepared editors whose first visible layout still needs a separate UI turn.
        /// </summary>
        private readonly Queue<ModuleSettingItemViewModel> _unrevealed = new();

        /// <summary>
        /// Controls first-time row exposure for the currently selected module only.
        /// </summary>
        private bool _revealRows;

        /// <summary>
        /// Gets the editor touched by the last renderer step, or null for a section-only operation.
        /// </summary>
        internal string? LastRenderedKey { get; private set; }

        /// <summary>
        /// Gets whether the last step changed row positions or section dimensions.
        /// </summary>
        internal bool LastStepChangedLayout { get; private set; }

        /// <summary>
        /// Gets whether previously prepared rows still need their first visible layout.
        /// </summary>
        internal bool HasUnrevealedRows => _unrevealed.Count > 0;

        /// <summary>
        /// Gets whether a schema still has editor operations waiting to be rendered.
        /// </summary>
        internal bool HasRenderSteps => _renderSteps != null;

        /// <summary>
        /// Starts a schema diff without clearing existing sections or creating all native templates synchronously.
        /// </summary>
        internal void BeginReconcile(ModuleSettingsDraft draft,
            IReadOnlyList<ModuleSettingsSectionPresentation> sections, string installedText, string missingText)
        {
            _renderSteps?.Dispose();
            var keys = sections.SelectMany(section => section.Settings).Select(setting => setting.Setting.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var pending = _unrevealed.Where(item => keys.Contains(item.Draft.Setting.Key)).ToArray();
            _unrevealed.Clear();
            foreach (var item in pending) _unrevealed.Enqueue(item);
            _moduleDraft = draft;
            IsLoading = true;
            IsFullyLoaded = false;
            _renderSteps = ReconcileSteps(draft, sections, installedText, missingText).GetEnumerator();
        }

        /// <summary>
        /// Performs at most one editor operation and retains the remaining work for the next UI turn.
        /// </summary>
        internal void RenderNext(bool reveal = true)
        {
            LastRenderedKey = null;
            LastStepChangedLayout = false;
            _revealRows = reveal;
            if (reveal && _unrevealed.TryDequeue(out var prepared))
            {
                if (Sections.Any(section => section.Settings.Contains(prepared)))
                {
                    prepared.SetPresented(true);
                    LastStepChangedLayout = true;
                    LastRenderedKey = prepared.Draft.Setting.Key;
                }
                return;
            }
            if (_renderSteps == null) return;
            LastStepChangedLayout = true;
            if (_renderSteps.MoveNext()) return;
            _renderSteps.Dispose();
            _renderSteps = null;
            IsLoading = false;
            IsFullyLoaded = true;
            RefreshVisibility();
        }

        /// <summary>
        /// Diffs validated definitions by setting key and section identity, retaining compatible editor instances.
        /// </summary>
        private IEnumerable<bool> ReconcileSteps(ModuleSettingsDraft draft,
            IReadOnlyList<ModuleSettingsSectionPresentation> definitions, string installedText, string missingText)
        {
            var existing = Sections.SelectMany(section => section.Settings)
                .ToDictionary(item => item.Draft.Setting.Key, StringComparer.OrdinalIgnoreCase);
            var retained = new HashSet<ModuleSettingsSectionViewModel>();
            for (var sectionIndex = 0; sectionIndex < definitions.Count; sectionIndex++)
            {
                var definition = definitions[sectionIndex];
                var key = SectionKey(definition.Kind, definition.Settings.FirstOrDefault()?.Setting.Category);
                var section = Sections.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                if (section == null)
                {
                    section = new(definition, [], ResolveNavigationTitle(draft.Module, definition), OnSectionSelected);
                    Sections.Insert(Math.Min(sectionIndex, Sections.Count), section);
                }
                else
                {
                    section.Rebind(definition, ResolveNavigationTitle(draft.Module, definition));
                    var oldIndex = Sections.IndexOf(section);
                    if (oldIndex != sectionIndex) Sections.Move(oldIndex, sectionIndex);
                }
                retained.Add(section);
                yield return true;

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var itemIndex = 0; itemIndex < definition.Settings.Count; itemIndex++)
                {
                    var setting = draft.GetSetting(definition.Settings[itemIndex].Setting.Key);
                    keys.Add(setting.Setting.Key);
                    if (existing.TryGetValue(setting.Setting.Key, out var item) &&
                        item.Draft.Setting.NormalizedType == setting.Setting.NormalizedType)
                        item.Rebind(setting);
                    else
                    {
                        item = new(setting, installedText, missingText, OnItemValueChanged, _valueChanged);
                        item.SetPresented(_revealRows);
                        if (!_revealRows) _unrevealed.Enqueue(item);
                    }

                    // Moving a row changes only its section; compatible editors keep their draft and pending edits.
                    var source = Sections.FirstOrDefault(candidate => !ReferenceEquals(candidate, section) &&
                        candidate.Settings.Contains(item));
                    source?.Settings.Remove(item);
                    var previousIndex = section.Settings.IndexOf(item);
                    if (previousIndex < 0)
                    {
                        if (itemIndex < section.Settings.Count && section.Settings[itemIndex].Draft.Setting.Key == setting.Setting.Key)
                            section.Settings[itemIndex] = item;
                        else section.Settings.Insert(itemIndex, item);
                    }
                    else if (previousIndex != itemIndex) section.Settings.Move(previousIndex, itemIndex);
                    LastRenderedKey = setting.Setting.Key;
                    section.RefreshVisibility();
                    HasSettings = true;
                    RefreshNavigationState();
                    yield return true;
                }

                // Definitions are complete before removals begin, so partial rendering never means an empty schema.
                for (var index = section.Settings.Count - 1; index >= 0; index--)
                    if (!keys.Contains(section.Settings[index].Draft.Setting.Key))
                    {
                        section.Settings.RemoveAt(index);
                        yield return true;
                    }
                section.RefreshVisibility();
            }
            for (var index = Sections.Count - 1; index >= 0; index--)
                if (!retained.Contains(Sections[index]))
                {
                    Sections.RemoveAt(index);
                    yield return true;
                }
            HasSettings = Sections.Any(section => section.Settings.Count > 0);
        }

        /// <summary>
        /// Returns a section identity that survives display-name changes in the module manifest.
        /// </summary>
        private static string SectionKey(ModuleSettingsSectionKind kind, string? category) =>
            kind == ModuleSettingsSectionKind.Uncategorized ? "uncategorized" : "category:" + category;

        /// <summary>
        /// Returns whether a runtime notification already has an editor to update.
        /// </summary>
        internal bool ContainsSetting(string key) => Sections.SelectMany(section => section.Settings)
            .Any(item => string.Equals(item.Draft.Setting.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Applies one prepared runtime value without replacing the row or overwriting pending keyboard input.
        /// </summary>
        internal bool ApplyPreparedValue(PreparedSettingValue value)
        {
            if (_moduleDraft == null) return true;
            var item = Sections.SelectMany(section => section.Settings).FirstOrDefault(candidate =>
                string.Equals(candidate.Draft.Setting.Key, value.Definition.Key, StringComparison.OrdinalIgnoreCase));
            if (item == null) return true;
            if (item.IsEditing) return false;
            if (item.Draft.HasChanges || item.Draft.Setting.NormalizedType != value.Definition.NormalizedType) return true;
            item.Draft.LoadRuntimeValue(value.Value, value.AutomaticValue, value.IsReadOnly);
            item.RefreshFromDraft();
            if (SettingsService.HasDependentSettings(_moduleDraft.Module, item.Draft.Setting)) RefreshVisibility();
            return true;
        }


        // Refreshing from the draft

        /// <summary>
        /// Refreshes existing bound rows after defaults, discard, or runtime loading changes a draft.
        /// </summary>
        public void RefreshFromDraft()
        {
            foreach (var section in Sections)
            {
                foreach (var item in section.Settings)
                {
                    item.RefreshFromDraft();
                }
            }

            RefreshVisibility();
        }

        /// <summary>
        /// Refreshes bound editor rows between UI frames so a large runtime snapshot cannot freeze input.
        /// </summary>
        public async Task RefreshFromDraftIncrementallyAsync(CancellationToken cancellationToken)
        {
            foreach (var section in Sections)
            {
                foreach (var item in section.Settings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    item.RefreshFromDraft();
                    await Task.Delay(1, cancellationToken);
                }
            }

            RefreshVisibility();
        }

        /// <summary>
        /// Refreshes one completed runtime getter without waiting for or rebuilding the remaining editor rows.
        /// </summary>
        /// <param name="settingKey">Manifest key of the setting whose row is refreshed.</param>
        /// <param name="refreshDependencies">True to re-evaluate dependency visibility for all rows afterwards.</param>
        public void RefreshSettingFromDraft(string settingKey, bool refreshDependencies)
        {
            var item = Sections
                .SelectMany(static section => section.Settings)
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Draft.Setting.Key,
                    settingKey,
                    StringComparison.OrdinalIgnoreCase));
            item?.RefreshFromDraft();

            if (refreshDependencies)
            {
                RefreshVisibility();
            }
        }

        /// <summary>
        /// Applies current dependency rules to existing rows without rebuilding XAML controls.
        /// </summary>
        public void RefreshVisibility()
        {
            if (_moduleDraft == null)
            {
                return;
            }

            SettingsService.RefreshModuleDraftVisibility(_moduleDraft);
            foreach (var section in Sections)
            {
                foreach (var item in section.Settings)
                {
                    item.RefreshVisibility();
                }

                section.RefreshVisibility();
            }

            RefreshNavigationState();
        }


        // Section navigation

        /// <summary>
        /// Selects the first visible section when its module page returns to the top.
        /// </summary>
        public void ActivateFirstVisibleSection() =>
            SetActiveSection(Sections.FirstOrDefault(static section => section.IsVisible));

        /// <summary>
        /// Selects a visible section detected from the current scroll position without requesting another scroll.
        /// </summary>
        public void ActivateVisibleSection(ModuleSettingsSectionViewModel section)
        {
            if (section.IsVisible && Sections.Contains(section))
            {
                SetActiveSection(section);
            }
        }


        // Navigation helpers

        /// <summary>
        /// Uses the module name for the unlabelled default group without changing its card title.
        /// </summary>
        private static string ResolveNavigationTitle(
            ModuleConfig module,
            ModuleSettingsSectionPresentation section)
        {
            if (section.Kind != ModuleSettingsSectionKind.Uncategorized &&
                !string.IsNullOrWhiteSpace(section.Title))
            {
                return section.Title;
            }

            return string.IsNullOrWhiteSpace(module.Name) ? module.Id : module.Name;
        }

        /// <summary>
        /// Recomputes navigation visibility and replaces an unavailable active section.
        /// </summary>
        private void RefreshNavigationState()
        {
            var visibleSections = Sections.Where(static section => section.IsVisible).ToList();
            HasSectionNavigation = visibleSections.Count > 1;

            if (_activeSection == null || !visibleSections.Contains(_activeSection))
            {
                SetActiveSection(visibleSections.FirstOrDefault());
            }
        }

        /// <summary>
        /// Marks one section active before asking the settings page to reveal it.
        /// </summary>
        private void OnSectionSelected(ModuleSettingsSectionViewModel section)
        {
            SetActiveSection(section);
            _sectionSelected(section);
        }

        /// <summary>
        /// Applies one active state across the stable section collection.
        /// </summary>
        private void SetActiveSection(ModuleSettingsSectionViewModel? activeSection)
        {
            if (ReferenceEquals(_activeSection, activeSection))
            {
                return;
            }

            _activeSection = activeSection;
            foreach (var section in Sections)
            {
                section.SetActive(ReferenceEquals(section, activeSection));
            }
        }

        /// <summary>
        /// Refreshes dependencies and forwards one editor change to the host page.
        /// </summary>
        private void OnItemValueChanged()
        {
            RefreshVisibility();
            _valueChanged();
        }
    }
}
