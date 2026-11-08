// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls
{
    /// <summary>
    /// Keeps the native switch's track and thumb inside the MAUI hit area and styles both CSS nodes from the app palette.
    /// </summary>
    public sealed class LinuxSwitchHandler : GtkViewHandler<ISwitch, Gtk.Box>
    {
        // Fields

        /// <summary>
        /// Maps switch properties to the native state or appearance update that each one requires.
        /// </summary>
        private static readonly IPropertyMapper<ISwitch, LinuxSwitchHandler> SwitchMapper =
            new PropertyMapper<ISwitch, LinuxSwitchHandler>(ViewMapper)
            {
                [nameof(ISwitch.IsOn)] = (handler, _) => handler.ApplyState(),
                [nameof(ISwitch.TrackColor)] = (handler, _) => handler.ApplyAppearance(),
                [nameof(ISwitch.ThumbColor)] = (handler, _) => handler.ApplyAppearance(),
                [nameof(IView.IsEnabled)] = (handler, view) =>
                {
                    ViewMapper.UpdateProperty(handler, view, nameof(IView.IsEnabled));
                    handler.ApplyAppearance();
                }
            };

        /// <summary>
        /// CSS class unique to this switch instance, so its provider styles only this control.
        /// </summary>
        private readonly string _styleClass = "aslm-switch-" + Guid.NewGuid().ToString("N");

        /// <summary>
        /// Native switch inside the container, or null while the handler is disconnected.
        /// </summary>
        private Gtk.Switch? _switch;

        /// <summary>
        /// Display-wide CSS provider that holds this switch's rules, or null while disconnected.
        /// </summary>
        private Gtk.CssProvider? _style;

        /// <summary>
        /// Display the provider was registered on, needed to unregister it again.
        /// </summary>
        private Gdk.Display? _display;

        /// <summary>
        /// CSS text last loaded into the provider, used to skip reloading identical rules.
        /// </summary>
        private string? _lastCss;

        /// <summary>
        /// Set while the handler changes the native switch itself, so the resulting signal is not echoed back to MAUI.
        /// </summary>
        private bool _syncing;


        // Construction and lifetime

        /// <summary>
        /// Uses the ordinary MAUI switch contract with a GTK container for its larger clickable layout slot.
        /// </summary>
        public LinuxSwitchHandler() : base(SwitchMapper) { }

        /// <summary>
        /// Centers the visual track without stretching the thumb to the full height of a settings row.
        /// </summary>
        protected override Gtk.Box CreatePlatformView()
        {
            // The box fills the MAUI layout slot, so the whole slot stays clickable.
            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            box.SetHalign(Gtk.Align.Fill);
            box.SetValign(Gtk.Align.Fill);

            // The switch itself stays centered at its own size and carries the unique style class.
            _switch = Gtk.Switch.New();
            _switch.AddCssClass(_styleClass);
            _switch.SetHalign(Gtk.Align.Center);
            _switch.SetValign(Gtk.Align.Center);
            box.Append(_switch);
            return box;
        }

        /// <summary>
        /// Connects native toggling and palette changes while this switch belongs to a live handler.
        /// </summary>
        protected override void ConnectHandler(Gtk.Box platformView)
        {
            // "state-set" reports a toggle; map and unmap decide when palette changes are followed.
            base.ConnectHandler(platformView);
            _switch!.OnStateSet += OnStateSet;
            platformView.OnMap += OnMapped;
            platformView.OnUnmap += OnUnmapped;
            _display = platformView.GetDisplay();
            _style = Gtk.CssProvider.New();

            // A root-only provider does not style the slider widget. Scope one display provider to this switch
            // so its child node receives the same palette, without changing any other native control.
            Gtk.StyleContext.AddProviderForDisplay(_display, _style, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION + 1);
        }

        /// <summary>
        /// Removes palette subscriptions and the scoped provider before native children are released.
        /// </summary>
        protected override void DisconnectHandler(Gtk.Box platformView)
        {
            // Stop listening to the static palette event and to native signals first.
            ThemeService.PaletteApplied -= OnPaletteApplied;
            platformView.OnMap -= OnMapped;
            platformView.OnUnmap -= OnUnmapped;
            if (_switch != null) _switch.OnStateSet -= OnStateSet;

            // The provider is registered display-wide, so it must be removed explicitly or it would leak.
            if (_style != null)
            {
                if (_display != null) Gtk.StyleContext.RemoveProviderForDisplay(_display, _style);
                _style.Dispose();
            }

            _style = null;
            _display = null;
            _switch = null;
            _lastCss = null;
            base.DisconnectHandler(platformView);
        }


        // State and appearance

        /// <summary>
        /// Refreshes a remapped control and listens to palette changes only while it is displayed.
        /// </summary>
        private void OnMapped(Gtk.Widget sender, EventArgs args)
        {
            ThemeService.PaletteApplied -= OnPaletteApplied;
            ThemeService.PaletteApplied += OnPaletteApplied;
            ApplyAppearance();
        }

        /// <summary>
        /// Prevents a static palette event from retaining switches belonging to closed settings sections.
        /// </summary>
        private void OnUnmapped(Gtk.Widget sender, EventArgs args) => ThemeService.PaletteApplied -= OnPaletteApplied;

        /// <summary>
        /// Publishes native user changes without echoing a programmatic IsOn update back into its binding.
        /// </summary>
        private bool OnStateSet(Gtk.Switch sender, Gtk.Switch.StateSetSignalArgs args)
        {
            if (!_syncing) VirtualView.IsOn = args.State;
            return false;
        }

        /// <summary>
        /// Updates GTK state from MAUI, preserving one toggle event per user or programmatic change.
        /// </summary>
        private void ApplyState()
        {
            if (_switch == null) return;

            // Suppress the echo: OnStateSet ignores the signal raised by this programmatic change.
            _syncing = true;
            try { if (_switch.GetActive() != VirtualView.IsOn) _switch.SetActive(VirtualView.IsOn); }
            finally { _syncing = false; }
            ApplyAppearance();
        }

        /// <summary>
        /// Refreshes native styling after built-in or custom themes replace palette resources.
        /// </summary>
        private void OnPaletteApplied()
        {
            if (((IElementHandler)this).VirtualView is not VisualElement view) return;
            view.Dispatcher.Dispatch(() => { if (_switch != null) ApplyAppearance(); });
        }

        /// <summary>
        /// Styles enabled, disabled, on and off states independently of the desktop's GTK theme.
        /// </summary>
        private void ApplyAppearance()
        {
            if (_style == null) return;

            // The track color depends on the on/off state, the thumb color on the explicit setting or the palette.
            var track = VirtualView.IsOn ? VirtualView.TrackColor ?? IconTintHelper.ResolvePaletteColor("ActionBlue")
                : IconTintHelper.ResolvePaletteColor("SystemGray4");
            var thumb = VirtualView.ThumbColor ?? IconTintHelper.ResolvePaletteColor("White");
            var css = FormattableString.Invariant($$"""
                switch.{{_styleClass}} {
                    min-width: 40px; min-height: 20px; padding: 2px; margin: 0;
                    border: 0; border-radius: 9999px; outline-width: 0;
                    background-image: none; box-shadow: none;
                    background-color: {{track.ToRgbaHex()}}; color: {{track.ToRgbaHex()}};
                    opacity: {{(VirtualView.IsEnabled ? 1 : .5)}};
                }
                switch.{{_styleClass}} > slider {
                    min-width: 20px; min-height: 20px; padding: 0; margin: 0;
                    border: 0; border-radius: 9999px; background-image: none; box-shadow: none;
                    background-color: {{thumb.ToRgbaHex()}}; color: {{thumb.ToRgbaHex()}};
                }
                switch.{{_styleClass}} > image { opacity: 0; }
                """);

            // Reload the provider only when the rules actually changed, to avoid needless restyling.
            if (_lastCss == css) return;
            _lastCss = css;
            _style.LoadFromString(css);
        }
    }
}
