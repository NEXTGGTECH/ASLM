// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;

namespace ASLM.Pages
{
    // ASLM API page

    /// <summary>
    /// Displays the local ASLM API mirror server address and the module hosts it mirrors, and opens the server
    /// in a browser. Host rows are kept in sync with the port map and with the installed modules while the page
    /// is visible.
    /// </summary>
    public partial class AslmApiView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Local mirror server whose address, running state and host port map the page shows.
        /// </summary>
        private readonly AslmMirrorServer _mirrorServer;

        /// <summary>
        /// Notification service handed to host rows for their copy confirmation toasts.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Installer used to discover installed modules for host names and enabled state.
        /// </summary>
        private readonly ModuleInstaller _moduleInstaller;

        /// <summary>
        /// Localization service used to re-apply texts when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Host rows by key, so refreshes update existing rows instead of recreating them.
        /// </summary>
        private readonly Dictionary<string, AslmApiHostViewModel> _hostRows = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Cached display state (name, enabled) of installed modules by module id.
        /// </summary>
        private Dictionary<string, AslmApiModuleDisplayState> _moduleDisplayStates = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Task of the module display state load in flight, or the last finished one.
        /// </summary>
        private Task? _moduleDisplayStatesLoadTask;

        /// <summary>
        /// Version of the module display state load; bumping it discards the result of older loads.
        /// </summary>
        private int _moduleDisplayStateLoadVersion;

        /// <summary>
        /// Version of the host refresh; bumping it discards the result of older in-flight port-map reads.
        /// </summary>
        private int _hostRefreshVersion;

        /// <summary>
        /// Whether <see cref="_moduleDisplayStates"/> holds a loaded snapshot.
        /// </summary>
        private bool _moduleDisplayStatesLoaded;

        /// <summary>
        /// UTC time the cached module display states were loaded, used to expire the cache.
        /// </summary>
        private DateTime _moduleDisplayStatesLoadedAt = DateTime.MinValue;

        /// <summary>
        /// Cancellation source of the periodic refresh loop, or null while the loop is stopped.
        /// </summary>
        private CancellationTokenSource? _refreshLoopCts;

        /// <summary>
        /// Whether the page is loaded into the visual tree; module changes refresh hosts only then.
        /// </summary>
        private bool _isVisible;

        /// <summary>
        /// Backing field of <see cref="ServerUrl"/>.
        /// </summary>
        private string _serverUrl = string.Empty;

        /// <summary>
        /// Backing field of <see cref="CanOpenServer"/>.
        /// </summary>
        private bool _canOpenServer;

        /// <summary>
        /// Maximum age of the cached module display states before a refresh reloads them.
        /// </summary>
        private static readonly TimeSpan ModuleStateRefreshInterval = TimeSpan.FromSeconds(15);


        // Bound collections

        /// <summary>
        /// Stores the host rows displayed by the page.
        /// </summary>
        public ObservableCollection<AslmApiHostViewModel> Hosts { get; } = new();


        // Initialization

        /// <summary>
        /// Creates the ASLM API page and hooks service state notifications.
        /// </summary>
        /// <param name="mirrorServer">Mirror server to display.</param>
        /// <param name="notifications">Notification service used for copy confirmation toasts.</param>
        /// <param name="moduleInstaller">Installer that supplies module names and enabled state.</param>
        /// <param name="localization">Localization service whose language changes re-apply the texts.</param>
        public AslmApiView(
            AslmMirrorServer mirrorServer,
            NotificationCenter notifications,
            ModuleInstaller moduleInstaller,
            AppLocalizationService localization)
        {
            _mirrorServer = mirrorServer;
            _notifications = notifications;
            _moduleInstaller = moduleInstaller;
            _localization = localization;

            InitializeComponent();
            BindingContext = this;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            LocalizableAttach.Hook(this, _localization, this);
            _mirrorServer.StateChanged += OnServerStateChanged;
            _moduleInstaller.ModulesChanged += OnModulesChanged;
        }


        // Localization

        /// <summary>
        /// Applies localized strings to page chrome and visible host rows.
        /// </summary>
        public void ApplyLocalization()
        {
            PageTitleLabel.Text = L.Get(LocalizationKeys.AslmApi_Title);
            OpenServerButton.Text = L.Get(LocalizationKeys.AslmApi_Open);
            HostsHeaderLabel.Text = L.Get(LocalizationKeys.AslmApi_Hosts);

            EmptyHostsLabel.Text = L.Get(LocalizationKeys.AslmApi_NoHosts);

            foreach (var host in Hosts)
            {
                host.RefreshLocalizationLabels();
            }

            OnPropertyChanged(nameof(Hosts));
        }


