// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services.Modules
{
    /// <summary>
    /// Verifies downloads bridge process transport and cancellation behavior, and how the download catalog loads
    /// categories from several bridge modules.
    /// </summary>
    public sealed class ModuleDownloadBridgeTests
    {
        // Bridge process

        /// <summary>
        /// Verifies bridge JSON starts with the object byte instead of a UTF-8 byte-order mark.
        /// </summary>
        [Fact]
        public void Process_start_info_uses_bomless_utf8_for_standard_input()
        {
            var service = CreateService();
            var module = CreateRawBridgeModule("cmd.exe /d /c more");
            var bridge = module.DownloadsBridge!;

            var startInfo = service.CreateProcessStartInfo(module, bridge, Path.GetTempPath());

            startInfo.StandardInputEncoding.Should().BeOfType<UTF8Encoding>();
            startInfo.StandardInputEncoding!.GetPreamble().Should().BeEmpty();
        }

        /// <summary>
        /// Verifies canceling a running bridge returns promptly after terminating its process tree.
        /// </summary>
        [Fact]
        public async Task InvokeAsync_cancels_running_process_promptly()
        {
            var service = CreateService();
            var module = CreateRawBridgeModule("cmd.exe /d /c ping -n 30 127.0.0.1 > nul");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var stopwatch = Stopwatch.StartNew();

            var act = () => service.InvokeAsync(
                module,
                new ModuleDownloadBridgeRequest { Operation = "list_categories" },
                cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }


        // Catalog loading

        /// <summary>
        /// Verifies that the download catalog publishes the category of a fast bridge while another bridge is still
        /// waiting. With separate categories, a search in the ready category does not interrupt the discovery still
        /// in progress; with a shared category the sources of both bridges merge without changing the snapshot
        /// published earlier; when the load is canceled, the waiting bridge publishes nothing afterwards.
        /// </summary>
        /// <param name="sharedCategory">True when both bridges declare the same category group key.</param>
        /// <param name="cancel">True to cancel the load after the first category was published.</param>
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task Catalog_publishes_ready_categories_while_another_bridge_is_waiting(bool sharedCategory, bool cancel)
        {
            using var layout = new AslmFileSystemLayout(resetData: false);
            var prefix = $"catalog-stream-{Guid.NewGuid():N}";
            var directories = new List<string>();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<DownloadCatalogSnapshot>? loading = null;
            try
            {
                // Two bridge modules: "fast" answers at once, "slow" blocks until its release file appears.
                var fastKey = sharedCategory ? prefix : prefix + "-fast";
                var slowKey = sharedCategory ? prefix : prefix + "-slow";
                WriteProvider("fast", fastKey, waitForRelease: false);
                var slowDirectory = WriteProvider("slow", slowKey, waitForRelease: true);

                // Catalog under test; the callback records every published category and signals the first one.
                var firstCategory = new TaskCompletionSource<DownloadCatalogCategory>(TaskCreationOptions.RunContinuationsAsynchronously);
                var updates = new List<DownloadCatalogCategory>();
                var catalog = new DownloadCatalog(
                    new ModuleInstaller(null!, null!, null!), CreateService(),
                    new DownloadStateStore(NullLogger<DownloadStateStore>.Instance),
                    NullLogger<DownloadCatalog>.Instance);

                loading = catalog.LoadCatalogAsync(ct: cancellation.Token, categoryLoaded: category =>
                {
                    if (category.GroupKey.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        updates.Add(category);
                        firstCategory.TrySetResult(category);
                    }

                    return Task.CompletedTask;
                });

                // The fast bridge's category arrives while the whole load is still pending on the slow bridge.
                var first = await firstCategory.Task.WaitAsync(cancellation.Token);
                first.GroupKey.Should().Be(fastKey);
                first.Items.Should().ContainSingle().Which.Sources.Should().ContainSingle();
                loading.IsCompleted.Should().BeFalse("the second provider has not been released yet");

                // Cancellation scenario: the load aborts and nothing is published after the first category.
                if (cancel)
                {
                    cancellation.Cancel();
                    var act = async () => await loading;
                    await act.Should().ThrowAsync<OperationCanceledException>();
                    updates.Should().ContainSingle("cancelled providers must not publish a late result");
                    return;
                }

                // Separate categories: a search restricted to the ready category returns its updated item while the
                // first load keeps waiting for the slow bridge.
                if (!sharedCategory)
                {
                    var query = await catalog.LoadCatalogAsync(
                        categoryQueries: new Dictionary<string, DownloadCatalogQuery>
                        {
                            [fastKey] = new("updated query", [])
                        },
                        categoryGroupKey: fastKey,
                        ct: cancellation.Token);
                    query.Categories.Single().Items.Single().Title.Should().Be("updated query");
                    loading.IsCompleted.Should().BeFalse("searching a ready category must not interrupt discovery");
                }

                // Release the slow bridge and check the final merge: one publication per bridge, and a shared
                // category combines the sources of both.
                await File.WriteAllTextAsync(Path.Combine(slowDirectory, "release"), string.Empty, cancellation.Token);
                var snapshot = await loading;
                updates.Should().HaveCount(2);
                var categories = snapshot.Categories.Where(category => category.GroupKey.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                categories.Should().HaveCount(sharedCategory ? 1 : 2);
                if (sharedCategory)
                {
                    categories.Single().Items.Single().Sources.Should().HaveCount(2);
                    updates.Last().Items.Single().Sources.Should().HaveCount(2);
                    first.Items.Single().Sources.Should().ContainSingle("published snapshots must not mutate later");
                }
            }
            finally
            {
                // Stop any bridge still running, wait for the load to end, then unregister and delete the providers.
                cancellation.Cancel();
                if (loading != null)
                {
                    try { await loading; }
                    catch (OperationCanceledException) { }
                }

                foreach (var directory in directories)
                {
                    new ModuleRegistry().Remove(Path.GetFileName(directory));

                    // A killed bridge keeps its module directory locked for a short while after
                    // termination (the transient console host outlives the awaited process).
                    await TestWait.DeleteDirectoryAsync(directory);
                }
            }

            // Registers a bridge module whose script answers with one item in the given category group; with
            // waitForRelease it first waits for a file named "release" in its working directory.
            string WriteProvider(string name, string groupKey, bool waitForRelease)
            {
                // Module folder inside the modules directory, tracked for cleanup.
                var directory = Path.Combine(layout.ModulesDir, prefix + "-" + name);
                Directory.CreateDirectory(directory);
                directories.Add(directory);

                // PowerShell script: read the request, optionally wait for the release file, and answer with the
                // prepared item; a search text in the request becomes the item title.
                var response = JsonSerializer.Serialize(new ModuleDownloadBridgeResponse
                {
                    Items = [new ModuleDownloadItemPayload { ResourceKey = groupKey + ":item", Title = name }]
                });
                var script = "$request = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
                    (waitForRelease ? "while (!(Test-Path -LiteralPath 'release')) { Start-Sleep -Milliseconds 25 }; " : "") +
                    $"$response = '{response.Replace("'", "''")}' | ConvertFrom-Json; " +
                    "if ($request.queryText) { $response.items[0].title = $request.queryText }; " +
                    "[Console]::Out.Write(($response | ConvertTo-Json -Depth 20 -Compress))";

                // Bridge manifest that runs the script, written to disk and added to the module registry.
                var module = new ModuleConfig
                {
                    Id = prefix + "-" + name,
                    Name = name,
                    DownloadsBridge = new ModuleDownloadsBridge
                    {
                        EntryPoint = "powershell.exe -NoProfile -NonInteractive -EncodedCommand " +
                            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                        Operations = ["list_items"],
                        Categories = [new ModuleDownloadBridgeCategory { Id = "items", GroupKey = groupKey, Title = name }]
                    }
                };
                File.WriteAllText(Path.Combine(directory, "ASLM_Module.json"), JsonSerializer.Serialize(module));
                new ModuleRegistry().Add(module.Id);
                return directory;
            }
        }


        // Test helpers

        /// <summary>
        /// Creates a bridge service whose engine dependencies are unused by raw commands.
        /// </summary>
        private static ModuleDownloadBridge CreateService()
        {
            return new ModuleDownloadBridge(
                null!,
                null!,
                null!,
                NullLogger<ModuleDownloadBridge>.Instance);
        }

        /// <summary>
        /// Creates a raw-command module rooted in the existing temporary directory.
        /// </summary>
        private static ModuleConfig CreateRawBridgeModule(string entryPoint)
        {
            return new ModuleConfig
            {
                Id = "bridge-test",
                Name = "Bridge test",
                SourcePath = Path.Combine(Path.GetTempPath(), "ASLM_Module.json"),
                DownloadsBridge = new ModuleDownloadsBridge
                {
                    EntryPoint = entryPoint,
                    Operations = ["list_categories"]
                }
            };
        }
    }
}
