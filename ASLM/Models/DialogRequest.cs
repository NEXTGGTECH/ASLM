// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Localization;

namespace ASLM.Models
{
    // Dialog text and choices

    /// <summary>
    /// Resolves dialog text on demand so open forms can follow application language changes.
    /// </summary>
    /// <param name="Resolve">Produces the current text each time the dialog is rendered.</param>
    public sealed record DialogText(Func<string> Resolve)
    {
        /// <summary>
        /// Creates a localized text source while preserving format arguments until rendering.
        /// </summary>
        public static DialogText Localized(string key, params object[] arguments) => new(() => L.Get(key, arguments));

        /// <summary>
        /// Wraps caller-supplied text that does not require another resource lookup.
        /// </summary>
        public static implicit operator DialogText(string text) => new(() => text);
    }

    /// <summary>
    /// Identifies the editor and validation rules used for a dialog field.
    /// </summary>
    public enum DialogFieldKind
    {
        /// <summary>
        /// Single-line text entry.
        /// </summary>
        Text,

        /// <summary>
        /// Single-line text entry that masks the typed characters.
        /// </summary>
        Password,

        /// <summary>
        /// Multi-line text editor.
        /// </summary>
        Multiline,

        /// <summary>
        /// Whole number entry; the value is parsed to <see cref="long"/> and checked against the field range.
        /// </summary>
        Integer,

        /// <summary>
        /// Decimal number entry; the value is parsed to <see cref="decimal"/> and checked against the field range.
        /// </summary>
        Decimal,

        /// <summary>
        /// On/off switch whose value is a <see cref="bool"/>.
        /// </summary>
        Boolean,

        /// <summary>
        /// Picker whose value is the ID of one of the field's choices.
        /// </summary>
        Choice
    }

    /// <summary>
    /// Selects one of the shared secondary, primary, or destructive button styles.
    /// </summary>
    public enum DialogActionStyle
    {
        /// <summary>
        /// Neutral button for cancel and other non-committing actions.
        /// </summary>
        Secondary,

        /// <summary>
        /// Emphasized button for the main, committing action.
        /// </summary>
        Primary,

        /// <summary>
        /// Warning-colored button for destructive actions.
        /// </summary>
        Danger
    }

    /// <summary>
    /// Associates a stable choice identifier with its localized display caption.
    /// </summary>
    /// <param name="Id">Stable identifier stored as the field value; it does not change with the UI language.</param>
    /// <param name="Label">Caption shown in the picker.</param>
    public sealed record DialogChoice(string Id, DialogText Label);


    // Dialog fields

    /// <summary>
    /// Describes an input independently of the UI, addressing values by ID rather than translated labels.
    /// </summary>
    public sealed class DialogField
    {
        // Fields

        /// <summary>
        /// Backing field of <see cref="Value"/>.
        /// </summary>
        private object? _value;

        /// <summary>
        /// Backing field of <see cref="Choices"/>; replaced at runtime by <see cref="UpdateChoices"/>.
        /// </summary>
        private IReadOnlyList<DialogChoice> _choices = [];


        // Field definition

        /// <summary>
        /// Stable identifier under which the value is reported to actions and validation errors.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Caption shown next to the editor; <see cref="UpdateChoices"/> can replace it while the dialog is open.
        /// </summary>
        public required DialogText Label { get; set; }

        /// <summary>
        /// Editor type and validation rules of the field.
        /// </summary>
        public DialogFieldKind Kind { get; init; }

        /// <summary>
        /// Optional hint shown while a text editor is empty.
        /// </summary>
        public DialogText? Placeholder { get; init; }

        /// <summary>
        /// Whether a blank value is rejected during validation.
        /// </summary>
        public bool Required { get; init; }

        /// <summary>
        /// Maximum text length accepted by the editor and validation; unlimited by default.
        /// </summary>
        public int MaxLength { get; init; } = int.MaxValue;

        /// <summary>
        /// Smallest accepted number for <see cref="DialogFieldKind.Integer"/> and <see cref="DialogFieldKind.Decimal"/>
        /// fields, or null for no lower bound.
        /// </summary>
        public decimal? Minimum { get; init; }

