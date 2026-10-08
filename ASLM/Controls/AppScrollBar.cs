// Copyright NEXTGGTECH. Apache License 2.0.

using System.ComponentModel;

namespace ASLM.Controls;

/// <summary>Shared stable scrollbar for application scroll views and native consoles.</summary>
public sealed class AppScrollBar : Grid
{
    public static readonly BindableProperty TargetProperty = BindableProperty.Create(
        nameof(Target), typeof(VisualElement), typeof(AppScrollBar), null,
        propertyChanged: (bindable, _, _) => ((AppScrollBar)bindable).Reconnect());

    private readonly BoxView _thumb = new();
    private VisualElement? _connected;
    private View? _content;
    private bool _updating;
    private double _dragStart;

    public VisualElement? Target
    {
        get => (VisualElement?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public AppScrollBar()
    {
        WidthRequest = MinimumWidthRequest = 12;
        BackgroundColor = Colors.Transparent;
        HorizontalOptions = LayoutOptions.Center;
        VerticalOptions = LayoutOptions.Fill;
        IsVisible = false;
        _thumb.SetDynamicResource(StyleProperty, "AppScrollBarThumbStyle");
        Children.Add(_thumb);
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        GestureRecognizers.Add(pan);
        Loaded += (_, _) => Reconnect();
        Unloaded += (_, _) => Disconnect();
        SizeChanged += (_, _) => Refresh();
    }

    private void Reconnect()
    {
        Disconnect();
        _connected = Target;
        if (_connected == null) return;
        _connected.SizeChanged += OnGeometryChanged;
        _connected.PropertyChanged += OnTargetPropertyChanged;
        _connected.HandlerChanged += OnHandlerChanged;
        if (_connected is ScrollView scroll)
        {
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Never;
            scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Never;
            scroll.Scrolled += OnScrolled;
            ConnectContent(scroll.Content);
        }
        else if (_connected is ConsoleOutputView console)
        {
            console.UseCustomScrollBar = true;
            console.ScrollMetricsChanged += OnGeometryChanged;
        }
        ApplyNativeChrome();
        Refresh();
    }

    private void Disconnect()
    {
        if (_connected != null)
        {
            _connected.SizeChanged -= OnGeometryChanged;
            _connected.PropertyChanged -= OnTargetPropertyChanged;
            _connected.HandlerChanged -= OnHandlerChanged;
        }
        if (_connected is ScrollView scroll) scroll.Scrolled -= OnScrolled;
        if (_connected is ConsoleOutputView console) console.ScrollMetricsChanged -= OnGeometryChanged;
        ConnectContent(null);
        _connected = null;
    }

    private void ConnectContent(View? content)
    {
        if (_content != null) _content.SizeChanged -= OnGeometryChanged;
        _content = content;
        if (_content != null) _content.SizeChanged += OnGeometryChanged;
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_connected is ScrollView scroll && e.PropertyName == nameof(ScrollView.Content)) ConnectContent(scroll.Content);
        if (e.PropertyName is nameof(ScrollView.ContentSize) or nameof(IsVisible) or nameof(ScrollView.Content)) Refresh();
    }

    private void OnHandlerChanged(object? sender, EventArgs e) { ApplyNativeChrome(); Refresh(); }
    private void OnGeometryChanged(object? sender, EventArgs e) => Refresh();
    private void OnScrolled(object? sender, ScrolledEventArgs e) => Refresh();

    private void ApplyNativeChrome()
    {
#if WINDOWS
        if (_connected is ScrollView { Handler.PlatformView: Microsoft.UI.Xaml.Controls.ScrollViewer viewer })
        {
            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            viewer.Background = viewer.BorderBrush = transparent;
            viewer.BorderThickness = viewer.Padding = new Microsoft.UI.Xaml.Thickness(0);
            viewer.VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
            viewer.HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Hidden;
        }
#endif
    }

    private (double Viewport, double Content, double Offset) Metrics => _connected switch
    {
        ScrollView scroll => (scroll.Height, Math.Max(scroll.ContentSize.Height, scroll.Content?.Height ?? 0), scroll.ScrollY),
        ConsoleOutputView console => (console.ScrollViewport, console.ScrollExtent, console.ScrollOffset),
        _ => default
    };

    internal static (double Height, double Top) CalculateThumb(double viewport, double content, double offset, double track = 0)
    {
        if (viewport <= 0 || content <= viewport + 1) return default;
        var length = track > 0 ? track : viewport;
        var height = Math.Min(length, Math.Max(24, length * viewport / content));
        return (height, Math.Clamp(offset / (content - viewport) * (length - height), 0, length - height));
    }

    internal void Refresh()
    {
        if (_updating) return;
        _updating = true;
        var changed = false;
        try
        {
            var metrics = Metrics;
            var (height, top) = CalculateThumb(metrics.Viewport, metrics.Content, metrics.Offset, Height);
            var visible = height > 0 && _connected?.IsVisible == true;
            changed = IsVisible != visible;
            IsVisible = visible;
            if (visible) _thumb.HeightRequest = height;
            _thumb.TranslationY = top;
        }
        finally { _updating = false; }
        if (changed) Dispatcher.Dispatch(Refresh);
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started) _dragStart = _thumb.TranslationY;
        else if (e.StatusType == GestureStatus.Running)
        {
            var metrics = Metrics;
            var travel = Math.Max(0, Height - _thumb.Height);
            var top = Math.Clamp(_dragStart + e.TotalY, 0, travel);
            var offset = travel > 0 ? top / travel * Math.Max(0, metrics.Content - metrics.Viewport) : 0;
            _thumb.TranslationY = top;
            if (_connected is ScrollView scroll) _ = scroll.ScrollToAsync(0, offset, false);
            else if (_connected is ConsoleOutputView console) console.ScrollToOffset?.Invoke(offset);
        }
        else Refresh();
    }
}
