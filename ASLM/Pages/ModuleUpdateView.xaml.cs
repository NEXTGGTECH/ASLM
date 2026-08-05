// Copyright NEXTGGTECH. Apache License 2.0.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ASLM.Localization;
using ASLM.Models;
using Microsoft.Maui.Controls;

namespace ASLM.Pages
{
    /// <summary>
    /// Distinguishes the supported module update dialog modes.
    /// </summary>
    public enum ModuleUpdateMode
    {
        Configure,
        Update
    }

    /// <summary>
    /// Displays module update configuration and installation progress inside the shell overlay.
    /// </summary>
    public partial class ModuleUpdateView : ContentView, INotifyPropertyChanged, ILocalizable
    {
        private const double DialogWidthFactor = 0.78;
        private const double DialogHeightFactor = 0.82;
        private const double MinDialogWidth = 860;
        private const double MinDialogHeight = 560;
        private const double MaxDialogWidth = 1200;
        private const double MaxDialogHeight = 820;

        private readonly ObservableCollection<string> _emptyBranches = [];
        private readonly ObservableCollection<UpdateCandidate> _emptyReleases = [];

        private ModuleViewModel? _module;
        private ModuleUpdateMode _mode;
        private bool _isSynchronizingPickers;
        private CancellationTokenSource? _logSyncCts;
        private int _logSyncQueued;
        private int _logSyncRequested;
        private int _updateLogLayoutRefreshQueued;
        private string _updateLogSessionKey = string.Empty;


        // Events

        /// <summary>
        /// Raised when the user asks to close the module update overlay.
        /// </summary>
        public event EventHandler? CloseRequested;

        /// <inheritdoc />
        public new event PropertyChangedEventHandler? PropertyChanged;


        // Initialization

        private readonly AppLocalizationService _localization;

        /// <summary>
        /// Creates the module update overlay and hooks layout updates.
        /// </summary>
        public ModuleUpdateView(AppLocalizationService localization)
        {
            _localization = localization;
            InitializeComponent();
            LocalizableAttach.Hook(this, _localization, this);
            ApplyBorderlessPickerStyle(SourceModePicker);
            ApplyBorderlessPickerStyle(ReleasePicker);
            ApplyBorderlessPickerStyle(BranchPicker);
            BindingContext = this;
            SizeChanged += OnViewSizeChanged;
        }


        // Dialog state

        /// <summary>
        /// Gets the title shown in the dialog header.
        /// </summary>
        public string DialogTitle => _mode == ModuleUpdateMode.Update
            ? L.Get(LocalizationKeys.ModuleUpdate_Title_Update)
            : L.Get(LocalizationKeys.ModuleUpdate_Title_Configure);

        /// <summary>
        /// Gets the subtitle shown in the dialog header.
        /// </summary>
        public string DialogSubtitle => _module == null
            ? L.Get(LocalizationKeys.ModuleUpdate_NoModuleSelected)
            : L.Get(LocalizationKeys.ModuleUpdate_SubtitleFormat, _module.Name, _module.Description);

