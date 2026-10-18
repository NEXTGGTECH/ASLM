// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;
using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace ASLM.Services.Controls;

/// <summary>
/// Gives multiline editors a native scrollable viewport, which the preview GTK handler lacks.
/// </summary>
public sealed class LinuxEditorHandler : GtkViewHandler<Editor, Gtk.ScrolledWindow>
{
    private static readonly IPropertyMapper<Editor, LinuxEditorHandler> EditorMapper =
        new PropertyMapper<Editor, LinuxEditorHandler>(ViewMapper)
        {
            [nameof(Editor.Text)] = (handler, _) => handler.ApplyText(),
            [nameof(Editor.MaxLength)] = (handler, _) => handler.ApplyText(),
            [nameof(Editor.CursorPosition)] = (handler, _) => handler.ApplySelection(),
            [nameof(Editor.SelectionLength)] = (handler, _) => handler.ApplySelection(),
            [nameof(Editor.AutoSize)] = (handler, _) => handler.InvalidateLayout(),
            [nameof(Editor.IsReadOnly)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(ITextStyle.Font)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(Editor.TextColor)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(Editor.Background)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(Editor.CharacterSpacing)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(Editor.HorizontalTextAlignment)] = (handler, _) => handler.ApplyPresentation(),
            [nameof(Editor.Keyboard)] = (handler, _) => handler.ApplyPresentation()
        };

    private Gtk.TextView _text = null!;
    private bool _syncing;

    /// <summary>
    /// Keeps native colors and font metrics valid when the selected locale uses decimal commas.
    /// </summary>
    static LinuxEditorHandler() => LinuxControlStyles.UseInvariantCss(EditorMapper);

    /// <summary>
    /// Registers multiline properties separately from the generic view mappings.
    /// </summary>
    public LinuxEditorHandler() : base(EditorMapper) { }

    /// <summary>
    /// Keeps the text view inside a bounded viewport so long legal documents remain readable.
    /// </summary>
    protected override Gtk.ScrolledWindow CreatePlatformView()
    {
        _text = Gtk.TextView.New();
        _text.SetWrapMode(Gtk.WrapMode.WordChar);
        _text.SetVexpand(true);
        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetPropagateNaturalWidth(false);
        scroll.SetPropagateNaturalHeight(false);
        scroll.SetChild(_text);
        return scroll;
    }

    /// <summary>
    /// Connects native edits and selection changes to the shared MAUI editor.
    /// </summary>
    protected override void ConnectHandler(Gtk.ScrolledWindow platformView)
    {
        base.ConnectHandler(platformView);
        _text.GetBuffer().OnChanged += OnTextChanged;
        _text.GetBuffer().OnMarkSet += OnSelectionChanged;
    }

    /// <summary>
    /// Removes buffer callbacks before the native view is disconnected.
    /// </summary>
    protected override void DisconnectHandler(Gtk.ScrolledWindow platformView)
    {
        _text.GetBuffer().OnChanged -= OnTextChanged;
        _text.GetBuffer().OnMarkSet -= OnSelectionChanged;
        base.DisconnectHandler(platformView);
    }

    /// <summary>
    /// Measures autosized inputs by their wrapped text while explicit-height editors keep their viewport.
    /// </summary>
    public override Size GetDesiredSize(double widthConstraint, double heightConstraint)
    {
        var measured = base.GetDesiredSize(widthConstraint, heightConstraint);
        var width = double.IsFinite(widthConstraint) ? widthConstraint : measured.Width;
        var height = measured.Height;
        if (VirtualView.AutoSize == EditorAutoSizeOption.TextChanges && VirtualView.HeightRequest < 0)
        {
            _text.Measure(Gtk.Orientation.Vertical, (int)Math.Clamp(width, 1, int.MaxValue),
                out _, out var natural, out _, out _);
            height = natural;
        }
        var view = (IView)VirtualView;
        return new Size(
            LayoutManager.ResolveConstraints(widthConstraint, view.Width, width, view.MinimumWidth, view.MaximumWidth),
            LayoutManager.ResolveConstraints(heightConstraint, view.Height, height, view.MinimumHeight, view.MaximumHeight));
    }

    /// <summary>
    /// Applies programmatic text without replacing the native buffer after an unchanged value notification.
    /// </summary>
    private void ApplyText()
    {
        if (_syncing || _text == null) return;
        _syncing = true;
        try
        {
            var value = LimitText(VirtualView.Text ?? string.Empty);
            var buffer = _text.GetBuffer();
            if (ReadText(buffer) != value) buffer.SetText(value, -1);
            if (VirtualView.Text != value) VirtualView.Text = value;
        }
        finally { _syncing = false; }
        InvalidateLayout();
    }

