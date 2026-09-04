// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using ASLM.Models;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Identifies the XAML editor displayed for one module setting.
    /// </summary>
    public enum ModuleSettingEditorKind
    {
        EngineStatus,
        Boolean,
        Managed,
        Choice,
        Numeric,
        Password,
        Text
    }

    /// <summary>
    /// Exposes one detached module setting as typed bindable editor state.
    /// </summary>
    public sealed class ModuleSettingItemViewModel : SettingsBindableObject
    {
        private readonly Action _valueChanged;
        private string _textValue;
        private string? _selectedValue;
        private IReadOnlyList<string> _options;
        private bool _booleanValue;
        private bool _useCustomValue;
        private bool _isVisible;
        private string _customTextValue;

        /// <summary>
        /// Creates typed editor state from one detached setting draft.
        /// </summary>
        public ModuleSettingItemViewModel(
            ModuleSettingDraft draft,
            string engineInstalledText,
            string engineNotInstalledText,
            Action valueChanged)
        {
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

        /// <summary>
        /// Refreshes dependency visibility without recreating the editor view model.
        /// </summary>
        public void RefreshVisibility() => IsVisible = Draft.IsVisible;

        /// <summary>
        /// Copies accepted or reset draft values into existing bound editor properties.
        /// </summary>
        public void RefreshFromDraft()
        {
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
        /// Selects the editor kind that preserves the existing module settings contract.
        /// </summary>
        private static ModuleSettingEditorKind ResolveEditorKind(ModuleSettingDraft draft)
        {
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

    /// <summary>
    /// Exposes one module settings group and its dependency-driven visibility.
    /// </summary>
    public sealed class ModuleSettingsSectionViewModel : SettingsBindableObject
    {
        private readonly Action<ModuleSettingsSectionViewModel> _select;
        private bool _isActive;
        private bool _isVisible;

        /// <summary>
        /// Creates a bindable section from one presentation descriptor.
        /// </summary>
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

        /// <summary>
        /// Refreshes the section after item visibility changes.
        /// </summary>
        public void RefreshVisibility()
        {
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

    /// <summary>
    /// Owns bindable module sections and refreshes dependency visibility in place.
    /// </summary>
    public sealed class ModuleSettingsPageViewModel : SettingsBindableObject
    {
        private readonly Action _valueChanged;
        private readonly Action<ModuleSettingsSectionViewModel> _sectionSelected;
        private ModuleSettingsDraft? _moduleDraft;
        private ModuleSettingsSectionViewModel? _activeSection;
        private bool _hasSettings;
        private bool _hasSectionNavigation;
        private bool _isLoading;
        private bool _isFullyLoaded;
        private Task _incrementalLoadTask = Task.CompletedTask;

        /// <summary>
        /// Creates the module page model with a callback used to refresh save actions.
        /// </summary>
        public ModuleSettingsPageViewModel(
            Action valueChanged,
            Action<ModuleSettingsSectionViewModel>? sectionSelected = null)
        {
            _valueChanged = valueChanged;
            _sectionSelected = sectionSelected ?? (static _ => { });
        }

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

        /// <summary>
        /// Loads one module draft and creates stable editor view models for all dependency states.
        /// </summary>
        public void Load(
            ModuleSettingsDraft moduleDraft,
            string engineInstalledText,
            string engineNotInstalledText)
        {
            if (ReferenceEquals(_moduleDraft, moduleDraft))
            {
                RefreshFromDraft();
                return;
            }

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
                IsLoading = false;
            }
        }

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
