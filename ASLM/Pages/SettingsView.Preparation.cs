// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.Concurrent;
using ASLM.Controls.Settings;
using ASLM.Localization;
using ASLM.Models;
using Debug = System.Diagnostics.Debug;

namespace ASLM.Pages
{
    /// <summary>
    /// Shares incremental settings rendering between background preparation, navigation and module refreshes.
    /// </summary>
    public partial class SettingsView
    {
        // Preparation state

        /// <summary>
        /// Shares initial discovery between startup and the first explicit open.
        /// </summary>
        private Task? _initialLoadTask;

        /// <summary>
        /// Identifies the interactive lifetime independently from the persistent preparation host.
        /// </summary>
        private bool _isDialogOpen;

        /// <summary>
        /// Prevents overlapping discard confirmations after repeated close gestures.
        /// </summary>
        private bool _isClosing;

        /// <summary>
        /// Retains schema tasks, pending value keys and renderer cursors across priority changes.
        /// </summary>
        private readonly Dictionary<string, SettingsSurfaceWork> _surfaceWork = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Coalesces worker notifications before they reach the dispatcher.
        /// </summary>
        private readonly ConcurrentDictionary<(string Owner, string Key), PreparedSettingChangedEventArgs> _valueNotifications = new();

        /// <summary>
        /// Prevents duplicate notification flush callbacks.
        /// </summary>
        private int _valueNotificationScheduled;

        /// <summary>
        /// Prevents repeated navigation from scheduling concurrent renderer loops.
        /// </summary>
        private bool _surfaceStepScheduled;

        /// <summary>
        /// Serializes schema planning while letting the next selected section take precedence over queued plans.
        /// </summary>
        private Task? _schemaPreparationTask;

        /// <summary>
        /// Rejects deferred navigation after the user has selected another category.
        /// </summary>
        private long _moduleNavigationVersion;

        /// <summary>
        /// Tracks the selected tree without changing its native parent.
        /// </summary>
        private ModuleSettingsView? _visibleModuleSettingsSurface;

        /// <summary>
        /// Suppresses transient layout notifications while restoring a viewport.
        /// </summary>
        private bool _restoringSettingsScroll;

        /// <summary>
        /// Rejects obsolete delayed scroll restorations.
        /// </summary>
        private long _settingsScrollVersion;


        // Dialog lifetime

        /// <summary>
        /// Opens the category shell first and schedules cached or partial content in subsequent dispatcher turns.
        /// </summary>
        internal async Task OpenAsync(SettingsCategoryKind? category)
        {
            if (category.HasValue) _requestedCategory = category;
            if (_isDialogOpen)
            {
                ApplyRequestedCategory();
                return;
            }
            _isDialogOpen = true;
            await EnsureInitializedAsync();
            if (!_isDialogOpen) return;
            ApplyRequestedCategory();
            if (_activeCategory?.Module is { } module) RenderModuleCategory(module);
            if (_activeCategory?.Kind == SettingsCategoryKind.Accounts) StartOllamaMetadataRefresh();
            ResetSettingsScrollPosition();
        }

        /// <summary>
        /// Releases visible priority while preserving all preparation and partial row progress.
        /// </summary>
        internal void HideOverlay()
        {
            if (!_isDialogOpen) return;
            _isDialogOpen = false;
            _moduleNavigationVersion++;
            _settingsService.Preparation?.PrioritizeSettings(null);
            ParkModuleSettingsSurface();
            StopOllamaStatusPolling();
            StopOllamaMetadataRefresh();
            StopAslmAccountAction();
            _ollamaSettings.StopManagedRuntime();
        }

        /// <summary>
        /// Shares startup discovery and allows a later retry after failure.
        /// </summary>
        private Task EnsureInitializedAsync() => _hasLoaded ? Task.CompletedTask :
            _initialLoadTask is { IsCompleted: false } ? _initialLoadTask : _initialLoadTask = InitializeSettingsAsync();

        /// <summary>
        /// Discovers schemas without waiting for runtime values or complete native trees.
        /// </summary>
        private async Task InitializeSettingsAsync()
        {
            try
            {
                _isLoading = true;
                await LoadSettingsAsync();
                _hasLoaded = true;
                QueueModuleSurfacePreparation();
            }
            catch (Exception ex) { Debug.WriteLine($"Failed to initialize settings: {ex}"); }
            finally { _isLoading = false; }
        }


        // Schema and value preparation

