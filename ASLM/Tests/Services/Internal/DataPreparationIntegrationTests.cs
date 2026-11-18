// Copyright NEXTGGTECH. Apache License 2.0.

using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies preparation with genuine bridge and setting processes over isolated module installations.
    /// </summary>
    [Collection("ModuleManifestDiscovery")]
    public sealed class DataPreparationIntegrationTests
    {
        // Session lifetime

        /// <summary>
        /// Verifies startup warms settings, categories, lists and details, and reopening performs no process calls.
        /// </summary>
        [Fact]
        public async Task Startup_prepares_all_module_data_and_navigation_reuses_it()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            await using var service = new DataPreparationService(queue, graph.Installer, graph.Catalog, settings, graph.Updates);
            service.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.WhenIdleAsync(timeout.Token);

            var directory = Path.GetDirectoryName(module.SourcePath)!;
            var operations = await File.ReadAllLinesAsync(Path.Combine(directory, "bridge-calls"));
            operations.Should().Equal("list_categories", "list_items", "describe_item");
            var value = await settings.LoadSettingValueAsync(module, module.Settings[0], timeout.Token);
            value.Value.Should().Be("initial");
            var snapshot = await graph.Catalog.LoadCatalogAsync(preferCached: true, ct: timeout.Token);
            snapshot.Categories.Should().ContainSingle().Which.Items.Should().ContainSingle();
            await graph.Catalog.GetItemDetailAsync(snapshot.Categories[0].Items[0], preferCached: true, ct: timeout.Token);
            File.ReadAllLines(Path.Combine(directory, "bridge-calls")).Should().Equal(operations);
            File.ReadAllLines(Path.Combine(directory, "getter-calls")).Should().ContainSingle();
        }

        /// <summary>
        /// Verifies closing during discovery still completes the remaining list requests and caches their data.
        /// </summary>
        [Fact]
        public async Task Closing_downloads_during_discovery_does_not_cancel_the_remaining_categories()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var module = await SeedAsync(graph);
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            await File.WriteAllTextAsync(Path.Combine(directory, "hold"), "wait");
            using var page = new CancellationTokenSource();
            var load = graph.Catalog.LoadCatalogAsync(preferCached: true, ct: page.Token);
            await TestWait.UntilAsync(() => File.Exists(Path.Combine(directory, "bridge-calls")),
                because: "the bridge must start before the page closes");
            page.Cancel();
            File.Delete(Path.Combine(directory, "hold"));
            var snapshot = await load.WaitAsync(TimeSpan.FromSeconds(15));
            snapshot.Categories[0].Items.Should().ContainSingle();
            await graph.Catalog.LoadCatalogAsync(preferCached: true);
            File.ReadAllLines(Path.Combine(directory, "bridge-calls")).Should().Equal("list_categories", "list_items");
        }


        // Module mutations

        /// <summary>
        /// Verifies equal resource keys in different categories retain both category-specific item descriptions.
        /// </summary>
        [Fact]
        public async Task Preparation_describes_matching_resource_keys_in_every_provider_category()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var module = await SeedAsync(graph);
            module.DownloadsBridge!.EntryPoint = Command("""
                $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
                Add-Content -LiteralPath 'categories' -Value ($request.operation + ':' + $request.categoryId)
                switch ($request.operation) {
                    'list_categories' { '{"success":true,"categories":[{"id":"one"},{"id":"two"}]}' }
                    'list_items' { '{"success":true,"items":[{"resourceKey":"shared","title":"Shared"}]}' }
                    'describe_item' { '{"success":true,"itemDetail":{"resourceKey":"shared"}}' }
                }
                """);
            await graph.Installer.SaveConfigAsync(module);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            await using var service = new DataPreparationService(queue, graph.Installer, graph.Catalog, settings, graph.Updates);
            service.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await service.WhenIdleAsync(timeout.Token);
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "categories"))
                .Where(line => line.StartsWith("describe_item", StringComparison.Ordinal))
                .Should().Equal("describe_item:one", "describe_item:two");
        }

        /// <summary>
        /// Verifies saving before a schema-change notification cannot resurrect removed keys or overwrite new metadata.
        /// </summary>
        [Fact]
        public async Task Saving_a_stale_draft_rebases_edits_onto_the_committed_schema()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            module.Settings[0].SetExec = Command("[IO.File]::WriteAllText('value', 'edited')");
            module.Settings.Add(new ModuleSetting { Key = "removed", Type = "string", Default = "old" });
            await graph.Installer.SaveConfigAsync(module);
            var draft = new ModuleSettingsDraft(module);
            draft.GetSetting("value").Value = "edited";
            draft.GetSetting("removed").Value = "obsolete edit";
            var updated = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            updated.Version = "2.0";
            updated.Settings[0].Value = "edited";
            updated.Settings.RemoveAll(setting => setting.Key == "removed");
            updated.Settings.Add(new ModuleSetting { Key = "new", Type = "string", Default = "new default" });
            await graph.Installer.SaveConfigAsync(updated, raiseModulesChanged: false);
            await settings.SaveActiveModuleAsync(draft);
            var saved = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            saved.Version.Should().Be("2.0");
            saved.Settings.Should().NotContain(setting => setting.Key == "removed");
            saved.Settings.Single(setting => setting.Key == "value").Value.Should().Be("edited");
            saved.Settings.Single(setting => setting.Key == "new").Default.Should().Be("new default");
            draft.HasChanges.Should().BeFalse();
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "value")).Should().Be("edited");
        }

        /// <summary>
        /// Verifies prepared artwork needs no further filesystem reads and response models are detached per consumer.
        /// </summary>
        [Fact]
        public async Task Prepared_bridge_artwork_survives_missing_source_files_without_sharing_mutable_fields()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var module = await SeedAsync(graph);
            var icon = Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "icon.png");
            await File.WriteAllBytesAsync(icon, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
            var json = """
                {"success":true,"resources":{"icons":{"test":{"path":"icon.png"}}},
                "items":[{"resourceKey":"item","title":"Original","details":[{"text":"Icon","icon":"test"}]}]}
                """;
            module.DownloadsBridge!.EntryPoint = Command("[Console]::In.ReadToEnd() | Out-Null; [Console]::Out.Write('" +
                json.Replace("'", "''") + "')");
            var first = await graph.Bridge.GetItemsAsync(module, "test");
            first.Items.Single().Details!.Single().Image.Should().NotBeNull();
            first.Items[0].Title = "Changed by a consumer";
            File.Delete(icon);
            var second = await graph.Bridge.GetItemsAsync(module, "test");
            second.Items.Single().Title.Should().Be("Original");
            second.Items[0].Details!.Single().Image.Should().BeSameAs(first.Items[0].Details!.Single().Image);
        }

        /// <summary>
        /// Verifies the real archive update invalidates cached content even though its manifest saves suppress events.
        /// </summary>
        [Fact]
        public async Task Archive_update_reloads_prepared_data_without_a_view_or_modules_changed_notification()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            await using var service = new DataPreparationService(queue, graph.Installer, graph.Catalog, settings, graph.Updates);
            service.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.WhenIdleAsync(timeout.Token);

            module.Version = "2.0";
            module.Update.RunFirstRunAfterUpdate = false;
            module.Settings.Add(new ModuleSetting { Key = "added", Type = "string", Default = "added" });
            var archive = Path.Combine(graph.Root.Root, "update.zip");
            TestArchives.WriteZip(archive,
                new TestArchives.Entry("module/ASLM_Module.json", JsonSerializer.Serialize(module)),
                new TestArchives.Entry("module/value", "new version"));
            const string url = "https://example.test/module.zip";
            graph.GitHubHttp.OnBytes(url, await File.ReadAllBytesAsync(archive));
            var original = (await graph.Installer.DiscoverInstalledModulesAsync()).Single();
            var changed = 0;
            graph.Installer.ModulesChanged += (_, _) => changed++;
            var success = await graph.Updates.ApplyModuleUpdateAsync(new UpdateCandidate
            {
                Module = original, TargetKind = "module", TargetId = original.Id, Name = original.Name,
                Mode = "release", RemoteVersion = "v2", ReleaseTag = "v2", DownloadUrl = url
            }, ct: timeout.Token);
            success.Should().BeTrue();
            changed.Should().Be(0);
            await service.WhenIdleAsync(timeout.Token);
            var updated = (await settings.DiscoverModulesAsync()).Single();
            updated.Version.Should().Be("2.0");
            updated.Settings.Should().Contain(setting => setting.Key == "added");
            (await settings.LoadSettingValueAsync(updated, updated.Settings[0])).Value.Should().Be("new version");
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(updated.SourcePath)!, "bridge-calls"))
                .Should().Equal("list_categories", "list_items", "describe_item");
        }

        /// <summary>
        /// Verifies a visible settings pass finishes later getters even after its first page observer detaches.
        /// </summary>
        [Fact]
        public async Task Complete_settings_pass_survives_a_closed_page_and_prepares_unvisited_keys()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            module.Settings.Add(new ModuleSetting
            {
                Key = "second", Type = "string", GetExec = Command("[Console]::Out.Write('second value')")
            });
            using var page = new CancellationTokenSource();
            var complete = settings.CompleteModuleValuesAsync(module);
            var observer = settings.LoadSettingValueAsync(module, module.Settings[0], page.Token);
            page.Cancel();
            try { await observer; }
            catch (OperationCanceledException)
            {
                // Closing detaches this observer while the complete value pass remains owned by the service.
            }
            await complete.WaitAsync(TimeSpan.FromSeconds(15));
            module.Settings[1].GetExec = "a-command-that-must-not-run";
            (await settings.LoadSettingValueAsync(module, module.Settings[1])).Value.Should().Be("second value");
        }

        /// <summary>
        /// Verifies a real restart refreshes values only, whereas content replacement reloads the bridge and schema.
        /// </summary>
        [Fact]
        public async Task Restart_refreshes_only_values_and_content_update_refreshes_the_full_module()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            await using var service = new DataPreparationService(queue, graph.Installer, graph.Catalog, settings, graph.Updates);
            service.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.WhenIdleAsync(timeout.Token);
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            var calls = File.ReadAllLines(Path.Combine(directory, "bridge-calls"));
            await File.WriteAllTextAsync(Path.Combine(directory, "value"), "restarted");

            // The regular lifecycle pipeline, rather than a view callback, owns values-only invalidation.
            await graph.Runner.ExecuteRunAsync(module, new InlineProgress<string>(_ => { }), timeout.Token);
            await service.WhenIdleAsync(timeout.Token);
            (await settings.LoadSettingValueAsync(module, module.Settings[0])).Value.Should().Be("restarted");
            File.ReadAllLines(Path.Combine(directory, "bridge-calls")).Should().Equal(calls);

            using (await queue.SuspendAsync(module.SourcePath))
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "value"), "updated");
                module.Settings.Add(new ModuleSetting { Key = "added", Type = "string", Default = "new key" });
                await graph.Installer.SaveConfigAsync(module, raiseModulesChanged: false);
            }
            await service.WhenIdleAsync(timeout.Token);
            (await settings.LoadSettingValueAsync(module, module.Settings[0])).Value.Should().Be("updated");
            (await settings.DiscoverModulesAsync()).Single().Settings.Should().Contain(setting => setting.Key == "added");
            File.ReadAllLines(Path.Combine(directory, "bridge-calls")).Should().HaveCount(calls.Length * 2);

            await graph.Installer.UninstallAsync(module);
            await service.WhenIdleAsync(timeout.Token);
            (await graph.Catalog.LoadCatalogAsync()).Categories.Should().BeEmpty();
            (await settings.DiscoverModulesAsync()).Should().BeEmpty();
            settings.GetPreparedValue(module, module.Settings[0]).Should().BeNull();
        }


        // Incremental values

        /// <summary>
        /// Verifies a successful early getter becomes cached before a later blocked getter completes.
        /// </summary>
        [Fact]
        public async Task Prepared_values_are_available_before_the_full_pass_finishes()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            var hold = Path.Combine(directory, "hold-value");
            await File.WriteAllTextAsync(hold, "wait");
            module.Settings.Add(new ModuleSetting
            {
                Key = "slow", Type = "string", Default = "fallback", GetExec = Command(
                    "while (Test-Path 'hold-value') { Start-Sleep -Milliseconds 20 }; 'late runtime'")
            });
            var pass = settings.CompleteModuleValuesAsync(module);
            try
            {
                await TestWait.UntilAsync(() => settings.GetPreparedValue(module, module.Settings[0]) != null,
                    because: "the first row must not wait for the remaining getter");
                settings.GetPreparedValue(module, module.Settings[0])!.Value.Should().Be("initial");
                pass.IsCompleted.Should().BeFalse();
                settings.CompleteModuleValuesAsync(module).Should().BeSameAs(pass);
            }
            finally { File.Delete(hold); }
            await pass.WaitAsync(TimeSpan.FromSeconds(15));
            settings.GetPreparedValue(module, module.Settings[1])!.Value.Should().Be("late runtime");
        }

        /// <summary>
        /// Verifies reindexing retains successful values even for a module whose settings were never opened.
        /// </summary>
        [Fact]
        public async Task Reindex_keeps_last_known_values_until_fresh_getters_finish()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            await settings.CompleteModuleValuesAsync(module).WaitAsync(TimeSpan.FromSeconds(15));
            var previous = settings.GetPreparedValue(module, module.Settings[0]);
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            var hold = Path.Combine(directory, "hold-value");
            module.Settings[0].GetExec = Command(
                "while (Test-Path 'hold-value') { Start-Sleep -Milliseconds 20 }; Get-Content 'value'");
            await File.WriteAllTextAsync(hold, "wait");
            using (await queue.SuspendAsync(module.SourcePath, valuesOnly: true))
                await File.WriteAllTextAsync(Path.Combine(directory, "value"), "fresh runtime");
            var pass = settings.CompleteModuleValuesAsync(module);
            try
            {
                settings.GetPreparedValue(module, module.Settings[0]).Should().BeSameAs(previous);
                pass.IsCompleted.Should().BeFalse();
            }
            finally { File.Delete(hold); }
            await pass.WaitAsync(TimeSpan.FromSeconds(15));
            settings.GetPreparedValue(module, module.Settings[0])!.Value.Should().Be("fresh runtime");
        }


        /// <summary>
        /// Verifies a failed getter retains its old value, allows other updates and remains retryable on the next open.
        /// </summary>
        [Fact]
        public async Task Failed_refresh_keeps_cached_values_and_retries_without_restarting_other_getters()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            await settings.CompleteModuleValuesAsync(module).WaitAsync(TimeSpan.FromSeconds(15));
            var previous = settings.GetPreparedValue(module, module.Settings[0]);
            module.Settings[0].GetExec = Command("exit 1");
            module.Settings.Add(new ModuleSetting
            {
                Key = "other", Type = "string", GetExec = Command("Add-Content 'other-calls' 'get'; 'other ready'")
            });
            using (await queue.SuspendAsync(module.SourcePath, valuesOnly: true)) { }
            await settings.CompleteModuleValuesAsync(module).WaitAsync(TimeSpan.FromSeconds(15));

            settings.GetPreparedValue(module, module.Settings[0]).Should().BeSameAs(previous);
            settings.GetPreparedValue(module, module.Settings[1])!.Value.Should().Be("other ready");
            module.Settings[0].GetExec = Command("'recovered'");
            await settings.CompleteModuleValuesAsync(module).WaitAsync(TimeSpan.FromSeconds(15));

            settings.GetPreparedValue(module, module.Settings[0])!.Value.Should().Be("recovered");
            File.ReadAllLines(Path.Combine(Path.GetDirectoryName(module.SourcePath)!, "other-calls"))
                .Should().ContainSingle();
        }

        /// <summary>
        /// Verifies a cancelled getter cannot overwrite a successful value from the next preparation generation.
        /// </summary>
        [Fact]
        public async Task Obsolete_getter_cannot_replace_a_newer_last_known_value()
        {
            await using var queue = new DataPreparationQueue();
            using var graph = new TestServiceGraph(preparation: queue);
            var settings = new SettingsService(graph.Engines, graph.Installer, graph.Runner, queue);
            var module = await SeedAsync(graph);
            var directory = Path.GetDirectoryName(module.SourcePath)!;
            var hold = Path.Combine(directory, "hold-value");
            await File.WriteAllTextAsync(hold, "wait");
            module.Settings[0].GetExec = Command(
                "Set-Content 'getter-started' 'yes'; while (Test-Path 'hold-value') { Start-Sleep -Milliseconds 20 }; 'obsolete'");
            var obsolete = settings.CompleteModuleValuesAsync(module);
            await TestWait.UntilAsync(() => File.Exists(Path.Combine(directory, "getter-started")),
                because: "the old getter must be running before its generation is invalidated");
            using (await queue.SuspendAsync(module.SourcePath, valuesOnly: true))
                module.Settings[0].GetExec = Command("'newest'");
            File.Delete(hold);
            await settings.CompleteModuleValuesAsync(module).WaitAsync(TimeSpan.FromSeconds(15));
            await obsolete.WaitAsync(TimeSpan.FromSeconds(15));

            settings.GetPreparedValue(module, module.Settings[0])!.Value.Should().Be("newest");
        }


        // Fixtures

        /// <summary>
        /// Seeds a raw executable module whose bridge and getter count actual process executions on disk.
        /// </summary>
        private static async Task<ModuleConfig> SeedAsync(TestServiceGraph graph)
        {
            var directory = Path.Combine(graph.Root.ModulesDir, "prepared");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "value"), "initial");
            var bridge = """
                $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
                Add-Content -LiteralPath 'bridge-calls' -Value $request.operation
                while (Test-Path -LiteralPath 'hold') { Start-Sleep -Milliseconds 20 }
                switch ($request.operation) {
                    'list_categories' { '{"success":true,"categories":[{"id":"test","title":"Test"}]}' }
                    'list_items' { '{"success":true,"items":[{"resourceKey":"item","title":"Item"}]}' }
                    'describe_item' { '{"success":true,"itemDetail":{"resourceKey":"item","title":"Details"}}' }
                }
                """;
            var module = ModuleConfigBuilder.Create("prepared", configure: config =>
            {
                config.SourcePath = Path.Combine(directory, "ASLM_Module.json");
                config.Commands.Run.Clear();
                config.Status.Installed = config.Status.FirstRunCompleted = true;
                config.Settings = [new ModuleSetting
                {
                    Key = "value", Type = "string", Default = "fallback",
                    GetExec = Command("Add-Content -LiteralPath 'getter-calls' -Value 'get'; Get-Content -LiteralPath 'value'")
                }];
                config.DownloadsBridge = new ModuleDownloadsBridge
                {
                    EntryPoint = Command(bridge), Operations = ["list_categories", "list_items", "describe_item"]
                };
            });
            await graph.Installer.SaveConfigAsync(module);
            return module;
        }

        /// <summary>
        /// Encodes a PowerShell command without relying on the module's implementation language or shell quoting.
        /// </summary>
        private static string Command(string script) => "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'; " + script));
    }
}
