// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Input;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    // Palette

    /// <summary>
    /// Reads semantic colors from the active app palette so presenter snapshots stay readable in light and dark themes.
    /// </summary>
    internal static class HomePaletteColors
    {
        /// <summary>
        /// Primary text color of the active theme; falls back to opaque black when the resource is missing.
        /// </summary>
        public static Color LabelPrimary =>
            Resolve("LabelPrimary", Color.FromArgb("#FF000000"));

        /// <summary>
        /// Secondary (muted) text color of the active theme; falls back to a mid gray when the resource is missing.
        /// </summary>
        public static Color LabelSecondary =>
            Resolve("LabelSecondary", Color.FromArgb("#FF636366"));

        /// <summary>
        /// Resolves a semantic palette color from application resources, returning the fallback when it is not a color.
        /// </summary>
        private static Color Resolve(string key, Color fallback)
        {
            if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            {
                return c;
            }

            return fallback;
        }
    }


    // Home view

    /// <summary>
    /// Displays the main ASLM dashboard with live runtime metrics, module controls, and the managed process tree.
    /// The view only binds and lays out prepared state; data gathering and module actions live in the presenter.
    /// </summary>
    public partial class HomeView : ContentView, IHomeDashboardView, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Minimum width in device-independent units reserved for one summary card when computing the grid span.
        /// </summary>
        private const double MinSummaryCardWidth = 400;

        /// <summary>
        /// Available width below which the process tree and the module list are stacked vertically.
        /// </summary>
        private const double CompactBreakpoint = 1480;

        /// <summary>
        /// Bindable page model that the XAML binds to.
        /// </summary>
        private readonly HomeDashboardPageViewModel _viewModel = new();

        /// <summary>
        /// Presenter that gathers diagnostics, builds dashboard state, and runs module actions.
        /// </summary>
        private readonly HomeDashboardPresenter _presenter;

        /// <summary>
        /// Localization service whose language changes re-apply the view texts.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Repeating timer that triggers a live refresh while the view is loaded; created on first use.
        /// </summary>
        private IDispatcherTimer? _refreshTimer;


        // Initialization

        /// <summary>
        /// Creates the home dashboard and prepares the presenter-driven lifecycle.
        /// </summary>
        public HomeView(
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            ModuleLaunchCoordinator launchCoordinator,
            ModuleConsoleStore consoleStore,
            ProcessSnapshotReader processSnapshots,
            AppDataStore appData,
            AppLocalizationService localization)
        {
            InitializeComponent();

            BindingContext = _viewModel;
            _localization = localization;

            _presenter = new HomeDashboardPresenter(
                this,
                moduleInstaller,
                moduleRunner,
                launchCoordinator,
                consoleStore,
                processSnapshots,
                appData);

            LocalizableAttach.Hook(this, _localization, this);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;

            UpdateResponsiveLayout();
        }


        // Localization and shell API

        /// <summary>
        /// Applies localized texts to the static labels and requests a refresh so generated card texts are rebuilt too.
        /// </summary>
        public void ApplyLocalization()
        {
            HomeTitleLabel.Text = L.Get(LocalizationKeys.Home_Title);
            ManagedProcessesHeaderLabel.Text = L.Get(LocalizationKeys.Home_ManagedProcesses);
            ColumnNameLabel.Text = L.Get(LocalizationKeys.Home_Column_Name);
            ColumnCpuLabel.Text = L.Get(LocalizationKeys.Home_Column_Cpu);
            ColumnGpuLabel.Text = L.Get(LocalizationKeys.Home_Column_Gpu);
            ColumnRamLabel.Text = L.Get(LocalizationKeys.Home_Column_Ram);
            ColumnDiskLabel.Text = L.Get(LocalizationKeys.Home_Column_Disk);
            ColumnNetLabel.Text = L.Get(LocalizationKeys.Home_Column_Net);
            ColumnDetailsLabel.Text = L.Get(LocalizationKeys.Home_Column_Details);
            ModulesHeaderLabel.Text = L.Get(LocalizationKeys.Home_ModulesHeader);

            _ = _presenter.RefreshAsync(forceModuleReload: false);
        }

        /// <summary>
        /// Attaches the shell host so the dashboard can request navigation to related pages.
        /// </summary>
        internal void Initialize(AppShellPage shell)
        {
            _presenter.AttachShell(shell);
        }

        /// <summary>
        /// Requests an immediate presenter-driven refresh of the dashboard.
        /// </summary>
        internal Task RefreshAsync()
        {
            return _presenter.RefreshAsync();
        }


        // Lifetime events

        /// <summary>
        /// Activates the presenter and starts the periodic refresh loop when the dashboard enters the visual tree.
        /// </summary>
        private async void OnLoaded(object? sender, EventArgs e)
        {
            await _presenter.ActivateAsync();
            StartRefreshTimer();
        }

        /// <summary>
        /// Deactivates the presenter and stops the periodic refresh loop when the dashboard leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            StopRefreshTimer();
            _presenter.Deactivate();
        }


        // Layout events

        /// <summary>
        /// Rebuilds the responsive dashboard layout when the host size changes.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e)
        {
            UpdateResponsiveLayout();
        }


        // Rendering

        /// <summary>
        /// Applies the fully prepared presenter state to the bindable page model.
        /// </summary>
        void IHomeDashboardView.Render(HomeDashboardState state)
        {
            // Summary cards are matched by their stable key.
            SyncKeyedItems(
                _viewModel.SummaryCards,
                state.SummaryCards,
                static item => item.Key,
                static (target, source) => target.CopyFrom(source));

            // Process rows disappear and reorder frequently, so vanished rows are removed before reordering.
            SyncKeyedItems(
                _viewModel.ProcessNodes,
                state.ProcessNodes,
                static item => item.Key,
                static (target, source) => target.CopyFrom(source),
                removeMissingItemsBeforeReorder: true);

            // Module rows are matched by their manifest path.
            SyncKeyedItems(
                _viewModel.Modules,
                state.Modules,
                static item => item.SourcePath,
                static (target, source) => target.CopyFrom(source));
        }


        // Responsive layout

        /// <summary>
        /// Adapts the summary grid and main workspace to the available dashboard width.
        /// </summary>
        private void UpdateResponsiveLayout()
        {
            // Reserve a fixed 24-unit horizontal gutter; before the first measure the breakpoint value is used,
            // which selects the wide layout.
            var availableWidth = Width > 0 ? Width - 24 : CompactBreakpoint;

            // Fit as many summary cards per row as the width allows, between one and three.
            if (availableWidth > 0)
            {
                var summaryWidth = Math.Max(0, availableWidth - 8);
                _viewModel.SummaryGridSpan = Math.Min(3, Math.Max(1, (int)(summaryWidth / MinSummaryCardWidth)));
            }

            var isCompact = availableWidth < CompactBreakpoint;

            // Rebuild the workspace grid from scratch: both panels are re-placed below for the chosen layout.
            WorkspaceLayout.ColumnDefinitions.Clear();
            WorkspaceLayout.RowDefinitions.Clear();

            if (isCompact)
            {
                // Compact layout: one column, process tree above the module list, equal heights.
                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid.SetRow(ProcessTreePanel, 0);
                Grid.SetColumn(ProcessTreePanel, 0);
                Grid.SetRow(ModulesPanel, 1);
                Grid.SetColumn(ModulesPanel, 0);
            }
            else
            {
                // Wide layout: process tree and module list side by side, the tree getting 1.35 parts of the width.
                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star) });
                WorkspaceLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                WorkspaceLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                Grid.SetRow(ProcessTreePanel, 0);
                Grid.SetColumn(ProcessTreePanel, 0);
                Grid.SetRow(ModulesPanel, 0);
                Grid.SetColumn(ModulesPanel, 1);
            }
        }


        // Timed refresh

        /// <summary>
        /// Starts the periodic dashboard refresh timer, creating it on first use; does nothing without a dispatcher.
        /// </summary>
        private void StartRefreshTimer()
        {
            if (Dispatcher == null)
            {
                return;
            }

            _refreshTimer ??= CreateRefreshTimer(Dispatcher);
            if (!_refreshTimer.IsRunning)
            {
                _refreshTimer.Start();
            }
        }

        /// <summary>
        /// Stops the periodic dashboard refresh timer.
        /// </summary>
        private void StopRefreshTimer()
        {
            _refreshTimer?.Stop();
        }

        /// <summary>
        /// Creates the repeating timer (one tick per second) used to keep the dashboard metrics current.
        /// </summary>
        private IDispatcherTimer CreateRefreshTimer(IDispatcher dispatcher)
        {
            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += OnRefreshTimerTick;
            return timer;
        }

        /// <summary>
        /// Requests a lightweight live refresh on each timer tick.
        /// </summary>
        private async void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            await _presenter.RefreshAsync(forceModuleReload: false);
        }


        // Collection helpers

        /// <summary>
        /// Updates an observable collection in place by key so item instances stay stable between refreshes.
        /// </summary>
        private static void SyncKeyedItems<T>(
            ObservableCollection<T> target,
            IReadOnlyList<T> source,
            Func<T, string> keySelector,
            Action<T, T> updateExisting,
            bool removeMissingItemsBeforeReorder = false)
            where T : class
        {
            if (removeMissingItemsBeforeReorder)
            {
                RemoveMissingItems(target, source, keySelector);
            }

            var indexByKey = BuildIndex(target, keySelector);

            // Walk the desired order; after each step the target prefix up to the index matches the source.
            for (var index = 0; index < source.Count; index++)
            {
                var sourceItem = source[index];
                var sourceKey = keySelector(sourceItem);

                // The item at this position already has the right key: only refresh its values.
                if (index < target.Count &&
                    string.Equals(keySelector(target[index]), sourceKey, StringComparison.Ordinal))
                {
                    updateExisting(target[index], sourceItem);
                    continue;
                }

                if (indexByKey.TryGetValue(sourceKey, out var existingIndex) && existingIndex >= index)
                {
                    // Move the matching item instead of replacing it so CollectionView rows
                    // keep their instance identity and avoid full reanimation on refresh.
                    var existingItem = target[existingIndex];
                    target.Move(existingIndex, index);
                    updateExisting(existingItem, sourceItem);
                    indexByKey = BuildIndex(target, keySelector);
                }
                else
                {
                    // No existing item with this key further down: insert the source item itself and
                    // rebuild the index map because positions shifted.
                    target.Insert(index, sourceItem);
                    indexByKey = BuildIndex(target, keySelector);
                }
            }

            // Drop the surplus tail: items whose keys no longer appear in the source.
            while (target.Count > source.Count)
            {
                target.RemoveAt(target.Count - 1);
            }
        }

        /// <summary>
        /// Removes no-longer-visible items before reordering to avoid redundant collapse animations.
        /// </summary>
        private static void RemoveMissingItems<T>(
            ObservableCollection<T> target,
            IReadOnlyList<T> source,
            Func<T, string> keySelector)
            where T : class
        {
            var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceItem in source)
            {
                sourceKeys.Add(keySelector(sourceItem));
            }

            for (var index = target.Count - 1; index >= 0; index--)
            {
                if (!sourceKeys.Contains(keySelector(target[index])))
                {
                    target.RemoveAt(index);
                }
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


    // Presenter

    /// <summary>
    /// Defines the rendering contract used by the home dashboard presenter.
    /// </summary>
    internal interface IHomeDashboardView
    {
        /// <summary>
        /// Applies a fully prepared dashboard state to the view.
        /// </summary>
        void Render(HomeDashboardState state);
    }

    /// <summary>
    /// Builds the home dashboard state, coordinates live refreshes, and handles module actions.
    /// Sampling and state construction run on a background thread; only rendering is marshalled to the UI thread.
    /// </summary>
    internal sealed class HomeDashboardPresenter
    {
        // Collaborators

        /// <summary>
        /// View that receives each prepared dashboard state.
        /// </summary>
        private readonly IHomeDashboardView _view;

        /// <summary>
        /// Discovers, loads, and saves installed module manifests.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Stops running module processes.
        /// </summary>
        private readonly ModuleRunner _moduleRunner;

        /// <summary>
        /// Launches modules with the same lifecycle as the main modules page.
        /// </summary>
        private readonly ModuleLaunchCoordinator _launchCoordinator;

        /// <summary>
        /// Source of module console sessions that feed the session counts and the console actions.
        /// </summary>
        private readonly ModuleConsoleStore _consoleStore;

        /// <summary>
        /// Persisted application data; supplies the console sidebar visibility setting.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Samples system, runtime, and per-process diagnostics on every refresh.
        /// </summary>
        private readonly HomeDiagnosticsCollector _diagnosticsCollector;


        // Refresh and tree state

        /// <summary>
        /// Single-slot gate that lets only one refresh run at a time; overlapping requests are dropped.
        /// </summary>
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        /// <summary>
        /// Busy state of module actions keyed by module manifest path.
        /// </summary>
        private readonly Dictionary<string, HomeModuleOperationState> _moduleOperations =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Expansion state of process tree nodes keyed by node key; the ASLM root starts expanded.
        /// </summary>
        private readonly Dictionary<string, bool> _expandedNodes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["root:aslm"] = true
            };

        /// <summary>
        /// Installed modules from the last discovery, reused until a forced module reload.
        /// </summary>
        private List<ModuleConfig> _knownModules = [];

        /// <summary>
        /// Console snapshot of the last refresh, reused when only the tree expansion changes.
        /// </summary>
        private IReadOnlyList<ModuleConsoleModuleSnapshot> _lastConsoleSnapshots = [];

        /// <summary>
        /// Diagnostics of the last refresh, or null before the first one completes.
        /// </summary>
        private HomeDiagnosticsSnapshot? _lastDiagnosticsSnapshot;

        /// <summary>
        /// Shell host used for page navigation, or null until attached.
        /// </summary>
        private AppShellPage? _shell;

        /// <summary>
        /// Whether the presenter is subscribed to console changes.
        /// </summary>
        private bool _isActive;

        /// <summary>
        /// Interlocked flag (0 or 1) recording that a forced module reload was requested while a refresh was running.
        /// </summary>
        private int _forceRefreshQueued;


        // Initialization

        /// <summary>
        /// Creates the presenter for the main home dashboard.
        /// </summary>
        public HomeDashboardPresenter(
            IHomeDashboardView view,
            ModuleInstaller moduleInstaller,
            ModuleRunner moduleRunner,
            ModuleLaunchCoordinator launchCoordinator,
            ModuleConsoleStore consoleStore,
            ProcessSnapshotReader processSnapshots,
            AppDataStore appData)
        {
            _view = view;
            _moduleInstaller = moduleInstaller;
            _moduleRunner = moduleRunner;
            _launchCoordinator = launchCoordinator;
            _consoleStore = consoleStore;
            _appData = appData;
            _diagnosticsCollector = new HomeDiagnosticsCollector(processSnapshots);
        }

        /// <summary>
        /// Attaches the shell host used for page navigation.
        /// </summary>
        public void AttachShell(AppShellPage shell)
        {
            _shell = shell;
        }


        // Lifecycle

        /// <summary>
        /// Starts listening for console changes and renders the initial dashboard state with a forced module reload.
        /// </summary>
        public async Task ActivateAsync()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            _consoleStore.StateChanged += OnConsoleStateChanged;
            await RefreshAsync(forceModuleReload: true);
        }

        /// <summary>
        /// Stops listening for console changes.
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


        // Refresh

        /// <summary>
        /// Reloads modules, captures diagnostics, and rerenders the dashboard using the default refresh policy.
        /// </summary>
        public Task RefreshAsync()
        {
            return RefreshAsync(forceModuleReload: true);
        }

        /// <summary>
        /// Reloads modules when needed, captures diagnostics, and rerenders the dashboard.
        /// </summary>
        /// <param name="forceModuleReload">When <see langword="true"/>, rediscover modules before sampling diagnostics.</param>
        public async Task RefreshAsync(bool forceModuleReload)
        {
            // Remember a forced reload so the refresh that currently holds the lock performs it too.
            if (forceModuleReload)
            {
                Interlocked.Exchange(ref _forceRefreshQueued, 1);
            }

            // Drop the request when a refresh is already running instead of queuing overlapping samples.
            if (!await _refreshLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                var shouldForceReload = forceModuleReload || Interlocked.Exchange(ref _forceRefreshQueued, 0) == 1;

                do
                {
                    // Rediscover installed modules only when forced or when none are known yet. Only a fresh
                    // discovery is passed to the console store: the cached list goes stale while modules start and
                    // stop, and would overwrite the enabled state that the runner keeps up to date.
                    var discovered = shouldForceReload || _knownModules.Count == 0;
                    if (discovered)
                    {
                        _knownModules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync());
                    }

                    // Sample diagnostics and build the whole state off the UI thread.
                    var modules = _knownModules.ToList();
                    var refreshSnapshot = await Task.Run(() =>
                    {
                        if (discovered)
                        {
                            _consoleStore.EnsureModules(modules);
                        }

                        var consoleSnapshots = _consoleStore.GetSnapshot();
                        var diagnostics = _diagnosticsCollector.Capture(modules, consoleSnapshots);
                        var state = BuildState(modules, consoleSnapshots, diagnostics);

                        return new HomeRefreshSnapshot
                        {
                            ConsoleSnapshots = consoleSnapshots,
                            Diagnostics = diagnostics,
                            State = state
                        };
                    }).ConfigureAwait(false);

                    // Keep the captured data so a tree toggle can re-render without sampling again.
                    _lastConsoleSnapshots = refreshSnapshot.ConsoleSnapshots;
                    _lastDiagnosticsSnapshot = refreshSnapshot.Diagnostics;

                    // Render on the UI thread, then repeat when a forced reload was requested during this pass.
                    await MainThread.InvokeOnMainThreadAsync(() => _view.Render(refreshSnapshot.State));
                    shouldForceReload = Interlocked.Exchange(ref _forceRefreshQueued, 0) == 1;
                }
                while (shouldForceReload);
            }
            finally
            {
                _refreshLock.Release();
            }
        }


        // Navigation

        /// <summary>
        /// Opens the full module management page.
        /// </summary>
        public void OpenModules()
        {
            MainThread.BeginInvokeOnMainThread(() => _shell?.OpenModules());
        }

        /// <summary>
        /// Opens the consoles page.
        /// </summary>
        public void OpenConsoles()
        {
            MainThread.BeginInvokeOnMainThread(() => _shell?.OpenConsoles());
        }


        // Console refresh triggers

        /// <summary>
        /// Handles console-store change notifications; periodic refresh already keeps metrics current.
        /// </summary>
        private void OnConsoleStateChanged(object? sender, EventArgs e)
        {
            // Periodic one-second refreshes already keep the dashboard current.
        }


        // State construction

        /// <summary>
        /// Builds the complete dashboard state from modules, console snapshots, and live diagnostics.
        /// </summary>
        private HomeDashboardState BuildState(
            IReadOnlyList<ModuleConfig> modules,
            IReadOnlyList<ModuleConsoleModuleSnapshot> consoleSnapshots,
            HomeDiagnosticsSnapshot diagnostics)
        {
            return new HomeDashboardState
            {
                SummaryCards = BuildSummaryCards(modules, consoleSnapshots, diagnostics),
                ProcessNodes = FlattenTree(diagnostics.RootNode),
                Modules = BuildModuleCards(modules, diagnostics)
            };
        }

        /// <summary>
        /// Builds the summary resource cards shown at the top of the dashboard.
        /// </summary>
        private IReadOnlyList<HomeSummaryCardViewModel> BuildSummaryCards(
            IReadOnlyList<ModuleConfig> modules,
            IReadOnlyList<ModuleConsoleModuleSnapshot> consoleSnapshots,
            HomeDiagnosticsSnapshot diagnostics)
        {
            // Sample time and session counts; observed processes (children discovered without stream capture) are
            // not real console sessions, so they are excluded.
            var updatedAt = diagnostics.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var activeSessionCount = consoleSnapshots.Sum(module => module.Sessions.Count(session => session.IsRunning && !session.IsObservedProcess));
            var totalSessionCount = consoleSnapshots.Sum(module => module.Sessions.Count(session => !session.IsObservedProcess));

            // System card: whole-machine CPU, per-adapter GPU, memory, disk activity, and network throughput.
            var systemMetrics = new List<HomeSummaryMetricViewModel>
            {
                CreatePercentMetric(
                    L.Get(LocalizationKeys.Home_Column_Cpu),
                    diagnostics.SystemUsage.CpuPercent,
                    L.Get(LocalizationKeys.Home_Metric_Cpu_LogicalCores, Environment.ProcessorCount))
            };

            systemMetrics.AddRange(BuildGpuMetrics(diagnostics.GpuAdapters, useRuntimeValues: false));
            systemMetrics.AddRange(
            [
                CreatePercentMetric(
                    L.Get(LocalizationKeys.Home_Column_Ram),
                    diagnostics.SystemUsage.MemoryPercent,
                    L.Get(
                        LocalizationKeys.Home_Metric_Ram_Usage,
                        FormatBytes(diagnostics.SystemUsage.MemoryBytes),
                        FormatBytes(diagnostics.SystemUsage.MemoryTotalBytes))),
                CreatePercentMetric(
                    L.Get(LocalizationKeys.Home_Column_Disk),
                    diagnostics.SystemUsage.DiskBusyPercent,
                    L.Get(
                        LocalizationKeys.Home_Metric_Disk_Rw,
                        FormatRate(diagnostics.SystemUsage.DiskReadBytesPerSec),
                        FormatRate(diagnostics.SystemUsage.DiskWriteBytesPerSec))),
                CreateInformationalMetric(
                    L.Get(LocalizationKeys.Home_Metric_Network),
                    FormatRate(diagnostics.SystemUsage.NetworkReceiveBytesPerSec + diagnostics.SystemUsage.NetworkSendBytesPerSec),
                    L.Get(
                        LocalizationKeys.Home_Metric_Network_RxTx,
                        FormatRate(diagnostics.SystemUsage.NetworkReceiveBytesPerSec),
                        FormatRate(diagnostics.SystemUsage.NetworkSendBytesPerSec)),
                    Color.FromArgb("#2264D2FF"))
            ]);

            // Runtime card: resources used by the ASLM process tree only (GPU adapters use the runtime share).
            var runtimeMetrics = new List<HomeSummaryMetricViewModel>
            {
                CreatePercentMetric(
                    L.Get(LocalizationKeys.Home_Column_Cpu),
                    diagnostics.RuntimeUsage.CpuPercent,
                    L.Get(LocalizationKeys.Home_Metric_Cpu_RuntimeDetail))
            };

            runtimeMetrics.AddRange(BuildGpuMetrics(diagnostics.GpuAdapters, useRuntimeValues: true));
            runtimeMetrics.AddRange(
            [
                CreatePercentMetric(
                    L.Get(LocalizationKeys.Home_Column_Ram),
                    diagnostics.RuntimeUsage.MemoryPercent,
                    L.Get(LocalizationKeys.Home_Metric_Ram_Resident, FormatBytes(diagnostics.RuntimeUsage.MemoryBytes))),
                CreateInformationalMetric(
                    L.Get(LocalizationKeys.Home_Column_Disk),
                    FormatRate(diagnostics.RuntimeUsage.DiskIoBytesPerSec),
                    L.Get(LocalizationKeys.Home_Metric_Disk_CombinedIo),
                    Color.FromArgb("#22FF9F0A")),
                CreateInformationalMetric(
                    L.Get(LocalizationKeys.Home_Metric_Network),
                    FormatConnectionCount(diagnostics.RuntimeUsage.ConnectionCount),
                    L.Get(LocalizationKeys.Home_Metric_Network_Endpoints),
                    Color.FromArgb("#2264D2FF"))
            ]);

            // Assemble the three cards in display order: system, runtime, and module overview.
            return
            [
                new HomeSummaryCardViewModel
                {
                    Key = "system",
                    Title = L.Get(LocalizationKeys.Home_Card_System_Title),
                    Subtitle = L.Get(LocalizationKeys.Home_Card_System_Subtitle),
                    StatusText = L.Get(LocalizationKeys.Home_Card_System_Status),
                    AccentOverlayColor = Color.FromArgb("#220A84FF"),
                    FooterText = L.Get(LocalizationKeys.Home_Card_System_SampledAt, updatedAt),
                    Metrics = systemMetrics
                },
                new HomeSummaryCardViewModel
                {
                    Key = "runtime",
                    Title = L.Get(LocalizationKeys.Home_Card_Runtime_Title),
                    Subtitle = L.Get(LocalizationKeys.Home_Card_Runtime_Subtitle),
                    StatusText = L.Get(LocalizationKeys.Home_Card_Runtime_StatusPid, Environment.ProcessId),
                    AccentOverlayColor = Color.FromArgb("#2232D74B"),
                    FooterText = diagnostics.ActiveProcessCount == 0
                        ? L.Get(LocalizationKeys.Home_Card_Runtime_NoChildren)
                        : L.Get(LocalizationKeys.Home_Card_Runtime_ChildCount, diagnostics.ActiveProcessCount),
                    Metrics = runtimeMetrics
                },
                new HomeSummaryCardViewModel
                {
                    Key = "modules",
                    Title = L.Get(LocalizationKeys.Home_Card_Modules_Title),
                    Subtitle = L.Get(LocalizationKeys.Home_Card_Modules_Subtitle),
                    StatusText = L.Get(LocalizationKeys.Home_Card_Modules_StatusTotal, modules.Count),
                    AccentOverlayColor = Color.FromArgb("#22FF9F0A"),
                    FooterText = diagnostics.LastModuleActivityUtc.HasValue
                        ? L.Get(
                            LocalizationKeys.Home_Card_Modules_LastActivity,
                            diagnostics.LastModuleActivityUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture))
                        : L.Get(LocalizationKeys.Home_Card_Modules_NoActivity),
                    Metrics =
                    [
                        CreateInformationalMetric(
                            L.Get(LocalizationKeys.Home_Metric_Installed),
                            modules.Count.ToString(CultureInfo.InvariantCulture),
                            L.Get(LocalizationKeys.Home_Metric_Installed_Detail),
                            Color.FromArgb("#2238383A")),
                        CreateInformationalMetric(
                            L.Get(LocalizationKeys.Home_Metric_Running),
                            modules.Count(module => module.Status.Enabled).ToString(CultureInfo.InvariantCulture),
                            L.Get(LocalizationKeys.Home_Metric_Running_Detail),
                            Color.FromArgb("#2232D74B")),
                        CreateInformationalMetric(
                            L.Get(LocalizationKeys.Home_Metric_Processes),
                            diagnostics.ActiveProcessCount.ToString(CultureInfo.InvariantCulture),
                            L.Get(LocalizationKeys.Home_Metric_Processes_Detail),
                            Color.FromArgb("#220A84FF")),
                        CreateInformationalMetric(
                            L.Get(LocalizationKeys.Home_Metric_Sessions),
                            totalSessionCount.ToString(CultureInfo.InvariantCulture),
                            L.Get(LocalizationKeys.Home_Metric_Sessions_Detail, activeSessionCount),
                            Color.FromArgb("#225E5CE6")),
                        CreateInformationalMetric(
                            L.Get(LocalizationKeys.Home_Metric_Active),
                            diagnostics.ActiveModuleCount.ToString(CultureInfo.InvariantCulture),
                            L.Get(LocalizationKeys.Home_Metric_Active_Detail),
                            Color.FromArgb("#22FF9F0A"))
                    ]
                }
            ];
        }

        /// <summary>
        /// Builds one compact dashboard row per installed module.
        /// </summary>
        private IReadOnlyList<HomeModuleCardViewModel> BuildModuleCards(
            IReadOnlyList<ModuleConfig> modules,
            HomeDiagnosticsSnapshot diagnostics)
        {
            // Console buttons are offered only while the consoles sidebar is enabled in the app settings.
            var rows = new List<HomeModuleCardViewModel>();
            _appData.Data.Normalize();
            var canShowConsoleActions = _appData.Data.Consoles.SidebarVisible;

            foreach (var module in modules)
            {
                // Look up this module's diagnostics, its busy state, and whether it has console sessions.
                diagnostics.ModulesBySourcePath.TryGetValue(module.SourcePath, out var moduleDiagnostics);
                var operationState = GetOperationState(module.SourcePath);
                var hasConsole = (moduleDiagnostics?.ConsoleSessionCount ?? 0) > 0;

                // The secondary line shows the status; stopped modules append a description, author, or default text.
                var statusText = module.Status.Enabled
                    ? L.Get(LocalizationKeys.Home_Metric_Running)
                    : L.Get(LocalizationKeys.Home_Module_Stopped);

                var secondaryParts = new List<string> { statusText };

                if (!module.Status.Enabled)
                {
                    secondaryParts.Add(BuildModuleSecondaryText(module));
                }

                // The busy and idle flags drive which action buttons the row offers;
                // each command captures the manifest path.
                rows.Add(new HomeModuleCardViewModel
                {
                    SourcePath = module.SourcePath,
                    Name = module.Name,
                    SecondaryText = string.Join(" | ", secondaryParts),
                    CanOpenConsole = canShowConsoleActions && (module.Status.Enabled || hasConsole),
                    IsBusy = operationState.IsBusy,
                    BusyText = operationState.BusyText,
                    IsRunningAndIdle = module.Status.Enabled && !operationState.IsBusy,
                    IsStoppedAndIdle = !module.Status.Enabled && !operationState.IsBusy,
                    LaunchCommand = new Command(async () => await LaunchModuleAsync(module.SourcePath)),
                    StopCommand = new Command(async () => await StopModuleAsync(module.SourcePath)),
                    RestartCommand = new Command(async () => await RestartModuleAsync(module.SourcePath)),
                    ConsoleCommand = new Command(() => OpenModuleConsole(module.SourcePath))
                });
            }

            return rows;
        }


        // Process tree

        /// <summary>
        /// Flattens the tree snapshot into a list of visible rows while preserving expand/collapse state.
        /// </summary>
        private IReadOnlyList<HomeProcessTreeNodeViewModel> FlattenTree(HomeProcessTreeNodeSnapshot root)
        {
            var visibleNodes = new List<HomeProcessTreeNodeViewModel>();
            AppendVisibleNode(root, depth: 0, visibleNodes);
            return visibleNodes;
        }

        /// <summary>
        /// Recursively appends one visible tree node and its expanded descendants.
        /// </summary>
        private void AppendVisibleNode(
            HomeProcessTreeNodeSnapshot node,
            int depth,
            List<HomeProcessTreeNodeViewModel> visibleNodes)
        {
            // Leaves are never expanded and the fixed ASLM root always is; other nodes use the remembered state.
            var isExpanded = node.Children.Count == 0
                ? false
                : IsFixedRootNode(node.Key)
                    ? true
                    : GetExpandedState(node.Key, node.DefaultExpanded);

            // Emit this node's row; depth indents it by 18 units per level and the root has no expander.
            visibleNodes.Add(new HomeProcessTreeNodeViewModel
            {
                Key = node.Key,
                Title = node.Title,
                TitleColor = node.TitleColor,
                TitleFontAttributes = node.TitleFontAttributes,
                CpuText = node.CpuText,
                GpuText = node.GpuText,
                MemoryText = node.MemoryText,
                DiskText = node.DiskText,
                NetworkText = node.NetworkText,
                DetailsText = node.DetailsText,
                IndentMargin = new Thickness(depth * 18, 0, 0, 0),
                CanExpand = node.Children.Count > 0 && !IsFixedRootNode(node.Key),
                ExpanderIconSource = node.Children.Count == 0 || IsFixedRootNode(node.Key)
                    ? string.Empty
                    : isExpanded
                        ? "icon_arrow_down.png"
                        : "icon_arrow_right.png",
                ToggleCommand = new Command(() =>
                {
                    if (node.Children.Count > 0 && !IsFixedRootNode(node.Key))
                    {
                        ToggleNodeExpansion(node.Key);
                    }
                })
            });

            // Descendants are listed only below an expanded node.
            if (!isExpanded)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                AppendVisibleNode(child, depth + 1, visibleNodes);
            }
        }

        /// <summary>
        /// Returns the persisted expansion state for one tree node, or a sensible default.
        /// </summary>
        private bool GetExpandedState(string nodeKey, bool defaultExpanded)
        {
            return _expandedNodes.TryGetValue(nodeKey, out var isExpanded)
                ? isExpanded
                : defaultExpanded;
        }

        /// <summary>
        /// Returns whether the supplied node is the fixed ASLM root.
        /// </summary>
        private static bool IsFixedRootNode(string nodeKey)
        {
            return string.Equals(nodeKey, "root:aslm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Toggles the expansion state of one tree node and rerenders the visible tree.
        /// </summary>
        private void ToggleNodeExpansion(string nodeKey)
        {
            if (IsFixedRootNode(nodeKey))
            {
                return;
            }

            var current = GetExpandedState(nodeKey, defaultExpanded: false);
            _expandedNodes[nodeKey] = !current;
            RenderCachedState();
        }


        // Cached rendering and operation state

        /// <summary>
        /// Re-renders the latest captured dashboard snapshot without sampling diagnostics again.
        /// </summary>
        private void RenderCachedState()
        {
            if (_lastDiagnosticsSnapshot == null)
            {
                return;
            }

            var state = BuildState(_knownModules, _lastConsoleSnapshots, _lastDiagnosticsSnapshot);
            MainThread.BeginInvokeOnMainThread(() => _view.Render(state));
        }

        /// <summary>
        /// Returns the shared operation state object for one module.
        /// </summary>
        private HomeModuleOperationState GetOperationState(string sourcePath)
        {
            if (!_moduleOperations.TryGetValue(sourcePath, out var operationState))
            {
                operationState = new HomeModuleOperationState();
                _moduleOperations[sourcePath] = operationState;
            }

            return operationState;
        }


        // Module actions

        /// <summary>
        /// Launches one module using the same lifecycle as the main modules page.
        /// </summary>
        private async Task LaunchModuleAsync(string sourcePath)
        {
            // Ignore the request while another action on this module is still running.
            var operationState = GetOperationState(sourcePath);
            if (operationState.IsBusy)
            {
                return;
            }

            // Show the busy state immediately; a light refresh renders it without rediscovering modules.
            operationState.StartLaunching();
            await RefreshAsync(forceModuleReload: false);

            try
            {
                // Launch the module (or confirm it is already running), then reload its effective manifest from disk.
                var launchLog = new Progress<string>(message => Debug.WriteLine($"[Launch] {message}"));
                var result = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    sourcePath,
                    launchLog,
                    CancellationToken.None);

                if (result.EffectiveConfig != null)
                {
                    await ReloadModuleAsync(result.EffectiveConfig.SourcePath);
                }
            }
            finally
            {
                // Always clear the busy state and rediscover modules, even when the launch failed.
                operationState.Clear();
                await RefreshAsync(forceModuleReload: true);
            }
        }

        /// <summary>
        /// Stops one module and persists the disabled state.
        /// </summary>
        private async Task StopModuleAsync(string sourcePath)
        {
            // Ignore the request while another action on this module is still running.
            var operationState = GetOperationState(sourcePath);
            if (operationState.IsBusy)
            {
                return;
            }

            // Show the busy state immediately; a light refresh renders it without rediscovering modules.
            operationState.StartStopping();
            await RefreshAsync(forceModuleReload: false);

            try
            {
                // Stop the module processes, then reload its manifest from disk before updating it.
                await Task.Run(() => _moduleRunner.StopModuleAsync(sourcePath));

                var module = await ReloadModuleAsync(sourcePath);
                if (module == null)
                {
                    return;
                }

                // Persist the disabled flag and mirror it in the console store.
                module.Status.Enabled = false;
                await Task.Run(() => _moduleInstaller.SaveConfigAsync(module));
                _consoleStore.UpdateModuleEnabledState(module.SourcePath, false);
            }
            finally
            {
                // Always clear the busy state and rediscover modules, even when stopping failed.
                operationState.Clear();
                await RefreshAsync(forceModuleReload: true);
            }
        }

        /// <summary>
        /// Restarts one module while keeping its enabled state.
        /// </summary>
        private async Task RestartModuleAsync(string sourcePath)
        {
            // Ignore the request while another action on this module is still running.
            var operationState = GetOperationState(sourcePath);
            if (operationState.IsBusy)
            {
                return;
            }

            // Show the busy state immediately; a light refresh renders it without rediscovering modules.
            operationState.StartRestarting();
            await RefreshAsync(forceModuleReload: false);

            try
            {
                // Stop the processes without saving a disabled state, then wait one second before launching again.
                await Task.Run(() => _moduleRunner.StopModuleAsync(sourcePath));
                await Task.Delay(1000);

                // Launch the module again and reload its effective manifest from disk.
                var restartLog = new Progress<string>(message => Debug.WriteLine($"[Restart] {message}"));
                var result = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    sourcePath,
                    restartLog,
                    CancellationToken.None);

                if (result.EffectiveConfig != null)
                {
                    await ReloadModuleAsync(result.EffectiveConfig.SourcePath);
                }
            }
            finally
            {
                // Always clear the busy state and rediscover modules, even when the restart failed.
                operationState.Clear();
                await RefreshAsync(forceModuleReload: true);
            }
        }

        /// <summary>
        /// Loads the latest module manifest from disk and updates the cached module list.
        /// </summary>
        private async Task<ModuleConfig?> ReloadModuleAsync(string sourcePath)
        {
            var module = await Task.Run(() => _moduleInstaller.LoadModuleConfig(sourcePath));
            if (module == null)
            {
                return null;
            }

            var existingIndex = _knownModules.FindIndex(candidate =>
                string.Equals(candidate.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                _knownModules[existingIndex] = module;
            }
            else
            {
                _knownModules.Add(module);
            }

            return module;
        }

        /// <summary>
        /// Opens the consoles page focused on the requested module.
        /// </summary>
        private void OpenModuleConsole(string sourcePath)
        {
            MainThread.BeginInvokeOnMainThread(() => _shell?.OpenConsoles(sourcePath));
        }


        // Metric and formatting helpers

        /// <summary>
        /// Creates a standard percent-based summary metric row with a progress bar clamped to 0-100% and a severity color.
        /// </summary>
        private static HomeSummaryMetricViewModel CreatePercentMetric(string name, double percentValue, string detailText)
        {
            var accent = GetSeverityColor(percentValue / 100d);
            return new HomeSummaryMetricViewModel
            {
                Name = name,
                ValueText = $"{percentValue:F1}%",
                DetailText = detailText,
                HasProgress = true,
                ProgressValue = Math.Clamp(percentValue / 100d, 0, 1),
                AccentColor = accent
            };
        }

        /// <summary>
        /// Creates a summary metric row for an optional percent-based signal;
        /// a missing value yields a "not available" row without a progress bar.
        /// </summary>
        private static HomeSummaryMetricViewModel CreateOptionalPercentMetric(string name, double? percentValue, string detailText)
        {
            if (!percentValue.HasValue)
            {
                return new HomeSummaryMetricViewModel
                {
                    Name = name,
                    ValueText = L.Get(LocalizationKeys.Home_Metric_NotAvailable),
                    DetailText = detailText,
                    HasProgress = false,
                    ProgressValue = 0,
                    AccentColor = Color.FromArgb("#225E5CE6")
                };
            }

            return CreatePercentMetric(name, percentValue.Value, detailText);
        }

        /// <summary>
        /// Creates a summary metric row without a progress bar.
        /// </summary>
        private static HomeSummaryMetricViewModel CreateInformationalMetric(
            string name,
            string valueText,
            string detailText,
            Color accentColor)
        {
            return new HomeSummaryMetricViewModel
            {
                Name = name,
                ValueText = valueText,
                DetailText = detailText,
                HasProgress = false,
                ProgressValue = 0,
                AccentColor = accentColor
            };
        }

        /// <summary>
        /// Builds summary metrics for each detected GPU adapter, using either the ASLM-runtime or the system-wide
        /// utilization of every adapter.
        /// </summary>
        private static IReadOnlyList<HomeSummaryMetricViewModel> BuildGpuMetrics(
            IReadOnlyList<HomeGpuAdapterSnapshot> adapters,
            bool useRuntimeValues)
        {
            // Without any adapter show a single "not available" row so the card layout stays stable.
            if (adapters.Count == 0)
            {
                return
                [
                    new HomeSummaryMetricViewModel
                    {
                        Name = L.Get(LocalizationKeys.Home_Column_Gpu),
                        ValueText = L.Get(LocalizationKeys.Home_Metric_NotAvailable),
                        DetailText = L.Get(LocalizationKeys.Home_Metric_Gpu_Unavailable),
                        HasProgress = false,
                        AccentColor = Color.FromArgb("#225E5CE6")
                    }
                ];
            }

            // One percent row per adapter in adapter-index order, labeled with the index and detailed with the name.
            var metrics = new List<HomeSummaryMetricViewModel>();
            foreach (var adapter in adapters.OrderBy(adapter => adapter.AdapterIndex))
            {
                var value = useRuntimeValues
                    ? adapter.RuntimePercent
                    : adapter.SystemPercent;

                var label = L.Get(LocalizationKeys.Home_Metric_Gpu_Index, adapter.AdapterIndex);

                metrics.Add(CreatePercentMetric(label, value, adapter.Name));
            }

            return metrics;
        }

        /// <summary>
        /// Builds the compact resource badges shown on module cards and tree rows.
        /// </summary>
        internal static IReadOnlyList<HomeMetricBadgeViewModel> BuildUsageBadges(
            HomeUsageSnapshot usage,
            int processCount,
            int sessionCount)
        {
            // Always show CPU, GPU (or an unavailable marker), RAM, disk I/O, and network connections.
            var badges = new List<HomeMetricBadgeViewModel>
            {
                CreateBadge($"CPU {usage.CpuPercent:F1}%", Color.FromArgb("#220A84FF"))
            };

            badges.Add(usage.GpuPercent.HasValue
                ? CreateBadge(L.Get(LocalizationKeys.Home_Badge_GpuPercentFormat, usage.GpuPercent.Value), Color.FromArgb("#225E5CE6"))
                : CreateBadge(L.Get(LocalizationKeys.Home_Badge_GpuUnavailable), Color.FromArgb("#2238383A")));

            badges.Add(CreateBadge(L.Get(LocalizationKeys.Home_Badge_RamFormat, FormatBytes(usage.MemoryBytes)), Color.FromArgb("#2232D74B")));
            badges.Add(CreateBadge(L.Get(LocalizationKeys.Home_Badge_DiskFormat, FormatRate(usage.DiskIoBytesPerSec)), Color.FromArgb("#22FF9F0A")));
            badges.Add(CreateBadge(L.Get(LocalizationKeys.Home_Badge_NetFormat, FormatConnectionCount(usage.ConnectionCount)), Color.FromArgb("#2264D2FF")));

            // Process and session counts appear only when there is something to count.
            if (processCount > 0)
            {
                badges.Add(CreateBadge(L.Get(LocalizationKeys.Home_Badge_ProcessesFormat, processCount), Color.FromArgb("#220A84FF")));
            }

            if (sessionCount > 0)
            {
                badges.Add(CreateBadge(L.Get(LocalizationKeys.Home_Badge_SessionsFormat, sessionCount), Color.FromArgb("#225E5CE6")));
            }

            return badges;
        }

        /// <summary>
        /// Creates one compact badge view model.
        /// </summary>
        private static HomeMetricBadgeViewModel CreateBadge(string text, Color backgroundColor)
        {
            return new HomeMetricBadgeViewModel
            {
                Text = text,
                BackgroundColor = backgroundColor
            };
        }

        /// <summary>
        /// Builds the secondary text shown under one module card title: its description when present,
        /// otherwise an author credit, otherwise a generic installed label.
        /// </summary>
        private static string BuildModuleSecondaryText(ModuleConfig module)
        {
            if (!string.IsNullOrWhiteSpace(module.Description))
            {
                return module.Description;
            }

            return string.IsNullOrWhiteSpace(module.Author)
                ? L.Get(LocalizationKeys.Home_Module_InstalledDefault)
                : L.Get(LocalizationKeys.Home_Module_ByAuthor, module.Author);
        }

        /// <summary>
        /// Formats byte counts using a compact 1024-based unit scale (B to TB), with no decimals for plain bytes
        /// and values of 100 or more, and one decimal otherwise.
        /// </summary>
        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            var unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return size >= 100 || unitIndex == 0
                ? $"{size:F0} {units[unitIndex]}"
                : $"{size:F1} {units[unitIndex]}";
        }

        /// <summary>
        /// Formats a byte throughput value in bytes per second using the same 1024-based scale and
        /// decimal rules as <see cref="FormatBytes"/>.
        /// </summary>
        internal static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "0 B/s";
            }

            string[] units = ["B/s", "KB/s", "MB/s", "GB/s", "TB/s"];
            var size = bytesPerSecond;
            var unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return size >= 100 || unitIndex == 0
                ? $"{size:F0} {units[unitIndex]}"
                : $"{size:F1} {units[unitIndex]}";
        }

        /// <summary>
        /// Formats a localized network-connection count, using a dedicated text for exactly one connection.
        /// </summary>
        private static string FormatConnectionCount(int connectionCount)
        {
            return connectionCount == 1
                ? L.Get(LocalizationKeys.Home_Metric_ConnectionOne)
                : L.Get(LocalizationKeys.Home_Metric_ConnectionManyFormat, connectionCount);
        }

        /// <summary>
        /// Formats a percent value with one decimal and a percent sign.
        /// </summary>
        internal static string FormatPercent(double value)
        {
            return $"{value:F1}%";
        }

        /// <summary>
        /// Formats an optional percent value with one decimal, or returns the placeholder <c>n/a</c> when it is missing.
        /// </summary>
        internal static string FormatPercentOrNa(double? value)
        {
            return value.HasValue
                ? $"{value.Value:F1}%"
                : "n/a";
        }

        /// <summary>
        /// Formats compact integer counts for narrow table columns.
        /// </summary>
        internal static string FormatCompactCount(int count)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns a severity color for one normalized usage value: red from 0.8, yellow from 0.5, green below.
        /// </summary>
        private static Color GetSeverityColor(double normalizedValue)
        {
            if (normalizedValue >= 0.8)
            {
                return Color.FromArgb("#FFFF453A");
            }

            if (normalizedValue >= 0.5)
            {
                return Color.FromArgb("#FFFFD60A");
            }

            return Color.FromArgb("#FF32D74B");
        }
    }


    // Dashboard state

    /// <summary>
    /// Represents the full UI state required to render the home dashboard.
    /// </summary>
    internal sealed class HomeDashboardState
    {
        /// <summary>
        /// Gets or sets the summary cards shown above the main workspace.
        /// </summary>
        public IReadOnlyList<HomeSummaryCardViewModel> SummaryCards { get; set; } = [];

        /// <summary>
        /// Gets or sets the visible rows of the managed process tree.
        /// </summary>
        public IReadOnlyList<HomeProcessTreeNodeViewModel> ProcessNodes { get; set; } = [];

        /// <summary>
        /// Gets or sets the module dashboard rows.
        /// </summary>
        public IReadOnlyList<HomeModuleCardViewModel> Modules { get; set; } = [];
    }

    /// <summary>
    /// Stores one fully prepared refresh payload before it is rendered on the UI thread.
    /// </summary>
    internal sealed class HomeRefreshSnapshot
    {
        /// <summary>
        /// Gets or sets the latest console snapshots.
        /// </summary>
        public IReadOnlyList<ModuleConsoleModuleSnapshot> ConsoleSnapshots { get; set; } = [];

        /// <summary>
        /// Gets or sets the latest diagnostics snapshot.
        /// </summary>
        public HomeDiagnosticsSnapshot Diagnostics { get; set; } = new();

        /// <summary>
        /// Gets or sets the render-ready dashboard state.
        /// </summary>
        public HomeDashboardState State { get; set; } = new();
    }


    // View models

    /// <summary>
    /// Exposes bindable dashboard state for the home page shell.
    /// </summary>
    public sealed class HomeDashboardPageViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="SummaryGridSpan"/>.
        /// </summary>
        private int _summaryGridSpan = 1;


        // Events

        /// <summary>
        /// Raised when a bindable property changes so the UI refreshes its binding.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Initialization

        /// <summary>
        /// Creates the bindable home dashboard state with a stable initial skeleton.
        /// </summary>
        public HomeDashboardPageViewModel()
        {
            SeedSummaryCards();
        }


        // Bindable state

        /// <summary>
        /// Gets the summary cards shown at the top of the dashboard.
        /// </summary>
        public ObservableCollection<HomeSummaryCardViewModel> SummaryCards { get; } = new();

        /// <summary>
        /// Gets the visible rows of the managed process tree.
        /// </summary>
        public ObservableCollection<HomeProcessTreeNodeViewModel> ProcessNodes { get; } = new();

        /// <summary>
        /// Gets the module rows shown in the modules panel.
        /// </summary>
        public ObservableCollection<HomeModuleCardViewModel> Modules { get; } = new();

        /// <summary>
        /// Gets or sets the number of columns used by the summary-card grid.
        /// </summary>
        public int SummaryGridSpan
        {
            get => _summaryGridSpan;
            set => SetProperty(ref _summaryGridSpan, value);
        }


        // Placeholder cards

        /// <summary>
        /// Adds placeholder summary cards so the top dashboard block does not pop in late.
        /// They use the keys of the real cards, so the first refresh updates them in place.
        /// </summary>
        private void SeedSummaryCards()
        {
            SummaryCards.Add(CreateLoadingCard(
                "system",
                L.Get(LocalizationKeys.Home_Card_System_Title),
                L.Get(LocalizationKeys.Home_Card_System_Subtitle),
                L.Get(LocalizationKeys.Home_Status_Loading)));
            SummaryCards.Add(CreateLoadingCard(
                "runtime",
                L.Get(LocalizationKeys.Home_Card_Runtime_Title),
                L.Get(LocalizationKeys.Home_Card_Runtime_Subtitle),
                L.Get(LocalizationKeys.Home_Status_Loading)));
            SummaryCards.Add(CreateLoadingCard(
                "modules",
                L.Get(LocalizationKeys.Home_Card_Modules_Title),
                L.Get(LocalizationKeys.Home_Card_Modules_Subtitle),
                L.Get(LocalizationKeys.Home_Status_Loading)));
        }

        /// <summary>
        /// Creates one stable summary placeholder while the first diagnostics refresh runs.
        /// </summary>
        private static HomeSummaryCardViewModel CreateLoadingCard(
            string key,
            string title,
            string subtitle,
            string statusText) =>
            new()
            {
                Key = key,
                Title = title,
                Subtitle = subtitle,
                StatusText = statusText,
                Metrics =
                [
                    new HomeSummaryMetricViewModel
                    {
                        Name = L.Get(LocalizationKeys.Home_Column_Cpu),
                        ValueText = "...",
                        DetailText = L.Get(LocalizationKeys.Home_Loading_WaitingSample)
                    },
                    new HomeSummaryMetricViewModel
                    {
                        Name = L.Get(LocalizationKeys.Home_Column_Ram),
                        ValueText = "...",
                        DetailText = L.Get(LocalizationKeys.Home_Loading_WaitingSample)
                    },
                    new HomeSummaryMetricViewModel
                    {
                        Name = L.Get(LocalizationKeys.Home_Column_Disk),
                        ValueText = "...",
                        DetailText = L.Get(LocalizationKeys.Home_Loading_WaitingSample)
                    }
                ]
            };


        // Change notification

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
    /// Provides a small bindable base implementation for dashboard item models.
    /// Item instances are kept alive between refreshes and updated in place, so every change must be notified.
    /// </summary>
    public abstract class HomeBindableItemViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// Raised when a bindable property changes so the UI refreshes its binding.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Updates a bindable field and raises <see cref="PropertyChanged"/> when it changes.
        /// </summary>
        protected void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
    /// Represents one summary card on the home dashboard.
    /// </summary>
    public sealed class HomeSummaryCardViewModel : HomeBindableItemViewModel
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="Key"/>.
        /// </summary>
        private string _key = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Title"/>.
        /// </summary>
        private string _title = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Subtitle"/>.
        /// </summary>
        private string _subtitle = string.Empty;

        /// <summary>
        /// Backing field of <see cref="StatusText"/>.
        /// </summary>
        private string _statusText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="AccentOverlayColor"/>.
        /// </summary>
        private Color _accentOverlayColor = Color.FromArgb("#220A84FF");

        /// <summary>
        /// Backing field of <see cref="Metrics"/>.
        /// </summary>
        private IReadOnlyList<HomeSummaryMetricViewModel> _metrics = [];

        /// <summary>
        /// Backing field of <see cref="FooterText"/>.
        /// </summary>
        private string _footerText = string.Empty;


        // Bindable properties

        /// <summary>
        /// Gets or sets the stable card key.
        /// </summary>
        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value);
        }

        /// <summary>
        /// Gets or sets the card title.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// Gets or sets the card subtitle.
        /// </summary>
        public string Subtitle
        {
            get => _subtitle;
            set => SetProperty(ref _subtitle, value);
        }

        /// <summary>
        /// Gets or sets the status pill text shown on the card.
        /// </summary>
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// Gets or sets the accent overlay color used by the status pill.
        /// </summary>
        public Color AccentOverlayColor
        {
            get => _accentOverlayColor;
            set => SetProperty(ref _accentOverlayColor, value);
        }

        /// <summary>
        /// Gets or sets the summary metrics shown inside the card.
        /// </summary>
        public IReadOnlyList<HomeSummaryMetricViewModel> Metrics
        {
            get => _metrics;
            set => SetProperty(ref _metrics, value);
        }

        /// <summary>
        /// Gets or sets the footer text shown at the bottom of the card.
        /// </summary>
        public string FooterText
        {
            get => _footerText;
            set => SetProperty(ref _footerText, value);
        }


        // Synchronization

        /// <summary>
        /// Applies the values from the latest presenter snapshot.
        /// </summary>
        public void CopyFrom(HomeSummaryCardViewModel source)
        {
            Key = source.Key;
            Title = source.Title;
            Subtitle = source.Subtitle;
            StatusText = source.StatusText;
            AccentOverlayColor = source.AccentOverlayColor;
            Metrics = source.Metrics;
            FooterText = source.FooterText;
        }
    }

    /// <summary>
    /// Represents one summary metric row inside a home dashboard card.
    /// </summary>
    public sealed class HomeSummaryMetricViewModel
    {
        /// <summary>
        /// Gets or sets the metric label.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the primary metric value.
        /// </summary>
        public string ValueText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the secondary explanatory text.
        /// </summary>
        public string DetailText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether a progress bar should be rendered for this metric.
        /// </summary>
        public bool HasProgress { get; set; }

        /// <summary>
        /// Gets or sets the progress bar fill as a fraction from 0 to 1.
        /// </summary>
        public double ProgressValue { get; set; }

        /// <summary>
        /// Gets or sets the accent color of the metric.
        /// </summary>
        public Color AccentColor { get; set; } = Color.FromArgb("#FF32D74B");
    }

    /// <summary>
    /// Represents one compact metric badge.
    /// </summary>
    public sealed class HomeMetricBadgeViewModel
    {
        /// <summary>
        /// Gets or sets the badge text.
        /// </summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the badge background color.
        /// </summary>
        public Color BackgroundColor { get; set; } = Color.FromArgb("#2238383A");
    }

    /// <summary>
    /// Represents one module row in the home dashboard.
    /// </summary>
    public sealed class HomeModuleCardViewModel : HomeBindableItemViewModel
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
        /// Backing field of <see cref="SecondaryText"/>.
        /// </summary>
        private string _secondaryText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="CanOpenConsole"/>.
        /// </summary>
        private bool _canOpenConsole;

        /// <summary>
        /// Backing field of <see cref="IsBusy"/>.
        /// </summary>
        private bool _isBusy;

        /// <summary>
        /// Backing field of <see cref="BusyText"/>.
        /// </summary>
        private string _busyText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="IsRunningAndIdle"/>.
        /// </summary>
        private bool _isRunningAndIdle;

        /// <summary>
        /// Backing field of <see cref="IsStoppedAndIdle"/>.
        /// </summary>
        private bool _isStoppedAndIdle;

        /// <summary>
        /// Backing field of <see cref="LaunchCommand"/>; a no-op command until the presenter assigns a real one.
        /// </summary>
        private ICommand _launchCommand = new Command(() => { });

        /// <summary>
        /// Backing field of <see cref="StopCommand"/>; a no-op command until the presenter assigns a real one.
        /// </summary>
        private ICommand _stopCommand = new Command(() => { });

        /// <summary>
        /// Backing field of <see cref="RestartCommand"/>; a no-op command until the presenter assigns a real one.
        /// </summary>
        private ICommand _restartCommand = new Command(() => { });

        /// <summary>
        /// Backing field of <see cref="ConsoleCommand"/>; a no-op command until the presenter assigns a real one.
        /// </summary>
        private ICommand _consoleCommand = new Command(() => { });


        // Bindable properties

        /// <summary>
        /// Gets or sets the stable module source path.
        /// </summary>
        public string SourcePath
        {
            get => _sourcePath;
            set => SetProperty(ref _sourcePath, value);
        }

        /// <summary>
        /// Gets or sets the module name.
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>
        /// Gets or sets the secondary module description line.
        /// </summary>
        public string SecondaryText
        {
            get => _secondaryText;
            set => SetProperty(ref _secondaryText, value);
        }

        /// <summary>
        /// Gets or sets whether the console button should stay enabled.
        /// </summary>
        public bool CanOpenConsole
        {
            get => _canOpenConsole;
            set => SetProperty(ref _canOpenConsole, value);
        }

        /// <summary>
        /// Gets or sets whether the row is currently showing a long-running module action.
        /// </summary>
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        /// <summary>
        /// Gets or sets the busy-state text.
        /// </summary>
        public string BusyText
        {
            get => _busyText;
            set => SetProperty(ref _busyText, value);
        }

        /// <summary>
        /// Gets or sets whether the running-state button row is visible.
        /// </summary>
        public bool IsRunningAndIdle
        {
            get => _isRunningAndIdle;
            set => SetProperty(ref _isRunningAndIdle, value);
        }

        /// <summary>
        /// Gets or sets whether the stopped-state launch button is visible.
        /// </summary>
        public bool IsStoppedAndIdle
        {
            get => _isStoppedAndIdle;
            set => SetProperty(ref _isStoppedAndIdle, value);
        }

        /// <summary>
        /// Gets or sets the launch command.
        /// </summary>
        public ICommand LaunchCommand
        {
            get => _launchCommand;
            set => SetProperty(ref _launchCommand, value);
        }

        /// <summary>
        /// Gets or sets the stop command.
        /// </summary>
        public ICommand StopCommand
        {
            get => _stopCommand;
            set => SetProperty(ref _stopCommand, value);
        }

        /// <summary>
        /// Gets or sets the restart command.
        /// </summary>
        public ICommand RestartCommand
        {
            get => _restartCommand;
            set => SetProperty(ref _restartCommand, value);
        }

        /// <summary>
        /// Gets or sets the console-navigation command.
        /// </summary>
        public ICommand ConsoleCommand
        {
            get => _consoleCommand;
            set => SetProperty(ref _consoleCommand, value);
        }


        // Synchronization

        /// <summary>
        /// Applies the values from the latest presenter snapshot.
        /// </summary>
        public void CopyFrom(HomeModuleCardViewModel source)
        {
            SourcePath = source.SourcePath;
            Name = source.Name;
            SecondaryText = source.SecondaryText;
            CanOpenConsole = source.CanOpenConsole;
            IsBusy = source.IsBusy;
            BusyText = source.BusyText;
            IsRunningAndIdle = source.IsRunningAndIdle;
            IsStoppedAndIdle = source.IsStoppedAndIdle;
            LaunchCommand = source.LaunchCommand;
            StopCommand = source.StopCommand;
            RestartCommand = source.RestartCommand;
            ConsoleCommand = source.ConsoleCommand;
        }
    }

    /// <summary>
    /// Represents one visible row of the managed process tree.
    /// </summary>
    public sealed class HomeProcessTreeNodeViewModel : HomeBindableItemViewModel
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="Key"/>.
        /// </summary>
        private string _key = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Title"/>.
        /// </summary>
        private string _title = string.Empty;

        /// <summary>
        /// Backing field of <see cref="TitleColor"/>; starts with the primary label color of the active theme.
        /// </summary>
        private Color _titleColor = HomePaletteColors.LabelPrimary;

        /// <summary>
        /// Backing field of <see cref="TitleFontAttributes"/>.
        /// </summary>
        private FontAttributes _titleFontAttributes = FontAttributes.None;

        /// <summary>
        /// Backing field of <see cref="CpuText"/>.
        /// </summary>
        private string _cpuText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="GpuText"/>.
        /// </summary>
        private string _gpuText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="MemoryText"/>.
        /// </summary>
        private string _memoryText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="DiskText"/>.
        /// </summary>
        private string _diskText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="NetworkText"/>.
        /// </summary>
        private string _networkText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="DetailsText"/>.
        /// </summary>
        private string _detailsText = string.Empty;

        /// <summary>
        /// Backing field of <see cref="IndentMargin"/>.
        /// </summary>
        private Thickness _indentMargin;

        /// <summary>
        /// Backing field of <see cref="CanExpand"/>.
        /// </summary>
        private bool _canExpand;

        /// <summary>
        /// Backing field of <see cref="ExpanderIconSource"/>.
        /// </summary>
        private string _expanderIconSource = string.Empty;

        /// <summary>
        /// Backing field of <see cref="ToggleCommand"/>; a no-op command until the presenter assigns a real one.
        /// </summary>
        private ICommand _toggleCommand = new Command(() => { });


        // Bindable properties

        /// <summary>
        /// Gets or sets the stable row key.
        /// </summary>
        public string Key
        {
            get => _key;
            set => SetProperty(ref _key, value);
        }

        /// <summary>
        /// Gets or sets the row title.
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        /// <summary>
        /// Gets or sets the row title color.
        /// </summary>
        public Color TitleColor
        {
            get => _titleColor;
            set => SetProperty(ref _titleColor, value);
        }

        /// <summary>
        /// Gets or sets the title font weight.
        /// </summary>
        public FontAttributes TitleFontAttributes
        {
            get => _titleFontAttributes;
            set => SetProperty(ref _titleFontAttributes, value);
        }

        /// <summary>
        /// Gets or sets the CPU column text.
        /// </summary>
        public string CpuText
        {
            get => _cpuText;
            set => SetProperty(ref _cpuText, value);
        }

        /// <summary>
        /// Gets or sets the GPU column text.
        /// </summary>
        public string GpuText
        {
            get => _gpuText;
            set => SetProperty(ref _gpuText, value);
        }

        /// <summary>
        /// Gets or sets the memory column text.
        /// </summary>
        public string MemoryText
        {
            get => _memoryText;
            set => SetProperty(ref _memoryText, value);
        }

        /// <summary>
        /// Gets or sets the disk column text.
        /// </summary>
        public string DiskText
        {
            get => _diskText;
            set => SetProperty(ref _diskText, value);
        }

        /// <summary>
        /// Gets or sets the network column text.
        /// </summary>
        public string NetworkText
        {
            get => _networkText;
            set => SetProperty(ref _networkText, value);
        }

        /// <summary>
        /// Gets or sets the trailing details column text.
        /// </summary>
        public string DetailsText
        {
            get => _detailsText;
            set => SetProperty(ref _detailsText, value);
        }

        /// <summary>
        /// Gets or sets the left margin used to indent the row according to tree depth.
        /// </summary>
        public Thickness IndentMargin
        {
            get => _indentMargin;
            set => SetProperty(ref _indentMargin, value);
        }

        /// <summary>
        /// Gets or sets whether the row can be expanded or collapsed.
        /// </summary>
        public bool CanExpand
        {
            get => _canExpand;
            set => SetProperty(ref _canExpand, value);
        }

        /// <summary>
        /// Gets or sets the current expander icon source.
        /// </summary>
        public string ExpanderIconSource
        {
            get => _expanderIconSource;
            set => SetProperty(ref _expanderIconSource, value);
        }

        /// <summary>
        /// Gets or sets the command used to toggle expansion.
        /// </summary>
        public ICommand ToggleCommand
        {
            get => _toggleCommand;
            set => SetProperty(ref _toggleCommand, value);
        }


        // Synchronization

        /// <summary>
        /// Applies the values from the latest presenter snapshot.
        /// </summary>
        public void CopyFrom(HomeProcessTreeNodeViewModel source)
        {
            Key = source.Key;
            Title = source.Title;
            TitleColor = source.TitleColor;
            TitleFontAttributes = source.TitleFontAttributes;
            CpuText = source.CpuText;
            GpuText = source.GpuText;
            MemoryText = source.MemoryText;
            DiskText = source.DiskText;
            NetworkText = source.NetworkText;
            DetailsText = source.DetailsText;
            IndentMargin = source.IndentMargin;
            CanExpand = source.CanExpand;
            ExpanderIconSource = source.ExpanderIconSource;
            ToggleCommand = source.ToggleCommand;
        }
    }


    // Module action state

    /// <summary>
    /// Tracks the busy state of one module action initiated from the home dashboard.
    /// </summary>
    internal sealed class HomeModuleOperationState
    {
        // Fields

        /// <summary>
        /// Localized busy text of the current action, or empty while idle.
        /// </summary>
        private string _busyText = string.Empty;


        // State

        /// <summary>
        /// Gets whether the module is currently processing an action.
        /// </summary>
        public bool IsBusy { get; private set; }

        /// <summary>
        /// Gets the user-visible busy-state text.
        /// </summary>
        public string BusyText => _busyText;


        // Transitions

        /// <summary>
        /// Marks the module as launching.
        /// </summary>
        public void StartLaunching()
        {
            IsBusy = true;
            _busyText = L.Get(LocalizationKeys.Home_Module_Launching);
        }

        /// <summary>
        /// Marks the module as stopping.
        /// </summary>
        public void StartStopping()
        {
            IsBusy = true;
            _busyText = L.Get(LocalizationKeys.Home_Module_Stopping);
        }

        /// <summary>
        /// Marks the module as restarting.
        /// </summary>
        public void StartRestarting()
        {
            IsBusy = true;
            _busyText = L.Get(LocalizationKeys.Modules_Restarting);
        }

        /// <summary>
        /// Clears the busy state after the module action completes.
        /// </summary>
        public void Clear()
        {
            IsBusy = false;
            _busyText = string.Empty;
        }
    }


    // Diagnostics collection

    /// <summary>
    /// Captures live system and process diagnostics used by the home dashboard.
    /// Cumulative counters (CPU time, I/O bytes, network bytes) are turned into rates by comparing each sample
    /// with the previous one, so the first sample of each reports zero. The slower queries (disk, GPU, connection
    /// counts) are cached for a few seconds between refreshes.
    /// </summary>
    internal sealed class HomeDiagnosticsCollector
    {
        // Static state

        /// <summary>
        /// Parses a Windows GPU Engine performance-counter instance name and captures, in order, the process id,
        /// the physical adapter index, and the engine index.
        /// </summary>
        private static readonly Regex GpuInstanceRegex =
            new(@"pid_(\d+).*?phys_(\d+).*?eng_(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
#if WINDOWS
        /// <summary>
        /// Display names of the installed GPU adapters, queried once and reused for the lifetime of the process.
        /// </summary>
        private static IReadOnlyList<string>? _cachedGpuAdapterNames;
#endif


        // Collaborators and previous samples

        /// <summary>
        /// Reader of the cached operating-system process table.
        /// </summary>
        private readonly ProcessSnapshotReader _processSnapshots;

        /// <summary>
        /// Previous raw sample of every managed process by process id; the base of the next CPU and I/O deltas.
        /// </summary>
        private readonly Dictionary<int, HomeProcessSample> _processSamples = new();

        /// <summary>
        /// Previous system-wide CPU time counters, or null before the first sample.
        /// </summary>
        private HomeSystemCpuSample? _lastSystemCpuSample;

        /// <summary>
        /// Previous network byte counters, or null before the first sample.
        /// </summary>
        private HomeNetworkSample? _lastNetworkSample;


        // Query caches

        /// <summary>
        /// Result of the last disk statistics query, reused while it is fresh.
        /// </summary>
        private HomeDiskStats _cachedDiskStats = new();

        /// <summary>
        /// Time of the last disk statistics query.
        /// </summary>
        private DateTimeOffset _cachedDiskStatsUtc = DateTimeOffset.MinValue;

        /// <summary>
        /// Result of the last GPU usage query, reused while it is fresh.
        /// </summary>
        private HomeGpuQueryResult _cachedGpuQuery = new();

        /// <summary>
        /// Time of the last GPU usage query.
        /// </summary>
        private DateTimeOffset _cachedGpuQueryUtc = DateTimeOffset.MinValue;

        /// <summary>
        /// Result of the last connection count query: connection count by process id.
        /// </summary>
        private Dictionary<int, int> _cachedConnectionCounts = new();

        /// <summary>
        /// Process ids that the cached connection counts were computed for.
        /// </summary>
        private HashSet<int> _cachedConnectionProcessIds = new();

        /// <summary>
        /// Time of the last connection count query.
        /// </summary>
        private DateTimeOffset _cachedConnectionCountsUtc = DateTimeOffset.MinValue;


        // Initialization

        /// <summary>
        /// Creates the diagnostics collector used by the home dashboard presenter.
        /// </summary>
        public HomeDiagnosticsCollector(ProcessSnapshotReader processSnapshots)
        {
            _processSnapshots = processSnapshots;
        }


        // Capture

        /// <summary>
        /// Captures a complete diagnostics snapshot for the current dashboard refresh.
        /// </summary>
        public HomeDiagnosticsSnapshot Capture(
            IReadOnlyList<ModuleConfig> modules,
            IReadOnlyList<ModuleConsoleModuleSnapshot> consoleSnapshots)
        {
            // Read the process table (cached for up to 500 ms) and index it by pid and by parent pid.
            var capturedUtc = DateTimeOffset.UtcNow;
            var currentProcessId = Environment.ProcessId;
            var processSnapshotEntries = _processSnapshots.GetSnapshot(TimeSpan.FromMilliseconds(500));
            var entriesByPid = processSnapshotEntries.ToDictionary(entry => entry.ProcessId);
            var childrenByParent = processSnapshotEntries
                .GroupBy(entry => entry.ParentProcessId)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.ProcessId).ToList());

            // The managed set is the ASLM process itself plus every descendant it started.
            var managedPids = CollectDescendantPids(currentProcessId, childrenByParent);
            managedPids.Add(currentProcessId);

            // Take the system-wide samples; the slow queries (disk, GPU, connections) are cached between refreshes.
            var memoryStatus = GetMemoryStatus();
            var systemCpu = SampleSystemCpu(capturedUtc);
            var diskStats = GetDiskStatsSample(capturedUtc);
            var networkStats = SampleNetwork(capturedUtc);
            var gpuQuery = GetGpuUsageSample(capturedUtc);
            var gpuAdapters = BuildGpuAdapterSnapshots(gpuQuery, managedPids);
            var connectionCounts = GetConnectionCountsSample(capturedUtc, managedPids);

            // Sample every managed process; processes that vanished or cannot be read are left out.
            var liveProcesses = new Dictionary<int, HomeLiveProcessInfo>();
            foreach (var processId in managedPids.OrderBy(processId => processId))
            {
                if (TrySampleProcess(
                        processId,
                        capturedUtc,
                        entriesByPid,
                        gpuQuery.ProcessByPid,
                        connectionCounts,
                        memoryStatus.TotalPhysicalBytes,
                        out var liveProcess))
                {
                    liveProcesses[processId] = liveProcess;
                }
            }

            // Forget the previous samples of processes that are gone so the sample table does not grow.
            CleanupProcessSamples(liveProcesses.Keys);

            // Attribute live processes to modules using console sessions and the process tree.
            var moduleDiagnostics = BuildModuleDiagnostics(
                modules,
                consoleSnapshots,
                liveProcesses,
                entriesByPid,
                childrenByParent,
                currentProcessId,
                memoryStatus.TotalPhysicalBytes);

            // Internal processes are managed child processes that belong to no module.
            var assignedProcessIds = moduleDiagnostics.Values
                .SelectMany(module => module.ProcessIds)
                .ToHashSet();

            var internalProcessIds = liveProcesses.Keys
                .Where(processId => processId != currentProcessId && !assignedProcessIds.Contains(processId))
                .ToHashSet();

            // Runtime usage sums all managed processes; its GPU figure is the busiest adapter's runtime share.
            var runtimeUsage = AggregateUsage(liveProcesses.Values.Select(process => process.Usage), memoryStatus.TotalPhysicalBytes);
            runtimeUsage.GpuPercent = gpuAdapters.Count > 0 ? gpuAdapters.Max(adapter => adapter.RuntimePercent) : null;

            // Build the process tree shown in the dashboard: ASLM root, modules, and internal processes.
            var rootNode = BuildRootNode(
                currentProcessId,
                liveProcesses,
                moduleDiagnostics.Values,
                internalProcessIds,
                childrenByParent,
                entriesByPid,
                memoryStatus.TotalPhysicalBytes);

            var lastModuleActivities = moduleDiagnostics.Values
                .Where(module => module.LastActivityUtc.HasValue)
                .Select(module => module.LastActivityUtc!.Value)
                .ToList();

            // Assemble the snapshot; the system GPU load is that of the busiest adapter and the active process
            // count covers internal and module processes.
            return new HomeDiagnosticsSnapshot
            {
                CapturedUtc = capturedUtc,
                SystemUsage = new HomeUsageSnapshot
                {
                    CpuPercent = systemCpu,
                    GpuPercent = gpuAdapters.Count > 0 ? gpuAdapters.Max(adapter => adapter.SystemPercent) : null,
                    MemoryBytes = memoryStatus.UsedPhysicalBytes,
                    MemoryTotalBytes = memoryStatus.TotalPhysicalBytes,
                    DiskBusyPercent = diskStats.BusyPercent,
                    DiskReadBytesPerSec = diskStats.ReadBytesPerSecond,
                    DiskWriteBytesPerSec = diskStats.WriteBytesPerSecond,
                    NetworkReceiveBytesPerSec = networkStats.ReceiveBytesPerSecond,
                    NetworkSendBytesPerSec = networkStats.SendBytesPerSecond
                },
                RuntimeUsage = runtimeUsage,
                ModulesBySourcePath = moduleDiagnostics,
                GpuAdapters = gpuAdapters,
                RootNode = rootNode,
                ActiveProcessCount = internalProcessIds.Count + moduleDiagnostics.Values.Sum(module => module.ProcessIds.Count),
                ActiveModuleCount = moduleDiagnostics.Values.Count(module => module.ProcessIds.Count > 0),
                LastModuleActivityUtc = lastModuleActivities.Count > 0
                    ? lastModuleActivities.Max()
                    : null
            };
        }


        // Cached queries

        /// <summary>
        /// Returns a cached disk sample when the previous WMI query is still fresh enough.
        /// </summary>
        private HomeDiskStats GetDiskStatsSample(DateTimeOffset capturedUtc)
        {
            if ((capturedUtc - _cachedDiskStatsUtc).TotalMilliseconds < 3000)
            {
                return _cachedDiskStats;
            }

            _cachedDiskStats = QueryDiskStats();
            _cachedDiskStatsUtc = capturedUtc;
            return _cachedDiskStats;
        }

        /// <summary>
        /// Returns a cached GPU sample when the previous WMI query is still fresh enough.
        /// </summary>
        private HomeGpuQueryResult GetGpuUsageSample(DateTimeOffset capturedUtc)
        {
            if ((capturedUtc - _cachedGpuQueryUtc).TotalMilliseconds < 3000)
            {
                return _cachedGpuQuery;
            }

            _cachedGpuQuery = QueryGpuUsage();
            _cachedGpuQueryUtc = capturedUtc;
            return _cachedGpuQuery;
        }

        /// <summary>
        /// Returns cached per-process connection counts while the managed PID set is unchanged.
        /// </summary>
        private IReadOnlyDictionary<int, int> GetConnectionCountsSample(DateTimeOffset capturedUtc, IReadOnlyCollection<int> processIds)
        {
            if ((capturedUtc - _cachedConnectionCountsUtc).TotalMilliseconds < 2000 &&
                _cachedConnectionProcessIds.SetEquals(processIds))
            {
                return _cachedConnectionCounts;
            }

            _cachedConnectionCounts = QueryProcessConnectionCounts(processIds);
            _cachedConnectionProcessIds = processIds.ToHashSet();
            _cachedConnectionCountsUtc = capturedUtc;
            return _cachedConnectionCounts;
        }


        // Module diagnostics

        /// <summary>
        /// Builds one diagnostics snapshot per module from live processes and console sessions.
        /// A process is attributed to at most one module (the first module in the list that claims it) and
        /// observed processes (children discovered without stream capture) are never attributed to a module.
        /// </summary>
        private static Dictionary<string, HomeModuleDiagnostics> BuildModuleDiagnostics(
            IReadOnlyList<ModuleConfig> modules,
            IReadOnlyList<ModuleConsoleModuleSnapshot> consoleSnapshots,
            IReadOnlyDictionary<int, HomeLiveProcessInfo> liveProcesses,
            IReadOnlyDictionary<int, ProcessSnapshotEntry> entriesByPid,
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            int currentProcessId,
            long totalPhysicalBytes)
        {
            var diagnostics = new Dictionary<string, HomeModuleDiagnostics>(StringComparer.OrdinalIgnoreCase);
            var consoleByModule = consoleSnapshots.ToDictionary(snapshot => snapshot.SourcePath, StringComparer.OrdinalIgnoreCase);
            var globallyAssigned = new HashSet<int>();

            foreach (var module in modules)
            {
                consoleByModule.TryGetValue(module.SourcePath, out var consoleSnapshot);

                // One session per process id: a running session wins over a stopped one, then a tracked over an untracked.
                var sessionsByPid = (consoleSnapshot?.Sessions ?? [])
                    .Where(session => session.ProcessId.HasValue && !session.IsObservedProcess)
                    .GroupBy(session => session.ProcessId!.Value)
                    .ToDictionary(
                        group => group.Key,
                        group => group
                            .OrderByDescending(session => session.IsRunning)
                            .ThenByDescending(session => session.IsTrackedProcess)
                            .First());

                // Observed processes (children discovered without stream capture) are kept out of the module's own set.
                var observedProcessIds = (consoleSnapshot?.Sessions ?? [])
                    .Where(session => session.IsObservedProcess &&
                                      session.ProcessId.HasValue)
                    .Select(session => session.ProcessId!.Value)
                    .ToHashSet();

                // Roots of the module's process trees: running tracked sessions whose process is still alive.
                var trackedRootPids = (consoleSnapshot?.Sessions ?? [])
                    .Where(session => session.IsRunning &&
                                      session.IsTrackedProcess &&
                                      !session.IsObservedProcess &&
                                      session.ProcessId.HasValue &&
                                      liveProcesses.ContainsKey(session.ProcessId.Value))
                    .Select(session => session.ProcessId!.Value)
                    .Distinct()
                    .ToList();

                // The module owns each tracked root with all its descendants plus every other live session process.
                var processIds = new HashSet<int>();

                foreach (var rootProcessId in trackedRootPids)
                {
                    CollectProcessBranch(rootProcessId, childrenByParent, liveProcesses, processIds);
                }

                foreach (var processId in sessionsByPid.Keys.Where(liveProcesses.ContainsKey))
                {
                    processIds.Add(processId);
                }

                // Drop observed processes and anything an earlier module already claimed, then claim the rest.
                processIds.ExceptWith(observedProcessIds);

                processIds.RemoveWhere(globallyAssigned.Contains);
                globallyAssigned.UnionWith(processIds);

                var rootProcessIds = trackedRootPids
                    .Where(processIds.Contains)
                    .Distinct()
                    .ToList();

                if (rootProcessIds.Count == 0 && processIds.Count > 0)
                {
                    // Without a tracked root the tree tops are the owned processes whose parent is ASLM itself or
                    // is not owned by the module (or is unknown to the process table).
                    rootProcessIds = processIds
                        .Where(processId =>
                        {
                            if (!entriesByPid.TryGetValue(processId, out var entry))
                            {
                                return true;
                            }

                            return entry.ParentProcessId == currentProcessId || !processIds.Contains(entry.ParentProcessId);
                        })
                        .OrderBy(processId => processId)
                        .ToList();
                }

                // Store the module result with the usage summed over its live processes.
                diagnostics[module.SourcePath] = new HomeModuleDiagnostics
                {
                    Module = module,
                    ConsoleSnapshot = consoleSnapshot,
                    ProcessIds = processIds,
                    RootProcessIds = rootProcessIds,
                    SessionsByPid = sessionsByPid,
                    Usage = AggregateUsage(
                        processIds
                            .Where(liveProcesses.ContainsKey)
                            .Select(processId => liveProcesses[processId].Usage),
                        totalPhysicalBytes)
                };
            }

            return diagnostics;
        }

        /// <summary>
        /// Collects one process branch and all of its descendants into the supplied set,
        /// skipping processes that are not live and nodes that were already visited.
        /// </summary>
        private static void CollectProcessBranch(
            int rootProcessId,
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            IReadOnlyDictionary<int, HomeLiveProcessInfo> liveProcesses,
            ISet<int> results)
        {
            if (!liveProcesses.ContainsKey(rootProcessId) || !results.Add(rootProcessId))
            {
                return;
            }

            if (!childrenByParent.TryGetValue(rootProcessId, out var children))
            {
                return;
            }

            foreach (var childProcessId in children)
            {
                CollectProcessBranch(childProcessId, childrenByParent, liveProcesses, results);
            }
        }


        // Tree construction

        /// <summary>
        /// Builds the full ASLM root tree used by the dashboard process panel.
        /// </summary>
        private static HomeProcessTreeNodeSnapshot BuildRootNode(
            int currentProcessId,
            IReadOnlyDictionary<int, HomeLiveProcessInfo> liveProcesses,
            IReadOnlyCollection<HomeModuleDiagnostics> moduleDiagnostics,
            ISet<int> internalProcessIds,
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            IReadOnlyDictionary<int, ProcessSnapshotEntry> entriesByPid,
            long totalPhysicalBytes)
        {
            // Show enabled or currently running modules only; modules with live processes come first, then by name.
            var moduleNodes = moduleDiagnostics
                .Where(module => module.Module.Status.Enabled || module.ProcessIds.Count > 0)
                .OrderByDescending(module => module.ProcessIds.Count > 0)
                .ThenBy(module => module.Module.Name, StringComparer.OrdinalIgnoreCase)
                .Select(module => BuildModuleNode(module, liveProcesses, childrenByParent, entriesByPid))
                .ToList();

            if (internalProcessIds.Count > 0)
            {
                // Group the managed child processes that belong to no module under a collapsed "ASLM Internal" node.
                var internalUsage = AggregateUsage(
                    internalProcessIds
                        .Where(liveProcesses.ContainsKey)
                        .Select(processId => liveProcesses[processId].Usage),
                    totalPhysicalBytes);

                var internalRoots = internalProcessIds
                    .Where(processId =>
                    {
                        if (!entriesByPid.TryGetValue(processId, out var entry))
                        {
                            return true;
                        }

                        return entry.ParentProcessId == currentProcessId || !internalProcessIds.Contains(entry.ParentProcessId);
                    })
                    .OrderBy(processId => processId)
                    .Select(processId => BuildProcessNode(
                        processId,
                        liveProcesses,
                        childrenByParent,
                        internalProcessIds,
                        sessionsByPid: null))
                    .ToList();

                moduleNodes.Add(new HomeProcessTreeNodeSnapshot
                {
                    Key = "module:aslm_internal",
                    Title = "ASLM Internal",
                    TitleColor = HomePaletteColors.LabelPrimary,
                    TitleFontAttributes = FontAttributes.Bold,
                    CpuText = HomeDashboardPresenter.FormatPercent(internalUsage.CpuPercent),
                    GpuText = HomeDashboardPresenter.FormatPercentOrNa(internalUsage.GpuPercent),
                    MemoryText = HomeDashboardPresenter.FormatBytes(internalUsage.MemoryBytes),
                    DiskText = HomeDashboardPresenter.FormatRate(internalUsage.DiskIoBytesPerSec),
                    NetworkText = HomeDashboardPresenter.FormatCompactCount(internalUsage.ConnectionCount),
                    DetailsText = FormatProcessCount(internalProcessIds.Count),
                    Children = internalRoots,
                    DefaultExpanded = false
                });
            }

            // The root row totals every managed process and shows the ASLM PID in its details column.
            liveProcesses.TryGetValue(currentProcessId, out var currentProcess);
            var rootUsage = AggregateUsage(liveProcesses.Values.Select(process => process.Usage), totalPhysicalBytes);

            return new HomeProcessTreeNodeSnapshot
            {
                Key = "root:aslm",
                Title = "ASLM",
                TitleColor = HomePaletteColors.LabelPrimary,
                TitleFontAttributes = FontAttributes.Bold,
                CpuText = HomeDashboardPresenter.FormatPercent(rootUsage.CpuPercent),
                GpuText = HomeDashboardPresenter.FormatPercentOrNa(rootUsage.GpuPercent),
                MemoryText = HomeDashboardPresenter.FormatBytes(rootUsage.MemoryBytes),
                DiskText = HomeDashboardPresenter.FormatRate(rootUsage.DiskIoBytesPerSec),
                NetworkText = HomeDashboardPresenter.FormatCompactCount(rootUsage.ConnectionCount),
                DetailsText = currentProcess == null
                    ? "Runtime"
                    : $"PID {currentProcess.ProcessId}",
                Children = moduleNodes,
                DefaultExpanded = true
            };
        }

        /// <summary>
        /// Builds one tree node for a module and all of its live processes.
        /// </summary>
        private static HomeProcessTreeNodeSnapshot BuildModuleNode(
            HomeModuleDiagnostics moduleDiagnostics,
            IReadOnlyDictionary<int, HomeLiveProcessInfo> liveProcesses,
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            IReadOnlyDictionary<int, ProcessSnapshotEntry> entriesByPid)
        {
            // Tree tops are the recorded roots that still belong to the module.
            var rootProcessIds = moduleDiagnostics.RootProcessIds
                .Where(processId => moduleDiagnostics.ProcessIds.Contains(processId))
                .Distinct()
                .ToList();

            if (rootProcessIds.Count == 0 && moduleDiagnostics.ProcessIds.Count > 0)
            {
                // No recorded root survived: use the module processes whose parent is not part of the module.
                rootProcessIds = moduleDiagnostics.ProcessIds
                    .Where(processId =>
                    {
                        if (!entriesByPid.TryGetValue(processId, out var entry))
                        {
                            return true;
                        }

                        return !moduleDiagnostics.ProcessIds.Contains(entry.ParentProcessId);
                    })
                    .OrderBy(processId => processId)
                    .ToList();
            }

            // Build the process rows below the module row, starting from its tree tops.
            var childNodes = rootProcessIds
                .Select(processId => BuildProcessNode(
                    processId,
                    liveProcesses,
                    childrenByParent,
                    moduleDiagnostics.ProcessIds,
                    moduleDiagnostics.SessionsByPid))
                .ToList();

            // The details column shows the process count, or the generic label "Module" while no process is live.
            return new HomeProcessTreeNodeSnapshot
            {
                Key = $"module:{moduleDiagnostics.Module.SourcePath}",
                Title = moduleDiagnostics.Module.Name,
                TitleColor = HomePaletteColors.LabelPrimary,
                TitleFontAttributes = FontAttributes.Bold,
                CpuText = HomeDashboardPresenter.FormatPercent(moduleDiagnostics.Usage.CpuPercent),
                GpuText = HomeDashboardPresenter.FormatPercentOrNa(moduleDiagnostics.Usage.GpuPercent),
                MemoryText = HomeDashboardPresenter.FormatBytes(moduleDiagnostics.Usage.MemoryBytes),
                DiskText = HomeDashboardPresenter.FormatRate(moduleDiagnostics.Usage.DiskIoBytesPerSec),
                NetworkText = HomeDashboardPresenter.FormatCompactCount(moduleDiagnostics.Usage.ConnectionCount),
                DetailsText = moduleDiagnostics.ProcessIds.Count == 0
                    ? "Module"
                    : FormatProcessCount(moduleDiagnostics.ProcessIds.Count),
                Children = childNodes,
                DefaultExpanded = false
            };
        }

        /// <summary>
        /// Builds one tree node for a live process and its descendants.
        /// The node title and status text come from the console session of the process when one exists.
        /// </summary>
        private static HomeProcessTreeNodeSnapshot BuildProcessNode(
            int processId,
            IReadOnlyDictionary<int, HomeLiveProcessInfo> liveProcesses,
            IReadOnlyDictionary<int, List<int>> childrenByParent,
            ISet<int> allowedProcessIds,
            IReadOnlyDictionary<int, ModuleConsoleSessionSnapshot>? sessionsByPid)
        {
            var process = liveProcesses[processId];
            ModuleConsoleSessionSnapshot? session = null;
            sessionsByPid?.TryGetValue(processId, out session);

            // Recurse into the children that belong to the same group, in pid order.
            var childNodes = new List<HomeProcessTreeNodeSnapshot>();
            if (childrenByParent.TryGetValue(processId, out var childProcessIds))
            {
                foreach (var childProcessId in childProcessIds
                             .Where(allowedProcessIds.Contains)
                             .OrderBy(childProcessId => childProcessId))
                {
                    childNodes.Add(BuildProcessNode(
                        childProcessId,
                        liveProcesses,
                        childrenByParent,
                        allowedProcessIds,
                        sessionsByPid));
                }
            }

            // Prefer the session title over the executable name; the details text carries the pid and the session
            // outcome: exit code, "service" for observed processes, or the current stage.
            var title = !string.IsNullOrWhiteSpace(session?.Title)
                ? session.Title
                : process.ProcessName;

            var isObservedService = session?.IsObservedProcess == true;
            var detailsText = session != null && session.ProcessId.HasValue
                ? $"PID {session.ProcessId.Value}"
                : $"PID {process.ProcessId}";

            if (session?.ExitCode.HasValue == true)
            {
                detailsText += $" - exit {session.ExitCode.Value}";
            }
            else if (isObservedService)
            {
                detailsText += " - service";
            }
            else if (session != null && !string.IsNullOrWhiteSpace(session.Stage))
            {
                detailsText += $" - {session.Stage.ToLowerInvariant()}";
            }

            // Observed services are highlighted in yellow; regular processes use the primary label color.
            return new HomeProcessTreeNodeSnapshot
            {
                Key = $"process:{process.ProcessId}",
                Title = title,
                TitleColor = isObservedService
                    ? Color.FromArgb("#FFFFD60A")
                    : HomePaletteColors.LabelPrimary,
                TitleFontAttributes = FontAttributes.None,
                CpuText = HomeDashboardPresenter.FormatPercent(process.Usage.CpuPercent),
                GpuText = HomeDashboardPresenter.FormatPercentOrNa(process.Usage.GpuPercent),
                MemoryText = HomeDashboardPresenter.FormatBytes(process.Usage.MemoryBytes),
                DiskText = HomeDashboardPresenter.FormatRate(process.Usage.DiskIoBytesPerSec),
                NetworkText = HomeDashboardPresenter.FormatCompactCount(process.Usage.ConnectionCount),
                DetailsText = detailsText,
                Children = childNodes,
                DefaultExpanded = false
            };
        }

        /// <summary>
        /// Formats a process count as "1 process" or "N processes" for the details column.
        /// </summary>
        private static string FormatProcessCount(int processCount)
        {
            return processCount == 1
                ? "1 process"
                : $"{processCount} processes";
        }


        // Process sampling

        /// <summary>
        /// Samples one live process and returns the resulting diagnostics payload.
        /// CPU and disk I/O rates are deltas against the previous sample of the same process, so a process seen
        /// for the first time reports zero for both. Returns false when the process exited or cannot be read.
        /// </summary>
        private bool TrySampleProcess(
            int processId,
            DateTimeOffset capturedUtc,
            IReadOnlyDictionary<int, ProcessSnapshotEntry> entriesByPid,
            IReadOnlyDictionary<int, double> gpuByPid,
            IReadOnlyDictionary<int, int> connectionCounts,
            long totalPhysicalBytes,
            out HomeLiveProcessInfo liveProcess)
        {
            liveProcess = null!;

            try
            {
                using var process = Process.GetProcessById(processId);
                process.Refresh();

                if (process.HasExited)
                {
                    return false;
                }

                // Read the current absolute values: parent pid and name from the process table, working set,
                // cumulative processor time, and cumulative I/O transfer counters.
                var hasEntry = entriesByPid.TryGetValue(processId, out var entry);
                var parentProcessId = hasEntry
                    ? entry!.ParentProcessId
                    : 0;

                var processName = TryResolveProcessName(process, hasEntry ? entry!.ExecutableName : null);
                var workingSetBytes = process.WorkingSet64;
                var totalProcessorTime = process.TotalProcessorTime;
                var ioCounters = GetProcessIoCountersSafe(process);

                var cpuPercent = 0d;
                var diskIoBytesPerSecond = 0d;

                // Rates need a previous sample; without one (first sighting) both stay zero.
                if (_processSamples.TryGetValue(processId, out var previousSample))
                {
                    var elapsedSeconds = (capturedUtc - previousSample.TimestampUtc).TotalSeconds;
                    if (elapsedSeconds > 0)
                    {
                        // CPU percent is the consumed processor time over the interval divided by the capacity of all
                        // logical cores, i.e. the share of the whole machine, clamped to 0-100.
                        cpuPercent = Math.Clamp(
                            ((totalProcessorTime - previousSample.TotalProcessorTime).TotalSeconds /
                             (elapsedSeconds * Environment.ProcessorCount)) * 100d,
                            0,
                            100);

                        // Disk throughput is read plus write bytes since the previous sample per second;
                        // a counter that went backwards counts as no transfer.
                        var readDelta = ioCounters.ReadTransferCount >= previousSample.ReadTransferCount
                            ? ioCounters.ReadTransferCount - previousSample.ReadTransferCount
                            : 0;

                        var writeDelta = ioCounters.WriteTransferCount >= previousSample.WriteTransferCount
                            ? ioCounters.WriteTransferCount - previousSample.WriteTransferCount
                            : 0;

                        diskIoBytesPerSecond = (readDelta + writeDelta) / elapsedSeconds;
                    }
                }

                // Remember the raw counters as the base for the next refresh.
                _processSamples[processId] = new HomeProcessSample
                {
                    TimestampUtc = capturedUtc,
                    TotalProcessorTime = totalProcessorTime,
                    ReadTransferCount = ioCounters.ReadTransferCount,
                    WriteTransferCount = ioCounters.WriteTransferCount
                };

                // GPU is unknown (null) when the GPU query reported nothing for this pid.
                gpuByPid.TryGetValue(processId, out var gpuPercent);
                connectionCounts.TryGetValue(processId, out var connectionCount);

                liveProcess = new HomeLiveProcessInfo
                {
                    ProcessId = processId,
                    ParentProcessId = parentProcessId,
                    ProcessName = processName,
                    Usage = new HomeUsageSnapshot
                    {
                        CpuPercent = cpuPercent,
                        GpuPercent = gpuByPid.ContainsKey(processId) ? gpuPercent : null,
                        MemoryBytes = workingSetBytes,
                        MemoryTotalBytes = totalPhysicalBytes,
                        DiskIoBytesPerSec = diskIoBytesPerSecond,
                        ConnectionCount = connectionCount
                    }
                };

                return true;
            }
            catch
            {
                // The process exited between the table snapshot and the read, or access was denied:
                // such a process is simply left out of this capture.
                return false;
            }
        }

        /// <summary>
        /// Removes stale process samples that no longer exist in the managed runtime tree.
        /// </summary>
        private void CleanupProcessSamples(IEnumerable<int> liveProcessIds)
        {
            var liveProcessSet = liveProcessIds.ToHashSet();
            var staleKeys = _processSamples.Keys
                .Where(processId => !liveProcessSet.Contains(processId))
                .ToList();

            foreach (var staleKey in staleKeys)
            {
                _processSamples.Remove(staleKey);
            }
        }

        /// <summary>
        /// Aggregates process usage into a single summary snapshot: CPU, GPU, memory, disk I/O, and connections are
        /// summed, the percentages are clamped to 0-100, and GPU stays unknown unless at least one process reports it.
        /// </summary>
        private static HomeUsageSnapshot AggregateUsage(IEnumerable<HomeUsageSnapshot> usages, long totalPhysicalBytes)
        {
            double cpuPercent = 0;
            double gpuPercent = 0;
            var hasGpu = false;
            long memoryBytes = 0;
            double diskIoBytesPerSecond = 0;
            var connectionCount = 0;

            // Sum every metric; GPU is added only for processes that reported it.
            foreach (var usage in usages)
            {
                cpuPercent += usage.CpuPercent;

                if (usage.GpuPercent.HasValue)
                {
                    gpuPercent += usage.GpuPercent.Value;
                    hasGpu = true;
                }

                memoryBytes += usage.MemoryBytes;
                diskIoBytesPerSecond += usage.DiskIoBytesPerSec;
                connectionCount += usage.ConnectionCount;
            }

            // Per-process readings are not taken at exactly the same instant, so the sums can slightly exceed 100%;
            // cap the percentages.
            return new HomeUsageSnapshot
            {
                CpuPercent = Math.Clamp(cpuPercent, 0, 100),
                GpuPercent = hasGpu ? Math.Clamp(gpuPercent, 0, 100) : null,
                MemoryBytes = memoryBytes,
                MemoryTotalBytes = totalPhysicalBytes,
                DiskIoBytesPerSec = diskIoBytesPerSecond,
                ConnectionCount = connectionCount
            };
        }


        // System metrics

        /// <summary>
        /// Samples total system CPU usage from cumulative idle, kernel, and user time counters.
        /// The kernel counter includes idle time, so utilization over the interval since the previous sample is
        /// <c>1 - idleDelta / (kernelDelta + userDelta)</c>; the first sample has no baseline and reports zero.
        /// </summary>
        /// <returns>Utilization in percent from 0 to 100.</returns>
        private double SampleSystemCpu(DateTimeOffset capturedUtc)
        {
            // Read the cumulative counters from the platform source: Mach on macOS, /proc on Linux,
            // GetSystemTimes (FILETIME values converted to ticks) on Windows.
#if MACCATALYST || LINUX
#if LINUX
            if (!LinuxSystemMetrics.TryGetSystemCpuTimes(out var idleTicks, out var kernelTicks, out var userTicks))
#else
            if (!MacSystemMetrics.TryGetSystemCpuTimes(out var idleTicks, out var kernelTicks, out var userTicks))
#endif
            {
                return 0;
            }

            var currentSample = new HomeSystemCpuSample
            {
                TimestampUtc = capturedUtc,
                IdleTime = idleTicks,
                KernelTime = kernelTicks,
                UserTime = userTicks
            };
#else
            if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            {
                return 0;
            }

            var currentSample = new HomeSystemCpuSample
            {
                TimestampUtc = capturedUtc,
                IdleTime = ToUInt64(idleTime),
                KernelTime = ToUInt64(kernelTime),
                UserTime = ToUInt64(userTime)
            };
#endif

            // The first sample only establishes the baseline for the next refresh.
            if (_lastSystemCpuSample == null)
            {
                _lastSystemCpuSample = currentSample;
                return 0;
            }

            // Counter growth since the previous sample, then store the current sample as the new baseline.
            var idleDelta = currentSample.IdleTime - _lastSystemCpuSample.IdleTime;
            var kernelDelta = currentSample.KernelTime - _lastSystemCpuSample.KernelTime;
            var userDelta = currentSample.UserTime - _lastSystemCpuSample.UserTime;
            var totalDelta = kernelDelta + userDelta;

            _lastSystemCpuSample = currentSample;

            // No time elapsed on the counters: avoid dividing by zero.
            if (totalDelta == 0)
            {
                return 0;
            }

            return Math.Clamp((1d - idleDelta / (double)totalDelta) * 100d, 0, 100);
        }

        /// <summary>
        /// Samples total network throughput across active adapters.
        /// Throughput is the growth of the cumulative received/sent byte counters since the previous sample divided
        /// by the elapsed seconds, so the first sample reports zero.
        /// </summary>
        private HomeNetworkStats SampleNetwork(DateTimeOffset capturedUtc)
        {
            // Add up the byte counters of all adapters that are up, ignoring loopback and tunnel adapters
            // so that local traffic and encapsulated traffic are not counted twice.
            long totalReceived = 0;
            long totalSent = 0;

            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                try
                {
                    var interfaceStats = networkInterface.GetIPStatistics();
                    totalReceived += interfaceStats.BytesReceived;
                    totalSent += interfaceStats.BytesSent;
                }
                catch
                {
                    // Ignore adapters that do not expose IP statistics.
                }
            }

            var currentSample = new HomeNetworkSample
            {
                TimestampUtc = capturedUtc,
                TotalReceivedBytes = totalReceived,
                TotalSentBytes = totalSent
            };

            // Without a baseline, or without elapsed time, no rate can be computed yet.
            if (_lastNetworkSample == null)
            {
                _lastNetworkSample = currentSample;
                return new HomeNetworkStats();
            }

            var elapsedSeconds = (capturedUtc - _lastNetworkSample.TimestampUtc).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                _lastNetworkSample = currentSample;
                return new HomeNetworkStats();
            }

            // The sum shrinks when an adapter leaves it (for example when it goes down); a negative delta counts as zero.
            var receivedDelta = Math.Max(0, currentSample.TotalReceivedBytes - _lastNetworkSample.TotalReceivedBytes);
            var sentDelta = Math.Max(0, currentSample.TotalSentBytes - _lastNetworkSample.TotalSentBytes);

            _lastNetworkSample = currentSample;

            return new HomeNetworkStats
            {
                ReceiveBytesPerSecond = receivedDelta / elapsedSeconds,
                SendBytesPerSecond = sentDelta / elapsedSeconds
            };
        }

        /// <summary>
        /// Queries total disk activity: IOKit block storage statistics on macOS, whole-device counters on Linux,
        /// and on Windows the pre-formatted <c>_Total</c> physical-disk performance counters of the WMI provider.
        /// Returns empty statistics when the query fails.
        /// </summary>
        private static HomeDiskStats QueryDiskStats()
        {
            // Each platform has its own source; only the Windows branch below runs a WMI query.
#if MACCATALYST
            return MacSystemMetrics.SampleDiskStats();
#elif LINUX
            return LinuxSystemMetrics.SampleDiskStats();
#else
            try
            {
                // The _Total instance aggregates all physical disks; the values are already per-second rates.
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, PercentIdleTime, PercentDiskTime, DiskReadBytesPersec, DiskWriteBytesPersec " +
                    "FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name = '_Total'");

                foreach (ManagementObject result in searcher.Get())
                {
                    // Busy time is the complement of the idle percentage; the disk-time counter is the fallback
                    // when the idle counter reports zero.
                    var idlePercent = ConvertToDouble(result["PercentIdleTime"]);
                    var busyPercent = idlePercent > 0
                        ? Math.Clamp(100d - idlePercent, 0, 100)
                        : Math.Clamp(ConvertToDouble(result["PercentDiskTime"]), 0, 100);

                    return new HomeDiskStats
                    {
                        BusyPercent = busyPercent,
                        ReadBytesPerSecond = ConvertToDouble(result["DiskReadBytesPersec"]),
                        WriteBytesPerSecond = ConvertToDouble(result["DiskWriteBytesPersec"])
                    };
                }
            }
            catch
            {
                // Best-effort sampling only: when WMI is unavailable the dashboard shows zero disk activity.
            }

            return new HomeDiskStats();
