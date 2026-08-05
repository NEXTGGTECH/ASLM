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
    /// <summary>
    /// Displays and controls the local ASLM API mirror server.
    /// </summary>
    public partial class AslmApiView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        private readonly AslmMirrorServer _mirrorServer;
        private readonly NotificationCenter _notifications;
        private readonly ModuleInstaller _moduleInstaller;
        private readonly AppLocalizationService _localization;
        private readonly Dictionary<string, AslmApiHostViewModel> _hostRows = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, AslmApiModuleDisplayState> _moduleDisplayStates = new(StringComparer.OrdinalIgnoreCase);
        private Task? _moduleDisplayStatesLoadTask;
        private int _moduleDisplayStateLoadVersion;
        private bool _moduleDisplayStatesLoaded;
        private DateTime _moduleDisplayStatesLoadedAt = DateTime.MinValue;
        private CancellationTokenSource? _refreshLoopCts;
        private bool _isVisible;
        private string _serverUrl = string.Empty;
        private bool _canOpenServer;

        private static readonly TimeSpan ModuleStateRefreshInterval = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Stores the host rows displayed by the page.
        /// </summary>
        public ObservableCollection<AslmApiHostViewModel> Hosts { get; } = new();

        // Initialization

        /// <summary>
        /// Creates the ASLM API page and hooks service state notifications.
        /// </summary>
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

            if (HostsCollection.EmptyView is Label empty)
            {
                empty.Text = L.Get(LocalizationKeys.AslmApi_NoHosts);
            }

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
            ThemeService.PaletteApplied -= OnPaletteAppliedForCopyButtons;
            ThemeService.PaletteApplied += OnPaletteAppliedForCopyButtons;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            RefreshHostCopyButtonChrome();
            _ = RefreshAsync();
            StartRefreshLoop();
        }

        /// <summary>
        /// Stops periodic host refresh when the page leaves the visual tree.
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
        /// Refreshes host copy-button chrome after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForCopyButtons()
        {
            MainThread.BeginInvokeOnMainThread(RefreshHostCopyButtonChrome);
        }

        /// <summary>
        /// Refreshes host copy-button chrome when the application theme changes.
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
            ApplyServerState();
            EnsureModuleDisplayStatesLoading();

            var hosts = await Task.Run(_mirrorServer.GetHosts);
            SynchronizeHostRows(hosts, _moduleDisplayStates);
        }

        /// <summary>
        /// Updates host rows in place so periodic refreshes do not recreate the list visual tree.
        /// </summary>
        private void SynchronizeHostRows(
            IReadOnlyList<AslmMirrorHostInfo> hosts,
            IReadOnlyDictionary<string, AslmApiModuleDisplayState> moduleStates)
        {
            var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var orderedHosts = hosts
                .OrderBy(
                    host => ResolveModuleDisplayState(host.ModuleId, moduleStates).Name,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(static host => host.Port)
                .ThenBy(static host => host.HostKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

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
        /// Loads the current module display state keyed by stable module id.
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
        /// Resolves the human-readable module state used by the host list.
        /// </summary>
        private static AslmApiModuleDisplayState ResolveModuleDisplayState(
            string moduleId,
            IReadOnlyDictionary<string, AslmApiModuleDisplayState> moduleStates)
        {
            if (moduleStates.TryGetValue(moduleId, out var moduleState) &&
                !string.IsNullOrWhiteSpace(moduleState.Name))
            {
                return moduleState;
            }

            return new AslmApiModuleDisplayState(AslmApiHostViewModel.NormalizeDisplayName(moduleId), true);
        }

        /// <summary>
        /// Keeps the displayed host list sorted by module name and port without recreating existing row views.
        /// </summary>
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
        private async Task LoadModuleDisplayStatesAsync(int loadVersion)
        {
            Dictionary<string, AslmApiModuleDisplayState> moduleStates;

            try
            {
                var modules = await Task.Run(() => _moduleInstaller.DiscoverModulesAsync());
                moduleStates = BuildModuleDisplayStates(modules);
            }
            catch
            {
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (loadVersion != _moduleDisplayStateLoadVersion)
                {
                    return;
                }

                _moduleDisplayStates = moduleStates;
                _moduleDisplayStatesLoaded = true;
                _moduleDisplayStatesLoadedAt = DateTime.UtcNow;

                foreach (var row in Hosts)
                {
                    row.UpdateModuleState(ResolveModuleDisplayState(row.ModuleId, _moduleDisplayStates));
                }

                if (Hosts.Count > 0)
                {
                    SynchronizeHostRows(_mirrorServer.GetHosts(), _moduleDisplayStates);
                }
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
        /// Reacts to server state changes raised outside the page.
        /// </summary>
        private void OnServerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () => await RefreshAsync());
        }

        /// <summary>
        /// Invalidates cached module status after module manifests are saved.
        /// </summary>
        private void OnModulesChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
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
        /// Updates bound server fields from the service state.
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

    /// <summary>
    /// Stores the lightweight module metadata needed by ASLM API host rows.
    /// </summary>
    public sealed record AslmApiModuleDisplayState(string Name, bool IsEnabled);


    /// <summary>
    /// Exposes one mirror host row to the ASLM API page.
    /// </summary>
    public class AslmApiHostViewModel : INotifyPropertyChanged
    {
        private string _moduleId = string.Empty;
        private string _moduleName = string.Empty;
        private string _hostKey = string.Empty;
        private string _hostName = string.Empty;
        private int _port;
        private string _mirrorUrl = string.Empty;
        private bool _isModuleDisabled;
        private Command? _copyCommand;
        private readonly NotificationCenter _notifications;
        private Color _copyButtonBackground = Colors.Black;
        private ImageSource _copyIconSource = ImageSource.FromFile("icon_copy.png");


        // Initialization

        /// <summary>
        /// Creates a host row from the current service host info.
        /// </summary>
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
        /// Copies the host URL and publishes a shared toast confirmation.
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
        /// Updates only the displayed module state after the background cache refreshes.
        /// </summary>
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
        /// Builds the stable key used by the ASLM API host list.
        /// </summary>
        public static string BuildKey(string moduleId, string hostKey)
        {
            return $"{moduleId}\n{hostKey}";
        }

        /// <summary>
        /// Normalizes a technical identifier into a compact display name.
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
        /// Removes the conventional trailing port marker from host keys.
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
        /// Formats one identifier word while preserving common technical acronyms.
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
