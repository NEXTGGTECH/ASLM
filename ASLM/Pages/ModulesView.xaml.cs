// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls;

namespace ASLM.Pages
{
    /// <summary>
    /// Displays module cards and keeps the grid responsive inside the shell.
    /// </summary>
    public partial class ModulesView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        // Fields and constants

        /// <summary>
        /// Minimum width of one module card; the grid gets as many columns as fit at this width.
        /// </summary>
        private const double MinCardWidth = 440;

        /// <summary>
        /// Whether the uninstall confirmation dialog is open; blocks a second removal or reinstall dialog.
        /// </summary>
        private bool _confirmingRemoval;

        /// <summary>
        /// Whether the reinstall form is open; blocks a second removal or reinstall dialog.
        /// </summary>
        private bool _confirmingReinstall;

        /// <summary>
        /// Card whose action menu is currently shown in the shared menu layer, or null when no menu is open.
        /// </summary>
        private ModuleViewModel? _menuModule;

        /// <summary>
        /// Bounds of the button that opened the shared menu, in dashboard coordinates.
        /// </summary>
        private Rect _menuAnchor;

        /// <summary>
        /// Service that checks, applies and reports module updates.
        /// </summary>
        private readonly UpdateManager _updateManager;

        /// <summary>
        /// Service that resolves the trust level of a module (verified badge or warning); passed on to every card.
        /// </summary>
        private readonly ModuleTrustService _moduleTrustService;

        /// <summary>
        /// Coordinator that runs the module launch sequence on behalf of the cards.
        /// </summary>
        private readonly ModuleLaunchCoordinator _launchCoordinator;

        /// <summary>
        /// Localization service used to re-apply texts when the language changes.
        /// </summary>
        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Saved application settings; read for the consoles sidebar flag.
        /// </summary>
        private readonly AppDataStore _appData;

        /// <summary>
        /// Application shell that hosts this view and handles navigation and overlays; set by <see cref="Initialize"/>.
        /// </summary>
        private AppShellPage? _shell;

        /// <summary>
        /// Console store of the module runner, observed so cards refresh when module consoles change.
        /// </summary>
        private ModuleConsoleStore? _consoleStore;

        /// <summary>
        /// Backing field of <see cref="GridSpan"/>.
        /// </summary>
        private int _gridSpan = 1;

        /// <summary>
        /// Backing field of <see cref="MoreIconSource"/>.
        /// </summary>
        private ImageSource _moreIconSource = null!;

        /// <summary>
        /// Source paths of modules being uninstalled; re-applied to cards that a refresh rebuilds meanwhile.
        /// </summary>
        private readonly HashSet<string> _removingSourcePaths = new(StringComparer.OrdinalIgnoreCase);


        // View data

        /// <summary>
        /// Stores the module cards shown in the dashboard.
        /// </summary>
        public ObservableCollection<ModuleViewModel> Modules { get; } = new();

        /// <summary>
        /// Shows the logs action only while the consoles page is enabled in saved settings.
        /// </summary>
        public bool ShowLogsAction => _appData.Data.Consoles.SidebarVisible;


        // Layout state

