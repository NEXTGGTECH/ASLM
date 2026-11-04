// Copyright NEXTGGTECH. Apache License 2.0.

using Microsoft.Maui.Layouts;

namespace ASLM.Controls.Downloads
{
    // Filter layout

    /// <summary>
    /// Wraps filters at their measured widths, then shares the remaining row space.
    /// Unlike FlexLayout.Grow, stretching never reduces a filter's natural width.
    /// </summary>
    public sealed class DownloadFilterLayout : Layout, IDownloadFilterLayout
    {
        // Bindable properties

        /// <summary>
        /// Identifies the bindable property that holds the horizontal gap between filters in a row.
        /// </summary>
        public static readonly BindableProperty ColumnSpacingProperty = BindableProperty.Create(
            nameof(ColumnSpacing), typeof(double), typeof(DownloadFilterLayout), 0d,
            propertyChanged: OnSpacingChanged);

        /// <summary>
        /// Identifies the bindable property that holds the vertical gap between wrapped rows.
        /// </summary>
        public static readonly BindableProperty RowSpacingProperty = BindableProperty.Create(
            nameof(RowSpacing), typeof(double), typeof(DownloadFilterLayout), 0d,
            propertyChanged: OnSpacingChanged);

        /// <summary>
        /// Identifies the bindable property that selects whether each row distributes its free width among its filters.
        /// </summary>
        public static readonly BindableProperty StretchItemsProperty = BindableProperty.Create(
            nameof(StretchItems), typeof(bool), typeof(DownloadFilterLayout), true,
            propertyChanged: OnSpacingChanged);


        // Properties

        /// <summary>
        /// Whether the free width of each row is shared equally among its filters, on top of their natural widths.
        /// </summary>
        public bool StretchItems
        {
            get => (bool)GetValue(StretchItemsProperty);
            set => SetValue(StretchItemsProperty, value);
        }

        /// <summary>
        /// Horizontal gap between neighbouring filters in a row.
        /// </summary>
        public double ColumnSpacing
        {
            get => (double)GetValue(ColumnSpacingProperty);
            set => SetValue(ColumnSpacingProperty, value);
        }

        /// <summary>
        /// Vertical gap between wrapped rows.
        /// </summary>
        public double RowSpacing
        {
            get => (double)GetValue(RowSpacingProperty);
            set => SetValue(RowSpacingProperty, value);
        }


        // Layout management

        /// <summary>
        /// Creates the manager that measures and arranges the filters.
        /// </summary>
        protected override ILayoutManager CreateLayoutManager() => new DownloadFilterLayoutManager(this);

        /// <summary>
        /// Re-measures the layout when any spacing or stretch property changes.
        /// </summary>
        private static void OnSpacingChanged(BindableObject bindable, object oldValue, object newValue) =>
            ((DownloadFilterLayout)bindable).InvalidateMeasure();
    }

    /// <summary>
    /// Layout settings that <see cref="DownloadFilterLayoutManager"/> reads while measuring and arranging.
    /// </summary>
    public interface IDownloadFilterLayout : Microsoft.Maui.ILayout
    {
        /// <summary>
        /// Horizontal gap between neighbouring filters in a row.
        /// </summary>
        double ColumnSpacing { get; }

        /// <summary>
        /// Vertical gap between wrapped rows.
        /// </summary>
        double RowSpacing { get; }

        /// <summary>
        /// Whether the free width of each row is shared among its filters.
        /// </summary>
        bool StretchItems { get; }
    }


    // Layout manager

    /// <summary>
    /// Measures filters at their natural width, packs them into rows, and arranges the rows with optional stretching.
    /// </summary>
    internal sealed class DownloadFilterLayoutManager(IDownloadFilterLayout filters) : LayoutManager(filters)
    {
        // Measuring and arranging

        /// <summary>
        /// Measures every visible filter at its natural width and returns the size of the wrapped rows.
        /// </summary>
        public override Size Measure(double widthConstraint, double heightConstraint)
        {
            // Children get the full inner width and unbounded height, so each reports its natural width.
            var padding = filters.Padding;
            var availableWidth = Math.Max(0, widthConstraint - padding.HorizontalThickness);
            foreach (var child in filters)
            {
                if (child.Visibility != Visibility.Collapsed)
                    child.Measure(availableWidth, double.PositiveInfinity);
            }

            // Content size is the widest row and the stacked row heights with row spacing between them.
            var rows = BuildRows(availableWidth);
            var contentWidth = rows.Count == 0 ? 0 : rows.Max(row => row.Width);
            var contentHeight = rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * filters.RowSpacing;
            return new Size(
                ResolveConstraints(widthConstraint, Layout.Width, contentWidth + padding.HorizontalThickness,
                    Layout.MinimumWidth, Layout.MaximumWidth),
                ResolveConstraints(heightConstraint, Layout.Height, contentHeight + padding.VerticalThickness,
                    Layout.MinimumHeight, Layout.MaximumHeight));
        }

        /// <summary>
        /// Places each filter in its row, left to right or right to left, and distributes the free row width
        /// when stretching.
        /// </summary>
        public override Size ArrangeChildren(Rect bounds)
        {
            var padding = filters.Padding;
            var availableWidth = Math.Max(0, bounds.Width - padding.HorizontalThickness);
            var left = bounds.Left + padding.Left;
            var right = left + availableWidth;
            var y = bounds.Top + padding.Top;
            var rightToLeft = Layout.FlowDirection == Microsoft.Maui.FlowDirection.RightToLeft;

            // Rebuild the rows for the final width; each row starts at the leading edge for the flow direction.
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


        // Row packing

        /// <summary>
        /// Packs the visible filters into rows in order, starting a new row when the next filter would not fit.
        /// </summary>
        private List<Row> BuildRows(double availableWidth)
        {
            var rows = new List<Row>();
            var row = new Row();
            foreach (var child in filters)
            {
                if (child.Visibility == Visibility.Collapsed)
                    continue;

                // A filter wider than the available width is clipped to a row of its own.
                var width = Math.Min(child.DesiredSize.Width, availableWidth);
                if (row.Children.Count > 0 && row.Width + filters.ColumnSpacing + width > availableWidth)
                {
                    rows.Add(row);
                    row = new Row();
                }

                // Add the filter with its leading gap; the row is as tall as its tallest filter.
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


        // Nested types

        /// <summary>
        /// One wrapped row of filters with its accumulated size.
        /// </summary>
        private sealed class Row
        {
            /// <summary>
            /// Filters placed in this row, in layout order.
            /// </summary>
            public List<IView> Children { get; } = [];

            /// <summary>
            /// Total width of the filters and the column gaps between them.
            /// </summary>
            public double Width { get; set; }

            /// <summary>
            /// Height of the tallest filter in the row.
            /// </summary>
            public double Height { get; set; }
        }
    }
}
