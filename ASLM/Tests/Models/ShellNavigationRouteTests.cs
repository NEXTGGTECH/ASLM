// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Models
{
    /// <summary>
    /// Verifies the persisted shell-route contract independently from MAUI controls and runtime ports.
    /// </summary>
    public sealed class ShellNavigationRouteTests
    {
        /// <summary>
        /// Verifies that the startup page is the remembered page when restoring is enabled and the home page otherwise,
        /// that unknown or missing remembered pages count as home, and that a disabled home page is replaced by the
        /// modules page.
        /// </summary>
        [Theory]
        [InlineData(true, true, "home", "modules")]
        [InlineData(false, true, "home", "home")]
        [InlineData(true, true, null, "modules")]
        [InlineData(false, true, null, "home")]
        [InlineData(true, true, "unknown", "modules")]
        [InlineData(false, true, "unknown", "home")]
        [InlineData(true, true, "consoles", "consoles")]
        [InlineData(false, true, "modules", "modules")]
        [InlineData(true, true, "aslm-api", "aslm-api")]
        [InlineData(true, true, "module::aslm-chat", "module::aslm-chat")]
        [InlineData(false, true, "module::aslm-chat", "module::aslm-chat")]
        [InlineData(true, false, "consoles", "modules")]
        [InlineData(false, false, "modules", "home")]
        public void Initial_page_respects_home_visibility_and_last_page_preference(
            bool disableHome, bool restoreLastPage, string? lastPage, string expected)
        {
            var navigation = new AppNavigationConfig
            {
                DisableHomePage = disableHome,
                RestoreLastPage = restoreLastPage,
                LastPage = lastPage!
            };

            navigation.GetInitialPage().Should().Be(expected);
        }

        /// <summary>
        /// Verifies that normalization trims the route, matches the fixed pages case-insensitively, returns their
        /// canonical spelling, keeps well-formed module routes with a lower-case prefix, and maps everything else
        /// to home.
        /// </summary>
        [Theory]
        [InlineData(null, ShellNavigationRoute.Home)]
        [InlineData("", ShellNavigationRoute.Home)]
        [InlineData("unknown", ShellNavigationRoute.Home)]
        [InlineData(" HOME ", ShellNavigationRoute.Home)]
        [InlineData("Consoles", ShellNavigationRoute.Consoles)]
        [InlineData("modules", ShellNavigationRoute.Modules)]
        [InlineData("ASLM-API", ShellNavigationRoute.AslmApi)]
        [InlineData(" MODULE::aslm-chat ", "module::aslm-chat")]
        public void Normalize_returns_canonical_supported_route(string? route, string expected)
        {
            ShellNavigationRoute.Normalize(route).Should().Be(expected);
        }

        /// <summary>
        /// Verifies that a module route created from a padded id can be parsed back to the trimmed id and contains
        /// no local address, so persisted routes never depend on a port.
        /// </summary>
        [Fact]
        public void Module_route_preserves_identifier_without_local_url_or_port()
        {
            var route = ShellNavigationRoute.ForModule(" aslm-chat ");

            ShellNavigationRoute.TryGetModuleId(route, out var moduleId).Should().BeTrue();
            moduleId.Should().Be("aslm-chat");
            route.Should().NotContain("localhost");
            route.Should().NotContain("127.0.0.1");
        }

        /// <summary>
        /// Verifies that a module route with a blank module id is normalized to the home route.
        /// </summary>
        [Fact]
        public void Invalid_module_route_falls_back_to_home()
        {
            ShellNavigationRoute.Normalize("module::   ").Should().Be(ShellNavigationRoute.Home);
        }
    }
}
