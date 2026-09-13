// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Controls.Downloads;
using Microsoft.Maui;
using Microsoft.Maui.Graphics;
using Moq;

namespace ASLM.Tests.Services;

public sealed class DownloadFilterLayoutTests
{
    [Theory]
    [InlineData(180)]
    [InlineData(240)]
    [InlineData(308)]
    [InlineData(332)]
    [InlineData(400)]
    public void Stretch_preserves_each_filters_measured_width_and_stays_inside_the_row(double width)
    {
        var layout = CreateLayout();
        var buttons = new[] { 64d, 62d, 60d, 82d, 50d, 49d, 62d }
            .Select(naturalWidth => AddButton(layout, naturalWidth)).ToList();
        var manager = new DownloadFilterLayoutManager(layout.Mock.Object);

        var size = manager.Measure(width, double.PositiveInfinity);
        manager.ArrangeChildren(new Rect(0, 0, width, size.Height));

        foreach (var button in buttons)
        {
            button.Bounds.Width.Should().BeGreaterThanOrEqualTo(button.NaturalWidth - 0.001);
            button.Bounds.Left.Should().BeGreaterThanOrEqualTo(2);
            button.Bounds.Right.Should().BeLessThanOrEqualTo(width - 2 + 0.001);
        }

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

    private static FilterLayoutStub CreateLayout() => new();

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

    private sealed class FilterLayoutStub
    {
        public List<IView> Children { get; } = [];
        public Mock<IDownloadFilterLayout> Mock { get; } = new();

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

    private sealed class ButtonMeasurement(double naturalWidth)
    {
        public double NaturalWidth { get; } = naturalWidth;
        public Mock<IView> View { get; } = new();
        public Size DesiredSize { get; set; }
        public Rect Bounds { get; set; }
    }
}
