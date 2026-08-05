// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Graphics;

namespace ASLM.Pages
{
    /// <summary>
    /// Modal color picker (360×240 card) on the same dimmed overlay resource as Settings and Downloads.
    /// HS plane: the saturation/hue field depends only on brightness (<see cref="_value"/>) and size, so it is painted on a
    /// bottom <see cref="GraphicsView"/> that is not invalidated on pointer moves. The selector ring is a top layer
    /// invalidated on every move for smooth tracking. Value/alpha sliders are not written on every HS move; hex/RGB
    /// entries and preview update live while dragging.
    /// </summary>
    public partial class ThemeColorPickerView : ContentView, ILocalizable
    {
        private readonly TaskCompletionSource<Color?> _completion = new();
        private Page? _modalHostPage;
        private double _hue;
        private double _saturation = 1;
        private double _value = 1;
        private double _alpha = 1;
        private bool _hsPointerDown;
        private bool _suppressSync;
        private readonly AppLocalizationService _localization;


        // Initialization

        /// <summary>
        /// Creates the picker, wires controls, and seeds HSV state from the initial color.
        /// </summary>
        public ThemeColorPickerView(Color initial, AppLocalizationService localization)
        {
            _localization = localization;
            InitializeComponent();
            LocalizableAttach.Hook(this, _localization, this);

            RgbToHsv(initial, out _hue, out _saturation, out _value);
            _alpha = initial.Alpha;

            HsGradient.Drawable = new HsGradientDrawable(this);
            HsCursor.Drawable = new HsCursorDrawable(this);

            HsPlaneHost.SizeChanged += (_, _) =>
            {
                HsGradient.Invalidate();
                HsCursor.Invalidate();
            };

            var ptr = new PointerGestureRecognizer();
            ptr.PointerPressed += OnHsPointerPressed;
            ptr.PointerMoved += OnHsPointerMoved;
            ptr.PointerReleased += OnHsPointerReleased;
            HsCursor.GestureRecognizers.Add(ptr);

            ValueSlider.ValueChanged += (_, _) =>
            {
                if (_suppressSync)
                {
                    return;
                }

                _value = ValueSlider.Value;
                HsGradient.Invalidate();
                HsCursor.Invalidate();
                SyncFromHsv();
            };

            AlphaSlider.ValueChanged += (_, _) =>
            {
                if (_suppressSync)
                {
                    return;
                }

                _alpha = AlphaSlider.Value;
                SyncFromHsv();
            };

            HexEntry.TextChanged += OnHexTextChanged;
            REntry.TextChanged += OnRgbTextChanged;
            GEntry.TextChanged += OnRgbTextChanged;
            BEntry.TextChanged += OnRgbTextChanged;

            ApplyFooterButtonStyle(DoneButton, isPrimary: true);
            ApplyFooterButtonStyle(CancelButton, isPrimary: false);
            DoneButton.Clicked += async (_, _) => await CompleteAsync(GetCurrentColor());
            CancelButton.Clicked += async (_, _) => await CompleteAsync(null);

            ValueSlider.Value = _value;
            AlphaSlider.Value = _alpha;
            SyncFromHsv();
            HsGradient.Invalidate();
            HsCursor.Invalidate();
        }


        // Localization

        /// <summary>
        /// Applies localized strings to picker labels and footer buttons.
        /// </summary>
        public void ApplyLocalization()
        {
            TitleLabel.Text = L.Get(LocalizationKeys.ThemeColorPicker_CustomColor);
            BrightnessLabel.Text = L.Get(LocalizationKeys.ThemeColorPicker_Brightness);
            OpacityLabel.Text = L.Get(LocalizationKeys.ThemeColorPicker_Opacity);
            HexEntry.Placeholder = L.Get(LocalizationKeys.ThemeColorPicker_HexPlaceholder);
            DoneButton.Text = L.Get(LocalizationKeys.Common_Done);
            CancelButton.Text = L.Get(LocalizationKeys.ThemeColorPicker_Cancel);
        }


        // Modal presentation

