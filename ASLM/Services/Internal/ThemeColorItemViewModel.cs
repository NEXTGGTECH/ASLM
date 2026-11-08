// Copyright NEXTGGTECH. Apache License 2.0.

using System.Windows.Input;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Presents one editable custom-theme palette value to the shared XAML row template.
    /// </summary>
    public sealed class ThemeColorItemViewModel : SettingsBindableObject
    {
        // Fields

        /// <summary>
        /// Page-owned callback that lets the user pick a new color for this row.
        /// </summary>
        private readonly Func<ThemeColorItemViewModel, Task> _pickAsync;

        /// <summary>
        /// Page-owned callback that removes the override of this row from the theme draft.
        /// </summary>
        private readonly Action<ThemeColorItemViewModel> _clear;

        /// <summary>
        /// Hex value of the override, or null when the row has none and uses the fallback color.
        /// </summary>
        private string? _hex;

        /// <summary>
        /// Backing field of <see cref="SwatchColor"/>.
        /// </summary>
        private Color _swatchColor;

        /// <summary>
        /// Backing field of <see cref="SwatchStroke"/>.
        /// </summary>
        private Color _swatchStroke;


        // Initialization

        /// <summary>
        /// Creates one palette row and connects its generic commands to page-level theme behavior.
        /// </summary>
        /// <param name="key">Palette key that the row edits.</param>
        /// <param name="hex">Current override as a hex color, or null when the key is not overridden.</param>
        /// <param name="fallbackColor">Color shown in the swatch while there is no override.</param>
        /// <param name="pickText">Localized label of the pick button.</param>
        /// <param name="clearText">Localized label of the clear button.</param>
        /// <param name="pickAsync">Callback that opens the color picker for this row.</param>
        /// <param name="clear">Callback that clears the override of this row.</param>
        public ThemeColorItemViewModel(
            string key,
            string? hex,
            Color fallbackColor,
            string pickText,
            string clearText,
            Func<ThemeColorItemViewModel, Task> pickAsync,
            Action<ThemeColorItemViewModel> clear)
        {
            Key = key;
            FallbackColor = fallbackColor;
            PickText = pickText;
            ClearText = clearText;
            _pickAsync = pickAsync;
            _clear = clear;
            _swatchColor = fallbackColor;
            _swatchStroke = ThemePaletteResolver.SwatchContrastStroke(fallbackColor);
            PickCommand = new Command(ExecutePick);
            ClearCommand = new Command(ExecuteClear);
            SetHex(hex);
        }


        // Bindable state

        /// <summary>
        /// Palette key whose color this row edits; the row template shows it as the row name.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Color the swatch shows while the key has no valid override.
        /// </summary>
        public Color FallbackColor { get; }

        /// <summary>
        /// Localized label of the pick button.
        /// </summary>
        public string PickText { get; }

        /// <summary>
        /// Localized label of the clear button.
        /// </summary>
        public string ClearText { get; }

        /// <summary>
        /// Hex text of the override, or a dash when the key is not overridden.
        /// </summary>
        public string DisplayValue => string.IsNullOrWhiteSpace(_hex) ? "—" : _hex;

        /// <summary>
        /// Color filled into the row swatch: the parsed override, or <see cref="FallbackColor"/>.
        /// </summary>
        public Color SwatchColor => _swatchColor;

        /// <summary>
        /// Outline color of the swatch, chosen to stay visible against <see cref="SwatchColor"/>.
        /// </summary>
        public Color SwatchStroke => _swatchStroke;


        // Commands

        /// <summary>
        /// Command of the pick button; opens the color picker for this row.
        /// </summary>
        public ICommand PickCommand { get; }

        /// <summary>
        /// Command of the clear button; removes the override of this row.
        /// </summary>
        public ICommand ClearCommand { get; }


        // State changes

        /// <summary>
        /// Replaces the displayed palette override and refreshes its swatch.
        /// </summary>
        public void SetHex(string? hex)
        {
            // An unparsable or missing override falls back to the default color so the swatch never goes blank.
            _hex = string.IsNullOrWhiteSpace(hex) ? null : hex;
            _swatchColor = _hex != null && ThemePaletteResolver.TryParseHex(_hex, out var parsed)
                ? parsed
                : FallbackColor;
            _swatchStroke = ThemePaletteResolver.SwatchContrastStroke(_swatchColor);

            // The computed properties have no setters, so bindings are notified explicitly.
            RaisePropertyChanged(nameof(DisplayValue));
            RaisePropertyChanged(nameof(SwatchColor));
            RaisePropertyChanged(nameof(SwatchStroke));
        }


        // Command handlers

        /// <summary>
        /// Opens the page-owned color picker without coupling the template to navigation services.
        /// </summary>
        private async void ExecutePick()
        {
            await _pickAsync(this);
        }

        /// <summary>
        /// Resets the current palette color through the page-owned theme draft.
        /// </summary>
        private void ExecuteClear()
        {
            _clear(this);
        }
    }
}
