// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Displays module consoles, per-process sessions, and merged console output.
    /// </summary>
    public partial class ConsolesView : ContentView, IConsolesView, ILocalizable
    {
        private const double CompactBreakpoint = 1180;
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);

        private readonly ConsolesPageViewModel _viewModel = new();
        private readonly ConsolesPresenter _presenter;
        private readonly AppLocalizationService _localization;
        private bool _suppressSelection;
        private int _layoutRefreshQueued;
        private CancellationTokenSource? _autoRefreshCts;

        /// <summary>
        /// Creates the consoles view and initializes its responsive shell layout.
        /// </summary>
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

        /// <inheritdoc />
        public void ApplyLocalization()
        {
            PageTitleLabel.Text = L.Get(LocalizationKeys.Consoles_Title);
            ModulesHeaderLabel.Text = L.Get(LocalizationKeys.Consoles_ModulesHeader);
            SessionsHeaderLabel.Text = L.Get(LocalizationKeys.Consoles_Title);

            ModulesEmptyLabel.Text = L.Get(LocalizationKeys.Consoles_NoActiveModules);
            SessionsEmptyLabel.Text = L.Get(LocalizationKeys.Consoles_NoConsoles);

            _ = _presenter.RefreshAsync();
        }

        /// <summary>
        /// Requests a presenter-driven refresh of the current consoles dashboard.
        /// </summary>
        internal Task RefreshAsync()
        {
            return _presenter.RefreshAsync();
        }

        /// <summary>
        /// Opens the consoles workspace focused on the requested module.
        /// </summary>
        internal Task ShowModuleAsync(string sourcePath)
        {
            return _presenter.SelectModuleAsync(sourcePath);
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
            _suppressSelection = true;

            _viewModel.SelectedSessionId = state.SelectedSessionId;
            _viewModel.SelectedSessionKey = state.SelectedSessionKey;
            _viewModel.SelectedSessionTitle = state.SelectedSessionTitle;
            _viewModel.SelectedSessionStatus = state.SelectedSessionStatus;
            _viewModel.SelectedSessionDescription = state.SelectedSessionDescription;
            _viewModel.SelectedSessionCommandLine = state.SelectedSessionCommandLine;
            _viewModel.SelectedSessionFooter = state.SelectedSessionFooter;
            _viewModel.SelectedSessionText = string.Join(Environment.NewLine, state.SelectedSessionLines);

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
            }
        }


        // Responsive layout

        /// <summary>
        /// Switches the workspace between the compact stacked layout and the wide three-column layout.
        /// </summary>
        private void UpdateResponsiveLayout(bool? showSessionList = null)
        {
            var isCompact = Width > 0 && Width < CompactBreakpoint;
            var includeSessions = showSessionList ?? SessionsPanel.IsVisible;

            WorkspaceLayout.ColumnDefinitions.Clear();
            WorkspaceLayout.RowDefinitions.Clear();

            if (isCompact)
            {
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
            RefreshConsoleLayout();

            if (Dispatcher == null || Interlocked.Exchange(ref _layoutRefreshQueued, 1) == 1)
            {
                return;
            }

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
        /// Synchronizes the module item view models with the latest presenter state.
        /// </summary>
        private static void SyncModuleItems(ObservableCollection<ConsoleModuleItemViewModel> target, IReadOnlyList<ConsoleModuleItemViewModel> source)
        {
            SyncKeyedItems(
                target,
                source,
                static item => item.SourcePath,
                static (targetItem, sourceItem) => targetItem.UpdateFrom(sourceItem));
        }

        /// <summary>
        /// Synchronizes the session item view models with the latest presenter state.
        /// </summary>
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
        private static void SyncKeyedItems<T>(
            ObservableCollection<T> target,
            IReadOnlyList<T> source,
            Func<T, string> keySelector,
            Action<T, T> updateExisting)
            where T : class
        {
            var indexByKey = BuildIndex(target, keySelector);

            for (var index = 0; index < source.Count; index++)
            {
                var sourceItem = source[index];
                var sourceKey = keySelector(sourceItem);

                if (index < target.Count &&
                    string.Equals(keySelector(target[index]), sourceKey, StringComparison.Ordinal))
                {
                    updateExisting(target[index], sourceItem);
                    continue;
                }

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

            while (target.Count > source.Count)
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        /// <summary>
        /// Builds a key-to-index map for the current observable collection contents.
        /// </summary>
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

    /// <summary>
    /// Defines the rendering contract used by the consoles presenter.
    /// </summary>
    internal interface IConsolesView
    {
        /// <summary>
        /// Applies a fully prepared dashboard state to the view.
        /// </summary>
        void Render(ConsolesDashboardState state);
    }

    /// <summary>
    /// Builds dashboard state for the consoles view and coordinates selections with the console store.
    /// </summary>
    internal sealed class ConsolesPresenter
    {
        private const string AllModulesModuleId = "__all_modules__";
        private const string GlobalUnifiedSessionId = "__all_modules_unified__";
        private const string UnifiedSessionId = "__module_unified__";

        private readonly IConsolesView _view;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly ModuleConsoleStore _consoleStore;
        private readonly AppDataStore _appData;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        private List<ModuleConfig> _knownModules = [];
        private bool _isActive;
        private int _refreshQueued;
        private bool _showCompletedProcesses;
        private string? _selectedModuleSourcePath;
        private string? _selectedSessionId;

        /// <summary>
        /// Creates the presenter for the consoles dashboard.
        /// </summary>
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
        /// Selects a console session inside the currently active module scope.
        /// </summary>
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
        /// Reloads modules when required and rerenders the dashboard.
        /// </summary>
        public Task RefreshAsync()
        {
            return RefreshAsync(forceModuleReload: true);
        }

        /// <summary>
        /// Reloads module metadata when requested, snapshots console state, and renders the result.
        /// </summary>
        public async Task RefreshAsync(bool forceModuleReload)
        {
            await _refreshLock.WaitAsync();
            try
            {
                LoadPreferences();

                if (forceModuleReload || _knownModules.Count == 0)
                {
                    _knownModules = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync());
                }

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
            if (!_isActive || Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Interlocked.Exchange(ref _refreshQueued, 0);
                await RefreshAsync(forceModuleReload: false);
            });
        }


        // Preferences

        /// <summary>
        /// Loads console preferences from persisted app data.
        /// </summary>
        private void LoadPreferences()
        {
            _appData.Data.Consoles.Normalize();
            _showCompletedProcesses = _appData.Data.Consoles.ShowCompletedProcesses;
        }


        // State building

        /// <summary>
        /// Builds the full dashboard state for the current module and session selection.
        /// </summary>
        private ConsolesDashboardState BuildState(IReadOnlyList<ModuleConsoleModuleSnapshot> snapshots)
        {
            var activeModules = snapshots
                .Where(module => module.IsEnabled || module.ActiveProcessCount > 0)
                .ToList();

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

            moduleItems.AddRange(activeModules.Select(MapModule));

            if (moduleItems.Count == 0)
            {
                _selectedModuleSourcePath = null;
            }
            else if (string.IsNullOrWhiteSpace(_selectedModuleSourcePath) ||
                     !moduleItems.Any(module => string.Equals(module.SourcePath, _selectedModuleSourcePath, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedModuleSourcePath = moduleItems[0].SourcePath;
            }

            var isGlobalModule = string.Equals(_selectedModuleSourcePath, AllModulesModuleId, StringComparison.Ordinal);
            var selectedModule = activeModules.FirstOrDefault(module => module.SourcePath == _selectedModuleSourcePath);
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
                var visibleSessions = selectedModule.Sessions
                    .Where(session => !string.Equals(session.Id, "overview", StringComparison.OrdinalIgnoreCase))
                    .Where(session => !session.IsObservedProcess)
                    .Where(session => _showCompletedProcesses || session.IsRunning)
                    .ToList();

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

                if (string.IsNullOrWhiteSpace(_selectedSessionId) ||
                    !sessionItems.Any(session => string.Equals(session.Id, _selectedSessionId, StringComparison.Ordinal)))
                {
                    _selectedSessionId = sessionItems.FirstOrDefault()?.Id;
                }

                var selectedSessionItem = sessionItems.FirstOrDefault(session => string.Equals(session.Id, _selectedSessionId, StringComparison.Ordinal));
                var selectedSession = selectedSessionItem == null ||
                                      string.Equals(selectedSessionItem.SessionSourceId, UnifiedSessionId, StringComparison.Ordinal)
                    ? null
                    : visibleSessions.FirstOrDefault(session => string.Equals(session.Id, selectedSessionItem.SessionSourceId, StringComparison.Ordinal));

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
                    selectedSessionLines = [L.Get(LocalizationKeys.Consoles_NoOutputYet)];
                    selectedSessionTitle = L.Get(LocalizationKeys.Consoles_EmptySelectionTitle);
                    selectedSessionStatus = L.Get(LocalizationKeys.Consoles_EmptySelectionStatus);
                    selectedSessionDescription = L.Get(LocalizationKeys.Consoles_EmptySelectionDescription);
                    selectedSessionCommandLine = string.Empty;
                    selectedSessionFooter = L.Get(LocalizationKeys.Consoles_EmptySelectionFooter);
                }
                else
                {
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
        /// Maps internal console stage identifiers to localized labels.
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
        /// Maps one module snapshot to its sidebar item view model.
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
        /// Maps one session snapshot to its list item view model.
        /// </summary>
        private static ConsoleSessionItemViewModel MapSession(ModuleConsoleModuleSnapshot module, ModuleConsoleSessionSnapshot session)
        {
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
        /// Builds the footer metadata shown under the selected console output.
        /// </summary>
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
        /// Formats the process identifier suffix used in session status labels.
        /// </summary>
        private static string FormatPid(int? processId)
        {
            return processId.HasValue
                ? L.Get(LocalizationKeys.Consoles_PidFormat, processId.Value)
                : string.Empty;
        }
    }

    /// <summary>
    /// Represents the complete UI state required to render the consoles dashboard.
    /// </summary>
    internal sealed class ConsolesDashboardState
    {
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
        /// Gets or sets whether the console picker should be visible.
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
        private string? _selectedSessionId;
        private string _selectedSessionKey = string.Empty;
        private string _selectedSessionTitle = string.Empty;
        private string _selectedSessionStatus = string.Empty;
        private string _selectedSessionDescription = string.Empty;
        private string _selectedSessionCommandLine = string.Empty;
        private string _selectedSessionFooter = string.Empty;
        private string _selectedSessionText = string.Empty;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

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
        private string _sourcePath = string.Empty;
        private string _name = string.Empty;
        private string _statusText = string.Empty;
        private string _activityText = string.Empty;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

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

        /// <summary>
        /// Compares two module items by their rendered state.
        /// </summary>
        public bool IsSameAs(ConsoleModuleItemViewModel other)
        {
            return SourcePath == other.SourcePath &&
                   Name == other.Name &&
                   StatusText == other.StatusText &&
                   ActivityText == other.ActivityText;
        }

        /// <summary>
        /// Copies the rendered state from another module item.
        /// </summary>
        public void UpdateFrom(ConsoleModuleItemViewModel other)
        {
            SourcePath = other.SourcePath;
            Name = other.Name;
            StatusText = other.StatusText;
            ActivityText = other.ActivityText;
        }

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
        private string _id = string.Empty;
        private string _moduleSourcePath = string.Empty;
        private string _sessionSourceId = string.Empty;
        private string _title = string.Empty;
        private string _statusText = string.Empty;
        private string _preview = string.Empty;

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

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

        /// <summary>
        /// Compares two session items by their rendered state.
        /// </summary>
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
        /// Copies the rendered state from another session item.
        /// </summary>
        public void UpdateFrom(ConsoleSessionItemViewModel other)
        {
            Id = other.Id;
            ModuleSourcePath = other.ModuleSourcePath;
            SessionSourceId = other.SessionSourceId;
            Title = other.Title;
            StatusText = other.StatusText;
            Preview = other.Preview;
        }

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
