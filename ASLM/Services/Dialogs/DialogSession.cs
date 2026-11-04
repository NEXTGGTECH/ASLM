// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs
{
    /// <summary>
    /// Owns validation, action execution, and cancellation independently of native dialog controls.
    /// </summary>
    internal sealed class DialogSession : IDisposable
    {
        // Fields

        /// <summary>
        /// Cancellation source whose token is passed to the running action and cancelled when the session ends.
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new();

        /// <summary>
        /// Source of the final dialog result; the first completion wins.
        /// </summary>
        private readonly TaskCompletionSource<DialogResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Set when disposal was requested while an action was still running, so the action's cleanup finishes it.
        /// </summary>
        private bool _disposeRequested;

        /// <summary>
        /// Whether the cancellation source was already released.
        /// </summary>
        private bool _disposed;


        // State exposed to the view

        /// <summary>
        /// Validated request that this session presents.
        /// </summary>
        public DialogRequest Request { get; }

        /// <summary>
        /// Completes with the accepted action and its values, or with a cancelled result.
        /// </summary>
        public Task<DialogResult> Completion => _completion.Task;

        /// <summary>
        /// Whether an action is executing; the view locks input and dismissal meanwhile.
        /// </summary>
        public bool IsBusy { get; private set; }

        /// <summary>
        /// Validation errors of the last attempted action, keyed by field ID.
        /// </summary>
        public Dictionary<string, string> Errors { get; } = [];

        /// <summary>
        /// Localized message of the last failed action, or null when there is none.
        /// </summary>
        public string? ActionError { get; private set; }

        /// <summary>
        /// Raised when busy state, errors or field presentation changed and the view must refresh.
        /// </summary>
        public event EventHandler? Changed;


        // Initialization

        /// <summary>
        /// Validates the request schema before it can enter a presentation queue.
        /// </summary>
        /// <param name="request">Request to present; it must have unique field and action IDs.</param>
        public DialogSession(DialogRequest request)
        {
            request.Validate();
            Request = request;
            foreach (var field in request.Fields) field.PresentationChanged += OnFieldPresentationChanged;
        }


        // Request lifecycle

        /// <summary>
        /// Re-evaluates action availability when a live selector finishes loading or changes mode.
        /// </summary>
        private void OnFieldPresentationChanged(object? sender, EventArgs e)
        {
            if (Completion.IsCompleted) return;
            if (sender is DialogField field) Errors.Remove(field.Id);
            Changed?.Invoke(this, EventArgs.Empty);
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
        /// <param name="action">Action chosen by the user; actions that are hidden, disabled or not part of the request are ignored.</param>
        public async Task InvokeAsync(DialogAction action)
        {
            // Ignore input while busy or finished, and actions the user cannot currently see or use.
            if (IsBusy || Completion.IsCompleted || !Request.Actions.Contains(action) ||
                action.IsVisible?.Invoke() == false || action.IsEnabled?.Invoke() == false) return;
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

            // Validation errors keep the dialog open so the view can show them next to the fields.
            if (Errors.Count > 0) { Changed?.Invoke(this, EventArgs.Empty); return; }

            // Only one action may execute at a time, including while an asynchronous callback yields.
            IsBusy = true;
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                // Run the caller's callback, then complete the dialog unless it was cancelled meanwhile.
                if (action.ExecuteAsync != null) await action.ExecuteAsync(values, _lifetime.Token);
                if (!Completion.IsCompleted && action.CloseOnSuccess) _completion.TrySetResult(new(action.Id, values));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // The session was cancelled or disposed, so the interrupted action is not an error.
            }
            catch (Exception ex)
            {
                // Input values (in particular access tokens) are never logged by the dialog framework.
                if (!Completion.IsCompleted) ActionError = L.Get(LocalizationKeys.Dialog_ActionFailedFormat, ex.Message);
            }
            finally
            {
                // Finish a disposal that had to wait for this action to unwind.
                IsBusy = false;
                Changed?.Invoke(this, EventArgs.Empty);
                if (_disposeRequested) Dispose();
            }
        }


        // Disposal

        /// <summary>
        /// Cancels the session and disposes its token once any active callback has unwound.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            // Stop reacting to field changes and cancel; the token is released below or by the running action.
            _disposeRequested = true;
            foreach (var field in Request.Fields) field.PresentationChanged -= OnFieldPresentationChanged;
            Cancel();

            // Keep the token alive until an in-flight callback has unwound after its owner was unloaded.
            if (!IsBusy) { _lifetime.Dispose(); _disposed = true; }
        }
    }
}
