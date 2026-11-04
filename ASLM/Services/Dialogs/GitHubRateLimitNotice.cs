// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs
{
    /// <summary>
    /// Coordinates quota-notice timing and account actions through the shared dialog service.
    /// </summary>
    internal sealed class GitHubRateLimitNotice
    {
        // Dependencies

        /// <summary>
        /// Page that owns the notice; dialogs are shown over it and its lifetime drives subscriptions.
        /// </summary>
        private readonly Page _owner;

        /// <summary>
        /// Dialog host of the owner page, used to detect other open dialogs and to refresh localization.
        /// </summary>
        private readonly DialogHostView _host;

        /// <summary>
        /// Shared service that presents the notice.
        /// </summary>
        private readonly DialogService _dialogs;

        /// <summary>
        /// Store that tracks GitHub quota state and deduplicates exhaustion notices per reset window.
        /// </summary>
        private readonly GitHubRateLimitStore _limits;

        /// <summary>
        /// Store that tells whether a GitHub account (personal access token) is connected.
        /// </summary>
        private readonly GitHubAccountStore _account;

        /// <summary>
        /// Opens the account settings where the user can connect GitHub.
        /// </summary>
        private readonly Action _openAccounts;

        /// <summary>
        /// Tells whether showing a notice is currently allowed, for example not over initial legal acceptance.
        /// </summary>
        private readonly Func<bool> _canShow;


        // State

        /// <summary>
        /// Whether the notice is subscribed to quota and host events, which holds while the owner is loaded.
        /// </summary>
        private bool _attached;

        /// <summary>
        /// Whether a notice dialog is currently open, so a second one is never started.
        /// </summary>
        private bool _showing;

        /// <summary>
        /// Whether a GitHub personal access token is stored, which selects the actions the notice offers.
        /// </summary>
        private bool Connected => !string.IsNullOrWhiteSpace(_account.GetPersonalAccessToken());


        // Initialization

        /// <summary>
        /// Connects quota and host-lifetime events without displaying over initial legal acceptance.
        /// </summary>
        /// <param name="owner">Page that shows the notice; its Loaded and Unloaded events control the subscriptions.</param>
        /// <param name="host">Dialog host of that page.</param>
        /// <param name="services">Provider that supplies the dialog service and the GitHub stores.</param>
        /// <param name="openAccounts">Opens the account settings from the connect action.</param>
        /// <param name="canShow">Returns false while the notice must stay hidden.</param>
        internal GitHubRateLimitNotice(Page owner, DialogHostView host, IServiceProvider services,
            Action openAccounts, Func<bool> canShow)
        {
            _owner = owner;
            _host = host;
            _dialogs = services.GetRequiredService<DialogService>();
            _limits = services.GetRequiredService<GitHubRateLimitStore>();
            _account = services.GetRequiredService<GitHubAccountStore>();
            _openAccounts = openAccounts;
            _canShow = canShow;
            owner.Loaded += OnOwnerLoaded;
            owner.Unloaded += OnOwnerUnloaded;
        }


        // Owner lifetime

        /// <summary>
        /// Subscribes to quota changes once the notice has a visible owner.
        /// </summary>
        private void OnOwnerLoaded(object? sender, EventArgs e)
        {
            if (_attached) return;
            _attached = true;
            _limits.StateChanged += OnChanged;
            _host.Closed += OnChanged;
            Refresh();
        }

        /// <summary>
        /// Removes external subscriptions when the owner leaves its window.
        /// </summary>
        private void OnOwnerUnloaded(object? sender, EventArgs e)
        {
            _attached = false;
            _limits.StateChanged -= OnChanged;
            _host.Closed -= OnChanged;
        }

        /// <summary>
        /// Marshals quota notifications from background requests onto the owner's UI thread.
        /// </summary>
        private void OnChanged(object? sender, EventArgs e) => _owner.Dispatcher.Dispatch(Refresh);


        // Notice presentation

        /// <summary>
        /// Opens an eligible exhaustion notice only when no other dialog occupies the host.
        /// </summary>
        internal void Refresh()
        {
            // Without a loaded owner there is nothing to show or refresh.
            if (!_attached) return;

            // An open notice only re-resolves its texts, so a connected account updates its message and buttons.
            if (_showing) { _host.ApplyLocalization(); return; }
            if (!_canShow() || _host.IsOpen) return;

            // The store owns reset-window deduplication; closing a notice must not repeat it immediately.
            var notice = _limits.TryBeginExhaustionNotice();
            if (notice == null) return;
            _showing = true;
            _ = ShowAsync(notice);
        }

        /// <summary>
        /// Shows account-dependent actions and acknowledges this quota window only after user input.
        /// </summary>
        /// <param name="notice">Reservation granted by the store for the current quota reset window.</param>
        private async Task ShowAsync(GitHubRateLimitStore.ExhaustionNotice notice)
        {
            var acknowledged = false;
            try
            {
                // Actions follow the account state: a guest may postpone or connect, a connected user only confirms.
                var result = await _dialogs.ShowAsync(_owner, new()
                {
                    Title = DialogText.Localized(LocalizationKeys.GitHubRateLimit_Title),
                    Message = new(() => L.Get(LocalizationKeys.GitHubRateLimit_Message) +
                        (Connected ? string.Empty : "\n\n" + L.Get(LocalizationKeys.GitHubRateLimit_ConnectHint))),
                    IsDismissible = false,
                    Actions =
                    [
                        new() { Id = "later", Text = DialogText.Localized(LocalizationKeys.GitHubRateLimit_RemindLater),
                            IsVisible = () => !Connected },
                        new() { Id = "connect", Text = DialogText.Localized(LocalizationKeys.GitHubRateLimit_ConnectAccount),
                            Style = DialogActionStyle.Primary, IsDefault = true, IsVisible = () => !Connected,
                            ExecuteAsync = (_, _) => { _openAccounts(); return Task.CompletedTask; } },
                        new() { Id = "ok", Text = DialogText.Localized(LocalizationKeys.Common_OK),
                            Style = DialogActionStyle.Primary, IsDefault = true, IsVisible = () => Connected }
                    ]
                });
                acknowledged = !result.IsCancelled;
            }
            finally
            {
                // Owner cancellation releases the notice without pretending the user acknowledged it.
                await _limits.EndExhaustionNoticeAsync(notice, acknowledged);
                _showing = false;
                if (_attached) Refresh();
            }
        }
    }
}