        /// <summary>
        /// Returns a task that completes when the user confirms or cancels the picker.
        /// </summary>
        public Task<Color?> WaitForResultAsync() => _completion.Task;

        /// <summary>
        /// Shows the picker modally and returns the chosen color, or <c>null</c> when cancelled.
        /// </summary>
        public static async Task<Color?> PickAsync(Color initial)
        {
            var host = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (host == null)
            {
                return null;
            }

            var localization = Application.Current?.Handler?.MauiContext?.Services.GetRequiredService<AppLocalizationService>();
            var picker = new ThemeColorPickerView(initial, localization!);
            var modal = new ContentPage
            {
                BackgroundColor = Colors.Transparent,
                Content = picker
            };
            NavigationPage.SetHasNavigationBar(modal, false);
            picker.AttachModalHost(modal);

            await host.Navigation.PushModalAsync(modal);
            return await picker.WaitForResultAsync().ConfigureAwait(true);
        }


        // Modal host

        /// <summary>
        /// Associates the modal host page and completes with <c>null</c> if it disappears early.
        /// </summary>
        internal void AttachModalHost(Page modalHost)
        {
            _modalHostPage = modalHost;
            modalHost.Disappearing += OnModalHostDisappearing;
        }

        /// <summary>
        /// Completes the picker with <c>null</c> when the modal host is dismissed externally.
        /// </summary>
        private void OnModalHostDisappearing(object? sender, EventArgs e)
        {
            if (!_completion.Task.IsCompleted)
            {
                _completion.TrySetResult(null);
            }
        }


        // Overlay gestures

        /// <summary>
        /// Cancels the picker when the dimmed backdrop is tapped.
        /// </summary>
        private void OnBackdropTapped(object? sender, TappedEventArgs e)
        {
            _ = CompleteAsync(null);
        }

        /// <summary>
        /// Swallows taps on the card so they do not reach the backdrop handler.
        /// </summary>
        private static void OnCardTapped(object? sender, TappedEventArgs e)
        {
            // Swallow taps so backdrop does not receive them (same pattern as SettingsView dialog).
        }


        // HS plane pointer

        /// <summary>
        /// Begins hue/saturation selection from a pointer press on the HS plane.
        /// </summary>
        private void OnHsPointerPressed(object? sender, PointerEventArgs e)
        {
            _hsPointerDown = true;
            ApplyHsFromPointer(e, syncTextFields: true);
        }

        /// <summary>
        /// Updates hue/saturation while the pointer moves over the HS plane.
        /// </summary>
        private void OnHsPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_hsPointerDown)
            {
                return;
            }