        /// <summary>
        /// Ensures background work exists without restarting any completed rows or active getter pass.
        /// </summary>
        private void QueueModuleSurfacePreparation(ModuleConfig? module = null)
        {
            if (_moduleSurfaceBuildCancellation.IsCancellationRequested) return;
            foreach (var candidate in module == null ? _loadedModules : [module])
            {
                var key = candidate.SourcePath;
                if (!_surfaceWork.TryGetValue(key, out var work))
                {
                    _surfaceWork[key] = work = new(candidate);
                    StartSurfaceSchema(work, discover: !SettingsService.IsModuleEligibleForSettings(candidate));
                }
                if (work.SchemaTask == null && (work.Discovering || !_moduleSettingsPresentations.ContainsKey(key)))
                    StartSurfaceSchema(work, work.Discovering);
                if (_moduleSettingsPresentationsNeedingRefresh.Remove(key) &&
                    _moduleSettingsPresentations.TryGetValue(key, out var presentation))
                    foreach (var item in presentation.Sections.SelectMany(section => section.Settings))
                        work.DraftRefresh.Add(item.Draft.Setting.Key);
                if (!work.Discovering && SettingsService.IsModuleEligibleForSettings(candidate))
                    _ = _settingsService.CompleteModuleValuesAsync(candidate);
            }
            ScheduleSurfaceStep();
        }

        /// <summary>
        /// Builds schema descriptors on a worker; cached controls and values stay available while discovery runs.
        /// </summary>
        private void StartSurfaceSchema(SettingsSurfaceWork work, bool discover, bool force = false)
        {
            if (work.SchemaRequested && !force)
            {
                work.Discovering |= discover;
                return;
            }
            work.SchemaRequested = true;
            work.Discovering |= discover;
            work.SchemaRevision++;
            ScheduleSurfaceStep();
        }

        /// <summary>
        /// Starts only the selected schema plan; queued modules retain their existing controls until their own turn.
        /// </summary>
        private void StartSchemaTask(SettingsSurfaceWork work)
        {
            var module = work.Module;
            var discover = work.Discovering;
            var revision = work.SchemaRevision;
            work.RunningSchemaRevision = revision;
            work.SchemaRequested = false;
            var generation = _settingsService.Preparation?.GetModuleLifetime(module.SourcePath) ?? CancellationToken.None;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_moduleSurfaceBuildCancellation.Token, generation);
            work.SchemaTask = Task.Run(async () =>
            {
                try
                {
                    var token = lifetime.Token;
                    var schema = discover ? (await _settingsService.DiscoverModulesAsync(token).ConfigureAwait(false))
                        .FirstOrDefault(candidate => string.Equals(candidate.SourcePath, module.SourcePath,
                            StringComparison.OrdinalIgnoreCase)) : module;
                    token.ThrowIfCancellationRequested();
                    if (schema == null || !SettingsService.IsModuleEligibleForSettings(schema)) return null;
                    var draft = new ModuleSettingsDraft(schema);
                    foreach (var setting in draft.Settings)
                        if (_settingsService.GetPreparedValue(schema, setting.Setting) is { } value)
                            setting.LoadRuntimeValue(value.Value, value.AutomaticValue, value.IsReadOnly);
                    return new PreparedSurfaceSchema(schema,
                        SettingsPresentationBuilder.BuildModuleSections(draft, includeDependencyHiddenSettings: true), generation, revision);
                }
                finally { lifetime.Dispose(); }
            });
            _schemaPreparationTask = work.SchemaTask;
            _ = WakeAfterSchemaAsync(work.SchemaTask);
        }

