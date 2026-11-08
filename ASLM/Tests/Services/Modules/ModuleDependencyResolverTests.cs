// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies module-to-module dependency expansion: install order, deduplication, cycle and missing-dependency
    /// detection, and direct dependency id listing.
    /// </summary>
    public sealed class ModuleDependencyResolverTests
    {
        // Install order

        /// <summary>
        /// Verifies that selecting a module whose manifest depends on another module yields the dependency first.
        /// </summary>
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

        /// <summary>
        /// Verifies that a dependency shared by two selected modules appears only once, ahead of both dependents.
        /// </summary>
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

        /// <summary>
        /// Verifies that the same dependency id declared twice, once with surrounding whitespace, is listed once and
        /// trimmed.
        /// </summary>
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

        /// <summary>
        /// Verifies that two modules depending on each other make the expansion throw an
        /// <see cref="InvalidOperationException"/> instead of recursing endlessly.
        /// </summary>
        [Fact]
        public void ExpandInstallOrder_rejects_cycles_without_recursing_forever()
        {
            var first = CreateModule("first", module => module.Dependencies.Modules.Add(new() { Id = "second" }));
            var second = CreateModule("second", module => module.Dependencies.Modules.Add(new() { Id = "first" }));
            var resolve = () => ModuleDependencyResolver.ExpandInstallOrder([first], [first, second]);
            resolve.Should().Throw<InvalidOperationException>();
        }

        /// <summary>
        /// Verifies that a dependency absent from the catalog makes the expansion throw an
        /// <see cref="InvalidOperationException"/> before any module would be installed.
        /// </summary>
        [Fact]
        public void ExpandInstallOrder_rejects_missing_dependencies_before_installation()
        {
            var module = CreateModule("module", item => item.Dependencies.Modules.Add(new() { Id = "missing" }));
            var resolve = () => ModuleDependencyResolver.ExpandInstallOrder([module], [module]);
            resolve.Should().Throw<InvalidOperationException>();
        }


        // Test helpers

        /// <summary>
        /// Creates a module manifest whose id and name are the given id, optionally customized by the caller.
        /// </summary>
        private static ModuleConfig CreateModule(string id, Action<ModuleConfig>? configure = null)
        {
            return ModuleConfigBuilder.Create(id: id, name: id, configure: configure);
        }
    }
}
