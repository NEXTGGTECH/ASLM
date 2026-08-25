// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        private const string MissingVersionDisplay = "—";
        private static readonly TimeSpan DownloadProgressUpdateInterval = TimeSpan.FromMilliseconds(100);

        private bool _isAslmUpdateDownloadRunning;
        private bool _isOllamaUpdateDownloadRunning;


        // Update state

        /// <summary>
        /// Returns the managed Ollama engine manifest when it is present on disk.
        /// </summary>
        private EngineConfig? ResolveOllamaEngineConfig()
        {
            return _engineInstaller.DiscoverEngines()
                .FirstOrDefault(engine =>
                    string.Equals(engine.Id, "ollama-service", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns the best available installed version label for the Ollama engine card.
        /// </summary>
        private static string ResolveOllamaDisplayVersion(EngineConfig engine)
        {
            if (!string.IsNullOrWhiteSpace(engine.Status.InstalledReleaseTag) &&
                !IsPlaceholderEngineReleaseTag(engine.Status.InstalledReleaseTag))
            {
                return engine.Status.InstalledReleaseTag.Trim();
            }

            if (!string.IsNullOrWhiteSpace(engine.Status.InstalledVersion) &&
                !IsPlaceholderEngineReleaseTag(engine.Status.InstalledVersion))
            {
                return engine.Status.InstalledVersion.Trim();
            }

            return MissingVersionDisplay;
        }

        /// <summary>
        /// Returns whether an engine version value is a manifest placeholder rather than a release tag.
        /// </summary>
        private static bool IsPlaceholderEngineReleaseTag(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the installed ASLM release tag, falling back to the running assembly version.
        /// </summary>
        private string ResolveAslmDisplayVersion()
        {
            var installedTag = _appData.Data.Updates.InstalledReleaseTag;
            if (!string.IsNullOrWhiteSpace(installedTag))
            {
                return installedTag.Trim();
            }

            return string.IsNullOrWhiteSpace(_updateManager.CurrentAppVersion)
                ? MissingVersionDisplay
                : _updateManager.CurrentAppVersion.Trim();
        }

        /// <summary>
        /// Returns the release label carried by an update candidate.
        /// </summary>
        private static string? ResolveCandidateDisplayVersion(UpdateCandidate? candidate)
        {
            var version = !string.IsNullOrWhiteSpace(candidate?.ReleaseTag)
                ? candidate.ReleaseTag
                : candidate?.RemoteVersion;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }

        /// <summary>
        /// Writes installed and optional available versions into one update card.
        /// </summary>
        private static void SetVersionInformation(
            Label? installedLabel,
            Label? availableLabel,
            string installedVersion,
            string? availableVersion)
        {
            if (installedLabel != null)
            {
                installedLabel.Text = L.Get(
                    LocalizationKeys.Settings_UpdateCard_InstalledVersion,
                    installedVersion);
            }

            if (availableLabel == null)
            {
                return;
            }

            var hasAvailableVersion = !string.IsNullOrWhiteSpace(availableVersion);
            availableLabel.IsVisible = hasAvailableVersion;
            availableLabel.Text = hasAvailableVersion
                ? L.Get(LocalizationKeys.Settings_UpdateCard_AvailableVersion, availableVersion!)
                : string.Empty;
        }

        /// <summary>
        /// Refreshes ASLM version rows from the installed, discovered, and prepared update state.
        /// </summary>
        private void RefreshAslmVersionInformation()
        {
            var availableVersion = ResolveCandidateDisplayVersion(_pendingAppUpdateCandidate);
            if (string.IsNullOrWhiteSpace(availableVersion))
            {
                availableVersion = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
            }

            SetVersionInformation(
                _aslmInstalledVersionLabel,
                _aslmAvailableVersionLabel,
                ResolveAslmDisplayVersion(),
                availableVersion);
        }

        /// <summary>
        /// Refreshes Ollama version rows from the installed engine and discovered update state.
        /// </summary>
        private void RefreshOllamaVersionInformation(EngineConfig? engine = null)
        {
            engine ??= ResolveOllamaEngineConfig();
            var installedVersion = engine?.Status.Installed == true
                ? ResolveOllamaDisplayVersion(engine)
                : MissingVersionDisplay;
            SetVersionInformation(
                _ollamaInstalledVersionLabel,
                _ollamaAvailableVersionLabel,
                installedVersion,
                ResolveCandidateDisplayVersion(_pendingOllamaUpdateCandidate));
        }

        /// <summary>
        /// Resolves and refreshes the installed Ollama version shown in its update card.
        /// </summary>
        private async Task RefreshOllamaVersionDisplayAsync()
        {
            var engine = ResolveOllamaEngineConfig();
            if (engine?.Status.Installed != true)
            {
                return;
            }

            try
            {
                await _updateManager.TrySyncEngineInstalledReleaseTagFromRuntimeAsync(engine, isManualRequest: false);
                _engineInstaller.InvalidateCache();
                engine = ResolveOllamaEngineConfig();
                if (engine == null)
                {
                    return;
                }

                var version = ResolveOllamaDisplayVersion(engine);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SetVersionInformation(
                        _ollamaInstalledVersionLabel,
                        _ollamaAvailableVersionLabel,
                        version,
                        ResolveCandidateDisplayVersion(_pendingOllamaUpdateCandidate));
                });
            }
            catch
            {
                // Runtime version probing only enriches the card and must not disrupt settings.
            }
        }

        /// <summary>
        /// Reloads update candidates and action visibility from the shared scheduler state.
        /// </summary>
        private void ResetUpdateActionControls()
        {
            _pendingAppUpdateCandidate = _updateScheduler.GetAvailableAppUpdate();
            _pendingOllamaUpdateCandidate = _updateScheduler.GetAvailableOllamaUpdate();

            // ASLM switches from download to installation as soon as the patcher payload is prepared.
            var hasPreparedAppUpdate = _updateManager.HasPendingAppUpdate;
            var hasDownloadableAppUpdate = _pendingAppUpdateCandidate != null && !hasPreparedAppUpdate;
            RefreshAslmVersionInformation();
            if (_prepareAppUpdateHost != null)
            {
                _prepareAppUpdateHost.IsVisible = hasDownloadableAppUpdate;
            }

            if (_restartAppUpdateButton != null)
            {
                _restartAppUpdateButton.IsVisible = hasPreparedAppUpdate;
            }

            if (_aslmUpdateActionHost != null)
            {
                _aslmUpdateActionHost.IsVisible = hasDownloadableAppUpdate || hasPreparedAppUpdate;
            }

            SetDownloadProgressState(
                _prepareAppUpdateButton,
                _prepareAppUpdateProgress,
                _prepareAppUpdateSpinner,
                _prepareAppUpdateProgressPercent,
                isRunning: false,
                fraction: 0);

            // Ollama exposes its download action only while the persisted candidate remains actionable.
            var engine = ResolveOllamaEngineConfig();
            RefreshOllamaVersionInformation(engine);
            if (_ollamaUpdateHost != null)
            {
                _ollamaUpdateHost.IsVisible = engine?.Status.Installed == true &&
                                              _pendingOllamaUpdateCandidate != null;
            }

            SetDownloadProgressState(
                _ollamaUpdateButton,
                _ollamaUpdateProgress,
                _ollamaUpdateSpinner,
                _ollamaUpdateProgressPercent,
                isRunning: false,
                fraction: 0);

            _ = RefreshOllamaVersionDisplayAsync();
        }


        // Scheduler integration

        /// <summary>
        /// Subscribes the visible settings view to shared background update results once.
        /// </summary>
        private void AttachUpdateSchedulerHandler()
        {
            if (_isUpdateSchedulerSubscribed)
            {
                return;
            }

            _updateScheduler.UpdateStateChanged += OnUpdateSchedulerStateChanged;
            _isUpdateSchedulerSubscribed = true;
        }

        /// <summary>
        /// Detaches the update listener when the settings view leaves the visual tree.
        /// </summary>
        private void DetachUpdateSchedulerHandler()
        {
            if (!_isUpdateSchedulerSubscribed)
            {
                return;
            }

            _updateScheduler.UpdateStateChanged -= OnUpdateSchedulerStateChanged;
            _isUpdateSchedulerSubscribed = false;
        }

        /// <summary>
        /// Refreshes update cards when a background check or automatic installation changes state.
        /// </summary>
        private void OnUpdateSchedulerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_isAslmUpdateDownloadRunning || _isOllamaUpdateDownloadRunning)
                {
                    return;
                }

                ResetUpdateActionControls();
            });
        }


        // Download presentation

        /// <summary>
        /// Creates a UI progress sink that limits visual updates to ten times per second.
        /// </summary>
        private static IProgress<DownloadProgress> CreateThrottledDownloadProgress(
            Action<double> applyProgress)
        {
            var lastUpdateUtc = DateTimeOffset.MinValue;
            return new Progress<DownloadProgress>(progress =>
            {
                var now = DateTimeOffset.UtcNow;
                if (progress.Fraction < 1 && now - lastUpdateUtc < DownloadProgressUpdateInterval)
                {
                    return;
                }

                lastUpdateUtc = now;
                applyProgress(Math.Clamp(progress.Fraction, 0, 1));
            });
        }

        /// <summary>
        /// Switches one update button between its normal label and inline download progress.
        /// </summary>
        private static void SetDownloadProgressState(
            Button? button,
            HorizontalStackLayout? progressContent,
            ActivityIndicator? spinner,
            Label? progressPercent,
            bool isRunning,
            double fraction)
        {
            if (button != null)
            {
                button.IsEnabled = !isRunning;
                button.Text = isRunning
                    ? string.Empty
                    : L.Get(LocalizationKeys.Settings_DownloadUpdate);
            }

            if (progressContent != null)
            {
                progressContent.IsVisible = isRunning;
            }

            if (spinner != null)
            {
                spinner.IsRunning = isRunning;
            }

            if (progressPercent != null)
            {
                var percentage = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);
                progressPercent.Text = isRunning ? $"{percentage}%" : string.Empty;
            }
        }


        // Update actions

        /// <summary>
        /// Downloads and applies an available Ollama engine update.
        /// </summary>
        private async void OnUpdateOllamaClicked(object? sender, EventArgs e)
        {
            var candidate = _pendingOllamaUpdateCandidate;
            if (candidate == null || _isOllamaUpdateDownloadRunning)
            {
                return;
            }

            _isOllamaUpdateDownloadRunning = true;
            SetDownloadProgressState(
                _ollamaUpdateButton,
                _ollamaUpdateProgress,
                _ollamaUpdateSpinner,
                _ollamaUpdateProgressPercent,
                isRunning: true,
                fraction: 0);

            try
            {
                var progress = CreateThrottledDownloadProgress(fraction =>
                    SetDownloadProgressState(
                        _ollamaUpdateButton,
                        _ollamaUpdateProgress,
                        _ollamaUpdateSpinner,
                        _ollamaUpdateProgressPercent,
                        isRunning: true,
                        fraction));
                var success = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(
                    candidate,
                    log: null,
                    progress: progress,
                    isManualRequest: true));

                if (!success)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Notifications_EngineUpdateFailed));
                    return;
                }

                _pendingOllamaUpdateCandidate = null;
                await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
                _engineInstaller.InvalidateCache();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(
                    $"{L.Get(LocalizationKeys.Notifications_EngineUpdateFailed)}\n\n{ex.Message}");
            }
            finally
            {
                _isOllamaUpdateDownloadRunning = false;
                ResetUpdateActionControls();
            }
        }

        /// <summary>
        /// Downloads an available ASLM build and writes the pending update manifest for the patcher.
        /// </summary>
        private async void OnPrepareAppUpdateClicked(object? sender, EventArgs e)
        {
            var candidate = _pendingAppUpdateCandidate;
            if (candidate == null || _isAslmUpdateDownloadRunning)
            {
                return;
            }

            _isAslmUpdateDownloadRunning = true;
            SetDownloadProgressState(
                _prepareAppUpdateButton,
                _prepareAppUpdateProgress,
                _prepareAppUpdateSpinner,
                _prepareAppUpdateProgressPercent,
                isRunning: true,
                fraction: 0);

            try
            {
                var progress = CreateThrottledDownloadProgress(fraction =>
                    SetDownloadProgressState(
                        _prepareAppUpdateButton,
                        _prepareAppUpdateProgress,
                        _prepareAppUpdateSpinner,
                        _prepareAppUpdateProgressPercent,
                        isRunning: true,
                        fraction));
                var success = await Task.Run(() => _updateManager.PrepareAppUpdateAsync(
                    candidate,
                    log: null,
                    progress: progress,
                    isManualRequest: true));

                if (!success)
                {
                    await ShowErrorAsync(L.Get(LocalizationKeys.Settings_UpdateStatus_CouldNotPrepare));
                    return;
                }

                _pendingAppUpdateCandidate = null;
                await _updateScheduler.ClearAvailableCandidateAsync(candidate.TargetKind, candidate.TargetId);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(
                    $"{L.Get(LocalizationKeys.Notifications_AslmUpdatePrepareFailed)}\n\n{ex.Message}");
            }
            finally
            {
                _isAslmUpdateDownloadRunning = false;
                ResetUpdateActionControls();
            }
        }

        /// <summary>
        /// Restarts through the launcher so the prepared ASLM update can be installed by the patcher.
        /// </summary>
        private async void OnRestartNowClicked(object? sender, EventArgs e)
        {
            if (sender is Button restartButton)
            {
                restartButton.IsEnabled = false;
            }

            try
            {
                await RestartApplicationThroughLauncherAsync();
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Settings_UpdateStatus_RestartFailed, ex.Message));
                if (sender is Button failedButton)
                {
                    failedButton.IsEnabled = true;
                }
            }
        }
    }
}
