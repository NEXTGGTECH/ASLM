// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Controls.Downloads;
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using Moq;

namespace ASLM.Tests.Services.Controls
{
    /// <summary>
    /// Verifies <see cref="DownloadFilterLayoutManager"/>: wrapping of filters into rows, sharing of free row width
    /// without narrowing any filter, right-to-left placement, and handling of oversized and collapsed filters.
    /// The layout and its filters are Moq doubles, so no MAUI runtime is needed.
    /// </summary>
    public sealed class DownloadFilterLayoutTests
    {
        // Stretching

        /// <summary>
        /// Verifies, for several row widths, that stretching never makes a filter narrower than its measured width,
        /// keeps every filter inside the padded row, ends each row exactly at the right padding edge, gives all
        /// filters of one row the same extra width, and keeps the column spacing between neighbours.
        /// </summary>
        [Theory]
        [InlineData(180)]
        [InlineData(240)]
        [InlineData(308)]
        [InlineData(332)]
        [InlineData(400)]
        public void Stretch_preserves_each_filters_measured_width_and_stays_inside_the_row(double width)
        {
            // Arrange: seven filters with different natural widths in a layout with 2 px side padding.
            var layout = CreateLayout();
            var buttons = new[] { 64d, 62d, 60d, 82d, 50d, 49d, 62d }
                .Select(naturalWidth => AddButton(layout, naturalWidth)).ToList();
            var manager = new DownloadFilterLayoutManager(layout.Mock.Object);

            // Act: measure for the given width, then arrange into exactly the measured height.
            var size = manager.Measure(width, double.PositiveInfinity);
            manager.ArrangeChildren(new Rect(0, 0, width, size.Height));

            // Assert, per filter: never narrower than its natural width and inside the padded area.
            foreach (var button in buttons)
            {
                button.Bounds.Width.Should().BeGreaterThanOrEqualTo(button.NaturalWidth - 0.001);
                button.Bounds.Left.Should().BeGreaterThanOrEqualTo(2);
                button.Bounds.Right.Should().BeLessThanOrEqualTo(width - 2 + 0.001);
            }

            // Assert, per row (filters sharing the same top): flush right edge, equal extra width, fixed gaps.
            foreach (var row in buttons.GroupBy(button => button.Bounds.Top))
            {
                row.Last().Bounds.Right.Should().BeApproximately(width - 2, 0.001);
                var extraWidth = row.First().Bounds.Width - row.First().NaturalWidth;
                foreach (var button in row)
                    (button.Bounds.Width - button.NaturalWidth).Should().BeApproximately(extraWidth, 0.001);

                foreach (var pair in row.Zip(row.Skip(1)))
                    (pair.Second.Bounds.Left - pair.First.Bounds.Right).Should().BeApproximately(4, 0.001);
            }
        }

        /// <summary>
        /// Verifies that four filters that fit one row share the free width equally on top of their natural widths,
        /// so the wider filter stays wider instead of being forced to an equal width as with a growing flex layout.
        /// </summary>
        [Fact]
        public void Wider_filter_is_not_forced_into_the_equal_width_used_by_FlexLayout_Grow()
        {
            var layout = CreateLayout();
            var buttons = new[] { 64d, 62d, 60d, 82d }
                .Select(naturalWidth => AddButton(layout, naturalWidth)).ToList();
            var manager = new DownloadFilterLayoutManager(layout.Mock.Object);

            manager.Measure(308, double.PositiveInfinity);
            manager.ArrangeChildren(new Rect(0, 0, 308, 24));

            buttons.Select(button => button.Bounds.Width).Should().Equal(70, 68, 66, 88);
            buttons.Select(button => button.Bounds.Top).Distinct().Should().ContainSingle();
        }


        // Reflow and visibility

        /// <summary>
        /// Verifies that arranging into a narrower area than the measured one rebuilds the rows, and that with a
        /// right-to-left flow the first filter sits at the right edge, rows are separated by the row spacing, and no
        /// filter shrinks below its natural width.
        /// </summary>
        [Fact]
        public void Resize_reflows_rows_and_respects_right_to_left_order()
        {
            var layout = CreateLayout();
            layout.Mock.SetupGet(value => value.FlowDirection).Returns(FlowDirection.RightToLeft);
            var buttons = new[] { 64d, 62d, 60d, 82d }
                .Select(naturalWidth => AddButton(layout, naturalWidth)).ToList();
            var manager = new DownloadFilterLayoutManager(layout.Mock.Object);
            manager.Measure(308, double.PositiveInfinity);

            manager.ArrangeChildren(new Rect(10, 20, 180, 100));

            buttons[0].Bounds.Right.Should().BeApproximately(188, 0.001);
            buttons[1].Bounds.Left.Should().BeApproximately(12, 0.001);
            buttons[2].Bounds.Top.Should().BeApproximately(buttons[0].Bounds.Bottom + 4, 0.001);
            foreach (var button in buttons)
                button.Bounds.Width.Should().BeGreaterThanOrEqualTo(button.NaturalWidth);
        }

