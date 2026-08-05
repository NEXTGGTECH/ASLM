// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleTrustServiceTests
{
    [Fact]
    public void Resolve_returns_official_for_catalog_module()
    {
        var service = new ModuleTrustService(TestLoggerFactory.Create<ModuleTrustService>());
        var module = ModuleConfigBuilder.Create(
            id: "aslm-chat",
            configure: m => m.Source.Repo = "NGGTLightKeeper/ASLM-Chat");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Official);
    }

    [Fact]
    public void Resolve_returns_unreviewed_for_unknown_module()
    {
        var service = new ModuleTrustService(TestLoggerFactory.Create<ModuleTrustService>());
        var module = ModuleConfigBuilder.Create(id: "unknown-module");

        service.Resolve(module).Should().Be(ModuleTrustLevel.Unreviewed);
    }
}