        /// <summary>
        /// Gets the theme-tinted more-menu glyph for module cards.
        /// </summary>
        public ImageSource MoreIconSource
        {
            get => _moreIconSource;
            private set
            {
                if (_moreIconSource == value)
                {
                    return;
                }

                _moreIconSource = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Gets or sets the number of columns used by the responsive grid; recalculated from the view width.
        /// </summary>
        public int GridSpan
        {
            get => _gridSpan;
            set
            {
                if (_gridSpan == value)
                {
                    return;
                }

                _gridSpan = value;
                OnPropertyChanged();
            }
        }


        // Initialization

        /// <summary>
        /// Creates the module management view, binds it to itself and hooks the list, lifecycle, resize and
        /// localization events. The module list itself is supplied later by <see cref="Initialize"/>.
        /// </summary>
        /// <param name="updateManager">Service that checks and applies module updates.</param>
        /// <param name="moduleTrustService">Service that resolves the trust level shown on each card.</param>
        /// <param name="launchCoordinator">Coordinator that runs the module launch sequence for the cards.</param>
        /// <param name="localization">Localization service whose language changes re-apply the texts.</param>
        /// <param name="appData">Saved application settings.</param>
        public ModulesView(
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            ModuleLaunchCoordinator launchCoordinator,
            AppLocalizationService localization,
            AppDataStore appData)
        {
            _updateManager = updateManager;
            _moduleTrustService = moduleTrustService;
            _launchCoordinator = launchCoordinator;
            _localization = localization;
            _appData = appData;
            InitializeComponent();
            BindingContext = this;
            DashboardView.HandlerChanged += OnDashboardViewHandlerChanged;
            DashboardView.Scrolled += OnDashboardScrolled;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += OnSizeChanged;
            LocalizableAttach.Hook(this, _localization, this);
            RefreshMoreIconChrome();
        }

        /// <summary>
        /// Re-applies the page title and empty-state texts and refreshes the localized labels of every card.
        /// </summary>
        public void ApplyLocalization()
        {
            PageTitleLabel.Text = L.Get(LocalizationKeys.Modules_Title);
            EmptyModulesLabel.Text = L.Get(LocalizationKeys.Modules_Empty);
            foreach (var module in Modules)
            {
                module.RefreshLocalizedLabels();
            }
        }


        // Notifications

        /// <summary>
        /// Raised when a bindable property of this view changes; hides the inherited event so it can be raised here.
        /// </summary>
        public new event PropertyChangedEventHandler? PropertyChanged;


        // Property change

        /// <summary>
        /// Raises the view-level property changed event for the calling member or the given property name.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }


        // Data binding

        /// <summary>
        /// Connects the dashboard to the shell and module services and shows the initial module list.
        /// The console store subscription is moved to the new runner, and only held while the view is loaded.
        /// </summary>
        /// <param name="shell">Shell used for navigation and info overlays.</param>
        /// <param name="modules">Discovered module configs to show as cards.</param>
        /// <param name="installer">Installer the cards use for installation state and config persistence.</param>
        /// <param name="runner">Runner the cards use to stop modules and reach their consoles.</param>
        internal void Initialize(
            AppShellPage shell,
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            _shell = shell;
            if (_consoleStore != null) _consoleStore.StateChanged -= OnMaintenanceChanged;
            _consoleStore = runner.ConsoleStore;
            if (IsLoaded) _consoleStore.StateChanged += OnMaintenanceChanged;
            PopulateModules(modules, installer, runner);
        }


        // Refresh

        /// <summary>
        /// Rebuilds the dashboard from the latest module list.
        /// </summary>
        /// <param name="modules">Discovered module configs to show as cards.</param>
        /// <param name="installer">Installer the cards use for installation state and config persistence.</param>
        /// <param name="runner">Runner the cards use to stop modules and reach their consoles.</param>
        internal void RefreshModules(
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            PopulateModules(modules, installer, runner);
        }

        /// <summary>
        /// Scrolls the card of the module with the given source path into view; does nothing when it is not listed.
        /// </summary>
        internal void FocusModule(string sourcePath)
        {
            var module = Modules.FirstOrDefault(candidate =>
                string.Equals(candidate.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));

            if (module == null || Dispatcher == null)
            {
                return;
            }

            Dispatcher.Dispatch(() =>
                DashboardView.ScrollTo(module, position: ScrollToPosition.MakeVisible, animate: true));
        }


        // Module population

        /// <summary>
        /// Recreates the module card view models from the current config list. Each card receives this view's
        /// handlers as callbacks, and cards of modules that are being uninstalled keep their removing state.
        /// </summary>
        private void PopulateModules(
            List<ModuleConfig> modules,
            ModuleInstaller installer,
            ModuleRunner runner)
        {
            // A rebuilt or recycled card must not leave actions bound to its previous view model.
            CloseAllMenus();
            Modules.Clear();

            foreach (var module in modules)
            {
                var viewModel = new ModuleViewModel(
                    module,
                    installer,
                    runner,
                    _launchCoordinator,
                    _updateManager,
                    _moduleTrustService,
                    OnModuleStateChanged,
                    OnMenuToggleRequested,
                    OpenConfigureUpdates,
                    UpdateModule,
                    CheckModuleUpdates,
                    OnRemoveRequested,
                    OnReinstallRequested,
                    OpenModuleInfo,
                    OpenModuleLogs);
                viewModel.SetRemoving(_removingSourcePaths.Contains(module.SourcePath));
                Modules.Add(viewModel);
            }
        }


        // State callback

        /// <summary>
        /// Forwards module state changes back to the shell.
        /// </summary>
        private void OnModuleStateChanged()
        {
            _shell?.OnModuleStateChanged();
        }


        // Menu flow

        /// <summary>
        /// Captures the tapped button's bounds in dashboard coordinates, then asks the card to toggle its menu
        /// so <see cref="OnMenuToggleRequested"/> can position the shared popup against them.
        /// </summary>
        private void OnModuleMenuTapped(object? sender, TappedEventArgs e)
        {
            if (sender is not View { BindingContext: ModuleViewModel module } anchor ||
                e.GetPosition(DashboardRoot) is not { } dashboardPoint ||
                e.GetPosition(anchor) is not { } localPoint)
            {
                return;
            }

            // Native gesture coordinates include collection scrolling and recycled item offsets on both platforms.
            _menuAnchor = new Rect(dashboardPoint.X - localPoint.X, dashboardPoint.Y - localPoint.Y,
                anchor.Width, anchor.Height);
            module.ToggleMenuCommand.Execute(null);
        }

        /// <summary>
        /// Opens the shared action menu for the card, or closes it when that card's menu was already open.
        /// Any menu that was open before is closed first, so only one card is bound to the popup at a time.
        /// </summary>
        private void OnMenuToggleRequested(ModuleViewModel module)
        {
            var shouldOpen = !module.IsMenuOpen;
            CloseAllMenus();
            if (!shouldOpen)
            {
                return;
            }

            // Read saved settings on every opening so a cached dashboard never exposes disabled consoles.
            OnPropertyChanged(nameof(ShowLogsAction));

            // One dashboard-level menu can extend beyond a card without affecting the card's size.
            _menuModule = module;
            module.PropertyChanged += OnMenuModulePropertyChanged;
            ModuleMenuPopup.BindingContext = module;
            module.SetMenuOpen(true);
            ModuleMenuLayer.IsVisible = true;
            UpdateModuleMenuPosition();
            Dispatcher.Dispatch(UpdateModuleMenuPosition);
        }

        /// <summary>
        /// Closes the shared menu layer when a command or lifecycle transition closes the open card's menu.
        /// </summary>
        private void OnMenuModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ModuleViewModel.IsMenuOpen) && _menuModule?.IsMenuOpen == false)
            {
                CloseAllMenus();
            }
        }

        /// <summary>
        /// Dismisses card actions when the user taps the surrounding dashboard.
        /// </summary>
        private void OnMenuBackdropTapped(object? sender, TappedEventArgs e) => CloseAllMenus();

        /// <summary>
        /// Measures the visible actions and fits the popup to the dashboard, scrolling only when necessary.
        /// Does nothing while no menu is open or the dashboard has not been laid out yet.
        /// </summary>
        private void UpdateModuleMenuPosition()
        {
            if (_menuModule == null || DashboardRoot.Width <= 0 || DashboardRoot.Height <= 0)
            {
                return;
            }

            // Border padding and stroke on both sides.
            const double inset = 10;
            var viewport = new Size(DashboardRoot.Width, DashboardRoot.Height);

            // Measure content without a height limit so adding actions never silently hides the last one.
            var content = ModuleMenuItems.Measure(double.PositiveInfinity, double.PositiveInfinity);

            // Scrolling is needed only when the content does not fit the viewport; the popup is then widened
            // to make room for the scroll bar next to the items.
            var needsScroll = content.Height + inset > viewport.Height - 8;
            var desired = new Size(content.Width + inset + (needsScroll ? 12 : 0), content.Height + inset);
            var bounds = CalculateModuleMenuBounds(_menuAnchor, viewport, desired);
            AbsoluteLayout.SetLayoutBounds(ModuleMenuPopup, bounds);
        }

        /// <summary>
        /// Aligns a menu to its button and keeps every edge inside the available dashboard viewport.
        /// </summary>
        /// <param name="anchor">Bounds of the button that opened the menu, in dashboard coordinates.</param>
        /// <param name="viewport">Size of the dashboard the menu must stay inside.</param>
        /// <param name="desired">Size the menu content asks for; clamped to the viewport minus the edge margins.</param>
        /// <returns>The menu bounds, right-aligned to the button and placed below or above it.</returns>
        internal static Rect CalculateModuleMenuBounds(Rect anchor, Size viewport, Size desired)
        {
            // Minimum distance to the viewport edges and gap between the button and the menu.
            const double edge = 4;
            const double gap = 4;

            // Shrink the menu to the viewport and compute the furthest position that still fits.
            var width = Math.Min(desired.Width, Math.Max(0, viewport.Width - edge * 2));
            var height = Math.Min(desired.Height, Math.Max(0, viewport.Height - edge * 2));
            var maxX = Math.Max(edge, viewport.Width - width - edge);
            var maxY = Math.Max(edge, viewport.Height - height - edge);
            var below = anchor.Bottom + gap;
            var above = anchor.Top - gap - height;

            // Prefer below the button, then above it; tiny windows constrain the menu's scroll viewport.
            var y = below > maxY && above >= edge ? above : below;
            return new Rect(Math.Clamp(anchor.Right - width, edge, maxX),
                Math.Clamp(y, edge, maxY), width, height);
        }


        // Module actions

        /// <summary>
        /// Opens the module update configuration overlay for the selected card.
        /// </summary>
        private void OpenConfigureUpdates(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleInfoOverlay(module.Config, ModuleInfo.Section.Configuration);
        }

        /// <summary>
        /// Checks and applies the selected update without opening an overlay or changing navigation.
        /// Ignored while the module is busy or being removed.
        /// </summary>
        private async void UpdateModule(ModuleViewModel module)
        {
            if (module.IsBusy || module.IsRemoving)
            {
                return;
            }

            CloseAllMenus();
            await _updateManager.UpdateModuleAsync(module.Config);

            // Refresh persisted version and pending-update badges after success, failure or an empty check.
            OnModuleStateChanged();
        }

        /// <summary>
        /// Checks the module source without installing a previously found or newly discovered update.
        /// Ignored while the module is busy or being removed.
        /// </summary>
        private async void CheckModuleUpdates(ModuleViewModel module)
        {
            if (module.IsBusy || module.IsRemoving)
            {
                return;
            }

            // Checking and updating are separate menu actions with separate service entry points.
            CloseAllMenus();
            await _updateManager.CheckModuleUpdatesAsync(module.Config);
            OnModuleStateChanged();
        }

        /// <summary>
        /// Opens public module metadata without starting any maintenance operation.
        /// </summary>
        private void OpenModuleInfo(ModuleViewModel module)
        {
            CloseAllMenus();
            _shell?.OpenModuleInfoOverlay(module.Config, ModuleInfo.Section.Info);
        }

        /// <summary>
        /// Opens a running module's merged console when console navigation is enabled.
        /// </summary>
        private void OpenModuleLogs(ModuleViewModel module)
        {
            // Recheck both conditions in case the module stopped after the menu was opened.
            CloseAllMenus();
            if (ShowLogsAction && module.IsRunning)
            {
                _shell?.OpenConsoles(module.SourcePath);
            }
        }

        /// <summary>
        /// Closes every module mini menu in the dashboard.
        /// </summary>
        private void CloseAllMenus()
        {
            // Detach before resetting the view model to avoid recursive close notifications.
            if (_menuModule != null)
            {
                _menuModule.PropertyChanged -= OnMenuModulePropertyChanged;
                _menuModule = null;
            }

            // Hide the shared layer, unbind the popup, and clear the open flag on every card.
            ModuleMenuLayer.IsVisible = false;
            ModuleMenuPopup.BindingContext = null;
            foreach (var module in Modules)
            {
                module.SetMenuOpen(false);
            }
        }

        /// <summary>
        /// Confirms uninstall in the dashboard overlay and keeps the card busy throughout removal.
        /// </summary>
        private async void OnRemoveRequested(ModuleViewModel module)
        {
            // Allow only one confirmation dialog at a time, and only for a card that can be removed right now.
            if (_confirmingRemoval || _confirmingReinstall || !module.RemoveCommand.CanExecute(null)) return;
            CloseAllMenus();

            // Ask for confirmation; the flag is released even when the dialog fails.
            _confirmingRemoval = true;
            bool confirmed;
            try
            {
                confirmed = await DialogService.For(this).ConfirmAsync(this,
                    DialogText.Localized(LocalizationKeys.Modules_RemoveConfirmTitle, module.Name),
                    DialogText.Localized(LocalizationKeys.Modules_RemoveConfirmFormat, module.Name),
                    DialogText.Localized(LocalizationKeys.Modules_Remove), danger: true);
            }
            finally { _confirmingRemoval = false; }

            // The card may have become busy while the dialog was open, so check the command again.
            if (!confirmed) return;
            if (!module.RemoveCommand.CanExecute(null)) return;

            // Uninstall while marking the module as removing; a failure is kept and reported after cleanup.
            SetRemovingModule(module.SourcePath, true);
            string? retainedFiles = null;
            Exception? failure = null;
            try
            {
                retainedFiles = await module.UninstallAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // A manifest refresh may have replaced the card while the uninstall was running.
                module.SetRemoving(false);
                SetRemovingModule(module.SourcePath, false);
            }

            // Report the outcome unless the view was unloaded meanwhile: an error, or files that could not be deleted.
            if (!IsLoaded) return;
            if (failure != null)
                await DialogService.For(this).AlertAsync(this, DialogText.Localized(LocalizationKeys.Modules_RemoveFailed), failure.Message);
            else if (retainedFiles != null)
                await DialogService.For(this).AlertAsync(this, DialogText.Localized(LocalizationKeys.Modules_RemoveCleanupTitle),
                    DialogText.Localized(LocalizationKeys.Modules_RemoveCleanupFormat, retainedFiles));
        }

        /// <summary>
        /// Confirms a clean reinstall with a detached copy of the latest saved update preferences.
        /// </summary>
        private async void OnReinstallRequested(ModuleViewModel module)
        {
            // Allow only one confirmation dialog at a time, and only for a card that can be reinstalled right now.
            if (_confirmingRemoval || _confirmingReinstall || !module.ReinstallCommand.CanExecute(null)) return;
            CloseAllMenus();
            _confirmingReinstall = true;
            try
            {
                ModuleUpdateConfig selection;

                // Dispose the form as soon as it closes; the queued job owns its own preferences snapshot.
                try
                {
                    // Show the form first and fill in its update options asynchronously, so the dialog opens at once.
                    using var form = new ModuleReinstallForm(await module.LoadReinstallConfigAsync(), _updateManager);
                    if (!IsLoaded) return;
                    var dialog = DialogService.For(this).ShowAsync(this, form.Request);
                    _ = form.LoadOptionsAsync();
                    var result = await dialog;
                    if (result.IsCancelled) return;
                    selection = form.BuildSelection(result.Values);
                }
                finally { _confirmingReinstall = false; }

                // The card may have become busy while the form was open, so check the command again. Then reinstall
                // with the chosen preferences and report a launch failure of the reinstalled module; started,
                // already running and nothing to run all count as success.
                if (!module.ReinstallCommand.CanExecute(null)) return;
                var launch = await module.ReinstallAsync(selection);
                if (IsLoaded && launch.Status is not (ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning or ModuleLaunchStatus.NoRunCommands))
                    await DialogService.For(this).AlertAsync(this,
                        module.Name,
                        DialogText.Localized(LocalizationKeys.ModuleInfo_Failed, launch.Message ?? launch.Status.ToString()));
            }
            catch (OperationCanceledException)
            {
                // A cancelled configuration load or reinstall ends the flow without an error dialog.
            }
            catch (Exception ex)
            {
                if (IsLoaded) await DialogService.For(this).AlertAsync(this,
                    DialogText.Localized(LocalizationKeys.Modules_ReinstallFailed), ex.Message);
            }
        }

        /// <summary>
        /// Records that a module is (or is no longer) being uninstalled and applies it to every card of that module,
        /// so the state survives a manifest refresh that replaces a visible card.
        /// </summary>
        private void SetRemovingModule(string sourcePath, bool removing)
        {
            if (removing) _removingSourcePaths.Add(sourcePath);
            else _removingSourcePaths.Remove(sourcePath);
            foreach (var module in Modules)
                module.SetRemoving(_removingSourcePaths.Contains(module.SourcePath));
        }


        // Layout events

        /// <summary>
        /// Closes any open menu and recalculates the grid columns when the view size changes.
        /// </summary>
        private void OnSizeChanged(object? sender, EventArgs e)
        {
            CloseAllMenus();
            RecalculateGridSpan();
        }

        /// <summary>
        /// Dismisses actions before collection scrolling moves or recycles their anchor card.
        /// </summary>
        private void OnDashboardScrolled(object? sender, ItemsViewScrolledEventArgs e) => CloseAllMenus();

        /// <summary>
        /// Subscribes to update, console and theme changes, refreshes the cards and reapplies native list behavior
        /// after the view enters the visual tree.
        /// </summary>
        private void OnLoaded(object? sender, EventArgs e)
        {
            // Follow update and console activity; unsubscribing first keeps repeated loads from stacking handlers.
            _updateManager.ModuleUpdateStateChanged -= OnMaintenanceChanged;
            _updateManager.ModuleUpdateStateChanged += OnMaintenanceChanged;
            if (_consoleStore != null)
            {
                _consoleStore.StateChanged -= OnMaintenanceChanged;
                _consoleStore.StateChanged += OnMaintenanceChanged;

                // Events raised while the view was unloaded were missed, so bring the cards up to date.
                foreach (var module in Modules) module.RefreshMaintenanceState();
            }

            // Follow palette and system theme changes so the tinted more-menu icon stays readable.
            ThemeService.PaletteApplied -= OnPaletteAppliedForMoreIcon;
            ThemeService.PaletteApplied += OnPaletteAppliedForMoreIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
                app.RequestedThemeChanged += OnApplicationRequestedThemeChanged;
            }

            // Tint the icon for the current palette and disable the native list transitions.
            RefreshMoreIconChrome();
            DisableDashboardItemTransitions();
        }

        /// <summary>
        /// Closes open menus and unsubscribes from update, console and theme events when the view leaves the visual
        /// tree.
        /// </summary>
        private void OnUnloaded(object? sender, EventArgs e)
        {
            CloseAllMenus();
            _updateManager.ModuleUpdateStateChanged -= OnMaintenanceChanged;
            if (_consoleStore != null) _consoleStore.StateChanged -= OnMaintenanceChanged;
            ThemeService.PaletteApplied -= OnPaletteAppliedForMoreIcon;
            if (Application.Current is { } app)
            {
                app.RequestedThemeChanged -= OnApplicationRequestedThemeChanged;
            }
        }

        /// <summary>
        /// Refreshes the maintenance state of every card on the main thread when update or console state changes,
        /// so updates started outside the dashboard show up on existing cards.
        /// </summary>
        private void OnMaintenanceChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!IsLoaded) return;
            foreach (var module in Modules) module.RefreshMaintenanceState();
        });

        /// <summary>
        /// Re-tints the more-menu icon on the main thread after a custom palette is applied.
        /// </summary>
        private void OnPaletteAppliedForMoreIcon()
        {
            MainThread.BeginInvokeOnMainThread(RefreshMoreIconChrome);
        }

        /// <summary>
        /// Re-tints the more-menu icon on the main thread when the application theme changes.
        /// </summary>
        private void OnApplicationRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(RefreshMoreIconChrome);
        }

        /// <summary>
        /// Recomputes the tinted more-menu icon for the current palette.
        /// </summary>
        private void RefreshMoreIconChrome()
        {
            var iconTint = IconTintHelper.ResolvePaletteColor("LabelPrimary");
            MoreIconSource = PackagedIconTintCache.Get("icon_more.png", iconTint);
        }

        /// <summary>
        /// Removes native list entrance transitions whenever the dashboard's platform handler is attached or replaced.
        /// </summary>
        private void OnDashboardViewHandlerChanged(object? sender, EventArgs e)
        {
            DisableDashboardItemTransitions();
        }


        // Background input

        /// <summary>
        /// Closes any open module menu when the user taps outside the popup actions.
        /// </summary>
        private void OnBackgroundTapped(object? sender, TappedEventArgs e)
        {
            CloseAllMenus();
        }


        // Layout calculation

        /// <summary>
        /// Computes the number of columns that fit into the view width (minus 60) at <see cref="MinCardWidth"/>,
        /// with at least one column. Keeps the current span until the view has a usable width.
        /// </summary>
        private void RecalculateGridSpan()
        {
            var availableWidth = Width - 60;
            if (availableWidth > 0)
            {
                GridSpan = Math.Max(1, (int)(availableWidth / MinCardWidth));
            }
        }


        // Native list behavior

        /// <summary>
        /// Disables platform item transitions for the module dashboard; only the Windows build has native work to do.
        /// </summary>
        private void DisableDashboardItemTransitions()
        {
#if WINDOWS
            if (DashboardView.Handler?.PlatformView is not Microsoft.UI.Xaml.DependencyObject platformView)
            {
                return;
            }

            DisableWinUiItemTransitions(platformView);
#endif
        }

