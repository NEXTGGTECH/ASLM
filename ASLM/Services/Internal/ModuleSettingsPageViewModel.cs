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
        public ModuleSettingItemViewModel(
            ModuleSettingDraft draft,
            string engineInstalledText,
            string engineNotInstalledText,
            Action valueChanged)
        {
            // The editor kind comes first because the initial display state depends on it.
            Draft = draft;
            _valueChanged = valueChanged;
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
        public ModuleSettingDraft Draft { get; }

        /// <summary>
        /// Gets the editor kind consumed by XAML visibility properties.
        /// </summary>
        public ModuleSettingEditorKind EditorKind { get; }

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
            get => _isVisible;
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
            Kind = section.Kind;
            Title = section.Title;
            Description = section.Description;
            NavigationTitle = navigationTitle;
            _select = select;
            Settings = new ObservableCollection<ModuleSettingItemViewModel>(settings);
            _isVisible = Settings.Any(static item => item.IsVisible);
            SelectCommand = new Command(Select, () => IsVisible);
        }


        // Section content

        /// <summary>
        /// Gets the section layout role.
        /// </summary>
        public ModuleSettingsSectionKind Kind { get; }

        /// <summary>
        /// Gets the optional section title.
        /// </summary>
        public string? Title { get; }

        /// <summary>
        /// Gets the optional section description.
        /// </summary>
        public string? Description { get; }

        /// <summary>
        /// Gets the title displayed in the module section navigation.
        /// </summary>
        public string NavigationTitle { get; }

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
        /// Refreshes the section after item visibility changes.
        /// </summary>
        public void RefreshVisibility()
        {
            // The select command can only run while the section is visible, so its availability follows visibility.
            var wasVisible = IsVisible;
            IsVisible = Settings.Any(static item => item.IsVisible);
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
                    OnItemValueChanged));
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
            // Start from an empty page and flag the load as running until every row is in place.
            _moduleDraft = moduleDraft;
            Sections.Clear();
            IsFullyLoaded = false;
            IsLoading = true;

            try
            {
                var sections = SettingsPresentationBuilder.BuildModuleSections(
                    moduleDraft,
                    includeDependencyHiddenSettings: true);
                HasSettings = sections.Count > 0;

                // Each section is added empty first, then filled row by row so the UI can paint between rows.
                foreach (var section in sections)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sectionViewModel = new ModuleSettingsSectionViewModel(
                        section,
                        [],
                        ResolveNavigationTitle(moduleDraft.Module, section),
                        OnSectionSelected);
                    Sections.Add(sectionViewModel);

                    foreach (var draft in section.Settings)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Navigation is recomputed whenever a new row makes its section appear.
                        var wasVisible = sectionViewModel.IsVisible;
                        sectionViewModel.Settings.Add(new ModuleSettingItemViewModel(
                            draft,
                            engineInstalledText,
                            engineNotInstalledText,
                            OnItemValueChanged));
                        sectionViewModel.RefreshVisibility();
                        if (wasVisible != sectionViewModel.IsVisible)
                        {
                            RefreshNavigationState();
                        }

                        // A short asynchronous boundary lets input, painting, and close events run between heavy XAML rows.
                        await Task.Delay(1, cancellationToken);
                    }
                }

                RefreshVisibility();
                IsFullyLoaded = true;
            }
            finally
            {
                // A cancelled or failed load must not leave the page stuck in the loading state.
                IsLoading = false;
            }
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