    /// <summary>
    /// Publishes user input and enforces the shared maximum without splitting a UTF-16 surrogate pair.
    /// </summary>
    private void OnTextChanged(Gtk.TextBuffer sender, EventArgs args)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var original = ReadText(sender);
            var value = LimitText(original);
            if (original != value) sender.SetText(value, -1);
            VirtualView.Text = value;
        }
        finally { _syncing = false; }
        InvalidateLayout();
    }

    /// <summary>
    /// Bounds text using MAUI's UTF-16 length convention while keeping valid Unicode text.
    /// </summary>
    private string LimitText(string text)
    {
        var length = Math.Clamp(VirtualView.MaxLength, 0, text.Length);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    /// <summary>
    /// Maps managed selection indices to GTK's Unicode scalar offsets.
    /// </summary>
    private void ApplySelection()
    {
        if (_syncing || _text == null) return;
        var buffer = _text.GetBuffer();
        var value = ReadText(buffer);
        var start = Math.Clamp(VirtualView.CursorPosition, 0, value.Length);
        var end = start + Math.Clamp(VirtualView.SelectionLength, 0, value.Length - start);
        _syncing = true;
        try
        {
            buffer.GetIterAtOffset(out var first, value[..start].EnumerateRunes().Count());
            buffer.GetIterAtOffset(out var last, value[..end].EnumerateRunes().Count());
            buffer.SelectRange(first, last);
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Preserves selection bindings when users move the caret or select native text.
    /// </summary>
    private void OnSelectionChanged(Gtk.TextBuffer sender, Gtk.TextBuffer.MarkSetSignalArgs args)
    {
        if (_syncing) return;
        sender.GetStartIter(out var origin);
        sender.GetSelectionBounds(out var start, out var end);
        _syncing = true;
        try
        {
            VirtualView.CursorPosition = (sender.GetText(origin, start, false) ?? string.Empty).Length;
            VirtualView.SelectionLength = (sender.GetText(start, end, false) ?? string.Empty).Length;
        }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Applies shared text styling to the actual editor rather than only its scroll container.
    /// </summary>
    private void ApplyPresentation()
    {
        if (_text == null) return;
        var view = (IEditor)VirtualView;
        _text.SetEditable(!view.IsReadOnly);
        _text.SetInputPurpose(view.Keyboard == Keyboard.Email ? Gtk.InputPurpose.Email
            : view.Keyboard == Keyboard.Url ? Gtk.InputPurpose.Url
            : view.Keyboard == Keyboard.Telephone ? Gtk.InputPurpose.Phone
            : view.Keyboard == Keyboard.Numeric ? Gtk.InputPurpose.Number
            : Gtk.InputPurpose.FreeForm);
        _text.SetJustification(view.HorizontalTextAlignment switch
        {
            TextAlignment.Center => Gtk.Justification.Center,
            TextAlignment.End => Gtk.Justification.Right,
            _ => Gtk.Justification.Left
        });
        var css = BuildFontCss(view.Font) + FormattableString.Invariant($" letter-spacing: {view.CharacterSpacing}px;");
        if (view.TextColor != null) css += $" color: {ToGtkColor(view.TextColor)};";
        css += view.Background is SolidPaint paint
            ? $" background-color: {ToGtkColor(paint.Color)};"
            : " background-color: transparent;";
        UpdateCssWithSelector(_text, "textview, textview text", css);
    }

    /// <summary>
    /// Propagates content measurement changes to GTK only for an autosized editor.
    /// </summary>
    private void InvalidateLayout()
    {
        if (VirtualView.AutoSize != EditorAutoSizeOption.TextChanges) return;
        ((IView)VirtualView).InvalidateMeasure();
        for (Gtk.Widget? widget = PlatformView; widget != null; widget = widget.GetParent())
            if (widget is GtkLayoutPanel panel) panel.LayoutDirty = true;
        PlatformView?.QueueResize();
    }

    /// <summary>
    /// Reads the current native buffer using fresh iterators after mutations.
    /// </summary>
    private static string ReadText(Gtk.TextBuffer buffer)
    {
        buffer.GetStartIter(out var start);
        buffer.GetEndIter(out var end);
        return buffer.GetText(start, end, false) ?? string.Empty;
    }
}
