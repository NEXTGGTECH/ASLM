// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class PortRegistryTests
{
    [Fact]
    public void ResolveModulePagePortKey_prefers_http_setting()
    {
        var module = ModuleConfigBuilder.Create(configure: m =>
        {
            m.Settings =
            [
                new ModuleSetting { Key = "admin", Type = "port" },
                new ModuleSetting { Key = "http", Type = "port" }
            ];
        });

        PortRegistry.ResolveModulePagePortKey(module).Should().Be("http");
    }

    [Fact]
    public void GetOrAssignPorts_allocates_from_modules_start()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Ports.ModulesStart = 25000;

        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "sample-module");

        var ports = registry.GetOrAssignPorts(module);

        ports.Should().ContainKey("http");
        ports["http"].Should().Be(25000);
    }

    [Fact]
    public void GetOrAssignInternalServicePort_is_stable_for_service_id()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Ports.ModulesStart = 26000;

        var registry = new PortRegistry(appData);

        var first = registry.GetOrAssignInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey);
        var second = registry.GetOrAssignInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey);

        second.Should().Be(first);
        registry.TryGetInternalServicePort(PortRegistry.AslmMirrorServiceId, PortRegistry.AslmMirrorPortKey)
            .Should().Be(first);
    }

    [Fact]
    public void RemoveModulePorts_clears_assignments()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create();

        registry.GetOrAssignPorts(module);
        registry.RemoveModulePorts(module.Id);

        registry.TryGetPort(module.Id, "http").Should().BeNull();
    }

    [Fact]
    public void TryGetAssignedPorts_returns_existing_without_allocation()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Ports.ModulesStart = 27000;
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "snapshot-module");

        // Before any assignment the read-only view returns null.
        registry.TryGetAssignedPorts(module.Id).Should().BeNull();

        // After assignment the read-only view reflects the same ports.
        var assigned = registry.GetOrAssignPorts(module);
        var snapshot = registry.TryGetAssignedPorts(module.Id);

        snapshot.Should().NotBeNull();
        snapshot!["http"].Should().Be(assigned["http"]);
    }

    [Fact]
    public void TryGetAssignedPorts_does_not_persist_new_entry()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "never-assigned");

        registry.TryGetAssignedPorts(module.Id).Should().BeNull();

        // A second call on the same unassigned module still returns null.
        registry.TryGetAssignedPorts(module.Id).Should().BeNull();
    }

    [Fact]
    public void TryGetModulePageUrl_returns_loopback_for_assigned_module()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Ports.ModulesStart = 28000;
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "page-url-module");

        registry.GetOrAssignPorts(module);
        var url = registry.TryGetModulePageUrl(module);

        url.Should().StartWith("http://127.0.0.1:");
        url.Should().EndWith("/");
    }

    [Fact]
    public void TryGetModulePageUrl_returns_null_when_not_assigned()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "unassigned-page-url");

        registry.TryGetModulePageUrl(module).Should().BeNull();
    }

    [Fact]
    public void EnsurePortsAvailable_keeps_free_ports_unchanged()
    {
        _ = new AslmFileSystemLayout();
        var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
        appData.Data.Ports.ModulesStart = 30000;
        var registry = new PortRegistry(appData);
        var module = ModuleConfigBuilder.Create(id: "availability-module");

        var assigned = registry.GetOrAssignPorts(module);
        var changed = registry.EnsurePortsAvailable(module.Id);

        changed.Should().BeFalse();
        registry.TryGetAssignedPorts(module.Id)!["http"].Should().Be(assigned["http"]);
    }

    [Theory]
    [InlineData("ui-port", "ui")]
    [InlineData("api-port", "api")]
    [InlineData("example_port", "example")]
    [InlineData("http", "http")]
    [InlineData("server port", "server")]
    public void BuildHostRouteKey_strips_known_suffixes(string hostKey, string expected)
    {
        PortRegistry.BuildHostRouteKey(hostKey).Should().Be(expected);
    }
}