#endif
        }

        /// <summary>
        /// Queries GPU engine utilization and groups it by process and adapter.
        /// On Windows every row of the GPU Engine performance counters is one engine of one process; its instance
        /// name encodes the process id, the physical adapter, and the engine, and the utilization is a percentage.
        /// Returns an empty result when the counters are unavailable.
        /// </summary>
        private static HomeGpuQueryResult QueryGpuUsage()
        {
            // Each platform has its own source; only the Windows branch below runs a WMI query.
#if MACCATALYST
            return MacSystemMetrics.QueryGpuUsage();
#elif LINUX
            // GPU providers are driver-specific; an empty sample keeps GPU usage explicitly unavailable.
            return new HomeGpuQueryResult();
#else
            var samples = new List<HomeGpuEngineSample>();
            var adapterNames = QueryGpuAdapterNames();

            try
            {
                // Collect one sample per engine instance; names that do not match the expected pattern are skipped.
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

                foreach (ManagementObject result in searcher.Get())
                {
                    var instanceName = result["Name"]?.ToString();
                    if (!TryParseGpuInstance(instanceName, out var processId, out var adapterIndex, out var engineId))
                    {
                        continue;
                    }

                    var utilization = ConvertToDouble(result["UtilizationPercentage"]);
                    samples.Add(new HomeGpuEngineSample
                    {
                        ProcessId = processId,
                        AdapterIndex = adapterIndex,
                        EngineId = engineId,
                        Utilization = Math.Max(0, utilization)
                    });
                }
            }
            catch
            {
                // The GPU counters can be unavailable on a machine: report no samples but keep the adapter names.
                return new HomeGpuQueryResult
                {
                    ProcessByPid = new Dictionary<int, double>(),
                    AdapterNames = adapterNames,
                    Samples = []
                };
            }

            // A process's GPU load is the sum over all its engines, capped at 100%.
            var byProcessId = samples
                .GroupBy(sample => sample.ProcessId)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Clamp(group.Sum(sample => sample.Utilization), 0, 100));

            return new HomeGpuQueryResult
            {
                ProcessByPid = byProcessId,
                AdapterNames = adapterNames,
                Samples = samples
            };
