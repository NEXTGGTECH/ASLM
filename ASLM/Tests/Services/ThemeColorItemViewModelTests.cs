// Copyright NEXTGGTECH. Apache License 2.0.

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies the bindable custom-theme palette rows used by the XAML editor.
/// </summary>
public sealed class ThemeColorItemViewModelTests
{
    /// <summary>
    /// Verifies a valid override updates both its display value and rendered swatch.
    /// </summary>
    [Fact]
    public void SetHex_uses_valid_override_for_display_and_swatch()
    {
        var item = CreateItem(Color.FromArgb("#FF010203"));

        item.SetHex("#FF336699");

        item.DisplayValue.Should().Be("#FF336699");
        ThemePaletteResolver.ToHex(item.SwatchColor).Should().Be("#FF336699");
    }

    /// <summary>
    /// Verifies clearing an override restores the inherited base color in the template model.
    /// </summary>
    [Fact]
    public void SetHex_restores_fallback_for_empty_override()
    {
        var fallback = Color.FromArgb("#FF102030");
        var item = CreateItem(fallback);

        item.SetHex(null);

        item.DisplayValue.Should().Be("—");
        item.SwatchColor.Should().Be(fallback);
    }

    /// <summary>
    /// Verifies XAML commands delegate behavior without embedding page services into the row view.
    /// </summary>
    [Fact]
    public void Commands_delegate_pick_and_clear_actions()
    {
        var picked = false;
        var cleared = false;
        var item = new ThemeColorItemViewModel(
            "ActionBlue",
            null,
            Colors.Blue,
            "Pick",
            "Clear",
            _ =>
            {
                picked = true;
                return Task.CompletedTask;
            },
            _ => cleared = true);

        item.PickCommand.Execute(null);
        item.ClearCommand.Execute(null);

        picked.Should().BeTrue();
        cleared.Should().BeTrue();
    }

    /// <summary>
    /// Creates one palette item with inert actions for value-focused tests.
    /// </summary>
    private static ThemeColorItemViewModel CreateItem(Color fallback) =>
        new(
            "ActionBlue",
            null,
            fallback,
            "Pick",
            "Clear",
            static _ => Task.CompletedTask,
            static _ => { });
}