        /// <summary>
        /// Verifies that a filter wider than the row is measured against the available width and arranged within the
        /// padded row, and that a collapsed filter is neither measured nor arranged and adds no height.
        /// </summary>
        [Fact]
        public void Long_filter_is_measured_with_the_viewport_limit_and_hidden_filters_take_no_space()
        {
            var layout = CreateLayout();
            var visible = AddButton(layout, 500);
            var hidden = AddButton(layout, 100, Visibility.Collapsed);
            var manager = new DownloadFilterLayoutManager(layout.Mock.Object);

            var size = manager.Measure(308, double.PositiveInfinity);
            manager.ArrangeChildren(new Rect(0, 0, 308, size.Height));

            visible.Bounds.Should().Be(new Rect(2, 0, 304, 24));
            hidden.Bounds.Should().Be(default(Rect));
            hidden.View.Verify(view => view.Measure(It.IsAny<double>(), It.IsAny<double>()), Times.Never);
            size.Height.Should().Be(24);
        }


        // Helpers

        /// <summary>
        /// Creates a layout double with 4 px spacing, stretching enabled, 2 px horizontal padding, and no size limits.
        /// </summary>
        private static FilterLayoutStub CreateLayout() => new();

        /// <summary>
        /// Adds a filter double to the layout. Its measurement reports the natural width limited by the width
        /// constraint and a fixed height of 24, and its arrangement records the bounds it was given.
        /// </summary>
        /// <param name="layout">Layout double that receives the filter.</param>
        /// <param name="width">Natural width the filter asks for.</param>
        /// <param name="visibility">Visibility reported by the filter.</param>
        private static ButtonMeasurement AddButton(FilterLayoutStub layout, double width,
            Visibility visibility = Visibility.Visible)
        {
            var button = new ButtonMeasurement(width);
            button.View.SetupGet(view => view.Visibility).Returns(visibility);
            button.View.SetupGet(view => view.DesiredSize).Returns(() => button.DesiredSize);
            button.View.Setup(view => view.Measure(It.IsAny<double>(), It.IsAny<double>()))
                .Returns((double widthConstraint, double _) =>
                    button.DesiredSize = new Size(Math.Min(width, widthConstraint), 24));
            button.View.Setup(view => view.Arrange(It.IsAny<Rect>()))
                .Returns((Rect bounds) =>
                {
                    button.Bounds = bounds;
                    return bounds.Size;
                });
            layout.Children.Add(button.View.Object);
            return button;
        }


        // Test doubles

        /// <summary>
        /// Mock of the filter layout settings together with the list of filters it enumerates.
        /// </summary>
        private sealed class FilterLayoutStub
        {
            /// <summary>
            /// Filters that the mocked layout enumerates, in order.
            /// </summary>
            public List<IView> Children { get; } = [];

            /// <summary>
            /// Moq double of the layout that the manager under test reads its settings from.
            /// </summary>
            public Mock<IDownloadFilterLayout> Mock { get; } = new();

            /// <summary>
            /// Configures 4 px column and row spacing, stretching, 2 px horizontal padding, undefined explicit
            /// size, and unlimited maximum size.
            /// </summary>
            public FilterLayoutStub()
            {
                Mock.SetupGet(layout => layout.ColumnSpacing).Returns(4);
                Mock.SetupGet(layout => layout.RowSpacing).Returns(4);
                Mock.SetupGet(layout => layout.StretchItems).Returns(true);
                Mock.SetupGet(layout => layout.Padding).Returns(new Thickness(2, 0));
                Mock.SetupGet(layout => layout.Width).Returns(double.NaN);
                Mock.SetupGet(layout => layout.Height).Returns(double.NaN);
                Mock.SetupGet(layout => layout.MaximumWidth).Returns(double.PositiveInfinity);
                Mock.SetupGet(layout => layout.MaximumHeight).Returns(double.PositiveInfinity);
                Mock.Setup(layout => layout.GetEnumerator()).Returns(() => Children.GetEnumerator());
            }
        }

        /// <summary>
        /// One mocked filter with the sizes the layout manager measured and arranged for it.
        /// </summary>
        private sealed class ButtonMeasurement(double naturalWidth)
        {
            /// <summary>
            /// Width the filter asks for when it has unlimited room.
            /// </summary>
            public double NaturalWidth { get; } = naturalWidth;

            /// <summary>
            /// Moq double of the filter view.
            /// </summary>
            public Mock<IView> View { get; } = new();

            /// <summary>
            /// Size returned by the latest measurement.
            /// </summary>
            public Size DesiredSize { get; set; }

            /// <summary>
            /// Bounds given by the latest arrangement; the default rectangle when it was never arranged.
            /// </summary>
            public Rect Bounds { get; set; }
        }
    }
}
