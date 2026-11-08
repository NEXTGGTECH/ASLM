// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using ASLM.Models;

namespace ASLM.Services.Dialogs
{
    /// <summary>
    /// Describes the name and base appearance collected before creating a custom theme.
    /// </summary>
    internal sealed class ThemeCreationForm
    {
        /// <summary>
        /// Input fields shown by the dialog: the theme name and the base appearance (<c>dark</c> or <c>light</c>).
        /// </summary>
        public IReadOnlyList<DialogField> Fields { get; }

        /// <summary>
        /// Creates localized fields while keeping the persisted appearance identifiers untranslated.
        /// </summary>
        public ThemeCreationForm()
        {
            // Name is free text prefilled with a localized default.
            var name = new DialogField { Id = "name", Label = DialogText.Localized(LocalizationKeys.Settings_ThemeNew_PromptMessage),
                Value = L.Get(LocalizationKeys.Settings_ThemeNew_DefaultName), Required = true };

            // Choice IDs are persisted untranslated; only their captions are localized.
            var appearance = new DialogField { Id = "baseAppearance", Label = DialogText.Localized(LocalizationKeys.Settings_ThemeEditor_Base),
                Kind = DialogFieldKind.Choice, Value = "dark", Required = true,
                Choices = [new("dark", DialogText.Localized(LocalizationKeys.Settings_Personalization_Appearance_Dark)),
                    new("light", DialogText.Localized(LocalizationKeys.Settings_Personalization_Appearance_Light))] };

            Fields = [name, appearance];
        }

        /// <summary>
        /// Builds a detached theme that inherits its base palette; the store assigns its ID on save.
        /// </summary>
        /// <param name="values">Validated dialog values keyed by field ID (<c>name</c> and <c>baseAppearance</c>).</param>
        /// <returns>A new custom theme with the trimmed name and the chosen base appearance.</returns>
        public CustomTheme BuildTheme(IReadOnlyDictionary<string, object?> values) => new()
        {
            Name = values["name"]!.ToString()!.Trim(),
            BaseAppearance = values["baseAppearance"]!.ToString()!
        };
    }
}