#endif
        }

        /// <summary>
        /// Builds the per-adapter GPU snapshots for system and runtime views:
        /// the system value uses all processes, the runtime value only the managed ASLM processes.
        /// </summary>
        private static IReadOnlyList<HomeGpuAdapterSnapshot> BuildGpuAdapterSnapshots(
            HomeGpuQueryResult gpuQuery,
            IReadOnlyCollection<int> managedProcessIds)
        {
            // Aggregate the same samples twice: once for the whole machine and once restricted to managed processes.
            var managedProcessSet = managedProcessIds.ToHashSet();
            var systemUsageByAdapter = AggregateGpuUsageByAdapter(gpuQuery.Samples);
            var runtimeUsageByAdapter = AggregateGpuUsageByAdapter(
                gpuQuery.Samples.Where(sample => managedProcessSet.Contains(sample.ProcessId)));

            // List every adapter that has samples or a known name, so idle adapters still appear with 0%.
            var adapterIndices = systemUsageByAdapter.Keys
                .Union(runtimeUsageByAdapter.Keys)
                .Union(Enumerable.Range(0, gpuQuery.AdapterNames.Count))
                .OrderBy(index => index)
                .ToList();

            // Names come from the adapter list by index; a sampled adapter without a known name is labeled "GPU n".
            return adapterIndices
                .Select(adapterIndex => new HomeGpuAdapterSnapshot
                {
                    AdapterIndex = adapterIndex,
                    Name = adapterIndex < gpuQuery.AdapterNames.Count
                        ? gpuQuery.AdapterNames[adapterIndex]
                        : $"GPU {adapterIndex}",
                    SystemPercent = systemUsageByAdapter.TryGetValue(adapterIndex, out var systemPercent)
                        ? systemPercent
                        : 0,
                    RuntimePercent = runtimeUsageByAdapter.TryGetValue(adapterIndex, out var runtimePercent)
                        ? runtimePercent
                        : 0
                })
                .ToList();
        }

        /// <summary>
        /// Aggregates GPU engine samples into one utilization value per adapter: the loads of all processes are
        /// summed per engine, and the adapter reports its busiest engine, capped at 100%.
        /// </summary>
        private static Dictionary<int, double> AggregateGpuUsageByAdapter(IEnumerable<HomeGpuEngineSample> samples)
        {
            return samples
                .GroupBy(sample => sample.AdapterIndex)
                .ToDictionary(
                    group => group.Key,
                    group => Math.Clamp(
                        group.GroupBy(sample => sample.EngineId)
                            .Select(engineGroup => engineGroup.Sum(sample => sample.Utilization))
                            .DefaultIfEmpty(0)
                            .Max(),
                        0,
                        100));
        }

        /// <summary>
        /// Queries installed GPU adapter names from the Windows video controller list; the result is cached for the
        /// process lifetime, and other platforms return no names.
        /// </summary>
        private static IReadOnlyList<string> QueryGpuAdapterNames()
        {
#if !WINDOWS
            return [];
#else
            if (_cachedGpuAdapterNames != null)
            {
                return _cachedGpuAdapterNames;
            }

            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name FROM Win32_VideoController");

                _cachedGpuAdapterNames = searcher.Get()
                    .OfType<ManagementObject>()
                    .Select(result => result["Name"]?.ToString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return _cachedGpuAdapterNames;
            }
            catch
            {
                // Cache the failure as an empty list so the failing query is not repeated on every refresh.
                _cachedGpuAdapterNames = [];
                return _cachedGpuAdapterNames;
            }
#endif
        }

        /// <summary>
        /// Queries the number of open TCP and UDP endpoints per process for the supplied process ids.
        /// Windows reads the extended TCP and UDP tables for IPv4 and IPv6; macOS and Linux count process sockets.
        /// </summary>
        private static Dictionary<int, int> QueryProcessConnectionCounts(IReadOnlyCollection<int> processIds)
        {
            var counts = new Dictionary<int, int>();
            if (processIds.Count == 0)
            {
                return counts;
            }

            var trackedProcessIds = processIds.ToHashSet();

#if MACCATALYST
            foreach (var pair in MacSystemMetrics.CountProcessSockets(trackedProcessIds))
            {
                counts[pair.Key] = pair.Value;
            }
#elif LINUX
            counts = LinuxSystemMetrics.CountProcessSockets(trackedProcessIds);
#else
            IncrementCountsFromTcpTable(counts, trackedProcessIds, AddressFamily.InterNetwork);
            IncrementCountsFromTcpTable(counts, trackedProcessIds, AddressFamily.InterNetworkV6);
            IncrementCountsFromUdpTable(counts, trackedProcessIds, AddressFamily.InterNetwork);
            IncrementCountsFromUdpTable(counts, trackedProcessIds, AddressFamily.InterNetworkV6);
#endif

            return counts;
        }

        /// <summary>
        /// Adds the number of TCP connections of each tracked process, in any state, to the counts for one address
        /// family, using the Windows extended TCP table that records the owning process of every row.
        /// </summary>
        private static void IncrementCountsFromTcpTable(
            IDictionary<int, int> counts,
            ISet<int> trackedProcessIds,
            AddressFamily addressFamily)
        {
            // First call with a null buffer only reports the required size (ERROR_INSUFFICIENT_BUFFER).
            var bufferLength = 0;
            var result = GetExtendedTcpTable(
                IntPtr.Zero,
                ref bufferLength,
                order: true,
                (int)addressFamily,
                TcpTableClass.OwnerPidAll,
                0);

            if (result != ErrorInsufficientBuffer || bufferLength <= 0)
            {
                return;
            }

            // Second call fills the native buffer, which is always released in the finally block.
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                result = GetExtendedTcpTable(
                    buffer,
                    ref bufferLength,
                    order: true,
                    (int)addressFamily,
                    TcpTableClass.OwnerPidAll,
                    0);

                if (result != ErrorSuccess)
                {
                    return;
                }

                // The table starts with a 32-bit row count followed by fixed-size rows; the row layout differs
                // between IPv4 and IPv6, so each family walks its own row type.
                var rowCount = Marshal.ReadInt32(buffer);
                var rowPointer = IntPtr.Add(buffer, sizeof(int));

                if (addressFamily == AddressFamily.InterNetwork)
                {
                    var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                    for (var index = 0; index < rowCount; index++)
                    {
                        var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPointer);
                        var processId = unchecked((int)row.OwningPid);
                        if (trackedProcessIds.Contains(processId))
                        {
                            counts[processId] = counts.TryGetValue(processId, out var value) ? value + 1 : 1;
                        }

                        rowPointer = IntPtr.Add(rowPointer, rowSize);
                    }
                }
                else
                {
                    var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                    for (var index = 0; index < rowCount; index++)
                    {
                        var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPointer);
                        var processId = unchecked((int)row.OwningPid);
                        if (trackedProcessIds.Contains(processId))
                        {
                            counts[processId] = counts.TryGetValue(processId, out var value) ? value + 1 : 1;
                        }

                        rowPointer = IntPtr.Add(rowPointer, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Adds the number of UDP endpoints of each tracked process to the counts for one address family,
        /// using the Windows extended UDP table that records the owning process of every row.
        /// </summary>
        private static void IncrementCountsFromUdpTable(
            IDictionary<int, int> counts,
            ISet<int> trackedProcessIds,
            AddressFamily addressFamily)
        {
            // First call with a null buffer only reports the required size (ERROR_INSUFFICIENT_BUFFER).
            var bufferLength = 0;
            var result = GetExtendedUdpTable(
                IntPtr.Zero,
                ref bufferLength,
                order: true,
                (int)addressFamily,
                UdpTableClass.OwnerPid,
                0);

            if (result != ErrorInsufficientBuffer || bufferLength <= 0)
            {
                return;
            }

            // Second call fills the native buffer, which is always released in the finally block.
            var buffer = Marshal.AllocHGlobal(bufferLength);
            try
            {
                result = GetExtendedUdpTable(
                    buffer,
                    ref bufferLength,
                    order: true,
                    (int)addressFamily,
                    UdpTableClass.OwnerPid,
                    0);

                if (result != ErrorSuccess)
                {
                    return;
                }

                // Same layout as the TCP table: a 32-bit row count followed by fixed-size rows per address family.
                var rowCount = Marshal.ReadInt32(buffer);
                var rowPointer = IntPtr.Add(buffer, sizeof(int));

                if (addressFamily == AddressFamily.InterNetwork)
                {
                    var rowSize = Marshal.SizeOf<MibUdpRowOwnerPid>();
                    for (var index = 0; index < rowCount; index++)
                    {
                        var row = Marshal.PtrToStructure<MibUdpRowOwnerPid>(rowPointer);
                        var processId = unchecked((int)row.OwningPid);
                        if (trackedProcessIds.Contains(processId))
                        {
                            counts[processId] = counts.TryGetValue(processId, out var value) ? value + 1 : 1;
                        }

                        rowPointer = IntPtr.Add(rowPointer, rowSize);
                    }
                }
                else
                {
                    var rowSize = Marshal.SizeOf<MibUdp6RowOwnerPid>();
                    for (var index = 0; index < rowCount; index++)
                    {
                        var row = Marshal.PtrToStructure<MibUdp6RowOwnerPid>(rowPointer);
                        var processId = unchecked((int)row.OwningPid);
                        if (trackedProcessIds.Contains(processId))
                        {
                            counts[processId] = counts.TryGetValue(processId, out var value) ? value + 1 : 1;
                        }

                        rowPointer = IntPtr.Add(rowPointer, rowSize);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }


        // Process-tree helpers

        /// <summary>
        /// Returns the complete set of descendant process identifiers rooted at one parent process,
        /// found breadth-first; the root itself is not part of the result.
        /// </summary>
        private static HashSet<int> CollectDescendantPids(
            int rootProcessId,
            IReadOnlyDictionary<int, List<int>> childrenByParent)
        {
            // Walk the parent-to-children map level by level; the visited check keeps each process once and
            // also guards against cycles in the snapshot.
            var descendants = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootProcessId);

            while (queue.Count > 0)
            {
                var parentProcessId = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parentProcessId, out var childProcessIds))
                {
                    continue;
                }

                foreach (var childProcessId in childProcessIds)
                {
                    if (!descendants.Add(childProcessId))
                    {
                        continue;
                    }

                    queue.Enqueue(childProcessId);
                }
            }

            return descendants;
        }


        // Native helpers

        /// <summary>
        /// Returns the current physical-memory totals for the host system: installed and used bytes
        /// (used is total minus available).
        /// </summary>
        private static HomeMemoryStatus GetMemoryStatus()
        {
#if MACCATALYST
            return MacSystemMetrics.GetMemoryStatus();
#elif LINUX
            return LinuxSystemMetrics.GetMemoryStatus();
#else
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status))
            {
                return new HomeMemoryStatus();
            }

            return new HomeMemoryStatus
            {
                TotalPhysicalBytes = unchecked((long)status.TotalPhys),
                UsedPhysicalBytes = unchecked((long)(status.TotalPhys - status.AvailPhys))
            };
#endif
        }

        /// <summary>
        /// Returns process I/O counters, or a zeroed structure when the native query fails.
        /// </summary>
        private static IoCounters GetProcessIoCountersSafe(Process process)
        {
#if MACCATALYST
            var (readBytes, writeBytes) = MacSystemMetrics.GetProcessDiskIo(process.Id);
            return new IoCounters
            {
                ReadTransferCount = readBytes,
                WriteTransferCount = writeBytes
            };
#elif LINUX
            var (readBytes, writeBytes) = LinuxSystemMetrics.GetProcessDiskIo(process.Id);
            return new IoCounters { ReadTransferCount = readBytes, WriteTransferCount = writeBytes };
#else
            return GetProcessIoCounters(process.Handle, out var ioCounters)
                ? ioCounters
                : default;
#endif
        }

        /// <summary>
        /// Resolves a stable display name for one process: the live process name, then the executable name from the
        /// process table snapshot, then a generic "Process pid" label.
        /// </summary>
        private static string TryResolveProcessName(Process process, string? executableName)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(process.ProcessName))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                // Fall back to the snapshot executable name.
            }

            var snapshotName = Path.GetFileNameWithoutExtension(executableName);
            return string.IsNullOrWhiteSpace(snapshotName)
                ? $"Process {process.Id}"
                : snapshotName;
        }

        /// <summary>
        /// Parses one GPU engine instance name and extracts the owning process identifier,
        /// the physical adapter index, and the engine identifier.
        /// </summary>
        private static bool TryParseGpuInstance(
            string? instanceName,
            out int processId,
            out int adapterIndex,
            out string engineId)
        {
            processId = 0;
            adapterIndex = 0;
            engineId = string.Empty;

            if (string.IsNullOrWhiteSpace(instanceName))
            {
                return false;
            }

            var match = GpuInstanceRegex.Match(instanceName);
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId) ||
                !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out adapterIndex))
            {
                return false;
            }

            engineId = match.Groups[3].Value;
            return true;
        }

        /// <summary>
        /// Converts one WMI numeric value to <see cref="double"/>; WMI returns counters as different integer widths
        /// or strings, so unknown types are parsed from their invariant text and unparsable values become zero.
        /// </summary>
        private static double ConvertToDouble(object? value)
        {
            if (value == null)
            {
                return 0;
            }

            return value switch
            {
                byte byteValue => byteValue,
                short shortValue => shortValue,
                int intValue => intValue,
                long longValue => longValue,
                float floatValue => floatValue,
                double doubleValue => doubleValue,
                decimal decimalValue => (double)decimalValue,
                _ when double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var parsedValue) => parsedValue,
                _ => 0
            };
        }

        /// <summary>
        /// Converts a native <see cref="FileTime"/> value to an unsigned 64-bit tick count (100-nanosecond units)
        /// by joining its high and low 32-bit halves.
        /// </summary>
        private static ulong ToUInt64(FileTime fileTime)
        {
            return ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
        }


        // Native interop

        /// <summary>
        /// Win32 <c>ERROR_SUCCESS</c>: the call completed without error.
        /// </summary>
        private const uint ErrorSuccess = 0;

        /// <summary>
        /// Win32 <c>ERROR_INSUFFICIENT_BUFFER</c>: the supplied buffer is too small and the required size was returned.
        /// </summary>
        private const uint ErrorInsufficientBuffer = 122;

        /// <summary>
        /// Reads cumulative system idle, kernel, and user times from the Windows kernel.
        /// The kernel time includes the idle time, and all values are in 100-nanosecond units.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(
            out FileTime idleTime,
            out FileTime kernelTime,
            out FileTime userTime);

        /// <summary>
        /// Reads the current global memory status for the host system.
        /// The structure's <c>Length</c> field must be set before the call.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx memoryStatus);

        /// <summary>
        /// Reads cumulative I/O counters for one process handle.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters ioCounters);

        /// <summary>
        /// Queries the extended TCP owner table for one IP version.
        /// With a null table it returns the required buffer length instead of rows.
        /// </summary>
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr tcpTable,
            ref int outBufferLength,
            bool order,
            int ipVersion,
            TcpTableClass tableClass,
            uint reserved);

        /// <summary>
        /// Queries the extended UDP owner table for one IP version.
        /// With a null table it returns the required buffer length instead of rows.
        /// </summary>
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(
            IntPtr udpTable,
            ref int outBufferLength,
            bool order,
            int ipVersion,
            UdpTableClass tableClass,
            uint reserved);

        /// <summary>
        /// Native <c>TCP_TABLE_CLASS</c> values accepted by <see cref="GetExtendedTcpTable"/>.
        /// </summary>
        private enum TcpTableClass
        {
            /// <summary>
            /// <c>TCP_TABLE_OWNER_PID_ALL</c>: rows for connections in all states, each with its owning process id.
            /// </summary>
            OwnerPidAll = 5
        }

        /// <summary>
        /// Native <c>UDP_TABLE_CLASS</c> values accepted by <see cref="GetExtendedUdpTable"/>.
        /// </summary>
        private enum UdpTableClass
        {
            /// <summary>
            /// <c>UDP_TABLE_OWNER_PID</c>: rows for UDP endpoints, each with its owning process id.
            /// </summary>
            OwnerPid = 1
        }

        /// <summary>
        /// Native <c>FILETIME</c>: a 64-bit count of 100-nanosecond intervals split into two 32-bit halves.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            /// <summary>
            /// Low 32 bits of the value.
            /// </summary>
            public uint LowDateTime;

            /// <summary>
            /// High 32 bits of the value.
            /// </summary>
            public uint HighDateTime;
        }

        /// <summary>
        /// Native <c>MEMORYSTATUSEX</c>: physical and virtual memory totals filled by <c>GlobalMemoryStatusEx</c>.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            /// <summary>
            /// Size of the structure in bytes; the API requires it to be set before the call.
            /// </summary>
            public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

            /// <summary>
            /// Approximate percentage of physical memory in use.
            /// </summary>
            public uint MemoryLoad;

            /// <summary>
            /// Total physical memory in bytes.
            /// </summary>
            public ulong TotalPhys;

            /// <summary>
            /// Physical memory currently available without paging, in bytes.
            /// </summary>
            public ulong AvailPhys;

            /// <summary>
            /// Commit limit (physical memory plus page files) in bytes.
            /// </summary>
            public ulong TotalPageFile;

            /// <summary>
            /// Memory the system can still commit, in bytes.
            /// </summary>
            public ulong AvailPageFile;

            /// <summary>
            /// Size of the user-mode virtual address space of the calling process, in bytes.
            /// </summary>
            public ulong TotalVirtual;

            /// <summary>
            /// Unreserved and uncommitted user-mode virtual memory of the calling process, in bytes.
            /// </summary>
            public ulong AvailVirtual;

            /// <summary>
            /// Reserved by the API; always zero.
            /// </summary>
            public ulong AvailExtendedVirtual;
        }

        /// <summary>
        /// Native <c>IO_COUNTERS</c>: cumulative I/O operation counts and transferred bytes of one process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            /// <summary>
            /// Number of read operations.
            /// </summary>
            public ulong ReadOperationCount;

            /// <summary>
            /// Number of write operations.
            /// </summary>
            public ulong WriteOperationCount;

            /// <summary>
            /// Number of I/O operations that are neither reads nor writes.
            /// </summary>
            public ulong OtherOperationCount;

            /// <summary>
            /// Bytes transferred by read operations.
            /// </summary>
            public ulong ReadTransferCount;

            /// <summary>
            /// Bytes transferred by write operations.
            /// </summary>
            public ulong WriteTransferCount;

            /// <summary>
            /// Bytes transferred by operations that are neither reads nor writes.
            /// </summary>
            public ulong OtherTransferCount;
        }

        /// <summary>
        /// Native <c>MIB_TCPROW_OWNER_PID</c>: one IPv4 TCP connection with its owning process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            /// <summary>
            /// TCP connection state code.
            /// </summary>
            public uint State;

            /// <summary>
            /// Local IPv4 address in network byte order.
            /// </summary>
            public uint LocalAddress;

            /// <summary>
            /// Local port in network byte order.
            /// </summary>
            public uint LocalPort;

            /// <summary>
            /// Remote IPv4 address in network byte order.
            /// </summary>
            public uint RemoteAddress;

            /// <summary>
            /// Remote port in network byte order.
            /// </summary>
            public uint RemotePort;

            /// <summary>
            /// Id of the process that owns the connection.
            /// </summary>
            public uint OwningPid;
        }

        /// <summary>
        /// Native <c>MIB_TCP6ROW_OWNER_PID</c>: one IPv6 TCP connection with its owning process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcp6RowOwnerPid
        {
            /// <summary>
            /// Local IPv6 address as 16 bytes.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddress;

            /// <summary>
            /// Scope id of the local address.
            /// </summary>
            public uint LocalScopeId;

            /// <summary>
            /// Local port in network byte order.
            /// </summary>
            public uint LocalPort;

            /// <summary>
            /// Remote IPv6 address as 16 bytes.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] RemoteAddress;

            /// <summary>
            /// Scope id of the remote address.
            /// </summary>
            public uint RemoteScopeId;

            /// <summary>
            /// Remote port in network byte order.
            /// </summary>
            public uint RemotePort;

            /// <summary>
            /// TCP connection state code.
            /// </summary>
            public uint State;

            /// <summary>
            /// Id of the process that owns the connection.
            /// </summary>
            public uint OwningPid;
        }

        /// <summary>
        /// Native <c>MIB_UDPROW_OWNER_PID</c>: one IPv4 UDP endpoint with its owning process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MibUdpRowOwnerPid
        {
            /// <summary>
            /// Local IPv4 address in network byte order.
            /// </summary>
            public uint LocalAddress;

            /// <summary>
            /// Local port in network byte order.
            /// </summary>
            public uint LocalPort;

            /// <summary>
            /// Id of the process that owns the endpoint.
            /// </summary>
            public uint OwningPid;
        }

        /// <summary>
        /// Native <c>MIB_UDP6ROW_OWNER_PID</c>: one IPv6 UDP endpoint with its owning process.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MibUdp6RowOwnerPid
        {
            /// <summary>
            /// Local IPv6 address as 16 bytes.
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] LocalAddress;

            /// <summary>
            /// Scope id of the local address.
            /// </summary>
            public uint LocalScopeId;

            /// <summary>
            /// Local port in network byte order.
            /// </summary>
            public uint LocalPort;

            /// <summary>
            /// Id of the process that owns the endpoint.
            /// </summary>
            public uint OwningPid;
        }
    }


    // Diagnostics snapshots

    /// <summary>
    /// Represents one complete diagnostics capture used to render the home dashboard.
    /// </summary>
    internal sealed class HomeDiagnosticsSnapshot
    {
        /// <summary>
        /// Gets or sets the capture timestamp.
        /// </summary>
        public DateTimeOffset CapturedUtc { get; set; }

        /// <summary>
        /// Gets or sets the host-system resource usage snapshot.
        /// </summary>
        public HomeUsageSnapshot SystemUsage { get; set; } = new();

        /// <summary>
        /// Gets or sets the ASLM runtime resource usage snapshot.
        /// </summary>
        public HomeUsageSnapshot RuntimeUsage { get; set; } = new();

        /// <summary>
        /// Gets or sets the per-module diagnostics snapshots keyed by source path.
        /// </summary>
        public IReadOnlyDictionary<string, HomeModuleDiagnostics> ModulesBySourcePath { get; set; } =
            new Dictionary<string, HomeModuleDiagnostics>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets or sets the GPU adapter utilization snapshots.
        /// </summary>
        public IReadOnlyList<HomeGpuAdapterSnapshot> GpuAdapters { get; set; } = [];

        /// <summary>
        /// Gets or sets the root node of the managed process tree.
        /// </summary>
        public HomeProcessTreeNodeSnapshot RootNode { get; set; } = new();

        /// <summary>
        /// Gets or sets the number of live managed child processes.
        /// </summary>
        public int ActiveProcessCount { get; set; }

        /// <summary>
        /// Gets or sets the number of modules with live managed processes.
        /// </summary>
        public int ActiveModuleCount { get; set; }

        /// <summary>
        /// Gets or sets the latest module activity timestamp.
        /// </summary>
        public DateTimeOffset? LastModuleActivityUtc { get; set; }
    }

    /// <summary>
    /// Represents one aggregate usage snapshot.
    /// </summary>
    internal sealed class HomeUsageSnapshot
    {
        /// <summary>
        /// Gets or sets CPU utilization in percent.
        /// </summary>
        public double CpuPercent { get; set; }

        /// <summary>
        /// Gets or sets GPU utilization in percent when available.
        /// </summary>
        public double? GpuPercent { get; set; }

        /// <summary>
        /// Gets or sets resident memory in bytes.
        /// </summary>
        public long MemoryBytes { get; set; }

        /// <summary>
        /// Gets or sets total physical memory in bytes.
        /// </summary>
        public long MemoryTotalBytes { get; set; }

        /// <summary>
        /// Gets the normalized memory usage in percent.
        /// </summary>
        public double MemoryPercent => MemoryTotalBytes <= 0
            ? 0
            : Math.Clamp(MemoryBytes / (double)MemoryTotalBytes * 100d, 0, 100);

        /// <summary>
        /// Gets or sets system disk busy percentage.
        /// </summary>
        public double DiskBusyPercent { get; set; }

        /// <summary>
        /// Gets or sets system disk read throughput in bytes per second.
        /// </summary>
        public double DiskReadBytesPerSec { get; set; }

        /// <summary>
        /// Gets or sets system disk write throughput in bytes per second.
        /// </summary>
        public double DiskWriteBytesPerSec { get; set; }

        /// <summary>
        /// Gets or sets aggregate process I/O throughput in bytes per second.
        /// </summary>
        public double DiskIoBytesPerSec { get; set; }

        /// <summary>
        /// Gets or sets system receive throughput in bytes per second.
        /// </summary>
        public double NetworkReceiveBytesPerSec { get; set; }

        /// <summary>
        /// Gets or sets system send throughput in bytes per second.
        /// </summary>
        public double NetworkSendBytesPerSec { get; set; }

        /// <summary>
        /// Gets or sets the number of open network endpoints.
        /// </summary>
        public int ConnectionCount { get; set; }
    }

    /// <summary>
    /// Represents the diagnostics for one installed module.
    /// </summary>
    internal sealed class HomeModuleDiagnostics
    {
        /// <summary>
        /// Gets or sets the module configuration.
        /// </summary>
        public ModuleConfig Module { get; set; } = new();

        /// <summary>
        /// Gets or sets the latest console snapshot for the module.
        /// </summary>
        public ModuleConsoleModuleSnapshot? ConsoleSnapshot { get; set; }

        /// <summary>
        /// Gets or sets the live process identifiers assigned to the module.
        /// </summary>
        public ISet<int> ProcessIds { get; set; } = new HashSet<int>();

        /// <summary>
        /// Gets or sets the root process identifiers assigned to the module.
        /// </summary>
        public IReadOnlyList<int> RootProcessIds { get; set; } = [];

        /// <summary>
        /// Gets or sets the console sessions indexed by process identifier.
        /// </summary>
        public IReadOnlyDictionary<int, ModuleConsoleSessionSnapshot> SessionsByPid { get; set; } =
            new Dictionary<int, ModuleConsoleSessionSnapshot>();

        /// <summary>
        /// Gets or sets the aggregate module usage.
        /// </summary>
        public HomeUsageSnapshot Usage { get; set; } = new();

        /// <summary>
        /// Gets the latest module activity timestamp.
        /// </summary>
        public DateTimeOffset? LastActivityUtc => ConsoleSnapshot?.LastActivityUtc;

        /// <summary>
        /// Gets the total number of console sessions for the module.
        /// </summary>
        public int ConsoleSessionCount => ConsoleSnapshot?.Sessions.Count ?? 0;
    }

    /// <summary>
    /// Represents one live process inside the managed runtime tree.
    /// </summary>
    internal sealed class HomeLiveProcessInfo
    {
        /// <summary>
        /// Gets or sets the process identifier.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Gets or sets the parent process identifier.
        /// </summary>
        public int ParentProcessId { get; set; }

        /// <summary>
        /// Gets or sets the display name of the process.
        /// </summary>
        public string ProcessName { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the sampled process usage.
        /// </summary>
        public HomeUsageSnapshot Usage { get; set; } = new();
    }

    /// <summary>
    /// Represents one GPU adapter with both system-wide and ASLM-runtime utilization.
    /// </summary>
    internal sealed class HomeGpuAdapterSnapshot
    {
        /// <summary>
        /// Gets or sets the adapter index reported by the GPU counters.
        /// </summary>
        public int AdapterIndex { get; set; }

        /// <summary>
        /// Gets or sets the user-visible adapter name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the system-wide utilization percentage.
        /// </summary>
        public double SystemPercent { get; set; }

        /// <summary>
        /// Gets or sets the utilization percentage attributed to the ASLM runtime tree.
        /// </summary>
        public double RuntimePercent { get; set; }
    }


    // GPU query data

    /// <summary>
    /// Stores one raw GPU engine sample: the load of one engine of one adapter caused by one process.
    /// </summary>
    internal sealed class HomeGpuEngineSample
    {
        /// <summary>
        /// Gets or sets the owning process identifier.
        /// </summary>
        public int ProcessId { get; set; }

        /// <summary>
        /// Gets or sets the adapter index.
        /// </summary>
        public int AdapterIndex { get; set; }

        /// <summary>
        /// Gets or sets the engine identifier reported by Windows.
        /// </summary>
        public string EngineId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the sampled utilization percentage.
        /// </summary>
        public double Utilization { get; set; }
    }

    /// <summary>
    /// Stores the GPU query results used for per-process and per-adapter aggregation.
    /// </summary>
    internal sealed class HomeGpuQueryResult
    {
        /// <summary>
        /// Gets or sets process utilization keyed by process identifier.
        /// </summary>
        public IReadOnlyDictionary<int, double> ProcessByPid { get; set; } = new Dictionary<int, double>();

        /// <summary>
        /// Gets or sets the ordered adapter-name list.
        /// </summary>
        public IReadOnlyList<string> AdapterNames { get; set; } = [];

        /// <summary>
        /// Gets or sets the raw engine samples returned by the query.
        /// </summary>
        public IReadOnlyList<HomeGpuEngineSample> Samples { get; set; } = [];
    }


    // Process tree snapshot

    /// <summary>
    /// Represents one tree node before it is flattened for display.
    /// </summary>
    internal sealed class HomeProcessTreeNodeSnapshot
    {
        /// <summary>
        /// Gets or sets the stable node key.
        /// </summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the row title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the row title color.
        /// </summary>
        public Color TitleColor { get; set; } = HomePaletteColors.LabelPrimary;

        /// <summary>
        /// Gets or sets the title font weight.
        /// </summary>
        public FontAttributes TitleFontAttributes { get; set; } = FontAttributes.None;

        /// <summary>
        /// Gets or sets the CPU column text.
        /// </summary>
        public string CpuText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the GPU column text.
        /// </summary>
        public string GpuText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the memory column text.
        /// </summary>
        public string MemoryText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the disk column text.
        /// </summary>
        public string DiskText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the network column text.
        /// </summary>
        public string NetworkText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the trailing details text.
        /// </summary>
        public string DetailsText { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the child nodes.
        /// </summary>
        public IReadOnlyList<HomeProcessTreeNodeSnapshot> Children { get; set; } = [];

        /// <summary>
        /// Gets or sets whether the node should default to expanded when first shown.
        /// </summary>
        public bool DefaultExpanded { get; set; }
    }


    // Raw samples

    /// <summary>
    /// Stores one raw per-process sample used to calculate CPU and I/O deltas.
    /// </summary>
    internal sealed class HomeProcessSample
    {
        /// <summary>
        /// Gets or sets the sample timestamp.
        /// </summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// Gets or sets the accumulated processor time for the process.
        /// </summary>
        public TimeSpan TotalProcessorTime { get; set; }

        /// <summary>
        /// Gets or sets the total bytes read by the process.
        /// </summary>
        public ulong ReadTransferCount { get; set; }

        /// <summary>
        /// Gets or sets the total bytes written by the process.
        /// </summary>
        public ulong WriteTransferCount { get; set; }
    }

    /// <summary>
    /// Stores one raw system CPU sample used for delta-based utilization calculation.
    /// </summary>
    internal sealed class HomeSystemCpuSample
    {
        /// <summary>
        /// Gets or sets the sample timestamp.
        /// </summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// Gets or sets the cumulative system idle time.
        /// </summary>
        public ulong IdleTime { get; set; }

        /// <summary>
        /// Gets or sets the cumulative system kernel time.
        /// </summary>
        public ulong KernelTime { get; set; }

        /// <summary>
        /// Gets or sets the cumulative system user time.
        /// </summary>
        public ulong UserTime { get; set; }
    }

    /// <summary>
    /// Stores one raw network-interface sample used to calculate transfer rates.
    /// </summary>
    internal sealed class HomeNetworkSample
    {
        /// <summary>
        /// Gets or sets the sample timestamp.
        /// </summary>
        public DateTimeOffset TimestampUtc { get; set; }

        /// <summary>
        /// Gets or sets the total received bytes across active interfaces.
        /// </summary>
        public long TotalReceivedBytes { get; set; }

        /// <summary>
        /// Gets or sets the total sent bytes across active interfaces.
        /// </summary>
        public long TotalSentBytes { get; set; }
    }


    // Host statistics

    /// <summary>
    /// Stores one host-memory snapshot used to build memory metrics.
    /// </summary>
    internal sealed class HomeMemoryStatus
    {
        /// <summary>
        /// Gets or sets the total physical memory in bytes.
        /// </summary>
        public long TotalPhysicalBytes { get; set; }

        /// <summary>
        /// Gets or sets the used physical memory in bytes.
        /// </summary>
        public long UsedPhysicalBytes { get; set; }
    }

    /// <summary>
    /// Stores one normalized disk-usage snapshot.
    /// </summary>
    internal sealed class HomeDiskStats
    {
        /// <summary>
        /// Gets or sets the disk busy percentage.
        /// </summary>
        public double BusyPercent { get; set; }

        /// <summary>
        /// Gets or sets read throughput in bytes per second.
        /// </summary>
        public double ReadBytesPerSecond { get; set; }

        /// <summary>
        /// Gets or sets write throughput in bytes per second.
        /// </summary>
        public double WriteBytesPerSecond { get; set; }
    }

    /// <summary>
    /// Stores one normalized network-throughput snapshot.
    /// </summary>
    internal sealed class HomeNetworkStats
    {
        /// <summary>
        /// Gets or sets receive throughput in bytes per second.
        /// </summary>
        public double ReceiveBytesPerSecond { get; set; }

        /// <summary>
        /// Gets or sets send throughput in bytes per second.
        /// </summary>
        public double SendBytesPerSecond { get; set; }
    }
}
