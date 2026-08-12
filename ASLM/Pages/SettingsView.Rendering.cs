// Copyright NEXTGGTECH. Apache License 2.0.

using Debug = System.Diagnostics.Debug;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Controls.Settings;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Rendering

        /// <summary>
        /// Shows the combined ASLM settings category while hiding module-specific content.
        /// </summary>
        private void RenderAslmCategory()
        {
            PrepareCategorySurface(showEmptyState: false, showBuiltInSettings: true);
            BuiltInSettingsContainer.ShowCategory(SettingsCategoryKind.Aslm);
        }

        /// <summary>
        /// Shows the combined accounts category with ASLM, GitHub, and Ollama sections.
        /// </summary>
        private void RenderAccountsCategory()
        {
            PrepareCategorySurface(showEmptyState: false, showBuiltInSettings: true);
            BuiltInSettingsContainer.ShowCategory(SettingsCategoryKind.Accounts);
            UserProfileSection.IsVisible = !_sunriseService.IsCloudAccount;

            _githubDraft = _githubAccountStore.GetState();
            UpdateAslmAccountActionControls();
            UpdateGitHubAccountActionControls();
            UpdateOllamaAccountActionControls();
            StartOllamaMetadataRefresh();
        }

        /// <summary>
        /// Shows the dedicated updates category.
        /// </summary>
        private void RenderUpdatesCategory()
        {
            PrepareCategorySurface(showEmptyState: false, showBuiltInSettings: true);
            BuiltInSettingsContainer.ShowCategory(SettingsCategoryKind.Updates);
        }

        /// <summary>
        /// Shows the stable XAML settings surface associated with one module draft.
        /// </summary>
        private void RenderModuleCategory(ModuleConfig module)
        {
            PrepareCategorySurface(showEmptyState: false, showModuleSettings: true);

            var moduleView = GetOrCreateModuleSettingsSurface(module, out var presentation);
            if (!ReferenceEquals(ModuleSettingsContainer.Content, moduleView))
            {
                ModuleSettingsContainer.Content = moduleView;
            }

            if (!presentation.HasSettings)
            {
                ShowEmptyCategory(L.Get(LocalizationKeys.Settings_ModuleNoSettings));
                return;
            }

            EmptyCategoryState.IsVisible = false;
        }

        /// <summary>
        /// Returns a stable module view and refreshes its presentation from the current draft.
        /// </summary>
        private ModuleSettingsView GetOrCreateModuleSettingsSurface(
            ModuleConfig module,
            out ModuleSettingsPageViewModel presentation)
        {
            var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
            if (!_moduleSettingsPresentations.TryGetValue(runtimeKey, out presentation!))
            {
                presentation = new ModuleSettingsPageViewModel(QueueActionButtonUpdate);
                _moduleSettingsPresentations[runtimeKey] = presentation;
                presentation.Load(
                    _editSession.GetModule(module),
                    L.Get(LocalizationKeys.Settings_Engine_Installed),
                    L.Get(LocalizationKeys.Settings_Engine_NotInstalled));
                _moduleSettingsPresentationsNeedingRefresh.Remove(runtimeKey);
            }
            else if (_moduleSettingsPresentationsNeedingRefresh.Remove(runtimeKey))
            {
                presentation.RefreshFromDraft();
            }

            if (!_moduleSettingsViews.TryGetValue(runtimeKey, out var moduleView))
            {
                moduleView = new ModuleSettingsView
                {
                    BindingContext = presentation
                };
                _moduleSettingsViews[runtimeKey] = moduleView;
            }

            return moduleView;
        }

        /// <summary>
        /// Marks one cached module presentation for a lazy refresh after its draft changes externally.
        /// </summary>
        private void MarkModuleSettingsPresentationForRefresh(ModuleConfig module)
        {
            var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
            _moduleSettingsPresentationsNeedingRefresh.Add(runtimeKey);
        }

        /// <summary>
        /// Marks every cached module presentation for a lazy refresh after a session-wide discard.
        /// </summary>
        private void MarkAllModuleSettingsPresentationsForRefresh()
        {
            foreach (var module in _loadedModules)
            {
                MarkModuleSettingsPresentationForRefresh(module);
            }
        }

        /// <summary>
        /// Builds detached module views between UI frames so first navigation does not create their full XAML trees.
        /// </summary>
        private async Task WarmModuleSettingsSurfacesAsync()
        {
            var generation = ++_moduleSettingsWarmupGeneration;

            try
            {
                // Let the active category render before allocating the remaining module editor trees.
                await Task.Yield();
                foreach (var module in _loadedModules)
                {
                    if (generation != _moduleSettingsWarmupGeneration)
                    {
                        return;
                    }

                    var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
                    if (!_moduleSettingsViews.ContainsKey(runtimeKey))
                    {
                        GetOrCreateModuleSettingsSurface(module, out _);
                    }

                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to prepare module settings views: {ex.Message}");
            }
        }

        /// <summary>
        /// Detaches and clears cached module trees when discovery or localization changes their source data.
        /// </summary>
        private void ClearModuleSettingsSurfaceCache()
        {
            _moduleSettingsWarmupGeneration++;
            ModuleSettingsContainer.Content = null;
            _moduleSettingsViews.Clear();
            _moduleSettingsPresentations.Clear();
            _moduleSettingsPresentationsNeedingRefresh.Clear();
        }

        /// <summary>
        /// Shows the empty-state card and hides other content containers.
        /// </summary>
        private void ShowEmptyCategory(string message)
        {
            PrepareCategorySurface(showEmptyState: true);
            EmptyCategoryLabel.Text = message;
        }

        /// <summary>
        /// Pushes the current ASLM draft values back into the always-created XAML controls.
        /// </summary>
        private void ApplyAslmDraftsToControls()
        {
            ApplyBuiltInControlState(() =>
            {
                UsernameEntry.Text = _userNameDraft;
                ModulePortEntry.Text = _portStartDraft;
                ApplyAslmBuiltInDraftsToToggles();
                ApplyUpdateDraftsToControls();
            });
        }

        /// <summary>
        /// Pushes API and console draft values into the stable XAML toggle controls.
        /// </summary>
        private void ApplyAslmBuiltInDraftsToToggles()
        {
            if (_apiServerToggle != null)
            {
                _apiServerToggle.SetStateWithoutToggleEvent(_apiServerEnabledDraft);
            }

            if (_consoleSidebarToggle != null)
            {
                _consoleSidebarToggle.SetStateWithoutToggleEvent(_consoleDraft.SidebarVisible);
            }

            if (_consoleIndividualToggle != null)
            {
                _consoleIndividualToggle.SetStateWithoutToggleEvent(_consoleDraft.ShowIndividualModuleConsoles);
            }

            if (_consoleCompletedToggle != null)
            {
                _consoleCompletedToggle.SetStateWithoutToggleEvent(_consoleDraft.ShowCompletedProcesses);
            }

            if (_legalAutoAcceptToggle != null)
            {
                _legalAutoAcceptToggle.SetStateWithoutToggleEvent(_legalAutoAcceptDraft);
            }
        }

        /// <summary>
        /// Pushes the update draft into the stable XAML controls without resetting action results.
        /// </summary>
        private void ApplyUpdateDraftsToControls()
        {
            ApplyBuiltInControlState(() =>
            {
                _checkUpdatesToggle?.SetStateWithoutToggleEvent(_updateDraft.CheckEnabled);
                _autoUpdatesToggle?.SetStateWithoutToggleEvent(_updateDraft.AutoUpdateEnabled);

                if (_appUpdateChannelPicker != null)
                {
                    _appUpdateChannelPicker.SelectedItem = _updateDraft.AppChannel;
                }

                if (_moduleUpdateChannelPicker != null)
                {
                    _moduleUpdateChannelPicker.SelectedItem = _updateDraft.ModuleDefaultChannel;
                }
            });
        }

        /// <summary>
        /// Executes one control-state update while suppressing nested change notifications.
        /// </summary>
        private void ApplyBuiltInControlState(Action apply)
        {
            _builtInControlStateApplicationDepth++;
            try
            {
                apply();
            }
            finally
            {
                _builtInControlStateApplicationDepth--;
            }
        }

        /// <summary>
        /// Copies API, console, and legal compact toggles into the shared drafts after user interaction.
        /// </summary>
        private void RefreshAslmApiAndConsoleDraftsFromToggles()
        {
            if (_apiServerToggle != null)
            {
                _apiServerEnabledDraft = _apiServerToggle.IsToggled;
            }

            if (_consoleSidebarToggle != null &&
                _consoleCompletedToggle != null &&
                _consoleIndividualToggle != null)
            {
                _consoleDraft = new ConsoleBaseline(
                    _consoleSidebarToggle.IsToggled,
                    _consoleCompletedToggle.IsToggled,
                    _consoleIndividualToggle.IsToggled);
            }

            if (_legalAutoAcceptToggle != null)
            {
                _legalAutoAcceptDraft = _legalAutoAcceptToggle.IsToggled;
            }
        }

        /// <summary>
        /// Applies baseline visibility before showing one stable category tree.
        /// </summary>
        private void PrepareCategorySurface(
            bool showEmptyState,
            bool showBuiltInSettings = false,
            bool showModuleSettings = false)
        {
            BuiltInSettingsContainer.IsVisible = showBuiltInSettings;
            if (!showBuiltInSettings)
            {
                BuiltInSettingsContainer.HideCategories();
            }

            ModuleSettingsContainer.IsVisible = showModuleSettings;
            EmptyCategoryState.IsVisible = showEmptyState;
        }
    }
}
