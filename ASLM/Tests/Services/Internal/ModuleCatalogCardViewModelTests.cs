// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Covers setup selection policy on the cards shared with the download catalog.
    /// </summary>
    public sealed class ModuleCatalogCardViewModelTests
    {
        // Selection policy

        /// <summary>
        /// Verifies that required modules (matched by id ignoring case) start selected and cannot be deselected
        /// through the card binding.
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
        /// Verifies that optional modules start unselected, can be selected and deselected, and keep the selection
        /// through localization refreshes.
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
        /// Verifies that a module rejected by the platform checks is shown as unsupported and cannot be selected
        /// through the setup checkbox.
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
        /// Verifies that a regular download card, created without the selection option, shows no setup checkbox and
        /// has no selection state.
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


        // Helpers

        /// <summary>
        /// Creates a compatible setup card without native controls or filesystem mutations.
        /// </summary>
        private static ModuleCatalogCardViewModel CreateCard(string id) =>
            new(ModuleConfigBuilder.Create(id), ModuleTrustLevel.Official, null!, showSelection: true);
    }
}
