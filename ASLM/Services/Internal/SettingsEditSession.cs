// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Services.Internal
{
    // Editing session

    /// <summary>
    /// Holds application and module setting drafts independently from rendered controls and persisted models.
    /// </summary>
    public sealed class SettingsEditSession
    {
        // Fields

        /// <summary>
        /// Module drafts by session key (see <see cref="GetModuleKey"/>), compared case-insensitively.
        /// </summary>
        private readonly Dictionary<string, ModuleSettingsDraft> _moduleDrafts =
            new(StringComparer.OrdinalIgnoreCase);


        // Draft access

        /// <summary>
        /// Gets the editable application settings and their accepted baselines.
        /// </summary>
        public ApplicationSettingsDraft Application { get; } = new();

        /// <summary>
        /// Gets the module drafts currently registered in this editing session.
        /// </summary>
        public IReadOnlyCollection<ModuleSettingsDraft> Modules => _moduleDrafts.Values;


        // Module draft management

        /// <summary>
        /// Replaces module drafts after discovery while keeping application drafts intact.
        /// </summary>
        public void ReplaceModules(IEnumerable<ModuleConfig> modules)
        {
            _moduleDrafts.Clear();

            // Source paths identify installed module instances even when manifests reuse an id.
            foreach (var module in modules)
            {
                var key = GetModuleKey(module);
                _moduleDrafts[key] = new ModuleSettingsDraft(module);
            }
        }

        /// <summary>
        /// Returns the draft registered for one discovered module.
        /// Throws <see cref="KeyNotFoundException"/> when the module was not registered by <see cref="ReplaceModules"/>.
        /// </summary>
        public ModuleSettingsDraft GetModule(ModuleConfig module)
        {
            var key = GetModuleKey(module);
            if (_moduleDrafts.TryGetValue(key, out var draft))
            {
                return draft;
            }

            throw new KeyNotFoundException($"No settings draft is registered for module '{module.Id}'.");
        }

        /// <summary>
        /// Returns whether any registered module draft differs from its accepted baseline.
        /// </summary>
        public bool HasModuleChanges() => _moduleDrafts.Values.Any(static draft => draft.HasChanges);

        /// <summary>
        /// Restores every module draft to the values accepted when the session was loaded or saved.
        /// </summary>
        public void DiscardModules()
        {
            foreach (var draft in _moduleDrafts.Values)
            {
                draft.DiscardChanges();
                SettingsService.RefreshModuleDraftVisibility(draft);
            }
        }

        /// <summary>
        /// Builds the stable session key used for one installed module instance:
        /// its manifest source path, or the module id when the path is unknown.
        /// </summary>
        private static string GetModuleKey(ModuleConfig module) =>
            string.IsNullOrWhiteSpace(module.SourcePath) ? $"id::{module.Id}" : module.SourcePath;
    }


    // Application settings draft

    /// <summary>
    /// Stores editable built-in settings together with their last accepted values.
    /// </summary>
    public sealed class ApplicationSettingsDraft
    {
        // Editable values

        /// <summary>
        /// Gets or sets the editable display name.
        /// </summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the editable first module port.
        /// </summary>
        public string PortStart { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the local ASLM API server is enabled.
        /// </summary>
        public bool ApiServerEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether ASLM restores the last stable shell page on startup.
        /// </summary>
        public bool RestoreLastPage { get; set; } = true;

        /// <summary>
        /// Gets or sets whether the unfinished home page is disabled.
        /// </summary>
        public bool DisableHomePage { get; set; } = true;

        /// <summary>
        /// Gets or sets the editable console preferences.
        /// </summary>
        public ConsoleBaseline Console { get; set; } = new(true, true, true);

        /// <summary>
        /// Gets or sets the editable update preferences.
        /// </summary>
        public UpdateBaseline Update { get; set; } = new(true, false, "release", "release");

        /// <summary>
        /// Gets or sets whether newly added legal documents are accepted automatically.
        /// </summary>
        public bool LegalAutoAcceptUpdates { get; set; } = true;

        /// <summary>
        /// Gets or sets the editable personalization preferences.
        /// </summary>
        public AppPersonalizationConfig Personalization { get; set; } = new();


        // Accepted baselines

        /// <summary>
        /// Gets or sets the accepted ASLM baseline used by dirty-state checks.
        /// </summary>
        public AslmBaseline AslmBaseline { get; set; } = new(string.Empty, string.Empty, true);

        /// <summary>
        /// Gets or sets the accepted console baseline used by dirty-state checks.
        /// </summary>
        public ConsoleBaseline ConsoleBaseline { get; set; } = new(true, true, true);

        /// <summary>
        /// Gets or sets the accepted update baseline used by dirty-state checks.
        /// </summary>
        public UpdateBaseline UpdateBaseline { get; set; } = new(true, false, "release", "release");

        /// <summary>
        /// Gets or sets the accepted legal baseline used by dirty-state checks.
        /// </summary>
        public bool LegalAutoAcceptBaseline { get; set; } = true;

        /// <summary>
        /// Gets or sets the accepted last-page restoration preference.
        /// </summary>
        public bool RestoreLastPageBaseline { get; set; } = true;

        /// <summary>
        /// Gets or sets the accepted home-page visibility preference.
        /// </summary>
        public bool DisableHomePageBaseline { get; set; } = true;

        /// <summary>
        /// Gets or sets the accepted personalization baseline used by dirty-state checks.
        /// </summary>
        public AppPersonalizationConfig PersonalizationBaseline { get; set; } = new();


        // Dirty-state checks

        /// <summary>
        /// Gets whether the display-name draft differs from its accepted value.
        /// Always false in builds without ASLM accounts.
        /// </summary>
        public bool HasAccountChanges =>
            SunriseService.IsEnabled && !string.Equals(UserName, AslmBaseline.UserName, StringComparison.Ordinal);

        /// <summary>
        /// Gets whether restart-relevant ASLM drafts differ from accepted values.
        /// </summary>
        public bool HasAslmRestartChanges =>
            !string.Equals(PortStart, AslmBaseline.PortStart, StringComparison.Ordinal) ||
            ApiServerEnabled != AslmBaseline.ApiServerEnabled ||
            Console != ConsoleBaseline ||
            Update != UpdateBaseline ||
            LegalAutoAcceptUpdates != LegalAutoAcceptBaseline;

        /// <summary>
        /// Gets whether the non-restart shell startup preference differs from its accepted value.
        /// </summary>
        public bool HasNavigationChanges => RestoreLastPage != RestoreLastPageBaseline ||
            DisableHomePage != DisableHomePageBaseline;

        /// <summary>
        /// Gets whether any setting shown in the ASLM category differs from its accepted value.
        /// </summary>
        public bool HasAslmChanges => HasAslmRestartChanges || HasNavigationChanges;

        /// <summary>
        /// Gets whether persisted personalization selection differs from its accepted value.
        /// </summary>
        public bool HasPersonalizationChanges =>
            !string.Equals(Personalization.Appearance, PersonalizationBaseline.Appearance, StringComparison.Ordinal) ||
            !string.Equals(Personalization.Language, PersonalizationBaseline.Language, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Personalization.CustomThemeId, PersonalizationBaseline.CustomThemeId, StringComparison.Ordinal);


        // Loading, accepting and discarding

        /// <summary>
        /// Loads ASLM drafts and baselines from one persisted snapshot.
        /// </summary>
        /// <param name="snapshot">Persisted values to copy into the drafts.</param>
        /// <param name="legalAutoAcceptUpdates">Persisted legal auto-accept preference; the snapshot lacks it.</param>
        public void LoadAslm(AslmDraftSnapshot snapshot, bool legalAutoAcceptUpdates)
        {
            // Copy the persisted values into the editable drafts.
            UserName = snapshot.UserName;
            PortStart = snapshot.PortStart;
            ApiServerEnabled = snapshot.ApiServerEnabled;
            RestoreLastPage = snapshot.RestoreLastPage;
            DisableHomePage = snapshot.DisableHomePage;
            Console = snapshot.ConsoleBaseline;
            Update = snapshot.UpdateBaseline;
            LegalAutoAcceptUpdates = legalAutoAcceptUpdates;

            // The freshly loaded values become the baseline, so nothing is dirty afterwards.
            AcceptAslm();
        }

        /// <summary>
        /// Loads personalization drafts and baselines without sharing the persisted mutable object.
        /// </summary>
        public void LoadPersonalization(AppPersonalizationConfig personalization)
        {
            Personalization = ClonePersonalization(personalization);
            PersonalizationBaseline = ClonePersonalization(personalization);
        }

        /// <summary>
        /// Accepts current ASLM values as the new persisted baseline after a successful save.
        /// </summary>
        public void AcceptAslm()
        {
            AslmBaseline = new AslmBaseline(UserName, PortStart, ApiServerEnabled);
            ConsoleBaseline = Console;
            UpdateBaseline = Update;
            LegalAutoAcceptBaseline = LegalAutoAcceptUpdates;
            RestoreLastPageBaseline = RestoreLastPage;
            DisableHomePageBaseline = DisableHomePage;
        }

        /// <summary>
        /// Accepts current personalization values as the new persisted baseline after a successful save.
        /// </summary>
        public void AcceptPersonalization() =>
            PersonalizationBaseline = ClonePersonalization(Personalization);

        /// <summary>
        /// Restores ASLM drafts from their last accepted baseline.
        /// </summary>
        public void DiscardAslm()
        {
            UserName = AslmBaseline.UserName;
            PortStart = AslmBaseline.PortStart;
            ApiServerEnabled = AslmBaseline.ApiServerEnabled;
            Console = ConsoleBaseline;
            Update = UpdateBaseline;
            LegalAutoAcceptUpdates = LegalAutoAcceptBaseline;
            RestoreLastPage = RestoreLastPageBaseline;
            DisableHomePage = DisableHomePageBaseline;
        }

        /// <summary>
        /// Restores personalization drafts from their last accepted baseline.
        /// </summary>
        public void DiscardPersonalization() =>
            Personalization = ClonePersonalization(PersonalizationBaseline);


        // Helpers

        /// <summary>
        /// Creates a detached personalization copy so draft edits cannot mutate persisted data.
        /// Only appearance, language and custom theme id are copied.
        /// </summary>
        private static AppPersonalizationConfig ClonePersonalization(AppPersonalizationConfig source) =>
            new()
            {
                Appearance = source.Appearance,
                Language = source.Language,
                CustomThemeId = source.CustomThemeId
            };
    }


    // Module settings draft

    /// <summary>
    /// Stores editable settings for one module without mutating its manifest model before commit.
    /// </summary>
    public sealed class ModuleSettingsDraft
    {
        // Fields

        /// <summary>
        /// Setting drafts by manifest key, compared case-insensitively.
        /// </summary>
        private readonly Dictionary<string, ModuleSettingDraft> _settingsByKey;


        // Initialization

        /// <summary>
        /// Creates detached setting drafts from one discovered module manifest.
        /// </summary>
        /// <param name="module">Module whose settings are copied into drafts.</param>
        public ModuleSettingsDraft(ModuleConfig module)
        {
            Module = module;
            Settings = module.Settings
                .Select(setting => new ModuleSettingDraft(setting))
                .ToList();
            _settingsByKey = Settings.ToDictionary(
                static draft => draft.Setting.Key,
                StringComparer.OrdinalIgnoreCase);
        }


        // Draft state

        /// <summary>
        /// Gets the module whose settings will receive committed values.
        /// </summary>
        public ModuleConfig Module { get; }

        /// <summary>
        /// Gets detached drafts in manifest declaration order.
        /// </summary>
        public IReadOnlyList<ModuleSettingDraft> Settings { get; }

        /// <summary>
        /// Gets whether any editable setting differs from its accepted baseline.
        /// </summary>
        public bool HasChanges => Settings.Any(static draft => draft.HasChanges);


        // Draft operations

        /// <summary>
        /// Returns the setting draft with the requested manifest key.
        /// Throws <see cref="KeyNotFoundException"/> when the module declares no such setting.
        /// </summary>
        public ModuleSettingDraft GetSetting(string key)
        {
            if (_settingsByKey.TryGetValue(key, out var draft))
            {
                return draft;
            }

            throw new KeyNotFoundException($"Module '{Module.Id}' has no setting named '{key}'.");
        }

        /// <summary>
        /// Creates the effective value map consumed by dependency visibility rules.
        /// </summary>
        public IReadOnlyDictionary<string, object?> BuildEffectiveValuesByKey() =>
            Settings.ToDictionary(
                static draft => draft.Setting.Key,
                static draft => draft.EffectiveValue,
                StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Commits changed detached values to the manifest model immediately before persistence.
        /// </summary>
        public void ApplyToModule()
        {
            foreach (var draft in Settings.Where(static draft => draft.HasChanges))
            {
                draft.ApplyToSetting();
            }
        }

        /// <summary>
        /// Accepts current effective values after persistence or a runtime reload succeeds.
        /// </summary>
        public void AcceptChanges()
        {
            foreach (var draft in Settings)
            {
                draft.AcceptChanges();
            }
        }

        /// <summary>
        /// Restores every setting to its accepted value without invoking runtime getters.
        /// </summary>
        public void DiscardChanges()
        {
            foreach (var draft in Settings)
            {
                draft.DiscardChanges();
            }
        }
    }


    // Single setting draft

    /// <summary>
    /// Represents one detached module setting value, host value, and accepted baseline.
    /// </summary>
    public sealed class ModuleSettingDraft
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="Baseline"/>; rebuilt whenever changes are accepted or discarded.
        /// </summary>
        private SettingBaseline _baseline = new(string.Empty, false);

        /// <summary>
        /// Detached user value captured by the last <see cref="AcceptChanges"/>.
        /// </summary>
        private object? _acceptedValue;

        /// <summary>
        /// Host-resolved value captured by the last <see cref="AcceptChanges"/>.
        /// </summary>
        private object? _acceptedAutomaticValue;

        /// <summary>
        /// Custom-value flag captured by the last <see cref="AcceptChanges"/>.
        /// </summary>
        private bool _acceptedUseCustomValue;

        /// <summary>
        /// Read-only flag captured by the last <see cref="AcceptChanges"/>.
        /// </summary>
        private bool _acceptedIsReadOnly;


        // Initialization

        /// <summary>
        /// Creates a detached draft from the persisted setting value.
        /// </summary>
        /// <param name="setting">Setting whose current value (or default) seeds the draft.</param>
        public ModuleSettingDraft(ModuleSetting setting)
        {
            // The persisted value, or the manifest default when none is stored, is the initial accepted state.
            Setting = setting;
            Value = setting.NormalizeUserValue(setting.Value ?? setting.Default);
            UseCustomValue = setting.UseCustomValue;
            AcceptChanges();
        }


        // Draft state

        /// <summary>
        /// Gets the setting definition and eventual commit target.
        /// </summary>
        public ModuleSetting Setting { get; }

        /// <summary>
        /// Gets or sets the detached user value.
        /// </summary>
        public object? Value { get; set; }

        /// <summary>
        /// Gets or sets the current value resolved by ASLM for a managed setting.
        /// </summary>
        public object? AutomaticValue { get; set; }

        /// <summary>
        /// Gets or sets whether a managed setting uses its detached user value.
        /// </summary>
        public bool UseCustomValue { get; set; }

        /// <summary>
        /// Gets or sets whether the value is informational and must never be committed by the editor.
        /// </summary>
        public bool IsReadOnly { get; set; }

        /// <summary>
        /// Gets whether dependency rules currently allow the setting to be rendered.
        /// </summary>
        public bool IsVisible { get; private set; } = true;

        /// <summary>
        /// Gets the value ASLM will apply for the current managed/custom state.
        /// </summary>
        public object? EffectiveValue =>
            Setting.IsAutomaticallyManaged && !UseCustomValue
                ? AutomaticValue ?? Value
                : Value;

        /// <summary>
        /// Gets the accepted display value and custom-mode state.
        /// </summary>
        public SettingBaseline Baseline => _baseline;

        /// <summary>
        /// Gets whether the editable value differs from its accepted baseline.
        /// </summary>
        public bool HasChanges => !IsReadOnly && IsDifferentFromBaseline(Value, UseCustomValue);


        // Draft operations

        /// <summary>
        /// Loads one runtime value and accepts it as the current baseline.
        /// </summary>
        /// <param name="loadedValue">Value read from the running module.</param>
        /// <param name="automaticValue">Value ASLM resolves for a managed setting, or null when there is none.</param>
        /// <param name="isReadOnly">Whether the value is informational and cannot be edited.</param>
        public void LoadRuntimeValue(object? loadedValue, object? automaticValue, bool isReadOnly)
        {
            AutomaticValue = automaticValue;
            IsReadOnly = isReadOnly;

            // Host-managed automatic mode keeps the last persisted custom value for later reuse.
            if (isReadOnly || !Setting.IsAutomaticallyManaged || UseCustomValue)
            {
                Value = Setting.NormalizeUserValue(loadedValue);
            }

            AcceptChanges();
        }

        /// <summary>
        /// Checks a control value against the baseline without mutating the draft first.
        /// Always false for read-only settings.
        /// </summary>
        /// <param name="rawValue">Value as it currently stands in the control.</param>
        /// <param name="useCustomValue">Custom-mode state as it currently stands in the control.</param>
        public bool WouldChange(object? rawValue, bool useCustomValue)
        {
            var normalized = Setting.NormalizeUserValue(rawValue);
            return !IsReadOnly && IsDifferentFromBaseline(normalized, useCustomValue);
        }

        /// <summary>
        /// Restores the manifest default and returns managed settings to host control.
        /// </summary>
        public void ResetToDefault()
        {
            if (IsReadOnly)
            {
                return;
            }

            if (Setting.IsAutomaticallyManaged)
            {
                UseCustomValue = false;
            }

            Value = Setting.NormalizeUserValue(Setting.Default);
        }

        /// <summary>
        /// Writes the detached value into its manifest model during commit.
        /// </summary>
        public void ApplyToSetting()
        {
            if (IsReadOnly)
            {
                return;
            }

            Setting.UseCustomValue = UseCustomValue;
            Setting.Value = Setting.NormalizeUserValue(Value);
        }

        /// <summary>
        /// Stores the visibility resolved from the complete module draft snapshot.
        /// </summary>
        public void SetVisibility(bool isVisible) => IsVisible = isVisible;

        /// <summary>
        /// Accepts the current effective value as the new persisted baseline.
        /// </summary>
        public void AcceptChanges()
        {
            _acceptedValue = Value;
            _acceptedAutomaticValue = AutomaticValue;
            _acceptedUseCustomValue = UseCustomValue;
            _acceptedIsReadOnly = IsReadOnly;
            _baseline = CreateBaseline();
        }

        /// <summary>
        /// Restores the detached value and host state captured by <see cref="AcceptChanges"/>.
        /// </summary>
        public void DiscardChanges()
        {
            Value = _acceptedValue;
            AutomaticValue = _acceptedAutomaticValue;
            UseCustomValue = _acceptedUseCustomValue;
            IsReadOnly = _acceptedIsReadOnly;
            _baseline = CreateBaseline();
        }


        // Baseline comparison

        /// <summary>
        /// Compares one candidate value and custom-mode state with the accepted baseline.
        /// </summary>
        private bool IsDifferentFromBaseline(object? value, bool useCustomValue)
        {
            // Compare display text rather than raw objects, so equal values of different runtime types match.
            var effectiveValue = Setting.IsAutomaticallyManaged && !useCustomValue
                ? AutomaticValue ?? value
                : value;
            var displayValue = Setting.FormatValueForDisplay(effectiveValue);

            return _baseline.UseCustomValue != useCustomValue ||
                   !string.Equals(_baseline.DisplayValue, displayValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// Builds a normalized baseline from the current effective value.
        /// </summary>
        private SettingBaseline CreateBaseline() =>
            new(Setting.FormatValueForDisplay(EffectiveValue), UseCustomValue);
    }
}
