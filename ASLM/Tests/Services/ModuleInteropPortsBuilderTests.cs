// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleInteropPortsBuilderTests
{
    // BuildAslmApiDto

    [Fact]
    public void BuildAslmApiDto_returns_disabled_when_api_not_enabled()
    {
        var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: false, apiPort: 20000, apiRunning: true);

        dto.Enabled.Should().BeFalse();
        dto.Running.Should().BeNull();
        dto.Port.Should().BeNull();
        dto.BaseUrl.Should().BeNull();
    }

    [Fact]
    public void BuildAslmApiDto_returns_full_info_when_enabled()
    {
        var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: 20000, apiRunning: true);

        dto.Enabled.Should().BeTrue();
        dto.Running.Should().BeTrue();
        dto.Port.Should().Be(20000);
        dto.BaseUrl.Should().Be("http://127.0.0.1:20000/");
    }

    [Fact]
    public void BuildAslmApiDto_propagates_not_running_when_listener_down()
    {
        var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: 20000, apiRunning: false);

        dto.Enabled.Should().BeTrue();
        dto.Running.Should().BeFalse();
        dto.Port.Should().Be(20000);
    }

    [Fact]
    public void BuildAslmApiDto_handles_null_port_when_enabled()
    {
        var dto = ModuleInteropPortsBuilder.BuildAslmApiDto(apiEnabled: true, apiPort: null, apiRunning: false);

        dto.Enabled.Should().BeTrue();
        dto.Port.Should().BeNull();
        dto.BaseUrl.Should().BeNull();
    }

    // BuildHosts

    [Fact]
    public void BuildHosts_returns_empty_when_no_assigned_ports()
    {
        var module = ModuleConfigBuilder.Create();
        var hosts = ModuleInteropPortsBuilder.BuildHosts(module, null, "http://127.0.0.1:20000/");

        hosts.Should().BeEmpty();
    }

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

    // ResolveMirrorBaseUrl

    [Fact]
    public void ResolveMirrorBaseUrl_returns_null_when_disabled()
    {
        ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: false, apiPort: 20000)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveMirrorBaseUrl_returns_null_when_no_port()
    {
        ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: true, apiPort: null)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveMirrorBaseUrl_returns_loopback_url_when_enabled()
    {
        ModuleInteropPortsBuilder.ResolveMirrorBaseUrl(apiEnabled: true, apiPort: 20000)
            .Should().Be("http://127.0.0.1:20000/");
    }

    // BuildRunningModulePorts

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
