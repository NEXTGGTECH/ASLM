// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Handlers;
using System.Globalization;
using GtkHandlers = Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Controls;

/// <summary>
/// Removes desktop-theme chrome that would otherwise override MAUI's explicit control styling and sizes.
/// </summary>
internal static class LinuxControlStyles
{
    private static bool _configured;
    private static Gtk.CssProvider? _reset;
    private static readonly HashSet<string> CssProperties =
    [
        "Background", "BackgroundColor", "TextColor", "Font", "CharacterSpacing", "LineHeight", "Padding",
        "CornerRadius", "StrokeColor", "StrokeThickness", "Stroke", "Shape", "Shadow", "Color",
        "PlaceholderColor", "TitleColor", "CancelButtonColor", "TrackColor", "OnColor", "ThumbColor",
        "ProgressColor", "MinimumTrackColor", "MaximumTrackColor", "TextDecorations", "Foreground",
        "StrokeDashOffset", "StrokeDashPattern", "StrokeLineCap", "StrokeLineJoin", "StrokeMiterLimit"
    ];

    /// <summary>
    /// Scopes the reset to MAUI widgets, leaving GTK file dialogs, selector popups and other native UI intact.
    /// </summary>
    internal static void Configure()
    {
        if (_configured) return;
        _configured = true;
        ViewHandler.ViewMapper.AppendToMapping("ASLMLinuxControlStyles", static (handler, _) =>
        {
            if (handler.PlatformView is not Gtk.Widget widget) return;
            EnsureReset();
            widget.AddCssClass("aslm-control");
            if (widget is Gtk.Entry entry) entry.SetWidthChars(1);
        });
        // The preview interpolates CSS numbers using CurrentCulture. A comma decimal separator breaks
        // translucent colors and font sizes after switching language; formatting must stay locale-independent.
        UseInvariantCss(GtkHandlers.LayoutHandler.Mapper);
        UseInvariantCss(GtkHandlers.ContentViewHandler.Mapper);
        UseInvariantCss(GtkHandlers.PageHandler.Mapper);
        UseInvariantCss(GtkHandlers.ButtonHandler.Mapper);
        UseInvariantCss(GtkHandlers.ImageButtonHandler.Mapper);
        UseInvariantCss(GtkHandlers.EntryHandler.Mapper);
        UseInvariantCss(GtkHandlers.LabelHandler.Mapper);
        UseInvariantCss(GtkHandlers.PickerHandler.Mapper);
        UseInvariantCss(GtkHandlers.SearchBarHandler.Mapper);
        UseInvariantCss(GtkHandlers.BorderHandler.Mapper);
        UseInvariantCss(GtkHandlers.BoxViewHandler.Mapper);
        UseInvariantCss(GtkHandlers.ScrollViewHandler.Mapper);
        UseInvariantCss(GtkHandlers.CollectionViewHandler.Mapper);
        UseInvariantCss(GtkHandlers.CheckBoxHandler.Mapper);
        UseInvariantCss(GtkHandlers.SwitchHandler.Mapper);
        UseInvariantCss(GtkHandlers.SliderHandler.Mapper);
        UseInvariantCss(GtkHandlers.ProgressBarHandler.Mapper);
        UseInvariantCss(GtkHandlers.ActivityIndicatorHandler.Mapper);
        UseInvariantCss(GtkHandlers.ImageHandler.Mapper);
        UseInvariantCss(GtkHandlers.GraphicsViewHandler.Mapper);
        UseInvariantCss(ConsoleOutputViewHandler.Mapper);
    }

    /// <summary>
    /// Formats only synchronous native-style mappings with invariant numbers, preserving application localization.
    /// </summary>
    internal static void UseInvariantCss<TView, THandler>(IPropertyMapper<TView, THandler> mapper)
        where TView : IElement where THandler : IElementHandler
    {
        foreach (var property in mapper.GetKeys().Where(CssProperties.Contains).ToArray())
            mapper.ModifyMapping(property, static (handler, view, original) =>
            {
                var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                    original?.Invoke(handler, view);
                }
                finally { CultureInfo.CurrentCulture = previous; }
            });
    }

    /// <summary>
    /// Installs one low-priority reset per display so individual MAUI property mappers remain authoritative.
    /// </summary>
    private static void EnsureReset()
    {
        if (_reset != null || Gdk.Display.GetDefault() is not { } display) return;
        _reset = Gtk.CssProvider.New();
        _reset.LoadFromString("""
            button.aslm-control, entry.aslm-control, searchentry.aslm-control,
            textview.aslm-control, scrolledwindow.aslm-control, dropdown.aslm-control,
            dropdown.aslm-control > button, checkbutton.aslm-control {
                min-width: 0; min-height: 0; margin: 0; padding: 0;
                border-width: 0; outline-width: 0; box-shadow: none;
                text-shadow: none; background-image: none;
            }
            entry.aslm-control > text, searchentry.aslm-control > text,
            dropdown.aslm-control > button {
                min-width: 0; min-height: 0; margin: 0; padding: 0;
                border-width: 0; box-shadow: none; background-color: transparent;
                background-image: none; color: inherit;
            }
            button.aslm-control > box { min-width: 0; min-height: 0; padding: 0; margin: 0; }
            listview.aslm-grid, listview.aslm-grid > row {
                min-width: 0; min-height: 0; margin: 0; padding: 0;
                border-width: 0; background: transparent; box-shadow: none;
            }
            """);
        // Above the desktop theme, below the backend's per-widget MAUI property providers.
        Gtk.StyleContext.AddProviderForDisplay(display, _reset, Gtk.Constants.STYLE_PROVIDER_PRIORITY_APPLICATION - 2);
    }
}
