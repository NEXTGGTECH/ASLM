// Copyright NEXTGGTECH. Apache License 2.0.

using System.Net;
using ASLM.Pages;
using ASLM.Services.API;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.API
{
    /// <summary>
    /// Covers persistent host ownership without starting an HTTP listener, plus the live
    /// proxy behavior (index pages, backend forwarding, error pages) over real loopback HTTP.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class AslmMirrorServerTests
    {
        // Host listing

        /// <summary>
        /// Verifies that, in both persisted port-map formats, the host listing contains only modules in the
        /// installed registry, matched case-insensitively, and excludes removed modules and the internal mirror
        /// listener, even when the stale assignment is still in the persisted file.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Host_listing_uses_the_installed_registry_in_both_port_map_formats(bool legacy)
        {
            // Arrange: one installed module plus a removed one and the mirror's own listener in the port map.
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

            // A second server with a fresh port registry sees the same filtered hosts.
            using var restarted = CreateServer(appData, new PortRegistry(appData), installer);
            restarted.GetHosts().Should().ContainSingle().Which.ModuleId.Should().Be("KEPT");
            File.ReadAllText(layout.PortsFilePath).Should().Contain("removed",
                "route filtering must work even while an old assignment is still persisted");
        }

        /// <summary>
        /// Verifies that one existing server stops listing a module removed from the installed registry and lists it
        /// again after it is re-added, without a restart.
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


        // Display state

        /// <summary>
        /// Verifies that the module display state is enabled only when the display metadata explicitly says so,
        /// and that a module without metadata is never reported as enabled.
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


        // Helpers

        /// <summary>
        /// Creates a listener-free service around the isolated test layout.
        /// </summary>
        private static AslmMirrorServer CreateServer(AppDataStore appData, PortRegistry ports, ModuleInstaller installer) =>
            new(appData, ports, installer, TestLoggerFactory.Create<AslmMirrorServer>());


        // Live proxy

        /// <summary>
        /// Verifies that a running, enabled proxy forwards <c>{module}/http/...</c> requests to the module's
        /// loopback backend, serves the module index and the root index, and answers a request for an unknown
        /// module with a 404 page that names it.
        /// </summary>
        [Fact]
        public async Task Enabled_server_proxies_module_routes_over_real_http()
        {
            // Arrange: a fake backend that serves a JSON status endpoint.
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

            // Act: start the proxy. RedistributePorts would rebuild the whole map; the live proxy path under test
            // is the routing itself, so the pre-seeded module assignment is preserved via StartAsync.
            await mirror.StartAsync();

            try
            {
                mirror.IsRunning.Should().BeTrue("the persisted setting enables the proxy");

                // Assert, forwarding: a module route reaches the backend and returns its payload.
                using var client = new HttpClient();
                using var proxied = await client.GetAsync(
                    $"{mirror.BaseUrl}proxied/http/api/status");
                proxied.StatusCode.Should().Be(HttpStatusCode.OK);
                (await proxied.Content.ReadAsStringAsync()).Should().Contain("running");

                // Assert, module index: the proxy itself serves the index page of a registered module.
                using var moduleIndex = await client.GetAsync($"{mirror.BaseUrl}proxied/");
                moduleIndex.StatusCode.Should().Be(HttpStatusCode.OK);

                // Assert, error page: an unregistered module yields a 404 page that names it.
                using var unknown = await client.GetAsync($"{mirror.BaseUrl}ghost/");
                unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
                (await unknown.Content.ReadAsStringAsync()).Should().Contain("ghost");

                // Assert, root index: the proxy's root address answers as well.
                using var root = await client.GetAsync(mirror.BaseUrl);
                root.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally
            {
                await mirror.StopAsync();
            }
        }

        /// <summary>
        /// Verifies that stopping a running proxy marks it as not running and frees its loopback port, so a new
        /// listener can bind the same port.
        /// </summary>
        [Fact]
        public async Task StopAsync_releases_the_listener_and_the_reserved_port()
        {
            // Arrange: start the proxy through the enabled setting and remember its port.
            using var graph = new TestServiceGraph();
            var mirror = new AslmMirrorServer(graph.AppData, graph.Ports, graph.Installer,
                TestLoggerFactory.Create<AslmMirrorServer>());
            using var _ = mirror;
            graph.AppData.Data.Api.ServerEnabled = true;
            await mirror.StartIfEnabledAsync();
            mirror.IsRunning.Should().BeTrue();
            var port = new Uri(mirror.BaseUrl).Port;

            // Act: stop the proxy.
            await mirror.StopAsync();

            // Assert: the proxy is stopped and the port can be bound by another listener.
            mirror.IsRunning.Should().BeFalse();
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
            probe.Start();
        }
    }
}
