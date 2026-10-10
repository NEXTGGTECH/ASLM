// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;

namespace ASLM.Controls.GitHub;

/// <summary>A button-only, application-wide warning for an exhausted GitHub hourly quota.</summary>
public partial class GitHubRateLimitNoticeView : ContentView, ILocalizable
{
    private GitHubRateLimitStore? _limits;
    private GitHubAccountStore? _account;
    private View? _background;
    private Action? _openAccounts;
    private Func<bool>? _canShow;
    private GitHubRateLimitStore.ExhaustionNotice? _notice;
    private bool _attached;
    private bool _connected;
    private bool _backgroundWasEnabled;

    internal bool IsOpen => _notice != null;

    public GitHubRateLimitNoticeView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ResizeDialog();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal void Initialize(IServiceProvider services, View background, Action openAccounts, Func<bool>? canShow = null)
    {
        _limits = services.GetRequiredService<GitHubRateLimitStore>();
        _account = services.GetRequiredService<GitHubAccountStore>();
        _background = background;
        _openAccounts = openAccounts;
        _canShow = canShow;
        LocalizableAttach.Hook(this, services.GetRequiredService<AppLocalizationService>(), this);
    }

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (_attached || _limits == null) return;
        _attached = true;
        _limits.StateChanged += OnRateLimitChanged;
        TryShowNotice();
    }

    private async void OnUnloaded(object? sender, EventArgs e)
    {
        _attached = false;
        if (_limits == null) return;
        _limits.StateChanged -= OnRateLimitChanged;
        var notice = _notice;
        HideNotice();
        if (notice != null)
            await _limits.EndExhaustionNoticeAsync(notice, acknowledged: false);
    }

    private void OnRateLimitChanged(object? sender, EventArgs e) => Dispatcher.Dispatch(TryShowNotice);

    private void TryShowNotice()
    {
        if (!_attached || _limits == null || _background == null) return;
        if (IsOpen)
        {
            // Account verification started before the warning may complete in the background.
            _connected = !string.IsNullOrWhiteSpace(_account?.GetPersonalAccessToken());
            ApplyLocalization();
            return;
        }
        if (_canShow?.Invoke() == false) return;
        var notice = _limits.TryBeginExhaustionNotice();
        if (notice == null) return;
        _notice = notice;
        // Use the actual configured credentials, not a possibly stale profile request.
        _connected = !string.IsNullOrWhiteSpace(_account?.GetPersonalAccessToken());
        _backgroundWasEnabled = _background.IsEnabled;
        _background.IsEnabled = false;
        ApplyLocalization();
        ResizeDialog();
        Buttons.IsEnabled = true;
        InputTransparent = false;
        DialogLayer.IsVisible = true;
        Dispatcher.Dispatch(() => { if (IsOpen) (_connected ? OkButton : ConnectButton).Focus(); });
    }

    internal void Refresh() => TryShowNotice();

    public void ApplyLocalization()
    {
        TitleLabel.Text = L.Get(LocalizationKeys.GitHubRateLimit_Title);
        MessageLabel.Text = L.Get(LocalizationKeys.GitHubRateLimit_Message) +
            (_connected ? string.Empty : "\n\n" + L.Get(LocalizationKeys.GitHubRateLimit_ConnectHint));
        LaterButton.Text = L.Get(LocalizationKeys.GitHubRateLimit_RemindLater);
        ConnectButton.Text = L.Get(LocalizationKeys.GitHubRateLimit_ConnectAccount);
        OkButton.Text = L.Get(LocalizationKeys.Common_OK);
        LaterButton.IsVisible = ConnectButton.IsVisible = !_connected;
        OkButton.IsVisible = _connected;
    }

    private void ResizeDialog() => DialogBorder.WidthRequest = Math.Max(0, Math.Min(540, Width - 48));
    private async void OnLaterClicked(object? sender, EventArgs e) => await CompleteAsync(openAccounts: false);
    private async void OnConnectClicked(object? sender, EventArgs e) => await CompleteAsync(openAccounts: true);

    private async Task CompleteAsync(bool openAccounts)
    {
        if (_notice == null || !Buttons.IsEnabled || _limits == null) return;
        var notice = _notice;
        Buttons.IsEnabled = false;
        if (openAccounts)
        {
            try { _openAccounts?.Invoke(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Could not open GitHub account settings: {ex}");
                Buttons.IsEnabled = true;
                return;
            }
        }
        await _limits.EndExhaustionNoticeAsync(notice, acknowledged: true);
        if (!ReferenceEquals(_notice, notice)) return;
        HideNotice();
        TryShowNotice();
    }

    private void HideNotice()
    {
        if (_notice != null && _background != null)
            _background.IsEnabled = _backgroundWasEnabled;
        _notice = null;
        DialogLayer.IsVisible = false;
        InputTransparent = true;
    }
}
