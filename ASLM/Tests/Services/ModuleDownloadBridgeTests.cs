// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ASLM.Models;
using ASLM.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ASLM.Tests.Services;

/// <summary>
/// Verifies downloads bridge process transport and cancellation behavior.
/// </summary>
public sealed class ModuleDownloadBridgeTests
{
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
            var fastKey = sharedCategory ? prefix : prefix + "-fast";
            var slowKey = sharedCategory ? prefix : prefix + "-slow";
            WriteProvider("fast", fastKey, waitForRelease: false);
            var slowDirectory = WriteProvider("slow", slowKey, waitForRelease: true);
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

            var first = await firstCategory.Task.WaitAsync(cancellation.Token);
            first.GroupKey.Should().Be(fastKey);
            first.Items.Should().ContainSingle().Which.Sources.Should().ContainSingle();
            loading.IsCompleted.Should().BeFalse("the second provider has not been released yet");

            if (cancel)
            {
                cancellation.Cancel();
                var act = async () => await loading;
                await act.Should().ThrowAsync<OperationCanceledException>();
                updates.Should().ContainSingle("cancelled providers must not publish a late result");
                return;
            }

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
            cancellation.Cancel();
            if (loading != null)
            {
                try { await loading; }
                catch (OperationCanceledException) { }
            }
            foreach (var directory in directories)
                Directory.Delete(directory, recursive: true);
        }

        string WriteProvider(string name, string groupKey, bool waitForRelease)
        {
            var directory = Path.Combine(layout.ModulesDir, prefix + "-" + name);
            Directory.CreateDirectory(directory);
            directories.Add(directory);
            var response = JsonSerializer.Serialize(new ModuleDownloadBridgeResponse
            {
                Items = [new ModuleDownloadItemPayload { ResourceKey = groupKey + ":item", Title = name }]
            });
            var script = "$request = [Console]::In.ReadToEnd() | ConvertFrom-Json; " +
                (waitForRelease ? "while (!(Test-Path -LiteralPath 'release')) { Start-Sleep -Milliseconds 25 }; " : "") +
                $"$response = '{response.Replace("'", "''")}' | ConvertFrom-Json; " +
                "if ($request.queryText) { $response.items[0].title = $request.queryText }; " +
                "[Console]::Out.Write(($response | ConvertTo-Json -Depth 20 -Compress))";
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
            return directory;
        }
    }

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
