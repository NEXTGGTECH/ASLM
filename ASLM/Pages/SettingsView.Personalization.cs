// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Personalization rendering

        /// <summary>
        /// Renders the personalization category: appearance picker, custom theme list, and color editor.
        /// </summary>
        private void RenderPersonalizationCategory()
        {
            PrepareCategorySurface(showEmptyState: false, showBuiltInSettings: true);
            BuiltInSettingsContainer.ShowCategory(SettingsCategoryKind.Personalization);

            if (!_personalizationControlsInitialized)
            {
                ApplyBuiltInControlState(() =>
                {
                    _languagePicker!.Items.Clear();
                    foreach (var language in AppLocalizationService.SupportedLanguages)
                    {
                        _languagePicker.Items.Add(GetLanguageDisplayName(language.Id));
                    }

                    var selectedLanguage = AppLocalizationService.SupportedLanguages
                        .FirstOrDefault(language =>
                            string.Equals(language.Id, _personalizationDraft.Language, StringComparison.OrdinalIgnoreCase))
                        ?? AppLocalizationService.SupportedLanguages[0];
                    _personalizationDraft.Language = selectedLanguage.Id;
                    _languagePicker.SelectedItem = GetLanguageDisplayName(selectedLanguage.Id);

                    _appearancePicker!.Items.Clear();
                    foreach (var appearance in AppearanceOptions)
                    {
                        _appearancePicker.Items.Add(GetAppearanceDisplayName(appearance));
                    }

                    _appearancePicker.SelectedItem = GetAppearanceDisplayName(_personalizationDraft.Appearance);
                });

                _customThemeSection!.IsVisible = string.Equals(_personalizationDraft.Appearance, "Custom", StringComparison.Ordinal);
                RebuildCustomThemeSection();
                _themeEditorSection!.IsVisible = false;
                if (string.Equals(_personalizationDraft.Appearance, "Custom", StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(_personalizationDraft.CustomThemeId))
                {
                    var selectedTheme = _customThemesStore.FindById(_personalizationDraft.CustomThemeId);
                    if (selectedTheme != null)
                    {
                        ApplyCustomThemeSelection(selectedTheme.Id);
                    }
                }

                _personalizationControlsInitialized = true;
            }

            if (HasUnsavedPersonalizationChanges())
            {
                _themeService.ApplyPersonalization(_personalizationDraft, _editingThemeDraft);
            }

            RefreshFooterChromeFromResources();
        }

        /// <summary>
        /// Rebuilds the custom theme picker when the Custom appearance mode is active.
        /// </summary>
        private void RebuildCustomThemeSection()
        {
            if (_customThemeSection == null || _customThemePicker == null)
            {
                return;
            }

            var themes = _customThemesStore.Root.Themes;
            BuiltInSettingsContainer.ExportThemeAction.IsEnabled = themes.Count > 0;
            BuiltInSettingsContainer.DeleteThemeAction.IsEnabled = themes.Count > 0;
            _customThemePicker.IsEnabled = themes.Count > 0;
            _customThemePicker.ItemsSource = themes;

            var selectDescription = themes.Count == 0
                ? L.Get(LocalizationKeys.Settings_Personalization_SelectThemeEmpty)
                : L.Get(LocalizationKeys.Settings_Personalization_SelectThemeActive);
            BuiltInSettingsContainer.SetActiveThemeDescription(selectDescription);

            _suppressCustomThemePickerEvents = true;
            try
            {
                if (themes.Count > 0)
                {
                    // Match the picker to the persisted draft without assigning an implicit theme id.
                    var selected = themes.FirstOrDefault(t =>
                        string.Equals(t.Id, _personalizationDraft.CustomThemeId, StringComparison.OrdinalIgnoreCase));
                    _customThemePicker.SelectedItem = selected;
                }
                else
                {
                    _customThemePicker.SelectedItem = null;
                }
            }
            finally
            {
                _suppressCustomThemePickerEvents = false;
            }
        }

        /// <summary>
        /// Loads the selected custom theme into the editor when the theme picker changes.
        /// </summary>
        private void OnCustomThemePickerSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressCustomThemePickerEvents || _customThemePicker == null)
            {
                return;
            }

            if (_customThemePicker.SelectedItem is not CustomTheme t)
            {
                return;
            }

            ApplyCustomThemeSelection(t.Id);
        }

        /// <summary>
        /// Loads the selected custom theme into the editor and updates the preview.
        /// </summary>
        private void ApplyCustomThemeSelection(string? themeId)
        {
            if (string.IsNullOrWhiteSpace(themeId) || _themeEditorSection == null)
            {
                return;
            }

            _personalizationDraft.CustomThemeId = themeId;
            var theme = _customThemesStore.FindById(themeId);
            if (theme == null)
            {
                return;
            }

            _editingThemeDraft = CloneCustomTheme(theme);

            BuildThemeColorEditor();
            _themeEditorSection.IsVisible = true;
            _themeService.PreviewCustomTheme(_editingThemeDraft);
            RefreshFooterChromeFromResources();
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Deletes the theme currently selected in the custom theme picker.
        /// </summary>
        private async void OnDeleteCurrentCustomThemeClicked(object? sender, EventArgs e)
        {
            if (_customThemePicker?.SelectedItem is not CustomTheme t)
            {
                return;
            }

            await OnDeleteThemeClickedAsync(t.Id);
        }

        /// <summary>
        /// Builds bindable color-key rows for the stable XAML editor columns.
        /// </summary>
        private void BuildThemeColorEditor()
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            BuiltInSettingsContainer.ThemeColorsTitle.Text = L.Get(
                LocalizationKeys.Settings_ThemeEditor_ColorsFormat,
                _editingThemeDraft.Name);

            var basePicker = BuiltInSettingsContainer.BaseAppearanceInput;
            var baseDarkLabel = GetAppearanceDisplayName("Dark");
            var baseLightLabel = GetAppearanceDisplayName("Light");
            ApplyBuiltInControlState(() =>
            {
                basePicker.Items.Clear();
                basePicker.Items.Add(baseDarkLabel);
                basePicker.Items.Add(baseLightLabel);
                basePicker.SelectedItem = string.Equals(_editingThemeDraft.BaseAppearance, "light", StringComparison.OrdinalIgnoreCase)
                    ? baseLightLabel
                    : baseDarkLabel;
            });

            var keys = ThemePaletteResolver.AllKeys.ToList();
            var mid = (keys.Count + 1) / 2;
            var items = keys.Select(CreateThemeColorItem).ToList();
            BuiltInSettingsContainer.ThemeColorsLeft = items.Take(mid).ToList();
            BuiltInSettingsContainer.ThemeColorsRight = items.Skip(mid).ToList();
        }

        /// <summary>
        /// Updates the selected custom theme base appearance and previews it immediately.
        /// </summary>
        private void OnBaseAppearancePickerChanged(object? sender, EventArgs e)
        {
            if (IsApplyingBuiltInControlState || _editingThemeDraft == null)
            {
                return;
            }

            _editingThemeDraft.BaseAppearance = string.Equals(
                BuiltInSettingsContainer.BaseAppearanceInput.SelectedItem as string,
                GetAppearanceDisplayName("Light"),
                StringComparison.Ordinal)
                ? "light"
                : "dark";
            BuildThemeColorEditor();
            PreviewEditingTheme();
        }

        /// <summary>
        /// Creates one bindable palette row backed by the current detached theme draft.
        /// </summary>
        private ThemeColorItemViewModel CreateThemeColorItem(string key)
        {
            _editingThemeDraft!.Colors.TryGetValue(key, out var existingHex);
            return new ThemeColorItemViewModel(
                key,
                existingHex,
                ResolveThemeBaseColor(key),
                L.Get(LocalizationKeys.Settings_ThemeEditor_Pick),
                L.Get(LocalizationKeys.Settings_ThemeEditor_Clear),
                PickThemeColorAsync,
                ClearThemeColor);
        }

        /// <summary>
        /// Opens the color picker for one bindable palette item and updates the theme draft.
        /// </summary>
        private async Task PickThemeColorAsync(ThemeColorItemViewModel item)
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            _editingThemeDraft.Colors.TryGetValue(item.Key, out var existingHex);
            var picked = await ThemeColorPickerView.PickAsync(ResolveThemeEditorColor(item.Key, existingHex));
            if (picked is not { } chosen)
            {
                return;
            }

            var hex = ThemePaletteResolver.ToHex(chosen);
            _editingThemeDraft.Colors[item.Key] = hex;
            item.SetHex(hex);
            PreviewEditingTheme();
        }

        /// <summary>
        /// Removes one palette override and restores its inherited preview color.
        /// </summary>
        private void ClearThemeColor(ThemeColorItemViewModel item)
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            _editingThemeDraft.Colors.Remove(item.Key);
            item.SetHex(null);
            PreviewEditingTheme();
        }

        /// <summary>
        /// Applies the current custom-theme draft and refreshes theme-sensitive selector chrome.
        /// </summary>
        private void PreviewEditingTheme()
        {
            if (_editingThemeDraft == null)
            {
                return;
            }

            _themeService.PreviewCustomTheme(_editingThemeDraft);
            RefreshFooterChromeFromResources();
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Resolves the initial color shown in the theme editor for a palette key.
        /// </summary>
        private Color ResolveThemeEditorColor(string key, string? existingHex)
        {
            if (!string.IsNullOrWhiteSpace(existingHex) && ThemePaletteResolver.TryParseHex(existingHex, out var parsed))
            {
                return parsed;
            }

            return ResolveThemeBaseColor(key);
        }

        /// <summary>
        /// Resolves one inherited palette color from the editing theme's selected built-in base.
        /// </summary>
        private Color ResolveThemeBaseColor(string key)
        {
            var palette = string.Equals(_editingThemeDraft?.BaseAppearance, "light", StringComparison.OrdinalIgnoreCase)
                ? ThemePaletteResolver.BuildLightPalette()
                : ThemePaletteResolver.BuildDarkPalette();
            return palette.TryGetValue(key, out var color) ? color : Colors.Gray;
        }

        /// <summary>
        /// Updates appearance draft state and live theme preview when the appearance picker changes.
        /// </summary>
        private void OnAppearancePickerChanged(object? sender, EventArgs e)
        {
            if (IsApplyingBuiltInControlState || _appearancePicker == null)
            {
                return;
            }

            var selectedDisplay = _appearancePicker.SelectedItem as string ?? GetAppearanceDisplayName("Dark");
            var selected = ResolveAppearanceFromDisplayName(selectedDisplay);
            _personalizationDraft.Appearance = AppPersonalizationConfig.NormalizeAppearance(selected);

            var isCustom = string.Equals(selected, "Custom", StringComparison.OrdinalIgnoreCase);
            if (_customThemeSection != null)
            {
                _customThemeSection.IsVisible = isCustom;
                if (isCustom)
                {
                    RebuildCustomThemeSection();
                    if (_customThemesStore.Root.Themes.Count > 0)
                    {
                        ApplyCustomThemeSelection(_personalizationDraft.CustomThemeId);
                    }
                    else if (_themeEditorSection != null)
                    {
                        _themeEditorSection.IsVisible = false;
                        _editingThemeDraft = null;
                    }
                }
            }

            if (_themeEditorSection != null && !isCustom)
            {
                _themeEditorSection.IsVisible = false;
            }

            QueueActionButtonUpdate();

            if (!string.Equals(selected, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                _editingThemeDraft = null;
            }

            _themeService.ApplyPersonalization(_personalizationDraft, _editingThemeDraft);
            RefreshFooterChromeFromResources();
        }

        /// <summary>
        /// Updates the language draft when the language picker selection changes.
        /// </summary>
        private void OnLanguagePickerChanged(object? sender, EventArgs e)
        {
            if (IsApplyingBuiltInControlState || _languagePicker == null)
            {
                return;
            }

            var selectedDisplayName = _languagePicker.SelectedItem as string;
            var language = AppLocalizationService.SupportedLanguages
                .FirstOrDefault(option =>
                    string.Equals(GetLanguageDisplayName(option.Id), selectedDisplayName, StringComparison.Ordinal))
                ?? AppLocalizationService.SupportedLanguages[0];
            _personalizationDraft.Language = language.Id;
            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Prompts for a new custom theme name, creates it, and selects it in the editor.
        /// </summary>
        private async void OnCreateThemeClicked(object? sender, EventArgs e)
        {
            var name = await PromptAsync(
                L.Get(LocalizationKeys.Settings_ThemeNew_PromptTitle),
                L.Get(LocalizationKeys.Settings_ThemeNew_PromptMessage),
                L.Get(LocalizationKeys.Settings_ThemeNew_DefaultName));
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null)
            {
                return;
            }

            var inheritDark = L.Get(LocalizationKeys.Settings_Personalization_InheritDark);
            var inheritLight = L.Get(LocalizationKeys.Settings_Personalization_InheritLight);
            var inheritChoice = await host.DisplayActionSheetAsync(
                L.Get(LocalizationKeys.Settings_Personalization_InheritTitle),
                L.Get(LocalizationKeys.Common_Cancel),
                null,
                inheritDark,
                inheritLight);

            if (string.IsNullOrEmpty(inheritChoice) ||
                string.Equals(inheritChoice, L.Get(LocalizationKeys.Common_Cancel), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var baseAppearance = string.Equals(inheritChoice, inheritLight, StringComparison.OrdinalIgnoreCase)
                ? "light"
                : "dark";

            var newTheme = _customThemesStore.CreateTheme(name.Trim(), baseAppearance);
            ThemePaletteResolver.PrefillCustomThemeFromBuiltIn(newTheme);
            await _customThemesStore.SaveAsync();

            _personalizationDraft.CustomThemeId = newTheme.Id;
            RebuildCustomThemeSection();
            ApplyCustomThemeSelection(newTheme.Id);
        }

        /// <summary>
        /// Imports a custom theme from a user-selected JSON file.
        /// </summary>
        private async void OnImportThemeClicked(object? sender, EventArgs e)
        {
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null)
            {
                return;
            }

            try
            {
                var pick = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = L.Get(LocalizationKeys.Settings_Personalization_ImportPickerTitle),
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.WinUI] = new[] { ".json" },
                        [DevicePlatform.MacCatalyst] = new[] { "public.json" },
                        [DevicePlatform.macOS] = new[] { "json" },
                        [DevicePlatform.iOS] = new[] { "public.json" },
                        [DevicePlatform.Android] = new[] { "application/json" }
                    })
                });

                if (pick == null)
                {
                    return;
                }

                await using var stream = await pick.OpenReadAsync();
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync();
                if (json.Length > 524_288)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_TooLarge));
                    return;
                }

                var imported = _customThemesStore.DeserializeImportedTheme(json);
                if (imported == null)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_Invalid));
                    return;
                }

                var added = _customThemesStore.ImportThemeCopy(imported);
                await _customThemesStore.SaveAsync();
                _personalizationDraft.CustomThemeId = added.Id;
                RebuildCustomThemeSection();
                ApplyCustomThemeSelection(added.Id);
                QueueActionButtonUpdate();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeImport_Failed, ex.Message));
            }
        }

        /// <summary>
        /// Exports the selected custom theme to a JSON file on disk.
        /// </summary>
        private async void OnExportThemeClicked(object? sender, EventArgs e)
        {
            if (_customThemePicker?.SelectedItem is not CustomTheme t)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeExport_Select));
                return;
            }

            try
            {
                var json = _customThemesStore.SerializeThemeForExport(t);
#if WINDOWS
                var path = await PickExportThemeFilePathAsync(SanitizeThemeFileName(t.Name));
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                await File.WriteAllTextAsync(path, json);
#elif MACCATALYST
                await MacThemeFileExporter.ExportAsync(SanitizeThemeFileName(t.Name), json);
#endif
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_ThemeExport_Failed, ex.Message));
            }
        }

        /// <summary>
        /// Normalizes a theme name into a safe file name for export.
        /// </summary>
        private static string SanitizeThemeFileName(string name)
        {
            var baseName = string.IsNullOrWhiteSpace(name) ? "ASLM_theme" : name.Trim();
            var invalid = global::System.IO.Path.GetInvalidFileNameChars();
            var buffer = new char[baseName.Length];
            for (var i = 0; i < baseName.Length; i++)
            {
                var c = baseName[i];
                buffer[i] = Array.IndexOf(invalid, c) >= 0 ? '_' : c;
            }

            var s = new string(buffer).Trim();
            return string.IsNullOrEmpty(s) ? "ASLM_theme" : s;
        }

        /// <summary>
        /// Confirms deletion, removes a custom theme, and refreshes personalization UI state.
        /// </summary>
        private async Task OnDeleteThemeClickedAsync(string themeId)
        {
            var confirmed = await ShowAlertAsync(
                L.Get(LocalizationKeys.Settings_ThemeDelete_Title),
                L.Get(LocalizationKeys.Settings_ThemeDelete_Message),
                L.Get(LocalizationKeys.Settings_ThemeDelete_Accept),
                L.Get(LocalizationKeys.Common_Cancel));

            if (!confirmed)
            {
                return;
            }

            _customThemesStore.DeleteTheme(themeId);
            await _customThemesStore.SaveAsync();

            if (string.Equals(_personalizationDraft.CustomThemeId, themeId, StringComparison.Ordinal))
            {
                _personalizationDraft.CustomThemeId = _customThemesStore.Root.Themes.FirstOrDefault()?.Id;
                _editingThemeDraft = null;

                if (_themeEditorSection != null)
                {
                    _themeEditorSection.IsVisible = false;
                }
            }

            RebuildCustomThemeSection();

            if (!string.IsNullOrWhiteSpace(_personalizationDraft.CustomThemeId) &&
                _customThemesStore.FindById(_personalizationDraft.CustomThemeId) != null)
            {
                ApplyCustomThemeSelection(_personalizationDraft.CustomThemeId);
            }
            else if (_themeEditorSection != null)
            {
                _themeEditorSection.IsVisible = false;
            }

            QueueActionButtonUpdate();
        }

        /// <summary>
        /// Persists the personalization draft to app data and optionally applies the new theme immediately.
        /// </summary>
        private async Task SavePersonalizationAsync(bool applyImmediately = true)
        {
            var languageChanged = !string.Equals(
                _personalizationBaseline.Language,
                _personalizationDraft.Language,
                StringComparison.OrdinalIgnoreCase);

            // Persist in-editor custom theme color changes before updating app data.
            if (_editingThemeDraft != null)
            {
                var existingTheme = _customThemesStore.FindById(_editingThemeDraft.Id);
                if (existingTheme != null)
                {
                    existingTheme.Name = _editingThemeDraft.Name;
                    existingTheme.BaseAppearance = _editingThemeDraft.BaseAppearance;
                    existingTheme.Colors = new Dictionary<string, string>(
                        _editingThemeDraft.Colors,
                        StringComparer.OrdinalIgnoreCase);
                    await _customThemesStore.SaveAsync();
                }
            }

            // Write appearance, language, and custom theme selection to app data.
            _appData.Data.Personalization.Appearance = _personalizationDraft.Appearance;
            _appData.Data.Personalization.Language = _personalizationDraft.Language;
            _appData.Data.Personalization.CustomThemeId = _personalizationDraft.CustomThemeId;
            _appData.Data.Personalization.Normalize();

            // Refresh personalization baselines used by unsaved-change detection.
            _editSession.Application.AcceptPersonalization();

            if (!applyImmediately)
            {
                return;
            }

            // Apply the saved theme and palette without waiting for restart.
            _themeService.ApplyFromSettings();

            if (languageChanged)
            {
                _localization.ApplyCulture();
            }
        }

        /// <summary>
        /// Shows a simple text-input prompt dialog and returns the user's entry.
        /// </summary>
        private static Task<string?> PromptAsync(string title, string message, string defaultValue) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayPromptAsync(title, message, initialValue: defaultValue)
                : Task.FromResult<string?>(null);

        /// <summary>
        /// Creates a deep copy of a custom theme for editing without modifying the stored version.
        /// </summary>
        private static CustomTheme CloneCustomTheme(CustomTheme source) =>
            new()
            {
                Id = source.Id,
                Name = source.Name,
                BaseAppearance = source.BaseAppearance,
                Colors = new Dictionary<string, string>(source.Colors, StringComparer.OrdinalIgnoreCase)
            };
    }
}