            ApplyHsFromPointer(e, syncTextFields: false);
        }

        /// <summary>
        /// Ends hue/saturation selection and syncs text fields on pointer release.
        /// </summary>
        private void OnHsPointerReleased(object? sender, PointerEventArgs e)
        {
            if (!_hsPointerDown)
            {
                return;
            }

            _hsPointerDown = false;
            ApplyHsFromPointer(e, syncTextFields: true);
        }

        /// <summary>
        /// Maps a pointer position on the HS plane to hue and saturation, then refreshes the UI.
        /// </summary>
        private void ApplyHsFromPointer(PointerEventArgs e, bool syncTextFields)
        {
            var pt = e.GetPosition(HsPlaneHost);
            if (!pt.HasValue)
            {
                return;
            }

            var w = Math.Max(1, HsPlaneHost.Width);
            var h = Math.Max(1, HsPlaneHost.Height);
            _hue = Math.Clamp(pt.Value.X / w, 0, 1);
            _saturation = Math.Clamp(1 - pt.Value.Y / h, 0, 1);
            HsCursor.Invalidate();
            if (syncTextFields)
            {
                SyncFromHsv();
            }
            else
            {
                ApplyPreviewColor(GetCurrentColor());
                SyncHexRgbEntriesFromHsv();
            }
        }


        // Color synchronization

        /// <summary>
        /// Updates hex and RGB entries from current HSV without touching sliders (HS drag does not change V/A).
        /// </summary>
        private void SyncHexRgbEntriesFromHsv()
        {
            var c = GetCurrentColor();
            _suppressSync = true;
            HexEntry.Text = ThemePaletteResolver.ToHex(c);
            REntry.Text = ((int)(c.Red * 255)).ToString();
            GEntry.Text = ((int)(c.Green * 255)).ToString();
            BEntry.Text = ((int)(c.Blue * 255)).ToString();
            _suppressSync = false;
        }

        /// <summary>
        /// Builds the current color from HSV and alpha components.
        /// </summary>
        private Color GetCurrentColor() => HsvToColor(_hue, _saturation, _value, _alpha);

        /// <summary>
        /// Updates the preview swatch fill and contrast stroke for the given color.
        /// </summary>
        private void ApplyPreviewColor(Color c)
        {
            PreviewFill.BackgroundColor = c;
            PreviewBorder.Stroke = ThemePaletteResolver.SwatchContrastStroke(c);
        }

        /// <summary>
        /// Syncs preview, hex/RGB entries, and value/alpha sliders from the current HSV state.
        /// </summary>
        private void SyncFromHsv()
        {
            var c = GetCurrentColor();
            _suppressSync = true;
            ApplyPreviewColor(c);
            HexEntry.Text = ThemePaletteResolver.ToHex(c);
            REntry.Text = ((int)(c.Red * 255)).ToString();
            GEntry.Text = ((int)(c.Green * 255)).ToString();
            BEntry.Text = ((int)(c.Blue * 255)).ToString();
            ValueSlider.Value = _value;
            AlphaSlider.Value = _alpha;
            _suppressSync = false;
        }


        // Completion

        /// <summary>
        /// Completes the picker, pops the modal host, and delivers the chosen color or <c>null</c>.
        /// </summary>
        private async Task CompleteAsync(Color? result)
        {
            if (_completion.Task.IsCompleted)
            {
                return;
            }

            _hsPointerDown = false;
            _completion.TrySetResult(result);

            var host = _modalHostPage;
            if (host != null)
            {
                host.Disappearing -= OnModalHostDisappearing;
                _modalHostPage = null;
            }

            if (host?.Navigation.ModalStack.Count > 0 &&
                ReferenceEquals(host.Navigation.ModalStack[^1], host))
            {
                await host.Navigation.PopModalAsync();
            }
        }


        // Text input

        /// <summary>
        /// Parses hex input and updates HSV, preview, and RGB fields when valid.
        /// </summary>
        private void OnHexTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_suppressSync)
            {
                return;
            }

            var text = e.NewTextValue?.Trim() ?? string.Empty;
            if (!ThemePaletteResolver.TryParseHex(text, out var c))
            {
                return;
            }

            RgbToHsv(c, out _hue, out _saturation, out _value);
            _alpha = c.Alpha;
            HsGradient.Invalidate();
            HsCursor.Invalidate();
            _suppressSync = true;
            ApplyPreviewColor(c);
            REntry.Text = ((int)(c.Red * 255)).ToString();
            GEntry.Text = ((int)(c.Green * 255)).ToString();
            BEntry.Text = ((int)(c.Blue * 255)).ToString();
            ValueSlider.Value = _value;
            AlphaSlider.Value = _alpha;
            _suppressSync = false;
        }

        /// <summary>
        /// Parses RGB entries and updates HSV, preview, and hex when all components are valid.
        /// </summary>
        private void OnRgbTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_suppressSync)
            {
                return;
            }

            if (!int.TryParse(REntry.Text, out var ri) ||
                !int.TryParse(GEntry.Text, out var gi) ||
                !int.TryParse(BEntry.Text, out var bi))
            {
                return;
            }

            _alpha = AlphaSlider.Value;
            ri = Math.Clamp(ri, 0, 255);
            gi = Math.Clamp(gi, 0, 255);
            bi = Math.Clamp(bi, 0, 255);
            var ai = (int)Math.Round(_alpha * 255);
            var withAlpha = Color.FromRgba(ri, gi, bi, ai);
            RgbToHsv(withAlpha, out _hue, out _saturation, out _value);
            ValueSlider.Value = _value;
            HsGradient.Invalidate();
            HsCursor.Invalidate();
            _suppressSync = true;
            ApplyPreviewColor(withAlpha);
            HexEntry.Text = ThemePaletteResolver.ToHex(withAlpha);
            _suppressSync = false;
        }


        // UI styling

        /// <summary>
        /// Applies the shared settings footer button style for primary or secondary actions.
        /// </summary>
        private static void ApplyFooterButtonStyle(Button button, bool isPrimary)
        {
            var key = isPrimary ? "SettingsFooterPrimaryButtonStyle" : "SettingsFooterButtonStyle";
            if (Application.Current?.Resources.TryGetValue(key, out var st) == true && st is Style style)
            {
                if (ReferenceEquals(button.Style, style))
                {
                    button.Style = null;
                }

                button.Style = style;
            }
        }


        // Color conversion

        /// <summary>
        /// Converts an RGBA color to normalized hue, saturation, and value components.
        /// </summary>
        private static void RgbToHsv(Color color, out double h, out double s, out double v)
        {
            var r = color.Red;
            var g = color.Green;
            var b = color.Blue;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            v = max;
            s = max < 1e-8 ? 0 : delta / max;

            if (delta < 1e-8)
            {
                h = 0;
                return;
            }

            double hh;
            if (Math.Abs(max - r) < 1e-8)
            {
                hh = ((g - b) / delta % 6 + 6) % 6;
            }
            else if (Math.Abs(max - g) < 1e-8)
            {
                hh = (b - r) / delta + 2;
            }
            else
            {
                hh = (r - g) / delta + 4;
            }

            h = hh / 6;
        }

        /// <summary>
        /// Converts normalized HSV and alpha components to an RGBA color.
        /// </summary>
        private static Color HsvToColor(double h, double s, double v, double a)
        {
            h = (h % 1 + 1) % 1;
            var hh = h * 6;
            var i = (int)Math.Floor(hh);
            var f = hh - i;
            var p = v * (1 - s);
            var q = v * (1 - f * s);
            var t = v * (1 - (1 - f) * s);

            double r, g, b;
            switch (i % 6)
            {
                case 0: r = v; g = t; b = p; break;
                case 1: r = q; g = v; b = p; break;
                case 2: r = p; g = v; b = t; break;
                case 3: r = p; g = q; b = v; break;
                case 4: r = t; g = p; b = v; break;
                default: r = v; g = p; b = q; break;
            }

            return new Color((float)r, (float)g, (float)b, (float)a);
        }


        // Drawables

        /// <summary>
        /// HS field at fixed value V; redraw only when brightness or size changes.
        /// </summary>
        private sealed class HsGradientDrawable : IDrawable
        {
            private readonly ThemeColorPickerView _owner;

            public HsGradientDrawable(ThemeColorPickerView owner) => _owner = owner;

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                const int step = 3;
                var w = dirtyRect.Width;
                var h = dirtyRect.Height;
                var v = (float)_owner._value;
                for (var y = 0; y < h; y += step)
                {
                    for (var x = 0; x < w; x += step)
                    {
                        var hue = x / w;
                        var sat = 1 - y / h;
                        canvas.FillColor = HsvToColor(hue, sat, v, 1f);
                        canvas.FillRectangle(x, y, step, step);
                    }
                }
            }
        }

        /// <summary>
        /// Selector ring only; redraws on every hue/sat change.
        /// </summary>
        private sealed class HsCursorDrawable : IDrawable
        {
            private readonly ThemeColorPickerView _owner;

            public HsCursorDrawable(ThemeColorPickerView owner) => _owner = owner;

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                var w = dirtyRect.Width;
                var h = dirtyRect.Height;
                var px = (float)(_owner._hue * w);
                var py = (float)((1 - _owner._saturation) * h);
                canvas.StrokeColor = Colors.White;
                canvas.StrokeSize = 2;
                canvas.DrawCircle(px, py, 7);
                canvas.StrokeColor = Colors.Black;
                canvas.StrokeSize = 1;
                canvas.DrawCircle(px, py, 7);
            }
        }
    }
}
