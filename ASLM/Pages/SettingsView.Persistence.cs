// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Persistence part of the settings view: draft synchronization, unsaved-change detection,
    /// reset and discard, saving, and restarting after a save.
    /// </summary>
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

            // Module editors update their drafts while the user edits, so there is nothing to copy.
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
            if (SunriseService.IsEnabled && UserProfileSection.IsVisible)
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
            // and by load/default flows so SyncDraftValuesFromControls cannot overwrite them
            // from WinUI timing glitches.
            _updateDraft = GetCurrentUpdateDraft();
        }

        /// <summary>
        /// Asks the user to confirm discarding unsaved changes and returns whether the caller may continue.
        /// A confirmed discard also reverts the drafts and the theme preview.
        /// </summary>
        private async Task<bool> ConfirmDiscardChangesIfNeededAsync()
        {
            // Capture the latest control values first, so edits that were not yet synchronized count as changes.
            SyncDraftValuesFromControls();

            if (_activeCategory == null || !HasAnyUnsavedChanges())
            {
                return true;
            }

            var discardChanges = await ShowAlertAsync(
                L.Get(LocalizationKeys.Settings_DiscardDialog_Title),
                L.Get(LocalizationKeys.Settings_DiscardDialog_Message),
                L.Get(LocalizationKeys.Common_Discard),
                L.Get(LocalizationKeys.Common_Stay), danger: true);

            if (!discardChanges)
            {
                return false;
            }

            // Drop the drafts and restore the saved theme, which an unsaved personalization preview may have replaced.
            DiscardAllDraftChanges();

            _themeService.ApplyFromSettings();

            return true;
        }


        // Unsaved change detection

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

            // Compare the name, the base appearance and then every palette color against the saved theme.
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

            // All draft colors match, so a differing count means colors were removed from the draft.
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
            // Refresh the drafts that are read from visible controls before comparing with the baselines.
            _portStartDraft = GetCurrentPortStartDraft();
            _updateDraft = GetCurrentUpdateDraft();
            return _editSession.Application.HasAslmChanges;
        }

        /// <summary>
        /// Determines whether ASLM settings that require an application restart differ from their baselines.
        /// </summary>
        private bool HasUnsavedAslmRestartSettingsChanges()
        {
            // Refresh the drafts that are read from visible controls before comparing with the baselines.
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


        // Defaults and UI helpers

        /// <summary>
        /// Pushes default update preferences into the visible update controls.
        /// </summary>
        private void ApplyUpdateDefaultsToControls()
        {
            _updateDraft = SettingsService.BuildDefaultUpdateBaseline();
            ApplyUpdateDraftsToControls();
        }

        /// <summary>
        /// Shows a confirmation dialog, optionally marking its accepting action as destructive.
        /// </summary>
        private Task<bool> ShowAlertAsync(string title, string message, string accept, string cancel, bool danger = false) =>
            DialogService.For(this).ConfirmAsync(this, title, message, accept, cancel, danger: danger);

        /// <summary>
        /// Finds one keyed XAML style used by selector and footer state updates.
        /// </summary>
        private static Style? GetStyleResource(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Style style
                ? style
                : null;

        /// <summary>
        /// Publishes a system toast that confirms the save result.
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
        private Task ShowErrorAsync(string message) =>
            DialogService.For(this).AlertAsync(this, DialogText.Localized(LocalizationKeys.Settings_ValidationError), message);


        // Reset and discard

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

            // Reset only the drafts of the visible category, push them into the controls and render the page again.
            // The baselines stay untouched, so the reset counts as an unsaved change.
            switch (_activeCategory.Kind)
            {
                case SettingsCategoryKind.Aslm:
                    var defaults = SettingsService.BuildDefaultAslmDrafts();
                    _portStartDraft = defaults.PortStart;
                    _apiServerEnabledDraft = defaults.ApiServerEnabled;
                    _consoleDraft = defaults.ConsoleDefaults;
                    _restoreLastPageDraft = defaults.RestoreLastPage;
                    _disableHomePageDraft = defaults.DisableHomePage;
                    _legalAutoAcceptDraft = defaults.LegalAutoAcceptUpdates;
                    PortErrorLabel.IsVisible = false;
                    ApplyAslmDraftsToControls();
                    RenderAslmCategory();
                    break;
                case SettingsCategoryKind.Accounts:
                    // Only the display name of a local account is editable; its default is the OS user name.
                    if (SunriseService.IsEnabled && !_sunriseService.IsCloudAccount)
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
                    // A fresh config holds the default appearance and language; the open theme edit is dropped.
                    _personalizationDraft = new AppPersonalizationConfig();
                    _editingThemeDraft = null;
                    _personalizationControlsInitialized = false;
                    RenderPersonalizationCategory();
                    break;
            }

            // Re-read the controls into the drafts, then refresh the footer and show the page from the top.
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

            ActivateCategory(targetCategory, loadModuleRuntimeValues: false);
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


        // Saving

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
        /// Validates the drafts of every category, persists them, and optionally restarts the changed targets.
        /// Nothing is written until all drafts passed validation.
        /// </summary>
        private async Task SaveAsync(bool restartAfterSave)
        {
            if (_activeCategory == null || _isSaving)
            {
                return;
            }

            try
            {
                // Lock the sidebar and the footer, and capture the latest control values in the drafts.
                _isSaving = true;
                SyncDraftValuesFromControls();
                UpdateSelectorButtonStates();
                UpdateActionButtons();

                // Validation, step 1: the display name, which exists only with ASLM accounts.
                if (SunriseService.IsEnabled)
                {
                    if (!SettingsService.TryValidateDisplayName(_userNameDraft, out var validatedUserName, out var displayNameErrorMessage))
                    {
                        await ShowErrorAsync(displayNameErrorMessage);
                        return;
                    }

                    _userNameDraft = validatedUserName;
                }

                // Step 2: the module port range; the error appears next to the field when the ASLM page is open,
                // and in a dialog otherwise.
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

                // Step 3: the update settings, which are also converted into the persisted form.
                _updateDraft = GetCurrentUpdateDraft();
                if (!SettingsService.TryValidateAndBuildUpdateSettings(_updateDraft, out var nextSettings, out var updateErrorMessage))
                {
                    await ShowErrorAsync(updateErrorMessage);
                    return;
                }

                // Step 4: the settings of every loaded module, not only the open one.
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

                // Personalization is saved first; when a restart follows, the new theme is not applied immediately.
                var hadPersonalizationChanges = HasUnsavedPersonalizationChanges();
                if (hadPersonalizationChanges)
                {
                    await SavePersonalizationAsync(applyImmediately: !restartAfterSave);
                }

                // Record what changed before the ASLM baseline is accepted below:
                // the results decide the message and the restarts.
                var hadAslmSettingsChanges = HasUnsavedAslmSettingsChanges();
                var hadAppRestartChanges = HasUnsavedAslmRestartSettingsChanges();
                var hadNavigationChanges = _editSession.Application.HasNavigationChanges;
                var hadAslmChanges = HasUnsavedAccountChanges() || hadAslmSettingsChanges;
                var modulesWithChanges = GetModulesWithUnsavedChanges();

                // Write the application drafts into app data and persist them.
                SettingsService.ApplyDraftsToAppData(
                    _appData,
                    _userNameDraft,
                    portResult.ModulesStart,
                    _consoleDraft,
                    nextSettings,
                    _restoreLastPageDraft,
                    _disableHomePageDraft,
                    _legalAutoAcceptDraft);
                await _appData.SaveAsync();

                // The Mirror Server is switched separately from app data, and only when its enabled state changed.
                // Then the drafts take over its actual state and the saved values, and become the new baseline.
                if (_apiServerEnabledDraft != _aslmBaseline.ApiServerEnabled)
                {
                    await _mirrorServer.SetEnabledAsync(_apiServerEnabledDraft);
                }

                _apiServerEnabledDraft = _mirrorServer.IsEnabled;
                _updateDraft = SettingsService.BuildAslmDraftSnapshot(
                    _appData,
                    _mirrorServer.IsEnabled).UpdateBaseline;
                _editSession.Application.AcceptAslm();
                if (hadNavigationChanges)
                    NavigationSettingsSaved?.Invoke(this, EventArgs.Empty);
                PortErrorLabel.IsVisible = false;

                // Persist only the modules that changed, and collect the modules that were affected
                // and the settings whose application is deferred.
                var touchedModules = new HashSet<ModuleConfig>();
                var deferredSettings = new List<string>();
                foreach (var module in modulesWithChanges)
                {
                    var moduleSaveResult = await _settingsService.SaveActiveModuleAsync(
                        _editSession.GetModule(module));
                    touchedModules.UnionWith(moduleSaveResult.TouchedModules);
                    deferredSettings.AddRange(moduleSaveResult.DeferredSettings);
                }

                // Once module saves touched modules, adopt the current catalog revision
                // so the next load does not rediscover them needlessly.
                if (touchedModules.Count > 0)
                {
                    _loadedModuleCatalogRevision = _settingsService.ModuleCatalogRevision;
                }

                // Rebuild the categories and reopen the active one on the freshly saved baselines.
                var activeCategoryId = _activeCategory.Id;
                _categories = SettingsPresentationBuilder.BuildCategories(_loadedModules).ToList();
                BuildCategorySelectors();
                var resolvedCategory = ResolveCategory(activeCategoryId);
                if (resolvedCategory != null)
                {
                    ActivateCategory(resolvedCategory, loadModuleRuntimeValues: false);
                }

                // Report the result; when a restart follows, the toast is published from a background task
                // after a short delay instead of immediately.
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

                // With personalization changes the restart goes through the launcher,
                // and a failure to restart is reported to the user.
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

                // Otherwise restart only the changed targets: the whole application, or just the changed modules.
                if (restartAfterSave && await RestartChangedTargetsAsync(hadAppRestartChanges, touchedModules))
                {
                    return;
                }
            }
            finally
            {
                // Unlock the UI, and open a category that was requested while the save was running.
                _isSaving = false;
                ApplyRequestedCategory();
                UpdateSelectorButtonStates();
                UpdateActionButtons();
            }
        }


        // Restarting

        /// <summary>
        /// Restarts the changed app-level target or changed module targets when supported.
        /// Returns true when the whole application was restarted instead of individual modules.
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


        // Validation feedback

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
