// Copyright NEXTGGTECH. Apache License 2.0.

using Debug = System.Diagnostics.Debug;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Controls.Settings;

namespace ASLM.Pages
{
    /// <summary>
    /// Rendering part of the settings view: category pages, the module settings surface,
    /// section navigation and scrolling, and the synchronization of drafts with the built-in controls.
    /// </summary>
    public partial class SettingsView
    {
        // Category rendering

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
            UserProfileSection.IsVisible = SunriseService.IsEnabled && !_sunriseService.IsCloudAccount;

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

            // Show the cached (or empty new) module page at once; its rows are created afterwards in the background.
            var moduleView = GetOrCreateModuleSettingsSurface(module, out var presentation);
            if (!ReferenceEquals(ModuleSettingsContainer.Content, moduleView))
            {
                ModuleSettingsContainer.Content = moduleView;
            }

            ShowModuleSectionNavigation(presentation);
            EmptyCategoryState.IsVisible = false;
            _ = PopulateModuleSettingsSurfaceAsync(module, presentation);
        }


        // Module settings surface

        /// <summary>
        /// Returns a stable module view without synchronously materializing or refreshing its editor rows.
        /// </summary>
        private ModuleSettingsView GetOrCreateModuleSettingsSurface(
            ModuleConfig module,
            out ModuleSettingsPageViewModel presentation)
        {
            // Create the page view model and the view once per module and reuse them on later visits.
            var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
            if (!_moduleSettingsPresentations.TryGetValue(runtimeKey, out presentation!))
            {
                presentation = new ModuleSettingsPageViewModel(
                    QueueActionButtonUpdate,
                    OnModuleSettingsSectionRequested);
                _moduleSettingsPresentations[runtimeKey] = presentation;
                _moduleSettingsPresentationsNeedingRefresh.Remove(runtimeKey);
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
        /// Populates a visible module page between UI frames and ignores work canceled by navigation or closing.
        /// </summary>
        private async Task PopulateModuleSettingsSurfaceAsync(
            ModuleConfig module,
            ModuleSettingsPageViewModel presentation)
        {
            var cancellationToken = _moduleSurfaceBuildCancellation.Token;
            try
            {
                // Let the empty module surface and close button paint before the first editor row is created.
                await Task.Yield();

                // Create the editor rows step by step from the module draft.
                await presentation.LoadIncrementallyAsync(
                    _editSession.GetModule(module),
                    L.Get(LocalizationKeys.Settings_Engine_Installed),
                    L.Get(LocalizationKeys.Settings_Engine_NotInstalled),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // A draft that was reset or discarded while the page was cached needs its rows refreshed.
                var runtimeKey = SettingsService.GetModuleRuntimeKey(module);
                if (_moduleSettingsPresentationsNeedingRefresh.Remove(runtimeKey))
                {
                    await presentation.RefreshFromDraftIncrementallyAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // Finish only when this page is still the visible one; otherwise the user has navigated away.
                var stillActive =
                    _activeCategory?.Kind == SettingsCategoryKind.Module &&
                    _activeCategory.Module != null &&
                    string.Equals(
                        _activeCategory.Module.SourcePath,
                        module.SourcePath,
                        StringComparison.OrdinalIgnoreCase);
                if (!stillActive ||
                    !_moduleSettingsPresentations.TryGetValue(
                        runtimeKey,
                        out var currentPresentation) ||
                    !ReferenceEquals(currentPresentation, presentation))
                {
                    return;
                }

                // Bind the section navigation, or show the empty state when the module has nothing to display.
                ShowModuleSectionNavigation(presentation);
                if (!presentation.HasSettings)
                {
                    ShowEmptyCategory(L.Get(LocalizationKeys.Settings_ModuleNoSettings));
                    return;
                }

                EmptyCategoryState.IsVisible = false;
                UpdateSettingsScrollBar();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Navigation and closing intentionally discard incomplete visual trees.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to build settings controls for module '{module.Name}': {ex.Message}");
            }
        }


        // Section navigation and scrolling

        /// <summary>
        /// Binds the right sidebar to the active module without inheriting it on built-in pages.
        /// </summary>
        private void ShowModuleSectionNavigation(ModuleSettingsPageViewModel presentation)
        {
            ModuleSectionNavigationContainer.RemoveBinding(IsVisibleProperty);
            ModuleSectionNavigationContainer.BindingContext = presentation;
            ModuleSectionNavigationContainer.SetBinding(
                IsVisibleProperty,
                new Binding(nameof(ModuleSettingsPageViewModel.HasSectionNavigation), source: presentation));
        }

        /// <summary>
        /// Removes the module navigation binding and collapses its reserved column.
        /// </summary>
        private void HideModuleSectionNavigation()
        {
            ModuleSectionNavigationContainer.RemoveBinding(IsVisibleProperty);
            ModuleSectionNavigationContainer.IsVisible = false;
            ModuleSectionNavigationContainer.BindingContext = null;
        }

        /// <summary>
        /// Scrolls the active module page directly to its selected settings section.
        /// </summary>
        private async void OnModuleSettingsSectionRequested(ModuleSettingsSectionViewModel section)
        {
            if (ModuleSettingsContainer.Content is not ModuleSettingsView moduleView)
            {
                return;
            }

            try
            {
                await Task.Yield();
                var sectionView = moduleView.FindSectionView(section);
                if (sectionView != null)
                {
                    await SettingsScroll.ScrollToAsync(sectionView, ScrollToPosition.Start, true);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to scroll to module settings section: {ex.Message}");
            }
        }

        /// <summary>
        /// Keeps the active module category synchronized with manual content scrolling.
        /// </summary>
        private void OnSettingsScrollScrolled(object? sender, ScrolledEventArgs e)
        {
            UpdateSettingsScrollBar();

            // Only a module page with at least two visible sections has a navigation to keep in sync.
            if (_activeCategory?.Kind != SettingsCategoryKind.Module ||
                ModuleSettingsContainer.Content is not ModuleSettingsView moduleView ||
                moduleView.BindingContext is not ModuleSettingsPageViewModel presentation ||
                !presentation.HasSectionNavigation)
            {
                return;
            }

            var sectionViews = moduleView.GetVisibleSectionViews().ToList();
            if (sectionViews.Count < 2)
            {
                return;
            }

            // The final section may never reach the top, so reaching the bottom selects it explicitly.
            var contentHeight = SettingsScroll.ContentSize.Height;
            var reachedBottom = SettingsScroll.Height > 0 &&
                contentHeight > SettingsScroll.Height + 1 &&
                e.ScrollY + SettingsScroll.Height >= contentHeight - 1;
            if (reachedBottom)
            {
                presentation.ActivateVisibleSection(sectionViews[^1].Section);
                return;
            }

            // Otherwise the active section is the last one whose top has reached a line just below the viewport top.
            var activationLine = e.ScrollY + 12;
            var activeSection = sectionViews[0].Section;
            foreach (var (section, sectionView) in sectionViews)
            {
                var sectionTop = GetVerticalOffset(sectionView, SettingsContentContainer);
                if (double.IsNaN(sectionTop) || sectionTop > activationLine)
                {
                    break;
                }

                activeSection = section;
            }

            presentation.ActivateVisibleSection(activeSection);
        }

        /// <summary>
        /// Keeps the stable scrollbar visible only while the settings content overflows.
        /// </summary>
        private void UpdateSettingsScrollBar() => SettingsScrollBarTrack.Refresh();

        /// <summary>
        /// Resolves one descendant's vertical offset inside the scroll content tree.
        /// </summary>
        private static double GetVerticalOffset(VisualElement element, Element ancestor)
        {
            var offset = 0d;
            Element? current = element;
            while (current != null && !ReferenceEquals(current, ancestor))
            {
                if (current is VisualElement visualElement)
                {
                    offset += visualElement.Y;
                }

                current = current.Parent;
            }

            return ReferenceEquals(current, ancestor) ? offset : double.NaN;
        }


        // Surface state

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
        /// Detaches and clears cached module trees when discovery or localization changes their source data.
        /// </summary>
        private void ClearModuleSettingsSurfaceCache()
        {
            RestartModuleSurfaceBuilds();
            ModuleSettingsContainer.Content = null;
            HideModuleSectionNavigation();
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


        // Draft and control synchronization

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
        /// Pushes API, console, legal, and navigation drafts into the stable XAML toggle controls.
        /// </summary>
        private void ApplyAslmBuiltInDraftsToToggles()
        {
            // The toggles are set without raising their Toggled event, so the drafts are not rewritten from them.
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

            if (_restoreLastPageToggle != null)
            {
                _restoreLastPageToggle.SetStateWithoutToggleEvent(_restoreLastPageDraft);
            }

            if (_disableHomePageToggle != null)
            {
                _disableHomePageToggle.SetStateWithoutToggleEvent(_disableHomePageDraft);
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
        /// Copies API, console, legal, and navigation toggles into the shared drafts after user interaction.
        /// </summary>
        private void RefreshAslmApiAndConsoleDraftsFromToggles()
        {
            if (_apiServerToggle != null)
            {
                _apiServerEnabledDraft = _apiServerToggle.IsToggled;
            }

            // The three console switches form one draft value, so they are read together.
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

            if (_restoreLastPageToggle != null)
            {
                _restoreLastPageDraft = _restoreLastPageToggle.IsToggled;
            }

            if (_disableHomePageToggle != null)
            {
                _disableHomePageDraft = _disableHomePageToggle.IsToggled;
            }
        }


        // Surface visibility

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
            if (!showModuleSettings)
            {
                HideModuleSectionNavigation();
            }

            EmptyCategoryState.IsVisible = showEmptyState;
        }
    }
}
