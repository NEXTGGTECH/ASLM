// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Owns validation, action execution, and cancellation independently of native dialog controls.
/// </summary>
internal sealed class DialogSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<DialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposeRequested;
    private bool _disposed;
    public DialogRequest Request { get; }
    public Task<DialogResult> Completion => _completion.Task;
    public bool IsBusy { get; private set; }
    public Dictionary<string, string> Errors { get; } = [];
    public string? ActionError { get; private set; }
    public event EventHandler? Changed;

    /// <summary>
    /// Validates the request schema before it can enter a presentation queue.
    /// </summary>
    public DialogSession(DialogRequest request)
    {
        request.Validate();
        Request = request;
    }

    /// <summary>
    /// Completes the request without values and signals cancellation to an in-flight action.
    /// </summary>
    public void Cancel()
    {
        if (_disposed) return;
        _completion.TrySetResult(DialogResult.Cancelled);
        _lifetime.Cancel();
    }

    /// <summary>
    /// Allows user dismissal only when the request permits it and no action is executing.
    /// </summary>
    public void Dismiss()
    {
        if (Request.IsDismissible && !IsBusy) Cancel();
    }

    /// <summary>
    /// Validates the form, invokes one action, and either completes the request or exposes its error.
    /// </summary>
    public async Task InvokeAsync(DialogAction action)
    {
        if (IsBusy || Completion.IsCompleted || !Request.Actions.Contains(action) || action.IsVisible?.Invoke() == false) return;
        if (action.IsCancel) { Cancel(); return; }

        // Parse a detached value snapshot; callbacks never need to read from native controls.
        Errors.Clear();
        ActionError = null;
        var values = new Dictionary<string, object?>();
        foreach (var field in Request.Fields)
        {
            object? parsed = field.Value;
            if (action.ValidateFields && field.GetError(out parsed) is { } error) Errors[field.Id] = error;
            values[field.Id] = parsed;
        }
        if (Errors.Count > 0) { Changed?.Invoke(this, EventArgs.Empty); return; }

        // Only one action may execute at a time, including while an asynchronous callback yields.
        IsBusy = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (action.ExecuteAsync != null) await action.ExecuteAsync(values, _lifetime.Token);
            if (!Completion.IsCompleted && action.CloseOnSuccess) _completion.TrySetResult(new(action.Id, values));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Input values (in particular access tokens) are never logged by the dialog framework.
            if (!Completion.IsCompleted) ActionError = L.Get(LocalizationKeys.Dialog_ActionFailedFormat, ex.Message);
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke(this, EventArgs.Empty);
            if (_disposeRequested) Dispose();
        }
    }

    /// <summary>
    /// Cancels the session and disposes its token once any active callback has unwound.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposeRequested = true;
        Cancel();

        // Keep the token alive until an in-flight callback has unwound after its owner was unloaded.
        if (!IsBusy) { _lifetime.Dispose(); _disposed = true; }
    }
}