        /// <summary>
        /// Largest accepted number for <see cref="DialogFieldKind.Integer"/> and <see cref="DialogFieldKind.Decimal"/>
        /// fields, or null for no upper bound.
        /// </summary>
        public decimal? Maximum { get; init; }

        /// <summary>
        /// Options offered by a <see cref="DialogFieldKind.Choice"/> field.
        /// </summary>
        public IReadOnlyList<DialogChoice> Choices { get => _choices; init => _choices = value; }


        // Presentation state

        /// <summary>
        /// Whether the editor accepts input; dependent selectors disable it while their options are loading.
        /// </summary>
        public bool IsEnabled { get; private set; } = true;

        /// <summary>
        /// Error shown next to the editor independently of validation, for example when loading options failed.
        /// </summary>
        public DialogText? PresentationError { get; private set; }


        // Validation and change notification

        /// <summary>
        /// Optional custom validator that receives the parsed value and returns an error message, or null when valid.
        /// </summary>
        public Func<object?, string?>? Validate { get; init; }

        /// <summary>
        /// Raised when <see cref="Value"/> changes so the view can mirror it.
        /// </summary>
        public event EventHandler? ValueChanged;

        /// <summary>
        /// Raised when <see cref="UpdateChoices"/> changed the caption, options, enabled state or presentation error.
        /// </summary>
        public event EventHandler? PresentationChanged;

        /// <summary>
        /// Current value as entered by the user; its runtime type depends on <see cref="Kind"/> until it is parsed.
        /// </summary>
        public object? Value
        {
            get => _value;
            set
            {
                if (Equals(_value, value)) return;
                _value = value;
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }


        // Dynamic options

        /// <summary>
        /// Replaces dependent selector options and their caption without closing the dialog.
        /// </summary>
        public void UpdateChoices(DialogText label, IReadOnlyList<DialogChoice> choices,
            bool isEnabled = true, DialogText? error = null)
        {
            Label = label;
            _choices = choices;
            IsEnabled = isEnabled;
            PresentationError = error;
            PresentationChanged?.Invoke(this, EventArgs.Empty);
        }


        // Validation

        /// <summary>
        /// Parses and validates a field using the current culture, returning a localized error if invalid.
        /// </summary>
        /// <param name="parsed">
        /// Receives the value converted to its typed form, or the raw value when parsing does not apply.
        /// </param>
        internal string? GetError(out object? parsed)
        {
            // Blank handling: required fields fail, other blank values skip the type checks.
            parsed = Value;
            var text = Convert.ToString(Value, CultureInfo.CurrentCulture) ?? string.Empty;
            if (Required && string.IsNullOrWhiteSpace(text)) return L.Get(LocalizationKeys.Dialog_Required);
            if (string.IsNullOrWhiteSpace(text) && Kind != DialogFieldKind.Boolean) return Validate?.Invoke(Value);
            if (text.Length > MaxLength) return L.Get(LocalizationKeys.Dialog_InvalidValue);

            // Numeric keyboards are only input hints; enforce the actual type and range here.
            if (Kind == DialogFieldKind.Integer)
            {
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
                    return L.Get(LocalizationKeys.Dialog_InvalidValue);
                parsed = number;
                if (number < Minimum || number > Maximum) return L.Get(LocalizationKeys.Dialog_InvalidValue);
            }

            if (Kind == DialogFieldKind.Decimal)
            {
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number))
                    return L.Get(LocalizationKeys.Dialog_InvalidValue);
                parsed = number;
                if (number < Minimum || number > Maximum) return L.Get(LocalizationKeys.Dialog_InvalidValue);
            }

