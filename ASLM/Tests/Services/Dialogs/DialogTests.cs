// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Controls.Dialogs;
using ASLM.Localization;
using ASLM.Models;
using ASLM.Resources.Strings;
using ASLM.Services.Dialogs;

namespace ASLM.Tests.Services.Dialogs;

/// <summary>
/// Verifies shared dialog validation, lifetime, queueing, and localized form contracts without native UI.
/// </summary>
public sealed class DialogTests
{
    /// <summary>
    /// Creates the default accepting action used by session tests.
    /// </summary>
    private static DialogAction Accept(Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task>? execute = null) =>
        new() { Id = "accept", Text = "Accept", IsDefault = true, ExecuteAsync = execute };

    /// <summary>
    /// Wraps one action and optional fields in a minimal valid request.
    /// </summary>
    private static DialogRequest Request(DialogAction action, params DialogField[] fields) =>
        new() { Title = "Test", Actions = [action], Fields = fields };

    /// <summary>
    /// Verifies required-field errors keep the request open until the user corrects the input.
    /// </summary>
    [Fact]
    public async Task Required_field_blocks_callback_and_keeps_form_open_until_corrected()
    {
        var calls = 0;
        var action = Accept((_, _) => { calls++; return Task.CompletedTask; });
        var field = new DialogField { Id = "name", Label = "Name", Required = true, Value = "  " };
        using var session = new DialogSession(Request(action, field));
        await session.InvokeAsync(action);
        session.Errors.Should().ContainKey("name");
        session.Completion.IsCompleted.Should().BeFalse();
        calls.Should().Be(0);
        // Correct the same live field and retry without constructing a second session.
        field.Value = "Ocean";
        await session.InvokeAsync(action);
        calls.Should().Be(1);
        session.Errors.Should().BeEmpty();
        (await session.Completion).Values["name"].Should().Be("Ocean");
    }

    /// <summary>
    /// Verifies integer validation rejects overflow and nonintegral input independently of keyboards.
    /// </summary>
    [Theory]
    [InlineData(DialogFieldKind.Integer, "9223372036854775807", true)]
    [InlineData(DialogFieldKind.Integer, "9223372036854775808", false)]
    [InlineData(DialogFieldKind.Integer, "2.5", false)]
    [InlineData(DialogFieldKind.Integer, "words", false)]
    public void Typed_fields_are_validated_not_just_filtered_by_keyboard(DialogFieldKind kind, string value, bool valid)
    {
        var field = new DialogField { Id = "value", Label = "Value", Kind = kind, Value = value };
        (field.GetError(out _) == null).Should().Be(valid);
    }

    /// <summary>
    /// Verifies range checks and stable choice IDs produce typed callback values.
    /// </summary>
    [Fact]
    public async Task Numeric_bounds_choices_and_boolean_return_typed_values()
    {
        var number = new DialogField { Id = "count", Label = "Count", Kind = DialogFieldKind.Integer, Value = "15", Minimum = 1, Maximum = 10 };
        number.GetError(out _).Should().NotBeNull();
        number.Value = "5";
        var choice = new DialogField { Id = "mode", Label = "Mode", Kind = DialogFieldKind.Choice, Value = "release",
            Choices = [new("release", "Localized release label")] };
        var flag = new DialogField { Id = "flag", Label = "Flag", Kind = DialogFieldKind.Boolean, Value = false, Required = true };
        var action = Accept();
        using var session = new DialogSession(Request(action, number, choice, flag));
        await session.InvokeAsync(action);
        var result = await session.Completion;
        result.Values["count"].Should().Be(5L);
        result.Values["mode"].Should().Be("release");
        result.Values["flag"].Should().Be(false);
        // A translated display caption must never be accepted in place of a choice ID.
        choice.Value = "Localized release label";
        choice.GetError(out _).Should().NotBeNull();
    }

