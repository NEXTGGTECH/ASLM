// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Platform;
using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace ASLM.Services.Controls;

/// <summary>
/// Presents shared grid templates as virtualized GTK list rows, including full-width collection headers and footers.
/// </summary>
internal sealed class LinuxCollectionGrid : IDisposable
{
    private readonly CollectionView _owner;
    private readonly IMauiContext _context;
    private readonly Gtk.StringList _model = Gtk.StringList.New(null);
    private readonly Gtk.SignalListItemFactory _factory = Gtk.SignalListItemFactory.New();
    private readonly List<Func<View>> _rows = [];
    private readonly Dictionary<nint, (Gtk.ListItem Item, View View, Gtk.Widget Widget, uint Tick, EventHandler Invalidated)> _realized = [];
    private bool _disposed;
    private int _span;
    private int _headerRows;
    private object?[] _items = [];
    private ContentView? _emptyRow;

    internal Gtk.ListView Widget { get; }

    // Construction and data

    /// <summary>
    /// Keeps the native list directly inside its scroller so GTK only realizes visible grid rows.
    /// </summary>
    internal LinuxCollectionGrid(CollectionView owner, IMauiContext context)
    {
        _owner = owner;
        _context = context;
        _factory.OnBind += OnBind;
        _factory.OnUnbind += OnUnbind;
        Widget = Gtk.ListView.New(Gtk.NoSelection.New(_model), _factory);
        Widget.SetHexpand(true);
        Widget.SetVexpand(true);
        Widget.AddCssClass("aslm-grid");
        _owner.ScrollToRequested += OnScrollToRequested;
    }

    /// <summary>
    /// Rebuilds row descriptors using the current span and shared templates without eagerly creating every card.
    /// </summary>
    internal void Refresh(GridItemsLayout layout)
    {
        // Clear native bindings before replacing the descriptors used by factory callbacks.
        foreach (var row in _realized.Values.ToArray()) ReleaseRow(row.Item);
        _emptyRow = null;
        _model.Splice(0, _model.GetNItems(), []);
        _rows.Clear();
        AddChrome(_owner.Header, _owner.HeaderTemplate);
        _headerRows = _rows.Count;
        _items = _owner.ItemsSource?.Cast<object?>().ToArray() ?? [];
        _span = Math.Max(1, layout.Span);
        for (var start = 0; start < _items.Length; start += _span)
        {
            var offset = start;
            _rows.Add(() => CreateRow(_items, offset, _span, layout));
        }
        if (_items.Length == 0 && (_owner.EmptyView != null || _owner.EmptyViewTemplate != null))
            _rows.Add(() => _emptyRow = new ContentView { Content = CreateContent(_owner.EmptyView, _owner.EmptyViewTemplate) });
        AddChrome(_owner.Footer, _owner.FooterTemplate);
        _model.Splice(0, 0, Enumerable.Range(0, _rows.Count).Select(i => i.ToString()).ToArray());
    }

    /// <summary>
    /// Adds a full-width header or footer while retaining its normal MAUI bindings and layout.
    /// </summary>
    private void AddChrome(object? content, DataTemplate? template)
    {
        if (content == null && template == null) return;
        _rows.Add(() => new ContentView { Content = CreateContent(content, template) });
    }

    /// <summary>
    /// Selects the application-provided template rather than reproducing a card's visual structure in GTK.
    /// </summary>
    private View CreateContent(object? item, DataTemplate? template)
    {
        var selected = template is DataTemplateSelector selector ? selector.SelectTemplate(item, _owner) : template;
        if (selected?.CreateContent() is View view)
        {
            view.BindingContext = item;
            return view;
        }
        return item as View ?? new Label { Text = item?.ToString() };
    }

    /// <summary>
    /// Arranges one row in equal star columns, retaining unused columns on the final incomplete row.
    /// </summary>
    private Grid CreateRow(object?[] items, int start, int span, GridItemsLayout layout)
    {
        var row = new Grid
        {
            ColumnSpacing = layout.HorizontalItemSpacing,
            Margin = new Thickness(0, 0, 0, start + span < items.Length ? layout.VerticalItemSpacing : 0)
        };
        for (var column = 0; column < span; column++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (start + column >= items.Length) continue;
            var card = CreateContent(items[start + column], _owner.ItemTemplate);
            Grid.SetColumn(card, column);
            row.Children.Add(card);
        }
        return row;
    }

    // Native row lifetime

