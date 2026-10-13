// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        private const string MissingVersionDisplay = "—";
        private int _updateOperationRefreshQueued;
        private UpdateOperationPhase? _shownAppUpdatePhase;
        private UpdateOperationPhase? _shownOllamaUpdatePhase;


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
            var availableVersion = _updateManager.GetUpdateOperation("app", "aslm")?.Version
                ?? ResolveCandidateDisplayVersion(_pendingAppUpdateCandidate);
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
                _updateManager.GetUpdateOperation("engine", "ollama-service")?.Version
                    ?? ResolveCandidateDisplayVersion(_pendingOllamaUpdateCandidate));
        }

        /// <summary>
        /// Resolves and refreshes the installed Ollama version shown in its update card.
        /// </summary>
        private async Task RefreshOllamaVersionDisplayAsync()
        {
            // Do not probe or rewrite runtime metadata while the engine is being replaced.
            if (_updateManager.GetUpdateOperation("engine", "ollama-service") != null)
            {
                return;
            }

            var engine = ResolveOllamaEngineConfig();
            if (engine?.Status.Installed != true)
            {
                return;
            }

            try
            {
                await _updateManager.TrySyncEngineInstalledReleaseTagFromRuntimeAsync(engine, isManualRequest: false);
                if (_updateManager.GetUpdateOperation("engine", "ollama-service") != null)
                {
                    return;
                }
                // The update manager invalidates discovery only when it actually changes the manifest.
                engine = ResolveOllamaEngineConfig();
                if (engine == null)
                {
                    return;
                }

                var version = ResolveOllamaDisplayVersion(engine);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (_updateManager.GetUpdateOperation("engine", "ollama-service") != null)
                    {
                        return;
                    }
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
        /// Reloads candidates, versions, and action visibility from shared update services.
        /// </summary>
        private void ResetUpdateActionControls()
        {
            _pendingAppUpdateCandidate = _updateScheduler.GetAvailableAppUpdate();
            _pendingOllamaUpdateCandidate = _updateScheduler.GetAvailableOllamaUpdate();
            var appOperation = _updateManager.GetUpdateOperation("app", "aslm");
            var ollamaOperation = _updateManager.GetUpdateOperation("engine", "ollama-service");

            // ASLM switches from download to installation as soon as the patcher payload is prepared.
            var hasPreparedAppUpdate = _updateManager.HasPendingAppUpdate;
            var hasDownloadableAppUpdate = _pendingAppUpdateCandidate != null && !hasPreparedAppUpdate;
            RefreshAslmVersionInformation();
            if (_prepareAppUpdateHost != null)
            {
                _prepareAppUpdateHost.IsVisible = appOperation != null || hasDownloadableAppUpdate;
            }

            if (_restartAppUpdateButton != null)
            {
                // A pending manifest can appear before staging cleanup finishes; wait for the operation.
                _restartAppUpdateButton.IsVisible = hasPreparedAppUpdate && appOperation == null;
            }

            if (_aslmUpdateActionHost != null)
            {
                _aslmUpdateActionHost.IsVisible = appOperation != null || hasDownloadableAppUpdate || hasPreparedAppUpdate;
            }

            // Ollama exposes its download action only while the persisted candidate remains actionable.
            var engine = ResolveOllamaEngineConfig();
            RefreshOllamaVersionInformation(engine);
            if (_ollamaUpdateHost != null)
            {
                _ollamaUpdateHost.IsVisible = ollamaOperation != null ||
                    (engine?.Status.Installed == true && _pendingOllamaUpdateCandidate != null);
            }

            RefreshUpdateProgressControls(appOperation, ollamaOperation);

            _ = RefreshOllamaVersionDisplayAsync();
        }


        // Scheduler integration

        /// <summary>
        /// Subscribes the visible settings view to candidates and live operations, then restores their snapshots.
        /// </summary>
        private void AttachUpdateSchedulerHandler()
        {
            if (_isUpdateSchedulerSubscribed)
            {
                return;
            }

            _updateScheduler.UpdateStateChanged += OnUpdateSchedulerStateChanged;
            _updateManager.UpdateOperationStateChanged += OnUpdateOperationStateChanged;
            _isUpdateSchedulerSubscribed = true;
            // A cached view may reopen halfway through an automatic download or engine replacement.
            ResetUpdateActionControls();
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
            _updateManager.UpdateOperationStateChanged -= OnUpdateOperationStateChanged;
            _isUpdateSchedulerSubscribed = false;
        }

        /// <summary>
        /// Refreshes update cards when a background check or automatic installation changes state.
        /// </summary>
        private void OnUpdateSchedulerStateChanged(object? sender, EventArgs e)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!_isUpdateSchedulerSubscribed)
                {
                    return;
                }

                ResetUpdateActionControls();
            });
        }

        /// <summary>
        /// Coalesces live update samples and refreshes metadata only when an operation's stage changes.
        /// </summary>
        private void OnUpdateOperationStateChanged(object? sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref _updateOperationRefreshQueued, 1) == 1)
            {
                return;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref _updateOperationRefreshQueued, 0);
                if (!_isUpdateSchedulerSubscribed)
                {
                    return;
                }

                var appOperation = _updateManager.GetUpdateOperation("app", "aslm");
                var ollamaOperation = _updateManager.GetUpdateOperation("engine", "ollama-service");
                // Download samples only change inline progress, not engine discovery or version probes.
                if (_shownAppUpdatePhase != appOperation?.Phase || _shownOllamaUpdatePhase != ollamaOperation?.Phase)
                {
                    ResetUpdateActionControls();
                }
                else
                {
                    RefreshUpdateProgressControls(appOperation, ollamaOperation);
                }
            });
        }


        // Operation presentation

        /// <summary>
        /// Restores both inline operation presentations from the latest shared snapshots.
        /// </summary>
        private void RefreshUpdateProgressControls(UpdateOperationSnapshot? appOperation,
            UpdateOperationSnapshot? ollamaOperation)
        {
            _shownAppUpdatePhase = appOperation?.Phase;
            _shownOllamaUpdatePhase = ollamaOperation?.Phase;
            SetUpdateOperationState(_prepareAppUpdateButton, _prepareAppUpdateProgress, _prepareAppUpdateSpinner,
                _prepareAppUpdateProgressText, _prepareAppUpdateProgressPercent, appOperation, isAppUpdate: true);
            SetUpdateOperationState(_ollamaUpdateButton, _ollamaUpdateProgress, _ollamaUpdateSpinner,
                _ollamaUpdateProgressText, _ollamaUpdateProgressPercent, ollamaOperation, isAppUpdate: false);
        }

        /// <summary>
        /// Replaces one actionable button with the current operation stage and measurable download progress.
        /// </summary>
        private static void SetUpdateOperationState(
            Button? button,
            HorizontalStackLayout? progressContent,
            ActivityIndicator? spinner,
            Label? progressText,
            Label? progressPercent,
            UpdateOperationSnapshot? operation,
            bool isAppUpdate)
        {
            var isRunning = operation != null;
            if (button != null)
            {
                button.IsEnabled = !isRunning;
                button.IsVisible = !isRunning;
                button.Text = L.Get(LocalizationKeys.Settings_DownloadUpdate);
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
                // Applying an archive is not a 100% network transfer; hide percentages until measurable.
                var hasDownloadProgress = operation?.Phase == UpdateOperationPhase.Downloading &&
                    operation.DownloadFraction.HasValue;
                progressPercent.IsVisible = hasDownloadProgress;
                progressPercent.Text = hasDownloadProgress
                    ? $"{(int)Math.Round(operation!.DownloadFraction!.Value * 100)}%" : string.Empty;
            }

            if (progressText != null)
            {
                progressText.Text = operation == null ? string.Empty : L.Get(operation.Phase switch
                {
                    UpdateOperationPhase.Downloading => LocalizationKeys.Settings_Downloading,
                    UpdateOperationPhase.Applying when !isAppUpdate => LocalizationKeys.Modules_Updating,
                    _ => LocalizationKeys.SetupWizard_Preparing
                });
            }
        }


        // Update actions

        /// <summary>
        /// Downloads and applies an available Ollama engine update.
        /// </summary>
        private async void OnUpdateOllamaClicked(object? sender, EventArgs e)
        {
            var candidate = _pendingOllamaUpdateCandidate;
            if (candidate == null || _updateManager.GetUpdateOperation("engine", "ollama-service") != null)
            {
                return;
            }

            try
            {
                // Every caller is observed through the service; there is no view-local progress state.
                var success = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(
                    candidate,
                    log: null,
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
                ResetUpdateActionControls();
            }
        }

        /// <summary>
        /// Downloads an available ASLM build and writes the pending update manifest for the patcher.
        /// </summary>
        private async void OnPrepareAppUpdateClicked(object? sender, EventArgs e)
        {
            var candidate = _pendingAppUpdateCandidate;
            if (candidate == null || _updateManager.GetUpdateOperation("app", "aslm") != null)
            {
                return;
            }

            try
            {
                // Automatic and manual staging publish the same operation snapshots and action state.
                var success = await Task.Run(() => _updateManager.PrepareAppUpdateAsync(
                    candidate,
                    log: null,
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
