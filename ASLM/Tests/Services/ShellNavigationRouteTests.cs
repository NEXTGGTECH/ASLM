// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies the persisted shell-route contract independently from MAUI controls and runtime ports.
/// </summary>
public sealed class ShellNavigationRouteTests
{
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

    [Fact]
    public void Module_route_preserves_identifier_without_local_url_or_port()
    {
        var route = ShellNavigationRoute.ForModule(" aslm-chat ");

        ShellNavigationRoute.TryGetModuleId(route, out var moduleId).Should().BeTrue();
        moduleId.Should().Be("aslm-chat");
        route.Should().NotContain("localhost");
        route.Should().NotContain("127.0.0.1");
    }

    [Fact]
    public void Invalid_module_route_falls_back_to_home()
    {
        ShellNavigationRoute.Normalize("module::   ").Should().Be(ShellNavigationRoute.Home);
    }
}
