// Copyright NEXTGGTECH. Apache License 2.0.

using Debug = System.Diagnostics.Debug;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    public partial class SettingsView
    {
        // Account actions

        /// <summary>
        /// Switches the ASLM profile between local mode and browser-authorized SUNRISE mode.
        /// </summary>
        private async void OnAslmAccountButtonClicked(object? sender, EventArgs e)
        {
            if (_isAslmAccountActionRunning)
            {
                return;
            }

            if (_sunriseService.IsCloudAccount)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalTitle),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalMessage),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalConfirm),
                    L.Get(LocalizationKeys.Common_Cancel));
                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                _isAslmAccountActionRunning = true;
                var actionCts = new CancellationTokenSource();
                _aslmAccountActionCts = actionCts;
                UpdateAslmAccountActionControls();
                UpdateActionButtons();

                if (_sunriseService.IsCloudAccount)
                {
                    await _sunriseService.SelectLocalAccountAsync(actionCts.Token);
                }
                else
                {
                    var result = await _sunriseService.AuthenticateApplicationAsync(actionCts.Token);
                    if (!result.Success)
                    {
                        if (actionCts.IsCancellationRequested)
                        {
                            return;
                        }

                        var error = string.IsNullOrWhiteSpace(result.Error)
                            ? L.Get(LocalizationKeys.SetupWizard_CloudAccountRequired)
                            : result.Error;
                        await ShowErrorAsync(L.Get(
                            LocalizationKeys.Settings_AslmAccount_AuthenticationFailed,
                            error));
                        return;
                    }
                }

                _userNameDraft = _appData.Data.User.Name;
                _aslmBaseline = _aslmBaseline with { UserName = _userNameDraft };
                UsernameEntry.Text = _userNameDraft;
                if (_activeCategory?.Kind == SettingsCategoryKind.Accounts)
                {
                    RenderAccountsCategory();
                    UpdateActionButtons();
                }
            }
            catch (OperationCanceledException)
            {
                // Closing the settings overlay cancels an outstanding browser sign-in.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ASLM account switch failed: {ex}");
                await ShowErrorAsync(L.Get(
                    LocalizationKeys.Settings_AslmAccount_AuthenticationFailed,
                    ex.Message));
            }
            finally
            {
                _aslmAccountActionCts?.Dispose();
                _aslmAccountActionCts = null;
                _isAslmAccountActionRunning = false;
                UpdateAslmAccountActionControls();
                UpdateActionButtons();
            }
        }

        /// <summary>
        /// Cancels an in-flight ASLM browser authorization when the settings view closes.
        /// </summary>
        private void StopAslmAccountAction()
        {
            _aslmAccountActionCts?.Cancel();
        }

        /// <summary>
        /// Builds the status line for the active local or cloud ASLM account mode.
        /// </summary>
        private string BuildAslmAccountStatusText()
        {
            if (_isAslmAccountActionRunning)
            {
                return L.Get(LocalizationKeys.Settings_AslmAccount_Connecting);
            }

            if (!_sunriseService.IsCloudAccount)
            {
                return L.Get(LocalizationKeys.Settings_AslmAccount_LocalStatus);
            }

            return L.Get(
                LocalizationKeys.Settings_AslmAccount_CloudStatus,
                GetCloudAccountDisplayName(_sunriseService.UserData.Account));
        }

        /// <summary>
        /// Refreshes the ASLM account card after authorization or a mode switch.
        /// </summary>
        private void UpdateAslmAccountActionControls()
        {
            if (_aslmAccountStatusLabel != null)
            {
                _aslmAccountStatusLabel.Text = BuildAslmAccountStatusText();
            }

            if (_aslmAccountButton != null)
            {
                _aslmAccountButton.Text = _isAslmAccountActionRunning
                    ? L.Get(LocalizationKeys.Settings_AslmAccount_Connecting)
                    : _sunriseService.IsCloudAccount
                        ? L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocal)
                        : L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToCloud);
                _aslmAccountButton.IsEnabled = !_isAslmAccountActionRunning;
            }
        }

        /// <summary>
        /// Resolves the best user-facing name exposed by a SUNRISE account.
        /// </summary>
        private static string GetCloudAccountDisplayName(SunriseUserAccount account)
        {
            if (!string.IsNullOrWhiteSpace(account.Aslm?.Username))
            {
                return account.Aslm.Username;
            }

            if (!string.IsNullOrWhiteSpace(account.Username))
            {
                return account.Username;
            }

            return account.Email;
        }

        /// <summary>
        /// Handles the GitHub account connect or disconnect button click.
        /// </summary>
        private async void OnGitHubAccountButtonClicked(object? sender, EventArgs e)
        {
            await ExecuteGitHubAccountActionAsync(connect: !IsGitHubConnected());
        }

        /// <summary>
        /// Runs one GitHub account action and refreshes the account card state.
        /// </summary>
        private async Task ExecuteGitHubAccountActionAsync(bool connect)
        {
            if (_isGitHubAccountActionRunning)
            {
                return;
            }

            try
            {
                _isGitHubAccountActionRunning = true;
                UpdateGitHubAccountActionControls();

                if (connect)
                {
                    try
                    {
                        await Launcher.OpenAsync(GitHubAccountStore.BuildTokenCreationUrl());
                    }
                    catch (Exception ex)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, ex.Message));
                        return;
                    }

                    var token = await PromptForGitHubTokenAsync();
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        return;
                    }

                    var result = await _githubAccountStore.ConnectAsync(token);
                    _githubDraft = result.State;

                    if (!result.Success)
                    {
                        await ShowErrorAsync(L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, result.Message));
                        return;
                    }

                    try
                    {
                        await _githubUpdateClient.RefreshRateLimitAsync(GitHubRequestSources.Manual);
                    }
                    catch
                    {
                        // Rate-limit refresh is best-effort after a successful connect.
                    }
                }
                else
                {
                    var result = await _githubAccountStore.DisconnectAsync();
                    _githubDraft = result.State;
                }
            }
            finally
            {
                _isGitHubAccountActionRunning = false;
                UpdateGitHubAccountActionControls();
            }
        }

        /// <summary>
        /// Returns whether the cached GitHub account state is connected.
        /// </summary>
        private bool IsGitHubConnected() => _githubDraft.IsConnected;

        /// <summary>
        /// Builds the GitHub account status line for the settings card.
        /// </summary>
        private string BuildGitHubAccountStatusText()
        {
            if (_isGitHubAccountActionRunning)
            {
                return L.Get(LocalizationKeys.Settings_GitHub_Connecting);
            }

            if (_githubDraft.IsConnected && !string.IsNullOrWhiteSpace(_githubDraft.UserName))
            {
                return L.Get(LocalizationKeys.Settings_GitHub_ConnectedAs, _githubDraft.UserName);
            }

            if (!string.IsNullOrWhiteSpace(_githubDraft.ErrorMessage))
            {
                return L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, _githubDraft.ErrorMessage);
            }

            return L.Get(LocalizationKeys.Settings_GitHub_NotConnectedHint);
        }

        /// <summary>
        /// Refreshes the GitHub account card labels and action button state.
        /// </summary>
        private void UpdateGitHubAccountActionControls()
        {
            if (_githubAccountStatusLabel != null)
            {
                _githubAccountStatusLabel.Text = BuildGitHubAccountStatusText();
            }

            if (_githubAccountButton != null)
            {
                var isConnected = IsGitHubConnected();
                _githubAccountButton.Text = _isGitHubAccountActionRunning
                    ? L.Get(LocalizationKeys.Settings_GitHub_Connecting)
                    : isConnected
                        ? L.Get(LocalizationKeys.Settings_GitHub_Disconnect)
                        : L.Get(LocalizationKeys.Settings_GitHub_Connect);
                _githubAccountButton.IsEnabled = !_isGitHubAccountActionRunning;
                ApplyAccountConnectionButtonState(_githubAccountButton, isConnected);
            }
        }

        /// <summary>
        /// Prompts the user to paste a GitHub personal access token after browser sign-in.
        /// </summary>
        private static Task<string?> PromptForGitHubTokenAsync() =>
            Application.Current?.Windows.Count > 0
                ? Application.Current.Windows[0].Page!.DisplayPromptAsync(
                    L.Get(LocalizationKeys.Settings_GitHub_ConnectPrompt_Title),
                    L.Get(LocalizationKeys.Settings_GitHub_ConnectPrompt_Message),
                    L.Get(LocalizationKeys.Settings_GitHub_Connect),
                    L.Get(LocalizationKeys.Common_Cancel),
                    L.Get(LocalizationKeys.Settings_GitHub_TokenPlaceholder),
                    maxLength: 256,
                    keyboard: Keyboard.Text)
                : Task.FromResult<string?>(null);

        /// <summary>
        /// Handles the single Ollama account action button click.
        /// </summary>
        private async void OnOllamaAccountButtonClicked(object? sender, EventArgs e)
        {
            await ExecuteOllamaAccountActionAsync(signIn: !IsOllamaSignedIn());
        }

        /// <summary>
        /// Runs one Ollama account action and refreshes the compact account button state.
        /// </summary>
        private async Task ExecuteOllamaAccountActionAsync(bool signIn)
        {
            if (_isOllamaAccountActionRunning)
            {
                return;
            }

            StopOllamaStatusPolling();

            if (!signIn)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Title),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Message),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Confirm),
                    L.Get(LocalizationKeys.Common_Cancel));

                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                _isOllamaAccountActionRunning = true;
                _ollamaAccountAction = signIn ? "signin" : "signout";
                UpdateOllamaAccountActionControls();

                var result = signIn
                    ? await _ollamaSettings.SignInAsync()
                    : await _ollamaSettings.SignOutAsync();

                await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: signIn);
                UpdateOllamaAccountActionControls();

                if (!result.Success)
                {
                    await ShowErrorAsync(result.Message);
                    return;
                }

                if (signIn && result.IsPendingVerification && !IsOllamaSignedIn())
                {
                    StartOllamaStatusPolling();
                }
            }
            finally
            {
                _isOllamaAccountActionRunning = false;
                _ollamaAccountAction = string.Empty;
                UpdateOllamaAccountActionControls();
            }
        }

        /// <summary>
        /// Refreshes the non-editable Ollama metadata without overwriting unsaved field edits.
        /// </summary>
        private async Task RefreshOllamaRuntimeMetadataAsync(bool queryLiveStatus, CancellationToken ct = default)
        {
            try
            {
                var refreshed = queryLiveStatus
                    ? await Task.Run(() => _ollamaSettings.RefreshSettingsAsync(ct), ct)
                    : await Task.Run(() => _ollamaSettings.LoadSettings(), ct);
                ApplyOllamaRuntimeMetadata(refreshed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh Ollama settings: {ex.Message}");
                ApplyOllamaRuntimeMetadata(new OllamaPersistentSettings());
            }
        }

        /// <summary>
        /// Copies the latest Ollama metadata into the visible UI draft.
        /// </summary>
        private void ApplyOllamaRuntimeMetadata(OllamaPersistentSettings refreshed)
        {
            _ollamaDraft.IsCliAvailable = refreshed.IsCliAvailable;
            _ollamaDraft.IsSignedIn = refreshed.IsSignedIn;
            _ollamaDraft.UserName = refreshed.UserName;
        }

        /// <summary>
        /// Updates the current account status labels and action buttons when the Ollama card is visible.
        /// </summary>
        private void UpdateOllamaAccountActionControls()
        {
            if (_ollamaAccountStatusLabel != null)
            {
                _ollamaAccountStatusLabel.Text = BuildOllamaAccountStatusText();
            }

            if (_ollamaAccountButton != null)
            {
                var isSignedIn = IsOllamaSignedIn();
                _ollamaAccountButton.Text =
                    _isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signin", StringComparison.Ordinal)
                        ? L.Get(LocalizationKeys.Settings_Ollama_SigningIn) :
                    _isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signout", StringComparison.Ordinal)
                        ? L.Get(LocalizationKeys.Settings_Ollama_SigningOut) :
                    isSignedIn ? L.Get(LocalizationKeys.Settings_Ollama_SignOut) : L.Get(LocalizationKeys.Settings_Ollama_SignIn);
                _ollamaAccountButton.IsEnabled = _ollamaDraft.IsCliAvailable &&
                    !_isOllamaAccountActionRunning &&
                    !_isOllamaMetadataRefreshRunning;
                ApplyAccountConnectionButtonState(_ollamaAccountButton, isSignedIn);
            }
        }

        /// <summary>
        /// Starts a background refresh for the live Ollama account state when the account page is visible.
        /// </summary>
        private void StartOllamaMetadataRefresh()
        {
            if (_activeCategory?.Kind != SettingsCategoryKind.Accounts)
            {
                return;
            }

            StopOllamaMetadataRefresh();

            if (!_ollamaDraft.IsCliAvailable)
            {
                UpdateOllamaAccountActionControls();
                return;
            }

            var refreshCts = new CancellationTokenSource();
            _ollamaMetadataRefreshCts = refreshCts;
            _isOllamaMetadataRefreshRunning = true;
            UpdateOllamaAccountActionControls();

            _ = RefreshOllamaMetadataAsync(refreshCts);
        }

        /// <summary>
        /// Stops the in-flight live Ollama metadata refresh, if any.
        /// </summary>
        private void StopOllamaMetadataRefresh()
        {
            var refreshCts = _ollamaMetadataRefreshCts;
            _ollamaMetadataRefreshCts = null;
            _isOllamaMetadataRefreshRunning = false;
            refreshCts?.Cancel();
            refreshCts?.Dispose();
            UpdateOllamaAccountActionControls();
        }

        /// <summary>
        /// Refreshes the live Ollama metadata without blocking the initial settings page render.
        /// </summary>
        private async Task RefreshOllamaMetadataAsync(CancellationTokenSource refreshCts)
        {
            try
            {
                await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: true, refreshCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!ReferenceEquals(_ollamaMetadataRefreshCts, refreshCts))
                    {
                        return;
                    }

                    refreshCts.Dispose();
                    _ollamaMetadataRefreshCts = null;
                    _isOllamaMetadataRefreshRunning = false;
                    UpdateOllamaAccountActionControls();
                });
            }
        }

        /// <summary>
        /// Determines whether the current Ollama account state should be treated as signed in.
        /// </summary>
        private bool IsOllamaSignedIn() => _ollamaDraft.IsSignedIn;

        /// <summary>
        /// Starts a short-lived background poll that waits for the browser sign-in flow to complete.
        /// </summary>
        private void StartOllamaStatusPolling()
        {
            StopOllamaStatusPolling();

            var pollingCts = new CancellationTokenSource();
            _ollamaStatusPollingCts = pollingCts;
            UpdateOllamaAccountActionControls();

            _ = PollOllamaStatusAsync(pollingCts);
        }

        /// <summary>
        /// Cancels the active background sign-in status poll, if any.
        /// </summary>
        private void StopOllamaStatusPolling()
        {
            var pollingCts = _ollamaStatusPollingCts;
            _ollamaStatusPollingCts = null;
            pollingCts?.Cancel();
            pollingCts?.Dispose();
            UpdateOllamaAccountActionControls();
        }

        /// <summary>
        /// Polls the local Ollama API until sign-in completes or the timeout window expires.
        /// </summary>
        private async Task PollOllamaStatusAsync(CancellationTokenSource pollingCts)
        {
            var ct = pollingCts.Token;
            var deadline = DateTime.UtcNow + OllamaSignInPollDuration;

            try
            {
                while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                {
                    await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: true, ct);

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        UpdateOllamaAccountActionControls();
                    });

                    if (IsOllamaSignedIn())
                    {
                        return;
                    }

                    await Task.Delay(OllamaSignInPollInterval, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    if (!ReferenceEquals(_ollamaStatusPollingCts, pollingCts))
                    {
                        return;
                    }

                    pollingCts.Dispose();
                    _ollamaStatusPollingCts = null;
                    UpdateOllamaAccountActionControls();
                });
            }
        }

        /// <summary>
        /// Returns the compact status line shown under the Ollama account title.
        /// </summary>
        private string BuildOllamaAccountStatusText()
        {
            if (!_ollamaDraft.IsCliAvailable)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_NotInstalled);
            }

            if (_isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signin", StringComparison.Ordinal))
            {
                return L.Get(LocalizationKeys.Settings_Ollama_WaitingSignIn);
            }

            if (_isOllamaAccountActionRunning && string.Equals(_ollamaAccountAction, "signout", StringComparison.Ordinal))
            {
                return L.Get(LocalizationKeys.Settings_Ollama_SigningOutStatus);
            }

            if (_isOllamaMetadataRefreshRunning)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_CheckingAccount);
            }

            if (_ollamaStatusPollingCts != null && !_ollamaDraft.IsSignedIn)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_WaitingSignIn);
            }

            if (_ollamaDraft.IsSignedIn)
            {
                return string.IsNullOrWhiteSpace(_ollamaDraft.UserName)
                    ? L.Get(LocalizationKeys.Settings_Ollama_SignedIn)
                    : L.Get(LocalizationKeys.Settings_Ollama_SignedInAs, _ollamaDraft.UserName);
            }

            return L.Get(LocalizationKeys.Settings_Ollama_NotSignedIn);
        }

    }
}
