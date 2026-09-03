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

            ShowModuleSectionNavigation(presentation);

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
                presentation = new ModuleSettingsPageViewModel(
                    QueueActionButtonUpdate,
                    OnModuleSettingsSectionRequested);
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
        /// Recalculates the custom scrollbar when the viewport or its content changes size.
        /// </summary>
        private void OnSettingsScrollGeometryChanged(object? sender, EventArgs e)
        {
            UpdateSettingsScrollBar();
        }

        /// <summary>
        /// Keeps the stable scrollbar visible only while the settings content overflows.
        /// </summary>
        private void UpdateSettingsScrollBar()
        {
            if (_isUpdatingSettingsScrollBar)
            {
                return;
            }

            var refreshAfterLayout = false;
            _isUpdatingSettingsScrollBar = true;
            try
            {
                var viewportHeight = SettingsScroll.Height;
                var contentHeight = Math.Max(SettingsScroll.ContentSize.Height, SettingsContentContainer.Height);
                var canScroll = viewportHeight > 0 && contentHeight > viewportHeight + 1;
                if (SettingsScrollBarTrack.IsVisible != canScroll)
                {
                    SettingsScrollBarTrack.IsVisible = canScroll;
                    refreshAfterLayout = canScroll;
                }

                if (!canScroll)
                {
                    _settingsScrollBarThumbTop = 0;
                    SettingsScrollBarThumb.TranslationY = 0;
                    return;
                }

                // Map the visible content ratio onto a fixed-width track without hover geometry changes.
                var thumbHeight = Math.Max(
                    SettingsScrollBarMinThumbHeight,
                    viewportHeight * viewportHeight / contentHeight);
                thumbHeight = Math.Min(viewportHeight, thumbHeight);
                var maximumThumbTravel = Math.Max(0, viewportHeight - thumbHeight);
                var maximumScroll = Math.Max(0, contentHeight - viewportHeight);
                _settingsScrollBarThumbTop = maximumScroll > 0
                    ? Math.Clamp(SettingsScroll.ScrollY / maximumScroll * maximumThumbTravel, 0, maximumThumbTravel)
                    : 0;

                SettingsScrollBarThumb.HeightRequest = thumbHeight;
                SettingsScrollBarThumb.TranslationY = _settingsScrollBarThumbTop;
            }
            finally
            {
                _isUpdatingSettingsScrollBar = false;
            }

            if (refreshAfterLayout)
            {
                Dispatcher.Dispatch(UpdateSettingsScrollBar);
            }
        }

        /// <summary>
        /// Converts direct thumb dragging into a proportional content scroll position.
        /// </summary>
        private void OnSettingsScrollBarPanUpdated(object? sender, PanUpdatedEventArgs e)
        {
            var viewportHeight = SettingsScroll.Height;
            var contentHeight = Math.Max(SettingsScroll.ContentSize.Height, SettingsContentContainer.Height);
            var thumbHeight = SettingsScrollBarThumb.Height;
            if (viewportHeight <= 0 || contentHeight <= viewportHeight || thumbHeight <= 0)
            {
                return;
            }

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    _settingsScrollBarDragStart = _settingsScrollBarThumbTop;
                    break;
                case GestureStatus.Running:
                    var maximumThumbTravel = Math.Max(0, viewportHeight - thumbHeight);
                    var thumbTop = Math.Clamp(_settingsScrollBarDragStart + e.TotalY, 0, maximumThumbTravel);
                    var maximumScroll = Math.Max(0, contentHeight - viewportHeight);
                    var targetScroll = maximumThumbTravel > 0
                        ? thumbTop / maximumThumbTravel * maximumScroll
                        : 0;

                    _settingsScrollBarThumbTop = thumbTop;
                    SettingsScrollBarThumb.TranslationY = thumbTop;
                    _ = SettingsScroll.ScrollToAsync(0, targetScroll, false);
                    break;
                case GestureStatus.Completed:
                case GestureStatus.Canceled:
                    UpdateSettingsScrollBar();
                    break;
            }
        }

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
            if (!showModuleSettings)
            {
                HideModuleSectionNavigation();
            }

            EmptyCategoryState.IsVisible = showEmptyState;
        }
    }
}