        /// <inheritdoc />
        public void ApplyLocalization()
        {
            ToolTipProperties.SetText(CloseButton, L.Get(LocalizationKeys.ModuleUpdate_CloseTooltip));
            CurrentVersionHeaderLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_CurrentVersion);
            SelectedTargetHeaderLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_SelectedTarget);
            UpdateSourceHeaderLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_UpdateSource);
            PrefsAutoSavedLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_PrefsAutoSaved);
            SearchModeLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_SearchMode);
            ReleaseVersionLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_ReleaseVersion);
            RepositoryBranchLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_RepositoryBranch);
            OverallProgressLabel.Text = L.Get(LocalizationKeys.ModuleUpdate_OverallProgress);
            CheckUpdatesButton.Text = L.Get(LocalizationKeys.ModuleUpdate_CheckUpdates);
            InstallUpdateButton.Text = L.Get(LocalizationKeys.ModuleUpdate_InstallUpdate);
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(DialogSubtitle));
            OnPropertyChanged(nameof(ActivityTitle));
            OnPropertyChanged(nameof(ActivityStatus));
        }

        /// <summary>
        /// Gets the currently installed module version.
        /// </summary>
        public string CurrentVersionLabel => _module?.VersionString ?? "-";

        /// <summary>
        /// Gets the selected target label shown in the header.
        /// </summary>
        public string TargetVersionLabel => _module?.SelectedTargetLabel ?? L.Get(LocalizationKeys.ModuleUpdate_NoVersionSelected);

        /// <summary>
        /// Gets selectable module update source modes.
        /// </summary>
        public IReadOnlyList<string> SourceModeOptions => _module?.SourceModeOptions ?? Array.Empty<string>();

        /// <summary>
        /// Gets selectable release versions for release and pre-release modes.
        /// </summary>
        public ObservableCollection<UpdateCandidate> ReleaseOptions => _module?.ReleaseOptions ?? _emptyReleases;

        /// <summary>
        /// Gets selectable repository branches for branch-based updates.
        /// </summary>
        public ObservableCollection<string> BranchOptions => _module?.BranchOptions ?? _emptyBranches;

        /// <summary>
        /// Gets or sets the active update source mode.
        /// </summary>
        public string SelectedSourceMode
        {
            get => _module?.SelectedSourceMode ?? "release";
            set
            {
                if (_module == null)
                {
                    return;
                }

                _module.SelectedSourceMode = value;
                RaiseModuleProperties();
                _ = EnsureModeOptionsLoadedAsync(forceRefresh: false);
            }
        }

        /// <summary>
        /// Gets or sets the active release target.
        /// </summary>
        public UpdateCandidate? SelectedReleaseOption
        {
            get => _module?.SelectedReleaseOption;
            set
            {
                if (_module == null)
                {
                    return;
                }

                _module.SelectedReleaseOption = value;
                RaiseModuleProperties();
            }
        }

        /// <summary>
        /// Gets or sets the active repository branch.
        /// </summary>
        public string SelectedBranch
        {
            get => _module?.SelectedBranch ?? "main";
            set
            {
                if (_module == null)
                {
                    return;
                }

                _module.ApplyBranchSelection(value);
                RaiseModuleProperties();
            }
        }

        /// <summary>
        /// Gets whether branch-related controls should be visible.
        /// </summary>
        public bool IsBranchMode => _module?.IsBranchMode ?? false;

        /// <summary>
        /// Gets whether release-related controls should be visible.
        /// </summary>
        public bool IsReleaseMode => _module?.IsReleaseMode ?? true;

        /// <summary>
        /// Gets whether the current module has a pending update candidate.
        /// </summary>
        public bool HasUpdate => _module?.HasUpdate ?? false;

        /// <summary>
        /// Gets whether the install action is currently available.
        /// </summary>
        public bool CanInstallUpdate => _module?.CanInstallSelectedUpdate ?? false;

        /// <summary>
        /// Gets whether the check action is currently available.
        /// </summary>
        public bool CanCheckUpdates => _module?.CanCheckUpdates ?? false;

        /// <summary>
        /// Gets whether the install button should stay visible.
        /// </summary>
        public bool ShowInstallAction => _module?.ShowInstallAction ?? false;

        /// <summary>
        /// Gets whether the overlay is currently running an update-related operation.
        /// </summary>
        public bool IsBusy => _module?.IsBusy ?? false;

        /// <summary>
        /// Gets the title shown above the activity panel.
        /// </summary>
        public string ActivityTitle => _mode == ModuleUpdateMode.Update
            ? L.Get(LocalizationKeys.ModuleUpdate_ActivityTitle_Activity)
            : L.Get(LocalizationKeys.ModuleUpdate_ActivityTitle_Status);

        /// <summary>
        /// Gets the current activity status text shown in the progress area.
        /// </summary>
        public string ActivityStatus => _module?.UpdateActivityStatus ?? _module?.UpdateStatus ?? L.Get(LocalizationKeys.ModuleUpdate_ReadyDefault);

        /// <summary>
        /// Gets whether the progress area should show the bars.
        /// </summary>
        public bool HasActivityProgress => _module?.HasUpdateProgress ?? IsBusy;

        /// <summary>
        /// Gets the overall update progress fraction.
        /// </summary>
        public double OverallProgress => _module?.UpdateOverallProgress ?? 0;

        /// <summary>
        /// Gets the download progress fraction for the current file transfer.
        /// </summary>
        public double FileProgress => _module?.UpdateFileProgress ?? 0;

        /// <summary>
        /// Gets the formatted per-file transfer detail text.
        /// </summary>
        public string DownloadDetail => _module?.UpdateDownloadDetail ?? string.Empty;

        /// <summary>
        /// Gets whether the download detail label should be visible.
        /// </summary>
        public bool HasDownloadDetail => _module?.HasUpdateDownloadDetail ?? false;

        /// <summary>
        /// Gets the console-like activity log text.
        /// </summary>
        public string LogText => _module?.UpdateLogText ?? string.Empty;

        /// <summary>
        /// Gets whether the activity log contains messages.
        /// </summary>
        public bool HasLog => _module?.HasUpdateLog ?? false;

        /// <summary>
        /// Gets the session key passed to the native log host so changing the module resets scroll position.
        /// </summary>
        public string UpdateLogSessionKey => _updateLogSessionKey;


        // Overlay opening

        /// <summary>
        /// Opens the overlay for the requested module and mode.
        /// </summary>
        public Task OpenAsync(ModuleViewModel module, ModuleUpdateMode mode)
        {
            AttachModule(module);
            var selectedModule = _module;
            if (selectedModule == null)
            {
                return Task.CompletedTask;
            }

            _mode = mode;
            selectedModule.ResetCompletedUpdateSession();
            BeginUpdateLogSession();
            UpdateDialogSize();
            RaiseDialogProperties();
            SyncPickerSelections();
            SyncLogView();
            _ = EnsureModeOptionsLoadedAsync(forceRefresh: false);
            return Task.CompletedTask;
        }


        // Overlay events

        /// <summary>
        /// Closes the overlay when the dimmed background is tapped.
        /// </summary>
        private void OnBackgroundTapped(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Swallows taps inside the dialog so they do not close the overlay.
        /// </summary>
        private void OnDialogTapped(object? sender, EventArgs e)
        {
            // Intentionally left blank so dialog taps do not bubble to the overlay background.
        }

        /// <summary>
        /// Closes the overlay from the close button.
        /// </summary>
        private void OnCloseClicked(object? sender, EventArgs e)
        {
            RequestClose();
        }

        /// <summary>
        /// Raises the close event for the host shell.
        /// </summary>
        private void RequestClose()
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            _logSyncCts?.Cancel();
            _logSyncCts = null;
            Interlocked.Exchange(ref _logSyncQueued, 0);
            Interlocked.Exchange(ref _logSyncRequested, 0);
            DetachModule();
        }


        // Layout

        /// <summary>
        /// Recalculates the dialog size when the overlay control changes size.
        /// </summary>
        private void OnViewSizeChanged(object? sender, EventArgs e)
        {
            UpdateDialogSize();
        }

        /// <summary>
        /// Applies the responsive width and height used by the overlay dialog.
        /// </summary>
        private void UpdateDialogSize()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            DialogBorder.WidthRequest = ClampDialogSize(
                Math.Floor(Width * DialogWidthFactor),
                MinDialogWidth,
                MaxDialogWidth);
            DialogBorder.HeightRequest = ClampDialogSize(
                Math.Floor(Height * DialogHeightFactor),
                MinDialogHeight,
                MaxDialogHeight);
        }

        /// <summary>
        /// Clamps one dialog dimension into the supported bounds.
        /// </summary>
        private static double ClampDialogSize(double value, double minValue, double maxValue)
        {
            return Math.Max(minValue, Math.Min(maxValue, value));
        }


        // Module attachment

        /// <summary>
        /// Subscribes the dialog to the selected module view model.
        /// </summary>
        private void AttachModule(ModuleViewModel module)
        {
            if (ReferenceEquals(_module, module))
            {
                return;
            }

            DetachModule();
            _module = module;
            _module.PropertyChanged += OnModulePropertyChanged;
        }

        /// <summary>
        /// Unsubscribes from the previously attached module view model.
        /// </summary>
        private void DetachModule()
        {
            if (_module == null)
            {
                return;
            }

            _module.PropertyChanged -= OnModulePropertyChanged;
            _module = null;
        }

        /// <summary>
        /// Refreshes dialog bindings after the module card state changes.
        /// </summary>
        private void OnModulePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            var propertyName = e.PropertyName;

            if (propertyName == nameof(ModuleViewModel.UpdateLogText) ||
                propertyName == nameof(ModuleViewModel.HasUpdateLog))
            {
                QueueLogViewSync();
                return;
            }

            if (IsActivityOnlyProperty(propertyName))
            {
                RaiseActivityProperties();
                return;
            }

            RaiseModuleProperties();

            if (ShouldSyncPickerSelection(propertyName))
            {
                SyncPickerSelections();
            }

            if (propertyName == nameof(ModuleViewModel.BranchOptions))
            {
                QueueBranchPickerResyncAfterListChange();
            }

            if (propertyName == nameof(ModuleViewModel.IsBranchMode) && IsBranchMode)
            {
                QueueBranchPickerResyncAfterListChange();
                _ = ResyncBranchPickerAfterBranchUiShownAsync();
            }
        }

        /// <summary>
        /// Returns whether the changed module property only affects the activity section.
        /// </summary>
        private static bool IsActivityOnlyProperty(string? propertyName)
        {
            return propertyName == nameof(ModuleViewModel.UpdateActivityStatus) ||
                   propertyName == nameof(ModuleViewModel.UpdateOverallProgress) ||
                   propertyName == nameof(ModuleViewModel.UpdateFileProgress) ||
                   propertyName == nameof(ModuleViewModel.HasUpdateProgress) ||
                   propertyName == nameof(ModuleViewModel.UpdateDownloadDetail) ||
                   propertyName == nameof(ModuleViewModel.HasUpdateDownloadDetail);
        }

        /// <summary>
        /// Returns whether a module property change should realign picker selections.
        /// </summary>
        private static bool ShouldSyncPickerSelection(string? propertyName)
        {
            return string.IsNullOrWhiteSpace(propertyName) ||
                   propertyName == nameof(ModuleViewModel.SelectedSourceMode) ||
                   propertyName == nameof(ModuleViewModel.SelectedReleaseOption) ||
                   propertyName == nameof(ModuleViewModel.SelectedBranch) ||
                   propertyName == nameof(ModuleViewModel.ReleaseOptions) ||
                   propertyName == nameof(ModuleViewModel.BranchOptions);
        }


        // Check flow

        /// <summary>
        /// Starts a manual update check from the dialog footer.
        /// </summary>
        private async void OnCheckUpdatesClicked(object? sender, EventArgs e)
        {
            await CheckForUpdatesAsync(forceOptionLoad: false, announceInLog: true);
        }

        /// <summary>
        /// Applies a source mode picker change even when MAUI skips the two-way binding update.
        /// </summary>
        private void OnSourceModeSelectionChanged(object? sender, EventArgs e)
        {
            if (_isSynchronizingPickers)
            {
                return;
            }

            if (SourceModePicker.SelectedItem is string mode)
            {
                SelectedSourceMode = mode;
            }
        }

        /// <summary>
        /// Applies a release picker change even when the selected object was refreshed in-place.
        /// </summary>
        private void OnReleaseSelectionChanged(object? sender, EventArgs e)
        {
            if (_isSynchronizingPickers)
            {
                return;
            }

            if (ReleasePicker.SelectedItem is UpdateCandidate release)
            {
                SelectedReleaseOption = release;
            }
        }

        /// <summary>
        /// Applies a branch picker change without waiting for delayed binding propagation.
        /// </summary>
        private void OnBranchSelectionChanged(object? sender, EventArgs e)
        {
            if (_isSynchronizingPickers)
            {
                return;
            }

            if (BranchPicker.SelectedItem is string branch)
            {
                _module?.ApplyBranchSelection(branch);
            }
        }

        /// <summary>
        /// Checks the selected module for updates and refreshes the dialog state.
        /// </summary>
        private async Task CheckForUpdatesAsync(bool forceOptionLoad, bool announceInLog)
        {
            var module = _module;
            if (module == null)
            {
                return;
            }

            if (!ApplyPickerSelectionsToModule())
            {
                return;
            }

            if (announceInLog)
            {
                BeginUpdateLogSession();
                module.AppendUpdateLog(L.Get(LocalizationKeys.ModuleUpdate_Log_CheckingFormat, module.Name));
            }

            module.SetUpdateActivityStatus(L.Get(LocalizationKeys.ModuleUpdate_Status_Checking));
            RaiseActivityProperties();

            try
            {
                await module.RefreshUpdateStateAsync(forceOptionLoad);

                if (announceInLog)
                {
                    module.AppendUpdateLog(module.UpdateStatus);
                }
            }
            catch (Exception ex)
            {
                module.SetUpdateActivityStatus(L.Get(LocalizationKeys.ModuleUpdate_Status_CheckFailedFormat, ex.Message));
                if (announceInLog)
                {
                    module.AppendUpdateLog(module.UpdateActivityStatus);
                }
            }
            finally
            {
                if (ReferenceEquals(_module, module))
                {
                    RaiseModuleProperties();
                    RaiseActivityProperties();
                }
            }
        }

        /// <summary>
        /// Loads the currently relevant branches or releases for the selected source mode.
        /// </summary>
        private async Task EnsureModeOptionsLoadedAsync(bool forceRefresh)
        {
            var module = _module;
            if (module == null)
            {
                return;
            }

            try
            {
                await module.EnsureSelectionOptionsLoadedAsync(forceRefresh);
                if (!ReferenceEquals(_module, module))
                {
                    return;
                }

                RaiseModuleProperties();
                SyncPickerSelections();
                if (module.IsBranchMode)
                {
                    QueueBranchPickerResyncAfterListChange();
                }
            }
            catch (Exception ex)
            {
                module.SetUpdateActivityStatus(L.Get(LocalizationKeys.ModuleUpdate_LoadOptionsFailedFormat, ex.Message));
                if (ReferenceEquals(_module, module))
                {
                    RaiseActivityProperties();
                }
            }
        }


        // Install flow

        /// <summary>
        /// Starts module update installation from the dialog footer.
        /// </summary>
        private async void OnInstallUpdateClicked(object? sender, EventArgs e)
        {
            await InstallUpdateAsync();
        }

        /// <summary>
        /// Applies the selected module update and streams progress into the dialog.
        /// </summary>
        private async Task InstallUpdateAsync()
        {
            var module = _module;
            if (module == null)
            {
                return;
            }

            if (!ApplyPickerSelectionsToModule())
            {
                return;
            }

            if (!module.CanInstallSelectedUpdate || module.IsUpdating)
            {
                return;
            }

            BeginUpdateLogSession();
            module.ResetUpdateSession(clearLog: true);
            module.SetUpdateActivityStatus(L.Get(LocalizationKeys.ModuleUpdate_Log_InstallingFormat, module.Name));
            RaiseActivityProperties();
            module.AppendUpdateLog(L.Get(LocalizationKeys.ModuleUpdate_Log_StartingFormat, module.Name));
            QueueUpdateLogLayoutRefresh();

            var success = await module.ApplyUpdateAsync();

            module.SetUpdateActivityStatus(success
                ? L.Get(LocalizationKeys.ModuleUpdate_Activity_SuccessFormat, module.Name)
                : L.Get(LocalizationKeys.ModuleUpdate_Activity_FailedFormat, module.Name));

            module.AppendUpdateLog(success
                ? L.Get(LocalizationKeys.ModuleUpdate_Log_SuccessFormat, module.VersionString)
                : L.Get(LocalizationKeys.ModuleUpdate_Log_Failed));

            if (ReferenceEquals(_module, module))
            {
                RaiseModuleProperties();
                RaiseActivityProperties();
            }
        }

        /// <summary>
        /// Pushes the visible picker values into the module view model before check or install actions run.
        /// </summary>
        private bool ApplyPickerSelectionsToModule()
        {
            if (_module == null)
            {
                return false;
            }

            var selectedMode = SourceModePicker.SelectedItem as string ?? SelectedSourceMode;
            if (!string.IsNullOrWhiteSpace(selectedMode))
            {
                _module.SelectedSourceMode = selectedMode;
            }

            if (string.Equals(_module.SelectedSourceMode, "branch", StringComparison.OrdinalIgnoreCase))
            {
                var selectedBranch = ResolveSelectedBranchFromPicker();
                if (string.IsNullOrWhiteSpace(selectedBranch))
                {
                    _module.SetUpdateActivityStatus(L.Get(LocalizationKeys.ModuleUpdate_SelectBranchRequired));
                    RaiseActivityProperties();
                    return false;
                }

                _module.ApplyBranchSelection(selectedBranch.Trim());
            }

            if (_module.IsReleaseMode)
            {
                var release = ReleasePicker.SelectedItem as UpdateCandidate ?? SelectedReleaseOption;
                if (release != null)
                {
                    _module.SelectedReleaseOption = release;
                }
            }

            RaiseModuleProperties();
            return true;
        }

        /// <summary>
        /// Resolves the visible branch picker choice without silently changing it to a default branch.
        /// </summary>
        private string? ResolveSelectedBranchFromPicker()
        {
            if (BranchPicker.SelectedItem is string selectedBranch &&
                !string.IsNullOrWhiteSpace(selectedBranch))
            {
                return selectedBranch;
            }

            if (BranchPicker.SelectedIndex >= 0 &&
                BranchPicker.SelectedIndex < BranchOptions.Count)
            {
                return BranchOptions[BranchPicker.SelectedIndex];
            }

            return BranchOptions.FirstOrDefault(branch =>
                string.Equals(branch, _module?.SelectedBranch, StringComparison.OrdinalIgnoreCase));
        }

        // Logging

        /// <summary>
        /// Starts a fresh native console session so the log host pins to the latest output.
        /// </summary>
        private void BeginUpdateLogSession()
        {
            _updateLogSessionKey = $"{_module?.SourcePath}|{DateTime.UtcNow.Ticks}";
            OnPropertyChanged(nameof(UpdateLogSessionKey));
        }

        /// <summary>
        /// Schedules layout refresh passes so the native console host measures like SetupWizard.
        /// </summary>
        private void QueueUpdateLogLayoutRefresh()
        {
            RefreshUpdateLogLayout();

            if (Dispatcher == null || Interlocked.Exchange(ref _updateLogLayoutRefreshQueued, 1) == 1)
            {
                return;
            }

            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(16), RefreshUpdateLogLayout);
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(48), () =>
            {
                RefreshUpdateLogLayout();
                Interlocked.Exchange(ref _updateLogLayoutRefreshQueued, 0);
            });
        }

        /// <summary>
        /// Invalidates the log panel and console host after log visibility or text changes.
        /// </summary>
        private void RefreshUpdateLogLayout()
        {
            UpdateLogPanel.InvalidateMeasure();
            UpdateLogConsole.InvalidateMeasure();
        }

        /// <summary>
        /// Pushes the latest log text through bindings so the native log host can refresh immediately.
        /// </summary>
        private void SyncLogView()
        {
            _logSyncCts?.Cancel();
            _logSyncCts = null;
            Interlocked.Exchange(ref _logSyncQueued, 0);
            Interlocked.Exchange(ref _logSyncRequested, 0);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                RaiseLogProperties();
                QueueUpdateLogLayoutRefresh();
            });
        }

        /// <summary>
        /// Coalesces rapid log updates so verbose installers do not saturate the UI thread.
        /// </summary>
        private void QueueLogViewSync()
        {
            Interlocked.Exchange(ref _logSyncRequested, 1);
            if (Interlocked.Exchange(ref _logSyncQueued, 1) == 1)
            {
                return;
            }

            _logSyncCts ??= new CancellationTokenSource();
            _ = FlushLogViewAsync(_logSyncCts.Token);
        }

        /// <summary>
        /// Flushes cached update log text regularly while update output is still arriving.
        /// </summary>
        private async Task FlushLogViewAsync(CancellationToken ct)
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _logSyncRequested, 0);
                    await Task.Delay(75, ct);

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        RaiseLogProperties();
                        QueueUpdateLogLayoutRefresh();
                    });
                }
                while (!ct.IsCancellationRequested &&
                       Interlocked.CompareExchange(ref _logSyncRequested, 0, 0) == 1);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _logSyncQueued, 0);
                if (!ct.IsCancellationRequested &&
                    Interlocked.CompareExchange(ref _logSyncRequested, 0, 0) == 1 &&
                    Interlocked.Exchange(ref _logSyncQueued, 1) == 0)
                {
                    _ = FlushLogViewAsync(ct);
                }
            }
        }


        // Picker synchronization

        /// <summary>
        /// Keeps picker controls aligned with the module view model after option lists are rebuilt.
        /// </summary>
        private void SyncPickerSelections()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _isSynchronizingPickers = true;

                try
                {
                    SourceModePicker.SelectedItem = SourceModeOptions.FirstOrDefault(mode =>
                        string.Equals(mode, SelectedSourceMode, StringComparison.OrdinalIgnoreCase));

                    ReleasePicker.SelectedItem = SelectedReleaseOption;

                    ReapplyBranchPickerFromViewModel();
                }
                finally
                {
                    _isSynchronizingPickers = false;
                }
            });
        }

        /// <summary>
        /// Re-applies branch selection after ItemsSource changes; repeats briefly so WinUI can hydrate the ComboBox.
        /// </summary>
        private void QueueBranchPickerResyncAfterListChange()
        {
            _ = BranchPickerDeferredResyncLoopAsync();
        }

        /// <summary>
        /// Re-applies branch selection on a short delay loop after the branch list changes.
        /// </summary>
        private async Task BranchPickerDeferredResyncLoopAsync()
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (_module == null || !IsBranchMode)
                {
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (_module == null || !IsBranchMode)
                    {
                        return;
                    }

                    _isSynchronizingPickers = true;
                    try
                    {
                        ReapplyBranchPickerFromViewModel();
                    }
                    finally
                    {
                        _isSynchronizingPickers = false;
                    }
                });

                if (attempt < 2)
                {
                    await Task.Delay(16).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Runs after the branch column becomes visible (same grid cell as release); native host may ignore the first selection pass.
        /// </summary>
        private async Task ResyncBranchPickerAfterBranchUiShownAsync()
        {
            try
            {
                await Task.Delay(48).ConfigureAwait(false);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (_module == null || !IsBranchMode)
                    {
                        return;
                    }

                    _isSynchronizingPickers = true;
                    try
                    {
                        ReapplyBranchPickerFromViewModel();
                    }
                    finally
                    {
                        _isSynchronizingPickers = false;
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Aligns the branch picker with <see cref="SelectedBranch"/> using the current <see cref="BranchOptions"/> list.
        /// </summary>
        private void ReapplyBranchPickerFromViewModel()
        {
            if (_module == null || !IsBranchMode)
            {
                return;
            }

            var index = -1;
            for (var i = 0; i < BranchOptions.Count; i++)
            {
                if (string.Equals(BranchOptions[i], SelectedBranch, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                BranchPicker.SelectedIndex = -1;
                BranchPicker.SelectedItem = null;
                ForceWinUiBranchPickerSelection(-1);
                return;
            }

            // Index first: Windows ComboBox often needs a concrete index after ItemsSource refresh before it shows text.
            BranchPicker.SelectedIndex = index;
            BranchPicker.SelectedItem = BranchOptions[index];
            ForceWinUiBranchPickerSelection(index);
        }

        /// <summary>
        /// Pushes selection into the WinUI ComboBox backing the MAUI picker (avoids blank display after ItemsSource updates).
        /// </summary>
        private void ForceWinUiBranchPickerSelection(int index)
        {
#if WINDOWS
            try
            {
                if (BranchPicker.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.ComboBox combo)
                {
                    return;
                }

                if (index < 0 || combo.Items.Count == 0)
                {
                    combo.SelectedIndex = -1;
                    return;
                }

                var bounded = Math.Min(index, combo.Items.Count - 1);
                combo.SelectedIndex = bounded;
            }
            catch
            {
                // ComboBox can throw while its item collection is mid-refresh; deferred resync will retry.
            }
#endif
        }

        /// <summary>
        /// Removes the native WinUI picker chrome while keeping the surrounding MAUI border intact.
        /// </summary>
        private static void ApplyBorderlessPickerStyle(Picker picker)
        {
            void ApplyPlatformStyle()
            {
#if WINDOWS
                if (picker.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ComboBox comboBox)
                {
                    var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    comboBox.Background = transparentBrush;
                    comboBox.BorderBrush = transparentBrush;
                    comboBox.BorderThickness = new Microsoft.UI.Xaml.Thickness(0);
                    comboBox.Padding = new Microsoft.UI.Xaml.Thickness(0);
                    comboBox.CornerRadius = new Microsoft.UI.Xaml.CornerRadius(0);
                    comboBox.UseSystemFocusVisuals = false;
                }
#endif
            }

            picker.HandlerChanged += (_, _) => ApplyPlatformStyle();
            ApplyPlatformStyle();
        }

        // Property refresh

        /// <summary>
        /// Raises every dialog-level property affected by a new module or mode.
        /// </summary>
        private void RaiseDialogProperties()
        {
            OnPropertyChanged(nameof(DialogTitle));
            OnPropertyChanged(nameof(DialogSubtitle));
            RaiseModuleProperties();
            RaiseActivityProperties();
            RaiseLogProperties();
            OnPropertyChanged(nameof(UpdateLogSessionKey));
        }

        /// <summary>
        /// Raises properties mirrored from the selected module card.
        /// </summary>
        private void RaiseModuleProperties()
        {
            OnPropertyChanged(nameof(CurrentVersionLabel));
            OnPropertyChanged(nameof(TargetVersionLabel));
            OnPropertyChanged(nameof(SourceModeOptions));
            OnPropertyChanged(nameof(ReleaseOptions));
            OnPropertyChanged(nameof(BranchOptions));
            OnPropertyChanged(nameof(SelectedSourceMode));
            OnPropertyChanged(nameof(SelectedReleaseOption));
            OnPropertyChanged(nameof(SelectedBranch));
            OnPropertyChanged(nameof(IsBranchMode));
            OnPropertyChanged(nameof(IsReleaseMode));
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(CanInstallUpdate));
            OnPropertyChanged(nameof(CanCheckUpdates));
            OnPropertyChanged(nameof(ShowInstallAction));
            OnPropertyChanged(nameof(IsBusy));
            RaiseActivityProperties();
        }

        /// <summary>
        /// Raises properties shown inside the activity section.
        /// </summary>
        private void RaiseActivityProperties()
        {
            OnPropertyChanged(nameof(ActivityTitle));
            OnPropertyChanged(nameof(ActivityStatus));
            OnPropertyChanged(nameof(HasActivityProgress));
            OnPropertyChanged(nameof(OverallProgress));
            OnPropertyChanged(nameof(FileProgress));
            OnPropertyChanged(nameof(DownloadDetail));
            OnPropertyChanged(nameof(HasDownloadDetail));
            OnPropertyChanged(nameof(CanInstallUpdate));
        }

        /// <summary>
        /// Raises log bindings consumed by the native console host.
        /// </summary>
        private void RaiseLogProperties()
        {
            OnPropertyChanged(nameof(LogText));
            OnPropertyChanged(nameof(HasLog));
        }


        // Property change

        /// <summary>
        /// Raises the overlay property changed event.
        /// </summary>
        protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
