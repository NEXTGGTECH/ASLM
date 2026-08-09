// Copyright NEXTGGTECH. Apache License 2.0.

using System.Windows.Input;

namespace ASLM.Services.Internal
{
    /// <summary>
    /// Presents one editable custom-theme palette value to the shared XAML row template.
    /// </summary>
    public sealed class ThemeColorItemViewModel : SettingsBindableObject
    {
        private readonly Func<ThemeColorItemViewModel, Task> _pickAsync;
        private readonly Action<ThemeColorItemViewModel> _clear;
        private string? _hex;
        private Color _swatchColor;
        private Color _swatchStroke;

        /// <summary>
        /// Creates one palette row and connects its generic commands to page-level theme behavior.
        /// </summary>
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

        public string Key { get; }
        public Color FallbackColor { get; }
        public string PickText { get; }
        public string ClearText { get; }
        public string DisplayValue => string.IsNullOrWhiteSpace(_hex) ? "—" : _hex;
        public Color SwatchColor => _swatchColor;
        public Color SwatchStroke => _swatchStroke;
        public ICommand PickCommand { get; }
        public ICommand ClearCommand { get; }

        /// <summary>
        /// Replaces the displayed palette override and refreshes its swatch.
        /// </summary>
        public void SetHex(string? hex)
        {
            _hex = string.IsNullOrWhiteSpace(hex) ? null : hex;
            _swatchColor = _hex != null && ThemePaletteResolver.TryParseHex(_hex, out var parsed)
                ? parsed
                : FallbackColor;
            _swatchStroke = ThemePaletteResolver.SwatchContrastStroke(_swatchColor);
            RaisePropertyChanged(nameof(DisplayValue));
            RaisePropertyChanged(nameof(SwatchColor));
            RaisePropertyChanged(nameof(SwatchStroke));
        }

        /// <summary>
        /// Opens the page-owned color picker without coupling the template to navigation services.
        /// </summary>
        private async void ExecutePick()
        {
            await _pickAsync(this);
        }

        /// <summary>
        /// Clears the current palette override through the page-owned theme draft.
        /// </summary>
        private void ExecuteClear()
        {
            _clear(this);
        }
    }
}
