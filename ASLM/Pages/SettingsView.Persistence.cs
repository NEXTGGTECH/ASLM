// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Draft synchronization

        /// <summary>
        /// Synchronizes the visible category controls back into the shared in-memory draft state.
        /// </summary>
        private void SyncDraftValuesFromControls()
        {
            if (_activeCategory == null)
            {
                return;
            }

            if (_activeCategory.Kind == SettingsCategoryKind.Module)
            {
                return;
            }

            if (_activeCategory.Kind == SettingsCategoryKind.Personalization)
            {
                // Personalization drafts are updated in-place by picker/toggle event handlers.
                return;
            }

            SyncAslmDraftValuesFromControls();
            SyncBuiltInDraftValuesFromControls();
        }

        /// <summary>
        /// Copies the visible ASLM input controls into the current draft values.
        /// </summary>
        private void SyncAslmDraftValuesFromControls()
        {
            if (UserProfileSection.IsVisible)
            {
                _userNameDraft = UsernameEntry.Text?.Trim() ?? string.Empty;
            }

            if (PortsSection.IsVisible)
            {
                _portStartDraft = ModulePortEntry.Text?.Trim() ?? string.Empty;
            }
        }

        /// <summary>
        /// Copies visible built-in ASLM controls into the cross-category draft values.
        /// </summary>
        private void SyncBuiltInDraftValuesFromControls()
        {
            // API and console drafts are driven by RefreshAslmApiAndConsoleDraftsFromToggles on user input
            // and by load/default flows so SyncDraftValuesFromControls cannot overwrite them from WinUI timing glitches.
            _updateDraft = GetCurrentUpdateDraft();
        }

        /// <summary>
        /// Prompts the user to discard unsaved changes before leaving the current category.
        /// </summary>
        private async Task<bool> ConfirmDiscardChangesIfNeededAsync()
        {
            SyncDraftValuesFromControls();

            if (_activeCategory == null || !HasAnyUnsavedChanges())
            {
                return true;
            }

            var discardChanges = await ShowAlertAsync(
                L.Get(LocalizationKeys.Settings_DiscardDialog_Title),
                L.Get(LocalizationKeys.Settings_DiscardDialog_Message),
                L.Get(LocalizationKeys.Common_Discard),
                L.Get(LocalizationKeys.Common_Stay));

            if (!discardChanges)
            {
                return false;
            }

            DiscardAllDraftChanges();

            _themeService.ApplyFromSettings();

            return true;
        }

        /// <summary>
        /// Determines whether the currently visible category has unsaved changes.
        /// </summary>
        private bool HasUnsavedChanges()
        {
            if (_activeCategory == null)
            {
                return false;
            }

            return _activeCategory.Kind switch
            {
                SettingsCategoryKind.Aslm => HasUnsavedAslmSettingsChanges(),
                SettingsCategoryKind.Accounts => HasUnsavedAccountChanges(),
                SettingsCategoryKind.Updates => HasUnsavedUpdateChanges(),
                SettingsCategoryKind.Module => HasUnsavedModuleChanges(),
                SettingsCategoryKind.Personalization => HasUnsavedPersonalizationChanges(),
                _ => false
            };
        }

        /// <summary>
        /// Determines whether any loaded settings category has pending unsaved edits.
        /// </summary>
        private bool HasAnyUnsavedChanges() =>
            HasUnsavedAccountChanges() ||
            HasUnsavedAslmSettingsChanges() ||
            HasUnsavedPersonalizationChanges() ||
            HasUnsavedModuleChanges() ||
            _editSession.HasModuleChanges();

        /// <summary>
        /// Determines whether the personalization draft differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedPersonalizationChanges() =>
            _editSession.Application.HasPersonalizationChanges ||
            HasUnsavedThemeColorChanges();

        /// <summary>
        /// Determines whether the in-memory theme editor differs from the saved custom theme.
        /// </summary>
        private bool HasUnsavedThemeColorChanges()
        {
            if (_editingThemeDraft == null)
            {
                return false;
            }

            var saved = _customThemesStore.FindById(_editingThemeDraft.Id);
            if (saved == null)
            {
                // Theme exists only in the editor draft, not yet persisted.
                return true;
            }

            if (!string.Equals(_editingThemeDraft.Name, saved.Name, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(_editingThemeDraft.BaseAppearance, saved.BaseAppearance, StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var (key, value) in _editingThemeDraft.Colors)
            {
                if (!saved.Colors.TryGetValue(key, out var savedValue) ||
                    !string.Equals(value, savedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return saved.Colors.Count != _editingThemeDraft.Colors.Count;
        }

        /// <summary>
        /// Determines whether the account display name differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedAccountChanges()
        {
            return _editSession.Application.HasAccountChanges;
        }

        /// <summary>
        /// Determines whether any combined ASLM setting differs from the saved baseline.
        /// </summary>
        private bool HasUnsavedAslmSettingsChanges()
        {
            _portStartDraft = GetCurrentPortStartDraft();
            _updateDraft = GetCurrentUpdateDraft();
            return _editSession.Application.HasAslmChanges;
        }

        /// <summary>
        /// Determines whether ASLM settings that require an application restart differ from their baselines.
        /// </summary>
        private bool HasUnsavedAslmRestartSettingsChanges()
        {
            _portStartDraft = GetCurrentPortStartDraft();
            _updateDraft = GetCurrentUpdateDraft();
            return _editSession.Application.HasAslmRestartChanges;
        }

        /// <summary>
        /// Reads the module start port draft from visible controls when the ports section is shown.
        /// </summary>
        private string GetCurrentPortStartDraft() =>
            PortsSection.IsVisible
                ? ModulePortEntry.Text?.Trim() ?? string.Empty
                : _portStartDraft;

        /// <summary>
        /// Determines whether the visible module editors differ from the last saved baseline.
        /// </summary>
        private bool HasUnsavedModuleChanges()
        {
            if (_activeCategory?.Kind != SettingsCategoryKind.Module || _activeCategory.Module == null)
            {
                return false;
            }

            return _editSession.GetModule(_activeCategory.Module).HasChanges;
        }

        /// <summary>
        /// Determines whether update controls differ from the last saved baseline.
        /// </summary>
        private bool HasUnsavedUpdateChanges()
        {
            _updateDraft = GetCurrentUpdateDraft();
            return _updateDraft != _updateBaseline;
        }

        /// <summary>
        /// Gets the latest update draft, reading visible controls when present.
        /// </summary>
        private UpdateBaseline GetCurrentUpdateDraft() =>
            _checkUpdatesToggle != null &&
            _autoUpdatesToggle != null &&
            _appUpdateChannelPicker != null &&
            _moduleUpdateChannelPicker != null
                ? new UpdateBaseline(
                    _checkUpdatesToggle.IsToggled,
                    _autoUpdatesToggle.IsToggled,
                    _appUpdateChannelPicker.SelectedItem?.ToString() ?? "release",
                    _moduleUpdateChannelPicker.SelectedItem?.ToString() ?? "release")
                : _updateDraft;

        /// <summary>
        /// Pushes default update preferences into the visible update controls.
        /// </summary>
        private void ApplyUpdateDefaultsToControls()
        {
            _updateDraft = SettingsService.BuildDefaultUpdateBaseline();
            ApplyUpdateDraftsToControls();
        }

        /// <summary>
        /// Shows a confirmation dialog on the current shell page.
        /// </summary>
        private static Task<bool> ShowAlertAsync(string title, string message, string accept, string cancel) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayAlertAsync(title, message, accept, cancel)
                : Task.FromResult(false);

        /// <summary>
        /// Finds one keyed XAML style used by selector and footer state updates.
        /// </summary>
        private static Style? GetStyleResource(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style
                ? style
                : null;

        /// <summary>
        /// Shows a simple informational dialog on the current shell page.
        /// </summary>
        private Task ShowSuccessAsync(string message)
        {
            _notifications.PublishSystemToast(
                L.Get(LocalizationKeys.Settings_SavedTitle),
                message,
                L.Get(LocalizationKeys.Common_Saved),
                "settings-save");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Shows an error dialog on the current shell page.
        /// </summary>
        private static Task ShowErrorAsync(string message) =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayAlertAsync(
                    L.Get(LocalizationKeys.Settings_ValidationError),
                    message,
                    L.Get(LocalizationKeys.Common_OK))
                : Task.CompletedTask;


        // Saving

        /// <summary>
        /// Restores the currently visible category to its default values.
        /// </summary>
        private void OnDefaultClicked(object? sender, EventArgs e)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            SyncDraftValuesFromControls();

            switch (_activeCategory.Kind)
            {
                case SettingsCategoryKind.Aslm:
                    var defaults = SettingsService.BuildDefaultAslmDrafts();
                    _portStartDraft = defaults.PortStart;
                    _apiServerEnabledDraft = defaults.ApiServerEnabled;
                    _consoleDraft = defaults.ConsoleDefaults;
                    _restoreLastPageDraft = defaults.RestoreLastPage;
                    _legalAutoAcceptDraft = defaults.LegalAutoAcceptUpdates;
                    PortErrorLabel.IsVisible = false;
                    ApplyAslmDraftsToControls();
                    RenderAslmCategory();
                    break;
                case SettingsCategoryKind.Accounts:
                    if (!_sunriseService.IsCloudAccount)
                    {
                        _userNameDraft = Environment.UserName;
                    }
                    ApplyAslmDraftsToControls();
                    RenderAccountsCategory();
                    break;
                case SettingsCategoryKind.Updates:
                    ApplyUpdateDefaultsToControls();
                    RenderUpdatesCategory();
                    break;
                case SettingsCategoryKind.Module:
                    SettingsService.ResetModuleToDefaults(_editSession.GetModule(_activeCategory.Module!));
                    MarkModuleSettingsPresentationForRefresh(_activeCategory.Module!);
                    RenderModuleCategory(_activeCategory.Module!);
                    break;
                case SettingsCategoryKind.Personalization:
                    _personalizationDraft = new AppPersonalizationConfig();
                    _editingThemeDraft = null;
                    _personalizationControlsInitialized = false;
                    RenderPersonalizationCategory();
                    break;
            }

            SyncDraftValuesFromControls();
            UpdateActionButtons();
            ResetSettingsScrollPosition();
        }

        /// <summary>
        /// Reverts all pending settings drafts back to the last persisted values.
        /// </summary>
        private void OnDiscardChangesClicked(object? sender, EventArgs e)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            DiscardUnsavedChanges();
        }

        /// <summary>
        /// Restores accepted values and refreshes the active category after an explicit discard.
        /// </summary>
        private void DiscardUnsavedChanges()
        {
            var activeCategoryId = _activeCategory?.Id;

            DiscardAllDraftChanges();

            var targetCategory = ResolveCategory(activeCategoryId) ?? _categories.FirstOrDefault();
            if (targetCategory == null)
            {
                _activeCategory = null;
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_NoSettingsAvailable));
                UpdateActionButtons();
                return;
            }

            ActivateCategory(targetCategory);
        }

        /// <summary>
        /// Restores accepted application and module drafts without slow runtime or discovery work.
        /// </summary>
        private void DiscardAllDraftChanges()
        {
            _editSession.Application.DiscardAslm();
            _editSession.Application.DiscardPersonalization();
            _editSession.DiscardModules();
            MarkAllModuleSettingsPresentationsForRefresh();
            _editingThemeDraft = null;
            _personalizationControlsInitialized = false;

            ApplyAslmDraftsToControls();
            PortErrorLabel.IsVisible = false;
        }

        /// <summary>
        /// Saves the current settings without restarting anything.
        /// </summary>
        private async void OnSaveClicked(object? sender, EventArgs e)
        {
            await SaveAsync(restartAfterSave: false);
        }

        /// <summary>
        /// Saves the current settings and restarts the active target when supported.
        /// </summary>
        private async void OnSaveAndRestartClicked(object? sender, EventArgs e)
        {
            await SaveAsync(restartAfterSave: true);
        }

        /// <summary>
        /// Validates, persists, and optionally restarts the active category target.
        /// </summary>
        private async Task SaveAsync(bool restartAfterSave)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            try
            {
                _isSaving = true;
                SyncDraftValuesFromControls();
                UpdateSelectorButtonStates();
                UpdateActionButtons();

                if (!SettingsService.TryValidateDisplayName(_userNameDraft, out var validatedUserName, out var displayNameErrorMessage))
                {
                    await ShowErrorAsync(displayNameErrorMessage);
                    return;
                }
                _userNameDraft = validatedUserName;

                var portResult = SettingsService.TryParsePortStart(_portStartDraft);
                if (!portResult.Success)
                {
                    if (_activeCategory.Kind == SettingsCategoryKind.Aslm)
                    {
                        ShowPortError(portResult.ErrorMessage);
                    }
                    else
                    {
                        await ShowErrorAsync(portResult.ErrorMessage);
                    }

                    return;
                }

                _updateDraft = GetCurrentUpdateDraft();
                if (!SettingsService.TryValidateAndBuildUpdateSettings(_updateDraft, out var nextSettings, out var updateErrorMessage))
                {
                    await ShowErrorAsync(updateErrorMessage);
                    return;
                }

                foreach (var module in _loadedModules)
                {
                    if (!_settingsService.TryValidateModuleSettings(
                            _editSession.GetModule(module),
                            out var moduleErrorMessage))
                    {
                        await ShowErrorAsync(moduleErrorMessage);
                        return;
                    }
                }

                var hadPersonalizationChanges = HasUnsavedPersonalizationChanges();
                if (hadPersonalizationChanges)
                {
                    await SavePersonalizationAsync(applyImmediately: !restartAfterSave);
                }

                var hadAslmSettingsChanges = HasUnsavedAslmSettingsChanges();
                var hadAppRestartChanges = HasUnsavedAslmRestartSettingsChanges();
                var hadAslmChanges = HasUnsavedAccountChanges() || hadAslmSettingsChanges;
                var modulesWithChanges = GetModulesWithUnsavedChanges();

                SettingsService.ApplyDraftsToAppData(
                    _appData,
                    _userNameDraft,
                    portResult.ModulesStart,
                    _consoleDraft,
                    nextSettings,
                    _restoreLastPageDraft,
                    _legalAutoAcceptDraft);
                await _appData.SaveAsync();

                if (_apiServerEnabledDraft != _aslmBaseline.ApiServerEnabled)
                {
                    await _mirrorServer.SetEnabledAsync(_apiServerEnabledDraft);
                }

                _apiServerEnabledDraft = _mirrorServer.IsEnabled;
                _updateDraft = SettingsService.BuildAslmDraftSnapshot(
                    _appData,
                    _mirrorServer.IsEnabled).UpdateBaseline;
                _editSession.Application.AcceptAslm();
                PortErrorLabel.IsVisible = false;

                var touchedModules = new HashSet<ModuleConfig>();
                var deferredSettings = new List<string>();
                foreach (var module in modulesWithChanges)
                {
                    var moduleSaveResult = await _settingsService.SaveActiveModuleAsync(
                        _editSession.GetModule(module));
                    touchedModules.UnionWith(moduleSaveResult.TouchedModules);
                    deferredSettings.AddRange(moduleSaveResult.DeferredSettings);
                }

                foreach (var module in touchedModules)
                {
                    await ReloadModuleRuntimeValuesAsync(module);
                }

                var activeCategoryId = _activeCategory.Id;
                _categories = SettingsPresentationBuilder.BuildCategories(_loadedModules).ToList();
                BuildCategorySelectors();
                var resolvedCategory = ResolveCategory(activeCategoryId);
                if (resolvedCategory != null)
                {
                    ActivateCategory(resolvedCategory);
                }

                var hadAnyPersistedSettingsChanges = hadAslmChanges || hadPersonalizationChanges;
                var successMessage = BuildLocalizedSaveMessage(
                    hadAnyPersistedSettingsChanges,
                    touchedModules.Count > 0,
                    deferredSettings);
                if (restartAfterSave)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await ShowSuccessAsync(successMessage);
                    });
                }
                else
                {
                    await ShowSuccessAsync(successMessage);
                }

                if (restartAfterSave && hadPersonalizationChanges)
                {
                    try
                    {
                        await RestartApplicationThroughLauncherAsync();
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_UpdateStatus_RestartFailed, ex.Message));
                    }

                    return;
                }

                if (restartAfterSave && await RestartChangedTargetsAsync(hadAppRestartChanges, touchedModules))
                {
                    return;
                }
            }
            finally
            {
                _isSaving = false;
                UpdateSelectorButtonStates();
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Restarts the changed app-level target or changed module targets when supported.
        /// </summary>
        private async Task<bool> RestartChangedTargetsAsync(bool restartApp, IEnumerable<ModuleConfig> changedModules)
        {
            if (restartApp)
            {
                await RestartApplicationAsync();
                return true;
            }

            foreach (var module in changedModules.Where(CanRestartModule))
            {
                await _settingsService.RestartModuleAsync(module);
            }

            return false;
        }

        /// <summary>
        /// Stops modules, starts the launcher with process wait, and exits so ASLM relaunches cleanly.
        /// </summary>
        private async Task RestartApplicationThroughLauncherAsync()
        {
            await _settingsService.StopAllModulesAsync();
            await Task.Run(SettingsService.StartLauncherForApplicationRestart);
            Application.Current?.Quit();
        }

        /// <summary>
        /// Restarts the application startup chain so app-level changes take effect.
        /// </summary>
        private async Task RestartApplicationAsync()
        {
            await _settingsService.StopAllModulesAsync();

            if (Application.Current is not App application || application.Windows.Count == 0)
            {
                return;
            }

            var newPage = application.CreateStartupPage();
            var window = application.Windows[0];
            window.Page = newPage;
        }

        /// <summary>
        /// Displays the current port validation error.
        /// </summary>
        private void ShowPortError(string message)
        {
            PortErrorLabel.Text = message;
            PortErrorLabel.IsVisible = true;
        }
    }
}
