// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs;

/// <summary>
/// Describes the name and base appearance collected before creating a custom theme.
/// </summary>
internal sealed class ThemeCreationForm
{
    public IReadOnlyList<DialogField> Fields { get; }

    /// <summary>
    /// Creates localized fields while keeping the persisted appearance identifiers untranslated.
    /// </summary>
    public ThemeCreationForm()
    {
        var name = new DialogField { Id = "name", Label = DialogText.Localized(LocalizationKeys.Settings_ThemeNew_PromptMessage),
            Value = L.Get(LocalizationKeys.Settings_ThemeNew_DefaultName), Required = true };
        var appearance = new DialogField { Id = "baseAppearance", Label = DialogText.Localized(LocalizationKeys.Settings_ThemeEditor_Base),
            Kind = DialogFieldKind.Choice, Value = "dark", Required = true,
            Choices = [new("dark", DialogText.Localized(LocalizationKeys.Settings_Personalization_InheritDark)),
                new("light", DialogText.Localized(LocalizationKeys.Settings_Personalization_InheritLight))] };
        Fields = [name, appearance];
    }

    /// <summary>
    /// Builds a detached theme that inherits its base palette; the store assigns its ID on save.
    /// </summary>
    public CustomTheme BuildTheme(IReadOnlyDictionary<string, object?> values) => new()
    {
        Name = values["name"]!.ToString()!.Trim(),
        BaseAppearance = values["baseAppearance"]!.ToString()!
    };
}
