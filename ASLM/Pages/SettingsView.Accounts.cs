// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using Debug = System.Diagnostics.Debug;
using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Pages
{
    /// <summary>
    /// Account settings part of the settings view.
    /// </summary>
    public partial class SettingsView
    {
        // ASLM account

        /// <summary>
        /// Switches the ASLM profile between local mode and browser-authorized SUNRISE mode.
        /// </summary>
        private async void OnAslmAccountButtonClicked(object? sender, EventArgs e)
        {
            // Account switching needs the SUNRISE integration, and only one switch may run at a time.
            if (!SunriseService.IsEnabled || _isAslmAccountActionRunning)
            {
                return;
            }

            // Switching from the cloud account back to the local one requires the user's confirmation.
            if (_sunriseService.IsCloudAccount)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalTitle),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalMessage),
                    L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocalConfirm),
                    L.Get(LocalizationKeys.Common_Cancel), danger: true);
                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                // Mark the action as running with a cancellable token, and lock the account card and the footer.
                _isAslmAccountActionRunning = true;
                var actionCts = new CancellationTokenSource();
                _aslmAccountActionCts = actionCts;
                UpdateAslmAccountActionControls();
                UpdateActionButtons();

                // Switch to local mode directly; switching to the cloud needs the browser authorization.
                if (_sunriseService.IsCloudAccount)
                {
                    await _sunriseService.SelectLocalAccountAsync(actionCts.Token);
                }
                else
                {
                    var result = await _sunriseService.AuthenticateApplicationAsync(actionCts.Token);
                    if (!result.Success)
                    {
                        // A cancelled authorization (settings closed) is not reported as an error.
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

                // Re-read the display name from app data after the switch and adopt it as both draft and
                // baseline, so it does not count as an unsaved edit.
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
                // Release the token and unlock the card and the footer, whatever the outcome.
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

            var account = _sunriseService.UserData.Account;
            return string.Join("\n",
                $"User: {FormatAccountIdentity(account.Username, account.Email)}",
                $"UID: {FormatAccountValue(account.Uid)}",
                $"ASLM UID: {FormatAccountValue(account.Aslm?.Uid)}");
        }

        /// <summary>
        /// Refreshes the ASLM account card after authorization or a mode switch.
        /// </summary>
        private void UpdateAslmAccountActionControls()
        {
            if (!SunriseService.IsEnabled)
            {
                return;
            }

            // Account type badge and profile link.
            var isCloudAccount = _sunriseService.IsCloudAccount;
            BuiltInSettingsContainer.AslmAccountTypeBadge.Text = L.Get(
                isCloudAccount
                    ? LocalizationKeys.SetupWizard_CloudAccount
                    : LocalizationKeys.SetupWizard_LocalAccount);
            BuiltInSettingsContainer.AslmAccountLink.IsVisible = isCloudAccount;

            if (_aslmAccountStatusLabel != null)
            {
                _aslmAccountStatusLabel.Text = BuildAslmAccountStatusText();
            }

            // The button offers the opposite mode and is disabled while a switch runs.
            if (_aslmAccountButton != null)
            {
                _aslmAccountButton.Text = _isAslmAccountActionRunning
                    ? L.Get(LocalizationKeys.Settings_AslmAccount_Connecting)
                    : isCloudAccount
                        ? L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToLocal)
                        : L.Get(LocalizationKeys.Settings_AslmAccount_SwitchToCloud);
                _aslmAccountButton.IsEnabled = !_isAslmAccountActionRunning;
                ApplyAccountConnectionButtonState(_aslmAccountButton, isCloudAccount);
            }
        }

        /// <summary>
        /// Opens the configured SUNRISE profile page for the active cloud account.
        /// </summary>
        private async void OnAslmAccountLinkClicked(object? sender, EventArgs e)
        {
            if (!_sunriseService.IsCloudAccount)
            {
                return;
            }

            await OpenAccountLinkAsync(() => _sunriseService.CreateFullUri(SunriseService.ProfileEndpoint));
        }


        // GitHub account

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
                // Show the busy state on the account card while the action runs.
                _isGitHubAccountActionRunning = true;
                UpdateGitHubAccountActionControls();

                if (connect)
                {
                    // Connect: open the browser page for creating an access token, then ask the user to paste it.
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

                    // The returned state is kept even on failure, so the card can show the error message.
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
                    // Disconnect: ask for confirmation first.
                    if (!await DialogService.For(this).ConfirmAsync(this,
                        DialogText.Localized(LocalizationKeys.Settings_GitHub_DisconnectTitle),
                        DialogText.Localized(LocalizationKeys.Settings_GitHub_DisconnectMessage),
                        DialogText.Localized(LocalizationKeys.Settings_GitHub_Disconnect),
                        DialogText.Localized(LocalizationKeys.Common_Cancel), danger: true)) return;
                    var result = await _githubAccountStore.DisconnectAsync();
                    _githubDraft = result.State;
                }
            }
            catch (Exception ex)
            {
                await ShowErrorAsync(L.Get(LocalizationKeys.Dialog_ActionFailedFormat, ex.Message));
            }
            finally
            {
                // Leave the busy state and show the resulting account state, whatever the outcome.
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

            // Every state ends with the API usage line; the first line depends on the connection state.
            var usage = BuildGitHubRateLimitUsageText();
            if (!_githubDraft.IsConnected && !string.IsNullOrWhiteSpace(_githubDraft.ErrorMessage))
            {
                return string.Join("\n",
                    L.Get(LocalizationKeys.Settings_GitHub_ConnectFailed, _githubDraft.ErrorMessage),
                    usage);
            }

            if (!_githubDraft.IsConnected)
            {
                return string.Join("\n",
                    L.Get(LocalizationKeys.Settings_GitHub_NotConnectedHint),
                    usage);
            }

            return string.Join("\n",
                $"User: {FormatAccountIdentity(_githubDraft.Name, _githubDraft.UserName)}",
                usage);
        }

        /// <summary>
        /// Formats the used portion of the current primary GitHub API request window.
        /// </summary>
        private string BuildGitHubRateLimitUsageText()
        {
            var rateLimit = _githubRateLimitStore.GetSnapshot();
            var usedRequests = Math.Clamp(rateLimit.Limit - rateLimit.Remaining, 0, rateLimit.Limit);
            var usedPercent = rateLimit.Limit == 0
                ? 0
                : (double)usedRequests / rateLimit.Limit * 100;
            return string.Create(
                CultureInfo.CurrentCulture,
                $"API: {usedRequests:N0}/{rateLimit.Limit:N0} ({usedPercent:0.##}%)");
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
        /// Opens the connected GitHub profile or the GitHub home page when no account is connected.
        /// </summary>
        private async void OnGitHubAccountLinkClicked(object? sender, EventArgs e)
        {
            await OpenAccountLinkAsync(() =>
            {
                var target = IsGitHubConnected() && !string.IsNullOrWhiteSpace(_githubDraft.ProfileUrl)
                    ? _githubDraft.ProfileUrl
                    : GitHubHomeUrl;
                return new Uri(target, UriKind.Absolute);
            });
        }

        /// <summary>
        /// Prompts the user to paste a GitHub personal access token after browser sign-in.
        /// </summary>
        private Task<string?> PromptForGitHubTokenAsync() =>
            DialogService.For(this).PromptAsync(this,
                DialogText.Localized(LocalizationKeys.Settings_GitHub_ConnectPrompt_Title),
                DialogText.Localized(LocalizationKeys.Settings_GitHub_ConnectPrompt_Message),
                accept: DialogText.Localized(LocalizationKeys.Settings_GitHub_Connect),
                placeholder: DialogText.Localized(LocalizationKeys.Settings_GitHub_TokenPlaceholder),
                kind: DialogFieldKind.Password, maxLength: 256);


        // Ollama account

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

            // A sign-in poll from an earlier action is obsolete once a new action starts.
            StopOllamaStatusPolling();

            // Signing out needs the user's confirmation.
            if (!signIn)
            {
                var confirmed = await ShowAlertAsync(
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Title),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Message),
                    L.Get(LocalizationKeys.Settings_OllamaSignOut_Confirm),
                    L.Get(LocalizationKeys.Common_Cancel), danger: true);

                if (!confirmed)
                {
                    return;
                }
            }

            try
            {
                // Show which action runs on the account card.
                _isOllamaAccountActionRunning = true;
                _ollamaAccountAction = signIn ? "signin" : "signout";
                UpdateOllamaAccountActionControls();

                var result = signIn
                    ? await _ollamaSettings.SignInAsync()
                    : await _ollamaSettings.SignOutAsync();

                // Update the card from the new account state; sign-out reads the cached state, sign-in the live one.
                await RefreshOllamaRuntimeMetadataAsync(queryLiveStatus: signIn);
                UpdateOllamaAccountActionControls();

                if (!result.Success)
                {
                    await ShowErrorAsync(result.Message);
                    return;
                }

                // Browser sign-in completes outside the app, so keep polling until the account shows as signed in.
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
                // Query the runtime for live data, or read the cached settings, away from the UI thread.
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
                // Any other failure resets the metadata to the empty state instead of keeping stale data.
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
            _ollamaDraft.UserId = refreshed.UserId;
            _ollamaDraft.Email = refreshed.Email;
            _ollamaDraft.Bio = refreshed.Bio;
            _ollamaDraft.AvatarUrl = refreshed.AvatarUrl;
            _ollamaDraft.FirstName = refreshed.FirstName;
            _ollamaDraft.LastName = refreshed.LastName;
            _ollamaDraft.Plan = refreshed.Plan;
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

            // The label names the running action, otherwise the action the button would start; the button is
            // disabled without a CLI and while an action or metadata refresh is running.
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

            // Replace any earlier refresh; without an Ollama CLI there is nothing to query.
            StopOllamaMetadataRefresh();

            if (!_ollamaDraft.IsCliAvailable)
            {
                UpdateOllamaAccountActionControls();
                return;
            }

            // Start the refresh in the background so the accounts page appears immediately.
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
                // A newer refresh or closing the dialog cancelled this one; its result is obsolete.
            }
            finally
            {
                // Finish on the UI thread, unless a newer refresh has already replaced this one.
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
                // Query the live account state at a fixed interval until the sign-in shows up or time runs out.
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
                // Polling was stopped by a new account action or by closing the dialog.
            }
            finally
            {
                // Release the poll's token on the UI thread, unless a newer poll has already replaced it.
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
            // Pick the first state that applies: missing CLI, a running action, a refresh or poll in progress,
            // signed out, and finally the signed-in account details.
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

            if (!_ollamaDraft.IsSignedIn)
            {
                return L.Get(LocalizationKeys.Settings_Ollama_NotSignedIn);
            }

            return string.Join("\n",
                $"User: {FormatAccountIdentity(_ollamaDraft.UserName, _ollamaDraft.Email)}",
                $"ID: {FormatAccountValue(_ollamaDraft.UserId)}",
                $"Plan: {FormatAccountValue(_ollamaDraft.Plan)}");
        }

        /// <summary>
        /// Opens the Ollama account settings page.
        /// </summary>
        private async void OnOllamaAccountLinkClicked(object? sender, EventArgs e)
        {
            await OpenAccountLinkAsync(() => new Uri(OllamaSettingsUrl, UriKind.Absolute));
        }


        // Account helpers

        /// <summary>
        /// Resolves and opens an account link while keeping launcher failures out of UI event handlers.
        /// </summary>
        private static async Task OpenAccountLinkAsync(Func<Uri> resolveUri)
        {
            try
            {
                await Launcher.OpenAsync(resolveUri());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open account link: {ex.Message}");
            }
        }

        /// <summary>
        /// Formats a primary account name with a distinct secondary identifier in parentheses.
        /// </summary>
        private static string FormatAccountIdentity(string? primary, string? secondary)
        {
            var normalizedPrimary = primary?.Trim() ?? string.Empty;
            var normalizedSecondary = secondary?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedPrimary))
            {
                return FormatAccountValue(normalizedSecondary);
            }

            if (string.IsNullOrWhiteSpace(normalizedSecondary) ||
                string.Equals(normalizedPrimary, normalizedSecondary, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedPrimary;
            }

            return $"{normalizedPrimary} ({normalizedSecondary})";
        }

        /// <summary>
        /// Replaces unavailable account values with a stable visual placeholder.
        /// </summary>
        private static string FormatAccountValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        }
    }
}