        // Property notifications

        /// <summary>
        /// Raised when a bindable property on this view changes.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raises property change notifications for binding updates.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // Bound state

        /// <summary>
        /// Gets the mirror server root URL.
        /// </summary>
        public string ServerUrl
        {
            get => _serverUrl;
            private set => SetProperty(ref _serverUrl, value);
        }

        /// <summary>
        /// Gets whether the server root can be opened in a browser.
        /// </summary>
        public bool CanOpenServer
        {
            get => _canOpenServer;
            private set => SetProperty(ref _canOpenServer, value);
        }


        // Lifecycle

        /// <summary>
        /// Starts the lightweight host refresh loop when the page becomes visible.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            _isVisible = true;

            // Follow theme changes; the unsubscribe-first pattern keeps repeated loads from stacking handlers.
            ThemeService.PaletteApplied -= OnPaletteAppliedForCopyButtons;
            ThemeService.PaletteApplied += OnPaletteAppliedForCopyButtons;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            // Tint existing rows for the current theme, load the hosts once and keep them fresh.
            RefreshHostCopyButtonChrome();
            _ = RefreshAsync();
            StartRefreshLoop();
        }

        /// <summary>
        /// Stops periodic host refresh and theme tracking when the page leaves the visual tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            _isVisible = false;
            ThemeService.PaletteApplied -= OnPaletteAppliedForCopyButtons;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }

            StopRefreshLoop();
        }


        // Theme chrome

        /// <summary>
        /// Re-tints the host copy buttons on the main thread after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForCopyButtons()
        {
            MainThread.BeginInvokeOnMainThread(RefreshHostCopyButtonChrome);
        }

        /// <summary>
        /// Re-tints the host copy buttons on the main thread when the application theme changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshHostCopyButtonChrome);
        }

        /// <summary>
        /// Updates copy-button fill and icon tint on every visible host row.
        /// </summary>
        private void RefreshHostCopyButtonChrome()
        {
            foreach (var host in Hosts)
            {
                host.RefreshCopyButtonChrome();
            }
        }


        // Actions

        /// <summary>
        /// Opens the ASLM API mirror root in the system browser.
        /// </summary>
        private async void OnOpenServerClicked(object? sender, EventArgs e)
        {
            if (!CanOpenServer)
            {
                return;
            }

            await Launcher.Default.OpenAsync(_mirrorServer.BaseUrl);
        }


        // Refresh

        /// <summary>
        /// Reloads server state and currently declared host entries.
        /// </summary>
        internal async Task RefreshAsync()
        {
            // Stamp this refresh, publish the server state and make sure module names are loading or fresh.
            var refreshVersion = ++_hostRefreshVersion;
            ApplyServerState();
            EnsureModuleDisplayStatesLoading();

            // Read the port map off the UI thread.
            var hosts = await Task.Run(_mirrorServer.GetHosts);

            // A newer module-state snapshot or refresh supersedes this in-flight port-map read.
            if (refreshVersion != _hostRefreshVersion) return;
            SynchronizeHostRows(hosts, _moduleDisplayStates);
        }

        /// <summary>
        /// Updates host rows in place so periodic refreshes do not recreate the list visual tree.
        /// Only hosts of installed modules are shown, ordered by module name, port and host key.
        /// </summary>
        /// <param name="hosts">Hosts read from the port map.</param>
        /// <param name="moduleStates">Display states of installed modules; hosts of unknown modules are dropped.</param>
        private void SynchronizeHostRows(
            IReadOnlyList<AslmMirrorHostInfo> hosts,
            IReadOnlyDictionary<string, AslmApiModuleDisplayState> moduleStates)
        {
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Keep only hosts of installed modules and order them for display.
            var orderedHosts = hosts
                .Where(host => moduleStates.ContainsKey(host.ModuleId))
                .OrderBy(
                    host => ResolveModuleDisplayState(host.ModuleId, moduleStates).Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(static host => host.Port)
                .ThenBy(static host => host.HostKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Update or move rows that already exist and insert new ones at their sorted position.
            for (var desiredIndex = 0; desiredIndex < orderedHosts.Count; desiredIndex++)
            {
                var host = orderedHosts[desiredIndex];
                var rowKey = AslmApiHostViewModel.BuildKey(host.ModuleId, host.HostKey);
                activeKeys.Add(rowKey);
                var moduleState = ResolveModuleDisplayState(host.ModuleId, moduleStates);

                if (_hostRows.TryGetValue(rowKey, out var existingRow))
                {
                    existingRow.Update(host, moduleState);
                    MoveHostRowIfNeeded(existingRow, desiredIndex);
                    continue;
                }

                var newRow = new AslmApiHostViewModel(host, moduleState, _notifications);
                _hostRows[rowKey] = newRow;
                Hosts.Insert(Math.Min(desiredIndex, Hosts.Count), newRow);
            }

            // Remove rows whose host disappeared from the port map; walk backwards so removal keeps indexes valid.
            for (var index = Hosts.Count - 1; index >= 0; index--)
            {
                var row = Hosts[index];
                if (activeKeys.Contains(row.Key))
                {
                    continue;
                }

                Hosts.RemoveAt(index);
                _hostRows.Remove(row.Key);
            }
        }

        /// <summary>
        /// Builds the display state (name and enabled flag) of the given modules keyed by module id.
        /// Modules without an id are skipped and the first of several modules sharing an id wins.
        /// </summary>
        private static Dictionary<string, AslmApiModuleDisplayState> BuildModuleDisplayStates(IEnumerable<ModuleConfig> modules)
        {
            return modules
                .Where(static module => !string.IsNullOrWhiteSpace(module.Id))
                .GroupBy(static module => module.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToDictionary(
                    static module => module.Id,
                    static module => new AslmApiModuleDisplayState(
                        string.IsNullOrWhiteSpace(module.Name) ? module.Id : module.Name.Trim(),
                        module.Status.Enabled),
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the human-readable module state used by the host list. Modules without a known name fall back
        /// to a name derived from the module id and are shown as disabled.
        /// </summary>
        internal static AslmApiModuleDisplayState ResolveModuleDisplayState(
            string moduleId,
            IReadOnlyDictionary<string, AslmApiModuleDisplayState> moduleStates)
        {
            if (moduleStates.TryGetValue(moduleId, out var moduleState) &&
                !string.IsNullOrWhiteSpace(moduleState.Name))
            {
                return moduleState;
            }

            return new AslmApiModuleDisplayState(AslmApiHostViewModel.NormalizeDisplayName(moduleId), false);
        }

        /// <summary>
        /// Keeps the displayed host list sorted by module name and port without recreating existing row views.
        /// </summary>
        /// <param name="row">Existing row to place.</param>
        /// <param name="desiredIndex">Sorted position the row should have in the list.</param>
        private void MoveHostRowIfNeeded(AslmApiHostViewModel row, int desiredIndex)
        {
            var currentIndex = Hosts.IndexOf(row);
            if (currentIndex >= 0 && currentIndex != desiredIndex)
            {
                Hosts.Move(currentIndex, Math.Min(desiredIndex, Hosts.Count - 1));
            }
        }

        /// <summary>
        /// Starts a background module-state refresh when the cached display state is stale.
        /// </summary>
        private void EnsureModuleDisplayStatesLoading()
        {
            var isLoadActive = _moduleDisplayStatesLoadTask is { IsCompleted: false };
            var isCacheFresh =
                _moduleDisplayStatesLoaded &&
                DateTime.UtcNow - _moduleDisplayStatesLoadedAt < ModuleStateRefreshInterval;

            if (isLoadActive || isCacheFresh)
            {
                return;
            }

            var loadVersion = ++_moduleDisplayStateLoadVersion;
            _moduleDisplayStatesLoadTask = LoadModuleDisplayStatesAsync(loadVersion);
        }

        /// <summary>
        /// Loads module display state without blocking initial page rendering.
        /// </summary>
        /// <param name="loadVersion">Version stamped when the load started; a newer version invalidates the result.</param>
        private async Task LoadModuleDisplayStatesAsync(int loadVersion)
        {
            Dictionary<string, AslmApiModuleDisplayState> moduleStates;

            // Discover the installed modules off the UI thread.
            try
            {
                var modules = await Task.Run(() => _moduleInstaller.DiscoverInstalledModulesAsync());
                moduleStates = BuildModuleDisplayStates(modules);
            }
            catch
            {
                // Module names are cosmetic: keep the previous state and let the next refresh retry.
                return;
            }

            // Publish the snapshot on the main thread unless module changes invalidated this load meanwhile.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (loadVersion != _moduleDisplayStateLoadVersion)
                {
                    return;
                }

                _moduleDisplayStates = moduleStates;
                _moduleDisplayStatesLoaded = true;
                _moduleDisplayStatesLoadedAt = DateTime.UtcNow;

                // Reconcile even an empty list: the initial port-map read may have preceded discovery.
                // Invalidate older reads so they cannot restore rows removed by this snapshot.
                ++_hostRefreshVersion;
                SynchronizeHostRows(_mirrorServer.GetHosts(), _moduleDisplayStates);
            });
        }

        /// <summary>
        /// Starts periodic refresh so the page follows runtime changes to ASLM_Ports.json.
        /// </summary>
        private void StartRefreshLoop()
        {
            StopRefreshLoop();
            _refreshLoopCts = new CancellationTokenSource();
            _ = RunRefreshLoopAsync(_refreshLoopCts.Token);
        }

        /// <summary>
        /// Stops the periodic refresh loop if it is active.
        /// </summary>
        private void StopRefreshLoop()
        {
            _refreshLoopCts?.Cancel();
            _refreshLoopCts?.Dispose();
            _refreshLoopCts = null;
        }

        /// <summary>
        /// Refreshes host rows at a modest interval while the view is visible.
        /// </summary>
        /// <param name="ct">Cancelled by <see cref="StopRefreshLoop"/> to end the loop.</param>
        private async Task RunRefreshLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), ct);
                    await MainThread.InvokeOnMainThreadAsync(RefreshAsync);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Refreshes server state and hosts on the main thread when the mirror server reports a state change.
        /// </summary>
        private void OnServerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
        }

        /// <summary>
        /// Invalidates cached module display states when module metadata or the installed set changes,
        /// and reloads the hosts right away when the page is visible.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                // Drop the cache and bump the version so a load already in flight cannot publish stale names.
                _moduleDisplayStatesLoaded = false;
                _moduleDisplayStatesLoadedAt = DateTime.MinValue;
                _moduleDisplayStatesLoadTask = null;
                _moduleDisplayStateLoadVersion++;

                if (_isVisible)
                {
                    await RefreshAsync();
                }
            });
        }

        /// <summary>
        /// Updates the bound server URL and the open-in-browser availability from the service state.
        /// </summary>
        private void ApplyServerState()
        {
            var isRunning = _mirrorServer.IsRunning;

            ServerUrl = _mirrorServer.BaseUrl;
            CanOpenServer = isRunning;
        }


        // Helpers

        /// <summary>
        /// Assigns a property value and raises a binding notification when it changes.
        /// </summary>
        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }


    // Host row models

    /// <summary>
    /// Stores the lightweight module metadata needed by ASLM API host rows.
    /// </summary>
    /// <param name="Name">Human-readable module name shown in the host row title.</param>
    /// <param name="IsEnabled">Whether the module is enabled; disabled modules get a badge in the host row.</param>
    public sealed record AslmApiModuleDisplayState(string Name, bool IsEnabled);

    /// <summary>
    /// Exposes one mirror host row to the ASLM API page.
    /// </summary>
    public class AslmApiHostViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="ModuleId"/>.
        /// </summary>
        private string _moduleId = string.Empty;

        /// <summary>
        /// Backing field of <see cref="ModuleName"/>.
        /// </summary>
        private string _moduleName = string.Empty;

        /// <summary>
        /// Backing field of <see cref="HostKey"/>.
        /// </summary>
        private string _hostKey = string.Empty;

        /// <summary>
        /// Backing field of <see cref="HostName"/>.
        /// </summary>
        private string _hostName = string.Empty;

        /// <summary>
        /// Backing field of <see cref="Port"/>.
        /// </summary>
        private int _port;

        /// <summary>
        /// Backing field of <see cref="MirrorUrl"/>.
        /// </summary>
        private string _mirrorUrl = string.Empty;

        /// <summary>
        /// Backing field of <see cref="IsModuleDisabled"/>.
        /// </summary>
        private bool _isModuleDisabled;

        /// <summary>
        /// Lazily created command behind <see cref="CopyCommand"/>.
        /// </summary>
        private Command? _copyCommand;

        /// <summary>
        /// Notification service that shows the copy confirmation toast.
        /// </summary>
        private readonly NotificationCenter _notifications;

        /// <summary>
        /// Backing field of <see cref="CopyButtonBackground"/>.
        /// </summary>
        private Color _copyButtonBackground = Colors.Black;

        /// <summary>
        /// Backing field of <see cref="CopyIconSource"/>.
        /// </summary>
        private ImageSource _copyIconSource = ImageSource.FromFile("icon_copy.png");


        // Initialization

        /// <summary>
        /// Creates a host row from the current service host info.
        /// </summary>
        /// <param name="host">Host entry read from the port map.</param>
        /// <param name="moduleState">Display state of the module that owns the host.</param>
        /// <param name="notifications">Notification service used for the copy confirmation toast.</param>
        public AslmApiHostViewModel(
            AslmMirrorHostInfo host,
            AslmApiModuleDisplayState moduleState,
            NotificationCenter notifications)
        {
            _notifications = notifications;
            Update(host, moduleState);
            RefreshCopyButtonChrome();
        }


        // Property notifications

        /// <summary>
        /// Raised when a bindable property on this host row changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Bound properties

        /// <summary>
        /// Gets the module identifier for this host.
        /// </summary>
        public string ModuleId
        {
            get => _moduleId;
            private set => SetProperty(ref _moduleId, value);
        }

        /// <summary>
        /// Gets the human-readable module name for display.
        /// </summary>
        public string ModuleName
        {
            get => _moduleName;
            private set => SetProperty(ref _moduleName, value);
        }

        /// <summary>
        /// Gets the port key declared under the module in the port map.
        /// </summary>
        public string HostKey
        {
            get => _hostKey;
            private set => SetProperty(ref _hostKey, value);
        }

        /// <summary>
        /// Gets the normalized host name for display.
        /// </summary>
        public string HostName
        {
            get => _hostName;
            private set => SetProperty(ref _hostName, value);
        }

        /// <summary>
        /// Gets the local target port used by the module service.
        /// </summary>
        public int Port
        {
            get => _port;
            private set => SetProperty(ref _port, value);
        }

        /// <summary>
        /// Gets the ASLM API mirror URL for this host.
        /// </summary>
        public string MirrorUrl
        {
            get => _mirrorUrl;
            private set => SetProperty(ref _mirrorUrl, value);
        }

        /// <summary>
        /// Gets whether the module that owns this host is disabled.
        /// </summary>
        public bool IsModuleDisabled
        {
            get => _isModuleDisabled;
            private set => SetProperty(ref _isModuleDisabled, value);
        }

        /// <summary>
        /// Gets the stable key used to diff host rows across refreshes.
        /// </summary>
        public string Key => BuildKey(ModuleId, HostKey);

        /// <summary>
        /// Gets the compact title shown in the host row.
        /// </summary>
        public string Title => $"{ModuleName} / {HostName}";

        /// <summary>
        /// Gets the compact status badge text for disabled modules.
        /// </summary>
        public string ModuleStatusText => IsModuleDisabled ? L.Get(LocalizationKeys.AslmApi_Disabled) : string.Empty;

        /// <summary>
        /// Gets the solid background for the copy action (white in dark theme, black in light theme).
        /// </summary>
        public Color CopyButtonBackground
        {
            get => _copyButtonBackground;
            private set => SetProperty(ref _copyButtonBackground, value);
        }

        /// <summary>
        /// Gets the theme-tinted copy glyph for the current appearance.
        /// </summary>
        public ImageSource CopyIconSource
        {
            get => _copyIconSource;
            private set => SetProperty(ref _copyIconSource, value);
        }

        /// <summary>
        /// Gets the command that copies this host URL to the system clipboard.
        /// </summary>
        public Command CopyCommand => _copyCommand ??= new Command(async () => await CopyMirrorUrlAsync());


        // Localization

        /// <summary>
        /// Refreshes localized bindable labels after a culture change.
        /// </summary>
        public void RefreshLocalizationLabels() =>
            OnPropertyChanged(nameof(ModuleStatusText));


        // Theme chrome

        /// <summary>
        /// Recomputes copy-button fill and tinted icon after theme or palette changes.
        /// </summary>
        public void RefreshCopyButtonChrome()
        {
            var dark = IsAppDarkAppearance();
            CopyButtonBackground = dark ? Color.FromArgb("#FFFFFFFF") : Color.FromArgb("#FF000000");
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            CopyIconSource = PackagedIconTintCache.Get("icon_copy.png", iconTint);
        }


        // Actions

        /// <summary>
        /// Copies the host mirror URL to the clipboard and publishes a toast confirmation.
        /// </summary>
        private async Task CopyMirrorUrlAsync()
        {
            await Clipboard.Default.SetTextAsync(MirrorUrl);
            _notifications.PublishSystemToast(
                L.Get(LocalizationKeys.AslmApi_AddressCopiedTitle),
                MirrorUrl,
                L.Get(LocalizationKeys.AslmApi_AddressCopiedStatus),
                $"aslm-api:{Key.GetHashCode(StringComparison.OrdinalIgnoreCase)}");
        }


        // Row updates

        /// <summary>
        /// Updates the row from fresh service data without recreating the row object.
        /// </summary>
        /// <param name="host">Host entry read from the port map.</param>
        /// <param name="moduleState">Display state of the module that owns the host.</param>
        public void Update(AslmMirrorHostInfo host, AslmApiModuleDisplayState moduleState)
        {
            ModuleId = host.ModuleId;
            UpdateModuleState(moduleState);
            HostKey = host.HostKey;
            HostName = NormalizeHostName(host.HostKey);
            Port = host.Port;
            MirrorUrl = host.MirrorUrl;

            OnPropertyChanged(nameof(Key));
        }

        /// <summary>
        /// Updates only the displayed module name and disabled badge after the background cache refreshes.
        /// </summary>
        /// <param name="moduleState">Fresh display state of the module that owns the host.</param>
        public void UpdateModuleState(AslmApiModuleDisplayState moduleState)
        {
            ModuleName = moduleState.Name;
            IsModuleDisabled = !moduleState.IsEnabled;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ModuleStatusText));
        }


        // Helpers

        /// <summary>
        /// Resolves whether the app should treat the current appearance as dark.
        /// </summary>
        private static bool IsAppDarkAppearance()
        {
            if (Application.Current is not { } app)
            {
                return false;
            }

            return app.RequestedTheme switch
            {
                AppTheme.Dark => true,
                AppTheme.Light => false,
                _ => ThemeService.IsSystemDark()
            };
        }

        /// <summary>
        /// Builds the stable key used by the ASLM API host list from the module id and the port-map host key.
        /// </summary>
        public static string BuildKey(string moduleId, string hostKey)
        {
            return $"{moduleId}\n{hostKey}";
        }

        /// <summary>
        /// Normalizes a technical identifier such as <c>my-module_api</c> into a compact display name
        /// by splitting it into words and title-casing them (<c>My Module API</c>).
        /// </summary>
        public static string NormalizeDisplayName(string value)
        {
            var words = (value ?? string.Empty)
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (words.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(' ', words.Select(FormatDisplayWord));
        }

        /// <summary>
        /// Normalizes a host port-map key into a user-facing host name.
        /// </summary>
        private static string NormalizeHostName(string hostKey)
        {
            var trimmed = TrimHostPortSuffix(hostKey?.Trim() ?? string.Empty);
            return NormalizeDisplayName(trimmed);
        }

        /// <summary>
        /// Removes the conventional trailing port marker (<c>-port</c>, <c>_port</c> or <c> port</c>) from host keys.
        /// </summary>
        private static string TrimHostPortSuffix(string value)
        {
            foreach (var suffix in new[] { "-port", "_port", " port" })
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    value.Length > suffix.Length)
                {
                    return value[..^suffix.Length];
                }
            }

            return value;
        }

        /// <summary>
        /// Formats one identifier word as title case, keeping the acronyms API and UI in upper case.
        /// </summary>
        private static string FormatDisplayWord(string word)
        {
            var lower = word.ToLowerInvariant();
            if (lower is "api" or "ui")
            {
                return lower.ToUpperInvariant();
            }

            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower);
        }


        // Property notifications

        /// <summary>
        /// Raises one binding notification for this row.
        /// </summary>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Assigns a row property and notifies bindings when it changes.
        /// </summary>
        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
