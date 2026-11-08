// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies the ports and hosts payload that the module interop API builds for running modules.
    /// </summary>
    public sealed class ModuleInteropPortsBuilderTests
    {
        // ASLM API block

        /// <summary>
        /// Verifies that a disabled ASLM API yields only <c>Enabled</c> = false, even when a port and a running
        /// listener are passed in.
        /// </summary>
        [Fact]
        public void BuildAslmApiDto_returns_disabled_when_api_not_enabled()
        {
            var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: false, apiPort: 20000, apiRunning: true);

            dto.Enabled.Should().BeFalse();
            dto.Running.Should().BeNull();
            dto.Port.Should().BeNull();
            dto.BaseUrl.Should().BeNull();
        }

        /// <summary>
        /// Verifies that an enabled and running ASLM API reports its port and the loopback base URL of that port.
        /// </summary>
        [Fact]
        public void BuildAslmApiDto_returns_full_info_when_enabled()
        {
            var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: 20000, apiRunning: true);

            dto.Enabled.Should().BeTrue();
            dto.Running.Should().BeTrue();
            dto.Port.Should().Be(20000);
            dto.BaseUrl.Should().Be("http://127.0.0.1:20000/");
        }

        /// <summary>
        /// Verifies that an enabled ASLM API whose listener is down still reports its port but with
        /// <c>Running</c> = false.
        /// </summary>
        [Fact]
        public void BuildAslmApiDto_propagates_not_running_when_listener_down()
        {
            var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: 20000, apiRunning: false);

            dto.Enabled.Should().BeTrue();
            dto.Running.Should().BeFalse();
            dto.Port.Should().Be(20000);
        }

        /// <summary>
        /// Verifies that an enabled ASLM API without an assigned port has neither a port nor a base URL.
        /// </summary>
        [Fact]
        public void BuildAslmApiDto_handles_null_port_when_enabled()
        {
            var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: null, apiRunning: false);

            dto.Enabled.Should().BeTrue();
            dto.Port.Should().BeNull();
            dto.BaseUrl.Should().BeNull();
        }


        // Host entries

        /// <summary>
        /// Verifies that a module without any assigned ports has no host entries.
        /// </summary>
        [Fact]
        public void BuildHosts_returns_empty_when_no_assigned_ports()
        {
            var module = ModuleConfigBuilder.Create();
            var hosts = ModuleInteropPortsBuilder.BuildHosts(module, null, "http://127.0.0.1:20000/");

            hosts.Should().BeEmpty();
        }

        /// <summary>
        /// Verifies that an assigned <c>ui-port</c> becomes a host with route key <c>ui</c>, a direct loopback target
        /// URL and a mirror URL below the API base URL and the module id.
        /// </summary>
        [Fact]
        public void BuildHosts_builds_host_with_mirror_url_when_api_enabled()
        {
            var module = ModuleConfigBuilder.Create(id: "aslm-chat");
            var ports = new Dictionary<string, int> { ["ui-port"] = 20002 };

            var hosts = ModuleInteropPortsBuilder.BuildHosts(module, ports, "http://127.0.0.1:20000/");

            hosts.Should().HaveCount(1);
            var host = hosts[0];
            host.HostKey.Should().Be("ui-port");
            host.RouteKey.Should().Be("ui");
            host.Port.Should().Be(20002);
            host.TargetUrl.Should().Be("http://127.0.0.1:20002/");
            host.MirrorUrl.Should().Be("http://127.0.0.1:20000/aslm-chat/ui/");
        }

        /// <summary>
        /// Verifies that without an API mirror base URL the host has no mirror URL but keeps its direct target URL.
        /// </summary>
        [Fact]
        public void BuildHosts_sets_mirror_url_to_null_when_api_disabled()
        {
            var module = ModuleConfigBuilder.Create(id: "aslm-chat");
            var ports = new Dictionary<string, int> { ["ui-port"] = 20002 };

            var hosts = ModuleInteropPortsBuilder.BuildHosts(module, ports, apiMirrorBaseUrl: null);

            hosts.Should().HaveCount(1);
            hosts[0].MirrorUrl.Should().BeNull();
            hosts[0].TargetUrl.Should().Be("http://127.0.0.1:20002/");
        }

        /// <summary>
        /// Verifies that a port entry with the value 0 is omitted from the hosts.
        /// </summary>
        [Fact]
        public void BuildHosts_skips_zero_ports()
        {
            var module = ModuleConfigBuilder.Create();
            var ports = new Dictionary<string, int>
            {
                ["ui-port"] = 20002,
                ["api-port"] = 0
            };

            var hosts = ModuleInteropPortsBuilder.BuildHosts(module, ports, null);

            hosts.Should().HaveCount(1);
            hosts[0].HostKey.Should().Be("ui-port");
        }

        /// <summary>
        /// Verifies that every assigned port becomes its own host and that all mirror URLs contain the module id.
        /// </summary>
        [Fact]
        public void BuildHosts_handles_multiple_ports()
        {
            var module = ModuleConfigBuilder.Create(id: "multi");
            var ports = new Dictionary<string, int>
            {
                ["ui-port"] = 20002,
                ["api-port"] = 20003
            };

            var hosts = ModuleInteropPortsBuilder.BuildHosts(module, ports, "http://127.0.0.1:20000/");

            hosts.Should().HaveCount(2);
            hosts.Select(h => h.HostKey).Should().Contain("ui-port").And.Contain("api-port");
            hosts.Select(h => h.MirrorUrl).Should().AllSatisfy(u => u.Should().Contain("/multi/"));
        }


        // Mirror base URL

        /// <summary>
        /// Verifies that a disabled ASLM API has no mirror base URL even when a port is known.
        /// </summary>
        [Fact]
        public void ResolveMirrorBaseUrl_returns_null_when_disabled()
        {
            ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: false, apiPort: 20000)
                .Should().BeNull();
        }

        /// <summary>
        /// Verifies that an enabled ASLM API without an assigned port has no mirror base URL.
        /// </summary>
        [Fact]
        public void ResolveMirrorBaseUrl_returns_null_when_no_port()
        {
            ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: true, apiPort: null)
                .Should().BeNull();
        }

        /// <summary>
        /// Verifies that an enabled ASLM API with a port yields the loopback root URL of that port.
        /// </summary>
        [Fact]
        public void ResolveMirrorBaseUrl_returns_loopback_url_when_enabled()
        {
            ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: true, apiPort: 20000)
                .Should().Be("http://127.0.0.1:20000/");
        }


        // Running module ports

        /// <summary>
        /// Verifies that a module with ports assigned by the registry reports its loopback page URL and one host.
        /// </summary>
        [Fact]
        public void BuildRunningModulePorts_includes_page_url_and_hosts()
        {
            _ = new AslmFileSystemLayout();
            var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            appData.Data.Ports.ModulesStart = 29000;
            var portRegistry = new PortRegistry(appData);
            var module = ModuleConfigBuilder.Create(id: "running-mod");

            portRegistry.GetOrAssignPorts(module);
            var dto = ModuleInteropPortsBuilder.BuildRunningModulePorts(module, portRegistry, null);

            dto.Id.Should().Be("running-mod");
            dto.PageUrl.Should().StartWith("http://127.0.0.1:");
            dto.Hosts.Should().HaveCount(1);
        }

        /// <summary>
        /// Verifies that a module without assigned ports has no page URL and no hosts.
        /// </summary>
        [Fact]
        public void BuildRunningModulePorts_returns_empty_hosts_and_null_url_when_no_assignment()
        {
            _ = new AslmFileSystemLayout();
            var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            var portRegistry = new PortRegistry(appData);
            var module = ModuleConfigBuilder.Create(id: "no-ports-mod");

            var dto = ModuleInteropPortsBuilder.BuildRunningModulePorts(module, portRegistry, null);

            dto.PageUrl.Should().BeNull();
            dto.Hosts.Should().BeEmpty();
        }
    }
}
