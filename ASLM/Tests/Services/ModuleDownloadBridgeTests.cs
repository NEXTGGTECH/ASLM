// Copyright NEXTGGTECH. Apache License 2.0.

using System.Diagnostics;
using System.Text;
using ASLM.Models;
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
