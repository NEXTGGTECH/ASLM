// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Pages;
using Microsoft.Maui.Graphics;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers dashboard popup placement independently of card dimensions and native collection clipping.
/// </summary>
public sealed class ModulesViewMenuTests
{
    /// <summary>
    /// Additional actions can extend below a card without shrinking or clipping the menu.
    /// </summary>
    [Theory]
    [InlineData(130)]
    [InlineData(170)]
    [InlineData(250)]
    public void Menu_uses_the_full_content_height_when_the_dashboard_has_room(double height)
    {
        var anchor = new Rect(400, 40, 24, 24);
        var desired = new Size(200, height);

        var bounds = ModulesView.CalculateModuleMenuBounds(anchor, new Size(800, 600), desired);

        bounds.Size.Should().Be(desired);
        bounds.Top.Should().BeGreaterThan(anchor.Bottom);
        bounds.Right.Should().Be(anchor.Right);
    }

    /// <summary>
    /// A card near the bottom opens its menu above the button without dropping the last action.
    /// </summary>
    [Fact]
    public void Menu_opens_above_a_button_near_the_bottom_of_the_dashboard()
    {
        var anchor = new Rect(400, 560, 24, 24);
        var desired = new Size(200, 170);

        var bounds = ModulesView.CalculateModuleMenuBounds(anchor, new Size(800, 600), desired);

        bounds.Size.Should().Be(desired);
        bounds.Bottom.Should().BeLessThan(anchor.Top);
        bounds.Top.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Small windows constrain the scroll viewport rather than placing actions outside the application.
    /// </summary>
    [Fact]
    public void Oversized_menu_stays_within_the_dashboard_scroll_viewport()
    {
        var viewport = new Size(150, 100);

        var bounds = ModulesView.CalculateModuleMenuBounds(new Rect(2, 2, 24, 24), viewport, new Size(200, 250));

        bounds.Left.Should().BeGreaterThanOrEqualTo(0);
        bounds.Top.Should().BeGreaterThanOrEqualTo(0);
        bounds.Right.Should().BeLessThanOrEqualTo(viewport.Width);
        bounds.Bottom.Should().BeLessThanOrEqualTo(viewport.Height);
        bounds.Height.Should().BeLessThan(250);
    }
}
