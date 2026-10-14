// Copyright NEXTGGTECH. Apache License 2.0.

using System.Globalization;
using ASLM.Localization;

namespace ASLM.Models;

/// <summary>
/// Resolves dialog text on demand so open forms can follow application language changes.
/// </summary>
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
public enum DialogFieldKind { Text, Password, Multiline, Integer, Decimal, Boolean, Choice }

/// <summary>
/// Selects one of the shared secondary, primary, or destructive button styles.
/// </summary>
public enum DialogActionStyle { Secondary, Primary, Danger }

/// <summary>
/// Associates a stable choice identifier with its localized display caption.
/// </summary>
public sealed record DialogChoice(string Id, DialogText Label);

/// <summary>
/// Describes an input independently of the UI, addressing values by ID rather than translated labels.
/// </summary>
public sealed class DialogField
{
    private object? _value;
    private IReadOnlyList<DialogChoice> _choices = [];
    public required string Id { get; init; }
    public required DialogText Label { get; set; }
    public DialogFieldKind Kind { get; init; }
    public DialogText? Placeholder { get; init; }
    public bool Required { get; init; }
    public int MaxLength { get; init; } = int.MaxValue;
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public IReadOnlyList<DialogChoice> Choices { get => _choices; init => _choices = value; }
    public bool IsEnabled { get; private set; } = true;
    public DialogText? PresentationError { get; private set; }
    public Func<object?, string?>? Validate { get; init; }
    public event EventHandler? ValueChanged;
    public event EventHandler? PresentationChanged;
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

    /// <summary>
    /// Parses and validates a field using the current culture, returning a localized error if invalid.
    /// </summary>
    internal string? GetError(out object? parsed)
    {
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

/// <summary>
/// Defines a dialog button, its validation policy, and the asynchronous action supplied by its caller.
/// </summary>
public sealed class DialogAction
{
    public required string Id { get; init; }
    public required DialogText Text { get; init; }
    public DialogActionStyle Style { get; init; }
    public bool IsCancel { get; init; }
    public bool IsDefault { get; init; }
    public bool ValidateFields { get; init; } = true;
    public bool CloseOnSuccess { get; init; } = true;
    public Func<IReadOnlyDictionary<string, object?>, CancellationToken, Task>? ExecuteAsync { get; init; }
    public Func<bool>? IsVisible { get; init; }
    public Func<bool>? IsEnabled { get; init; }
}

/// <summary>
/// Describes shared modal content, optional fields, actions, and dismissal behavior.
/// </summary>
public sealed class DialogRequest
{
    public required DialogText Title { get; init; }
    public DialogText? Message { get; init; }
    public IReadOnlyList<DialogField> Fields { get; init; } = [];
    public required IReadOnlyList<DialogAction> Actions { get; init; }
    public bool IsDismissible { get; init; } = true;
    public bool ActionsAsList { get; init; }
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
public sealed record DialogResult(string? ActionId, IReadOnlyDictionary<string, object?> Values)
{
    public bool IsCancelled => ActionId == null;
    public static DialogResult Cancelled { get; } = new(null, new Dictionary<string, object?>());
}
