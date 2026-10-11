// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Coordinates quota-notice timing and account actions through the shared dialog service.
/// </summary>
internal sealed class GitHubRateLimitNotice
{
    private readonly Page _owner;
    private readonly DialogHostView _host;
    private readonly DialogService _dialogs;
    private readonly GitHubRateLimitStore _limits;
    private readonly GitHubAccountStore _account;
    private readonly Action _openAccounts;
    private readonly Func<bool> _canShow;
    private bool _attached;
    private bool _showing;
    private bool Connected => !string.IsNullOrWhiteSpace(_account.GetPersonalAccessToken());

    /// <summary>
    /// Connects quota and host-lifetime events without displaying over initial legal acceptance.
    /// </summary>
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

    /// <summary>
    /// Opens an eligible exhaustion notice only when no other dialog occupies the host.
    /// </summary>
    internal void Refresh()
    {
        if (!_attached) return;
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
    private async Task ShowAsync(GitHubRateLimitStore.ExhaustionNotice notice)
    {
        var acknowledged = false;
        try
        {
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
