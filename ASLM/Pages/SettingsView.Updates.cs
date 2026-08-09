// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Update actions

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

            return "—";
        }

        /// <summary>
        /// Returns whether an engine version value is a manifest placeholder rather than a release tag.
        /// </summary>
        private static bool IsPlaceholderEngineReleaseTag(string? value) =>
            string.IsNullOrWhiteSpace(value) ||
            string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Builds the Ollama update status label shown before a check runs in this session.
        /// </summary>
        private string BuildInitialOllamaUpdateStatusText() =>
            L.Get(LocalizationKeys.Settings_UpdateStatus_None);

        /// <summary>
        /// Resolves and refreshes only the installed Ollama version label in the Updates category.
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
                    if (_ollamaVersionDescriptionLabel != null)
                    {
                        _ollamaVersionDescriptionLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Description,
                            version);
                    }
                });
            }
            catch
            {
                // Version sync is best-effort UI enrichment only.
            }
        }

        /// <summary>
        /// Builds the manual-check status label shown before a check runs in this session.
        /// </summary>
        private string BuildInitialManualUpdateStatusText()
        {
            if (_updateManager.HasPendingAppUpdate)
            {
                var pending = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                return string.IsNullOrWhiteSpace(pending)
                    ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                    : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, pending);
            }

            return L.Get(LocalizationKeys.Settings_UpdateStatus_None);
        }

        /// <summary>
        /// Resets transient update actions after persisted settings are loaded or discarded.
        /// </summary>
        private void ResetUpdateActionControls()
        {
            _pendingAppUpdateCandidate = null;
            _pendingOllamaUpdateCandidate = null;

            // ASLM status is derived from the currently installed and already prepared releases.
            var installedReleaseSummary = SettingsService.BuildAslmInstalledReleaseSummary(_appData);
            BuiltInSettingsContainer.InstalledReleaseSummary.Text = installedReleaseSummary;
            BuiltInSettingsContainer.InstalledReleaseSummary.IsVisible = !string.IsNullOrWhiteSpace(installedReleaseSummary);
            if (_updateStatusLabel != null)
            {
                _updateStatusLabel.Text = BuildInitialManualUpdateStatusText();
            }

            if (_prepareAppUpdateButton != null)
            {
                _prepareAppUpdateButton.IsVisible = false;
            }

            if (_restartAppUpdateButton != null)
            {
                _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
            }

            // Ollama availability and version are independent from editable update preferences.
            var engine = ResolveOllamaEngineConfig();
            var isInstalled = engine?.Status.Installed == true;
            var currentVersion = isInstalled
                ? ResolveOllamaDisplayVersion(engine!)
                : "-";
            if (_ollamaVersionDescriptionLabel != null)
            {
                _ollamaVersionDescriptionLabel.Text = L.Get(
                    LocalizationKeys.Settings_OllamaUpdate_Description,
                    currentVersion);
            }

            if (_ollamaCheckUpdateButton != null)
            {
                _ollamaCheckUpdateButton.IsEnabled = isInstalled;
            }

            if (_ollamaUpdateButton != null)
            {
                _ollamaUpdateButton.IsVisible = false;
                _ollamaUpdateButton.IsEnabled = isInstalled;
            }

            if (_ollamaUpdateStatusLabel != null)
            {
                _ollamaUpdateStatusLabel.Text = BuildInitialOllamaUpdateStatusText();
            }

            _ = RefreshOllamaVersionDisplayAsync();
        }

        /// <summary>
        /// Builds the ASLM manual-check summary after <see cref="UpdateManager.CheckAppUpdateAsync"/> completes.
        /// </summary>
        private string BuildAslmManualUpdateCheckStatusMessage()
        {
            var appTag = _pendingAppUpdateCandidate != null
                ? (_pendingAppUpdateCandidate.ReleaseTag ?? _pendingAppUpdateCandidate.RemoteVersion).Trim()
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(appTag))
            {
                if (_updateManager.HasPendingAppUpdate)
                {
                    var pending = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                    if (!string.IsNullOrWhiteSpace(pending) &&
                        string.Equals(pending, appTag, StringComparison.OrdinalIgnoreCase))
                    {
                        return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmTagPrepared, appTag);
                    }

                    if (!string.IsNullOrWhiteSpace(pending))
                    {
                        return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmAvailableStaged, appTag, pending);
                    }
                }

                return L.Get(LocalizationKeys.Settings_UpdateStatus_AslmAvailable, appTag);
            }

            if (_updateManager.HasPendingAppUpdate)
            {
                var pendingOnly = _updateManager.TryGetPendingPreparedAppVersion()?.Trim();
                return string.IsNullOrWhiteSpace(pendingOnly)
                    ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                    : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, pendingOnly);
            }

            return L.Get(LocalizationKeys.Settings_UpdateStatus_UpToDate);
        }

        /// <summary>
        /// Runs a manual ASLM update check and exposes self-update preparation when available.
        /// </summary>
        private async void OnCheckAslmUpdatesClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            try
            {
                _pendingAppUpdateCandidate = null;
                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = false;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
                }

                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Checking);
                }

                _pendingAppUpdateCandidate = await Task.Run(() =>
                    _updateManager.CheckAppUpdateAsync(isManualRequest: true));

                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = _pendingAppUpdateCandidate != null &&
                                                        !_updateManager.HasPendingAppUpdate;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = _updateManager.HasPendingAppUpdate;
                }

                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = BuildAslmManualUpdateCheckStatusMessage();
                }
            }
            catch (Exception ex)
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Runs a manual Ollama engine update check and exposes the update action when available.
        /// </summary>
        private async void OnCheckOllamaUpdatesClicked(object? sender, EventArgs e)
        {
            if (sender is Button button)
            {
                button.IsEnabled = false;
            }

            try
            {
                _pendingOllamaUpdateCandidate = null;
                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = false;
                }

                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Checking);
                }

                var engine = ResolveOllamaEngineConfig();
                if (engine?.Status.Installed != true)
                {
                    if (_ollamaUpdateStatusLabel != null)
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, "Ollama is not installed.");
                    }

                    return;
                }

                _pendingOllamaUpdateCandidate = await Task.Run(() =>
                    _updateManager.CheckEngineUpdateAsync(engine, isManualRequest: true));

                await RefreshOllamaVersionDisplayAsync();
                engine = ResolveOllamaEngineConfig();

                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = _pendingOllamaUpdateCandidate != null;
                }

                if (_ollamaUpdateStatusLabel != null)
                {
                    if (_pendingOllamaUpdateCandidate != null)
                    {
                        var ollamaTag = (_pendingOllamaUpdateCandidate.ReleaseTag ??
                                         _pendingOllamaUpdateCandidate.RemoteVersion).Trim();
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Available,
                            ollamaTag);
                    }
                    else if (engine?.Status.Installed == true)
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_UpToDate);
                    }
                }
            }
            catch (Exception ex)
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CheckFailed, ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Downloads and applies an available Ollama engine update.
        /// </summary>
        private async void OnUpdateOllamaClicked(object? sender, EventArgs e)
        {
            if (_pendingOllamaUpdateCandidate == null)
            {
                return;
            }

            if (sender is Button updateButton)
            {
                updateButton.IsEnabled = false;
            }

            try
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(LocalizationKeys.Settings_OllamaUpdate_Updating);
                }

                var candidate = _pendingOllamaUpdateCandidate;
                var log = new Progress<string>(message =>
                {
                    if (_ollamaUpdateStatusLabel != null)
                    {
                        MainThread.BeginInvokeOnMainThread(() => _ollamaUpdateStatusLabel.Text = message);
                    }
                });

                var success = await Task.Run(() => _updateManager.ApplyEngineUpdateAsync(
                    candidate,
                    log,
                    isManualRequest: true));

                if (_ollamaUpdateStatusLabel != null)
                {
                    if (success)
                    {
                        var installedTag = (candidate.ReleaseTag ?? candidate.RemoteVersion).Trim();
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Success,
                            installedTag);
                        if (_ollamaVersionDescriptionLabel != null)
                        {
                            _ollamaVersionDescriptionLabel.Text = L.Get(
                                LocalizationKeys.Settings_OllamaUpdate_Description,
                                installedTag);
                        }

                        _pendingOllamaUpdateCandidate = null;
                    }
                    else
                    {
                        _ollamaUpdateStatusLabel.Text = L.Get(
                            LocalizationKeys.Settings_OllamaUpdate_Failed,
                            "The update could not be applied.");
                    }
                }

                if (_ollamaUpdateButton != null)
                {
                    _ollamaUpdateButton.IsVisible = !success && _pendingOllamaUpdateCandidate != null;
                }
            }
            catch (Exception ex)
            {
                if (_ollamaUpdateStatusLabel != null)
                {
                    _ollamaUpdateStatusLabel.Text = L.Get(
                        LocalizationKeys.Settings_OllamaUpdate_Failed,
                        ex.Message);
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Downloads an available ASLM build and writes the pending update manifest for the patcher.
        /// </summary>
        private async void OnPrepareAppUpdateClicked(object? sender, EventArgs e)
        {
            if (_pendingAppUpdateCandidate == null)
            {
                return;
            }

            if (sender is Button prepareButton)
            {
                prepareButton.IsEnabled = false;
            }

            try
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Downloading);
                }

                var log = new Progress<string>(message =>
                {
                    if (_updateStatusLabel != null)
                    {
                        MainThread.BeginInvokeOnMainThread(() => _updateStatusLabel.Text = message);
                    }
                });

                var success = await Task.Run(() => _updateManager.PrepareAppUpdateAsync(
                    _pendingAppUpdateCandidate,
                    log,
                    isManualRequest: true));
                if (_updateStatusLabel != null)
                {
                    if (success)
                    {
                        var preparedTag = _pendingAppUpdateCandidate?.ReleaseTag ?? _pendingAppUpdateCandidate?.RemoteVersion;
                        _updateStatusLabel.Text = string.IsNullOrWhiteSpace(preparedTag)
                            ? L.Get(LocalizationKeys.Settings_UpdateStatus_Prepared)
                            : L.Get(LocalizationKeys.Settings_UpdateStatus_PreparedWithVersion, preparedTag.Trim());
                    }
                    else
                    {
                        _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_CouldNotPrepare);
                    }
                }

                if (_prepareAppUpdateButton != null)
                {
                    _prepareAppUpdateButton.IsVisible = !success;
                }

                if (_restartAppUpdateButton != null)
                {
                    _restartAppUpdateButton.IsVisible = success || _updateManager.HasPendingAppUpdate;
                }
            }
            finally
            {
                if (sender is Button senderButton)
                {
                    senderButton.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// Restarts through the launcher so the prepared ASLM update can be applied by the patcher.
        /// </summary>
        private async void OnRestartNowClicked(object? sender, EventArgs e)
        {
            if (sender is Button restartButton)
            {
                restartButton.IsEnabled = false;
            }

            try
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_Restarting);
                }

                await RestartApplicationThroughLauncherAsync();
            }
            catch (Exception ex)
            {
                if (_updateStatusLabel != null)
                {
                    _updateStatusLabel.Text = L.Get(LocalizationKeys.Settings_UpdateStatus_RestartFailed, ex.Message);
                }

                if (sender is Button failedButton)
                {
                    failedButton.IsEnabled = true;
                }
            }
        }
    }
}
