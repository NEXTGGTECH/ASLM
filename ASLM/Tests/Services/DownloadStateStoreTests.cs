// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services;

public sealed class DownloadStateStoreTests
{
    [Fact]
    public async Task MarkInstalledAsync_and_MarkUninstalledAsync_persist_state()
    {
        _ = new AslmFileSystemLayout();
        var store = new DownloadStateStore(TestLoggerFactory.Create<DownloadStateStore>());

        await store.MarkInstalledAsync("engine:ollama", "1.2.3", "aslm-chat");

        store.GetResourceState("engine:ollama")!.Installed.Should().BeTrue();
        store.GetResourceState("engine:ollama")!.InstalledVersion.Should().Be("1.2.3");

        await store.MarkUninstalledAsync("engine:ollama");

        store.GetResourceState("engine:ollama")!.Installed.Should().BeFalse();
    }

    [Fact]
    public void GetResourceState_returns_null_for_unknown_key()
    {
        _ = new AslmFileSystemLayout();
        var store = new DownloadStateStore(TestLoggerFactory.Create<DownloadStateStore>());

        store.GetResourceState("missing").Should().BeNull();
    }
}
