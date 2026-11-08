// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies per-module port allocation, persistence, lookup and cleanup in the port registry.
    /// </summary>
    public sealed class PortRegistryTests
    {
        // Redistribution and cleanup

        /// <summary>
        /// Verifies that startup redistribution discards legacy reservations for removed modules even when their
        /// catalog manifests remain, while registered modules and the internal mirror and interop owners keep theirs.
        /// </summary>
        [Fact]
        public void Redistribution_removes_unregistered_catalog_ports_but_keeps_installed_and_internal_owners()
        {
            // Arrange: only "kept" is registered, and a removed module still leaves a manifest in the catalog.
            using var layout = new AslmFileSystemLayout();
            File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), """["kept"]""");
            var catalog = ModuleConfigBuilder.Create(id: "removed-" + Guid.NewGuid().ToString("N"));
            var directory = Path.GetDirectoryName(catalog.SourcePath)!;
            Directory.CreateDirectory(directory);
            try
            {
                // A retained uninstalled manifest is the regression that defeated the old directory scan.
                File.WriteAllText(catalog.SourcePath, System.Text.Json.JsonSerializer.Serialize(catalog));

                // A persisted port map with an entry for the registered module, the removed one and the mirror server.
                layout.WritePortsJson(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, int>>
                {
                    ["kept"] = new() { ["http"] = 25001 },
                    [catalog.Id] = new() { ["http"] = 25002 },
                    ["__aslm-mirror"] = new() { ["mirror-port"] = 25000 }
                }));
                var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
                var registry = new PortRegistry(appData);

                // Act: redistribute as at startup, with the mirror server reserved.
                registry.RedistributePorts(reserveAslmMirrorServer: true);

                // Assert: the removed module lost its ports, the others kept theirs, and the result was persisted.
                registry.TryGetAssignedPorts(catalog.Id).Should().BeNull();
                registry.TryGetAssignedPorts("kept").Should().NotBeNull();
                registry.TryGetAssignedPorts(PortRegistry.AslmMirrorServiceId).Should().NotBeNull();
                registry.TryGetAssignedPorts(PortRegistry.AslmModuleInteropServiceId).Should().NotBeNull();
                new PortRegistry(appData).TryGetAssignedPorts(catalog.Id).Should().BeNull();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// Verifies that cleanup preserves reservations made by first-run commands before registration is
        /// committed, and that they disappear only after an explicit removal.
        /// </summary>
        [Fact]
        public void Cleanup_keeps_pending_installation_ports_until_explicit_removal()
        {
            // Arrange: an empty module registry and a module that reserved ports while still installing.
            using var layout = new AslmFileSystemLayout();
            File.WriteAllText(Path.Combine(layout.DataAppDir, "ASLM_Modules.json"), "[]");
            var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            var registry = new PortRegistry(appData);
            var module = ModuleConfigBuilder.Create(id: "installing");
            registry.GetOrAssignPorts(module);

            // The redistribution cleanup keeps the pending reservation although the module is not registered.
            registry.RedistributePorts(reserveAslmMirrorServer: false);
            registry.TryGetAssignedPorts(module.Id).Should().NotBeNull();

            // After an explicit removal the next cleanup leaves nothing behind.
            registry.RemoveModulePorts(module.Id);
            registry.RedistributePorts(reserveAslmMirrorServer: false);
            registry.TryGetAssignedPorts(module.Id).Should().BeNull();
        }


        // Port assignment

        /// <summary>
        /// Verifies that the page port key is the <c>http</c> setting even when another port setting is declared
        /// before it.
        /// </summary>
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

        /// <summary>
        /// Verifies that the first module's <c>http</c> port is the configured start of the module port range.
        /// </summary>
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

        /// <summary>
        /// Verifies that asking twice for the mirror service port returns the same port, which the read-only lookup
        /// reports as well.
        /// </summary>
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

        /// <summary>
        /// Verifies that removing a module's ports leaves it without an <c>http</c> port.
        /// </summary>
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


        // Read-only queries

        /// <summary>
        /// Verifies that the read-only lookup returns null before a module has ports and the same ports once they
        /// were assigned.
        /// </summary>
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

        /// <summary>
        /// Verifies that looking up an unassigned module returns null repeatedly instead of creating an entry.
        /// </summary>
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

        /// <summary>
        /// Verifies that an assigned module gets a loopback page URL that ends with a slash.
        /// </summary>
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

        /// <summary>
        /// Verifies that a module without assigned ports has no page URL.
        /// </summary>
        [Fact]
        public void TryGetModulePageUrl_returns_null_when_not_assigned()
        {
            _ = new AslmFileSystemLayout();
            var appData = new AppDataStore(TestLoggerFactory.Create<AppDataStore>());
            var registry = new PortRegistry(appData);
            var module = ModuleConfigBuilder.Create(id: "unassigned-page-url");

            registry.TryGetModulePageUrl(module).Should().BeNull();
        }


        // Port availability

        /// <summary>
        /// Verifies that checking availability reports no change and keeps the assigned port when it can still be
        /// bound.
        /// </summary>
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


        // Route keys

        /// <summary>
        /// Verifies that the <c>-port</c>, <c>_port</c> and <c> port</c> suffixes are removed from a host key to
        /// form its route key, and that a key without such a suffix is returned unchanged.
        /// </summary>
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
}
