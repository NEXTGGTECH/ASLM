// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Layouts;

namespace ASLM.Controls.Downloads;

/// <summary>
/// Wraps filters at their measured widths, then shares the remaining row space.
/// Unlike FlexLayout.Grow, stretching never reduces a filter's natural width.
/// </summary>
public sealed class DownloadFilterLayout : Layout, IDownloadFilterLayout
{
    public static readonly BindableProperty ColumnSpacingProperty = BindableProperty.Create(
        nameof(ColumnSpacing), typeof(double), typeof(DownloadFilterLayout), 0d,
        propertyChanged: OnSpacingChanged);

    public static readonly BindableProperty RowSpacingProperty = BindableProperty.Create(
        nameof(RowSpacing), typeof(double), typeof(DownloadFilterLayout), 0d,
        propertyChanged: OnSpacingChanged);

    public static readonly BindableProperty StretchItemsProperty = BindableProperty.Create(
        nameof(StretchItems), typeof(bool), typeof(DownloadFilterLayout), true,
        propertyChanged: OnSpacingChanged);

    public bool StretchItems
    {
        get => (bool)GetValue(StretchItemsProperty);
        set => SetValue(StretchItemsProperty, value);
    }

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    protected override ILayoutManager CreateLayoutManager() => new DownloadFilterLayoutManager(this);

    private static void OnSpacingChanged(BindableObject bindable, object oldValue, object newValue) =>
        ((DownloadFilterLayout)bindable).InvalidateMeasure();
}

public interface IDownloadFilterLayout : Microsoft.Maui.ILayout
{
    double ColumnSpacing { get; }
    double RowSpacing { get; }
    bool StretchItems { get; }
}

internal sealed class DownloadFilterLayoutManager(IDownloadFilterLayout filters) : LayoutManager(filters)
{
    public override Size Measure(double widthConstraint, double heightConstraint)
    {
        var padding = filters.Padding;
        var availableWidth = Math.Max(0, widthConstraint - padding.HorizontalThickness);
        foreach (var child in filters)
        {
            if (child.Visibility != Visibility.Collapsed)
                child.Measure(availableWidth, double.PositiveInfinity);
        }

        var rows = BuildRows(availableWidth);
        var contentWidth = rows.Count == 0 ? 0 : rows.Max(row => row.Width);
        var contentHeight = rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * filters.RowSpacing;
        return new Size(
            ResolveConstraints(widthConstraint, Layout.Width, contentWidth + padding.HorizontalThickness,
                Layout.MinimumWidth, Layout.MaximumWidth),
            ResolveConstraints(heightConstraint, Layout.Height, contentHeight + padding.VerticalThickness,
                Layout.MinimumHeight, Layout.MaximumHeight));
    }

    public override Size ArrangeChildren(Rect bounds)
    {
        var padding = filters.Padding;
        var availableWidth = Math.Max(0, bounds.Width - padding.HorizontalThickness);
        var left = bounds.Left + padding.Left;
        var right = left + availableWidth;
        var y = bounds.Top + padding.Top;
        var rightToLeft = Layout.FlowDirection == Microsoft.Maui.FlowDirection.RightToLeft;

        foreach (var row in BuildRows(availableWidth))
        {
            var extraWidth = filters.StretchItems ? (availableWidth - row.Width) / row.Children.Count : 0;
            var x = rightToLeft ? right : left;
            for (var index = 0; index < row.Children.Count; index++)
            {
                var child = row.Children[index];
                var width = Math.Min(child.DesiredSize.Width, availableWidth) + extraWidth;
                // End the last border exactly at the content edge, without accumulated rounding drift.
                if (filters.StretchItems && index == row.Children.Count - 1)
                    width = rightToLeft ? x - left : right - x;

                if (rightToLeft)
                    x -= width;
                child.Arrange(new Rect(x, y, Math.Max(0, width), row.Height));
                x += rightToLeft ? -filters.ColumnSpacing : width + filters.ColumnSpacing;
            }
            y += row.Height + filters.RowSpacing;
        }

        return bounds.Size;
    }

    private List<Row> BuildRows(double availableWidth)
    {
        var rows = new List<Row>();
        var row = new Row();
        foreach (var child in filters)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var width = Math.Min(child.DesiredSize.Width, availableWidth);
            if (row.Children.Count > 0 && row.Width + filters.ColumnSpacing + width > availableWidth)
            {
                rows.Add(row);
                row = new Row();
            }

            if (row.Children.Count > 0)
                row.Width += filters.ColumnSpacing;
            row.Children.Add(child);
            row.Width += width;
            row.Height = Math.Max(row.Height, child.DesiredSize.Height);
        }

        if (row.Children.Count > 0)
            rows.Add(row);
        return rows;
    }

    private sealed class Row
    {
        public List<IView> Children { get; } = [];
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
