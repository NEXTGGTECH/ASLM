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
        private bool _isVisible;

        /// <summary>
        /// Creates a bindable section from one presentation descriptor.
        /// </summary>
        public ModuleSettingsSectionViewModel(
            ModuleSettingsSectionPresentation section,
            IEnumerable<ModuleSettingItemViewModel> settings)
        {
            Kind = section.Kind;
            Title = section.Title;
            Description = section.Description;
            Settings = new ObservableCollection<ModuleSettingItemViewModel>(settings);
            _isVisible = Settings.Any(static item => item.IsVisible);
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
        /// Gets setting rows in manifest declaration order.
        /// </summary>
        public ObservableCollection<ModuleSettingItemViewModel> Settings { get; }

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
        public void RefreshVisibility() =>
            IsVisible = Settings.Any(static item => item.IsVisible);
    }

    /// <summary>
    /// Owns bindable module sections and refreshes dependency visibility in place.
    /// </summary>
    public sealed class ModuleSettingsPageViewModel : SettingsBindableObject
    {
        private readonly Action _valueChanged;
        private ModuleSettingsDraft? _moduleDraft;
        private bool _hasSettings;

        /// <summary>
        /// Creates the module page model with a callback used to refresh save actions.
        /// </summary>
        public ModuleSettingsPageViewModel(Action valueChanged)
        {
            _valueChanged = valueChanged;
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
                Sections.Add(new ModuleSettingsSectionViewModel(section, items));
            }

            HasSettings = Sections.Count > 0;
            RefreshVisibility();
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
