// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Pages;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

/// <summary>
/// Covers persistent host ownership without starting an HTTP listener or module processes.
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
}
