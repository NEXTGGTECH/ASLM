// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace ASLM.Services.Internal;

/// <summary>
/// Renders selectable console output in GTK while publishing metrics to ASLM's shared scrollbar.
/// </summary>
public sealed class ConsoleOutputViewHandler : GtkViewHandler<ConsoleOutputView, Gtk.ScrolledWindow>
{
    public static readonly IPropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler> Mapper =
        new PropertyMapper<ConsoleOutputView, ConsoleOutputViewHandler>(ViewMapper)
        {
            [nameof(ConsoleOutputView.Text)] = (handler, _) => handler.QueueTextUpdate(),
            [nameof(ConsoleOutputView.SessionKey)] = (handler, _) => handler.ResetSession(),
            [nameof(ConsoleOutputView.UseCustomScrollBar)] = (handler, _) => handler.ApplyScrollChrome()
        };

    private Gtk.TextView? _textView;
    private Gtk.Adjustment? _adjustment;
    private string _rendered = string.Empty;
    private bool _pending;
    private bool _pinToBottom = true;
    private int _generation;

    /// <summary>
    /// Creates the console property mapper without introducing a separate MAUI control.
    /// </summary>
    public ConsoleOutputViewHandler() : base(Mapper) { }

    /// <summary>
    /// Creates a read-only, selectable text view whose viewport is constrained by MAUI layout.
    /// </summary>
    protected override Gtk.ScrolledWindow CreatePlatformView()
    {
        _textView = Gtk.TextView.New();
        _textView.SetEditable(false);
        _textView.SetCursorVisible(false);
        _textView.SetMonospace(true);
        _textView.SetWrapMode(Gtk.WrapMode.WordChar);
        _textView.SetLeftMargin(10);
        _textView.SetRightMargin(10);
        _textView.SetTopMargin(8);
        _textView.SetBottomMargin(8);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPropagateNaturalHeight(false);
        scroll.SetPropagateNaturalWidth(false);
        scroll.SetChild(_textView);
        return scroll;
    }

    /// <summary>
    /// Connects scroll and palette notifications for the current native host.
    /// </summary>
    protected override void ConnectHandler(Gtk.ScrolledWindow platformView)
    {
        base.ConnectHandler(platformView);
        _generation++;
        _adjustment = platformView.GetVadjustment();
        _adjustment.OnChanged += OnGeometryChanged;
        _adjustment.OnValueChanged += OnScrollChanged;
        VirtualView.ScrollToOffset = ScrollTo;
        ThemeService.PaletteApplied += OnPaletteApplied;
        ApplyTheme();
        ApplyScrollChrome();
        QueueTextUpdate();
    }

    /// <summary>
    /// Releases subscriptions and invalidates queued work before native widgets are detached.
    /// </summary>
    protected override void DisconnectHandler(Gtk.ScrolledWindow platformView)
    {
        _generation++;
        _pending = false;
        if (_adjustment != null)
        {
            _adjustment.OnChanged -= OnGeometryChanged;
            _adjustment.OnValueChanged -= OnScrollChanged;
        }
        _adjustment = null;
        VirtualView.ScrollToOffset = null;
        ThemeService.PaletteApplied -= OnPaletteApplied;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Coalesces console notifications into one GTK update and ignores callbacks from old handlers.
    /// </summary>
    private void QueueTextUpdate()
    {
        if (_pending || _adjustment == null) return;
        _pending = true;
        var generation = _generation;
        VirtualView.Dispatcher.Dispatch(() =>
        {
            if (generation != _generation) return;
            _pending = false;
            ApplyText();
        });
    }

    /// <summary>
    /// Appends new output without replacing existing selection; replaces text only after trimming or session changes.
    /// </summary>
    private void ApplyText()
    {
        var text = VirtualView.Text ?? string.Empty;
        var buffer = _textView?.GetBuffer();
        if (buffer == null || text == _rendered) return;
        if (text.StartsWith(_rendered, StringComparison.Ordinal))
        {
            buffer.GetEndIter(out var end);
            buffer.Insert(end, text[_rendered.Length..], -1);
        }
        else buffer.SetText(text, -1);
        _rendered = text;
        PublishScrollMetrics();
    }

    /// <summary>
    /// Resets bottom pinning when a different process or module console is selected.
    /// </summary>
    private void ResetSession()
    {
        _pinToBottom = true;
        _rendered = string.Empty;
        _textView?.GetBuffer().SetText(string.Empty, -1);
        QueueTextUpdate();
    }

    /// <summary>
    /// Hides native chrome without disabling GTK's scrollable viewport.
    /// </summary>
    private void ApplyScrollChrome() => PlatformView?.SetPolicy(Gtk.PolicyType.Never,
        VirtualView.UseCustomScrollBar ? Gtk.PolicyType.External : Gtk.PolicyType.Automatic);

    /// <summary>
    /// Preserves the user's scroll position unless output was already pinned to the bottom.
    /// </summary>
    private void OnGeometryChanged(Gtk.Adjustment sender, EventArgs args)
    {
        if (_pinToBottom) sender.SetValue(Math.Max(0, sender.GetUpper() - sender.GetPageSize()));
        PublishScrollMetrics();
    }

    /// <summary>
    /// Tracks user scrolling so new output does not pull a reader away from older log entries.
    /// </summary>
    private void OnScrollChanged(Gtk.Adjustment sender, EventArgs args)
    {
        _pinToBottom = sender.GetUpper() - sender.GetPageSize() - sender.GetValue() <= 48;
        PublishScrollMetrics();
    }

    /// <summary>
    /// Applies a scrollbar drag within the native adjustment's valid range.
    /// </summary>
    private void ScrollTo(double offset)
    {
        if (_adjustment is { } adjustment)
            adjustment.SetValue(Math.Clamp(offset, 0, Math.Max(0, adjustment.GetUpper() - adjustment.GetPageSize())));
    }

    /// <summary>
    /// Publishes native viewport dimensions without measuring or replacing the console text.
    /// </summary>
    private void PublishScrollMetrics()
    {
        if (_adjustment is { } adjustment)
            VirtualView.SetScrollMetrics(adjustment.GetPageSize(), adjustment.GetUpper(), adjustment.GetValue());
    }

    /// <summary>
    /// Marshals palette updates to the GTK thread and ignores disconnected views.
    /// </summary>
    private void OnPaletteApplied() => VirtualView.Dispatcher.Dispatch(() =>
    {
        if (_adjustment != null) ApplyTheme();
    });

    /// <summary>
    /// Applies the same surface and text palette as the other console handlers.
    /// </summary>
    private void ApplyTheme()
    {
        var surface = IconTintHelper.ResolvePaletteColor("BackgroundSecondary");
        var foreground = IconTintHelper.ResolvePaletteColor("LabelPrimary");
        UpdateCssWithSelector(_textView, "textview, textview text",
            $"background-color: {surface.ToRgbaHex()}; color: {foreground.ToRgbaHex()}; font-family: monospace; font-size: 12px;");
    }
}