        /// <summary>
        /// Wakes rendering after schema completion without retaining an unobserved task exception.
        /// </summary>
        private async Task WakeAfterSchemaAsync(Task<PreparedSurfaceSchema?> task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                // Full maintenance replaces the schema task without clearing its existing controls.
            }
            catch (Exception ex) { Debug.WriteLine($"Failed to prepare settings schema: {ex}"); }
            MainThread.BeginInvokeOnMainThread(ScheduleSurfaceStep);
        }

        /// <summary>
        /// Collects one successful value notification without scheduling one UI callback per getter.
        /// </summary>
        private void OnPreparedSettingValue(object? sender, PreparedSettingChangedEventArgs e)
        {
            if (e.Generation.IsCancellationRequested) return;
            _valueNotifications[(DataPreparationQueue.NormalizeOwner(e.SourcePath), e.SettingKey)] = e;
            if (Interlocked.Exchange(ref _valueNotificationScheduled, 1) == 0)
                MainThread.BeginInvokeOnMainThread(FlushPreparedSettingValues);
        }

        /// <summary>
        /// Marks existing editors dirty; newly created rows read the last-known cache when their turn arrives.
        /// </summary>
        private void FlushPreparedSettingValues()
        {
            Interlocked.Exchange(ref _valueNotificationScheduled, 0);
            foreach (var key in _valueNotifications.Keys)
                if (_valueNotifications.TryRemove(key, out var notification) && !notification.Generation.IsCancellationRequested &&
                    _surfaceWork.TryGetValue(notification.SourcePath, out var work) &&
                    _moduleSettingsPresentations.TryGetValue(notification.SourcePath, out var presentation) &&
                    presentation.ContainsSetting(notification.SettingKey))
                    work.PendingValues.Add(notification.SettingKey);
            ScheduleSurfaceStep();
        }

        /// <summary>
        /// Resumes deferred editor values after a local edit or focus change without restarting any getter.
        /// </summary>
        private void WakeModuleSurface(string key)
        {
            QueueActionButtonUpdate();
            if (!_surfaceWork.TryGetValue(key, out var work)) return;
            work.PendingValues.UnionWith(work.DeferredValues);
            work.DeferredValues.Clear();
            ScheduleSurfaceStep();
        }


        // Incremental dispatcher work

        /// <summary>
        /// Schedules one small UI operation and returns control to input and painting between operations.
        /// </summary>
        private void ScheduleSurfaceStep()
        {
            if (!_hasLoaded || _surfaceStepScheduled || _moduleSurfaceBuildCancellation.IsCancellationRequested) return;
            _surfaceStepScheduled = true;
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(1), RunSurfaceStep);
        }

        /// <summary>
        /// Chooses the visible section first while retaining every other module's cursor and pending values.
        /// </summary>
        private void RunSurfaceStep()
        {
            _surfaceStepScheduled = false;
            if (_moduleSurfaceBuildCancellation.IsCancellationRequested) return;
            var selected = _isDialogOpen ? _activeCategory?.Module?.SourcePath : null;
            var work = selected != null && _surfaceWork.TryGetValue(selected, out var preferred) && CanRenderSurface(preferred)
                ? preferred : _surfaceWork.Values.FirstOrDefault(CanRenderSurface);
            if (work == null) return;
            try { RenderSurfaceStep(work); }
            catch (Exception ex) { Debug.WriteLine($"Failed to render settings for '{work.Module.Name}': {ex}"); }
            if (_surfaceWork.Values.Any(CanRenderSurface)) ScheduleSurfaceStep();
        }

        /// <summary>
        /// Waits for maintenance asynchronously instead of repeatedly restarting cancelled schema plans.
        /// </summary>
        private bool CanStartSchema(SettingsSurfaceWork work) => work.SchemaRequested && work.SchemaTask == null &&
            _schemaPreparationTask is not { IsCompleted: false } &&
            !(_settingsService.Preparation?.GetModuleLifetime(work.Module.SourcePath).IsCancellationRequested ?? false);

        /// <summary>
        /// Detects available work without waiting for pending I/O or rereading a module.
        /// </summary>
        private bool CanRenderSurface(SettingsSurfaceWork work)
        {
            if (CanStartSchema(work))
                return !_isSaving;
            if (work.SchemaTask is { IsCompleted: true }) return !_isSaving;
            return work.PendingValues.Count > 0 || work.DraftRefresh.Count > 0 ||
                (_moduleSettingsPresentations.TryGetValue(work.Module.SourcePath, out var presentation) &&
                    ((presentation.HasRenderSteps && work.SchemaTask == null && !work.SchemaRequested) || (presentation.HasUnrevealedRows &&
                        _isDialogOpen && _activeCategory?.Module?.SourcePath == work.Module.SourcePath)));
        }

        /// <summary>
        /// Applies one schema, editor or value operation through the same path in foreground and background.
        /// </summary>
        private void RenderSurfaceStep(SettingsSurfaceWork work)
        {
            var key = work.Module.SourcePath;
            if (CanStartSchema(work))
            {
                StartSchemaTask(work);
                return;
            }

            // Validated schema metadata is published without dropping the rows of the preceding generation.
            if (work.SchemaTask is { IsCompleted: true } schemaTask)
            {
                work.SchemaTask = null;
                if (!schemaTask.IsCompletedSuccessfully)
                {
                    if (schemaTask.IsCanceled && !_moduleSurfaceBuildCancellation.IsCancellationRequested)
                        StartSurfaceSchema(work, work.Discovering);
                    return;
                }
                if (work.RunningSchemaRevision != work.SchemaRevision) return;
                if (schemaTask.Result is not { } schema)
                {
                    _loadedModules.RemoveAll(module => string.Equals(module.SourcePath, key, StringComparison.OrdinalIgnoreCase));
                    _editSession.RemoveModule(work.Module);
                    RemoveModuleSettingsSurface(key);
                    PublishModuleCategories();
                    return;
                }
                if (schema.Revision != work.SchemaRevision) return;
                if (schema.Generation.IsCancellationRequested)
                {
                    StartSurfaceSchema(work, work.Discovering);
                    return;
                }
                work.Discovering = false;
                work.Module = schema.Module;
                var draft = _editSession.ReconcileModule(schema.Module);
                SettingsService.RefreshModuleDraftVisibility(draft);
                _loadedModules.RemoveAll(module => string.Equals(module.SourcePath, key, StringComparison.OrdinalIgnoreCase));
                _loadedModules.Add(schema.Module);
                if (!_moduleSettingsPresentations.TryGetValue(key, out var model))
                {
                    model = new ModuleSettingsPageViewModel(() => WakeModuleSurface(key), OnModuleSettingsSectionRequested);
                    _moduleSettingsPresentations[key] = model;
                    var view = new ModuleSettingsView { BindingContext = model, IsVisible = false };
                    _moduleSettingsViews[key] = view;
                    ModuleSettingsContainer.Children.Add(view);
                }
                model.BeginReconcile(draft, schema.Sections, L.Get(LocalizationKeys.Settings_Engine_Installed),
                    L.Get(LocalizationKeys.Settings_Engine_NotInstalled));
                PublishModuleCategories();
                if (_isDialogOpen && _activeCategory?.Module?.SourcePath == key) RenderModuleCategory(schema.Module);
                _ = _settingsService.CompleteModuleValuesAsync(schema.Module);
                return;
            }
            if (!_moduleSettingsPresentations.TryGetValue(key, out var presentation)) return;
            var revealing = presentation.HasUnrevealedRows && _isDialogOpen && _activeCategory?.Module?.SourcePath == key;
            // Local drafts and completed values update only their corresponding existing editor.
            if (work.DraftRefresh.Count > 0 && !revealing)
            {
                var settingKey = work.DraftRefresh.First();
                work.DraftRefresh.Remove(settingKey);
                presentation.RefreshSettingFromDraft(settingKey, refreshDependencies: work.DraftRefresh.Count == 0);
                return;
            }
            if (work.PendingValues.Count > 0 && !revealing)
            {
                var settingKey = work.PendingValues.First();
                work.PendingValues.Remove(settingKey);
                var definition = work.Module.Settings.FirstOrDefault(setting => setting.Key == settingKey);
                if (definition != null && _settingsService.GetPreparedValue(work.Module, definition) is { } value &&
                    !presentation.ApplyPreparedValue(value)) work.DeferredValues.Add(settingKey);
                return;
            }

            // Preserve a reading anchor before structural updates; first-time rows still appear incrementally.
            var anchor = !_restoringSettingsScroll && SettingsScroll.ScrollY > 0 &&
                _visibleModuleSettingsSurface?.BindingContext == presentation
                ? _visibleModuleSettingsSurface.CaptureScrollAnchor(SettingsScroll.ScrollY) : null;
            presentation.RenderNext(_isDialogOpen && _activeCategory?.Module?.SourcePath == key);
            if (presentation.LastRenderedKey is { } rendered)
            {
                var definition = work.Module.Settings.FirstOrDefault(setting => setting.Key == rendered);
                if (definition != null && _settingsService.GetPreparedValue(work.Module, definition) is { } value &&
                    !presentation.ApplyPreparedValue(value)) work.DeferredValues.Add(rendered);
            }
            if (_visibleModuleSettingsSurface?.BindingContext == presentation)
            {
                ShowModuleSectionNavigation(presentation);
                UpdateSettingsScrollBar();
                if (anchor?.SettingKey != null && presentation.LastStepChangedLayout)
                    _ = RestoreModuleScrollAsync(_visibleModuleSettingsSurface, anchor);
            }
        }

        /// <summary>
        /// Reconciles sidebar membership after validated schema changes without resetting the reading position.
        /// </summary>
        private void PublishModuleCategories()
        {
            var categories = SettingsPresentationBuilder.BuildCategories(_loadedModules).ToList();
            var changed = !_categories.Select(category => (category.Id, category.Title))
                .SequenceEqual(categories.Select(category => (category.Id, category.Title)));
            _categories = categories;
            var active = ResolveCategory(_activeCategory?.Id);
            if (changed) BuildCategorySelectors();
            if (active != null)
            {
                _activeCategory = active;
                ActiveCategoryTitleLabel.Text = GetLocalizedCategoryTitle(active);
                UpdateSelectorButtonStates();
            }
            else if (_categories.FirstOrDefault() is { } fallback) ActivateCategory(fallback);
        }

        /// <summary>
        /// Retains unfinished operations across navigation and values-only invalidation.
        /// </summary>
        private sealed class SettingsSurfaceWork(ModuleConfig module)
        {
            /// <summary>
            /// Gets or sets the latest validated module definition.
            /// </summary>
            public ModuleConfig Module { get; set; } = module;

            /// <summary>
            /// Gets or sets the asynchronous schema plan independently from the existing rendered rows.
            /// </summary>
            public Task<PreparedSurfaceSchema?>? SchemaTask { get; set; }

            /// <summary>
            /// Prevents an old schema from starting a new getter generation while replacement definitions are pending.
            /// </summary>
            public bool Discovering { get; set; }

            /// <summary>
            /// Gets or sets whether a schema plan is queued independently from a running renderer or value pass.
            /// </summary>
            public bool SchemaRequested { get; set; }

            /// <summary>
            /// Rejects completed plans superseded by a newer content change.
            /// </summary>
            public long SchemaRevision { get; set; }

            /// <summary>
            /// Identifies the request that owns the running schema task, including a result that removes a module.
            /// </summary>
            public long RunningSchemaRevision { get; set; }




            /// <summary>
            /// Gets runtime values ready to apply to individual rows.
            /// </summary>
            public HashSet<string> PendingValues { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Gets values deferred while their editor owns focus.
            /// </summary>
            public HashSet<string> DeferredValues { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Gets local reset or discard changes waiting for their existing editor to refresh.
            /// </summary>
            public HashSet<string> DraftRefresh { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Carries immutable schema grouping built on a worker before rows are reconciled on the dispatcher.
        /// </summary>
        /// <param name="Module">Latest validated module definition.</param>
        /// <param name="Sections">Section ordering and setting keys consumed by the incremental renderer.</param>
        /// <param name="Generation">Lifetime that rejects schemas superseded by full maintenance.</param>
        /// <param name="Revision">Revision captured before planning began.</param>
        private sealed record PreparedSurfaceSchema(ModuleConfig Module,
            IReadOnlyList<ModuleSettingsSectionPresentation> Sections, CancellationToken Generation, long Revision);


        // Attached control ownership

        /// <summary>
        /// Hides the previously selected tree without changing its native parent or retaining navigation scroll state.
        /// </summary>
        private void ParkModuleSettingsSurface()
        {
            if (_visibleModuleSettingsSurface is not { } previous) return;
            _settingsScrollVersion++;
            _restoringSettingsScroll = false;
            previous.IsVisible = false;
            _visibleModuleSettingsSurface = null;
        }

        /// <summary>
        /// Reveals prepared content from the beginning without synchronous layout.
        /// </summary>
        private void ShowPreparedModuleSettingsSurface(ModuleSettingsView view)
        {
            if (ReferenceEquals(_visibleModuleSettingsSurface, view)) return;
            ParkModuleSettingsSurface();
            view.IsVisible = true;
            _visibleModuleSettingsSurface = view;
            ResetSettingsScrollPosition();
        }

        /// <summary>
        /// Restores the same setting after layout accepts updated content without forcing a layout pass.
        /// </summary>
        private async Task RestoreModuleScrollAsync(ModuleSettingsView view, ModuleSettingsScrollAnchor anchor)
        {
            var version = ++_settingsScrollVersion;
            _restoringSettingsScroll = true;
            try
            {
                await Task.Delay(16, _moduleSurfaceBuildCancellation.Token);
                if (version != _settingsScrollVersion || !ReferenceEquals(_visibleModuleSettingsSurface, view)) return;
                await SettingsScroll.ScrollToAsync(0, view.ResolveScrollOffset(anchor), false);
            }
            catch (OperationCanceledException)
            {
                // A new locale or window destruction discards the old restoration request.
            }
            catch (Exception ex) { Debug.WriteLine($"Failed to restore settings scroll position: {ex}"); }
            finally
            {
                if (version == _settingsScrollVersion) _restoringSettingsScroll = false;
            }
        }

        /// <summary>
        /// Releases the viewport of a removed module rather than discarding valid pages during reindexing.
        /// </summary>
        private void RemoveModuleSettingsSurface(string key)
        {
            if (_moduleSettingsViews.Remove(key, out var view))
            {
                if (ReferenceEquals(_visibleModuleSettingsSurface, view)) ParkModuleSettingsSurface();
                ModuleSettingsContainer.Children.Remove(view);
            }
            _moduleSettingsPresentations.Remove(key);
            _moduleSettingsPresentationsNeedingRefresh.Remove(key);
            _surfaceWork.Remove(key);
        }
    }
}
