// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal;

/// <summary>
/// Covers setup selection policy on the cards shared with the download catalog.
/// </summary>
public sealed class ModuleCatalogCardViewModelTests
{
    /// <summary>
    /// Required modules start selected and cannot be deselected through the card binding.
    /// </summary>
    [Theory]
    [InlineData("aslm-chat")]
    [InlineData("ASLM-CHAT")]
    public void Required_modules_are_selected_and_locked(string id)
    {
        var card = CreateCard(id);

        card.IsSelected.Should().BeTrue();
        card.CanSelect.Should().BeFalse();
        card.IsSelected = false;
        card.IsSelected.Should().BeTrue();
    }

    /// <summary>
    /// Optional modules require an explicit selection and preserve it through localization refreshes.
    /// </summary>
    [Fact]
    public void Optional_modules_start_unselected_and_keep_the_users_choice()
    {
        var card = CreateCard("optional");
        card.IsSelected.Should().BeFalse();
        card.CanSelect.Should().BeTrue();

        card.IsSelected = true;
        card.RefreshLocalization();
        card.IsSelected.Should().BeTrue();

        card.IsSelected = false;
        card.IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Modules rejected by platform checks cannot be added through the setup checkbox.
    /// </summary>
    [Fact]
    public void Unsupported_optional_modules_cannot_be_selected()
    {
        var module = ModuleConfigBuilder.Create();
        module.SupportedPlatforms = [new() { Os = "macos", Arch = "arm64" }];
        module.ResolveForPlatform("windows", "amd64");
        var card = new ModuleCatalogCardViewModel(module, ModuleTrustLevel.Official, null!, showSelection: true);

        card.ShowUnsupported.Should().BeTrue();
        card.CanSelect.Should().BeFalse();
        card.IsSelected = true;
        card.IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Regular download cards expose navigation and status without setup selection state.
    /// </summary>
    [Fact]
    public void Downloads_do_not_offer_a_setup_checkbox()
    {
        var card = new ModuleCatalogCardViewModel(ModuleConfigBuilder.Create("aslm-chat"),
            ModuleTrustLevel.Official, null!);

        card.ShowSelection.Should().BeFalse();
        card.CanSelect.Should().BeFalse();
        card.IsSelected.Should().BeFalse();
    }

    /// <summary>
    /// Creates a compatible setup card without native controls or filesystem mutations.
    /// </summary>
    private static ModuleCatalogCardViewModel CreateCard(string id) =>
        new(ModuleConfigBuilder.Create(id), ModuleTrustLevel.Official, null!, showSelection: true);
}
