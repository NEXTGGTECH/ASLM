// Copyright NEXTGGTECH. Apache License 2.0.

using ASLM.Tests.TestSupport;

namespace ASLM.Tests.Services.Internal
{
    /// <summary>
    /// Verifies the persisted installation state of download catalog resources kept by
    /// <see cref="DownloadStateStore"/>.
    /// </summary>
    public sealed class DownloadStateStoreTests
    {
        /// <summary>
        /// Verifies that marking a resource installed records its installed flag and version, and that marking it
        /// uninstalled clears the installed flag.
        /// </summary>
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

        /// <summary>
        /// Verifies that looking up a resource key that was never recorded returns no state.
        /// </summary>
        [Fact]
        public void GetResourceState_returns_null_for_unknown_key()
        {
            _ = new AslmFileSystemLayout();
            var store = new DownloadStateStore(TestLoggerFactory.Create<DownloadStateStore>());

            store.GetResourceState("missing").Should().BeNull();
        }
    }
}
