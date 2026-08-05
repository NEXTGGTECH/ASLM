// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class ModuleDependencyResolverTests
{
    [Fact]
    public void ExpandInstallOrder_places_dependencies_before_dependents()
    {
        var chat = CreateModule("aslm-chat");
        var code = CreateModule("aslm-code", module =>
        {
            module.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "aslm-chat" });
        });

        var order = ModuleDependencyResolver.ExpandInstallOrder([code], [chat, code]);

        order.Select(module => module.Id).Should().Equal(["aslm-chat", "aslm-code"]);
    }

    [Fact]
    public void ExpandInstallOrder_deduplicates_shared_dependencies()
    {
        var chat = CreateModule("aslm-chat");
        var code = CreateModule("aslm-code", module =>
        {
            module.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "aslm-chat" });
        });
        var other = CreateModule("other-module", module =>
        {
            module.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "aslm-chat" });
        });

        var order = ModuleDependencyResolver.ExpandInstallOrder([code, other], [chat, code, other]);

        order.Select(module => module.Id).Should().Equal(["aslm-chat", "aslm-code", "other-module"]);
    }

    [Fact]
    public void GetDirectModuleDependencyIds_returns_trimmed_unique_ids()
    {
        var module = CreateModule("aslm-code", config =>
        {
            config.Dependencies.Modules.Add(new ModuleModuleDependency { Id = " aslm-chat " });
            config.Dependencies.Modules.Add(new ModuleModuleDependency { Id = "aslm-chat" });
        });

        ModuleDependencyResolver.GetDirectModuleDependencyIds(module).Should().Equal(["aslm-chat"]);
    }

    private static ModuleConfig CreateModule(string id, Action<ModuleConfig>? configure = null)
    {
        return ModuleConfigBuilder.Create(id: id, name: id, configure: configure);
    }
}
