// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using ASLM.Pages;
using ASLM.Services.API;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.API;

/// <summary>
/// Covers persistent host ownership without starting an HTTP listener, plus the live
/// proxy behavior (index pages, backend forwarding, error pages) over real loopback HTTP.
/// </summary>
[Collection("ModuleManifestDiscovery")]
public sealed class AslmMirrorServerTests
{
    /// <summary>
    /// Both persisted port-map formats exclude removed modules and internal ASLM listeners.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Host_listing_uses_the_installed_registry_in_both_port_map_formats(bool legacy)
    {
        using var layout = new AslmFileSystemLayout();
        File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), """["kept"]""");
        layout.WritePortsJson(legacy
            ? """{"KEPT":25001,"removed":25002,"__aslm-mirror":25000}"""
            : """{"KEPT":{"http":25001},"removed":{"http":25002},"__aslm-mirror":{"mirror-port":25000}}""");
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var ports = new PortRegistry(appData);
        var installer = new ModuleInstaller(null!, null!, null!, ports: ports);

        // Reading routes must not rely on cleanup having run in this process.
        using var server = CreateServer(appData, ports, installer);
        server.GetHosts().Should().ContainSingle().Which.ModuleId.Should().Be("KEPT");
        using var restarted = CreateServer(appData, new PortRegistry(appData), installer);
        restarted.GetHosts().Should().ContainSingle().Which.ModuleId.Should().Be("KEPT");
        File.ReadAllText(layout.PortsFilePath).Should().Contain("removed",
            "route filtering must work even while an old assignment is still persisted");
    }

    /// <summary>
    /// An existing server stops exposing a removed module and accepts it again after reinstallation.
    /// </summary>
    [Fact]
    public void Host_listing_follows_removal_and_reinstallation_without_restarting()
    {
        using var layout = new AslmFileSystemLayout();
        File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), """["module"]""");
        layout.WritePortsJson("""{"module":{"http":25001}}""");
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var ports = new PortRegistry(appData);
        var installer = new ModuleInstaller(null!, null!, null!, ports: ports);
        using var server = CreateServer(appData, ports, installer);
        server.GetHosts().Should().ContainSingle();

        installer.Registry.Remove("module");
        server.GetHosts().Should().BeEmpty();

        installer.Registry.Add("module");
        server.GetHosts().Should().ContainSingle();
    }

    /// <summary>
    /// Missing display metadata must never imply that a module is enabled.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Display_state_requires_an_explicit_enabled_module(bool enabled)
    {
        var states = new Dictionary<string, AslmApiModuleDisplayState>(StringComparer.OrdinalIgnoreCase)
        {
            ["installed"] = new("Installed module", enabled)
        };

        AslmApiView.ResolveModuleDisplayState("installed", states).IsEnabled.Should().Be(enabled);
        AslmApiView.ResolveModuleDisplayState("removed", states).IsEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Creates a listener-free service around the isolated test layout.
    /// </summary>
    private static AslmMirrorServer CreateServer(AppDataStore appData, PortRegistry ports, ModuleInstaller installer) =>
        new(appData, ports, installer, TestLoggerFactory.Create<AslmMirrorServer>());

    /// <summary>
    /// A running proxy forwards module traffic to the assigned loopback port and reports
    /// unknown modules on its index page.
    /// </summary>
    [Fact]
    public async Task Enabled_server_proxies_module_routes_over_real_http()
    {
        using var graph = new TestServiceGraph();
        using var backend = LoopbackHttpServer.Start();
        backend.ServeJson("api/status", """{"state":"running"}""");
        // Register the module and point its http route at the fake backend port.
        File.WriteAllText(Path.Combine(graph.Root.DataAppDir, "ASLM_Modules.json"), """["proxied"]""");
        graph.Root.WriteDataAppFile("ASLM_Ports.json", """{"proxied":{"http":""" + backend.BaseUri.Port + "}}");
        var mirror = new AslmMirrorServer(graph.AppData, graph.Ports, graph.Installer,
            TestLoggerFactory.Create<AslmMirrorServer>());
        using var _ = mirror;
        graph.AppData.Data.Api.ServerEnabled = true;
        // RedistributePorts would rebuild the whole map; the live proxy path under test is
        // the routing itself, so the pre-seeded module assignment is preserved via StartAsync.
        await mirror.StartAsync();

        try
        {
            mirror.IsRunning.Should().BeTrue("the persisted setting enables the proxy");

            using var client = new HttpClient();
            using var proxied = await client.GetAsync(
                $"{mirror.BaseUrl}proxied/http/api/status");
            proxied.StatusCode.Should().Be(HttpStatusCode.OK);
            (await proxied.Content.ReadAsStringAsync()).Should().Contain("running");

            using var moduleIndex = await client.GetAsync($"{mirror.BaseUrl}proxied/");
            moduleIndex.StatusCode.Should().Be(HttpStatusCode.OK);

            using var unknown = await client.GetAsync($"{mirror.BaseUrl}ghost/");
            unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await unknown.Content.ReadAsStringAsync()).Should().Contain("ghost");

            using var root = await client.GetAsync(mirror.BaseUrl);
            root.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await mirror.StopAsync();
        }
    }

    /// <summary>
    /// A stopped proxy must not keep the loopback port reserved in the port map.
    /// </summary>
    [Fact]
    public async Task StopAsync_releases_the_listener_and_the_reserved_port()
    {
        using var graph = new TestServiceGraph();
        var mirror = new AslmMirrorServer(graph.AppData, graph.Ports, graph.Installer,
            TestLoggerFactory.Create<AslmMirrorServer>());
        using var _ = mirror;
        graph.AppData.Data.Api.ServerEnabled = true;
        await mirror.StartIfEnabledAsync();
        mirror.IsRunning.Should().BeTrue();
        var port = new Uri(mirror.BaseUrl).Port;

        await mirror.StopAsync();

        mirror.IsRunning.Should().BeFalse();
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        probe.Start();
    }
}