    /// <summary>
    /// Verifies decimal input follows the user's culture and restores test-thread state afterward.
    /// </summary>
    [Fact]
    public void Decimal_uses_the_current_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru");
            var field = new DialogField { Id = "value", Label = "Value", Kind = DialogFieldKind.Decimal, Value = "2,5" };
            field.GetError(out var parsed).Should().BeNull();
            parsed.Should().Be(2.5m);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    /// <summary>
    /// Verifies cancellation bypasses validation and never returns password field values.
    /// </summary>
    [Fact]
    public async Task Cancel_never_validates_or_returns_secret_fields()
    {
        var cancel = new DialogAction { Id = "cancel", Text = "Cancel", IsCancel = true };
        using var session = new DialogSession(Request(cancel,
            new DialogField { Id = "token", Label = "Token", Kind = DialogFieldKind.Password, Value = "secret", Validate = _ => throw new Exception() }));
        await session.InvokeAsync(cancel);
        var result = await session.Completion;
        result.IsCancelled.Should().BeTrue();
        result.Values.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a button-only notice ignores backdrop dismissal but accepts its explicit action.
    /// </summary>
    [Fact]
    public async Task Button_only_notice_ignores_dismissal_but_accepts_a_button()
    {
        var action = Accept();
        using var session = new DialogSession(new() { Title = "Quota", IsDismissible = false, Actions = [action] });
        session.Dismiss();
        session.Completion.IsCompleted.Should().BeFalse();
        await session.InvokeAsync(action);
        (await session.Completion).ActionId.Should().Be("accept");
    }

    /// <summary>
    /// Verifies owner cancellation can release a dialog even when user dismissal is prohibited.
    /// </summary>
    [Fact]
    public async Task Owner_cancellation_also_closes_button_only_notices()
    {
        using var session = new DialogSession(new() { Title = "Quota", IsDismissible = false, Actions = [Accept()] });
        session.Cancel();
        (await session.Completion).IsCancelled.Should().BeTrue();
    }

    /// <summary>
    /// Verifies repeat clicks and dismissal cannot race an unfinished asynchronous callback.
    /// </summary>
    [Fact]
    public async Task Duplicate_click_and_dismissal_are_ignored_while_callback_is_running()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var action = Accept(async (_, _) => { calls++; await gate.Task; });
        using var session = new DialogSession(Request(action));
        var first = session.InvokeAsync(action);
        await session.InvokeAsync(action);
        session.Dismiss();
        session.Completion.IsCompleted.Should().BeFalse();
        calls.Should().Be(1);
        // Finish the original callback only after the competing inputs have been rejected.
        gate.SetResult();
        await first;
        (await session.Completion).IsCancelled.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failed callback exposes an error and allows retry with the original field values.
    /// </summary>
    [Fact]
    public async Task Failed_action_preserves_input_and_can_be_retried()
    {
        var fail = true;
        var action = Accept((_, _) => fail ? Task.FromException(new IOException("disk error")) : Task.CompletedTask);
        var field = new DialogField { Id = "name", Label = "Name", Value = "My theme" };
        using var session = new DialogSession(Request(action, field));
        await session.InvokeAsync(action);
        session.IsBusy.Should().BeFalse();
        session.Completion.IsCompleted.Should().BeFalse();
        session.ActionError.Should().Contain("disk error");
        field.Value.Should().Be("My theme");
        fail = false;
        await session.InvokeAsync(action);
        (await session.Completion).ActionId.Should().Be("accept");
    }

    /// <summary>
    /// Verifies unloading cancels an active callback and cannot later turn cancellation into acceptance.
    /// </summary>
    [Fact]
    public async Task Unload_during_callback_cancels_token_and_late_completion_cannot_accept()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var action = Accept(async (_, ct) => { token = ct; await gate.Task; ct.ThrowIfCancellationRequested(); });
        var session = new DialogSession(Request(action));
        var running = session.InvokeAsync(action);
        session.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        (await session.Completion).IsCancelled.Should().BeTrue();
        // The callback still owns its token until it observes cancellation and exits.
        gate.SetResult();
        await running;
        session.Dispose();
        (await session.Completion).IsCancelled.Should().BeTrue();
    }

    /// <summary>
    /// Verifies hidden account-dependent actions cannot be invoked through stale references.
    /// </summary>
    [Fact]
    public async Task Hidden_actions_cannot_be_invoked()
    {
        var hidden = new DialogAction { Id = "hidden", Text = "Hidden", IsVisible = () => false };
        using var session = new DialogSession(Request(hidden));
        await session.InvokeAsync(hidden);
        session.Completion.IsCompleted.Should().BeFalse();
    }

