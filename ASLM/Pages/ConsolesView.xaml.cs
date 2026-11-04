// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    // Consoles page

    /// <summary>
    /// Displays module consoles, per-process sessions, and merged console output.
    /// The view only renders; a <see cref="ConsolesPresenter"/> builds the state and reacts to the console store.
    /// </summary>
    public partial class ConsolesView : ContentView, IConsolesView, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Page width in device-independent units below which the workspace stacks its panels vertically.
        /// </summary>
        private const double CompactBreakpoint = 1180;

        /// <summary>
        /// Delay between periodic dashboard refreshes while the view is loaded.
        /// </summary>
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Bindable state of the page; the presenter output is copied into it by <see cref="IConsolesView.Render"/>.
        /// </summary>
        private readonly ConsolesPageViewModel _viewModel = new();

        /// <summary>
        /// Presenter that builds dashboard state and handles selection and refresh requests.
        /// </summary>
        private readonly ConsolesPresenter _presenter;

        /// <summary>
        /// Localization service used to re-apply texts when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Set while <see cref="IConsolesView.Render"/> changes the list selections, so those changes are not
        /// treated as user selections.
        /// </summary>
        private bool _suppressSelection;

        /// <summary>
        /// Non-zero while delayed layout refresh passes are scheduled; accessed through <see cref="Interlocked"/>.
        /// </summary>
        private int _layoutRefreshQueued;

        /// <summary>
        /// Cancellation source of the periodic refresh loop, or null while the loop is stopped.
        /// </summary>
        private CancellationTokenSource? _autoRefreshCts;


        // Initialization

        /// <summary>
        /// Creates the consoles view and initializes its responsive shell layout.
        /// </summary>
        /// <param name="moduleInstaller">Installer used to discover the installed modules.</param>
        /// <param name="consoleStore">Store that holds the console sessions and their output.</param>
        /// <param name="appData">Application data that holds the console display preferences.</param>
        /// <param name="localization">Localization service whose language changes re-apply the texts.</param>
        public ConsolesView(
            ModuleInstaller moduleInstaller,
            ModuleConsoleStore consoleStore,
            AppDataStore appData,
            AppLocalizationService localization)
        {
            InitializeComponent();

            BindingContext = _viewModel;
            _localization = localization;

            _presenter = new ConsolesPresenter(this, moduleInstaller, consoleStore, appData);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            LocalizableAttach.Hook(this, _localization, this);

            UpdateResponsiveLayout();
        }


        // Localization

        /// <summary>
        /// Applies localized strings to the page chrome and refreshes the dashboard so generated texts are rebuilt.
        /// </summary>
        public void ApplyLocalization()
        {
            PageTitleLabel.Text = L.Get(LocalizationKeys.Consoles_Title);
            ModulesHeaderLabel.Text = L.Get(LocalizationKeys.Consoles_ModulesHeader);
            SessionsHeaderLabel.Text = L.Get(LocalizationKeys.Consoles_Title);

            ModulesEmptyLabel.Text = L.Get(LocalizationKeys.Consoles_NoActiveModules);
            SessionsEmptyLabel.Text = L.Get(LocalizationKeys.Consoles_NoConsoles);

            _ = _presenter.RefreshAsync();
        }


        // Public API

        /// <summary>
        /// Requests a presenter-driven refresh of the current consoles dashboard.
        /// </summary>
        internal Task RefreshAsync()
        {
            return _presenter.RefreshAsync();
        }

        /// <summary>
        /// Opens the requested module's merged output regardless of the previously selected process.
        /// </summary>
        /// <param name="sourcePath">Manifest source path that identifies the module to show.</param>
        internal Task ShowModuleAsync(string sourcePath)
        {
            return _presenter.ShowModuleAsync(sourcePath);
        }


        // Lifetime events

        /// <summary>
        /// Activates the presenter and refreshes the measured output layout when the view loads.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            await _presenter.ActivateAsync();
            StartAutoRefresh();
            QueueConsoleLayoutRefresh();
        }

        /// <summary>
        /// Deactivates the presenter when the view leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            StopAutoRefresh();
            _presenter.Deactivate();
        }


        // Layout events

        /// <summary>
        /// Rebuilds the responsive workspace layout and refreshes console sizing after a resize.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e)
        {
            UpdateResponsiveLayout();
            QueueConsoleLayoutRefresh();
        }


        // Selection events

        /// <summary>
        /// Selects the requested module unless the selection is being synchronized programmatically.
        /// </summary>
        private async void OnModuleSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection)
            {
                return;
            }

            if (e.CurrentSelection.FirstOrDefault() is ConsoleModuleItemViewModel module)
            {
                await _presenter.SelectModuleAsync(module.SourcePath);
            }
        }

        /// <summary>
        /// Selects the requested console session unless the selection is being synchronized programmatically.
        /// </summary>
        private async void OnSessionSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelection)
            {
                return;
            }

            if (e.CurrentSelection.FirstOrDefault() is ConsoleSessionItemViewModel session)
            {
                await _presenter.SelectSessionAsync(session.Id);
            }
        }


        // Rendering

        /// <summary>
        /// Applies the latest presenter state to the view model and synchronized selections.
        /// </summary>
        void IConsolesView.Render(ConsolesDashboardState state)
        {
            // Selection changes made below come from the presenter, so they must not be fed back as user input.
            _suppressSelection = true;

            // Copy the selected session's texts into the bindable view model.
            _viewModel.SelectedSessionId = state.SelectedSessionId;
            _viewModel.SelectedSessionKey = state.SelectedSessionKey;
            _viewModel.SelectedSessionTitle = state.SelectedSessionTitle;
            _viewModel.SelectedSessionStatus = state.SelectedSessionStatus;
            _viewModel.SelectedSessionDescription = state.SelectedSessionDescription;
            _viewModel.SelectedSessionCommandLine = state.SelectedSessionCommandLine;
            _viewModel.SelectedSessionFooter = state.SelectedSessionFooter;
            _viewModel.SelectedSessionText = string.Join(Environment.NewLine, state.SelectedSessionLines);

            // Update the lists in place and point both collection views at the presenter's selection.
            SyncModuleItems(_viewModel.Modules, state.Modules);
            SyncSessionItems(_viewModel.Sessions, state.Sessions);

            var selectedModule = _viewModel.Modules.FirstOrDefault(module => module.SourcePath == state.SelectedModuleSourcePath);
            if (!ReferenceEquals(ModulesCollection.SelectedItem, selectedModule))
            {
                ModulesCollection.SelectedItem = selectedModule;
            }

            var selectedSession = _viewModel.Sessions.FirstOrDefault(session => session.Id == state.SelectedSessionId);
            if (!ReferenceEquals(SessionsCollection.SelectedItem, selectedSession))
            {
                SessionsCollection.SelectedItem = selectedSession;
            }

            // Show the session list only when there is more than one console to pick from, then re-measure.
            SessionsPanel.IsVisible = state.ShowSessionList;
            UpdateResponsiveLayout(state.ShowSessionList);
            QueueConsoleLayoutRefresh();
            _suppressSelection = false;
        }


        // Auto refresh

        /// <summary>
        /// Starts the lightweight periodic refresh used instead of a manual refresh button.
        /// </summary>
        private void StartAutoRefresh()
        {
            StopAutoRefresh();
            var cts = new CancellationTokenSource();
            _autoRefreshCts = cts;
            _ = RunAutoRefreshAsync(cts.Token);
        }

        /// <summary>
        /// Stops the periodic refresh loop when the page unloads.
        /// </summary>
        private void StopAutoRefresh()
        {
            _autoRefreshCts?.Cancel();
            _autoRefreshCts?.Dispose();
            _autoRefreshCts = null;
        }

        /// <summary>
        /// Refreshes the dashboard on a fixed interval while the view is visible.
        /// </summary>
        /// <param name="cancellationToken">Cancelled by <see cref="StopAutoRefresh"/> to end the loop.</param>
        private async Task RunAutoRefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(AutoRefreshInterval, cancellationToken);
                    await _presenter.RefreshAsync(forceModuleReload: false);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal way to stop the loop when the view unloads.
            }
        }


        // Responsive layout

        /// <summary>
        /// Switches the workspace between the compact stacked layout and the wide three-column layout.
        /// </summary>
        /// <param name="showSessionList">
        /// Whether the sessions panel takes part in the layout; null uses the current visibility of the panel.
        /// </param>
        private void UpdateResponsiveLayout(bool? showSessionList = null)
        {
            // A desktop sidebar can make this page narrower than the breakpoint without making the
            // application window compact. Honor the host's minimum width before stacking console panels.
            var minimumWindowWidth = Window?.MinimumWidth ?? 0;
            var isCompact = Width > 0 && Width < CompactBreakpoint && minimumWindowWidth < CompactBreakpoint;
            var includeSessions = showSessionList ?? SessionsPanel.IsVisible;

            // The grid definitions are rebuilt from scratch for the chosen layout.
            WorkspaceLayout.ColumnDefinitions.Clear();
            WorkspaceLayout.RowDefinitions.Clear();

            if (isCompact)
            {
                // Compact: one column with modules, optional sessions and output stacked in rows.
                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                if (includeSessions)
                {
                    WorkspaceLayout.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
                }

                Grid.SetRow(ModulesPanel, 0);
                Grid.SetColumn(ModulesPanel, 0);
                Grid.SetRow(SessionsPanel, includeSessions ? 1 : 0);
                Grid.SetColumn(SessionsPanel, 0);
                Grid.SetRow(OutputPanel, includeSessions ? 2 : 1);
                Grid.SetColumn(OutputPanel, 0);
            }
            else
            {
                // Wide: fixed-width module and session columns beside a flexible output column.
                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = 260 });
                if (includeSessions)
                {
                    WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = 260 });
                }

                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid.SetRow(ModulesPanel, 0);
                Grid.SetColumn(ModulesPanel, 0);
                Grid.SetRow(SessionsPanel, 0);
                Grid.SetColumn(SessionsPanel, includeSessions ? 1 : 0);
                Grid.SetRow(OutputPanel, 0);
                Grid.SetColumn(OutputPanel, includeSessions ? 2 : 1);
            }
        }

        /// <summary>
        /// Schedules several delayed layout refresh passes so the native console host can settle after updates.
        /// </summary>
        private void QueueConsoleLayoutRefresh()
        {
            // Refresh immediately; the delayed passes below catch what the native host measures late.
            RefreshConsoleLayout();

            // Only one set of delayed passes may be pending, no matter how many resizes or renders request one.
            if (Dispatcher == null || Interlocked.Exchange(ref _layoutRefreshQueued, 1) == 1)
            {
                return;
            }

            // The last pass clears the flag so the next request can schedule a new set.
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), RefreshConsoleLayout);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(48), () =>
            {
                RefreshConsoleLayout();
                Interlocked.Exchange(ref _layoutRefreshQueued, 0);
            });
        }

        /// <summary>
        /// Invalidates the page and console containers so the output region is remeasured.
        /// </summary>
        private void RefreshConsoleLayout()
        {
            InvalidateMeasure();
            WorkspaceLayout.InvalidateMeasure();
            ModulesPanel.InvalidateMeasure();
            SessionsPanel.InvalidateMeasure();
            OutputPanel.InvalidateMeasure();
            ConsoleOutputHost.InvalidateMeasure();
        }


        // Collection synchronization

        /// <summary>
        /// Synchronizes the module item view models with the latest presenter state, keyed by module source path.
        /// </summary>
        /// <param name="target">Module items bound to the module list.</param>
        /// <param name="source">Module items prepared by the presenter.</param>
        private static void SyncModuleItems(ObservableCollection<ConsoleModuleItemViewModel> target, IReadOnlyList<ConsoleModuleItemViewModel> source)
        {
            SyncKeyedItems(
                target,
                source,
                static item => item.SourcePath,
                static (targetItem, sourceItem) => targetItem.UpdateFrom(sourceItem));
        }

        /// <summary>
        /// Synchronizes the session item view models with the latest presenter state, keyed by session id.
        /// </summary>
        /// <param name="target">Session items bound to the session list.</param>
        /// <param name="source">Session items prepared by the presenter.</param>
        private static void SyncSessionItems(ObservableCollection<ConsoleSessionItemViewModel> target, IReadOnlyList<ConsoleSessionItemViewModel> source)
        {
            SyncKeyedItems(
                target,
                source,
                static item => item.Id,
                static (targetItem, sourceItem) => targetItem.UpdateFrom(sourceItem));
        }

        /// <summary>
        /// Updates an observable collection in place by key so item instances stay stable for the UI.
        /// </summary>
        /// <param name="target">Collection bound to the UI; it is changed to match <paramref name="source"/>.</param>
        /// <param name="source">Desired items in display order.</param>
        /// <param name="keySelector">Returns the stable key that identifies an item.</param>
        /// <param name="updateExisting">Copies the state of a source item into the existing target item.</param>
        private static void SyncKeyedItems<T>(
            ObservableCollection<T> target,
            IReadOnlyList<T> source,
            Func<T, string> keySelector,
            Action<T, T> updateExisting)
            where T : class
        {
            var indexByKey = BuildIndex(target, keySelector);

            // Walk the desired order and make the target match position by position.
            for (var index = 0; index < source.Count; index++)
            {
                var sourceItem = source[index];
                var sourceKey = keySelector(sourceItem);

                // Already in place: only refresh its state.
                if (index < target.Count &&
                    string.Equals(keySelector(target[index]), sourceKey, StringComparison.Ordinal))
                {
                    updateExisting(target[index], sourceItem);
                    continue;
                }

                // Present further down: move it up instead of recreating it; otherwise it is new.
                // The index map is rebuilt after each structural change because positions shifted.
                if (indexByKey.TryGetValue(sourceKey, out var existingIndex) && existingIndex >= index)
                {
                    var existingItem = target[existingIndex];
                    target.Move(existingIndex, index);
                    updateExisting(existingItem, sourceItem);
                    indexByKey = BuildIndex(target, keySelector);
                }
                else
                {
                    target.Insert(index, sourceItem);
                    indexByKey = BuildIndex(target, keySelector);
                }
            }

            // Drop items that are no longer in the source.
            while (target.Count > source.Count)
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        /// <summary>
        /// Builds a key-to-index map for the current observable collection contents.
        /// </summary>
        /// <param name="target">Collection to index.</param>
        /// <param name="keySelector">Returns the key of an item.</param>
        private static Dictionary<string, int> BuildIndex<T>(ObservableCollection<T> target, Func<T, string> keySelector)
            where T : class
        {
            var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < target.Count; index++)
            {
                indexByKey[keySelector(target[index])] = index;
            }

            return indexByKey;
        }
    }


    // Presenter and view contract

    /// <summary>
    /// Defines the rendering contract used by the consoles presenter.
    /// </summary>
    internal interface IConsolesView
    {
        /// <summary>
        /// Applies a fully prepared dashboard state to the view.
        /// </summary>
        /// <param name="state">Complete dashboard state built by the presenter.</param>
        void Render(ConsolesDashboardState state);
    }

    /// <summary>
    /// Builds dashboard state for the consoles view and coordinates selections with the console store.
    /// </summary>
    internal sealed class ConsolesPresenter
    {
        // Fields and constants

        /// <summary>
        /// Module source path of the synthetic "all modules" entry that merges the output of every active module.
        /// </summary>
        private const string AllModulesModuleId = "__all_modules__";

        /// <summary>
        /// Session id of the single merged console shown for the "all modules" entry.
        /// </summary>
        private const string GlobalUnifiedSessionId = "__all_modules_unified__";

        /// <summary>
        /// Session id of the merged console of one module, listed before its individual process consoles.
        /// </summary>
        private const string UnifiedSessionId = "__module_unified__";

        /// <summary>
        /// View that receives the dashboard state.
        /// </summary>
        private readonly IConsolesView _view;

        /// <summary>
        /// Installer used to discover installed modules.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Store that holds the console sessions and output of all modules.
        /// </summary>
        private readonly ModuleConsoleStore _consoleStore;

        /// <summary>
        /// Application data that persists the console display preferences.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Serializes refreshes so only one builds and renders state at a time.
        /// </summary>
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        /// <summary>
        /// Installed modules from the last discovery, reused until a forced reload.
        /// </summary>
        private List<ModuleConfig> _knownModules = [];

        /// <summary>
        /// Whether the presenter is subscribed to console store changes.
        /// </summary>
        private bool _isActive;

        /// <summary>
        /// Non-zero while a debounced refresh triggered by a console store change is pending;
        /// accessed through <see cref="Interlocked"/>.
        /// </summary>
        private int _refreshQueued;

        /// <summary>
        /// Whether finished process sessions stay listed; mirrors the persisted console preference.
        /// </summary>
        private bool _showCompletedProcesses;

        /// <summary>
        /// Source path of the selected module (or the "all modules" entry), or null when nothing is selected.
        /// </summary>
        private string? _selectedModuleSourcePath;

        /// <summary>
        /// Id of the selected session inside the selected module, or null to pick a default.
        /// </summary>
        private string? _selectedSessionId;


        // Initialization

        /// <summary>
        /// Creates the presenter for the consoles dashboard.
        /// </summary>
        /// <param name="view">View that renders the dashboard states.</param>
        /// <param name="moduleInstaller">Installer used to discover installed modules.</param>
        /// <param name="consoleStore">Store that holds the console sessions and output.</param>
        /// <param name="appData">Application data that holds the console display preferences.</param>
        public ConsolesPresenter(IConsolesView view, ModuleInstaller moduleInstaller, ModuleConsoleStore consoleStore, AppDataStore appData)
        {
            _view = view;
            _moduleInstaller = moduleInstaller;
            _consoleStore = consoleStore;
            _appData = appData;
            _showCompletedProcesses = _appData.Data.Consoles.ShowCompletedProcesses;
        }


        // Lifetime

        /// <summary>
        /// Starts listening for console store changes and renders the initial dashboard state.
        /// </summary>
        public async Task ActivateAsync()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            _consoleStore.StateChanged += OnConsoleStateChanged;
            LoadPreferences();
            await RefreshAsync(forceModuleReload: true);
        }

        /// <summary>
        /// Stops listening for console store changes.
        /// </summary>
        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _consoleStore.StateChanged -= OnConsoleStateChanged;
            _isActive = false;
        }


        // Selection

        /// <summary>
        /// Selects a module and resets the session selection to that module's default console.
        /// </summary>
        /// <param name="sourcePath">Source path of the module, or the "all modules" entry, chosen by the user.</param>
        public async Task SelectModuleAsync(string sourcePath)
        {
            if (string.Equals(_selectedModuleSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _selectedModuleSourcePath = sourcePath;
            _selectedSessionId = null;
            await RefreshAsync(forceModuleReload: false);
        }

        /// <summary>
        /// Opens a module's merged console even when its individual process was previously selected.
        /// </summary>
        /// <param name="sourcePath">Source path of the module to show.</param>
        public Task ShowModuleAsync(string sourcePath)
        {
            // Shell navigation already refreshes module metadata; this pass only changes the selection.
            return RefreshAsync(forceModuleReload: false, moduleSourcePath: sourcePath);
        }

        /// <summary>
        /// Selects a console session inside the currently active module scope.
        /// </summary>
        /// <param name="sessionId">Id of the session item chosen by the user.</param>
        public async Task SelectSessionAsync(string sessionId)
        {
            if (string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }

            _selectedSessionId = sessionId;
            await RefreshAsync(forceModuleReload: false);
        }


        // Refresh

        /// <summary>
        /// Rediscovers installed modules and rerenders the dashboard.
        /// </summary>
        public Task RefreshAsync()
        {
            return RefreshAsync(forceModuleReload: true);
        }

        /// <summary>
        /// Reloads module metadata when requested, snapshots console state, and renders the result.
        /// </summary>
        /// <param name="forceModuleReload">
        /// True to rediscover installed modules; false reuses the last discovery unless it found none.
        /// </param>
        public Task RefreshAsync(bool forceModuleReload)
        {
            return RefreshAsync(forceModuleReload, moduleSourcePath: null);
        }

        /// <summary>
        /// Serializes refreshes and applies an optional merged-console shortcut before building state.
        /// </summary>
        /// <param name="forceModuleReload">True to rediscover installed modules before building the state.</param>
        /// <param name="moduleSourcePath">
        /// Module whose merged console should be selected, or null to keep the current selection.
        /// </param>
        private async Task RefreshAsync(bool forceModuleReload, string? moduleSourcePath)
        {
            await _refreshLock.WaitAsync();
            try
            {
                // Apply external selection under the refresh lock so an older refresh cannot reset it.
                if (!string.IsNullOrWhiteSpace(moduleSourcePath))
                {
                    _selectedModuleSourcePath = moduleSourcePath;
                    _selectedSessionId = UnifiedSessionId;
                }

                LoadPreferences();

                // Rediscover modules when asked to, or when none are known yet.
                if (forceModuleReload || _knownModules.Count == 0)
                {
                    _knownModules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync());
                }

                // Build the state off the UI thread, then render it on the main thread.
                var state = await Task.Run(() =>
                {
                    _consoleStore.EnsureModules(_knownModules);
                    var snapshots = _consoleStore.GetSnapshot();
                    return BuildState(snapshots);
                });

                await MainThread.InvokeOnMainThreadAsync(() => _view.Render(state));
            }
            finally
            {
                _refreshLock.Release();
            }
        }


        // Console events

        /// <summary>
        /// Coalesces bursts of console change notifications into a single refresh.
        /// </summary>
        private void OnConsoleStateChanged(object? sender, EventArgs e)
        {
            // Ignore changes while inactive, and while a refresh for an earlier change is already pending.
            if (!_isActive || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            {
                return;
            }

            // Wait briefly so a burst of output lines collapses into one refresh; changes arriving after the flag
            // is cleared schedule the next one.
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync(forceModuleReload: false);
            });
        }


        // Preferences

        /// <summary>
        /// Normalizes the persisted console preferences and re-reads whether finished processes stay listed.
        /// </summary>
        private void LoadPreferences()
        {
            _appData.Data.Consoles.Normalize();
            _showCompletedProcesses = _appData.Data.Consoles.ShowCompletedProcesses;
        }


        // State building

        /// <summary>
        /// Builds the full dashboard state for the current module and session selection.
        /// Corrects the stored selection when it no longer exists, so the result always points at something valid.
        /// </summary>
        /// <param name="snapshots">Console store snapshot of every known module.</param>
        private ConsolesDashboardState BuildState(IReadOnlyList<ModuleConsoleModuleSnapshot> snapshots)
        {
            // Retain the explicitly selected module's history even when it is currently stopped.
            var visibleModules = snapshots
                .Where(module => module.IsEnabled || module.ActiveProcessCount > 0 ||
                    string.Equals(module.SourcePath, _selectedModuleSourcePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var activeModules = snapshots
                .Where(module => module.IsEnabled || module.ActiveProcessCount > 0)
                .ToList();

            // Module list: the "all modules" entry first (only when something is active), then each visible module.
            var moduleItems = new List<ConsoleModuleItemViewModel>();
            if (activeModules.Count > 0)
            {
                moduleItems.Add(new ConsoleModuleItemViewModel
                {
                    SourcePath = AllModulesModuleId,
                    Name = L.Get(LocalizationKeys.Consoles_AllModules),
                    StatusText = L.Get(LocalizationKeys.Consoles_ActiveModulesFormat, activeModules.Count),
                    ActivityText = L.Get(LocalizationKeys.Consoles_UnifiedConsoleForActive)
                });
            }

            moduleItems.AddRange(visibleModules.Select(MapModule));

            // Module selection: clear it when the list is empty, and fall back to the first item when the stored
            // selection is missing or no longer listed.
            if (moduleItems.Count == 0)
            {
                _selectedModuleSourcePath = null;
            }
            else if (string.IsNullOrWhiteSpace(_selectedModuleSourcePath) ||
                     !moduleItems.Any(module => string.Equals(module.SourcePath, _selectedModuleSourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedModuleSourcePath = moduleItems[0].SourcePath;
            }

            // Resolve the selected scope: the merged "all modules" entry or one concrete module.
            var isGlobalModule = string.Equals(_selectedModuleSourcePath, AllModulesModuleId, StringComparison.Ordinal);
            var selectedModule = visibleModules.FirstOrDefault(module =>
                string.Equals(module.SourcePath, _selectedModuleSourcePath, StringComparison.OrdinalIgnoreCase));
            var activeModulePaths = activeModules.Select(module => module.SourcePath).ToList();
            var showIndividualConsoles = _appData.Data.Consoles.ShowIndividualModuleConsoles;

            IReadOnlyList<ConsoleSessionItemViewModel> sessionItems;
            IReadOnlyList<string> selectedSessionLines;
            string selectedSessionTitle;
            string selectedSessionStatus;
            string selectedSessionDescription;
            string selectedSessionCommandLine;
            string selectedSessionFooter;

            if (isGlobalModule)
            {
                // All modules: a single merged console over every active module.
                _selectedSessionId = GlobalUnifiedSessionId;
                sessionItems =
                [
                    new ConsoleSessionItemViewModel
                    {
                        Id = GlobalUnifiedSessionId,
                        ModuleSourcePath = AllModulesModuleId,
                        SessionSourceId = GlobalUnifiedSessionId,
                        Title = L.Get(LocalizationKeys.Consoles_UnifiedConsole),
                        StatusText = L.Get(LocalizationKeys.Consoles_AllActiveModules),
                        Preview = L.Get(LocalizationKeys.Consoles_UnifiedPreview)
                    }
                ];

                selectedSessionLines = _consoleStore.GetUnifiedOverviewLines(activeModulePaths);
                selectedSessionTitle = L.Get(LocalizationKeys.Consoles_AllModulesUnifiedTitle);
                selectedSessionStatus = string.Empty;
                selectedSessionDescription = string.Empty;
                selectedSessionCommandLine = string.Empty;
                selectedSessionFooter = $"{selectedSessionLines.Count} visible lines";
            }
            else if (selectedModule == null)
            {
                // Nothing selected: show the empty-selection placeholders.
                _selectedSessionId = null;
                sessionItems = [];
                selectedSessionLines = [L.Get(LocalizationKeys.Consoles_NoOutputYet)];
                selectedSessionTitle = L.Get(LocalizationKeys.Consoles_EmptySelectionTitle);
                selectedSessionStatus = L.Get(LocalizationKeys.Consoles_EmptySelectionStatus);
                selectedSessionDescription = L.Get(LocalizationKeys.Consoles_EmptySelectionDescription);
                selectedSessionCommandLine = string.Empty;
                selectedSessionFooter = L.Get(LocalizationKeys.Consoles_EmptySelectionFooter);
            }
            else
            {
                // One module: list its process sessions, hiding the overview, observed processes and, depending on
                // the preference, finished ones.
                var visibleSessions = selectedModule.Sessions
                    .Where(session => !string.Equals(session.Id, "overview", StringComparison.OrdinalIgnoreCase))
                    .Where(session => !session.IsObservedProcess)
                    .Where(session => _showCompletedProcesses || session.IsRunning)
                    .ToList();

                // The module's merged console is always first; individual consoles follow when the preference allows.
                var unifiedItem = new ConsoleSessionItemViewModel
                {
                    Id = UnifiedSessionId,
                    ModuleSourcePath = selectedModule.SourcePath,
                    SessionSourceId = UnifiedSessionId,
                    Title = L.Get(LocalizationKeys.Consoles_UnifiedConsole),
                    StatusText = L.Get(LocalizationKeys.Consoles_MergedOutput),
                    Preview = L.Get(LocalizationKeys.Consoles_UnifiedPreview)
                };

                sessionItems = showIndividualConsoles
                    ? [unifiedItem, .. visibleSessions.Select(session => MapSession(selectedModule, session))]
                    : [unifiedItem];

                // Session selection: fall back to the first item when the stored session is missing or gone.
                if (string.IsNullOrWhiteSpace(_selectedSessionId) ||
                    !sessionItems.Any(session => string.Equals(session.Id, _selectedSessionId, StringComparison.Ordinal)))
                {
                    _selectedSessionId = sessionItems.FirstOrDefault()?.Id;
                }

                // Map the selected list item back to the store session; the merged console has no store session.
                var selectedSessionItem = sessionItems.FirstOrDefault(session => string.Equals(session.Id, _selectedSessionId, StringComparison.Ordinal));
                var selectedSession = selectedSessionItem == null ||
                                      string.Equals(selectedSessionItem.SessionSourceId, UnifiedSessionId, StringComparison.Ordinal)
                    ? null
                    : visibleSessions.FirstOrDefault(session => string.Equals(session.Id, selectedSessionItem.SessionSourceId, StringComparison.Ordinal));

                // Fill the output pane: merged module console, placeholders for a vanished session, or session output.
                if (selectedSessionItem == null || string.Equals(_selectedSessionId, UnifiedSessionId, StringComparison.Ordinal))
                {
                    selectedSessionLines = _consoleStore.GetUnifiedModuleLines(selectedModule.SourcePath);
                    selectedSessionTitle = L.Get(LocalizationKeys.Consoles_ModuleUnifiedTitleFormat, selectedModule.Name);
                    selectedSessionStatus = string.Empty;
                    selectedSessionDescription = string.Empty;
                    selectedSessionCommandLine = string.Empty;
                    selectedSessionFooter = L.Get(LocalizationKeys.Consoles_VisibleLinesFormat, selectedSessionLines.Count);
                }
                else if (selectedSession == null)
                {
                    // The selected item has no matching store session any more.
                    selectedSessionLines = [L.Get(LocalizationKeys.Consoles_NoOutputYet)];
                    selectedSessionTitle = L.Get(LocalizationKeys.Consoles_EmptySelectionTitle);
                    selectedSessionStatus = L.Get(LocalizationKeys.Consoles_EmptySelectionStatus);
                    selectedSessionDescription = L.Get(LocalizationKeys.Consoles_EmptySelectionDescription);
                    selectedSessionCommandLine = string.Empty;
                    selectedSessionFooter = L.Get(LocalizationKeys.Consoles_EmptySelectionFooter);
                }
                else
                {
                    // A concrete process session: its output, status, description (or stage) and command line.
                    selectedSessionLines = _consoleStore.GetSessionLines(selectedModule.SourcePath, selectedSession.Id);
                    selectedSessionTitle = $"{selectedModule.Name} / {selectedSession.Title}";
                    selectedSessionStatus = BuildSelectedStatus(selectedSession);
                    selectedSessionDescription = selectedSession.IsObservedProcess
                        ? L.Get(LocalizationKeys.Consoles_SessionObservedDescription)
                        : string.IsNullOrWhiteSpace(selectedSession.CommandDescription)
                            ? L.Get(LocalizationKeys.Consoles_SessionStageFormat, LocalizeConsoleStage(selectedSession.Stage))
                            : selectedSession.CommandDescription;
                    selectedSessionCommandLine = selectedSession.CommandLine ?? string.Empty;
                    selectedSessionFooter = BuildFooter(selectedSession);
                }
            }

            // The session picker is only useful when there is a choice and the scope is a single module.
            return new ConsolesDashboardState
            {
                Modules = moduleItems,
                SelectedModuleSourcePath = _selectedModuleSourcePath,
                Sessions = sessionItems,
                ShowSessionList = sessionItems.Count > 1 && !isGlobalModule,
                SelectedSessionId = _selectedSessionId,
                SelectedSessionKey = $"{_selectedModuleSourcePath}|{_selectedSessionId}",
                SelectedSessionTitle = selectedSessionTitle,
                SelectedSessionStatus = selectedSessionStatus,
                SelectedSessionDescription = selectedSessionDescription,
                SelectedSessionCommandLine = selectedSessionCommandLine,
                SelectedSessionLines = selectedSessionLines,
                SelectedSessionFooter = selectedSessionFooter
            };
        }


        // Mapping

        /// <summary>
        /// Maps internal console stage identifiers to localized labels; unknown stages are shown as they are.
        /// </summary>
        private static string LocalizeConsoleStage(string stage) => stage switch
        {
            "Run" => L.Get(LocalizationKeys.Consoles_Run),
            "Command" => L.Get(LocalizationKeys.Consoles_Stage_Command),
            "Service" => L.Get(LocalizationKeys.Consoles_Stage_Service),
            "Lifecycle" => L.Get(LocalizationKeys.Consoles_Stage_Lifecycle),
            _ => stage
        };

        /// <summary>
        /// Maps one module snapshot to its sidebar item view model:
        /// name, enabled state with process counts, and last activity time.
        /// </summary>
        private static ConsoleModuleItemViewModel MapModule(ModuleConsoleModuleSnapshot module)
        {
            var activityText = module.LastActivityUtc.HasValue
                ? L.Get(LocalizationKeys.Consoles_LastActivityFormat, module.LastActivityUtc.Value.ToLocalTime().ToString("HH:mm:ss"))
                : L.Get(LocalizationKeys.Consoles_NoActivityYet);

            var enabledLabel = module.IsEnabled
                ? L.Get(LocalizationKeys.Consoles_Status_Enabled)
                : L.Get(LocalizationKeys.Consoles_Status_Disabled);

            return new ConsoleModuleItemViewModel
            {
                SourcePath = module.SourcePath,
                Name = module.Name,
                StatusText = L.Get(
                    LocalizationKeys.Consoles_Status_ModuleFormat,
                    enabledLabel,
                    module.ActiveProcessCount,
                    module.Sessions.Count(session => !session.IsObservedProcess)),
                ActivityText = activityText
            };
        }

        /// <summary>
        /// Maps one session snapshot to its list item view model. The item id combines the module source path and
        /// the session id so sessions of different modules never collide.
        /// </summary>
        /// <param name="module">Module that owns the session.</param>
        /// <param name="session">Session to map.</param>
        private static ConsoleSessionItemViewModel MapSession(ModuleConsoleModuleSnapshot module, ModuleConsoleSessionSnapshot session)
        {
            // Status line: observed services only report running or stopped; own processes also report the exit code.
            var pidSuffix = FormatPid(session.ProcessId);
            var stageLabel = LocalizeConsoleStage(session.Stage);
            var status = session.IsObservedProcess
                ? session.IsRunning
                    ? L.Get(LocalizationKeys.Consoles_Status_ServiceObservedRunningFormat, pidSuffix)
                    : L.Get(LocalizationKeys.Consoles_Status_ServiceObservedStoppedFormat, pidSuffix)
                : session.IsRunning
                    ? L.Get(LocalizationKeys.Consoles_Status_RunningFormat, stageLabel, pidSuffix)
                    : session.ExitCode.HasValue
                        ? L.Get(LocalizationKeys.Consoles_Status_ExitFormat, stageLabel, session.ExitCode.Value, pidSuffix)
                        : L.Get(LocalizationKeys.Consoles_Status_Completed, stageLabel);

            // Build the list item; a session without preview text gets a placeholder that says why it is empty.
            return new ConsoleSessionItemViewModel
            {
                Id = $"{module.SourcePath}::{session.Id}",
                ModuleSourcePath = module.SourcePath,
                SessionSourceId = session.Id,
                Title = session.Title,
                StatusText = status,
                Preview = string.IsNullOrWhiteSpace(session.Preview)
                    ? session.IsObservedProcess
                        ? L.Get(LocalizationKeys.Consoles_SessionObservedPreview)
                        : L.Get(LocalizationKeys.Consoles_NoOutputYet)
                    : session.Preview
            };
        }

        /// <summary>
        /// Builds the human-readable status line shown above the selected console output.
        /// </summary>
        /// <param name="session">Selected session snapshot.</param>
        private static string BuildSelectedStatus(ModuleConsoleSessionSnapshot session)
        {
            if (session.IsObservedProcess)
            {
                return session.IsRunning
                    ? L.Get(LocalizationKeys.Consoles_Selected_ObservedRunning, FormatPid(session.ProcessId))
                    : L.Get(LocalizationKeys.Consoles_Selected_ObservedStopped, FormatPid(session.ProcessId));
            }

            var stageLabel = LocalizeConsoleStage(session.Stage);
            if (session.IsRunning)
            {
                return L.Get(LocalizationKeys.Consoles_Selected_SessionRunning, stageLabel, FormatPid(session.ProcessId));
            }

            if (session.ExitCode.HasValue)
            {
                return L.Get(
                    LocalizationKeys.Consoles_Selected_SessionExitFormat,
                    stageLabel,
                    session.ExitCode.Value,
                    FormatPid(session.ProcessId));
            }

            return L.Get(LocalizationKeys.Consoles_Selected_SessionCompleted, stageLabel);
        }

        /// <summary>
        /// Builds the footer metadata shown under the selected console output:
        /// buffered line count, start and end times.
        /// </summary>
        /// <param name="session">Selected session snapshot.</param>
        private static string BuildFooter(ModuleConsoleSessionSnapshot session)
        {
            var started = L.Get(
                LocalizationKeys.Consoles_Footer_StartedFormat,
                session.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            var ended = session.EndedUtc.HasValue
                ? L.Get(
                    LocalizationKeys.Consoles_Footer_EndedFormat,
                    session.EndedUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))
                : string.Empty;
            var observedSuffix = session.IsObservedProcess
                ? L.Get(LocalizationKeys.Consoles_Footer_ObservedCaptureNote)
                : string.Empty;

            return L.Get(
                LocalizationKeys.Consoles_Footer_BufferedFormat,
                session.LineCount,
                started,
                ended,
                observedSuffix);
        }

        /// <summary>
        /// Formats the process identifier suffix used in session status labels; empty when there is no process id.
        /// </summary>
        private static string FormatPid(int? processId)
        {
            return processId.HasValue
                ? L.Get(LocalizationKeys.Consoles_PidFormat, processId.Value)
                : string.Empty;
        }
    }


    // Dashboard state and view models

    /// <summary>
    /// Represents the complete UI state required to render the consoles dashboard.
    /// </summary>
    internal sealed class ConsolesDashboardState
    {
        // Dashboard properties

        /// <summary>
        /// Gets or sets the rendered module items.
        /// </summary>
        public IReadOnlyList<ConsoleModuleItemViewModel> Modules { get; set; } = [];

        /// <summary>
        /// Gets or sets the source path of the selected module scope.
        /// </summary>
        public string? SelectedModuleSourcePath { get; set; }

        /// <summary>
        /// Gets or sets the rendered session items for the selected module scope.
        /// </summary>
        public IReadOnlyList<ConsoleSessionItemViewModel> Sessions { get; set; } = [];

        /// <summary>
        /// Gets or sets whether the console (session) picker should be visible.
        /// </summary>
        public bool ShowSessionList { get; set; }

        /// <summary>
        /// Gets or sets the selected session identifier.
        /// </summary>
        public string? SelectedSessionId { get; set; }

        /// <summary>
        /// Gets or sets the composite key used to detect session changes in the native console host.
        /// </summary>
        public string SelectedSessionKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the title shown above the selected console output.
        /// </summary>
        public string SelectedSessionTitle { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the status line shown above the selected console output.
        /// </summary>
        public string SelectedSessionStatus { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the descriptive text shown for the selected console output.
        /// </summary>
        public string SelectedSessionDescription { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the command line shown for the selected console output.
        /// </summary>
        public string SelectedSessionCommandLine { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the console lines shown in the output pane.
        /// </summary>
        public IReadOnlyList<string> SelectedSessionLines { get; set; } = [];

        /// <summary>
        /// Gets or sets the footer shown below the selected console output.
        /// </summary>
        public string SelectedSessionFooter { get; set; } = string.Empty;
    }

    /// <summary>
    /// Exposes bindable state for the consoles page shell and output pane.
    /// </summary>
    public sealed class ConsolesPageViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="SelectedSessionId"/>.
        /// </summary>
        private string? _selectedSessionId;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionKey"/>.
        /// </summary>
        private string _selectedSessionKey = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionTitle"/>.
        /// </summary>
        private string _selectedSessionTitle = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionStatus"/>.
        /// </summary>
        private string _selectedSessionStatus = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionDescription"/>.
        /// </summary>
        private string _selectedSessionDescription = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionCommandLine"/>.
        /// </summary>
        private string _selectedSessionCommandLine = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionFooter"/>.
        /// </summary>
        private string _selectedSessionFooter = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SelectedSessionText"/>.
        /// </summary>
        private string _selectedSessionText = string.Empty;


        // Events

        /// <summary>
        /// Raised when a bindable property of the page view model changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Bindable state

        /// <summary>
        /// Gets the module items shown in the module list.
        /// </summary>
        public ObservableCollection<ConsoleModuleItemViewModel> Modules { get; } = new();

        /// <summary>
        /// Gets the session items shown in the session list.
        /// </summary>
        public ObservableCollection<ConsoleSessionItemViewModel> Sessions { get; } = new();

        /// <summary>
        /// Gets or sets the selected session identifier.
        /// </summary>
        public string? SelectedSessionId
        {
            get => _selectedSessionId;
            set => SetProperty(ref _selectedSessionId, value);
        }

        /// <summary>
        /// Gets or sets the composite session key used by the native console host.
        /// </summary>
        public string SelectedSessionKey
        {
            get => _selectedSessionKey;
            set => SetProperty(ref _selectedSessionKey, value);
        }

        /// <summary>
        /// Gets or sets the selected console title.
        /// </summary>
        public string SelectedSessionTitle
        {
            get => _selectedSessionTitle;
            set => SetProperty(ref _selectedSessionTitle, value);
        }

        /// <summary>
        /// Gets or sets the selected console status line.
        /// </summary>
        public string SelectedSessionStatus
        {
            get => _selectedSessionStatus;
            set => SetProperty(ref _selectedSessionStatus, value);
        }

        /// <summary>
        /// Gets or sets the selected console description.
        /// </summary>
        public string SelectedSessionDescription
        {
            get => _selectedSessionDescription;
            set => SetProperty(ref _selectedSessionDescription, value);
        }

        /// <summary>
        /// Gets or sets the selected console command line.
        /// </summary>
        public string SelectedSessionCommandLine
        {
            get => _selectedSessionCommandLine;
            set => SetProperty(ref _selectedSessionCommandLine, value);
        }

        /// <summary>
        /// Gets or sets the selected console footer.
        /// </summary>
        public string SelectedSessionFooter
        {
            get => _selectedSessionFooter;
            set => SetProperty(ref _selectedSessionFooter, value);
        }

        /// <summary>
        /// Gets or sets the text rendered by the native console output host.
        /// </summary>
        public string SelectedSessionText
        {
            get => _selectedSessionText;
            set => SetProperty(ref _selectedSessionText, value);
        }


        // Property notifications

        /// <summary>
        /// Updates a bindable field and raises <see cref="PropertyChanged"/> when the value changes.
        /// </summary>
        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents one module item in the consoles module list.
    /// </summary>
    public sealed class ConsoleModuleItemViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="SourcePath"/>.
        /// </summary>
        private string _sourcePath = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Name"/>.
        /// </summary>
        private string _name = string.Empty;

        /// <summary>
        /// Backing field of <see cref="StatusText"/>.
        /// </summary>
        private string _statusText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="ActivityText"/>.
        /// </summary>
        private string _activityText = string.Empty;


        // Events

        /// <summary>
        /// Raised when a bindable property of the module item changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Bindable state

        /// <summary>
        /// Gets or sets the module source path used as the stable list key.
        /// </summary>
        public string SourcePath
        {
            get => _sourcePath;
            set => SetProperty(ref _sourcePath, value);
        }

        /// <summary>
        /// Gets or sets the module name shown in the list.
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// Gets or sets the secondary module status text.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// Gets or sets the activity summary shown for the module.
        /// </summary>
        public string ActivityText
        {
            get => _activityText;
            set => SetProperty(ref _activityText, value);
        }


        // State comparison and copying

        /// <summary>
        /// Compares two module items by their rendered state.
        /// </summary>
        /// <param name="other">Item to compare with.</param>
        public bool IsSameAs(ConsoleModuleItemViewModel other)
        {
            return SourcePath == other.SourcePath &&
                   Name == other.Name &&
                   StatusText == other.StatusText &&
                   ActivityText == other.ActivityText;
        }

        /// <summary>
        /// Copies the rendered state from another module item, raising change notifications only for changed values.
        /// </summary>
        /// <param name="other">Item whose state is copied.</param>
        public void UpdateFrom(ConsoleModuleItemViewModel other)
        {
            SourcePath = other.SourcePath;
            Name = other.Name;
            StatusText = other.StatusText;
            ActivityText = other.ActivityText;
        }


        // Property notifications

        /// <summary>
        /// Updates a bindable field and raises <see cref="PropertyChanged"/> when the value changes.
        /// </summary>
        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Represents one console session item in the consoles session list.
    /// </summary>
    public sealed class ConsoleSessionItemViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="Id"/>.
        /// </summary>
        private string _id = string.Empty;

        /// <summary>
        /// Backing field of <see cref="ModuleSourcePath"/>.
        /// </summary>
        private string _moduleSourcePath = string.Empty;

        /// <summary>
        /// Backing field of <see cref="SessionSourceId"/>.
        /// </summary>
        private string _sessionSourceId = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Title"/>.
        /// </summary>
        private string _title = string.Empty;

        /// <summary>
        /// Backing field of <see cref="StatusText"/>.
        /// </summary>
        private string _statusText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Preview"/>.
        /// </summary>
        private string _preview = string.Empty;


        // Events

        /// <summary>
        /// Raised when a bindable property of the session item changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Bindable state

        /// <summary>
        /// Gets or sets the stable list identifier for this item.
        /// </summary>
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        /// <summary>
        /// Gets or sets the owning module source path.
        /// </summary>
        public string ModuleSourcePath
        {
            get => _moduleSourcePath;
            set => SetProperty(ref _moduleSourcePath, value);
        }

        /// <summary>
        /// Gets or sets the underlying session identifier from the console store.
        /// </summary>
        public string SessionSourceId
        {
            get => _sessionSourceId;
            set => SetProperty(ref _sessionSourceId, value);
        }

        /// <summary>
        /// Gets or sets the session title shown in the list.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// Gets or sets the session status text shown in the list.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// Gets or sets the short preview text shown in the list.
        /// </summary>
        public string Preview
        {
            get => _preview;
            set => SetProperty(ref _preview, value);
        }


        // State comparison and copying

        /// <summary>
        /// Compares two session items by their rendered state.
        /// </summary>
        /// <param name="other">Item to compare with.</param>
        public bool IsSameAs(ConsoleSessionItemViewModel other)
        {
            return Id == other.Id &&
                   ModuleSourcePath == other.ModuleSourcePath &&
                   SessionSourceId == other.SessionSourceId &&
                   Title == other.Title &&
                   StatusText == other.StatusText &&
                   Preview == other.Preview;
        }

        /// <summary>
        /// Copies the rendered state from another session item, raising change notifications only for changed values.
        /// </summary>
        /// <param name="other">Item whose state is copied.</param>
        public void UpdateFrom(ConsoleSessionItemViewModel other)
        {
            Id = other.Id;
            ModuleSourcePath = other.ModuleSourcePath;
            SessionSourceId = other.SessionSourceId;
            Title = other.Title;
            StatusText = other.StatusText;
            Preview = other.Preview;
        }


        // Property notifications

        /// <summary>
        /// Updates a bindable field and raises <see cref="PropertyChanged"/> when the value changes.
        /// </summary>
        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