#if WINDOWS
        /// <summary>
        /// Clears WinUI item transitions from the native collection surface and its visual children.
        /// </summary>
        private static void DisableWinUiItemTransitions(Microsoft.UI.Xaml.DependencyObject element)
        {
            if (element is Microsoft.UI.Xaml.Controls.ListViewBase listView)
            {
                listView.ItemContainerTransitions = new Microsoft.UI.Xaml.Media.Animation.TransitionCollection();
            }

            var childrenCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
            for (var index = 0; index < childrenCount; index++)
            {
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, index) is Microsoft.UI.Xaml.DependencyObject child)
                {
                    DisableWinUiItemTransitions(child);
                }
            }
        }
#endif
    }

    /// <summary>
    /// Wraps one module config and exposes UI state and commands for its card.
    /// </summary>
    public class ModuleViewModel : INotifyPropertyChanged
    {
        // Fields

        /// <summary>
        /// Module config this card wraps; its settings, commands and status are updated in place.
        /// </summary>
        private readonly ModuleConfig _config;

        /// <summary>
        /// Installer used to query installation and removal state and to load and save the module config.
        /// </summary>
        private readonly ModuleInstaller _installer;

        /// <summary>
        /// Runner used to stop the module and to reach its console store.
        /// </summary>
        private readonly ModuleRunner _runner;

        /// <summary>
        /// Coordinator that runs the launch sequence for the launch and restart actions.
        /// </summary>
        private readonly ModuleLaunchCoordinator _launchCoordinator;

        /// <summary>
        /// Service that checks for updates and owns the pending update candidates.
        /// </summary>
        private readonly UpdateManager _updateManager;

        /// <summary>
        /// Callback that tells the dashboard the module state changed after a launch, stop or restart.
        /// </summary>
        private readonly Action _onStateChanged;

        /// <summary>
        /// Callback asking the dashboard to open or close the shared action menu for this card.
        /// </summary>
        private readonly Action<ModuleViewModel> _onMenuToggleRequested;

        /// <summary>
        /// Callback asking the dashboard to open the update configuration of this module.
        /// </summary>
        private readonly Action<ModuleViewModel> _onConfigureUpdatesRequested;

        /// <summary>
        /// Callback asking the dashboard to apply the available update of this module.
        /// </summary>
        private readonly Action<ModuleViewModel> _onUpdateRequested;

        /// <summary>
        /// Callback asking the dashboard to check this module for updates.
        /// </summary>
        private readonly Action<ModuleViewModel> _onCheckUpdatesRequested;

        /// <summary>
        /// Callback asking the dashboard to confirm and run the uninstall of this module.
        /// </summary>
        private readonly Action<ModuleViewModel> _onRemoveRequested;

        /// <summary>
        /// Callback asking the dashboard to confirm and run a reinstall; null makes the reinstall action do nothing.
        /// </summary>
        private readonly Action<ModuleViewModel>? _onReinstallRequested;

        /// <summary>
        /// Callback asking the dashboard to open this module's console; null makes the logs action do nothing.
        /// </summary>
        private readonly Action<ModuleViewModel>? _onLogsRequested;

        /// <summary>
        /// Command behind <see cref="LaunchCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _launchCommand;

        /// <summary>
        /// Command behind <see cref="StopCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _stopCommand;

        /// <summary>
        /// Command behind <see cref="RestartCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _restartCommand;

        /// <summary>
        /// Command behind <see cref="CheckUpdateCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _checkUpdateCommand;

        /// <summary>
        /// Command behind <see cref="CloseMenuCommand"/>.
        /// </summary>
        private readonly Command _closeMenuCommand;

        /// <summary>
        /// Command behind <see cref="RemoveCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _removeCommand;

        /// <summary>
        /// Command behind <see cref="ReinstallCommand"/>; kept to re-evaluate its availability.
        /// </summary>
        private readonly Command _reinstallCommand;

        /// <summary>
        /// Whether the dashboard reserved this module for removal before the installer reports its own removal state.
        /// </summary>
        private bool _isRemoving;

        /// <summary>
        /// Backing field of <see cref="IsRestarting"/>.
        /// </summary>
        private bool _isRestarting;

        /// <summary>
        /// Backing field of <see cref="IsStarting"/>.
        /// </summary>
        private bool _isStarting;

        /// <summary>
        /// Backing field of <see cref="IsMenuOpen"/>.
        /// </summary>
        private bool _isMenuOpen;

        /// <summary>
        /// Console activity seen by the last <see cref="RefreshMaintenanceState"/>, used to skip redundant redraws.
        /// </summary>
        private ModuleActivity? _lastMaintenanceActivity;

        /// <summary>
        /// Whether an update was pending at the last <see cref="RefreshMaintenanceState"/>.
        /// </summary>
        private bool _lastUpdatePending;

        /// <summary>
        /// Whether an update check was running at the last <see cref="RefreshMaintenanceState"/>.
        /// </summary>
        private bool _lastCheckingUpdates;


        // Notifications

        /// <summary>
        /// Raised when a bindable property of this card changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;


        // Trust level

        /// <summary>
        /// Trust level of the module, resolved once when the card is created.
        /// </summary>
        private readonly ModuleTrustLevel _trustLevel;


        // Initialization

        /// <summary>
        /// Creates the card view model for one module: resolves its trust level, normalizes its config and builds
        /// the commands, wiring each to the matching dashboard callback.
        /// </summary>
        /// <param name="config">Module config this card wraps.</param>
        /// <param name="installer">Installer used for installation state and config persistence.</param>
        /// <param name="runner">Runner used to stop the module and reach its consoles.</param>
        /// <param name="launchCoordinator">Coordinator that runs the launch sequence.</param>
        /// <param name="updateManager">Service that checks for updates and owns pending candidates.</param>
        /// <param name="moduleTrustService">Service that resolves the trust level of the module.</param>
        /// <param name="onStateChanged">Called after a launch, stop or restart changed the module state.</param>
        /// <param name="onMenuToggleRequested">Called to open or close the shared action menu.</param>
        /// <param name="onConfigureUpdatesRequested">Called to open the update configuration.</param>
        /// <param name="onUpdateRequested">Called to apply the available update.</param>
        /// <param name="onCheckUpdatesRequested">Called to check for updates.</param>
        /// <param name="onRemoveRequested">Called to confirm and run the uninstall.</param>
        /// <param name="onReinstallRequested">Called to confirm and run a reinstall; null disables the action.</param>
        /// <param name="onInfoRequested">Called to open the module info; null makes the action only close the menu.</param>
        /// <param name="onLogsRequested">Called to open the module console; null disables the action.</param>
        public ModuleViewModel(
            ModuleConfig config,
            ModuleInstaller installer,
            ModuleRunner runner,
            ModuleLaunchCoordinator launchCoordinator,
            UpdateManager updateManager,
            ModuleTrustService moduleTrustService,
            Action onStateChanged,
            Action<ModuleViewModel> onMenuToggleRequested,
            Action<ModuleViewModel> onConfigureUpdatesRequested,
            Action<ModuleViewModel> onUpdateRequested,
            Action<ModuleViewModel> onCheckUpdatesRequested,
            Action<ModuleViewModel> onRemoveRequested,
            Action<ModuleViewModel>? onReinstallRequested = null,
            Action<ModuleViewModel>? onInfoRequested = null,
            Action<ModuleViewModel>? onLogsRequested = null)
        {
            // Keep the services and the dashboard callbacks the card actions delegate to.
            _config = config;
            _installer = installer;
            _runner = runner;
            _launchCoordinator = launchCoordinator;
            _updateManager = updateManager;
            _trustLevel = moduleTrustService.Resolve(config);
            _onStateChanged = onStateChanged;
            _onMenuToggleRequested = onMenuToggleRequested;
            _onConfigureUpdatesRequested = onConfigureUpdatesRequested;
            _onUpdateRequested = onUpdateRequested;
            _onCheckUpdatesRequested = onCheckUpdatesRequested;
            _onRemoveRequested = onRemoveRequested;
            _onReinstallRequested = onReinstallRequested;
            _onLogsRequested = onLogsRequested;

            // Normalize once so every card starts from the persisted update preferences.
            _config.Normalize();

            // Create the commands. Lifecycle, update-check, remove and reinstall commands are gated by availability
            // rules that RefreshCommandStates re-evaluates; the other commands are always enabled and check their
            // conditions when they run.
            _launchCommand = new Command(OnLaunch, CanLaunch);
            _stopCommand = new Command(OnStop, CanStop);
            _restartCommand = new Command(OnRestart, CanRestart);
            ToggleMenuCommand = new Command(ExecuteToggleMenuCommand);
            _closeMenuCommand = new Command(ExecuteCloseMenuCommand);
            OpenConfigureUpdatesCommand = new Command(ExecuteOpenConfigureUpdatesCommand);
            OpenInfoCommand = new Command(() => { SetMenuOpen(false); onInfoRequested?.Invoke(this); });
            ViewLogsCommand = new Command(ExecuteViewLogsCommand);
            UpdateCommand = new Command(ExecuteUpdateCommand);
            _checkUpdateCommand = new Command(ExecuteCheckUpdateCommand, CanCheckOrUpdate);
            _removeCommand = new Command(OnRemove, () => CanUninstall && !IsRemoving && !IsBusy && !IsStarting && !IsRestarting);
            _reinstallCommand = new Command(OnReinstall, () => !IsRemoving && !IsBusy && !IsStarting && !IsRestarting &&
                string.IsNullOrEmpty(_installer.GetPendingConsumers(_config.Id)));

            // Expose the commands kept in fields through their read-only properties and fill in the localized labels.
            LaunchCommand = _launchCommand;
            StopCommand = _stopCommand;
            RestartCommand = _restartCommand;
            CheckUpdateCommand = _checkUpdateCommand;
            CloseMenuCommand = _closeMenuCommand;
            RefreshLocalizedLabels();
        }


        // Removal, reinstall and info actions

        /// <summary>
        /// Gets the command that starts the uninstall flow; available only for a removable, idle module.
        /// </summary>
        public ICommand RemoveCommand => _removeCommand;

        /// <summary>
        /// Gets the module config this card wraps, for the dashboard to hand to overlays and services.
        /// </summary>
        internal ModuleConfig Config => _config;

        /// <summary>
        /// Gets the command that closes the menu and opens the module info overlay.
        /// </summary>
        public ICommand OpenInfoCommand { get; }

        /// <summary>
        /// Gets the localized label of the info menu action.
        /// </summary>
        public string InfoLabel => L.Get(LocalizationKeys.ModuleInfo_InfoTab);

        /// <summary>
        /// Gets whether the module may be uninstalled: it is not a required module and no pending catalog
        /// installation still needs it.
        /// </summary>
        public bool CanUninstall => !ModuleRegistry.IsRequired(_config.Id) && string.IsNullOrEmpty(_installer.GetPendingConsumers(_config.Id));

        /// <summary>
        /// Gets the localized label of the remove menu action.
        /// </summary>
        public string RemoveLabel => L.Get(LocalizationKeys.Modules_Remove);

        /// <summary>
        /// Gets the command that starts the reinstall flow; unlike removal it also works for required modules.
        /// </summary>
        public ICommand ReinstallCommand => _reinstallCommand;

        /// <summary>
        /// Gets the localized label of the reinstall menu action.
        /// </summary>
        public string ReinstallLabel => L.Get(LocalizationKeys.Modules_Reinstall);

        /// <summary>
        /// Gets the localized status text for the removal pill: "queued" while the removal waits in the queue,
        /// otherwise "removing".
        /// </summary>
        public string RemovingLabel => L.Get(_installer.GetRemovalState(_config.Id) == DownloadOperationState.Queued
            ? LocalizationKeys.Downloads_Queued : LocalizationKeys.Modules_Removing);

        /// <summary>
        /// Gets whether the module is being removed: reserved by the dashboard, queued or running in the installer,
        /// or in the removing phase of an installation operation.
        /// </summary>
        public bool IsRemoving => _isRemoving || _installer.GetRemovalState(_config.Id) != null ||
            _installer.GetInstallationState(_config.Id)?.Phase == DownloadOperationState.Removing;

        /// <summary>
        /// Gets whether the module is not being removed; the inverse of <see cref="IsRemoving"/> for bindings.
        /// </summary>
        public bool IsNotRemoving => !IsRemoving;

        /// <summary>
        /// Updates the removal reservation and all card actions affected by it.
        /// </summary>
        internal void SetRemoving(bool removing)
        {
            // Apply only real changes; starting a removal also closes the card menu.
            if (_isRemoving == removing) return;
            _isRemoving = removing;
            if (removing) SetMenuOpen(false);

            // Everything derived from the removing state must redraw and re-evaluate its availability.
            OnPropertyChanged(nameof(IsRemoving));
            OnPropertyChanged(nameof(IsNotRemoving));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            OnPropertyChanged(nameof(ShowCardUpdateAction));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            OnPropertyChanged(nameof(ShowStartingStatus));
            RefreshCommandStates();
        }

        /// <summary>
        /// Queues removal through the shared module lifecycle. Returns the directory that still holds files which
        /// could not be deleted, or null when the cleanup was complete.
        /// </summary>
        internal Task<string?> UninstallAsync() => _updateManager.UninstallModuleAsync(_config);

        /// <summary>
        /// Reads persisted preferences rather than potentially stale selections cached by the card.
        /// Throws when the module config can no longer be loaded from its source path.
        /// </summary>
        internal async Task<ModuleConfig> LoadReinstallConfigAsync() =>
            await _installer.LoadModuleConfig(_config.SourcePath)
                ?? throw new InvalidOperationException(L.Get(LocalizationKeys.ModuleInfo_NotAvailable, Name));

        /// <summary>
        /// Runs reinstallation through the shared queues and keeps failures visible in the module console.
        /// </summary>
        /// <param name="selection">Update preferences chosen in the reinstall form.</param>
        internal async Task<ModuleLaunchResult> ReinstallAsync(ModuleUpdateConfig selection)
        {
            var log = _runner.ConsoleStore.CreateMaintenanceLog(_config, null, reset: true);
            try
            {
                return await _updateManager.ReinstallModuleAsync(_config, selection, log);
            }
            catch (Exception ex)
            {
                // Record the outcome in the module console and let the caller handle the exception.
                log.Report(ex is OperationCanceledException ? L.Get(LocalizationKeys.ModuleInfo_Canceled)
                    : L.Get(LocalizationKeys.ModuleInfo_Failed, ex.Message));
                throw;
            }
        }


        // Card labels

        /// <summary>
        /// Gets the localized not-verified badge text.
        /// </summary>
        public string NotVerifiedLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized update action label.
        /// </summary>
        public string UpdateLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized stop action label.
        /// </summary>
        public string StopLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized restart action label.
        /// </summary>
        public string RestartLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized restarting status label.
        /// </summary>
        public string RestartingLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized starting status label.
        /// </summary>
        public string StartingLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized updating status label.
        /// </summary>
        public string UpdatingLabel { get; private set; } = string.Empty;


        // Installation state

        /// <summary>
        /// Gets whether the installer is installing this module right now.
        /// </summary>
        public bool IsInstalling => _installer.IsInstalling(_config.Id);

        /// <summary>
        /// Gets the text of the maintenance status pill: the label of the running installation operation,
        /// or the generic updating label when there is none.
        /// </summary>
        public string MaintenanceLabel => _installer.GetInstallationState(_config.Id)?.Label ?? UpdatingLabel;


        // Launch and menu labels

        /// <summary>
        /// Gets the localized launch action label.
        /// </summary>
        public string LaunchLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized check-updates menu label.
        /// </summary>
        public string CheckUpdatesLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized configure-updates menu label.
        /// </summary>
        public string ConfigureUpdatesLabel { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized action label for opening the module's console output.
        /// </summary>
        public string ViewLogsLabel => L.Get(LocalizationKeys.Modules_ViewLogs);

        /// <summary>
        /// Reloads the localized strings stored on this card and raises change notifications for every label
        /// shown on the card template, including the labels that are computed on each read.
        /// </summary>
        internal void RefreshLocalizedLabels()
        {
            // Reload the labels that are cached in properties.
            NotVerifiedLabel = L.Get(LocalizationKeys.Modules_NotVerified);
            UpdateLabel = L.Get(LocalizationKeys.Modules_Update);
            StopLabel = L.Get(LocalizationKeys.Modules_Stop);
            RestartLabel = L.Get(LocalizationKeys.Modules_Restart);
            RestartingLabel = L.Get(LocalizationKeys.Modules_Restarting);
            StartingLabel = L.Get(LocalizationKeys.Modules_Starting);
            UpdatingLabel = L.Get(LocalizationKeys.Modules_Updating);
            LaunchLabel = L.Get(LocalizationKeys.Modules_Launch);
            CheckUpdatesLabel = L.Get(LocalizationKeys.Modules_CheckUpdates);
            ConfigureUpdatesLabel = L.Get(LocalizationKeys.Modules_ConfigureUpdates);

            // Let the bindings re-read every label, cached or computed.
            OnPropertyChanged(nameof(NotVerifiedLabel));
            OnPropertyChanged(nameof(BetaLabel));
            OnPropertyChanged(nameof(ExperimentalLabel));
            OnPropertyChanged(nameof(UpdateLabel));
            OnPropertyChanged(nameof(StopLabel));
            OnPropertyChanged(nameof(RestartLabel));
            OnPropertyChanged(nameof(RestartingLabel));
            OnPropertyChanged(nameof(StartingLabel));
            OnPropertyChanged(nameof(UpdatingLabel));
            OnPropertyChanged(nameof(MaintenanceLabel));
            OnPropertyChanged(nameof(LaunchLabel));
            OnPropertyChanged(nameof(CheckUpdatesLabel));
            OnPropertyChanged(nameof(ConfigureUpdatesLabel));
            OnPropertyChanged(nameof(RemoveLabel));
            OnPropertyChanged(nameof(ReinstallLabel));
            OnPropertyChanged(nameof(InfoLabel));
            OnPropertyChanged(nameof(ViewLogsLabel));
            OnPropertyChanged(nameof(RemovingLabel));
        }


        // Card properties

        /// <summary>
        /// Gets the module name shown on the card.
        /// </summary>
        public string Name => _config.Name;

        /// <summary>
        /// Gets the resolved trust level for the module card.
        /// </summary>
        public ModuleTrustLevel TrustLevel => _trustLevel;

        /// <summary>
        /// Gets whether the official verified badge should be shown.
        /// </summary>
        public bool ShowVerifiedBadge => _trustLevel == ModuleTrustLevel.Official;

        /// <summary>
        /// Gets whether the unverified warning badge should be shown.
        /// </summary>
        public bool ShowUnverifiedWarning => _trustLevel == ModuleTrustLevel.Unreviewed;

        /// <summary>
        /// Gets the short module description shown on the card.
        /// </summary>
        public string Description => _config.Description;

        /// <summary>
        /// Gets the stable source path of the module manifest.
        /// </summary>
        public string SourcePath => _config.SourcePath;

        /// <summary>
        /// Gets the formatted version label shown on the card.
        /// </summary>
        public string VersionString => $"v{_config.Version}";

        /// <summary>
        /// Gets whether the module is marked as beta in its manifest.
        /// </summary>
        public bool IsBeta => _config.IsBeta;

        /// <summary>
        /// Gets whether the module is marked as experimental in its manifest.
        /// </summary>
        public bool IsExperimental => _config.IsExperimental;

        /// <summary>
        /// Gets whether the card shows a development-status badge (beta or experimental).
        /// </summary>
        public bool HasDevelopmentStatus => IsBeta || IsExperimental;

        /// <summary>
        /// Gets the localized text of the beta badge.
        /// </summary>
        public string BetaLabel => L.Get(LocalizationKeys.Modules_Beta);

        /// <summary>
        /// Gets the localized text of the experimental badge.
        /// </summary>
        public string ExperimentalLabel => L.Get(LocalizationKeys.Modules_Experimental);

        /// <summary>
        /// Gets the resolved module icon path.
        /// </summary>
        public string? IconFullPath => _config.IconFullPath;

        /// <summary>
        /// Gets whether the module has an icon to render.
        /// </summary>
        public bool HasIcon => !string.IsNullOrEmpty(_config.IconFullPath);

        /// <summary>
        /// Gets whether the module is currently enabled, which is the persisted running flag of the module.
        /// </summary>
        public bool IsRunning => _config.Status.Enabled;

        /// <summary>
        /// Gets whether the module is currently stopped.
        /// </summary>
        public bool IsStopped => !_config.Status.Enabled;

        /// <summary>
        /// Gets or sets whether the module is currently restarting; changing it refreshes the dependent actions.
        /// </summary>
        public bool IsRestarting
        {
            get => _isRestarting;
            set
            {
                if (_isRestarting == value)
                {
                    return;
                }

                _isRestarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotRestarting));
                OnPropertyChanged(nameof(ShowRunningActions));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether the module is not restarting; the inverse of <see cref="IsRestarting"/> for bindings.
        /// </summary>
        public bool IsNotRestarting => !IsRestarting;


        // Maintenance state

        /// <summary>
        /// Gets whether an update is available for the module. Uses the persisted candidate, so rebuilding a card
        /// does not lose its update badge.
        /// </summary>
        public bool HasUpdate => _updateManager.TryRestorePendingUpdateCandidate(_config, out _);

        /// <summary>
        /// Gets whether the module is being updated. Reads the lightweight activity state without formatting
        /// console output.
        /// </summary>
        public bool IsUpdating => _runner.ConsoleStore.GetActivity(SourcePath) == ModuleActivity.Updating;

        /// <summary>
        /// Gets whether an update check is running; shows the header spinner, including for checks initiated in
        /// ModuleInfo.
        /// </summary>
        public bool IsCheckingUpdates => _updateManager.IsCheckingModuleUpdate(SourcePath);

        /// <summary>
        /// Gets whether an installation or update is in progress or pending, which blocks conflicting card actions.
        /// </summary>
        public bool IsBusy => IsUpdating || IsInstalling || _updateManager.IsModuleUpdatePending(SourcePath);

        /// <summary>
        /// Raises change notifications for the state bindings when background maintenance changes the console
        /// activity, the pending-update flag or the update-check flag. Does nothing when none of the three differs
        /// from the previous call.
        /// </summary>
        internal void RefreshMaintenanceState()
        {
            // Console output is frequent; only an actual phase change needs to redraw card controls.
            var activity = _runner.ConsoleStore.GetActivity(SourcePath);
            var updatePending = _updateManager.IsModuleUpdatePending(SourcePath);
            var checking = IsCheckingUpdates;
            if (_lastMaintenanceActivity == activity && _lastUpdatePending == updatePending &&
                _lastCheckingUpdates == checking)
            {
                return;
            }

            // Remember the new state, then redraw everything derived from it.
            _lastMaintenanceActivity = activity;
            _lastUpdatePending = updatePending;
            _lastCheckingUpdates = checking;
            OnPropertyChanged(nameof(IsCheckingUpdates));
            OnPropertyChanged(nameof(IsUpdating));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(ShowCardUpdateAction));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            RefreshCommandStates();
        }

        /// <summary>
        /// Gets whether the compact update action should stay visible in the card header.
        /// </summary>
        public bool ShowCardUpdateAction => HasUpdate && !IsBusy && !IsRemoving;

        /// <summary>
        /// Gets whether the module is currently starting; set only by the launch flow.
        /// </summary>
        public bool IsStarting
        {
            get => _isStarting;
            private set
            {
                if (_isStarting == value)
                {
                    return;
                }

                _isStarting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanShowLaunchAction));
                OnPropertyChanged(nameof(ShowStartingStatus));
                OnPropertyChanged(nameof(ShowRunningActions));
                RefreshCommandStates();
            }
        }

        /// <summary>
        /// Gets whether the launch button should be visible: the module is stopped and nothing else is changing it.
        /// </summary>
        public bool CanShowLaunchAction => IsStopped && !IsUpdating && !IsStarting && !IsRemoving && !IsInstalling;

        /// <summary>
        /// Gets whether the stop and restart buttons should be visible: the module is running and nothing else is
        /// changing it.
        /// </summary>
        public bool ShowRunningActions => IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsRemoving && !IsInstalling;

        /// <summary>
        /// Gets whether the maintenance status pill is visible; one pill covers both installation and updating.
        /// </summary>
        public bool ShowMaintenanceStatus => (IsUpdating || IsInstalling) && !IsRemoving;

        /// <summary>
        /// Gets whether the starting status pill should be visible.
        /// </summary>
        public bool ShowStartingStatus => IsStarting && !IsRemoving && !IsInstalling;


        // Card menu

        /// <summary>
        /// Gets whether the mini actions menu is open for this card; changed through <see cref="SetMenuOpen"/>.
        /// </summary>
        public bool IsMenuOpen
        {
            get => _isMenuOpen;
            private set
            {
                if (_isMenuOpen == value)
                {
                    return;
                }

                _isMenuOpen = value;
                OnPropertyChanged();
            }
        }


        // Commands

        /// <summary>
        /// Gets the command that launches the module.
        /// </summary>
        public ICommand LaunchCommand { get; }

        /// <summary>
        /// Gets the command that stops the module.
        /// </summary>
        public ICommand StopCommand { get; }

        /// <summary>
        /// Gets the command that restarts the module.
        /// </summary>
        public ICommand RestartCommand { get; }

        /// <summary>
        /// Gets the command that toggles the mini actions menu.
        /// </summary>
        public ICommand ToggleMenuCommand { get; }

        /// <summary>
        /// Gets the command that closes the mini actions menu.
        /// </summary>
        public ICommand CloseMenuCommand { get; }

        /// <summary>
        /// Gets the command that checks for module updates.
        /// </summary>
        public ICommand CheckUpdateCommand { get; }

        /// <summary>
        /// Gets the command that opens update configuration for the module.
        /// </summary>
        public ICommand OpenConfigureUpdatesCommand { get; }

        /// <summary>
        /// Gets the command that opens the merged console output for this module.
        /// </summary>
        public ICommand ViewLogsCommand { get; }

        /// <summary>
        /// Gets the command that starts an available update without opening a view.
        /// </summary>
        public ICommand UpdateCommand { get; }


        // Menu helpers

        /// <summary>
        /// Opens or closes the card mini menu through the parent dashboard; ignored while the module is being
        /// removed or installed.
        /// </summary>
        private void ExecuteToggleMenuCommand()
        {
            if (IsRemoving || IsInstalling) return;
            _onMenuToggleRequested(this);
        }

        /// <summary>
        /// Closes the mini menu without running an action.
        /// </summary>
        private void ExecuteCloseMenuCommand()
        {
            SetMenuOpen(false);
        }

        /// <summary>
        /// Closes the menu and asks the dashboard to open the update configuration; ignored while the module is
        /// being removed or installed.
        /// </summary>
        private void ExecuteOpenConfigureUpdatesCommand()
        {
            if (IsRemoving || IsInstalling) return;
            SetMenuOpen(false);
            _onConfigureUpdatesRequested(this);
        }

        /// <summary>
        /// Closes the card menu and asks the dashboard to open the module's console.
        /// </summary>
        private void ExecuteViewLogsCommand()
        {
            // Console navigation is read-only and does not participate in module lifecycle operations.
            SetMenuOpen(false);
            _onLogsRequested?.Invoke(this);
        }

        /// <summary>
        /// Closes the menu and asks the dashboard to apply the update, when the card has an available candidate
        /// and no other operation blocks it.
        /// </summary>
        private void ExecuteUpdateCommand()
        {
            if (!HasUpdate || !CanCheckOrUpdate())
            {
                return;
            }

            SetMenuOpen(false);
            _onUpdateRequested(this);
        }

        /// <summary>
        /// Sets the visible state of the mini menu on this card.
        /// </summary>
        internal void SetMenuOpen(bool isOpen)
        {
            IsMenuOpen = isOpen;
        }


        // Command availability and update check

        /// <summary>
        /// Returns whether update-related commands may run: the module is neither being removed nor busy.
        /// </summary>
        private bool CanCheckOrUpdate()
        {
            return !IsRemoving && !IsBusy;
        }

        /// <summary>
        /// Returns whether the launch command may run: the module is stopped and no start, update, installation
        /// or removal is in progress.
        /// </summary>
        private bool CanLaunch()
        {
            return !IsRemoving && IsStopped && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Returns whether the stop command may run: the module is running and no restart, start, update,
        /// installation or removal is in progress.
        /// </summary>
        private bool CanStop()
        {
            return !IsRemoving && IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Returns whether the restart command may run; the conditions match those of the stop command.
        /// </summary>
        private bool CanRestart()
        {
            return !IsRemoving && IsRunning && !IsRestarting && !IsStarting && !IsUpdating && !IsInstalling;
        }

        /// <summary>
        /// Re-evaluates the availability of every gated command after one of the state flags changes.
        /// </summary>
        private void RefreshCommandStates()
        {
            _launchCommand.ChangeCanExecute();
            _stopCommand.ChangeCanExecute();
            _restartCommand.ChangeCanExecute();
            _checkUpdateCommand.ChangeCanExecute();
            _removeCommand.ChangeCanExecute();
            _reinstallCommand.ChangeCanExecute();
        }

        /// <summary>
        /// Closes the menu and asks the dashboard to check for updates, without applying a candidate or opening
        /// ModuleInfo.
        /// </summary>
        private void ExecuteCheckUpdateCommand()
        {
            if (!CanCheckOrUpdate())
            {
                return;
            }

            SetMenuOpen(false);
            _onCheckUpdatesRequested(this);
        }


        // Remove and reinstall flow

        /// <summary>
        /// Closes the menu and asks the dashboard to confirm the uninstall of a removable, idle module.
        /// </summary>
        private void OnRemove()
        {
            if (!_removeCommand.CanExecute(null)) return;
            SetMenuOpen(false);
            _onRemoveRequested(this);
        }

        /// <summary>
        /// Closes the menu and asks the dashboard to confirm a reinstall, without applying the uninstall
        /// restriction of mandatory modules.
        /// </summary>
        private void OnReinstall()
        {
            if (!_reinstallCommand.CanExecute(null)) return;
            SetMenuOpen(false);
            _onReinstallRequested?.Invoke(this);
        }


        // Launch flow

        /// <summary>
        /// Loads the latest config, completes first-run setup if needed, and starts the module.
        /// While it runs the card shows the starting state, which is always cleared afterwards.
        /// </summary>
        private async void OnLaunch()
        {
            if (!CanLaunch())
            {
                return;
            }

            IsStarting = true;

            try
            {
                // Use the settings and commands saved in the overlay since the card was built.
                await ReloadEditableConfigAsync();

                // The coordinator performs the shared launch sequence; its log goes to the debug output only.
                var launchLog = new Progress<string>(message => Debug.WriteLine($"[Launch] {message}"));
                var result = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    _config.SourcePath,
                    launchLog,
                    CancellationToken.None);

                // Adopt the state the coordinator produced, but only when it belongs to this card's module.
                if (result.EffectiveConfig != null &&
                    string.Equals(result.EffectiveConfig.SourcePath, _config.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _config.Settings = result.EffectiveConfig.Settings;
                    _config.Commands = result.EffectiveConfig.Commands;
                    _config.Status.FirstRunCompleted = result.EffectiveConfig.Status.FirstRunCompleted;
                    _config.Status.Enabled = result.EffectiveConfig.Status.Enabled;
                }

                // Redraw the card and tell the dashboard only when the module is running; failures are only logged.
                if (result.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning)
                {
                    NotifyStateChanged();
                    _onStateChanged?.Invoke();
                }
                else
                {
                    Debug.WriteLine($"Launch failed ({result.Status}): {result.Message}");
                }
            }
            catch (Exception ex)
            {
                // UI event handlers must contain unexpected failures so WinUI can keep processing input.
                Debug.WriteLine($"Launch failed with an unexpected error: {ex}");
            }
            finally
            {
                IsStarting = false;
            }
        }


        // Stop flow

        /// <summary>
        /// Stops the module and persists the disabled state. The module is registered as in use meanwhile,
        /// so a removal cannot run while its files are being saved.
        /// </summary>
        private async void OnStop()
        {
            if (!CanStop())
            {
                return;
            }

            try
            {
                // Register the module as in use, stop it, save the disabled flag, then refresh the card and the dashboard.
                using var operation = await ModuleInstaller.BeginContentOperationAsync(CancellationToken.None, _config);
                await Task.Run(() => _runner.StopModuleAsync(_config.SourcePath));
                _config.Status.Enabled = false;
                await Task.Run(() => _installer.SaveConfigAsync(_config));
                NotifyStateChanged();
                _onStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                // Show the failure in the module's console overview; an async void handler must not throw.
                _runner.ConsoleStore.AppendOverviewLine(_config, ex.Message);
                Debug.WriteLine($"Stop failed: {ex}");
            }
        }


        // Restart flow

        /// <summary>
        /// Stops and starts the module again while showing the restart state. The restart is also recorded as a
        /// console activity, and both it and the restart flag are cleared when the flow ends.
        /// </summary>
        private async void OnRestart()
        {
            if (!CanRestart())
            {
                return;
            }

            IsRestarting = true;
            using var activity = _runner.ConsoleStore.BeginActivity(_config.SourcePath, ModuleActivity.Restarting);

            try
            {
                // Register the module as in use, reload its latest settings and stop it, then wait one second
                // before starting it again.
                using var operation = await ModuleInstaller.BeginContentOperationAsync(CancellationToken.None, _config);
                await ReloadEditableConfigAsync();
                await Task.Run(() => _runner.StopModuleAsync(_config.SourcePath));
                await Task.Delay(1000);

                // Start it again through the shared launch sequence; its log goes to the debug output only.
                var restartLog = new Progress<string>(message => Debug.WriteLine($"[Restart] {message}"));
                var restartResult = await _launchCoordinator.LaunchOrEnsureRunningBySourcePathAsync(
                    _config.SourcePath,
                    restartLog,
                    CancellationToken.None);

                // Adopt the enabled flag the coordinator produced, but only for this card's module.
                if (restartResult.EffectiveConfig != null &&
                    string.Equals(restartResult.EffectiveConfig.SourcePath, _config.SourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _config.Status.Enabled = restartResult.EffectiveConfig.Status.Enabled;
                }

                // Redraw the card and tell the dashboard only when the module is running again.
                if (restartResult.Status is ModuleLaunchStatus.Started or ModuleLaunchStatus.AlreadyRunning)
                {
                    NotifyStateChanged();
                    _onStateChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                // Show the failure in the module's console overview; an async void handler must not throw.
                _runner.ConsoleStore.AppendOverviewLine(_config, ex.Message);
                Debug.WriteLine($"Restart failed: {ex}");
            }
            finally
            {
                IsRestarting = false;
            }
        }


        // Config refresh

        /// <summary>
        /// Reloads module settings and commands from disk before launch or restart.
        /// </summary>
        private async Task ReloadEditableConfigAsync()
        {
            // Launch and restart should respect the latest settings saved in the overlay,
            // so the card refreshes the mutable sections from disk just before execution.
            var freshConfig = await _installer.LoadModuleConfig(_config.SourcePath);
            if (freshConfig == null)
            {
                return;
            }

            _config.Settings = freshConfig.Settings;
            _config.Commands = freshConfig.Commands;
        }


        // UI state refresh

        /// <summary>
        /// Raises property changes for the running state flags and re-evaluates the command availability.
        /// </summary>
        private void NotifyStateChanged()
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsStopped));
            OnPropertyChanged(nameof(CanShowLaunchAction));
            OnPropertyChanged(nameof(ShowRunningActions));
            OnPropertyChanged(nameof(ShowMaintenanceStatus));
            RefreshCommandStates();
        }


        // Property change

        /// <summary>
        /// Raises the view-model property changed event for the calling member or the given property name.
        /// </summary>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