    /// <summary>
    /// Inflates a visible row through the shared handlers and follows GTK's allocated viewport width.
    /// </summary>
    private void OnBind(Gtk.SignalListItemFactory sender, Gtk.SignalListItemFactory.BindSignalArgs args)
    {
        var item = (Gtk.ListItem)args.Object;
        ReleaseRow(item);
        var position = (int)item.GetPosition();
        if (_disposed || position < 0 || position >= _rows.Count) return;
        var view = _rows[position]();
        _owner.AddLogicalChild(view);
        var widget = (Gtk.Widget)view.ToPlatform(_context);
        DisableExpansion(widget);
        widget.SetHexpand(true);
        item.SetChild(widget);
        var lastWidth = -1;
        var lastViewportHeight = double.NaN;
        var dirty = true;
        EventHandler invalidated = (_, _) => dirty = true;
        view.MeasureInvalidated += invalidated;
        // GTK owns row allocation; MAUI owns its content height. Only remeasure on a width or content change,
        // not on every frame, and let GTK recycle the entire row when it leaves the visible range.
        var tick = widget.AddTickCallback((_, _) =>
        {
            var width = widget.GetAllocatedWidth();
            if (width > 0 && (dirty || width != lastWidth || ReferenceEquals(view, _emptyRow) && lastViewportHeight != _owner.Height))
            {
                ArrangeRow(view, widget, width);
                lastWidth = width;
                lastViewportHeight = _owner.Height;
                dirty = false;
            }
            return !_disposed;
        });
        _realized[item.Handle.DangerousGetHandle()] = (item, view, widget, tick, invalidated);
        var initialWidth = Widget.GetAllocatedWidth();
        if (initialWidth <= 0) initialWidth = _owner.Width > 0 ? (int)_owner.Width : 400;
        ArrangeRow(view, widget, initialWidth);
    }

    /// <summary>
    /// Releases a row when it leaves GTK's realized range or its model entry changes.
    /// </summary>
    private void OnUnbind(Gtk.SignalListItemFactory sender, Gtk.SignalListItemFactory.UnbindSignalArgs args) =>
        ReleaseRow((Gtk.ListItem)args.Object);

    /// <summary>
    /// Maps a card focus request to its containing native row, accounting for a collection header.
    /// </summary>
    private void OnScrollToRequested(object? sender, ScrollToRequestEventArgs args)
    {
        var index = args.Mode == ScrollToMode.Position ? args.Index : Array.IndexOf(_items, args.Item);
        if (index >= 0 && index < _items.Length)
            Widget.ScrollTo((uint)(_headerRows + index / _span), Gtk.ListScrollFlags.None, null);
    }

    /// <summary>
    /// Measures wrapping text and card contents at their actual row width instead of forcing a native theme minimum.
    /// </summary>
    private void ArrangeRow(View view, Gtk.Widget widget, int width)
    {
        if (_disposed || width <= 0 || !_realized.Values.Any(row => ReferenceEquals(row.View, view))) return;
        var layout = (IView)view;
        var size = layout.Measure(width, double.PositiveInfinity);
        var height = Math.Max(1, (int)Math.Ceiling(size.Height));
        if (ReferenceEquals(view, _emptyRow))
        {
            // Empty content occupies the remaining viewport; the header still scrolls and retains its own height.
            var chromeHeight = _realized.Values.Where(row => !ReferenceEquals(row.View, view))
                .Sum(row => ((IView)row.View).Measure(width, double.PositiveInfinity).Height);
            height = Math.Max(height, (int)Math.Ceiling(_owner.Height - chromeHeight));
        }
        layout.Arrange(new Rect(0, 0, width, height));
        widget.SetSizeRequest(-1, height);
    }

    /// <summary>
    /// Lets MAUI's measured row height govern nested fixed layouts rather than expanding each row to the viewport.
    /// </summary>
    private static void DisableExpansion(Gtk.Widget widget)
    {
        if (widget is GtkLayoutPanel panel) panel.IsExternallyManaged = true;
        widget.SetVexpand(false);
        for (var child = widget.GetFirstChild(); child != null; child = child.GetNextSibling())
            DisableExpansion(child);
    }

    /// <summary>
    /// Detaches logical and native ownership, including reusable header/footer views.
    /// </summary>
    private void ReleaseRow(Gtk.ListItem item)
    {
        if (!_realized.Remove(item.Handle.DangerousGetHandle(), out var row)) return;
        row.Widget.RemoveTickCallback(row.Tick);
        row.View.MeasureInvalidated -= row.Invalidated;
        item.SetChild(null);
        _owner.RemoveLogicalChild(row.View);
        row.View.DisconnectHandlers();
        if (row.View is ContentView chrome) chrome.Content = null;
    }

    /// <summary>
    /// Removes callbacks and all realized template handlers when this grid presentation is replaced.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _owner.ScrollToRequested -= OnScrollToRequested;
        Widget.SetModel(null);
        Widget.SetFactory(null);
        foreach (var row in _realized.Values.ToArray()) ReleaseRow(row.Item);
        _factory.OnBind -= OnBind;
        _factory.OnUnbind -= OnUnbind;
        _rows.Clear();
        _factory.Dispose();
        _model.Dispose();
    }
}