    /// <summary>
    /// Verifies duplicate field and button IDs are rejected before presentation.
    /// </summary>
    [Fact]
    public void Duplicate_field_and_action_ids_are_rejected()
    {
        var action = Accept();
        var field = new DialogField { Id = "same", Label = "Field" };
        var duplicateFields = () => new DialogSession(Request(action, field, field));
        duplicateFields.Should().Throw<ArgumentException>();
        var duplicateActions = () => new DialogSession(new() { Title = "Test", Actions = [action, action] });
        duplicateActions.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies requests are presented sequentially and cancelled waiters never become visible.
    /// </summary>
    [Fact]
    public async Task Queue_serializes_requests_and_skips_cancelled_waiters()
    {
        var queue = new DialogQueue();
        var action = Accept();
        using var first = new DialogSession(Request(action));
        using var cancelled = new DialogSession(Request(action));
        using var last = new DialogSession(Request(action));
        var shown = new List<DialogSession>();
        // Record presentation separately from enqueueing to detect flashes of cancelled dialogs.
        Task<DialogResult> Present(DialogSession session) { shown.Add(session); return session.Completion; }
        var firstTask = queue.EnqueueAsync(first, Present);
        var cancelledTask = queue.EnqueueAsync(cancelled, Present);
        var lastTask = queue.EnqueueAsync(last, Present);
        shown.Should().Equal(first);
        cancelled.Cancel();
        await first.InvokeAsync(action);
        await firstTask;
        (await cancelledTask).IsCancelled.Should().BeTrue();
        // Cancellation of the host must release both active and pending requests.
        queue.CancelAll();
        (await lastTask).IsCancelled.Should().BeTrue();
        shown.Should().NotContain(cancelled);
    }

    /// <summary>
    /// Verifies presentation failure releases the queue so the next request can complete.
    /// </summary>
    [Fact]
    public async Task Failed_presenter_does_not_block_the_next_request()
    {
        var queue = new DialogQueue();
        using var failed = new DialogSession(Request(Accept()));
        using var next = new DialogSession(Request(Accept()));
        var failure = queue.EnqueueAsync(failed, _ => throw new InvalidOperationException("view error"));
        var success = queue.EnqueueAsync(next, _ => Task.FromResult(new DialogResult("ok", new Dictionary<string, object?>())));
        await ((Func<Task>)(async () => await failure)).Should().ThrowAsync<InvalidOperationException>();
        (await success).ActionId.Should().Be("ok");
    }

    /// <summary>
    /// Verifies theme creation collects only name and base, leaving palette overrides to settings.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Theme_creation_only_collects_name_and_base_and_inherits_the_palette(string appearance)
    {
        var form = new ThemeCreationForm();
        form.Fields.Select(f => f.Id).Should().Equal("name", "baseAppearance");
        form.Fields.Single(f => f.Id == "name").Value = "  My theme  ";
        form.Fields.Single(f => f.Id == "baseAppearance").Value = appearance;
        var theme = form.BuildTheme(form.Fields.ToDictionary(f => f.Id, f => f.Value));
        theme.Name.Should().Be("My theme");
        theme.BaseAppearance.Should().Be(appearance);
        theme.Colors.Should().BeEmpty("new themes inherit their base instead of freezing its colors as overrides");
        theme.Id.Should().BeEmpty("an ID is allocated by the store only when the user saves");
    }

    /// <summary>
    /// Supplies every supported application language to the resource completeness test.
    /// </summary>
    public static IEnumerable<object[]> Cultures => AppLocalizationService.SupportedLanguages.Select(l => new object[] { l.Id });

    /// <summary>
    /// Verifies dialog strings exist in each locale itself, including required format placeholders.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cultures))]
    public void New_dialog_strings_exist_in_every_locale_without_fallback(string culture)
    {
        var resources = AppResources.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo(culture == "en" ? "" : culture), true, false)!;
        string[] keys = [LocalizationKeys.Common_Close, LocalizationKeys.Dialog_Required, LocalizationKeys.Dialog_InvalidValue,
            LocalizationKeys.Dialog_ActionFailedFormat, LocalizationKeys.Settings_GitHub_DisconnectTitle,
            LocalizationKeys.Settings_GitHub_DisconnectMessage, LocalizationKeys.SetupWizard_FinishFailedFormat];
        foreach (var key in keys)
        {
            var text = resources.GetString(key);
            text.Should().NotBeNullOrWhiteSpace($"{culture}: {key}");
            if (culture != "en") text.Should().NotBe(AppResources.ResourceManager.GetString(key, CultureInfo.InvariantCulture));
            if (key.EndsWith("Format")) text.Should().Contain("{0}");
        }
    }
}