            // Choice IDs remain stable across locale changes; custom validation receives parsed values.
            if (Kind == DialogFieldKind.Choice && !Choices.Any(c => c.Id == text)) return L.Get(LocalizationKeys.Dialog_InvalidValue);
            if (Kind == DialogFieldKind.Boolean && Value is not bool) return L.Get(LocalizationKeys.Dialog_InvalidValue);
            return Validate?.Invoke(parsed);
        }
    }


    // Dialog actions

    /// <summary>
    /// Defines a dialog button, its validation policy, and the asynchronous action supplied by its caller.
    /// </summary>
    public sealed class DialogAction
    {
        /// <summary>
        /// Stable identifier reported in <see cref="DialogResult.ActionId"/> when this action completes the dialog.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Button caption.
        /// </summary>
        public required DialogText Text { get; init; }

        /// <summary>
        /// Visual style of the button.
        /// </summary>
        public DialogActionStyle Style { get; init; }

        /// <summary>
        /// Whether invoking the action cancels the dialog without validation or callback.
        /// </summary>
        public bool IsCancel { get; init; }

        /// <summary>
        /// Whether the action is triggered by Enter and receives the initial focus when it is visible.
        /// </summary>
        public bool IsDefault { get; init; }

        /// <summary>
        /// Whether the fields are validated before the action runs; actions that skip it receive unvalidated values.
        /// </summary>
        public bool ValidateFields { get; init; } = true;

        /// <summary>
        /// Whether the dialog completes with this action after <see cref="ExecuteAsync"/> succeeds.
        /// </summary>
        public bool CloseOnSuccess { get; init; } = true;

        /// <summary>
        /// Optional callback that receives the field values by ID and a token cancelled when the dialog ends.
        /// An exception it throws is shown as the dialog's action error instead of closing the dialog.
        /// </summary>
        public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task>? ExecuteAsync { get; init; }

        /// <summary>
        /// Optional predicate deciding whether the button is shown; null means always visible.
        /// </summary>
        public Func<bool>? IsVisible { get; init; }

        /// <summary>
        /// Optional predicate deciding whether the button can be used; null means always enabled.
        /// </summary>
        public Func<bool>? IsEnabled { get; init; }
    }


    // Dialog request and result

    /// <summary>
    /// Describes shared modal content, optional fields, actions, and dismissal behavior.
    /// </summary>
    public sealed class DialogRequest
    {
        // Content

        /// <summary>
        /// Dialog heading.
        /// </summary>
        public required DialogText Title { get; init; }

        /// <summary>
        /// Optional explanatory text shown below the title.
        /// </summary>
        public DialogText? Message { get; init; }

        /// <summary>
        /// Input fields shown in order; empty for message-only dialogs.
        /// </summary>
        public IReadOnlyList<DialogField> Fields { get; init; } = [];

        /// <summary>
        /// Buttons shown in order; at least one is required.
        /// </summary>
        public required IReadOnlyList<DialogAction> Actions { get; init; }

        /// <summary>
        /// Whether the user may close the dialog without choosing an action.
        /// </summary>
        public bool IsDismissible { get; init; } = true;

        /// <summary>
        /// Whether the buttons are stacked as full-width rows instead of one compact button row.
        /// </summary>
        public bool ActionsAsList { get; init; }


        // Validation

        /// <summary>
        /// Rejects ambiguous field or action identifiers before values can be routed to callbacks.
        /// </summary>
        internal void Validate()
        {
            if (Actions.Count == 0 || Actions.Select(a => a.Id).Distinct().Count() != Actions.Count ||
                Fields.Select(f => f.Id).Distinct().Count() != Fields.Count ||
                Actions.Any(a => string.IsNullOrWhiteSpace(a.Id)) || Fields.Any(f => string.IsNullOrWhiteSpace(f.Id)))
                throw new ArgumentException("Dialogs require uniquely identified fields and at least one uniquely identified action.");
        }
    }

    /// <summary>
    /// Returns the accepted action and its values, or a cancellation result containing no input data.
    /// </summary>
    /// <param name="ActionId">
    /// Identifier of the action that completed the dialog, or null when it was cancelled.
    /// </param>
    /// <param name="Values">Field values by field ID at the moment the action ran.</param>
    public sealed record DialogResult(string? ActionId, IReadOnlyDictionary<string, object?> Values)
    {
        /// <summary>
        /// Whether the dialog ended without an accepted action.
        /// </summary>
        public bool IsCancelled => ActionId == null;

        /// <summary>
        /// Shared result for cancelled dialogs; it has no action and no values.
        /// </summary>
        public static DialogResult Cancelled { get; } = new(null, new Dictionary<string, object?>());
    }
}
