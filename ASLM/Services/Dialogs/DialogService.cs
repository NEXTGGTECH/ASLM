// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Controls.Dialogs;
using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Routes requests to the caller's page and serializes dialogs within its application window.
/// </summary>
public sealed class DialogService
{
    private sealed record Registration(DialogHostView Host, DialogQueue Queue);
    private readonly Dictionary<Page, Registration> _hosts = [];

    /// <summary>
    /// Registers a page's host, cancelling requests owned by a replaced presenter.
    /// </summary>
    internal void Attach(Page page, DialogHostView host)
    {
        if (_hosts.TryGetValue(page, out var previous))
        {
            if (previous.Host == host) return;
            previous.Queue.CancelAll();
        }
        _hosts[page] = new(host, new());
    }

    /// <summary>
    /// Removes only the matching host and cancels its active and queued requests.
    /// </summary>
    internal void Detach(Page page, DialogHostView host)
    {
        if (_hosts.TryGetValue(page, out var registration) && registration.Host == host)
        {
            _hosts.Remove(page);
            registration.Queue.CancelAll();
        }
    }

    /// <summary>
    /// Resolves the shared service from the caller's MAUI context or application services.
    /// </summary>
    public static DialogService For(VisualElement owner) =>
        (owner.Handler?.MauiContext?.Services ?? Application.Current!.Handler!.MauiContext!.Services)
        .GetRequiredService<DialogService>();

    /// <summary>
    /// Enqueues a dialog on its owner's UI thread, or returns cancellation for a detached owner.
    /// </summary>
    public Task<DialogResult> ShowAsync(VisualElement owner, DialogRequest request, CancellationToken ct = default) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            // Resolve the actual owning page rather than whichever window was opened first.
            Element? element = owner;
            while (element != null && element is not Page) element = element.Parent;
            return element is Page page && _hosts.TryGetValue(page, out var host)
                ? ShowCoreAsync(owner, host, request, ct)
                : Task.FromResult(DialogResult.Cancelled);
        });

    /// <summary>
    /// Links a queued session to owner unloading and caller cancellation until presentation ends.
    /// </summary>
    private static async Task<DialogResult> ShowCoreAsync(VisualElement owner, Registration registration,
        DialogRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return DialogResult.Cancelled;
        using var session = new DialogSession(request);

        // Cancel the session when its initiating view leaves the visual tree.
        void Cancel(object? _, EventArgs __) => session.Cancel();

        // UI cancellation also removes a waiting request before it can become visible.
        owner.Unloaded += Cancel;
        using var cancellation = ct.Register(() => owner.Dispatcher.Dispatch(session.Cancel));
        try { return await registration.Queue.EnqueueAsync(session, registration.Host.PresentAsync); }
        finally { owner.Unloaded -= Cancel; }
    }

    /// <summary>
    /// Shows a message with one primary acknowledgement action.
    /// </summary>
    public Task<DialogResult> AlertAsync(VisualElement owner, DialogText title, DialogText message, DialogText? accept = null) =>
        ShowAsync(owner, new()
        {
            Title = title, Message = message,
            Actions = [new() { Id = "ok", Text = accept ?? DialogText.Localized(LocalizationKeys.Common_OK),
                Style = DialogActionStyle.Primary, IsDefault = true }]
        });

    /// <summary>
    /// Requests confirmation with an optional cancel button and destructive action styling.
    /// </summary>
    public async Task<bool> ConfirmAsync(VisualElement owner, DialogText title, DialogText message,
        DialogText accept, DialogText? cancel = null, bool danger = false)
    {
        var actions = new List<DialogAction>();
        if (cancel != null) actions.Add(new() { Id = "cancel", Text = cancel, IsCancel = true });
        // Destructive confirmations must not execute merely because Enter was pressed.
        actions.Add(new() { Id = "confirm", Text = accept,
            Style = danger ? DialogActionStyle.Danger : DialogActionStyle.Primary, IsDefault = !danger });
        return (await ShowAsync(owner, new() { Title = title, Message = message, Actions = actions })).ActionId == "confirm";
    }

    /// <summary>
    /// Collects a required typed value and returns null when the user cancels or the owner unloads.
    /// </summary>
    public async Task<string?> PromptAsync(VisualElement owner, DialogText title, DialogText label,
        string initialValue = "", DialogText? accept = null, DialogText? placeholder = null,
        DialogFieldKind kind = DialogFieldKind.Text, int maxLength = int.MaxValue)
    {
        var result = await ShowAsync(owner, new()
        {
            Title = title,
            Fields = [new() { Id = "value", Label = label, Value = initialValue, Required = true,
                Kind = kind, MaxLength = maxLength, Placeholder = placeholder }],
            Actions = [new() { Id = "cancel", Text = DialogText.Localized(LocalizationKeys.Common_Cancel), IsCancel = true },
                new() { Id = "accept", Text = accept ?? DialogText.Localized(LocalizationKeys.Common_OK),
                    Style = DialogActionStyle.Primary, IsDefault = true }]
        });
        return result.IsCancelled ? null : result.Values["value"]?.ToString();
    }
}
